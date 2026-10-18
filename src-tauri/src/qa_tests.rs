//! Cross-cutting quality tests, written from the testing strategy: security boundary, hostile and extreme input,
//! migrations from every version, atomic failure, duplicate protection, concurrency and randomised accounting invariants.

use crate::auth::{Permission, Role};
use crate::billing::{self, InvoiceInput, LineInput};
use crate::db;
use crate::ledger;
use crate::receipts::{self, ReceiptInput};
use rusqlite::{params, Connection};

fn fresh() -> Connection {
    let mut c = Connection::open_in_memory().unwrap();
    c.pragma_update(None, "foreign_keys", "ON").unwrap();
    db::migrate(&mut c).unwrap();
    c.execute("INSERT INTO users (username, password_hash, role) VALUES ('o','x','owner')", []).unwrap();
    c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
    c.execute("INSERT INTO projects (client_id, name) VALUES (1,'Villa')", []).unwrap();
    c.execute("UPDATE company_profile SET name='Studio', gst_scheme='regular'", []).unwrap();
    c
}

fn tmp(tag: &str) -> std::path::PathBuf {
    std::env::temp_dir().join(format!("ao_qa_{tag}_{}_{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()))
}

fn draft(c: &mut Connection, date: &str, rate: &str) -> i64 {
    billing::save_draft(c, &InvoiceInput {
        id: None, project_id: 1, kind: "tax_invoice".into(), issue_date: date.into(), due_date: "".into(), gst_mode: "intra".into(), gst_rate: "".into(), notes: "".into(),
        lines: vec![LineInput { description: "Fee".into(), qty: "1".into(), rate: rate.into() }],
    })
    .unwrap()
}

fn receipt(invoice_id: i64, date: &str, amount: &str) -> ReceiptInput {
    ReceiptInput { invoice_id, received_date: date.into(), amount: amount.into(), tds: "".into(), tds_section: "".into(), mode: "bank".into(), reference: "".into(), notes: "".into(), bank_account_id: None }
}

fn count(c: &Connection, table: &str) -> i64 {
    c.query_row(&format!("SELECT COUNT(*) FROM {table}"), [], |r| r.get(0)).unwrap()
}

// ---------------------------------------------------------------- security boundary

/// Every command that Tauri exposes must check the session. The only exceptions are the sign-in flow itself.
#[test]
fn every_tauri_command_checks_the_session_and_permission() {
    let files: [(&str, &str); 19] = [
        ("workflow.rs", include_str!("workflow.rs")),
        ("studio.rs", include_str!("studio.rs")),
        ("company_file.rs", include_str!("company_file.rs")),
        ("commands.rs", include_str!("commands.rs")), ("admin.rs", include_str!("admin.rs")), ("bank.rs", include_str!("bank.rs")), ("billing.rs", include_str!("billing.rs")),
        ("receipts.rs", include_str!("receipts.rs")), ("documents.rs", include_str!("documents.rs")), ("accounting.rs", include_str!("accounting.rs")),
        ("registers.rs", include_str!("registers.rs")), ("clients.rs", include_str!("clients.rs")), ("entries.rs", include_str!("entries.rs")), ("ledger.rs", include_str!("ledger.rs")),
        ("practice.rs", include_str!("practice.rs")),
        ("timebook.rs", include_str!("timebook.rs")),
        ("transmittals.rs", include_str!("transmittals.rs")),
        ("boards.rs", include_str!("boards.rs")),
        ("stages.rs", include_str!("stages.rs")),
    ];
    let open = ["company_active", "company_validate", "company_recent_list", "company_recent_remove", "company_open", "company_create", "company_close", "company_legacy_info", "company_convert_legacy", "auth_status", "auth_setup", "auth_login", "auth_unlock", "auth_lock", "auth_logout", "auth_recover"];
    let mut checked = 0;
    for (name, text) in files {
        for segment in text.split("#[tauri::command]").skip(1) {
            let body = segment.split("\n}\n").next().unwrap();
            let fn_name = body.split("fn ").nth(1).unwrap().split('(').next().unwrap().trim();
            checked += 1;
            if open.contains(&fn_name) {
                continue;
            }
            assert!(body.contains("require(&state"), "{name}: command `{fn_name}` does not call require(); any local user could invoke it");
        }
    }
    assert!(checked > 100, "the scan found only {checked} commands; the test is no longer looking at the real code");
}

/// The role/permission table, written out by hand so a careless change to `allows` is caught.
#[test]
fn role_permission_matrix_is_exactly_as_designed() {
    use Permission::*;
    use Role::*;
    let all = [View, Edit, Issue, ManageSettings, ManageUsers, ViewAudit, Backup, Finance, Post, ClosePeriod];
    let granted = |r: Role| -> Vec<Permission> {
        match r {
            Owner => all.to_vec(),
            Principal => vec![View, Edit, Issue, Finance],
            Accounts => vec![View, Edit, Issue, Finance, Post],
            DocumentEditor => vec![View, Edit],
            Auditor => vec![View, ViewAudit, Finance],
        }
    };
    for role in [Owner, Principal, Accounts, DocumentEditor, Auditor] {
        for p in all {
            assert_eq!(role.allows(p), granted(role).contains(&p), "{role:?} / {p:?}");
        }
    }
    // The read-only role can never change anything.
    for p in [Edit, Issue, ManageSettings, ManageUsers, Backup, Post, ClosePeriod] {
        assert!(!Auditor.allows(p));
    }
}

#[test]
fn sensitive_code_never_logs_or_returns_secrets() {
    // Password material must only ever be handled in auth/commands/admin and never be formatted into messages.
    for (name, text) in [("commands.rs", include_str!("commands.rs")), ("admin.rs", include_str!("admin.rs")), ("auth.rs", include_str!("auth.rs"))] {
        for (i, line) in text.lines().enumerate() {
            let l = line.trim();
            if l.starts_with("//") { continue; }
            assert!(!(l.contains("format!") && (l.contains("{password") || l.contains("{pw}") || l.contains("{recovery}") || l.contains("{code}"))), "{name}:{} formats a secret into a string", i + 1);
            // Event names are literals ("password_changed"); only a secret *variable* in the arguments is a leak.
            let without_literals: String = l.split('"').enumerate().filter(|(n, _)| n % 2 == 0).map(|(_, p)| p).collect();
            assert!(!(without_literals.contains("audit(") && (without_literals.contains("password") || without_literals.contains("recovery") || without_literals.contains("hash"))), "{name}:{} writes a secret into the audit log", i + 1);
        }
    }
    assert!(!include_str!("commands.rs").contains("admin/admin"), "no default credentials");
}

// ---------------------------------------------------------------- hostile and extreme input

#[test]
fn absurd_amounts_are_rejected_instead_of_overflowing() {
    let mut c = fresh();
    let dir = tmp("big");
    let cases = ["9223372036854775807", "99999999999999999999999", "92233720368547758.07", "999999999999999", "1e9", "-1", "0x10", " ", "١٢٣"];
    for rate in cases {
        let r = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            billing::calculate(&InvoiceInput {
                id: None, project_id: 1, kind: "tax_invoice".into(), issue_date: "2026-10-01".into(), due_date: "".into(), gst_mode: "intra".into(), gst_rate: "".into(), notes: "".into(),
                lines: vec![LineInput { description: "x".into(), qty: "999999999".into(), rate: rate.into() }],
            }, "regular")
        }));
        let r = r.unwrap_or_else(|_| panic!("calculate panicked on rate {rate:?}"));
        assert!(r.is_err(), "rate {rate:?} should have been refused, got {:?}", r.ok().map(|t| t.total_paise));
    }
    // many large-but-legal lines must also stay within range
    let lines = (0..50).map(|i| LineInput { description: format!("l{i}"), qty: "1".into(), rate: "99999999999.99".into() }).collect();
    let big = billing::calculate(&InvoiceInput { id: None, project_id: 1, kind: "tax_invoice".into(), issue_date: "2026-10-01".into(), due_date: "".into(), gst_mode: "intra".into(), gst_rate: "".into(), notes: "".into(), lines }, "regular");
    assert!(big.is_ok() || big.is_err()); // either is fine; it must not panic
    // receipts, expenses and journals with absurd amounts
    let id = draft(&mut c, "2026-10-01", "1000");
    billing::issue(&mut c, id).unwrap();
    for amt in ["9223372036854775807", "99999999999999999999", "92233720368547758.07"] {
        assert!(std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| receipts::record(&mut c, 1, &receipt(id, "2026-10-20", amt)))).expect("receipt panicked").is_err());
    }
    assert_eq!(count(&c, "receipts"), 0);
    let (cash, fees) = (ledger::acct(&c, "1000").unwrap(), ledger::acct(&c, "4000").unwrap());
    let r = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| ledger::post(&c, None, "2026-10-01", "x", "manual", None, vec![ledger::dr(cash, i64::MAX), ledger::cr(fees, i64::MAX)])));
    assert!(r.is_ok(), "posting i64::MAX panicked");
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn unreasonable_dates_are_rejected_everywhere() {
    for d in ["0000-01-01", "0001-01-01", "1899-12-31", "9999-12-31", "2026-13-01", "2026-02-29", "2026-00-10", "2026-1-1", "20261012", "2026/10/12", "", "2026-10-12T00:00", "２０２６-10-12"] {
        assert!(billing::validate_date(d).is_err(), "{d:?} should be rejected");
    }
    for d in ["2026-10-12", "2024-02-29", "2000-01-01", "2100-12-31"] {
        assert!(billing::validate_date(d).is_ok(), "{d:?} should be accepted");
    }
}

