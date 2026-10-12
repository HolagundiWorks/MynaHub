//! General ledger: one double-entry posting engine, automatic postings from billing, and the standard reports.
//! Posted journals are immutable. Corrections are reversal journals. A business event posts at most once.

use crate::billing::{allocate_number, financial_year, fmt_inr, validate_date, Snapshot};
use crate::db;
use rusqlite::{params, Connection};
use serde::Serialize;

// ---------- accounts ----------

pub fn acct(conn: &Connection, code: &str) -> Result<i64, String> {
    conn.query_row("SELECT id FROM accounts WHERE code = ?1", params![code], |r| r.get(0)).map_err(|_| format!("Ledger account {code} is missing."))
}

#[derive(Serialize)]
pub struct AccountRow {
    pub id: i64,
    pub code: String,
    pub name: String,
    pub kind: String,
    pub is_system: bool,
    pub active: bool,
    pub balance_paise: i64,
    pub balance_display: String,
}

fn natural(kind: &str, debit: i64, credit: i64) -> i64 {
    if kind == "asset" || kind == "expense" { debit - credit } else { credit - debit }
}

pub fn list_accounts(conn: &Connection, as_of: &str) -> Result<Vec<AccountRow>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT a.id, a.code, a.name, a.type, a.is_system, a.active,
                    COALESCE((SELECT SUM(l.debit_paise) FROM journal_lines l JOIN journals j ON j.id = l.journal_id WHERE l.account_id = a.id AND j.date <= ?1), 0),
                    COALESCE((SELECT SUM(l.credit_paise) FROM journal_lines l JOIN journals j ON j.id = l.journal_id WHERE l.account_id = a.id AND j.date <= ?1), 0)
             FROM accounts a ORDER BY a.code",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![as_of], |r| {
            let kind: String = r.get(3)?;
            let bal = natural(&kind, r.get(6)?, r.get(7)?);
            Ok(AccountRow { id: r.get(0)?, code: r.get(1)?, name: r.get(2)?, kind, is_system: r.get::<_, i64>(4)? != 0, active: r.get::<_, i64>(5)? != 0, balance_paise: bal, balance_display: fmt_inr(bal) })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn create_account(conn: &Connection, actor: i64, code: &str, name: &str, kind: &str) -> Result<i64, String> {
    let (code, name) = (code.trim(), name.trim());
    if code.is_empty() || name.is_empty() {
        return Err("Enter a code and a name.".into());
    }
    if !["asset", "liability", "equity", "income", "expense"].contains(&kind) {
        return Err("Invalid account type.".into());
    }
    conn.execute("INSERT INTO accounts (code, name, type) VALUES (?1,?2,?3)", params![code, name, kind]).map_err(|_| "An account with this code already exists.".to_string())?;
    let id = conn.last_insert_rowid();
    db::audit(conn, Some(actor), "account_created", &format!("{code} {name}"));
    Ok(id)
}

pub fn set_account_active(conn: &Connection, actor: i64, id: i64, active: bool) -> Result<(), String> {
    let system: i64 = conn.query_row("SELECT is_system FROM accounts WHERE id = ?1", params![id], |r| r.get(0)).map_err(|_| "Account not found.".to_string())?;
    if system != 0 && !active {
        return Err("System accounts cannot be deactivated.".into());
    }
    conn.execute("UPDATE accounts SET active = ?1 WHERE id = ?2", params![active, id]).map_err(|e| e.to_string())?;
    db::audit(conn, Some(actor), "account_active_changed", &format!("account {id}: {active}"));
    Ok(())
}

