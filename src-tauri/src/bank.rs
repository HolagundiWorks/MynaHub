//! Bank statement import and reconciliation.
//! Importing never marks anything paid: it only stores rows. Receipts are created solely by an explicit
//! `reconcile` call, which records the receipt (with any TDS) and links it to the bank transaction.

use crate::auth::Permission;
use crate::billing::{fmt_inr, parse_decimal, sha2_lite::sha256_hex, validate_date};
use crate::commands::{require, AppState, INTERNAL};
use crate::db;
use crate::receipts::{insert_receipt, ReceiptInput};
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};
use tauri::State;

// ---------- parsing ----------

#[derive(Serialize, Deserialize, Clone)]
pub struct Mapping {
    /// One of "," ";" "\t" "|".
    pub delimiter: String,
    pub has_header: bool,
    /// "dmy", "mdy" or "ymd" (separators and month names are detected).
    pub date_order: String,
    pub date: usize,
    pub narration: Option<usize>,
    pub reference: Option<usize>,
    pub debit: Option<usize>,
    pub credit: Option<usize>,
    /// Single signed amount column (negative or "Dr" = debit, positive or "Cr" = credit).
    pub amount: Option<usize>,
    pub balance: Option<usize>,
}

#[derive(Serialize, Clone, Debug)]
pub struct ParsedRow {
    pub row_no: usize,
    pub raw: String,
    pub txn_date: String,
    pub narration: String,
    pub reference: String,
    pub debit_paise: i64,
    pub credit_paise: i64,
    pub balance_paise: Option<i64>,
    pub error: Option<String>,
}

pub fn parse_bank_date(s: &str, order: &str) -> Result<String, String> {
    let bad = || format!("'{s}' is not a recognised date.");
    let cleaned: String = s.trim().chars().map(|c| if "/-. ".contains(c) { ' ' } else { c }).collect();
    let parts: Vec<&str> = cleaned.split_whitespace().collect();
    if parts.len() != 3 {
        return Err(bad());
    }
    let month = |p: &str| -> Option<u32> {
        if let Ok(n) = p.parse::<u32>() {
            return Some(n);
        }
        let names = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
        let l = p.to_lowercase();
        names.iter().position(|n| l.starts_with(n)).map(|i| i as u32 + 1)
    };
    let (d, m, y) = match order {
        "ymd" => (parts[2], month(parts[1]), parts[0]),
        "mdy" => (parts[1], month(parts[0]), parts[2]),
        _ => (parts[0], month(parts[1]), parts[2]),
    };
    let m = m.ok_or_else(bad)?;
    let d: u32 = d.parse().map_err(|_| bad())?;
    let mut y: i32 = y.parse().map_err(|_| bad())?;
    if y < 100 {
        y += 2000;
    }
    let iso = format!("{y:04}-{m:02}-{d:02}");
    validate_date(&iso).map_err(|_| bad())?;
    Ok(iso)
}

/// Returns (paise, is_debit_marker). Empty cells give Ok(None).
fn parse_amount_cell(s: &str) -> Result<Option<(i64, Option<bool>)>, String> {
    let mut t = s.trim().to_string();
    if t.is_empty() {
        return Ok(None);
    }
    let mut neg: Option<bool> = None;
    let lower = t.to_lowercase();
    if lower.ends_with("dr") || lower.ends_with("dr.") {
        neg = Some(true);
    } else if lower.ends_with("cr") || lower.ends_with("cr.") {
        neg = Some(false);
    }
    t = t.replace(['₹', ','], "");
    for w in ["INR", "inr", "Rs.", "Rs", "rs", "Dr.", "Cr.", "DR", "CR", "Dr", "Cr", "dr", "cr"] {
        t = t.replace(w, "");
    }
    t = t.trim().to_string();
    if t.starts_with('(') && t.ends_with(')') {
        neg = Some(true);
        t = t[1..t.len() - 1].to_string();
    }
    if let Some(rest) = t.strip_prefix('-') {
        neg = Some(true);
        t = rest.to_string();
    } else if let Some(rest) = t.strip_prefix('+') {
        t = rest.to_string();
    }
    let t = t.trim();
    let v = parse_decimal(t, 2)?;
    Ok(Some((v, neg)))
}

pub fn parse_statement(text: &str, m: &Mapping) -> Result<Vec<ParsedRow>, String> {
    let delim = match m.delimiter.as_str() {
        "," => b',',
        ";" => b';',
        "\t" => b'\t',
        "|" => b'|',
        _ => return Err("Unsupported delimiter.".into()),
    };
    if m.amount.is_none() && m.debit.is_none() && m.credit.is_none() {
        return Err("Map an amount column, or debit and credit columns.".into());
    }
    let text = text.trim_start_matches('\u{feff}');
    let mut rdr = csv::ReaderBuilder::new().has_headers(false).delimiter(delim).flexible(true).from_reader(text.as_bytes());
    let mut out = Vec::new();
    for (i, rec) in rdr.records().enumerate() {
        let row_no = i + 1;
        if m.has_header && row_no == 1 {
            continue;
        }
        let rec = match rec {
            Ok(r) => r,
            Err(e) => {
                out.push(ParsedRow { row_no, raw: String::new(), txn_date: String::new(), narration: String::new(), reference: String::new(), debit_paise: 0, credit_paise: 0, balance_paise: None, error: Some(e.to_string()) });
                continue;
            }
        };
        let raw = rec.iter().collect::<Vec<_>>().join(&m.delimiter);
        if rec.iter().all(|c| c.trim().is_empty()) {
            continue;
        }
        let cell = |idx: Option<usize>| idx.and_then(|i| rec.get(i)).unwrap_or("").trim().to_string();
        let mut row = ParsedRow {
            row_no, raw, txn_date: String::new(), narration: cell(m.narration), reference: cell(m.reference),
            debit_paise: 0, credit_paise: 0, balance_paise: None, error: None,
        };
        let result: Result<(), String> = (|| {
            row.txn_date = parse_bank_date(rec.get(m.date).unwrap_or(""), &m.date_order)?;
            if let Some(a) = m.amount {
                match parse_amount_cell(rec.get(a).unwrap_or(""))? {
                    Some((v, Some(true))) => row.debit_paise = v,
                    Some((v, _)) => row.credit_paise = v,
                    None => {}
                }
            }
            if let Some(d) = m.debit {
                if let Some((v, _)) = parse_amount_cell(rec.get(d).unwrap_or(""))? {
                    row.debit_paise += v;
                }
            }
            if let Some(c) = m.credit {
                if let Some((v, _)) = parse_amount_cell(rec.get(c).unwrap_or(""))? {
                    row.credit_paise += v;
                }
            }
            if let Some(b) = m.balance {
                // Balance is informational; an unreadable one must not reject the row.
                row.balance_paise = parse_amount_cell(rec.get(b).unwrap_or("")).ok().flatten().map(|(v, neg)| if neg == Some(true) { -v } else { v });
            }
            match (row.debit_paise > 0, row.credit_paise > 0) {
                (true, true) => Err("Row has both a debit and a credit.".to_string()),
                (false, false) => Err("Row has no amount.".to_string()),
                _ => Ok(()),
            }
        })();
        row.error = result.err();
        out.push(row);
    }
    Ok(out)
}