#[test]
fn hostile_text_is_stored_and_rendered_safely() {
    let mut c = fresh();
    let nasty = ["'; DROP TABLE clients; --", "<script>alert(1)</script>", "{{company.name}}", "=cmd|' /C calc'!A0", "\u{202e}evil", "a\u{0}b", "😀 मराठी", &"W".repeat(5000)];
    for n in nasty {
        c.execute("INSERT INTO clients (name) VALUES (?1)", params![format!("{n}{}", c.last_insert_rowid())]).unwrap();
    }
    assert_eq!(count(&c, "clients"), 1 + nasty.len() as i64);
    // PDF rendering of awkward text must neither panic nor produce an empty file
    let html = crate::billing::invoice_html(&crate::billing::Snapshot {
        number: "INV/1".into(), kind: "tax_invoice".into(), issue_date: "2026-10-01".into(), due_date: "".into(), gst_mode: "none".into(), scheme: "none".into(), notes: nasty.join("\n"),
        company_name: nasty[1].into(), company_address: nasty[7].into(), company_pan: "".into(), company_gstin: "".into(), company_coa: "".into(), bank_details: "".into(),
        company_email: "".into(), company_phone: "".into(), architect_name: "".into(),
        client_name: nasty[6].into(), client_address: nasty[0].into(), client_gstin: "".into(), client_email: "".into(), client_phone: "".into(),
        client_pan: "".into(), client_tan: "".into(), client_state: "".into(), project_name: nasty[7].into(),
        totals: billing::calculate(&InvoiceInput { id: None, project_id: 1, kind: "tax_invoice".into(), issue_date: "2026-10-01".into(), due_date: "".into(), gst_mode: "none".into(), gst_rate: "".into(), notes: "".into(), lines: vec![LineInput { description: nasty[6].into(), qty: "1".into(), rate: "10".into() }] }, "none").unwrap(),
        against: String::new(), upi_uri: String::new(), logo_square_b64: String::new(), logo_wide_b64: String::new(), accent_hex: String::new(),
    });
    assert!(!html.contains("<script>"), "user text must be escaped");
    let r = std::panic::catch_unwind(|| crate::pdf::render_html(&html, &crate::pdf::bundled_fonts())).expect("PDF layout panicked on awkward text");
    assert!(r.unwrap().bytes.starts_with(b"%PDF"));
}

