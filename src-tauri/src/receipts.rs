use crate::auth::Permission;
use crate::billing::{allocate_number, fmt_inr, parse_decimal};
use crate::commands::{require, AppState, INTERNAL};
use crate::db;
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};
use tauri::State;

#[derive(Serialize, Deserialize)]
pub struct ReceiptInput {
    pub invoice_id: i64,
    pub received_date: String,
    /// Cash actually received in the bank / by cheque / UPI, in rupees.
    pub amount: String,
    /// Tax the client deducted at source and will deposit on the firm's behalf, in rupees.
    pub tds: String,
    pub tds_section: String,
    pub mode: String,
    pub reference: String,
    pub notes: String,
    /// Which bank account the money reached (bank / UPI / cheque). Defaults to the primary account.
    #[serde(default)]
    pub bank_account_id: Option<i64>,
}

#[derive(Serialize)]
pub struct ReceiptRow {
    pub id: i64,
    pub reversed: bool,
    pub number: String,
    pub received_date: String,
    pub amount_display: String,
    pub tds_display: String,
    pub tds_section: String,
    pub mode: String,
    pub reference: String,
}

#[derive(Serialize)]
pub struct Balance {
    pub total_paise: i64,
    pub received_paise: i64,
    pub tds_paise: i64,
    pub credited_paise: i64,
    pub balance_paise: i64,
    pub credited_display: String,
    pub total_display: String,
    pub received_display: String,
    pub tds_display: String,
    pub balance_display: String,
}

fn optional_money(s: &str) -> Result<i64, String> {
    if s.trim().is_empty() { Ok(0) } else { parse_decimal(s, 2) }
}

pub fn balance(conn: &Connection, invoice_id: i64) -> Result<Balance, String> {
    let (total, received, tds, credited): (i64, i64, i64, i64) = conn
        .query_row(
            "SELECT total_paise, received_paise, tds_paise, credited_paise FROM invoice_balances WHERE invoice_id = ?1",
            params![invoice_id],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)),
        )
        .map_err(|_| "Invoice not found.".to_string())?;
    let bal = total - received - tds - credited;
    Ok(Balance {
        total_paise: total, received_paise: received, tds_paise: tds, credited_paise: credited, balance_paise: bal,
        total_display: fmt_inr(total), received_display: fmt_inr(received), tds_display: fmt_inr(tds), credited_display: fmt_inr(credited),
        balance_display: fmt_inr(bal),
    })
}

/// Suggested TDS = rate% of the taxable value (the subtotal, excluding GST), capped at the balance.
pub fn suggest_tds(conn: &Connection, invoice_id: i64, rate: &str) -> Result<String, String> {
    let bp = parse_decimal(rate, 2).map_err(|e| format!("TDS rate. {e}"))?;
    if bp > 10_000 {
        return Err("TDS rate cannot exceed 100%.".into());
    }
    let subtotal: i64 = conn
        .query_row("SELECT subtotal_paise FROM invoices WHERE id = ?1", params![invoice_id], |r| r.get(0))
        .map_err(|_| "Invoice not found.".to_string())?;
    let b = balance(conn, invoice_id)?;
    let tds = ((subtotal * bp + 5_000) / 10_000).min(b.balance_paise.max(0));
    Ok(format!("{}.{:02}", tds / 100, tds % 100))
}