fn dedupe_key(r: &ParsedRow, account_id: i64) -> String {
    sha256_hex(format!("{account_id}|{}|{}|{}|{}|{}|{:?}", r.txn_date, r.narration, r.reference, r.debit_paise, r.credit_paise, r.balance_paise).as_bytes())
}

// ---------- import ----------

#[derive(Serialize, Debug)]
pub struct ImportResult {
    pub batch_id: i64,
    pub imported: usize,
    pub duplicates: usize,
    pub rejected: usize,
}

pub fn import(conn: &mut Connection, user_id: i64, account_id: i64, filename: &str, text: &str, m: &Mapping) -> Result<ImportResult, String> {
    let rows = parse_statement(text, m)?;
    if rows.is_empty() {
        return Err("The file has no data rows.".into());
    }
    let sha = sha256_hex(text.as_bytes());
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    require_active_account(&tx, account_id)?;
    if let Ok((id, at)) = tx.query_row::<(i64, String), _, _>("SELECT id, imported_at FROM bank_batches WHERE file_sha256 = ?1", params![sha], |r| Ok((r.get(0)?, r.get(1)?))) {
        return Err(format!("This exact file was already imported (batch {id}, {at})."));
    }
    tx.execute("INSERT INTO bank_batches (filename, file_sha256, row_count, imported_by, account_id) VALUES (?1,?2,?3,?4,?5)", params![filename, sha, rows.len() as i64, user_id, account_id])
        .map_err(|e| e.to_string())?;
    let batch_id = tx.last_insert_rowid();
    let (mut imported, mut duplicates, mut rejected) = (0, 0, 0);
    for r in &rows {
        if let Some(reason) = &r.error {
            tx.execute("INSERT INTO bank_rejected_rows (batch_id, row_no, raw_row, reason) VALUES (?1,?2,?3,?4)", params![batch_id, r.row_no as i64, r.raw, reason])
                .map_err(|e| e.to_string())?;
            rejected += 1;
            continue;
        }
        let key = dedupe_key(r, account_id);
        let seen: i64 = tx
            .query_row("SELECT COUNT(*) FROM bank_transactions WHERE dedupe_key = ?1 AND batch_id <> ?2", params![key, batch_id], |x| x.get(0))
            .map_err(|e| e.to_string())?;
        let status = if seen > 0 { "duplicate" } else { "unmatched" };
        tx.execute(
            "INSERT INTO bank_transactions (batch_id, row_no, txn_date, narration, reference, debit_paise, credit_paise, balance_paise, raw_row, dedupe_key, status)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11)",
            params![batch_id, r.row_no as i64, r.txn_date, r.narration, r.reference, r.debit_paise, r.credit_paise, r.balance_paise, r.raw, key, status],
        )
        .map_err(|e| e.to_string())?;
        if status == "duplicate" { duplicates += 1 } else { imported += 1 }
    }
    db::audit(&tx, Some(user_id), "bank_import", &format!("account {account_id}, batch {batch_id}: {imported} rows, {duplicates} duplicates, {rejected} rejected"));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(ImportResult { batch_id, imported, duplicates, rejected })
}

// ---------- listing ----------

#[derive(Serialize)]
pub struct TxnRow {
    pub id: i64,
    pub txn_date: String,
    pub narration: String,
    pub reference: String,
    pub debit_display: String,
    pub credit_display: String,
    pub credit_paise: i64,
    pub debit_paise: i64,
    pub allocated_display: String,
    pub unallocated_paise: i64,
    pub status: String,
    pub note: String,
    pub account_id: i64,
    pub account_name: String,
}

