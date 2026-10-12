//! Server-side paging for the registers that grow without bound: bank transactions, journals, invoices,
//! documents and the audit log. Searching, filtering, sorting and totals all happen in SQL, over an allow-list
//! of sortable columns, so the screen only ever holds one page of rows.

use crate::auth::Permission;
use crate::bank::TxnRow;
use crate::billing::{fmt_inr, parse_decimal, InvoiceRow};
use crate::commands::{require, AppState, INTERNAL};
use crate::documents::DocRow;
use crate::ledger::JournalRow;
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use tauri::State;

#[derive(Deserialize, Clone, Default)]
pub struct PageQuery {
    #[serde(default)]
    pub search: String,
    /// One of the register's allow-listed sort keys; anything else falls back to the default.
    #[serde(default)]
    pub sort: String,
    #[serde(default)]
    pub dir: String,
    #[serde(default)]
    pub page: i64,
    #[serde(default)]
    pub page_size: i64,
    #[serde(default)]
    pub filters: BTreeMap<String, String>,
}

impl PageQuery {
    fn limit(&self) -> i64 {
        if self.page_size <= 0 { 50 } else { self.page_size.clamp(10, 500) }
    }
    fn offset(&self) -> i64 {
        self.page.max(0) * self.limit()
    }
    fn desc(&self, default_desc: bool) -> bool {
        match self.dir.as_str() {
            "asc" => false,
            "desc" => true,
            _ => default_desc,
        }
    }
    fn needle(&self) -> String {
        self.search.trim().to_lowercase()
    }
    /// A search that is a plain amount (e.g. "11,800.00") also matches amount columns exactly.
    fn amount(&self) -> i64 {
        parse_decimal(self.search.trim(), 2).unwrap_or(-1)
    }
    fn filter(&self, key: &str) -> String {
        self.filters.get(key).cloned().unwrap_or_default()
    }
}

#[derive(Serialize)]
pub struct Page<T> {
    pub rows: Vec<T>,
    /// Number of rows matching the search and filters (not just this page).
    pub total: i64,
    /// Totals over all matching rows, by name.
    pub sums: BTreeMap<String, i64>,
}

fn pick<'a>(allowed: &[(&'a str, &'a str)], key: &str, default: &'a str) -> &'a str {
    allowed.iter().find(|(k, _)| *k == key).map(|(_, v)| *v).unwrap_or(default)
}

// ---------- bank transactions ----------

const TXN_FROM: &str = "FROM bank_transactions t JOIN bank_batches b ON b.id = t.batch_id JOIN bank_accounts a ON a.id = b.account_id
    WHERE (?1 = '' OR t.status = ?1) AND (?2 = '0' OR b.account_id = CAST(?2 AS INTEGER))
      AND (?3 = '' OR instr(lower(t.narration || ' ' || t.reference || ' ' || t.note || ' ' || a.name), ?3) > 0 OR t.debit_paise = ?4 OR t.credit_paise = ?4)";