/// Ledger account for a bank account, created when the bank account is.
pub fn ensure_bank_ledger(conn: &Connection, bank_account_id: i64, name: &str) -> Result<(), String> {
    let existing: Option<i64> = conn.query_row("SELECT ledger_account_id FROM bank_accounts WHERE id = ?1", params![bank_account_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    match existing {
        Some(id) => {
            conn.execute("UPDATE accounts SET name = ?1 WHERE id = ?2", params![format!("Bank - {name}"), id]).map_err(|e| e.to_string())?;
        }
        None => {
            conn.execute("INSERT INTO accounts (code, name, type) VALUES (?1, ?2, 'asset')", params![format!("BANK-{bank_account_id}"), format!("Bank - {name}")]).map_err(|e| e.to_string())?;
            let id = conn.last_insert_rowid();
            conn.execute("UPDATE bank_accounts SET ledger_account_id = ?1 WHERE id = ?2", params![id, bank_account_id]).map_err(|e| e.to_string())?;
        }
    }
    Ok(())
}

/// Where money for a payment goes: cash, a chosen/primary bank account's ledger, or the unassigned bank account.
pub fn money_account(conn: &Connection, mode: &str, bank_account_id: Option<i64>) -> Result<i64, String> {
    if mode == "cash" {
        return acct(conn, "1000");
    }
    let bank: Option<i64> = match bank_account_id {
        Some(b) => Some(b),
        None => conn.query_row("SELECT id FROM bank_accounts WHERE is_primary = 1 AND active = 1", [], |r| r.get(0)).ok(),
    };
    if let Some(b) = bank {
        let led: Option<i64> = conn.query_row("SELECT ledger_account_id FROM bank_accounts WHERE id = ?1", params![b], |r| r.get(0)).map_err(|_| "Bank account not found.".to_string())?;
        if let Some(l) = led {
            return Ok(l);
        }
    }
    acct(conn, "1010")
}

// ---------- periods ----------

fn fy_bounds(date: &str) -> Result<(String, String, String), String> {
    let fy = financial_year(date)?;
    let start_year: i32 = fy[..4].parse().map_err(|_| "Bad financial year.".to_string())?;
    Ok((fy, format!("{start_year}-04-01"), format!("{}-03-31", start_year + 1)))
}

fn ensure_period(conn: &Connection, date: &str) -> Result<(), String> {
    let (fy, start, end) = fy_bounds(date)?;
    conn.execute("INSERT OR IGNORE INTO accounting_periods (fy, start_date, end_date) VALUES (?1,?2,?3)", params![fy, start, end]).map_err(|e| e.to_string())?;
    Ok(())
}

#[derive(Serialize)]
pub struct PeriodRow {
    pub fy: String,
    pub start_date: String,
    pub end_date: String,
    pub status: String,
    pub journals: i64,
}

pub fn list_periods(conn: &Connection) -> Result<Vec<PeriodRow>, String> {
    let mut stmt = conn
        .prepare("SELECT p.fy, p.start_date, p.end_date, p.status, (SELECT COUNT(*) FROM journals j WHERE j.date BETWEEN p.start_date AND p.end_date) FROM accounting_periods p ORDER BY p.start_date DESC")
        .map_err(|e| e.to_string())?;
    let rows = stmt.query_map([], |r| Ok(PeriodRow { fy: r.get(0)?, start_date: r.get(1)?, end_date: r.get(2)?, status: r.get(3)?, journals: r.get(4)? })).map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn set_period_status(conn: &Connection, actor: i64, fy: &str, closed: bool) -> Result<(), String> {
    let status: String = conn.query_row("SELECT status FROM accounting_periods WHERE fy = ?1", params![fy], |r| r.get(0)).map_err(|_| "Period not found.".to_string())?;
    if closed {
        if status == "closed" {
            return Err("The period is already closed.".into());
        }
        if unbalanced_journals(conn)? > 0 {
            return Err("There are unbalanced journals; the period cannot be closed.".into());
        }
        conn.execute("UPDATE accounting_periods SET status='closed', closed_at=datetime('now'), closed_by=?1 WHERE fy=?2", params![actor, fy]).map_err(|e| e.to_string())?;
    } else {
        if status == "open" {
            return Err("The period is already open.".into());
        }
        conn.execute("UPDATE accounting_periods SET status='open', closed_at=NULL, closed_by=NULL WHERE fy=?1", params![fy]).map_err(|e| e.to_string())?;
    }
    db::audit(conn, Some(actor), if closed { "period_closed" } else { "period_reopened" }, fy);
    Ok(())
}

// ---------- posting engine ----------

#[derive(Clone, Debug)]
pub struct Line {
    pub account_id: i64,
    pub debit: i64,
    pub credit: i64,
    pub memo: String,
}

pub fn dr(account_id: i64, amount: i64) -> Line {
    Line { account_id, debit: amount, credit: 0, memo: String::new() }
}

pub fn cr(account_id: i64, amount: i64) -> Line {
    Line { account_id, debit: 0, credit: amount, memo: String::new() }
}

/// Posts one balanced journal. Zero-amount lines are dropped.
pub fn post(conn: &Connection, actor: Option<i64>, date: &str, narration: &str, source_type: &str, source_id: Option<i64>, lines: Vec<Line>) -> Result<i64, String> {
    validate_date(date)?;
    let lines: Vec<Line> = lines.into_iter().filter(|l| l.debit > 0 || l.credit > 0).collect();
    if lines.len() < 2 {
        return Err("A journal needs at least two lines.".into());
    }
    if lines.iter().any(|l| l.debit > 0 && l.credit > 0) {
        return Err("A line cannot be both a debit and a credit.".into());
    }
    let (d, c): (i64, i64) = (lines.iter().map(|l| l.debit).sum(), lines.iter().map(|l| l.credit).sum());
    if d != c {
        return Err(format!("The journal does not balance: debits {} and credits {}.", fmt_inr(d), fmt_inr(c)));
    }
    for l in &lines {
        let active: Option<i64> = conn.query_row("SELECT active FROM accounts WHERE id = ?1", params![l.account_id], |r| r.get(0)).ok();
        match active {
            None => return Err("A journal line uses an account that does not exist.".into()),
            Some(0) => return Err("A journal line uses an inactive account.".into()),
            _ => {}
        }
    }
    ensure_period(conn, date)?;
    let (fy, _, _) = fy_bounds(date)?;
    let closed: i64 = conn.query_row("SELECT COUNT(*) FROM accounting_periods WHERE fy = ?1 AND status = 'closed'", params![fy], |r| r.get(0)).map_err(|e| e.to_string())?;
    if closed > 0 {
        return Err(format!("The accounting period {fy} is closed. Reopen it, or post in the current period."));
    }
    let number = allocate_number(conn, "journal", date)?;
    conn.execute("INSERT INTO journals (number, date, narration, source_type, source_id, created_by) VALUES (?1,?2,?3,?4,?5,?6)", params![number, date, narration, source_type, source_id, actor])
        .map_err(|e| format!("Could not post: {e}"))?;
    let jid = conn.last_insert_rowid();
    for l in &lines {
        conn.execute("INSERT INTO journal_lines (journal_id, account_id, debit_paise, credit_paise, memo) VALUES (?1,?2,?3,?4,?5)", params![jid, l.account_id, l.debit, l.credit, l.memo])
            .map_err(|e| e.to_string())?;
    }
    Ok(jid)
}

fn posted(conn: &Connection, source_type: &str, source_id: i64) -> Option<i64> {
    conn.query_row("SELECT id FROM journals WHERE source_type = ?1 AND source_id = ?2", params![source_type, source_id], |r| r.get(0)).ok()
}

/// Reverses a journal by posting its mirror image. Each journal can be reversed once.
pub fn reverse_journal(conn: &Connection, actor: Option<i64>, journal_id: i64, reason: &str) -> Result<i64, String> {
    let (number, source_type): (String, String) = conn.query_row("SELECT number, source_type FROM journals WHERE id = ?1", params![journal_id], |r| Ok((r.get(0)?, r.get(1)?))).map_err(|_| "Journal not found.".to_string())?;
    if posted(conn, "reversal", journal_id).is_some() {
        return Err("This journal has already been reversed.".into());
    }
    let mut stmt = conn.prepare("SELECT account_id, debit_paise, credit_paise, memo FROM journal_lines WHERE journal_id = ?1 ORDER BY id").map_err(|e| e.to_string())?;
    let lines: Vec<Line> = stmt
        .query_map(params![journal_id], |r| Ok(Line { account_id: r.get(0)?, debit: r.get::<_, i64>(2)?, credit: r.get::<_, i64>(1)?, memo: r.get(3)? }))
        .map_err(|e| e.to_string())?
        .collect::<Result<_, _>>()
        .map_err(|e| e.to_string())?;
    let today: String = conn.query_row("SELECT date('now')", [], |r| r.get(0)).map_err(|e| e.to_string())?;
    let _ = source_type;
    post(conn, actor, &today, &format!("Reversal of {number}: {}", reason.trim()), "reversal", Some(journal_id), lines)
}

/// Reverses the journal that was posted for a business event, if one exists (older data may predate the ledger).
pub fn reverse_source(conn: &Connection, actor: Option<i64>, source_type: &str, source_id: i64, reason: &str) -> Result<Option<i64>, String> {
    match posted(conn, source_type, source_id) {
        Some(j) => Ok(Some(reverse_journal(conn, actor, j, reason)?)),
        None => Ok(None),
    }
}

// ---------- automatic postings ----------

pub fn post_invoice(conn: &Connection, actor: Option<i64>, invoice_id: i64) -> Result<Option<i64>, String> {
    if posted(conn, "invoice", invoice_id).is_some() {
        return Ok(None);
    }
    let (status, kind, number, date, snap): (String, String, Option<String>, String, Option<String>) = conn
        .query_row("SELECT status, kind, number, issue_date, snapshot_json FROM invoices WHERE id = ?1", params![invoice_id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?)))
        .map_err(|_| "Invoice not found.".to_string())?;
    if kind != "tax_invoice" || status != "issued" {
        return Ok(None);
    }
    let snap: Snapshot = serde_json::from_str(&snap.ok_or("The invoice has no snapshot.")?).map_err(|e| e.to_string())?;
    let t = &snap.totals;
    let lines = vec![
        dr(acct(conn, "1200")?, t.total_paise),
        cr(acct(conn, "4000")?, t.subtotal_paise),
        cr(acct(conn, "2100")?, t.cgst_paise),
        cr(acct(conn, "2110")?, t.sgst_paise),
        cr(acct(conn, "2120")?, t.igst_paise),
        dr(acct(conn, "5950")?, t.composition_paise),
        cr(acct(conn, "2130")?, t.composition_paise),
    ];
    post(conn, actor, &date, &format!("Invoice {}", number.unwrap_or_default()), "invoice", Some(invoice_id), lines).map(Some)
}

pub fn post_credit_note(conn: &Connection, actor: Option<i64>, cn_id: i64) -> Result<Option<i64>, String> {
    if posted(conn, "credit_note", cn_id).is_some() {
        return Ok(None);
    }
    let (number, date, snap): (String, String, String) = conn
        .query_row("SELECT number, issue_date, snapshot_json FROM credit_notes WHERE id = ?1", params![cn_id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
        .map_err(|_| "Credit note not found.".to_string())?;
    let snap: Snapshot = serde_json::from_str(&snap).map_err(|e| e.to_string())?;
    let t = &snap.totals;
    let lines = vec![
        cr(acct(conn, "1200")?, t.total_paise),
        dr(acct(conn, "4000")?, t.subtotal_paise),
        dr(acct(conn, "2100")?, t.cgst_paise),
        dr(acct(conn, "2110")?, t.sgst_paise),
        dr(acct(conn, "2120")?, t.igst_paise),
        cr(acct(conn, "5950")?, t.composition_paise),
        dr(acct(conn, "2130")?, t.composition_paise),
    ];
    post(conn, actor, &date, &format!("Credit note {number}"), "credit_note", Some(cn_id), lines).map(Some)
}

pub fn post_receipt(conn: &Connection, actor: Option<i64>, receipt_id: i64) -> Result<Option<i64>, String> {
    if posted(conn, "receipt", receipt_id).is_some() {
        return Ok(None);
    }
    let (number, date, amount, tds, mode, bank): (String, String, i64, i64, String, Option<i64>) = conn
        .query_row("SELECT number, received_date, amount_paise, tds_paise, mode, bank_account_id FROM receipts WHERE id = ?1", params![receipt_id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?)))
        .map_err(|_| "Receipt not found.".to_string())?;
    let lines = vec![dr(money_account(conn, &mode, bank)?, amount), dr(acct(conn, "1210")?, tds), cr(acct(conn, "1200")?, amount + tds)];
    post(conn, actor, &date, &format!("Receipt {number}"), "receipt", Some(receipt_id), lines).map(Some)
}

/// Posts anything that was recorded before the ledger existed. Safe to run repeatedly.
pub fn backfill(conn: &Connection, actor: i64) -> Result<(i64, i64, i64), String> {
    let ids = |sql: &str| -> Result<Vec<i64>, String> {
        let mut stmt = conn.prepare(sql).map_err(|e| e.to_string())?;
        let rows = stmt.query_map([], |r| r.get(0)).map_err(|e| e.to_string())?;
        rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
    };
    let (mut inv, mut cn, mut rc) = (0, 0, 0);
    for id in ids("SELECT id FROM invoices WHERE kind = 'tax_invoice' AND status = 'issued' ORDER BY id")? {
        if post_invoice(conn, Some(actor), id)?.is_some() { inv += 1 }
    }
    for id in ids("SELECT id FROM credit_notes ORDER BY id")? {
        if post_credit_note(conn, Some(actor), id)?.is_some() { cn += 1 }
    }
    for id in ids("SELECT id FROM receipts ORDER BY id")? {
        if post_receipt(conn, Some(actor), id)?.is_some() {
            rc += 1;
            let reversed: i64 = conn.query_row("SELECT COUNT(*) FROM receipt_reversals WHERE receipt_id = ?1", params![id], |r| r.get(0)).map_err(|e| e.to_string())?;
            if reversed > 0 {
                reverse_source(conn, Some(actor), "receipt", id, "Receipt reversed before the ledger existed")?;
            }
        }
    }
    db::audit(conn, Some(actor), "ledger_backfill", &format!("{inv} invoices, {cn} credit notes, {rc} receipts"));
    Ok((inv, cn, rc))
}

// ---------- reports ----------

#[derive(Serialize)]
pub struct TbRow {
    pub code: String,
    pub name: String,
    pub kind: String,
    pub debit_paise: i64,
    pub credit_paise: i64,
    pub debit_display: String,
    pub credit_display: String,
}

#[derive(Serialize)]
pub struct TrialBalance {
    pub rows: Vec<TbRow>,
    pub total_debit: String,
    pub total_credit: String,
    pub balanced: bool,
}

pub fn trial_balance(conn: &Connection, as_of: &str) -> Result<TrialBalance, String> {
    validate_date(as_of)?;
    let mut rows = Vec::new();
    let (mut td, mut tc) = (0i64, 0i64);
    for a in list_accounts_raw(conn, as_of)? {
        let net = a.1 - a.2;
        if a.1 == 0 && a.2 == 0 {
            continue;
        }
        let (d, c) = if net >= 0 { (net, 0) } else { (0, -net) };
        td += d;
        tc += c;
        rows.push(TbRow { code: a.3, name: a.4, kind: a.5, debit_paise: d, credit_paise: c, debit_display: if d > 0 { fmt_inr(d) } else { String::new() }, credit_display: if c > 0 { fmt_inr(c) } else { String::new() } });
    }
    Ok(TrialBalance { rows, total_debit: fmt_inr(td), total_credit: fmt_inr(tc), balanced: td == tc })
}

fn list_accounts_raw(conn: &Connection, as_of: &str) -> Result<Vec<(i64, i64, i64, String, String, String)>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT a.id,
                    COALESCE((SELECT SUM(l.debit_paise) FROM journal_lines l JOIN journals j ON j.id = l.journal_id WHERE l.account_id = a.id AND j.date <= ?1), 0),
                    COALESCE((SELECT SUM(l.credit_paise) FROM journal_lines l JOIN journals j ON j.id = l.journal_id WHERE l.account_id = a.id AND j.date <= ?1), 0),
                    a.code, a.name, a.type
             FROM accounts a ORDER BY a.code",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt.query_map(params![as_of], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?))).map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

#[derive(Serialize)]
pub struct LedgerRow {
    pub date: String,
    pub number: String,
    pub narration: String,
    pub debit_display: String,
    pub credit_display: String,
    pub balance_display: String,
}

#[derive(Serialize)]
pub struct AccountLedger {
    pub code: String,
    pub name: String,
    pub opening_display: String,
    pub closing_display: String,
    pub rows: Vec<LedgerRow>,
}

pub fn account_ledger(conn: &Connection, account_id: i64, from: &str, to: &str) -> Result<AccountLedger, String> {
    validate_date(from)?;
    validate_date(to)?;
    let (code, name, kind): (String, String, String) = conn.query_row("SELECT code, name, type FROM accounts WHERE id = ?1", params![account_id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?))).map_err(|_| "Account not found.".to_string())?;
    let (od, oc): (i64, i64) = conn
        .query_row(
            "SELECT COALESCE(SUM(l.debit_paise),0), COALESCE(SUM(l.credit_paise),0) FROM journal_lines l JOIN journals j ON j.id = l.journal_id WHERE l.account_id = ?1 AND j.date < ?2",
            params![account_id, from],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .map_err(|e| e.to_string())?;
    let opening = natural(&kind, od, oc);
    let mut running = opening;
    let mut stmt = conn
        .prepare(
            "SELECT j.date, j.number, j.narration, l.debit_paise, l.credit_paise FROM journal_lines l JOIN journals j ON j.id = l.journal_id
             WHERE l.account_id = ?1 AND j.date BETWEEN ?2 AND ?3 ORDER BY j.date, j.id, l.id",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![account_id, from, to], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?, r.get::<_, String>(2)?, r.get::<_, i64>(3)?, r.get::<_, i64>(4)?)))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?
        .into_iter()
        .map(|(date, number, narration, d, c)| {
            running += natural(&kind, d, c);
            LedgerRow { date, number, narration, debit_display: if d > 0 { fmt_inr(d) } else { String::new() }, credit_display: if c > 0 { fmt_inr(c) } else { String::new() }, balance_display: fmt_inr(running) }
        })
        .collect();
    Ok(AccountLedger { code, name, opening_display: fmt_inr(opening), closing_display: fmt_inr(running), rows })
}

#[derive(Serialize)]
pub struct AmountRow {
    pub code: String,
    pub name: String,
    pub amount_paise: i64,
    pub amount_display: String,
}

#[derive(Serialize)]
pub struct ProfitAndLoss {
    pub income: Vec<AmountRow>,
    pub expenses: Vec<AmountRow>,
    pub total_income: String,
    pub total_expenses: String,
    pub profit_paise: i64,
    pub profit_display: String,
}

fn movements(conn: &Connection, kinds: &[&str], from: Option<&str>, to: &str) -> Result<Vec<(String, String, String, i64, i64)>, String> {
    let mut out = Vec::new();
    let mut stmt = conn
        .prepare(
            "SELECT a.code, a.name, a.type,
                    COALESCE((SELECT SUM(l.debit_paise) FROM journal_lines l JOIN journals j ON j.id = l.journal_id WHERE l.account_id = a.id AND j.date <= ?2 AND (?1 IS NULL OR j.date >= ?1)), 0),
                    COALESCE((SELECT SUM(l.credit_paise) FROM journal_lines l JOIN journals j ON j.id = l.journal_id WHERE l.account_id = a.id AND j.date <= ?2 AND (?1 IS NULL OR j.date >= ?1)), 0)
             FROM accounts a ORDER BY a.code",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt.query_map(params![from, to], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?, r.get::<_, String>(2)?, r.get::<_, i64>(3)?, r.get::<_, i64>(4)?))).map_err(|e| e.to_string())?;
    for r in rows {
        let r = r.map_err(|e| e.to_string())?;
        if kinds.contains(&r.2.as_str()) && (r.3 != 0 || r.4 != 0) {
            out.push(r);
        }
    }
    Ok(out)
}

pub fn profit_and_loss(conn: &Connection, from: &str, to: &str) -> Result<ProfitAndLoss, String> {
    validate_date(from)?;
    validate_date(to)?;
    let mk = |kind: &str| -> Result<(Vec<AmountRow>, i64), String> {
        let mut total = 0;
        let rows = movements(conn, &[kind], Some(from), to)?
            .into_iter()
            .map(|(code, name, k, d, c)| {
                let amt = natural(&k, d, c);
                total += amt;
                AmountRow { code, name, amount_paise: amt, amount_display: fmt_inr(amt) }
            })
            .collect();
        Ok((rows, total))
    };
    let (income, ti) = mk("income")?;
    let (expenses, te) = mk("expense")?;
    Ok(ProfitAndLoss { income, expenses, total_income: fmt_inr(ti), total_expenses: fmt_inr(te), profit_paise: ti - te, profit_display: fmt_inr(ti - te) })
}

#[derive(Serialize)]
pub struct BalanceSheet {
    pub assets: Vec<AmountRow>,
    pub liabilities: Vec<AmountRow>,
    pub equity: Vec<AmountRow>,
    pub total_assets: String,
    pub total_liabilities_equity: String,
    pub balanced: bool,
}

pub fn balance_sheet(conn: &Connection, as_of: &str) -> Result<BalanceSheet, String> {
    validate_date(as_of)?;
    let mut sections: [Vec<AmountRow>; 3] = [Vec::new(), Vec::new(), Vec::new()];
    let mut totals = [0i64; 3];
    for (code, name, kind, d, c) in movements(conn, &["asset", "liability", "equity"], None, as_of)? {
        let i = match kind.as_str() { "asset" => 0, "liability" => 1, _ => 2 };
        let amt = natural(&kind, d, c);
        totals[i] += amt;
        sections[i].push(AmountRow { code, name, amount_paise: amt, amount_display: fmt_inr(amt) });
    }
    // Income and expense accounts are not closed into retained earnings, so their running result is shown as equity.
    let pl = profit_and_loss(conn, "1990-01-01", as_of)?;
    totals[2] += pl.profit_paise;
    sections[2].push(AmountRow { code: String::new(), name: "Profit not yet transferred to retained earnings".into(), amount_paise: pl.profit_paise, amount_display: pl.profit_display });
    let [assets, liabilities, equity] = sections;
    Ok(BalanceSheet { assets, liabilities, equity, total_assets: fmt_inr(totals[0]), total_liabilities_equity: fmt_inr(totals[1] + totals[2]), balanced: totals[0] == totals[1] + totals[2] })
}

#[derive(Serialize)]
pub struct TaxSummary {
    pub output_cgst: String,
    pub output_sgst: String,
    pub output_igst: String,
    pub input_credit: String,
    pub net_gst_payable: String,
    pub composition_tax: String,
    pub tds_receivable: String,
    pub tds_payable: String,
}

pub fn tax_summary(conn: &Connection, from: &str, to: &str) -> Result<TaxSummary, String> {
    validate_date(from)?;
    validate_date(to)?;
    let net = |code: &str, kind: &str| -> Result<i64, String> {
        let id = acct(conn, code)?;
        let (d, c): (i64, i64) = conn
            .query_row(
                "SELECT COALESCE(SUM(l.debit_paise),0), COALESCE(SUM(l.credit_paise),0) FROM journal_lines l JOIN journals j ON j.id = l.journal_id WHERE l.account_id = ?1 AND j.date BETWEEN ?2 AND ?3",
                params![id, from, to],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .map_err(|e| e.to_string())?;
        Ok(natural(kind, d, c))
    };
    let (cg, sg, ig, input) = (net("2100", "liability")?, net("2110", "liability")?, net("2120", "liability")?, net("1220", "asset")?);
    Ok(TaxSummary {
        output_cgst: fmt_inr(cg), output_sgst: fmt_inr(sg), output_igst: fmt_inr(ig), input_credit: fmt_inr(input), net_gst_payable: fmt_inr(cg + sg + ig - input),
        composition_tax: fmt_inr(net("2130", "liability")?), tds_receivable: fmt_inr(net("1210", "asset")?), tds_payable: fmt_inr(net("2200", "liability")?),
    })
}

#[derive(Serialize)]
pub struct JournalRow {
    pub id: i64,
    pub number: String,
    pub date: String,
    pub narration: String,
    pub source_type: String,
    pub total_display: String,
    pub total_paise: i64,
    pub reversed: bool,
}

pub fn journal_list(conn: &Connection, from: &str, to: &str, limit: i64) -> Result<Vec<JournalRow>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT j.id, j.number, j.date, j.narration, j.source_type, (SELECT SUM(debit_paise) FROM journal_lines WHERE journal_id = j.id),
                    EXISTS (SELECT 1 FROM journals r WHERE r.source_type = 'reversal' AND r.source_id = j.id)
             FROM journals j WHERE j.date BETWEEN ?1 AND ?2 ORDER BY j.date DESC, j.id DESC LIMIT ?3",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![from, to, limit.clamp(1, 1000)], |r| Ok(JournalRow { id: r.get(0)?, number: r.get(1)?, date: r.get(2)?, narration: r.get(3)?, source_type: r.get(4)?, total_display: fmt_inr(r.get(5)?), total_paise: r.get(5)?, reversed: r.get::<_, i64>(6)? != 0 }))
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

#[derive(Serialize)]
pub struct JournalLineRow {
    pub account: String,
    pub memo: String,
    pub debit_display: String,
    pub credit_display: String,
}

pub fn journal_lines(conn: &Connection, journal_id: i64) -> Result<Vec<JournalLineRow>, String> {
    let mut stmt = conn
        .prepare("SELECT a.code || ' ' || a.name, l.memo, l.debit_paise, l.credit_paise FROM journal_lines l JOIN accounts a ON a.id = l.account_id WHERE l.journal_id = ?1 ORDER BY l.id")
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![journal_id], |r| {
            let (d, c): (i64, i64) = (r.get(2)?, r.get(3)?);
            Ok(JournalLineRow { account: r.get(0)?, memo: r.get(1)?, debit_display: if d > 0 { fmt_inr(d) } else { String::new() }, credit_display: if c > 0 { fmt_inr(c) } else { String::new() } })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn unbalanced_journals(conn: &Connection) -> Result<i64, String> {
    conn.query_row(
        "SELECT COUNT(*) FROM (SELECT journal_id FROM journal_lines GROUP BY journal_id HAVING SUM(debit_paise) <> SUM(credit_paise))",
        [],
        |r| r.get(0),
    )
    .map_err(|e| e.to_string())
}

#[derive(Serialize)]
pub struct ArCheck {
    pub ledger_display: String,
    pub invoices_display: String,
    pub agrees: bool,
}

/// The receivable account should equal what the open invoices say is owed.
pub fn ar_check(conn: &Connection, as_of: &str) -> Result<ArCheck, String> {
    let id = acct(conn, "1200")?;
    let (d, c): (i64, i64) = conn
        .query_row(
            "SELECT COALESCE(SUM(l.debit_paise),0), COALESCE(SUM(l.credit_paise),0) FROM journal_lines l JOIN journals j ON j.id = l.journal_id WHERE l.account_id = ?1 AND j.date <= ?2",
            params![id, as_of],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .map_err(|e| e.to_string())?;
    let owed: i64 = conn
        .query_row(
            "SELECT COALESCE(SUM(b.total_paise - b.received_paise - b.tds_paise - b.credited_paise), 0) FROM invoice_balances b JOIN invoices i ON i.id = b.invoice_id WHERE i.kind = 'tax_invoice' AND i.status = 'issued'",
            [],
            |r| r.get(0),
        )
        .map_err(|e| e.to_string())?;
    Ok(ArCheck { ledger_display: fmt_inr(d - c), invoices_display: fmt_inr(owed), agrees: d - c == owed })
}

#[derive(Serialize)]
pub struct TaxEstimate {
    pub profit_before_tax: String,
    pub tax_display: String,
    pub cess_display: String,
    pub total_display: String,
}

/// A rough estimate only: tax at a flat rate on the ledger profit, plus 4% cess. Not a tax computation.
pub fn income_tax_estimate(conn: &Connection, from: &str, to: &str, rate_percent: &str) -> Result<TaxEstimate, String> {
    let bp = crate::billing::parse_decimal(rate_percent, 2)?;
    if bp > 10_000 {
        return Err("The rate cannot exceed 100%.".into());
    }
    let pl = profit_and_loss(conn, from, to)?;
    let base = pl.profit_paise.max(0);
    let tax = (base * bp + 5_000) / 10_000;
    let cess = (tax * 4 + 50) / 100;
    Ok(TaxEstimate { profit_before_tax: pl.profit_display, tax_display: fmt_inr(tax), cess_display: fmt_inr(cess), total_display: fmt_inr(tax + cess) })
}

#[derive(serde::Deserialize)]
pub struct Slab {
    /// Upper limit of this slab in rupees; empty for the top slab.
    pub upto: String,
    pub rate_percent: String,
}

/// Progressive tax on ledger profit using the slabs the caller supplies, plus cess. No rebates, surcharge or
/// deductions are applied: this is arithmetic on the entered slabs, not a tax computation.
pub fn income_tax_by_slabs(conn: &Connection, from: &str, to: &str, slabs: &[Slab], cess_percent: &str) -> Result<TaxEstimate, String> {
    if slabs.is_empty() {
        return Err("Enter at least one slab.".into());
    }
    let pl = profit_and_loss(conn, from, to)?;
    let profit = pl.profit_paise.max(0);
    let cess_bp = crate::billing::parse_decimal(cess_percent, 2)?;
    let (mut lower, mut tax) = (0i64, 0i64);
    for (i, s) in slabs.iter().enumerate() {
        let rate = crate::billing::parse_decimal(&s.rate_percent, 2).map_err(|e| format!("Slab {}: rate. {e}", i + 1))?;
        if rate > 10_000 {
            return Err(format!("Slab {}: the rate cannot exceed 100%.", i + 1));
        }
        let upper = if s.upto.trim().is_empty() {
            if i + 1 != slabs.len() {
                return Err("Only the last slab can have no upper limit.".into());
            }
            i64::MAX
        } else {
            let u = crate::billing::parse_decimal(&s.upto, 2).map_err(|e| format!("Slab {}: limit. {e}", i + 1))?;
            if u <= lower {
                return Err(format!("Slab {}: the limit must be higher than the previous one.", i + 1));
            }
            u
        };
        if profit > lower {
            let part = profit.min(upper) - lower;
            tax += (part * rate + 5_000) / 10_000;
        }
        lower = upper;
        if upper == i64::MAX {
            break;
        }
    }
    let cess = (tax * cess_bp + 5_000) / 10_000;
    Ok(TaxEstimate { profit_before_tax: pl.profit_display, tax_display: fmt_inr(tax), cess_display: fmt_inr(cess), total_display: fmt_inr(tax + cess) })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::billing::{issue, save_draft, InvoiceInput, LineInput};
    use crate::receipts::{record, ReceiptInput};

    fn setup() -> (Connection, std::path::PathBuf) {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('o','x','owner')", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1,'Villa')", []).unwrap();
        c.execute("UPDATE company_profile SET name='Studio', gst_scheme='regular'", []).unwrap();
        let dir = std::env::temp_dir().join(format!("ao_gl_{}_{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()));
        (c, dir)
    }

    fn invoice(c: &mut Connection, dir: &std::path::Path, rate: &str, mode: &str) -> i64 {
        let id = save_draft(c, &InvoiceInput {
            id: None, project_id: 1, kind: "tax_invoice".into(), issue_date: "2026-10-01".into(), due_date: "".into(), gst_mode: mode.into(), gst_rate: "".into(), notes: "".into(),
            lines: vec![LineInput { description: "Fee".into(), qty: "1".into(), rate: rate.into() }],
        }).unwrap();
        issue(c, dir, id).unwrap();
        id
    }

    fn rc(invoice_id: i64, amount: &str, tds: &str, mode: &str) -> ReceiptInput {
        ReceiptInput { invoice_id, received_date: "2026-10-20".into(), amount: amount.into(), tds: tds.into(), tds_section: if tds.is_empty() { "".into() } else { "194J".into() }, mode: mode.into(), reference: "".into(), notes: "".into(), bank_account_id: None }
    }

    fn balance_of(c: &Connection, code: &str) -> i64 {
        let id = acct(c, code).unwrap();
        let (d, k): (i64, i64) = c.query_row("SELECT COALESCE(SUM(debit_paise),0), COALESCE(SUM(credit_paise),0) FROM journal_lines WHERE account_id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?))).unwrap();
        d - k
    }

    #[test]
    fn engine_rejects_unbalanced_single_line_and_closed_periods() {
        let (c, _d) = setup();
        let (cash, fees) = (acct(&c, "1000").unwrap(), acct(&c, "4000").unwrap());
        assert!(post(&c, None, "2026-10-01", "x", "manual", None, vec![dr(cash, 100), cr(fees, 90)]).unwrap_err().contains("does not balance"));
        assert!(post(&c, None, "2026-10-01", "x", "manual", None, vec![dr(cash, 100)]).unwrap_err().contains("at least two"));
        assert!(post(&c, None, "2026-13-01", "x", "manual", None, vec![dr(cash, 1), cr(fees, 1)]).is_err());
        let j = post(&c, Some(1), "2026-10-01", "Sale", "manual", None, vec![dr(cash, 100), cr(fees, 100)]).unwrap();
        assert!(c.execute("UPDATE journals SET narration = 'x' WHERE id = ?1", params![j]).is_err());
        assert!(c.execute("DELETE FROM journal_lines WHERE journal_id = ?1", params![j]).is_err());
        set_period_status(&c, 1, "2026-27", true).unwrap();
        assert!(post(&c, None, "2026-11-01", "x", "manual", None, vec![dr(cash, 1), cr(fees, 1)]).unwrap_err().contains("closed"));
        assert!(c.execute("INSERT INTO journals (number, date, source_type) VALUES ('X','2026-11-01','manual')", []).is_err(), "the database refuses it too");
        set_period_status(&c, 1, "2026-27", false).unwrap();
        post(&c, None, "2026-11-01", "ok", "manual", None, vec![dr(cash, 1), cr(fees, 1)]).unwrap();
        assert_eq!(unbalanced_journals(&c).unwrap(), 0);
    }

    #[test]
    fn invoice_receipt_with_tds_and_gst_post_correctly_and_agree_with_invoices() {
        let (mut c, dir) = setup();
        let id = invoice(&mut c, &dir, "100000", "intra");
        // Dr AR 1,18,000; Cr fees 1,00,000; Cr CGST 9,000; Cr SGST 9,000
        assert_eq!(balance_of(&c, "1200"), 11_800_000);
        assert_eq!(balance_of(&c, "4000"), -10_000_000);
        assert_eq!((balance_of(&c, "2100"), balance_of(&c, "2110")), (-900_000, -900_000));
        // client pays 1,08,000 and deducts 10,000 TDS
        let mut r = rc(id, "108000", "10000", "bank");
        r.reference = "UTR1".into();
        record(&mut c, 1, &r).unwrap();
        assert_eq!(balance_of(&c, "1200"), 0);
        assert_eq!(balance_of(&c, "1210"), 1_000_000);
        assert_eq!(balance_of(&c, "1010"), 10_800_000, "no bank account yet, so the unassigned bank account");
        assert!(ar_check(&c, "2026-12-31").unwrap().agrees);
        let tb = trial_balance(&c, "2026-12-31").unwrap();
        assert!(tb.balanced);
        let pl = profit_and_loss(&c, "2026-04-01", "2027-03-31").unwrap();
        assert_eq!(pl.profit_paise, 10_000_000);
        assert!(balance_sheet(&c, "2026-12-31").unwrap().balanced);
        let tax = tax_summary(&c, "2026-10-01", "2026-10-31").unwrap();
        assert_eq!((tax.output_cgst.as_str(), tax.tds_receivable.as_str()), ("₹9,000.00", "₹10,000.00"));
        // reversing the receipt reverses its journal and brings the receivable back
        let rid: i64 = c.query_row("SELECT id FROM receipts", [], |r| r.get(0)).unwrap();
        crate::receipts::reverse(&mut c, 1, rid, "Cheque bounced").unwrap();
        assert_eq!(balance_of(&c, "1200"), 11_800_000);
        assert!(ar_check(&c, "2026-12-31").unwrap().agrees);
        // cancelling is blocked while money is involved; a credit note reduces fees and GST together
        let n = crate::billing::issue_credit_note(&mut c, &dir, 1, id, "2026-10-25", "Scope cut", vec![LineInput { description: "x".into(), qty: "1".into(), rate: "10000".into() }]).unwrap();
        assert!(n.starts_with("CN/"));
        assert_eq!(balance_of(&c, "4000"), -9_000_000);
        assert_eq!(balance_of(&c, "2100"), -810_000);
        assert!(ar_check(&c, "2026-12-31").unwrap().agrees);
        assert!(trial_balance(&c, "2026-12-31").unwrap().balanced);
        assert_eq!(unbalanced_journals(&c).unwrap(), 0);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn cancel_reverses_the_invoice_and_composition_tax_is_an_expense_not_a_charge() {
        let (mut c, dir) = setup();
        let id = invoice(&mut c, &dir, "50000", "intra");
        crate::billing::cancel(&mut c, 1, id, "Raised in error").unwrap();
        assert_eq!((balance_of(&c, "1200"), balance_of(&c, "4000"), balance_of(&c, "2100")), (0, 0, 0));
        c.execute("UPDATE company_profile SET gst_scheme='composition'", []).unwrap();
        let id2 = invoice(&mut c, &dir, "100000", "intra");
        assert_eq!(balance_of(&c, "1200"), 10_000_000, "the client is billed the fee only");
        assert_eq!((balance_of(&c, "5950"), balance_of(&c, "2130")), (600_000, -600_000));
        let _ = id2;
        assert_eq!(profit_and_loss(&c, "2026-04-01", "2027-03-31").unwrap().profit_paise, 10_000_000 - 600_000);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn a_journal_is_reversed_once_and_backfill_is_idempotent() {
        let (mut c, dir) = setup();
        let (cash, fees) = (acct(&c, "1000").unwrap(), acct(&c, "4000").unwrap());
        let j = post(&c, Some(1), "2026-10-01", "Sale", "manual", None, vec![dr(cash, 500), cr(fees, 500)]).unwrap();
        reverse_journal(&c, Some(1), j, "Mistake").unwrap();
        assert!(reverse_journal(&c, Some(1), j, "Again").unwrap_err().contains("already"));
        assert_eq!(balance_of(&c, "1000"), 0);
        assert!(journal_list(&c, "2000-01-01", "2100-01-01", 50).unwrap().iter().any(|r| r.reversed));
        // data that predates the ledger is brought in exactly once
        let id = invoice(&mut c, &dir, "1000", "none");
        assert_eq!(backfill(&c, 1).unwrap(), (0, 0, 0), "already posted at issue time");
        let _ = id;
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn slab_estimate_is_progressive_and_validates_the_table() {
        let (mut c, dir) = setup();
        c.execute("UPDATE company_profile SET gst_scheme='none'", []).unwrap();
        invoice(&mut c, &dir, "1000000", "none"); // profit 10,00,000
        let slabs = vec![
            Slab { upto: "300000".into(), rate_percent: "0".into() },
            Slab { upto: "700000".into(), rate_percent: "10".into() },
            Slab { upto: "".into(), rate_percent: "20".into() },
        ];
        let e = income_tax_by_slabs(&c, "2026-04-01", "2027-03-31", &slabs, "4").unwrap();
        // 0 on the first 3,00,000; 10% of the next 4,00,000 = 40,000; 20% of the last 3,00,000 = 60,000; cess 4% of 1,00,000
        assert_eq!((e.tax_display.as_str(), e.cess_display.as_str(), e.total_display.as_str()), ("₹1,00,000.00", "₹4,000.00", "₹1,04,000.00"));
        let bad = vec![Slab { upto: "".into(), rate_percent: "10".into() }, Slab { upto: "5".into(), rate_percent: "20".into() }];
        assert!(income_tax_by_slabs(&c, "2026-04-01", "2027-03-31", &bad, "4").is_err());
        assert!(income_tax_by_slabs(&c, "2026-04-01", "2027-03-31", &[], "4").is_err());
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn ledger_view_runs_a_balance_and_estimate_is_labelled_arithmetic() {
        let (mut c, dir) = setup();
        c.execute("UPDATE company_profile SET gst_scheme='none'", []).unwrap();
        let id = invoice(&mut c, &dir, "100000", "none");
        record(&mut c, 1, &rc(id, "50000", "", "cash")).unwrap();
        let l = account_ledger(&c, acct(&c, "1200").unwrap(), "2026-04-01", "2027-03-31").unwrap();
        assert_eq!(l.rows.len(), 2);
        assert_eq!((l.opening_display.as_str(), l.closing_display.as_str()), ("₹0.00", "₹50,000.00"));
        assert_eq!(balance_of(&c, "1000"), 5_000_000, "cash receipts go to cash in hand");
        let e = income_tax_estimate(&c, "2026-04-01", "2027-03-31", "25").unwrap();
        assert_eq!(e.tax_display, "₹25,000.00");
        assert_eq!(e.cess_display, "₹1,000.00");
        assert!(income_tax_estimate(&c, "2026-04-01", "2027-03-31", "250").is_err());
        let _ = std::fs::remove_dir_all(dir);
    }
}