// ---------------------------------------------------------------- migrations

#[test]
fn every_schema_version_upgrades_cleanly_to_the_latest() {
    for from in 0..=db::latest_version() {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        db::apply_first(&mut c, from).unwrap();
        db::migrate(&mut c).unwrap_or_else(|e| panic!("upgrade from version {from} failed: {e}"));
        let v: i64 = c.pragma_query_value(None, "user_version", |r| r.get(0)).unwrap();
        assert_eq!(v as usize, db::latest_version());
        let bad: i64 = c.query_row("SELECT COUNT(*) FROM pragma_foreign_key_check", [], |r| r.get(0)).unwrap();
        assert_eq!(bad, 0, "foreign key violations after upgrading from {from}");
        let ok: String = c.query_row("PRAGMA integrity_check", [], |r| r.get(0)).unwrap();
        assert_eq!(ok, "ok");
        // and running startup again must not apply anything twice
        db::migrate(&mut c).unwrap();
    }
}

// ---------------------------------------------------------------- atomic failure and duplicate protection

#[test]
fn a_failed_posting_leaves_no_partial_records_numbers_or_files() {
    let mut c = fresh();
    let dir = tmp("atomic");
    let paid = draft(&mut c, "2026-04-10", "1000");
    billing::issue(&mut c, paid).unwrap();
    let next = draft(&mut c, "2026-05-01", "2000");
    ledger::set_period_status(&c, 1, "2026-27", true).unwrap();

    let (series_before, files_before) = (c.query_row("SELECT SUM(last_seq) FROM number_series", [], |r| r.get::<_, i64>(0)).unwrap(), std::fs::read_dir(dir.join("issued-pdfs")).map(|d| d.count()).unwrap_or(0));
    // issuing into a closed year must fail as a whole
    assert!(billing::issue(&mut c, next).is_err());
    assert_eq!(billing::get(&c, next).unwrap().status, "draft", "the draft must stay a draft");
    assert_eq!(c.query_row("SELECT SUM(last_seq) FROM number_series", [], |r| r.get::<_, i64>(0)).unwrap(), series_before, "the number must not be consumed");
    assert_eq!(std::fs::read_dir(dir.join("issued-pdfs")).map(|d| d.count()).unwrap_or(0), files_before, "no orphan PDF");
    // a receipt into a closed year fails the same way
    let (rc, jc) = (count(&c, "receipts"), count(&c, "journals"));
    assert!(receipts::record(&mut c, 1, &receipt(paid, "2026-06-01", "100")).is_err());
    assert_eq!((count(&c, "receipts"), count(&c, "journals")), (rc, jc));
    // reopen: the same operations now succeed and numbers continue without a gap
    ledger::set_period_status(&c, 1, "2026-27", false).unwrap();
    assert_eq!(billing::issue(&mut c, next).unwrap(), "INV/2026-27/0002");
    receipts::record(&mut c, 1, &receipt(paid, "2026-06-01", "100")).unwrap();
    assert_eq!(ledger::unbalanced_journals(&c).unwrap(), 0);
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn business_events_post_to_the_ledger_at_most_once() {
    let mut c = fresh();
    let dir = tmp("once");
    let id = draft(&mut c, "2026-10-01", "1000");
    billing::issue(&mut c, id).unwrap();
    receipts::record(&mut c, 1, &receipt(id, "2026-10-02", "500")).unwrap();
    let before = count(&c, "journals");
    assert!(ledger::post_invoice(&c, Some(1), id).unwrap().is_none());
    assert_eq!(ledger::backfill(&c, 1).unwrap(), (0, 0, 0));
    assert_eq!(ledger::backfill(&c, 1).unwrap(), (0, 0, 0));
    assert_eq!(count(&c, "journals"), before);
    // the database refuses a second journal for the same source even if code tried
    let dup = c.execute("INSERT INTO journals (number, date, source_type, source_id) SELECT 'JV/X', date, source_type, source_id FROM journals WHERE source_type='invoice'", []);
    assert!(dup.is_err(), "unique source index must hold");
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn two_connections_cannot_issue_the_same_invoice_twice() {
    let dir = tmp("race");
    std::fs::create_dir_all(&dir).unwrap();
    let path = dir.join("race.sqlite");
    {
        let mut c = db::open(&path).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('o','x','owner')", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1,'Villa')", []).unwrap();
        c.execute("UPDATE company_profile SET gst_scheme='none'", []).unwrap();
        draft(&mut c, "2026-10-01", "1000");
    }
    let results: Vec<Result<String, String>> = (0..4)
        .map(|_| {
            let p = path.clone();
            std::thread::spawn(move || {
                let mut c = db::open(&p).unwrap();
                billing::issue(&mut c, 1)
            })
        })
        .collect::<Vec<_>>()
        .into_iter()
        .map(|h| h.join().expect("a worker panicked"))
        .collect();
    let winners = results.iter().filter(|r| r.is_ok()).count();
    assert_eq!(winners, 1, "exactly one issue may win: {results:?}");
    let c = db::open(&path).unwrap();
    assert_eq!(count(&c, "invoices"), 1);
    let numbered: i64 = c.query_row("SELECT COUNT(*) FROM invoices WHERE number IS NOT NULL", [], |r| r.get(0)).unwrap();
    assert_eq!(numbered, 1);
    assert_eq!(count(&c, "journals"), 1, "one invoice, one journal");
    assert_eq!(count(&c, "stored_files"), 1, "losers must not leave documents behind");
    let _ = std::fs::remove_dir_all(dir);
}

// ---------------------------------------------------------------- randomised accounting invariants

struct Lcg(u64);
impl Lcg {
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        self.0 >> 33
    }
    fn below(&mut self, n: u64) -> u64 {
        self.next() % n
    }
}