pub fn list_txns(conn: &Connection, status: Option<&str>, account_id: Option<i64>) -> Result<Vec<TxnRow>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT t.id, t.txn_date, t.narration, t.reference, t.debit_paise, t.credit_paise, t.status, t.note,
                    COALESCE((SELECT SUM(r.amount_paise) FROM bank_txn_receipts l JOIN effective_receipts r ON r.id = l.receipt_id WHERE l.txn_id = t.id), 0),
                    b.account_id, a.name
             FROM bank_transactions t JOIN bank_batches b ON b.id = t.batch_id JOIN bank_accounts a ON a.id = b.account_id
             WHERE (?1 IS NULL OR t.status = ?1) AND (?2 IS NULL OR b.account_id = ?2) ORDER BY t.txn_date DESC, t.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![status, account_id], |r| {
            let (debit, credit, alloc): (i64, i64, i64) = (r.get(4)?, r.get(5)?, r.get(8)?);
            Ok(TxnRow {
                id: r.get(0)?, txn_date: r.get(1)?, narration: r.get(2)?, reference: r.get(3)?,
                debit_display: if debit > 0 { fmt_inr(debit) } else { String::new() },
                credit_display: if credit > 0 { fmt_inr(credit) } else { String::new() },
                credit_paise: credit, debit_paise: debit, allocated_display: fmt_inr(alloc), unallocated_paise: credit - alloc,
                status: r.get(6)?, note: r.get(7)?, account_id: r.get(9)?, account_name: r.get(10)?,
            })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

#[derive(Serialize)]
pub struct Summary {
    pub unmatched: i64,
    pub partial: i64,
    pub matched: i64,
    pub ignored: i64,
    pub duplicate: i64,
    pub rejected_rows: i64,
    pub unallocated_credits_display: String,
}

pub fn summary(conn: &Connection, account_id: Option<i64>) -> Result<Summary, String> {
    let count = |s: &str| -> Result<i64, String> {
        conn.query_row(
            "SELECT COUNT(*) FROM bank_transactions t JOIN bank_batches b ON b.id = t.batch_id WHERE t.status = ?1 AND (?2 IS NULL OR b.account_id = ?2)",
            params![s, account_id],
            |r| r.get(0),
        )
        .map_err(|e| e.to_string())
    };
    let rejected: i64 = conn
        .query_row("SELECT COUNT(*) FROM bank_rejected_rows x JOIN bank_batches b ON b.id = x.batch_id WHERE ?1 IS NULL OR b.account_id = ?1", params![account_id], |r| r.get(0))
        .map_err(|e| e.to_string())?;
    let unalloc: i64 = conn
        .query_row(
            "SELECT COALESCE(SUM(t.credit_paise - COALESCE((SELECT SUM(r.amount_paise) FROM bank_txn_receipts l JOIN effective_receipts r ON r.id = l.receipt_id WHERE l.txn_id = t.id), 0)), 0)
             FROM bank_transactions t JOIN bank_batches b ON b.id = t.batch_id
             WHERE t.status IN ('unmatched','partial') AND t.credit_paise > 0 AND (?1 IS NULL OR b.account_id = ?1)",
            params![account_id],
            |r| r.get(0),
        )
        .map_err(|e| e.to_string())?;
    Ok(Summary {
        unmatched: count("unmatched")?, partial: count("partial")?, matched: count("matched")?, ignored: count("ignored")?,
        duplicate: count("duplicate")?, rejected_rows: rejected, unallocated_credits_display: fmt_inr(unalloc),
    })
}

#[derive(Serialize)]
pub struct RejectedRow {
    pub batch_id: i64,
    pub row_no: i64,
    pub raw_row: String,
    pub reason: String,
}

pub fn list_rejected(conn: &Connection) -> Result<Vec<RejectedRow>, String> {
    let mut stmt = conn.prepare("SELECT batch_id, row_no, raw_row, reason FROM bank_rejected_rows ORDER BY batch_id DESC, row_no").map_err(|e| e.to_string())?;
    let rows = stmt.query_map([], |r| Ok(RejectedRow { batch_id: r.get(0)?, row_no: r.get(1)?, raw_row: r.get(2)?, reason: r.get(3)? })).map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

// ---------- suggestions ----------

#[derive(Serialize)]
pub struct Suggestion {
    pub invoice_id: i64,
    pub number: String,
    pub client_name: String,
    pub balance_paise: i64,
    pub balance_display: String,
    pub score: i32,
    pub reasons: Vec<String>,
    /// If the credit only matches after TDS, the TDS (section, amount) that would explain it.
    pub tds_section: Option<String>,
    pub tds_amount: Option<String>,
}

fn alnum(s: &str) -> String {
    s.chars().filter(|c| c.is_alphanumeric()).collect::<String>().to_lowercase()
}

pub fn suggest(conn: &Connection, txn_id: i64) -> Result<Vec<Suggestion>, String> {
    let (narration, reference, credit): (String, String, i64) = conn
        .query_row("SELECT narration, reference, credit_paise FROM bank_transactions WHERE id = ?1", params![txn_id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
        .map_err(|_| "Transaction not found.".to_string())?;
    if credit == 0 {
        return Ok(vec![]);
    }
    let hay = alnum(&format!("{narration} {reference}"));
    let mut stmt = conn
        .prepare(
            "SELECT i.id, i.number, c.name, i.subtotal_paise,
                    i.total_paise - (SELECT received_paise + tds_paise + credited_paise FROM invoice_balances WHERE invoice_id = i.id)
             FROM invoices i JOIN projects p ON p.id = i.project_id JOIN clients c ON c.id = p.client_id
             WHERE i.status = 'issued' AND i.kind = 'tax_invoice'",
        )
        .map_err(|e| e.to_string())?;
    let candidates = stmt
        .query_map([], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, String>(1)?, r.get::<_, String>(2)?, r.get::<_, i64>(3)?, r.get::<_, i64>(4)?)))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    let mut out = Vec::new();
    for (id, number, client, subtotal, balance) in candidates {
        if balance <= 0 {
            continue;
        }
        let (mut score, mut reasons, mut tds_section, mut tds_amount) = (0, Vec::new(), None, None);
        if hay.contains(&alnum(&number)) {
            score += 5;
            reasons.push("Invoice number appears in the narration".to_string());
        }
        if credit == balance {
            score += 4;
            reasons.push("Amount equals the outstanding balance".to_string());
        } else {
            for (code, pct) in [("194J", 10i64), ("194C-1", 1), ("194C-2", 2), ("194M", 5)] {
                let tds = (subtotal * pct * 100 + 5_000) / 10_000;
                if credit + tds == balance {
                    score += 3;
                    reasons.push(format!("Amount matches the balance after {pct}% TDS (section {code})"));
                    tds_section = Some(code.to_string());
                    tds_amount = Some(format!("{}.{:02}", tds / 100, tds % 100));
                    break;
                }
            }
            if credit < balance && score == 0 {
                // possible part payment; only worth listing if the client matches below
            }
        }
        let client_hit = client.split_whitespace().filter(|w| w.chars().count() >= 4).any(|w| hay.contains(&alnum(w)));
        if client_hit {
            score += 2;
            reasons.push("Client name appears in the narration".to_string());
        }
        if score > 0 {
            out.push(Suggestion { invoice_id: id, number, client_name: client, balance_paise: balance, balance_display: fmt_inr(balance), score, reasons, tds_section, tds_amount });
        }
    }
    out.sort_by(|a, b| b.score.cmp(&a.score));
    Ok(out)
}

// ---------- reconcile / ignore ----------

#[derive(Serialize, Deserialize)]
pub struct Allocation {
    pub invoice_id: i64,
    pub amount: String,
    pub tds: String,
    pub tds_section: String,
}

pub fn reconcile(conn: &mut Connection, user_id: i64, txn_id: i64, allocations: &[Allocation]) -> Result<Vec<String>, String> {
    if allocations.is_empty() {
        return Err("Add at least one allocation.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let (date, reference, credit, status, account_id): (String, String, i64, String, i64) = tx
        .query_row(
            "SELECT t.txn_date, t.reference, t.credit_paise, t.status, b.account_id FROM bank_transactions t JOIN bank_batches b ON b.id = t.batch_id WHERE t.id = ?1",
            params![txn_id],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?)),
        )
        .map_err(|_| "Transaction not found.".to_string())?;
    if credit == 0 {
        return Err("Only bank credits can be matched to invoices.".into());
    }
    if status != "unmatched" && status != "partial" {
        return Err(format!("This transaction is {status} and cannot be matched."));
    }
    let already: i64 = tx
        .query_row(
            "SELECT COALESCE(SUM(r.amount_paise),0) FROM bank_txn_receipts l JOIN effective_receipts r ON r.id = l.receipt_id WHERE l.txn_id = ?1",
            params![txn_id],
            |r| r.get(0),
        )
        .map_err(|e| e.to_string())?;
    let mut numbers = Vec::new();
    let mut new_total = 0i64;
    for a in allocations {
        let amount = if a.amount.trim().is_empty() { 0 } else { parse_decimal(&a.amount, 2).map_err(|e| format!("Amount. {e}"))? };
        new_total += amount;
        let input = ReceiptInput {
            invoice_id: a.invoice_id, received_date: date.clone(), amount: a.amount.clone(), tds: a.tds.clone(),
            tds_section: a.tds_section.clone(), mode: "bank".into(), reference: reference.clone(), notes: format!("Bank transaction {txn_id}"),
            bank_account_id: Some(account_id),
        };
        let number = insert_receipt(&tx, user_id, &input, false)?;
        let rid: i64 = tx.query_row("SELECT id FROM receipts WHERE number = ?1", params![number], |r| r.get(0)).map_err(|e| e.to_string())?;
        tx.execute("INSERT INTO bank_txn_receipts (txn_id, receipt_id, matched_by) VALUES (?1,?2,?3)", params![txn_id, rid, user_id]).map_err(|e| e.to_string())?;
        numbers.push(number);
    }
    if already + new_total > credit {
        return Err(format!("Allocations exceed the bank credit of {}.", fmt_inr(credit)));
    }
    let new_status = if already + new_total == credit { "matched" } else { "partial" };
    tx.execute("UPDATE bank_transactions SET status = ?1 WHERE id = ?2", params![new_status, txn_id]).map_err(|e| e.to_string())?;
    db::audit(&tx, Some(user_id), "bank_reconciled", &format!("txn {txn_id} -> {}", numbers.join(", ")));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(numbers)
}

pub fn ignore(conn: &Connection, user_id: i64, txn_id: i64, note: &str, undo: bool) -> Result<(), String> {
    let (status, linked): (String, i64) = conn
        .query_row("SELECT status, (SELECT COUNT(*) FROM bank_txn_receipts WHERE txn_id = bank_transactions.id) FROM bank_transactions WHERE id = ?1", params![txn_id], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|_| "Transaction not found.".to_string())?;
    if undo {
        if status != "ignored" {
            return Err("Only ignored transactions can be restored.".into());
        }
        conn.execute("UPDATE bank_transactions SET status = 'unmatched', note = '' WHERE id = ?1", params![txn_id]).map_err(|e| e.to_string())?;
        db::audit(conn, Some(user_id), "bank_unignored", &format!("txn {txn_id}"));
        return Ok(());
    }
    if linked > 0 {
        return Err("This transaction already has receipts and cannot be ignored.".into());
    }
    if status == "ignored" {
        return Ok(());
    }
    if note.trim().is_empty() {
        return Err("Give a reason (for example: bank charge, refund, own transfer).".into());
    }
    conn.execute("UPDATE bank_transactions SET status = 'ignored', note = ?1 WHERE id = ?2", params![note.trim(), txn_id]).map_err(|e| e.to_string())?;
    db::audit(conn, Some(user_id), "bank_ignored", &format!("txn {txn_id}: {}", note.trim()));
    Ok(())
}

// ---------- accounts ----------

#[derive(Serialize, Deserialize)]
pub struct Account {
    pub id: Option<i64>,
    pub name: String,
    pub bank_name: String,
    pub account_holder: String,
    pub account_number: String,
    pub ifsc: String,
    pub is_primary: bool,
    #[serde(default = "yes")]
    pub active: bool,
}

fn yes() -> bool {
    true
}

fn require_active_account(conn: &Connection, id: i64) -> Result<(), String> {
    let active: Option<i64> = conn.query_row("SELECT active FROM bank_accounts WHERE id = ?1", params![id], |r| r.get(0)).ok();
    match active {
        None => Err("Choose a bank account.".into()),
        Some(0) => Err("That bank account is inactive.".into()),
        Some(_) => Ok(()),
    }
}

pub fn list_accounts(conn: &Connection) -> Result<Vec<Account>, String> {
    let mut stmt = conn
        .prepare("SELECT id, name, bank_name, account_holder, account_number, ifsc, is_primary, active FROM bank_accounts ORDER BY is_primary DESC, name")
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| {
            Ok(Account { id: Some(r.get(0)?), name: r.get(1)?, bank_name: r.get(2)?, account_holder: r.get(3)?, account_number: r.get(4)?, ifsc: r.get(5)?, is_primary: r.get::<_, i64>(6)? != 0, active: r.get::<_, i64>(7)? != 0 })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn save_account(conn: &mut Connection, actor: i64, a: &Account) -> Result<i64, String> {
    let name = a.name.trim();
    if name.is_empty() {
        return Err("Give the account a name (for example 'HDFC current').".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let dup = |e: rusqlite::Error| match e {
        rusqlite::Error::SqliteFailure(f, _) if f.code == rusqlite::ErrorCode::ConstraintViolation => "An account with this name already exists.".to_string(),
        other => other.to_string(),
    };
    let id = match a.id {
        Some(id) => {
            tx.execute(
                "UPDATE bank_accounts SET name=?1, bank_name=?2, account_holder=?3, account_number=?4, ifsc=?5, active=?6 WHERE id=?7",
                params![name, a.bank_name.trim(), a.account_holder.trim(), a.account_number.trim(), a.ifsc.trim().to_uppercase(), a.active, id],
            )
            .map_err(dup)?;
            id
        }
        None => {
            tx.execute(
                "INSERT INTO bank_accounts (name, bank_name, account_holder, account_number, ifsc, active) VALUES (?1,?2,?3,?4,?5,?6)",
                params![name, a.bank_name.trim(), a.account_holder.trim(), a.account_number.trim(), a.ifsc.trim().to_uppercase(), a.active],
            )
            .map_err(dup)?;
            tx.last_insert_rowid()
        }
    };
    crate::ledger::ensure_bank_ledger(&tx, id, name)?;
    // Exactly one primary among active accounts; the first account created becomes primary.
    let other_primary: i64 = tx
        .query_row("SELECT COUNT(*) FROM bank_accounts WHERE is_primary = 1 AND active = 1 AND id <> ?1", params![id], |r| r.get(0))
        .map_err(|e| e.to_string())?;
    if a.active && (a.is_primary || other_primary == 0) {
        tx.execute("UPDATE bank_accounts SET is_primary = (id = ?1)", params![id]).map_err(|e| e.to_string())?;
    } else if !a.active {
        tx.execute("UPDATE bank_accounts SET is_primary = 0 WHERE id = ?1", params![id]).map_err(|e| e.to_string())?;
    }
    db::audit(&tx, Some(actor), "bank_account_saved", &format!("account {id}: {name}"));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(id)
}

// ---------- manual entries and manual linking ----------

#[derive(Deserialize)]
pub struct ManualEntry {
    pub account_id: i64,
    pub txn_date: String,
    pub narration: String,
    pub reference: String,
    /// "credit" or "debit".
    pub direction: String,
    pub amount: String,
}

/// Records a bank transaction by hand (no statement available). It is a normal, unmatched transaction afterwards.
pub fn add_manual(conn: &mut Connection, actor: i64, e: &ManualEntry) -> Result<i64, String> {
    validate_date(&e.txn_date)?;
    let amount = parse_decimal(&e.amount, 2).map_err(|x| format!("Amount. {x}"))?;
    if amount == 0 {
        return Err("Amount must be more than zero.".into());
    }
    let (debit, credit) = match e.direction.as_str() {
        "credit" => (0, amount),
        "debit" => (amount, 0),
        _ => return Err("Choose credit or debit.".into()),
    };
    let tx = conn.transaction().map_err(|x| x.to_string())?;
    require_active_account(&tx, e.account_id)?;
    let marker = format!("manual:{}", e.account_id);
    let batch_id: i64 = match tx.query_row("SELECT id FROM bank_batches WHERE file_sha256 = ?1", params![marker], |r| r.get(0)) {
        Ok(id) => id,
        Err(_) => {
            tx.execute(
                "INSERT INTO bank_batches (filename, file_sha256, row_count, imported_by, account_id) VALUES ('Manual entries', ?1, 0, ?2, ?3)",
                params![marker, actor, e.account_id],
            )
            .map_err(|x| x.to_string())?;
            tx.last_insert_rowid()
        }
    };
    let row_no: i64 = tx.query_row("SELECT COUNT(*) + 1 FROM bank_transactions WHERE batch_id = ?1", params![batch_id], |r| r.get(0)).map_err(|x| x.to_string())?;
    let parsed = ParsedRow {
        row_no: row_no as usize, raw: String::new(), txn_date: e.txn_date.clone(), narration: e.narration.trim().to_string(),
        reference: e.reference.trim().to_string(), debit_paise: debit, credit_paise: credit, balance_paise: None, error: None,
    };
    let key = dedupe_key(&parsed, e.account_id);
    let dup: i64 = tx.query_row("SELECT COUNT(*) FROM bank_transactions WHERE dedupe_key = ?1", params![key], |r| r.get(0)).map_err(|x| x.to_string())?;
    if dup > 0 {
        return Err("An identical transaction already exists on this account.".into());
    }
    tx.execute(
        "INSERT INTO bank_transactions (batch_id, row_no, txn_date, narration, reference, debit_paise, credit_paise, raw_row, dedupe_key, status)
         VALUES (?1,?2,?3,?4,?5,?6,?7,'manual entry',?8,'unmatched')",
        params![batch_id, row_no, parsed.txn_date, parsed.narration, parsed.reference, debit, credit, key],
    )
    .map_err(|x| x.to_string())?;
    let id = tx.last_insert_rowid();
    tx.execute("UPDATE bank_batches SET row_count = row_count + 1 WHERE id = ?1", params![batch_id]).map_err(|x| x.to_string())?;
    db::audit(&tx, Some(actor), "bank_manual_entry", &format!("txn {id} on account {}", e.account_id));
    tx.commit().map_err(|x| x.to_string())?;
    Ok(id)
}

#[derive(Serialize)]
pub struct UnlinkedReceipt {
    pub id: i64,
    pub number: String,
    pub invoice_number: String,
    pub client_name: String,
    pub received_date: String,
    pub amount_paise: i64,
    pub amount_display: String,
    pub reference: String,
    pub mode: String,
}

/// Receipts that were recorded by hand and are not yet tied to any bank transaction.
pub fn unlinked_receipts(conn: &Connection) -> Result<Vec<UnlinkedReceipt>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT r.id, r.number, i.number, c.name, r.received_date, r.amount_paise, r.reference, r.mode
             FROM effective_receipts r JOIN invoices i ON i.id = r.invoice_id JOIN projects p ON p.id = i.project_id JOIN clients c ON c.id = p.client_id
             WHERE r.amount_paise > 0 AND r.id NOT IN (SELECT receipt_id FROM bank_txn_receipts) ORDER BY r.received_date DESC, r.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| {
            let amt: i64 = r.get(5)?;
            Ok(UnlinkedReceipt {
                id: r.get(0)?, number: r.get(1)?, invoice_number: r.get::<_, Option<String>>(2)?.unwrap_or_default(), client_name: r.get(3)?,
                received_date: r.get(4)?, amount_paise: amt, amount_display: fmt_inr(amt), reference: r.get(6)?, mode: r.get(7)?,
            })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

/// Ties already-recorded receipts to a bank credit. No new receipts or amounts are created.
pub fn link_receipts(conn: &mut Connection, actor: i64, txn_id: i64, receipt_ids: &[i64]) -> Result<(), String> {
    if receipt_ids.is_empty() {
        return Err("Select at least one receipt.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let (credit, status): (i64, String) = tx
        .query_row("SELECT credit_paise, status FROM bank_transactions WHERE id = ?1", params![txn_id], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|_| "Transaction not found.".to_string())?;
    if credit == 0 {
        return Err("Only bank credits can be linked to receipts.".into());
    }
    if status != "unmatched" && status != "partial" {
        return Err(format!("This transaction is {status} and cannot be matched."));
    }
    for rid in receipt_ids {
        let amount: i64 = tx.query_row("SELECT amount_paise FROM effective_receipts WHERE id = ?1", params![rid], |r| r.get(0)).map_err(|_| "Receipt not found.".to_string())?;
        if amount == 0 {
            return Err("A TDS-only receipt has no bank amount to link.".into());
        }
        tx.execute("INSERT INTO bank_txn_receipts (txn_id, receipt_id, matched_by, origin) VALUES (?1,?2,?3,'link')", params![txn_id, rid, actor])
            .map_err(|_| "A selected receipt is already linked to a bank transaction.".to_string())?;
    }
    let allocated: i64 = tx
        .query_row("SELECT COALESCE(SUM(r.amount_paise),0) FROM bank_txn_receipts l JOIN effective_receipts r ON r.id = l.receipt_id WHERE l.txn_id = ?1", params![txn_id], |r| r.get(0))
        .map_err(|e| e.to_string())?;
    if allocated > credit {
        return Err(format!("The selected receipts total more than the bank credit of {}.", fmt_inr(credit)));
    }
    let new_status = if allocated == credit { "matched" } else { "partial" };
    tx.execute("UPDATE bank_transactions SET status = ?1 WHERE id = ?2", params![new_status, txn_id]).map_err(|e| e.to_string())?;
    db::audit(&tx, Some(actor), "bank_receipts_linked", &format!("txn {txn_id} <- receipts {receipt_ids:?}"));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(())
}

/// Undoes a bank match. Receipts created by the match are reversed; receipts that merely were linked are set free again.
pub fn unmatch(conn: &mut Connection, actor: i64, txn_id: i64, reason: &str) -> Result<(), String> {
    if reason.trim().is_empty() {
        return Err("Give a reason for undoing the match.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let status: String = tx.query_row("SELECT status FROM bank_transactions WHERE id = ?1", params![txn_id], |r| r.get(0)).map_err(|_| "Transaction not found.".to_string())?;
    if status != "matched" && status != "partial" {
        return Err("Only matched or partly matched transactions can be unmatched.".into());
    }
    let links: Vec<(i64, String)> = {
        let mut stmt = tx.prepare("SELECT receipt_id, origin FROM bank_txn_receipts WHERE txn_id = ?1").map_err(|e| e.to_string())?;
        let rows = stmt.query_map(params![txn_id], |r| Ok((r.get(0)?, r.get(1)?))).map_err(|e| e.to_string())?;
        rows.collect::<Result<_, _>>().map_err(|e| e.to_string())?
    };
    for (rid, origin) in &links {
        tx.execute("DELETE FROM bank_txn_receipts WHERE txn_id = ?1 AND receipt_id = ?2", params![txn_id, rid]).map_err(|e| e.to_string())?;
        if origin == "allocation" {
            tx.execute(
                "INSERT INTO receipt_reversals (receipt_id, reason, reversed_by) VALUES (?1,?2,?3)",
                params![rid, format!("Bank match undone: {}", reason.trim()), actor],
            )
            .map_err(|e| e.to_string())?;
            crate::ledger::reverse_source(&tx, Some(actor), "receipt", *rid, &format!("Bank match undone: {}", reason.trim()))?;
        }
    }
    tx.execute("UPDATE bank_transactions SET status = 'unmatched' WHERE id = ?1", params![txn_id]).map_err(|e| e.to_string())?;
    db::audit(&tx, Some(actor), "bank_unmatched", &format!("txn {txn_id}: {}", reason.trim()));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(())
}

// ---------- commands ----------

#[tauri::command]
pub fn bank_account_list(state: State<AppState>) -> Result<Vec<Account>, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list_accounts(&conn)
}

#[tauri::command]
pub fn bank_account_save(state: State<AppState>, account: Account) -> Result<i64, String> {
    let uid = require(&state, Permission::Issue)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    save_account(&mut conn, uid, &account)
}

#[tauri::command]
pub fn bank_manual_entry(state: State<AppState>, entry: ManualEntry) -> Result<i64, String> {
    let uid = require(&state, Permission::Issue)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    add_manual(&mut conn, uid, &entry)
}

#[tauri::command]
pub fn bank_unlinked_receipts(state: State<AppState>) -> Result<Vec<UnlinkedReceipt>, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    unlinked_receipts(&conn)
}

#[tauri::command]
pub fn bank_link_receipts(state: State<AppState>, txn_id: i64, receipt_ids: Vec<i64>) -> Result<(), String> {
    let uid = require(&state, Permission::Issue)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    link_receipts(&mut conn, uid, txn_id, &receipt_ids)
}

#[tauri::command]
pub fn bank_unmatch(state: State<AppState>, txn_id: i64, reason: String) -> Result<(), String> {
    let uid = require(&state, Permission::Issue)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    unmatch(&mut conn, uid, txn_id, &reason)
}

#[tauri::command]
pub fn bank_csv_preview(state: State<AppState>, text: String, mapping: Mapping) -> Result<Vec<ParsedRow>, String> {
    require(&state, Permission::Finance)?;
    let mut rows = parse_statement(&text, &mapping)?;
    rows.truncate(200);
    Ok(rows)
}

#[tauri::command]
pub fn bank_import_commit(state: State<AppState>, account_id: i64, filename: String, text: String, mapping: Mapping) -> Result<ImportResult, String> {
    let uid = require(&state, Permission::Issue)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    import(&mut conn, uid, account_id, &filename, &text, &mapping)
}

#[tauri::command]
pub fn bank_txn_list(state: State<AppState>, status: Option<String>, account_id: Option<i64>) -> Result<Vec<TxnRow>, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list_txns(&conn, status.as_deref(), account_id)
}

#[tauri::command]
pub fn bank_summary(state: State<AppState>, account_id: Option<i64>) -> Result<Summary, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    summary(&conn, account_id)
}

#[tauri::command]
pub fn bank_rejected_list(state: State<AppState>) -> Result<Vec<RejectedRow>, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list_rejected(&conn)
}

#[tauri::command]
pub fn bank_txn_suggest(state: State<AppState>, txn_id: i64) -> Result<Vec<Suggestion>, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    suggest(&conn, txn_id)
}

#[tauri::command]
pub fn bank_reconcile(state: State<AppState>, txn_id: i64, allocations: Vec<Allocation>) -> Result<Vec<String>, String> {
    let uid = require(&state, Permission::Issue)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    reconcile(&mut conn, uid, txn_id, &allocations)
}

#[tauri::command]
pub fn bank_txn_ignore(state: State<AppState>, txn_id: i64, note: String, undo: bool) -> Result<(), String> {
    let uid = require(&state, Permission::Issue)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    ignore(&conn, uid, txn_id, &note, undo)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::billing::{issue, save_draft, InvoiceInput, LineInput};

    fn mapping() -> Mapping {
        Mapping { delimiter: ",".into(), has_header: true, date_order: "dmy".into(), date: 0, narration: Some(1), reference: Some(2), debit: Some(3), credit: Some(4), amount: None, balance: Some(5) }
    }

    const CSV: &str = "Date,Narration,Ref,Debit,Credit,Balance\n\
12/10/2026,NEFT ACME DEVELOPERS INV/2026-27/0001,UTR111,,\"1,08,000.00\",\"2,00,000.00\"\n\
13-Oct-2026,Bank charges,CHG1,45.00,,\"1,99,955.00\"\n\
bad-date,Oops,X,,10.00,\n\
14/10/2026,No amount,X,,,\n";

    fn setup() -> (Connection, Vec<i64>, std::path::PathBuf) {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('o','x','owner')", []).unwrap();
        c.execute("INSERT INTO bank_accounts (name, is_primary) VALUES ('HDFC current', 1)", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Acme Developers')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1,'Villa')", []).unwrap();
        c.execute("UPDATE company_profile SET gst_scheme='regular'", []).unwrap();
        let dir = std::env::temp_dir().join(format!("ao_bank_{}_{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()));
        let mut ids = Vec::new();
        for rate in ["100000", "50000"] {
            let id = save_draft(&mut c, &InvoiceInput {
                id: None, project_id: 1, kind: "tax_invoice".into(), issue_date: "2026-10-01".into(), due_date: "".into(),
                gst_mode: "intra".into(), gst_rate: "".into(), notes: "".into(),
                lines: vec![LineInput { description: "Fee".into(), qty: "1".into(), rate: rate.into() }],
            }).unwrap();
            issue(&mut c, id).unwrap();
            ids.push(id);
        }
        (c, ids, dir)
    }

    #[test]
    fn parses_dates_amounts_and_rejects_bad_rows() {
        assert_eq!(parse_bank_date("12/10/2026", "dmy").unwrap(), "2026-10-12");
        assert_eq!(parse_bank_date("13-Oct-26", "dmy").unwrap(), "2026-10-13");
        assert_eq!(parse_bank_date("2026-10-14", "ymd").unwrap(), "2026-10-14");
        assert!(parse_bank_date("31/02/2026", "dmy").is_err());
        let rows = parse_statement(CSV, &mapping()).unwrap();
        assert_eq!(rows.len(), 4);
        assert_eq!((rows[0].credit_paise, rows[0].error.is_none()), (10_800_000, true));
        assert_eq!(rows[1].debit_paise, 4_500);
        assert!(rows[2].error.as_deref().unwrap().contains("not a recognised date"));
        assert!(rows[3].error.as_deref().unwrap().contains("no amount"));
        // single signed-amount column
        let m = Mapping { debit: None, credit: None, amount: Some(3), ..mapping() };
        let r = parse_statement("d,n,r,a\n01/01/2026,x,y,-500.00\n02/01/2026,x,y,700 Cr\n03/01/2026,x,y,250 Dr", &m).unwrap();
        assert_eq!((r[0].debit_paise, r[1].credit_paise, r[2].debit_paise), (50_000, 70_000, 25_000));
    }

    #[test]
    fn import_keeps_originals_flags_duplicates_and_never_marks_paid() {
        let (mut c, ids, dir) = setup();
        let res = import(&mut c, 1, 1, "oct.csv", CSV, &mapping()).unwrap();
        assert_eq!((res.imported, res.duplicates, res.rejected), (2, 0, 2));
        assert!(import(&mut c, 1, 1, "oct-again.csv", CSV, &mapping()).unwrap_err().contains("already imported"));
        // an overlapping statement: same first row plus a new one
        let overlap = format!("{}15/10/2026,Interest,INT1,,12.00,\n", CSV.lines().take(2).collect::<Vec<_>>().join("\n") + "\n");
        let res2 = import(&mut c, 1, 1, "oct-b.csv", &overlap, &mapping()).unwrap();
        assert_eq!((res2.imported, res2.duplicates), (1, 1));
        assert_eq!(summary(&c, None).unwrap().duplicate, 1);
        // nothing is paid yet
        assert_eq!(crate::receipts::balance(&c, ids[0]).unwrap().received_paise, 0);
        // imported data is immutable
        assert!(c.execute("UPDATE bank_transactions SET credit_paise = 1", []).is_err());
        assert!(c.execute("DELETE FROM bank_transactions", []).is_err());
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn suggests_by_number_amount_and_client_and_detects_tds() {
        let (mut c, ids, dir) = setup();
        import(&mut c, 1, 1, "oct.csv", CSV, &mapping()).unwrap();
        let txn: i64 = c.query_row("SELECT id FROM bank_transactions WHERE reference='UTR111'", [], |r| r.get(0)).unwrap();
        let s = suggest(&c, txn).unwrap();
        // 1,08,000 = 1,18,000 - 10% TDS on 1,00,000, and the narration carries the invoice number
        assert_eq!(s[0].invoice_id, ids[0]);
        assert_eq!(s[0].tds_section.as_deref(), Some("194J"));
        assert_eq!(s[0].tds_amount.as_deref(), Some("10000.00"));
        assert!(s[0].reasons.iter().any(|r| r.contains("Invoice number")));
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn reconcile_with_tds_then_split_and_guards() {
        let (mut c, ids, dir) = setup();
        import(&mut c, 1, 1, "oct.csv", CSV, &mapping()).unwrap();
        let txn: i64 = c.query_row("SELECT id FROM bank_transactions WHERE reference='UTR111'", [], |r| r.get(0)).unwrap();
        // over-allocation is refused atomically
        let too_much = [Allocation { invoice_id: ids[0], amount: "108000.01".into(), tds: "10000".into(), tds_section: "194J".into() }];
        assert!(reconcile(&mut c, 1, txn, &too_much).is_err());
        assert_eq!(crate::receipts::balance(&c, ids[0]).unwrap().received_paise, 0);
        // 1,08,000 cash + 10,000 TDS settles invoice 1 in full
        let ok = [Allocation { invoice_id: ids[0], amount: "108000".into(), tds: "10000".into(), tds_section: "194J".into() }];
        let nums = reconcile(&mut c, 1, txn, &ok).unwrap();
        assert_eq!(nums.len(), 1);
        let b = crate::receipts::balance(&c, ids[0]).unwrap();
        assert_eq!((b.balance_paise, b.tds_paise), (0, 1_000_000));
        let t = list_txns(&c, None, None).unwrap().into_iter().find(|t| t.id == txn).unwrap();
        assert_eq!(t.status, "matched");
        assert!(reconcile(&mut c, 1, txn, &ok).is_err(), "a matched credit cannot be used again");
        // ignoring needs a reason and is refused once receipts exist
        let charge: i64 = c.query_row("SELECT id FROM bank_transactions WHERE reference='CHG1'", [], |r| r.get(0)).unwrap();
        assert!(ignore(&c, 1, charge, "", false).is_err());
        ignore(&c, 1, charge, "Bank charge", false).unwrap();
        assert!(ignore(&c, 1, txn, "x", false).is_err());
        assert!(reconcile(&mut c, 1, charge, &ok).is_err(), "debits cannot be matched to invoices");
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn one_credit_can_pay_several_invoices_and_partial_stays_open() {
        let (mut c, ids, dir) = setup();
        // invoice totals: 1,18,000 and 59,000. One credit of 1,50,000 covers part of each.
        let csv = "d,n,r,db,cr,b\n20/10/2026,NEFT ACME,UTR777,,\"1,50,000.00\",\n";
        import(&mut c, 1, 1, "split.csv", csv, &mapping()).unwrap();
        let txn: i64 = c.query_row("SELECT id FROM bank_transactions", [], |r| r.get(0)).unwrap();
        let first = [
            Allocation { invoice_id: ids[0], amount: "100000".into(), tds: "".into(), tds_section: "".into() },
        ];
        reconcile(&mut c, 1, txn, &first).unwrap();
        assert_eq!(list_txns(&c, None, None).unwrap()[0].status, "partial");
        let second = [Allocation { invoice_id: ids[1], amount: "50000".into(), tds: "".into(), tds_section: "".into() }];
        reconcile(&mut c, 1, txn, &second).unwrap();
        assert_eq!(list_txns(&c, None, None).unwrap()[0].status, "matched");
        // and several credits can pay one invoice
        assert_eq!(crate::receipts::balance(&c, ids[0]).unwrap().balance_paise, 1_800_000);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn accounts_keep_statements_separate_and_one_is_primary() {
        let (mut c, _ids, dir) = setup();
        let blank = |name: &str| Account { id: None, name: name.into(), bank_name: "".into(), account_holder: "".into(), account_number: "".into(), ifsc: "".into(), is_primary: false, active: true };
        let second = save_account(&mut c, 1, &blank("SBI savings")).unwrap();
        assert_eq!(list_accounts(&c).unwrap().iter().filter(|a| a.is_primary).count(), 1);
        assert!(save_account(&mut c, 1, &blank("hdfc CURRENT")).unwrap_err().contains("already exists"));
        // the same statement lines on two different accounts are not duplicates of each other
        let r1 = import(&mut c, 1, 1, "a.csv", CSV, &mapping()).unwrap();
        let other = CSV.replace("UTR111", "UTR111 ");
        let r2 = import(&mut c, 1, second, "b.csv", &other, &mapping()).unwrap();
        assert_eq!((r1.duplicates, r2.duplicates), (0, 0));
        assert_eq!(list_txns(&c, None, Some(second)).unwrap().len(), 2);
        assert_eq!(list_txns(&c, None, None).unwrap().len(), 4);
        assert_eq!(summary(&c, Some(second)).unwrap().unmatched, 2);
        // inactive accounts cannot receive imports
        save_account(&mut c, 1, &Account { id: Some(second), active: false, ..blank("SBI savings") }).unwrap();
        assert!(import(&mut c, 1, second, "c.csv", "d,n,r,db,cr,b\n01/01/2026,x,y,,5,\n", &mapping()).unwrap_err().contains("inactive"));
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn manual_entry_then_manual_link_of_an_existing_receipt() {
        let (mut c, ids, dir) = setup();
        // a receipt recorded by hand earlier (no bank statement yet)
        crate::receipts::record(
            &mut c, 1,
            &ReceiptInput { invoice_id: ids[1], received_date: "2026-10-18".into(), amount: "30000".into(), tds: "".into(), tds_section: "".into(), mode: "bank".into(), reference: "".into(), notes: "".into(), bank_account_id: None },
        )
        .unwrap();
        assert_eq!(unlinked_receipts(&c).unwrap().len(), 1);
        // the credit is keyed in manually
        let entry = ManualEntry { account_id: 1, txn_date: "2026-10-18".into(), narration: "Transfer from Acme".into(), reference: "".into(), direction: "credit".into(), amount: "30000".into() };
        let txn = add_manual(&mut c, 1, &entry).unwrap();
        assert!(add_manual(&mut c, 1, &entry).unwrap_err().contains("identical"));
        let rid = unlinked_receipts(&c).unwrap()[0].id;
        link_receipts(&mut c, 1, txn, &[rid]).unwrap();
        assert_eq!(list_txns(&c, None, None).unwrap()[0].status, "matched");
        assert!(unlinked_receipts(&c).unwrap().is_empty());
        // linking did not create money
        assert_eq!(crate::receipts::balance(&c, ids[1]).unwrap().received_paise, 3_000_000);
        // a matched credit cannot take more links, and a receipt cannot be linked twice
        assert!(link_receipts(&mut c, 1, txn, &[rid]).is_err());
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn linking_more_than_the_credit_is_refused() {
        let (mut c, ids, dir) = setup();
        crate::receipts::record(
            &mut c, 1,
            &ReceiptInput { invoice_id: ids[1], received_date: "2026-10-18".into(), amount: "30000".into(), tds: "".into(), tds_section: "".into(), mode: "bank".into(), reference: "".into(), notes: "".into(), bank_account_id: None },
        )
        .unwrap();
        let txn = add_manual(&mut c, 1, &ManualEntry { account_id: 1, txn_date: "2026-10-18".into(), narration: "Small".into(), reference: "".into(), direction: "credit".into(), amount: "100".into() }).unwrap();
        let rid = unlinked_receipts(&c).unwrap()[0].id;
        assert!(link_receipts(&mut c, 1, txn, &[rid]).unwrap_err().contains("more than"));
        assert_eq!(unlinked_receipts(&c).unwrap().len(), 1, "failed link leaves no trace");
        let _ = std::fs::remove_dir_all(dir);
    }
    #[test]
    fn unmatch_reverses_allocations_but_keeps_manual_receipts() {
        let (mut c, ids, dir) = setup();
        import(&mut c, 1, 1, "oct.csv", CSV, &mapping()).unwrap();
        let txn: i64 = c.query_row("SELECT id FROM bank_transactions WHERE reference='UTR111'", [], |r| r.get(0)).unwrap();
        let ok = [Allocation { invoice_id: ids[0], amount: "108000".into(), tds: "10000".into(), tds_section: "194J".into() }];
        reconcile(&mut c, 1, txn, &ok).unwrap();
        assert_eq!(crate::receipts::balance(&c, ids[0]).unwrap().balance_paise, 0);
        assert!(unmatch(&mut c, 1, txn, "").is_err());
        unmatch(&mut c, 1, txn, "Matched to the wrong invoice").unwrap();
        // money and TDS are back to outstanding, the credit is open again, and it can be matched elsewhere
        assert_eq!(crate::receipts::balance(&c, ids[0]).unwrap().balance_paise, 11_800_000);
        assert_eq!(list_txns(&c, None, None).unwrap().into_iter().find(|t| t.id == txn).unwrap().status, "unmatched");
        let again = [Allocation { invoice_id: ids[0], amount: "108000".into(), tds: "10000".into(), tds_section: "194J".into() }];
        reconcile(&mut c, 1, txn, &again).unwrap();
        // a manually recorded receipt that was linked survives an unmatch
        let manual = save_draft(&mut c, &InvoiceInput { id: None, project_id: 1, kind: "tax_invoice".into(), issue_date: "2026-10-01".into(), due_date: "".into(), gst_mode: "none".into(), gst_rate: "".into(), notes: "".into(), lines: vec![LineInput { description: "x".into(), qty: "1".into(), rate: "1000".into() }] }).unwrap();
        issue(&mut c, manual).unwrap();
        crate::receipts::record(&mut c, 1, &ReceiptInput { invoice_id: manual, received_date: "2026-10-18".into(), amount: "1000".into(), tds: "".into(), tds_section: "".into(), mode: "bank".into(), reference: "".into(), notes: "".into(), bank_account_id: None }).unwrap();
        let t2 = add_manual(&mut c, 1, &ManualEntry { account_id: 1, txn_date: "2026-10-18".into(), narration: "n".into(), reference: "".into(), direction: "credit".into(), amount: "1000".into() }).unwrap();
        let rid = unlinked_receipts(&c).unwrap()[0].id;
        link_receipts(&mut c, 1, t2, &[rid]).unwrap();
        unmatch(&mut c, 1, t2, "Linked by mistake").unwrap();
        assert_eq!(crate::receipts::balance(&c, manual).unwrap().received_paise, 100_000, "the manual receipt still counts");
        assert_eq!(unlinked_receipts(&c).unwrap().len(), 1);
        let _ = std::fs::remove_dir_all(dir);
    }
}
