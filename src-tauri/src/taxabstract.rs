//! The GST and TDS abstract for a period, with the tax payments made in it, and a CSV of the same figures.
//!
//! Every figure comes from the posted journals. For each tax head the abstract shows: what was owed at the start, what was
//! charged in the period, what was paid to the government in the period, and what is owed at the end. A payment that was later
//! reversed is netted out of "paid", not shown as tax charged. TDS the clients deducted is taken from the receipts, by section,
//! because that is what the firm claims credit for. Nothing here posts or changes a record.
use crate::auth::Permission;
use crate::billing::validate_date;
use crate::commands::{require, HostState, INTERNAL};
use rusqlite::{params, Connection};
use serde::Serialize;

/// Amounts are whole paise throughout; the screens and the CSV format them.
#[derive(Serialize, Debug, Clone, PartialEq)]
pub struct HeadRow {
    pub head: String,
    pub opening: i64,
    pub charged: i64,
    pub paid: i64,
    pub closing: i64,
}

#[derive(Serialize, Debug, Clone, PartialEq)]
pub struct TdsSection {
    pub section: String,
    pub receipts: i64,
    pub received: i64,
    pub deducted: i64,
}

#[derive(Serialize, Debug, Clone, PartialEq)]
pub struct PaymentLine {
    pub date: String,
    pub journal: String,
    pub head: String,
    pub amount: i64,
    pub via: String,
    pub narration: String,
    pub reversed: bool,
}

#[derive(Serialize, Debug)]
pub struct TaxAbstract {
    pub from: String,
    pub to: String,
    /// CGST, SGST, IGST and composition tax, only those with any figure in or before the period.
    pub gst: Vec<HeadRow>,
    pub gst_total: HeadRow,
    /// Input tax credit: `charged` is credit added, `paid` is credit used, `opening` and `closing` are the balance available.
    pub input_credit: HeadRow,
    /// TDS the clients deducted from the firm's receipts, by section.
    pub tds_by_clients: Vec<TdsSection>,
    pub tds_by_clients_total: TdsSection,
    /// TDS the firm deducted and owes to the government.
    pub tds_payable: HeadRow,
    /// Income tax, provident fund, ESI and professional tax, when they have any figure.
    pub other: Vec<HeadRow>,
    pub payments: Vec<PaymentLine>,
}

/// A journal that is a tax payment, or the reversal of one.
const IS_PAYMENT: &str = "(j.source_type = 'tax_payment' OR (j.source_type = 'reversal' AND EXISTS (SELECT 1 FROM journals o WHERE o.id = j.source_id AND o.source_type = 'tax_payment')))";

/// Opening, charged and paid for one ledger account. `liability` flips the sign for an asset such as input credit.
fn flows(conn: &Connection, code: &str, from: &str, to: &str, liability: bool) -> Result<(i64, i64, i64), String> {
    let id = crate::ledger::acct(conn, code)?;
    let sign = if liability { "l.credit_paise - l.debit_paise" } else { "l.debit_paise - l.credit_paise" };
    let sql = format!(
        "SELECT COALESCE(SUM(CASE WHEN j.date < ?2 THEN {sign} END), 0),
                COALESCE(SUM(CASE WHEN j.date BETWEEN ?2 AND ?3 AND NOT {IS_PAYMENT} THEN {sign} END), 0),
                COALESCE(SUM(CASE WHEN j.date BETWEEN ?2 AND ?3 AND {IS_PAYMENT} THEN -({sign}) END), 0)
         FROM journal_lines l JOIN journals j ON j.id = l.journal_id WHERE l.account_id = ?1"
    );
    conn.query_row(&sql, params![id, from, to], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?))).map_err(|e| e.to_string())
}

fn head(conn: &Connection, label: &str, code: &str, from: &str, to: &str, liability: bool) -> Result<HeadRow, String> {
    let (opening, charged, paid) = flows(conn, code, from, to, liability)?;
    Ok(HeadRow { head: label.to_string(), opening, charged, paid, closing: opening + charged - paid })
}

fn active(h: &HeadRow) -> bool {
    h.opening != 0 || h.charged != 0 || h.paid != 0 || h.closing != 0
}