pub fn page_txns(conn: &Connection, q: &PageQuery) -> Result<Page<TxnRow>, String> {
    let (status, account, needle, amount) = (q.filter("status"), if q.filter("account").is_empty() { "0".to_string() } else { q.filter("account") }, q.needle(), q.amount());
    let key = pick(
        &[("date", "t.txn_date"), ("account", "lower(a.name)"), ("narration", "lower(t.narration)"), ("ref", "lower(t.reference)"), ("debit", "t.debit_paise"), ("credit", "t.credit_paise"), ("status", "t.status")],
        &q.sort, "t.txn_date",
    );
    let dir = if q.desc(true) { "DESC" } else { "ASC" };
    let sql = format!(
        "SELECT t.id, t.txn_date, t.narration, t.reference, t.debit_paise, t.credit_paise, t.status, t.note,
                COALESCE((SELECT SUM(r.amount_paise) FROM bank_txn_receipts l JOIN effective_receipts r ON r.id = l.receipt_id WHERE l.txn_id = t.id), 0),
                b.account_id, a.name {TXN_FROM} ORDER BY {key} {dir}, t.id DESC LIMIT ?5 OFFSET ?6"
    );
    let mut stmt = conn.prepare(&sql).map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![status, account, needle, amount, q.limit(), q.offset()], |r| {
            let (debit, credit, alloc): (i64, i64, i64) = (r.get(4)?, r.get(5)?, r.get(8)?);
            Ok(TxnRow {
                id: r.get(0)?, txn_date: r.get(1)?, narration: r.get(2)?, reference: r.get(3)?,
                debit_display: if debit > 0 { fmt_inr(debit) } else { String::new() },
                credit_display: if credit > 0 { fmt_inr(credit) } else { String::new() },
                credit_paise: credit, debit_paise: debit, allocated_display: fmt_inr(alloc), unallocated_paise: credit - alloc,
                status: r.get(6)?, note: r.get(7)?, account_id: r.get(9)?, account_name: r.get(10)?,
            })
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    let (total, debits, credits): (i64, i64, i64) = conn
        .query_row(&format!("SELECT COUNT(*), COALESCE(SUM(t.debit_paise),0), COALESCE(SUM(t.credit_paise),0) {TXN_FROM}"), params![status, account, needle, amount], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
        .map_err(|e| e.to_string())?;
    Ok(Page { rows, total, sums: BTreeMap::from([("debit".into(), debits), ("credit".into(), credits)]) })
}

// ---------- journals ----------

const JOURNAL_AMOUNT: &str = "(SELECT COALESCE(SUM(debit_paise),0) FROM journal_lines WHERE journal_id = j.id)";

const JOURNAL_WHERE: &str = "WHERE j.date BETWEEN ?1 AND ?2 AND (?3 = '' OR j.source_type = ?3)
      AND (?4 = '' OR instr(lower(j.number || ' ' || j.narration || ' ' || j.source_type), ?4) > 0)";

pub fn page_journals(conn: &Connection, q: &PageQuery) -> Result<Page<JournalRow>, String> {
    let from = if q.filter("from").is_empty() { "0000-01-01".to_string() } else { q.filter("from") };
    let to = if q.filter("to").is_empty() { "9999-12-31".to_string() } else { q.filter("to") };
    let (source, needle) = (q.filter("source"), q.needle());
    let amount_key = JOURNAL_AMOUNT.to_string();
    let reversed = "EXISTS (SELECT 1 FROM journals r WHERE r.source_type = 'reversal' AND r.source_id = j.id)";
    let key = match q.sort.as_str() {
        "number" => "j.number".to_string(),
        "narration" => "lower(j.narration)".to_string(),
        "source" => "j.source_type".to_string(),
        "amount" => amount_key.clone(),
        "status" => reversed.to_string(),
        _ => "j.date".to_string(),
    };
    let dir = if q.desc(true) { "DESC" } else { "ASC" };
    let sql = format!(
        "SELECT j.id, j.number, j.date, j.narration, j.source_type, {JOURNAL_AMOUNT}, {reversed} FROM journals j {JOURNAL_WHERE}
         ORDER BY {key} {dir}, j.id DESC LIMIT ?5 OFFSET ?6"
    );
    let mut stmt = conn.prepare(&sql).map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![from, to, source, needle, q.limit(), q.offset()], |r| {
            let total: i64 = r.get(5)?;
            Ok(JournalRow { id: r.get(0)?, number: r.get(1)?, date: r.get(2)?, narration: r.get(3)?, source_type: r.get(4)?, total_display: fmt_inr(total), total_paise: total, reversed: r.get::<_, i64>(6)? != 0 })
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    let (total, value): (i64, i64) = conn
        .query_row(&format!("SELECT COUNT(*), COALESCE(SUM({JOURNAL_AMOUNT}),0) FROM journals j {JOURNAL_WHERE}"), params![from, to, source, needle], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|e| e.to_string())?;
    Ok(Page { rows, total, sums: BTreeMap::from([("amount".into(), value)]) })
}

// ---------- invoices ----------

#[derive(Serialize)]
pub struct InvoiceRegisterRow {
    #[serde(flatten)]
    pub row: InvoiceRow,
    /// Draft, issued, partial, overdue, paid, completed (a converted pro forma) or cancelled.
    pub effective_status: String,
}

const EFFECTIVE: &str = "CASE
    WHEN i.status <> 'issued' THEN i.status
    WHEN i.kind = 'proforma' THEN CASE WHEN EXISTS (SELECT 1 FROM invoices t WHERE t.source_proforma_id = i.id AND t.status <> 'cancelled') THEN 'completed' ELSE 'issued' END
    WHEN i.total_paise - b.received_paise - b.tds_paise - b.credited_paise <= 0 THEN 'paid'
    WHEN i.due_date <> '' AND i.due_date < ?1 THEN 'overdue'
    WHEN b.received_paise + b.tds_paise + b.credited_paise > 0 THEN 'partial'
    ELSE 'issued' END";

const INVOICE_FROM: &str = "FROM invoices i JOIN invoice_balances b ON b.invoice_id = i.id JOIN projects p ON p.id = i.project_id JOIN clients c ON c.id = p.client_id";

pub fn page_invoices(conn: &Connection, q: &PageQuery, today: &str) -> Result<Page<InvoiceRegisterRow>, String> {
    let (kind, status, needle, amount) = (q.filter("kind"), q.filter("status"), q.needle(), q.amount());
    let where_sql = format!(
        "WHERE (?2 = '' OR i.kind = ?2) AND (?3 = '' OR ({EFFECTIVE}) = ?3)
           AND (?4 = '' OR instr(lower(COALESCE(i.number,'draft') || ' ' || c.name || ' ' || p.name), ?4) > 0 OR i.total_paise = ?5)"
    );
    let balance = "(i.total_paise - b.received_paise - b.tds_paise - b.credited_paise)";
    let key = match q.sort.as_str() {
        "number" => "COALESCE(i.number, '~draft')".to_string(),
        "type" => "i.kind".to_string(),
        "party" => "lower(c.name)".to_string(),
        "due" => "COALESCE(NULLIF(i.due_date,''), '9999')".to_string(),
        "total" => "i.total_paise".to_string(),
        "received" => "b.received_paise".to_string(),
        "tds" => "b.tds_paise".to_string(),
        "credited" => "b.credited_paise".to_string(),
        "balance" => format!("CASE WHEN i.status = 'issued' AND i.kind = 'tax_invoice' THEN {balance} ELSE -1 END"),
        "status" => EFFECTIVE.to_string(),
        _ => "i.issue_date".to_string(),
    };
    let dir = if q.desc(true) { "DESC" } else { "ASC" };
    let sql = format!(
        "SELECT i.id, i.number, i.kind, i.status, c.name, p.name, i.issue_date, i.total_paise, b.received_paise, b.tds_paise, b.credited_paise,
                (SELECT COALESCE(t.number, 'Draft') FROM invoices t WHERE t.source_proforma_id = i.id AND t.status <> 'cancelled'),
                (SELECT s.number FROM invoices s WHERE s.id = i.source_proforma_id), i.due_date, {EFFECTIVE}
         {INVOICE_FROM} {where_sql} ORDER BY {key} {dir}, i.id DESC LIMIT ?6 OFFSET ?7"
    );
    let mut stmt = conn.prepare(&sql).map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![today, kind, status, needle, amount, q.limit(), q.offset()], |r| {
            let total: i64 = r.get(7)?;
            let (paid, tds, credited): (i64, i64, i64) = (r.get(8)?, r.get(9)?, r.get(10)?);
            let bal = total - paid - tds - credited;
            Ok(InvoiceRegisterRow {
                row: InvoiceRow {
                    id: r.get(0)?, number: r.get(1)?, kind: r.get(2)?, status: r.get(3)?, client_name: r.get(4)?, project_name: r.get(5)?, issue_date: r.get(6)?,
                    total_display: fmt_inr(total), total_paise: total, paid_display: fmt_inr(paid), tds_display: fmt_inr(tds), balance_paise: bal, balance_display: fmt_inr(bal),
                    credited_display: fmt_inr(credited), paid_paise: paid, tds_paise: tds, credited_paise: credited, due_date: r.get(13)?,
                    converted_to: r.get(11)?, from_proforma: r.get(12)?,
                },
                effective_status: r.get(14)?,
            })
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    // Billed and outstanding cover issued tax invoices only, as the screen labels them.
    let (total, billed, outstanding): (i64, i64, i64) = conn
        .query_row(
            &format!(
                "SELECT COUNT(*),
                        COALESCE(SUM(CASE WHEN i.status = 'issued' AND i.kind = 'tax_invoice' THEN i.total_paise END), 0),
                        COALESCE(SUM(CASE WHEN i.status = 'issued' AND i.kind = 'tax_invoice' THEN {balance} END), 0)
                 {INVOICE_FROM} {where_sql}"
            ),
            params![today, kind, status, needle, amount],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .map_err(|e| e.to_string())?;
    Ok(Page { rows, total, sums: BTreeMap::from([("billed".into(), billed), ("outstanding".into(), outstanding)]) })
}

// ---------- documents ----------

pub fn page_docs(conn: &Connection, q: &PageQuery) -> Result<Page<DocRow>, String> {
    let (kind, status, needle) = (q.filter("kind"), q.filter("status"), q.needle());
    let key = pick(
        &[("number", "COALESCE(d.number, '~draft')"), ("type", "d.kind"), ("date", "d.doc_date"), ("party", "lower(COALESCE(NULLIF(d.party_name,''), c.name, ''))"), ("subject", "lower(d.subject)"), ("project", "lower(COALESCE(p.name,''))"), ("status", "d.status")],
        &q.sort, "d.doc_date",
    );
    let dir = if q.desc(true) { "DESC" } else { "ASC" };
    let from = "FROM documents d LEFT JOIN projects p ON p.id = d.project_id LEFT JOIN clients c ON c.id = p.client_id
        WHERE (?1 = '' OR d.kind = ?1) AND (?2 = '' OR d.status = ?2)
          AND (?3 = '' OR instr(lower(COALESCE(d.number,'draft') || ' ' || d.subject || ' ' || d.party_name || ' ' || d.reference || ' ' || COALESCE(c.name,'') || ' ' || COALESCE(p.name,'')), ?3) > 0)";
    let sql = format!(
        "SELECT d.id, d.kind, d.number, d.status, d.party_name, d.subject, d.reference, d.doc_date, COALESCE(p.name,''), COALESCE(c.name,''),
                EXISTS (SELECT 1 FROM document_attachments a WHERE a.document_id = d.id AND a.kind = 'signed_copy')
         {from} ORDER BY {key} {dir}, d.id DESC LIMIT ?4 OFFSET ?5"
    );
    let mut stmt = conn.prepare(&sql).map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![kind, status, needle, q.limit(), q.offset()], |r| {
            Ok(DocRow {
                id: r.get(0)?, kind: r.get(1)?, number: r.get(2)?, status: r.get(3)?, party_name: r.get(4)?, subject: r.get(5)?, reference: r.get(6)?,
                doc_date: r.get(7)?, project_name: r.get(8)?, client_name: r.get(9)?, signed: r.get::<_, i64>(10)? != 0,
            })
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    let total: i64 = conn.query_row(&format!("SELECT COUNT(*) {from}"), params![kind, status, needle], |r| r.get(0)).map_err(|e| e.to_string())?;
    Ok(Page { rows, total, sums: BTreeMap::new() })
}

// ---------- audit log ----------

#[derive(Serialize)]
pub struct AuditPageRow {
    pub id: i64,
    pub at: String,
    pub user: String,
    pub event: String,
    pub detail: String,
}

pub fn page_audit(conn: &Connection, q: &PageQuery) -> Result<Page<AuditPageRow>, String> {
    let needle = q.needle();
    let key = pick(&[("at", "a.id"), ("user", "lower(COALESCE(u.username,''))"), ("event", "a.event")], &q.sort, "a.id");
    let dir = if q.desc(true) { "DESC" } else { "ASC" };
    let from = "FROM audit_log a LEFT JOIN users u ON u.id = a.user_id
        WHERE (?1 = '' OR instr(lower(COALESCE(u.username,'') || ' ' || a.event || ' ' || a.detail), ?1) > 0)";
    let sql = format!("SELECT a.id, a.at, COALESCE(u.username, ''), a.event, a.detail {from} ORDER BY {key} {dir}, a.id DESC LIMIT ?2 OFFSET ?3");
    let mut stmt = conn.prepare(&sql).map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![needle, q.limit(), q.offset()], |r| Ok(AuditPageRow { id: r.get(0)?, at: r.get(1)?, user: r.get(2)?, event: r.get(3)?, detail: r.get(4)? }))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    let total: i64 = conn.query_row(&format!("SELECT COUNT(*) {from}"), params![needle], |r| r.get(0)).map_err(|e| e.to_string())?;
    Ok(Page { rows, total, sums: BTreeMap::new() })
}

// ---------- commands ----------

#[tauri::command]
pub fn bank_txn_page(state: State<AppState>, query: PageQuery) -> Result<Page<TxnRow>, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    page_txns(&conn, &query)
}

#[tauri::command]
pub fn journal_page(state: State<AppState>, query: PageQuery) -> Result<Page<JournalRow>, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    page_journals(&conn, &query)
}

#[tauri::command]
pub fn invoice_page(state: State<AppState>, query: PageQuery) -> Result<Page<InvoiceRegisterRow>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let today: String = conn.query_row("SELECT date('now')", [], |r| r.get(0)).map_err(|e| e.to_string())?;
    page_invoices(&conn, &query, &today)
}

#[tauri::command]
pub fn doc_page(state: State<AppState>, query: PageQuery) -> Result<Page<DocRow>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    page_docs(&conn, &query)
}

#[tauri::command]
pub fn audit_page(state: State<AppState>, query: PageQuery) -> Result<Page<AuditPageRow>, String> {
    require(&state, Permission::ViewAudit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    page_audit(&conn, &query)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::db;

    fn setup() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('o','x','owner')", []).unwrap();
        c.execute("INSERT INTO bank_accounts (name, is_primary) VALUES ('HDFC', 1), ('SBI', 0)", []).unwrap();
        c.execute("INSERT INTO bank_batches (filename, file_sha256, row_count, account_id) VALUES ('a','1',0,1), ('b','2',0,2)", []).unwrap();
        // 250 rows: alternating accounts, credits every 3rd row is a debit
        for i in 0..250i64 {
            let (debit, credit) = if i % 3 == 0 { (100 + i, 0) } else { (0, 1000 + i) };
            c.execute(
                "INSERT INTO bank_transactions (batch_id, row_no, txn_date, narration, reference, debit_paise, credit_paise, raw_row, dedupe_key, status)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, 'r', ?8, ?9)",
                params![1 + (i % 2), i, format!("2026-10-{:02}", 1 + (i % 28)), format!("Narration {i}"), format!("UTR{i:04}"), debit, credit, format!("k{i}"), if i % 5 == 0 { "matched" } else { "unmatched" }],
            )
            .unwrap();
        }
        c
    }

    fn q(page: i64, size: i64) -> PageQuery {
        PageQuery { page, page_size: size, ..Default::default() }
    }

    #[test]
    fn paging_returns_disjoint_pages_with_exact_totals_independent_of_the_page() {
        let c = setup();
        let all = page_txns(&c, &q(0, 500)).unwrap();
        assert_eq!((all.rows.len(), all.total), (250, 250));
        let mut seen = std::collections::BTreeSet::new();
        for p in 0..5 {
            let page = page_txns(&c, &q(p, 50)).unwrap();
            assert_eq!(page.rows.len(), 50);
            assert_eq!(page.total, 250, "the total does not depend on the page");
            assert_eq!(page.sums["credit"], all.sums["credit"], "nor do the sums");
            for r in page.rows { assert!(seen.insert(r.id), "a row appeared on two pages"); }
        }
        assert_eq!(seen.len(), 250);
        assert!(page_txns(&c, &q(5, 50)).unwrap().rows.is_empty());
        // oversize and nonsense page sizes are clamped, negative pages become the first page
        assert_eq!(page_txns(&c, &q(-3, 100000)).unwrap().rows.len(), 250);
        assert_eq!(page_txns(&c, &q(0, 1)).unwrap().rows.len(), 10);
    }

    #[test]
    fn sorting_uses_the_allow_list_with_a_deterministic_tie_break() {
        let c = setup();
        let mut query = q(0, 500);
        query.sort = "credit".into();
        query.dir = "desc".into();
        let rows = page_txns(&c, &query).unwrap().rows;
        assert!(rows.windows(2).all(|w| w[0].credit_paise >= w[1].credit_paise));
        // an attempt to inject SQL through the sort key falls back to the default order instead of running
        query.sort = "t.id; DROP TABLE bank_transactions; --".into();
        assert_eq!(page_txns(&c, &query).unwrap().total, 250);
        let dates: Vec<String> = page_txns(&c, &query).unwrap().rows.into_iter().map(|r| r.txn_date).collect();
        assert!(dates.windows(2).all(|w| w[0] >= w[1]));
        // equal sort values come out in a stable order across calls
        query.sort = "status".into();
        let a: Vec<i64> = page_txns(&c, &query).unwrap().rows.into_iter().map(|r| r.id).collect();
        let b: Vec<i64> = page_txns(&c, &query).unwrap().rows.into_iter().map(|r| r.id).collect();
        assert_eq!(a, b);
    }

    #[test]
    fn search_and_filters_narrow_the_total_and_the_sums_together() {
        let c = setup();
        let mut query = q(0, 500);
        query.filters.insert("status".into(), "matched".into());
        let matched = page_txns(&c, &query).unwrap();
        assert_eq!(matched.total, 50);
        query.filters.insert("account".into(), "2".into());
        let m2 = page_txns(&c, &query).unwrap();
        assert!(m2.total < matched.total && m2.rows.iter().all(|r| r.account_id == 2 && r.status == "matched"));
        assert_eq!(m2.sums["credit"], m2.rows.iter().map(|r| r.credit_paise).sum::<i64>());
        let mut s = q(0, 500);
        s.search = "utr0007".into();
        assert_eq!(page_txns(&c, &s).unwrap().total, 1);
        s.search = "%".into();
        assert_eq!(page_txns(&c, &s).unwrap().total, 0, "wildcards are plain text");
        // a search that is an exact amount matches the amount columns
        s.search = "10.07".into();
        assert_eq!(page_txns(&c, &s).unwrap().total, 1);
        s.search = "no such thing".into();
        assert_eq!(page_txns(&c, &s).unwrap().total, 0);
    }

    #[test]
    fn journals_invoices_documents_and_audit_page_the_same_way() {
        let mut c = setup();
        let (cash, fees) = (crate::ledger::acct(&c, "1000").unwrap(), crate::ledger::acct(&c, "4000").unwrap());
        for i in 0..120i64 {
            crate::ledger::post(&c, Some(1), &format!("2026-10-{:02}", 1 + (i % 28)), &format!("Sale {i}"), "manual", None, vec![crate::ledger::dr(cash, 100 + i), crate::ledger::cr(fees, 100 + i)]).unwrap();
        }
        let mut jq = q(1, 50);
        let p = page_journals(&c, &jq).unwrap();
        assert_eq!((p.rows.len(), p.total), (50, 120));
        assert_eq!(p.sums["amount"], (0..120).map(|i| 100 + i).sum::<i64>());
        jq.filters.insert("from".into(), "2026-10-10".into());
        jq.filters.insert("to".into(), "2026-10-12".into());
        let narrowed = page_journals(&c, &PageQuery { page_size: 500, filters: jq.filters.clone(), ..Default::default() }).unwrap();
        assert!(narrowed.rows.iter().all(|r| r.date >= "2026-10-10".to_string() && r.date <= "2026-10-12".to_string()));
        // invoices: effective status is computed in SQL and can be filtered and sorted on
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1,'Villa')", []).unwrap();
        c.execute("UPDATE company_profile SET gst_scheme='none'", []).unwrap();
        let dir = std::env::temp_dir().join(format!("ao_pg_{}", std::process::id()));
        for (i, due) in ["2026-09-01", "2099-01-01", ""].iter().enumerate() {
            let id = crate::billing::save_draft(&mut c, &crate::billing::InvoiceInput {
                id: None, project_id: 1, kind: "tax_invoice".into(), issue_date: "2026-08-01".into(), due_date: due.to_string(), gst_mode: "none".into(), gst_rate: "".into(), notes: "".into(),
                lines: vec![crate::billing::LineInput { description: "Fee".into(), qty: "1".into(), rate: format!("{}", 1000 * (i + 1)) }],
            }).unwrap();
            if i < 2 { crate::billing::issue(&mut c, id).unwrap(); }
        }
        let mut iq = q(0, 50);
        let all = page_invoices(&c, &iq, "2026-10-12").unwrap();
        assert_eq!(all.total, 3);
        let status_of = |n: &str| all.rows.iter().find(|r| r.row.number.as_deref() == Some(n)).map(|r| r.effective_status.clone());
        assert_eq!(status_of("INV/2026-27/0001").as_deref(), Some("overdue"));
        assert_eq!(status_of("INV/2026-27/0002").as_deref(), Some("issued"));
        assert_eq!(all.rows.iter().filter(|r| r.effective_status == "draft").count(), 1);
        assert_eq!((all.sums["billed"], all.sums["outstanding"]), (300_000, 300_000), "drafts are not billed");
        iq.filters.insert("status".into(), "overdue".into());
        assert_eq!(page_invoices(&c, &iq, "2026-10-12").unwrap().total, 1);
        iq.filters.clear();
        iq.sort = "balance".into();
        iq.dir = "asc".into();
        let sorted = page_invoices(&c, &iq, "2026-10-12").unwrap().rows;
        assert_eq!(sorted[0].effective_status, "draft", "non-invoice rows sort below real balances");
        // audit log and documents
        for i in 0..70 { crate::db::audit(&c, Some(1), "test_event", &format!("detail {i}")); }
        let ap = page_audit(&c, &q(0, 50)).unwrap();
        assert_eq!(ap.rows.len(), 50);
        assert!(ap.total >= 70);
        assert!(ap.rows[0].id > ap.rows[1].id, "newest first");
        let mut aq = q(0, 50);
        aq.search = "detail 69".into();
        assert_eq!(page_audit(&c, &aq).unwrap().total, 1);
        assert_eq!(page_docs(&c, &q(0, 50)).unwrap().total, 0);
        let _ = std::fs::remove_dir_all(dir);
    }
}