/// Validates and inserts a receipt inside an open transaction. `check_reference` guards manual entry against
/// a UTR being recorded twice; bank reconciliation passes false because one bank credit may be split across
/// several invoices (the bank transaction itself is what prevents double use).
pub(crate) fn insert_receipt(tx: &Connection, user_id: i64, input: &ReceiptInput, check_reference: bool) -> Result<String, String> {
    let amount = optional_money(&input.amount).map_err(|e| format!("Amount received. {e}"))?;
    let tds = optional_money(&input.tds).map_err(|e| format!("TDS. {e}"))?;
    if amount + tds == 0 {
        return Err("Enter the amount received and/or the TDS deducted.".into());
    }
    if tds > 0 && input.tds_section.trim().is_empty() {
        return Err("Choose the TDS section.".into());
    }
    if !["bank", "upi", "cheque", "cash"].contains(&input.mode.as_str()) {
        return Err("Invalid payment mode.".into());
    }
    crate::billing::validate_date(&input.received_date)?;
    let reference = input.reference.trim();

    let (status, kind): (String, String) = tx
        .query_row("SELECT status, kind FROM invoices WHERE id = ?1", params![input.invoice_id], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|_| "Invoice not found.".to_string())?;
    if status != "issued" {
        return Err("Payments can only be recorded against issued invoices.".into());
    }
    if kind != "tax_invoice" {
        return Err("Payments are recorded against invoices, not pro forma invoices.".into());
    }
    let b = balance(tx, input.invoice_id)?;
    if amount + tds > b.balance_paise {
        return Err(format!("This exceeds the outstanding balance of {}.", b.balance_display));
    }
    if check_reference && !reference.is_empty() {
        let dup: i64 = tx
            .query_row("SELECT COUNT(*) FROM effective_receipts WHERE reference = ?1", params![reference], |r| r.get(0))
            .map_err(|e| e.to_string())?;
        if dup > 0 {
            return Err("A payment with this reference has already been recorded.".into());
        }
    }
    let number = allocate_number(tx, "receipt", &input.received_date)?;
    tx.execute(
        "INSERT INTO receipts (number, invoice_id, received_date, amount_paise, tds_paise, tds_section, mode, reference, notes, created_by, bank_account_id)
         VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11)",
        params![number, input.invoice_id, input.received_date, amount, tds, input.tds_section.trim(), input.mode, reference, input.notes.trim(), user_id, input.bank_account_id],
    )
    .map_err(|e| e.to_string())?;
    let receipt_id = tx.last_insert_rowid();
    crate::ledger::post_receipt(tx, Some(user_id), receipt_id)?;
    db::audit(tx, Some(user_id), "receipt_recorded", &format!("{number} on invoice {}", input.invoice_id));
    Ok(number)
}

pub fn record(conn: &mut Connection, user_id: i64, input: &ReceiptInput) -> Result<String, String> {
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let number = insert_receipt(&tx, user_id, input, true)?;
    tx.commit().map_err(|e| e.to_string())?;
    Ok(number)
}