fn sum(label: &str, rows: &[HeadRow]) -> HeadRow {
    HeadRow {
        head: label.to_string(),
        opening: rows.iter().map(|r| r.opening).sum(),
        charged: rows.iter().map(|r| r.charged).sum(),
        paid: rows.iter().map(|r| r.paid).sum(),
        closing: rows.iter().map(|r| r.closing).sum(),
    }
}

pub fn tax_abstract(conn: &Connection, from: &str, to: &str) -> Result<TaxAbstract, String> {
    validate_date(from)?;
    validate_date(to)?;
    if from > to {
        return Err("The start date is after the end date.".into());
    }
    let gst: Vec<HeadRow> = [("CGST", "2100"), ("SGST", "2110"), ("IGST", "2120"), ("Composition tax", "2130")]
        .iter()
        .map(|(label, code)| head(conn, label, code, from, to, true))
        .collect::<Result<Vec<_>, _>>()?
        .into_iter()
        .filter(active)
        .collect();
    let gst_total = sum("Total GST", &gst);
    let input_credit = head(conn, "Input tax credit", "1220", from, to, false)?;
    let tds_payable = head(conn, "TDS deducted by the firm", "2200", from, to, true)?;
    let other: Vec<HeadRow> = [("Income tax", "2500"), ("Provident fund", "2310"), ("ESI", "2320"), ("Professional tax", "2330")]
        .iter()
        .map(|(label, code)| head(conn, label, code, from, to, true))
        .collect::<Result<Vec<_>, _>>()?
        .into_iter()
        .filter(active)
        .collect();

    // TDS the clients deducted, by section, from receipts that were not reversed
    let tds_by_clients: Vec<TdsSection> = {
        let mut stmt = conn
            .prepare(
                "SELECT CASE WHEN r.tds_section = '' THEN 'Section not stated' ELSE r.tds_section END, COUNT(*), SUM(r.amount_paise), SUM(r.tds_paise)
                 FROM effective_receipts r WHERE r.tds_paise > 0 AND r.received_date BETWEEN ?1 AND ?2 GROUP BY 1 ORDER BY 1",
            )
            .map_err(|e| e.to_string())?;
        let rows = stmt.query_map(params![from, to], |r| Ok(TdsSection { section: r.get(0)?, receipts: r.get(1)?, received: r.get(2)?, deducted: r.get(3)? })).map_err(|e| e.to_string())?;
        rows.collect::<Result<_, _>>().map_err(|e| e.to_string())?
    };
    let tds_by_clients_total = TdsSection {
        section: "Total".into(),
        receipts: tds_by_clients.iter().map(|t| t.receipts).sum(),
        received: tds_by_clients.iter().map(|t| t.received).sum(),
        deducted: tds_by_clients.iter().map(|t| t.deducted).sum(),
    };

    // the payments themselves, with the account they left from
    let payments: Vec<PaymentLine> = {
        let mut stmt = conn
            .prepare(
                "SELECT j.date, j.number, a.name, l.debit_paise, COALESCE(j.narration,''),
                        COALESCE((SELECT a2.name FROM journal_lines l2 JOIN accounts a2 ON a2.id = l2.account_id WHERE l2.journal_id = j.id AND l2.credit_paise > 0 LIMIT 1), ''),
                        EXISTS (SELECT 1 FROM journals rv WHERE rv.source_type = 'reversal' AND rv.source_id = j.id)
                 FROM journals j JOIN journal_lines l ON l.journal_id = j.id AND l.debit_paise > 0 JOIN accounts a ON a.id = l.account_id
                 WHERE j.source_type = 'tax_payment' AND j.date BETWEEN ?1 AND ?2 ORDER BY j.date, j.id",
            )
            .map_err(|e| e.to_string())?;
        let rows = stmt
            .query_map(params![from, to], |r| {
                Ok(PaymentLine { date: r.get(0)?, journal: r.get(1)?, head: r.get(2)?, amount: r.get(3)?, narration: r.get(4)?, via: r.get(5)?, reversed: r.get::<_, i64>(6)? != 0 })
            })
            .map_err(|e| e.to_string())?;
        rows.collect::<Result<_, _>>().map_err(|e| e.to_string())?
    };

    Ok(TaxAbstract { from: from.to_string(), to: to.to_string(), gst, gst_total, input_credit, tds_by_clients, tds_by_clients_total, tds_payable, other, payments })
}