#[test]
fn random_operation_sequences_keep_the_ledger_balanced_and_reconciled() {
    for seed in 1..=8u64 {
        let mut rng = Lcg(seed * 7919);
        let mut c = fresh();
        let dir = tmp("rand");
        let mut open: Vec<i64> = Vec::new();
        for step in 0..60 {
            let day = 1 + rng.below(27);
            let date = format!("2026-{:02}-{:02}", 4 + rng.below(8), day);
            match rng.below(6) {
                0 | 1 => {
                    let rate = format!("{}", 1000 + rng.below(90_000));
                    let id = draft(&mut c, &date, &rate);
                    if billing::issue(&mut c, id).is_ok() { open.push(id); }
                }
                2 if !open.is_empty() => {
                    let id = open[rng.below(open.len() as u64) as usize];
                    let amt = format!("{}.{:02}", 1 + rng.below(500), rng.below(100));
                    let _ = receipts::record(&mut c, 1, &ReceiptInput { reference: format!("R{seed}-{step}"), ..receipt(id, &date, &amt) });
                }
                3 if !open.is_empty() => {
                    let id = open[rng.below(open.len() as u64) as usize];
                    let _ = billing::issue_credit_note(&mut c, 1, id, &date, "random", vec![LineInput { description: "c".into(), qty: "1".into(), rate: format!("{}", 1 + rng.below(300)) }]);
                }
                4 => {
                    let rid: Option<i64> = c.query_row("SELECT id FROM effective_receipts ORDER BY RANDOM() LIMIT 1", [], |r| r.get(0)).ok();
                    if let Some(r) = rid { let _ = receipts::reverse(&mut c, 1, r, "random"); }
                }
                _ => {
                    let id = open.get(rng.below(open.len().max(1) as u64) as usize).copied();
                    if let Some(id) = id { let _ = billing::cancel(&mut c, 1, id, "random"); }
                }
            }
            assert_eq!(ledger::unbalanced_journals(&c).unwrap(), 0, "seed {seed} step {step}: an unbalanced journal exists");
        }
        let tb = ledger::trial_balance(&c, "2027-03-31").unwrap();
        assert!(tb.balanced, "seed {seed}: trial balance does not balance");
        assert!(ledger::balance_sheet(&c, "2027-03-31").unwrap().balanced, "seed {seed}: balance sheet does not balance");
        let ar = ledger::ar_check(&c, "2027-03-31").unwrap();
        assert!(ar.agrees, "seed {seed}: receivable {} vs invoices {}", ar.ledger_display, ar.invoices_display);
        // no invoice may ever be over-collected
        let over: i64 = c.query_row("SELECT COUNT(*) FROM invoice_balances WHERE total_paise - received_paise - tds_paise - credited_paise - written_off_paise < 0", [], |r| r.get(0)).unwrap();
        assert_eq!(over, 0, "seed {seed}: an invoice has a negative balance");
        let _ = std::fs::remove_dir_all(dir);
    }
}