pub fn list(conn: &Connection, invoice_id: i64) -> Result<Vec<ReceiptRow>, String> {
    let mut stmt = conn
        .prepare("SELECT number, received_date, amount_paise, tds_paise, tds_section, mode, reference, id, EXISTS (SELECT 1 FROM receipt_reversals v WHERE v.receipt_id = receipts.id) FROM receipts WHERE invoice_id = ?1 ORDER BY id")
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![invoice_id], |r| {
            Ok(ReceiptRow {
                number: r.get(0)?, received_date: r.get(1)?, amount_display: fmt_inr(r.get(2)?), tds_display: fmt_inr(r.get(3)?),
                tds_section: r.get(4)?, mode: r.get(5)?, reference: r.get(6)?, id: r.get(7)?, reversed: r.get::<_, i64>(8)? != 0,
            })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

/// Marks a receipt as no longer effective (wrong amount, bounced cheque...). The receipt itself is kept.
pub fn reverse(conn: &mut Connection, actor: i64, receipt_id: i64, reason: &str) -> Result<(), String> {
    if reason.trim().is_empty() {
        return Err("Give a reason for the reversal.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let number: String = tx.query_row("SELECT number FROM receipts WHERE id = ?1", params![receipt_id], |r| r.get(0)).map_err(|_| "Receipt not found.".to_string())?;
    let already: i64 = tx.query_row("SELECT COUNT(*) FROM receipt_reversals WHERE receipt_id = ?1", params![receipt_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if already > 0 {
        return Err("This receipt has already been reversed.".into());
    }
    let linked: i64 = tx.query_row("SELECT COUNT(*) FROM bank_txn_receipts WHERE receipt_id = ?1", params![receipt_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if linked > 0 {
        return Err("This receipt is matched to a bank transaction. Undo the bank match first.".into());
    }
    tx.execute("INSERT INTO receipt_reversals (receipt_id, reason, reversed_by) VALUES (?1,?2,?3)", params![receipt_id, reason.trim(), actor]).map_err(|e| e.to_string())?;
    crate::ledger::reverse_source(&tx, Some(actor), "receipt", receipt_id, reason)?;
    db::audit(&tx, Some(actor), "receipt_reversed", &format!("{number}: {}", reason.trim()));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(())
}

fn receipt_html(conn: &Connection, receipt_id: i64) -> Result<String, String> {
    use crate::billing::esc;
    let (number, date, amount, tds, section, mode, reference, inv_no, client): (String, String, i64, i64, String, String, String, String, String) = conn
        .query_row(
            "SELECT r.number, r.received_date, r.amount_paise, r.tds_paise, r.tds_section, r.mode, r.reference, COALESCE(i.number,''), c.name
             FROM receipts r JOIN invoices i ON i.id = r.invoice_id JOIN projects p ON p.id = i.project_id JOIN clients c ON c.id = p.client_id WHERE r.id = ?1",
            params![receipt_id],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?, r.get(6)?, r.get(7)?, r.get(8)?)),
        )
        .map_err(|_| "Receipt not found.".to_string())?;
    let (name, address, pan, gstin): (String, String, String, String) = conn
        .query_row("SELECT name, address, pan, gstin FROM company_profile WHERE id = 1", [], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)))
        .map_err(|e| e.to_string())?;
    let tds_row = if tds > 0 { format!("<tr><td>TDS deducted by payer ({})</td><td>{}</td></tr>", esc(&section), fmt_inr(tds)) } else { String::new() };
    let ref_line = if reference.is_empty() { String::new() } else { format!("<br/>Reference / UTR: {}", esc(&reference)) };
    let gst_line = if gstin.is_empty() { String::new() } else { format!("GSTIN: {} &nbsp; ", esc(&gstin)) };
    Ok(format!(
        r#"<html><head><style>
        body {{ font-family: DejaVu; font-size: 10px; color: #161616; }}
        h1 {{ font-size: 18px; margin-bottom: 2px; }}
        h2 {{ font-size: 14px; margin-bottom: 2px; }}
        table {{ width: 100%; border-collapse: collapse; margin-top: 8px; }}
        th, td {{ border: 1px solid #8d8d8d; padding: 4px; }}
        th {{ background-color: #e0e0e0; }}
        </style></head><body>
        <h1>{name}</h1>
        <p>{address}<br/>{gst_line}PAN: {pan}</p>
        <h2>PAYMENT RECEIPT</h2>
        <p>Receipt no.: {number}<br/>Date: {date}</p>
        <p>Received with thanks from <strong>{client}</strong> against invoice {inv_no}.<br/>Mode: {mode}{ref_line}</p>
        <table><tr><td>Amount received</td><td>{amount}</td></tr>{tds_row}<tr><th>Total settled against the invoice</th><th>{total}</th></tr></table>
        <p>This is a receipt for the payment above and not a tax invoice.</p>
        </body></html>"#,
        name = esc(&name), address = esc(&address), gst_line = gst_line, pan = esc(&pan), number = esc(&number), date = esc(&date),
        client = esc(&client), inv_no = esc(&inv_no), mode = esc(&mode), ref_line = ref_line, amount = fmt_inr(amount), tds_row = tds_row, total = fmt_inr(amount + tds),
    ))
}

/// The receipt PDF is rendered the first time it is requested and then frozen (stored with its hash).
pub fn receipt_pdf_bytes(conn: &Connection, files_root: &std::path::Path, receipt_id: i64) -> Result<Vec<u8>, String> {
    use crate::billing::sha2_lite::sha256_hex;
    let stored: Option<(String, String)> = conn
        .query_row("SELECT pdf_key, pdf_sha256 FROM receipt_pdfs WHERE receipt_id = ?1", params![receipt_id], |r| Ok((r.get(0)?, r.get(1)?)))
        .ok();
    if let Some((key, sha)) = stored {
        let bytes = std::fs::read(files_root.join(key)).map_err(|_| "The stored PDF could not be read.".to_string())?;
        if sha256_hex(&bytes) != sha {
            return Err("The stored PDF failed its integrity check.".into());
        }
        return Ok(bytes);
    }
    let number: String = conn.query_row("SELECT number FROM receipts WHERE id = ?1", params![receipt_id], |r| r.get(0)).map_err(|_| "Receipt not found.".to_string())?;
    let rendered = crate::pdf::render_html(&receipt_html(conn, receipt_id)?, &crate::pdf::bundled_fonts())?;
    let key = format!("receipt-pdfs/{}.pdf", number.replace('/', "_"));
    let path = files_root.join(&key);
    std::fs::create_dir_all(path.parent().unwrap()).map_err(|e| e.to_string())?;
    if path.exists() {
        return Err("A PDF for this receipt already exists; refusing to overwrite.".into());
    }
    std::fs::write(&path, &rendered.bytes).map_err(|e| e.to_string())?;
    conn.execute("INSERT INTO receipt_pdfs (receipt_id, pdf_key, pdf_sha256) VALUES (?1,?2,?3)", params![receipt_id, key, sha256_hex(&rendered.bytes)]).map_err(|e| e.to_string())?;
    Ok(rendered.bytes)
}

#[tauri::command]
pub fn receipt_reverse(state: State<AppState>, receipt_id: i64, reason: String) -> Result<(), String> {
    let uid = require(&state, Permission::Issue)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    reverse(&mut conn, uid, receipt_id, &reason)
}

#[tauri::command]
pub fn receipt_pdf(state: State<AppState>, receipt_id: i64) -> Result<String, String> {
    use base64::Engine;
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    Ok(base64::engine::general_purpose::STANDARD.encode(receipt_pdf_bytes(&conn, &state.files_root, receipt_id)?))
}

#[tauri::command]
pub fn receipt_balance(state: State<AppState>, invoice_id: i64) -> Result<Balance, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    balance(&conn, invoice_id)
}

#[tauri::command]
pub fn receipt_list(state: State<AppState>, invoice_id: i64) -> Result<Vec<ReceiptRow>, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list(&conn, invoice_id)
}

#[tauri::command]
pub fn receipt_suggest_tds(state: State<AppState>, invoice_id: i64, rate: String) -> Result<String, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    suggest_tds(&conn, invoice_id, &rate)
}

#[tauri::command]
pub fn receipt_record(state: State<AppState>, input: ReceiptInput) -> Result<String, String> {
    let uid = require(&state, Permission::Issue)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    record(&mut conn, uid, &input)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::billing::{issue, save_draft, InvoiceInput, LineInput};

    /// A DB with one issued invoice of Rs 1,18,000 (1,00,000 + 18% GST) and a user.
    fn setup() -> (Connection, i64, std::path::PathBuf) {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('o','x','owner')", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1,'Villa')", []).unwrap();
        c.execute("UPDATE company_profile SET gst_scheme='regular'", []).unwrap();
        let dir = std::env::temp_dir().join(format!("ao_rc_{}_{}", std::process::id(), rand_suffix()));
        let input = InvoiceInput {
            id: None, project_id: 1, kind: "tax_invoice".into(), issue_date: "2026-10-12".into(), due_date: "".into(),
            gst_mode: "intra".into(), gst_rate: "".into(), notes: "".into(),
            lines: vec![LineInput { description: "Fee".into(), qty: "1".into(), rate: "100000".into() }],
        };
        let id = save_draft(&mut c, &input).unwrap();
        issue(&mut c, &dir, id).unwrap();
        (c, id, dir)
    }

    fn rand_suffix() -> u128 {
        std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
    }

    fn rc(invoice_id: i64, amount: &str, tds: &str, reference: &str) -> ReceiptInput {
        ReceiptInput {
            invoice_id, received_date: "2026-10-20".into(), amount: amount.into(), tds: tds.into(),
            tds_section: if tds.is_empty() { "".into() } else { "194J".into() }, mode: "bank".into(), reference: reference.into(), notes: "".into(), bank_account_id: None,
        }
    }

    #[test]
    fn tds_reduces_cash_but_settles_invoice() {
        let (mut c, id, dir) = setup();
        // Invoice Rs 1,18,000. Client deducts 10% TDS on Rs 1,00,000 = 10,000 and pays 1,08,000.
        assert_eq!(suggest_tds(&c, id, "10").unwrap(), "10000.00");
        let n = record(&mut c, 1, &rc(id, "108000", "10000", "UTR1")).unwrap();
        assert_eq!(n, "RCT/2026-27/0001");
        let b = balance(&c, id).unwrap();
        assert_eq!((b.received_paise, b.tds_paise, b.balance_paise), (10_800_000, 1_000_000, 0));
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn partial_payments_accumulate_and_overpayment_is_refused() {
        let (mut c, id, dir) = setup();
        record(&mut c, 1, &rc(id, "50000", "", "A")).unwrap();
        record(&mut c, 1, &rc(id, "40000", "", "B")).unwrap();
        assert_eq!(balance(&c, id).unwrap().balance_paise, 2_800_000);
        assert!(record(&mut c, 1, &rc(id, "28000.01", "", "C")).unwrap_err().contains("exceeds"));
        record(&mut c, 1, &rc(id, "28000", "", "D")).unwrap();
        assert_eq!(balance(&c, id).unwrap().balance_paise, 0);
        assert!(record(&mut c, 1, &rc(id, "1", "", "E")).is_err());
        assert_eq!(list(&c, id).unwrap().len(), 3);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn duplicate_reference_and_missing_tds_section_are_refused() {
        let (mut c, id, dir) = setup();
        record(&mut c, 1, &rc(id, "1000", "", "UTR9")).unwrap();
        assert!(record(&mut c, 1, &rc(id, "1000", "", "UTR9")).unwrap_err().contains("already"));
        let mut no_section = rc(id, "1000", "100", "UTR10");
        no_section.tds_section = "".into();
        assert!(record(&mut c, 1, &no_section).unwrap_err().contains("TDS section"));
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn drafts_and_proformas_cannot_be_paid_and_receipts_are_immutable() {
        let (mut c, id, dir) = setup();
        let draft = save_draft(&mut c, &InvoiceInput {
            id: None, project_id: 1, kind: "tax_invoice".into(), issue_date: "2026-10-12".into(), due_date: "".into(),
            gst_mode: "none".into(), gst_rate: "".into(), notes: "".into(),
            lines: vec![LineInput { description: "x".into(), qty: "1".into(), rate: "10".into() }],
        }).unwrap();
        assert!(record(&mut c, 1, &rc(draft, "1", "", "")).is_err());
        record(&mut c, 1, &rc(id, "10", "", "")).unwrap();
        assert!(c.execute("UPDATE receipts SET amount_paise = 1", []).is_err());
        assert!(c.execute("DELETE FROM receipts", []).is_err());
        let _ = std::fs::remove_dir_all(dir);
    }
    #[test]
    fn reversal_restores_balance_and_blocks_reuse() {
        let (mut c, id, dir) = setup();
        record(&mut c, 1, &rc(id, "118000", "", "UTR50")).unwrap();
        assert_eq!(balance(&c, id).unwrap().balance_paise, 0);
        let rid = list(&c, id).unwrap()[0].id;
        assert!(reverse(&mut c, 1, rid, "  ").is_err());
        reverse(&mut c, 1, rid, "Cheque bounced").unwrap();
        assert_eq!(balance(&c, id).unwrap().balance_paise, 11_800_000);
        assert!(reverse(&mut c, 1, rid, "again").unwrap_err().contains("already"));
        // the UTR of a reversed receipt can be recorded again, and the original row is still there
        record(&mut c, 1, &rc(id, "118000", "", "UTR50")).unwrap();
        assert_eq!(list(&c, id).unwrap().len(), 2);
        assert!(list(&c, id).unwrap()[0].reversed);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn receipt_pdf_is_frozen_after_first_render() {
        let (mut c, id, dir) = setup();
        record(&mut c, 1, &rc(id, "1000", "", "")).unwrap();
        let rid = list(&c, id).unwrap()[0].id;
        let a = receipt_pdf_bytes(&c, &dir, rid).unwrap();
        assert!(a.starts_with(b"%PDF"));
        c.execute("UPDATE company_profile SET name = 'Renamed Studio'", []).unwrap();
        assert_eq!(receipt_pdf_bytes(&c, &dir, rid).unwrap(), a);
        let _ = std::fs::remove_dir_all(dir);
    }
}