/// 1234567 paise as "12345.67": plain digits a spreadsheet reads as a number, with a minus sign for a negative figure.
fn plain(paise: i64) -> String {
    let sign = if paise < 0 { "-" } else { "" };
    let a = paise.unsigned_abs();
    format!("{sign}{}.{:02}", a / 100, a % 100)
}

/// A text cell that a spreadsheet could take for a formula is prefixed with an apostrophe.
fn text(value: &str) -> String {
    match value.chars().next() {
        Some('=' | '+' | '-' | '@' | '\t' | '\r') => format!("'{value}"),
        _ => value.to_string(),
    }
}

pub fn tax_abstract_csv(conn: &Connection, from: &str, to: &str) -> Result<String, String> {
    let a = tax_abstract(conn, from, to)?;
    let firm: String = conn.query_row("SELECT name FROM company_profile WHERE id = 1", [], |r| r.get(0)).unwrap_or_default();
    let mut w = csv::WriterBuilder::new().flexible(true).from_writer(Vec::new());
    let mut row = |cells: Vec<String>| w.write_record(cells).map_err(|e| e.to_string());
    let heads = |rows: &[HeadRow], first: &str, labels: [&str; 4]| -> Vec<Vec<String>> {
        let mut out = vec![vec![first.to_string(), labels[0].into(), labels[1].into(), labels[2].into(), labels[3].into()]];
        for r in rows {
            out.push(vec![text(&r.head), plain(r.opening), plain(r.charged), plain(r.paid), plain(r.closing)]);
        }
        out
    };

    row(vec!["Tax abstract".into(), text(&firm)])?;
    row(vec!["Period".into(), a.from.clone(), a.to.clone()])?;
    row(vec!["Amounts in rupees".into()])?;
    row(vec![])?;
    row(vec!["GST".into()])?;
    let mut gst = a.gst.clone();
    gst.push(a.gst_total.clone());
    for r in heads(&gst, "Head", ["Payable at start", "Charged in period", "Paid in period", "Payable at end"]) {
        row(r)?;
    }
    row(vec![])?;
    for r in heads(std::slice::from_ref(&a.input_credit), "Input tax credit", ["Available at start", "Added in period", "Used in period", "Available at end"]) {
        row(r)?;
    }
    row(vec![])?;
    row(vec!["TDS deducted by clients (credit to claim)".into()])?;
    row(vec!["Section".into(), "Receipts".into(), "Received excluding TDS".into(), "TDS deducted".into()])?;
    for t in a.tds_by_clients.iter().chain(std::iter::once(&a.tds_by_clients_total)) {
        row(vec![text(&t.section), t.receipts.to_string(), plain(t.received), plain(t.deducted)])?;
    }
    row(vec![])?;
    row(vec!["TDS payable by the firm".into()])?;
    for r in heads(std::slice::from_ref(&a.tds_payable), "Head", ["Payable at start", "Deducted in period", "Paid in period", "Payable at end"]) {
        row(r)?;
    }
    if !a.other.is_empty() {
        row(vec![])?;
        row(vec!["Other statutory dues".into()])?;
        for r in heads(&a.other, "Head", ["Payable at start", "Charged in period", "Paid in period", "Payable at end"]) {
            row(r)?;
        }
    }
    row(vec![])?;
    row(vec!["Tax payments".into()])?;
    row(vec!["Date".into(), "Journal".into(), "Tax".into(), "Amount".into(), "Paid via".into(), "Narration".into(), "Reversed".into()])?;
    for p in &a.payments {
        row(vec![p.date.clone(), text(&p.journal), text(&p.head), plain(p.amount), text(&p.via), text(&p.narration), if p.reversed { "Yes".into() } else { "No".into() }])?;
    }
    let bytes = w.into_inner().map_err(|e| e.to_string())?;
    String::from_utf8(bytes).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn report_tax_abstract(state: HostState<'_>, from: String, to: String) -> Result<TaxAbstract, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    tax_abstract(&conn, &from, &to)
}

#[tauri::command]
pub fn report_tax_abstract_csv(state: HostState<'_>, from: String, to: String) -> Result<String, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    tax_abstract_csv(&conn, &from, &to)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ledger::{acct, cr, dr, post, reverse_journal};

    fn setup() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('o','x','owner')", []).unwrap();
        c.execute("UPDATE company_profile SET name = '=Studio, X'", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1, 'Villa')", []).unwrap();
        c
    }

    /// Output GST on a sale of 10,00,000 taxable at 18% intra-state, posted on `date`.
    fn sale(c: &Connection, date: &str, taxable: i64) {
        let tax = taxable * 9 / 100;
        post(c, Some(1), date, "Sale", "invoice", None, vec![dr(acct(c, "1200").unwrap(), taxable + 2 * tax), cr(acct(c, "4000").unwrap(), taxable), cr(acct(c, "2100").unwrap(), tax), cr(acct(c, "2110").unwrap(), tax)]).unwrap();
    }

    #[test]
    fn opening_plus_charged_less_paid_is_always_closing_and_payments_are_listed() {
        let mut c = setup();
        sale(&c, "2026-03-20", 1_000_000_00); // before the period: becomes the opening balance
        sale(&c, "2026-04-10", 2_000_000_00);
        let j = crate::entries::tax_payment(&mut c, 1, "cgst", "2026-04-20", "90000", "bank", "March CGST").unwrap();
        crate::entries::tax_payment(&mut c, 1, "sgst", "2026-04-20", "90000", "bank", "March SGST").unwrap();
        let a = tax_abstract(&c, "2026-04-01", "2026-04-30").unwrap();
        let cgst = a.gst.iter().find(|h| h.head == "CGST").unwrap();
        assert_eq!(cgst.opening, 9_000_000, "March's CGST is owed at the start");
        assert_eq!(cgst.charged, 18_000_000, "April's sales charged more");
        assert_eq!(cgst.paid, 9_000_000);
        assert_eq!(cgst.closing, cgst.opening + cgst.charged - cgst.paid);
        assert_eq!(cgst.closing, 18_000_000);
        assert!(a.gst.iter().all(|h| h.closing == h.opening + h.charged - h.paid));
        assert_eq!(a.gst_total.paid, 18_000_000);
        assert!(a.gst.iter().all(|h| h.head != "IGST"), "a head with no figure is left out");
        assert_eq!(a.payments.len(), 2);
        assert_eq!((a.payments[0].head.as_str(), a.payments[0].amount, a.payments[0].reversed), ("GST output - CGST", 9_000_000, false));
        assert!(!a.payments[0].via.is_empty() && a.payments[0].journal.len() > 2);
        // a payment that is later reversed is netted out of "paid" and flagged, never shown as tax charged
        reverse_journal(&c, Some(1), j, "paid the wrong head").unwrap();
        // (a reversal is posted on the day it is made, so the period must run to the end of the books)
        let b = tax_abstract(&c, "2026-04-01", "2099-12-31").unwrap();
        let cgst = b.gst.iter().find(|h| h.head == "CGST").unwrap();
        assert_eq!(cgst.charged, 18_000_000, "the reversal did not increase what was charged");
        assert_eq!(cgst.paid, 0);
        assert_eq!(cgst.closing, 27_000_000);
        assert!(b.payments.iter().any(|p| p.reversed));
        // the closing balance equals the ledger balance at the end date
        let ledger = crate::ledger::tax_summary(&c, "1990-01-01", "2099-12-31").unwrap();
        assert_eq!(crate::billing::fmt_inr(cgst.closing), ledger.output_cgst);
    }

    #[test]
    fn tds_deducted_by_clients_is_grouped_by_section_and_excludes_reversed_receipts() {
        let c = setup();
        c.execute("INSERT INTO invoices (project_id, kind, status, number, issue_date, gst_mode, subtotal_paise, tax_paise, total_paise) VALUES (1, 'tax_invoice', 'issued', 'INV-1', '2026-04-01', 'none', 100000, 0, 100000)", []).unwrap();
        for (n, section, tds, date) in [("R-1", "194J", 10_000, "2026-04-05"), ("R-2", "194J", 5_000, "2026-04-20"), ("R-3", "", 2_000, "2026-04-25"), ("R-4", "194C", 1_000, "2026-05-02")] {
            c.execute("INSERT INTO receipts (number, invoice_id, received_date, amount_paise, tds_paise, tds_section, mode, created_by) VALUES (?1, 1, ?2, 20000, ?3, ?4, 'bank', 1)", params![n, date, tds, section]).unwrap();
        }
        let a = tax_abstract(&c, "2026-04-01", "2026-04-30").unwrap();
        assert_eq!(a.tds_by_clients.len(), 2);
        let j = a.tds_by_clients.iter().find(|t| t.section == "194J").unwrap();
        assert_eq!((j.receipts, j.deducted, j.received), (2, 15_000, 40_000));
        assert!(a.tds_by_clients.iter().any(|t| t.section == "Section not stated" && t.deducted == 2_000));
        assert_eq!(a.tds_by_clients_total.deducted, 17_000, "May's receipt is outside the period");
        assert_eq!(a.tds_by_clients_total.receipts, 3);
    }

    #[test]
    fn tds_payable_by_the_firm_tracks_deduction_and_payment() {
        let mut c = setup();
        post(&c, Some(1), "2026-04-10", "TDS on fees", "manual", None, vec![dr(acct(&c, "5000").unwrap_or_else(|_| acct(&c, "4000").unwrap()), 50_000), cr(acct(&c, "2200").unwrap(), 50_000)]).unwrap();
        crate::entries::tax_payment(&mut c, 1, "tds", "2026-05-07", "300", "bank", "").unwrap();
        let a = tax_abstract(&c, "2026-04-01", "2026-05-31").unwrap();
        assert_eq!((a.tds_payable.opening, a.tds_payable.charged, a.tds_payable.paid, a.tds_payable.closing), (0, 50_000, 30_000, 20_000));
        let may = tax_abstract(&c, "2026-05-01", "2026-05-31").unwrap();
        assert_eq!((may.tds_payable.opening, may.tds_payable.paid, may.tds_payable.closing), (50_000, 30_000, 20_000));
    }

    #[test]
    fn the_csv_is_plain_numbers_safe_text_and_covers_every_section() {
        let mut c = setup();
        sale(&c, "2026-04-10", 100_000_00);
        crate::entries::tax_payment(&mut c, 1, "cgst", "2026-04-20", "9000", "bank", "=cmd|' /C calc'!A0").unwrap();
        let csv = tax_abstract_csv(&c, "2026-04-01", "2026-04-30").unwrap();
        let mut rd = csv::ReaderBuilder::new().has_headers(false).flexible(true).from_reader(csv.as_bytes());
        let rows: Vec<Vec<String>> = rd.records().map(|r| r.unwrap().iter().map(String::from).collect()).collect();
        let find = |label: &str| rows.iter().find(|r| r.first().map(String::as_str) == Some(label)).unwrap_or_else(|| panic!("no row {label}: {csv}"));
        assert_eq!(find("Tax abstract")[1], "'=Studio, X", "a firm name that looks like a formula is neutralised and its comma kept");
        assert_eq!(find("Period")[1..], ["2026-04-01".to_string(), "2026-04-30".to_string()]);
        let cgst = find("CGST");
        assert_eq!(cgst[1..], ["0.00", "9000.00", "9000.00", "0.00"].map(String::from), "paid in full this month: nothing left");
        for label in ["GST", "Total GST", "Input tax credit", "TDS deducted by clients (credit to claim)", "TDS payable by the firm", "Tax payments"] {
            find(label);
        }
        assert!(rows.iter().any(|r| r.get(5).map(|c| c.starts_with("'=cmd")).unwrap_or(false)), "narration beginning with = is neutralised");
        assert!(!csv.contains('₹') && !csv.contains(",000"), "amounts are plain numbers without symbols or digit grouping");
        assert!(tax_abstract_csv(&c, "2026-05-01", "2026-04-01").is_err());
    }

    #[test]
    fn dates_are_checked() {
        let c = setup();
        assert!(tax_abstract(&c, "2026-13-01", "2026-04-30").is_err());
        assert!(tax_abstract(&c, "2026-04-30", "2026-04-01").is_err());
        let empty = tax_abstract(&c, "2026-04-01", "2026-04-30").unwrap();
        assert!(empty.gst.is_empty() && empty.payments.is_empty() && empty.gst_total.closing == 0);
    }
}
