//! Tax Deducted at Source as clients' deductors reported it (Form 26AS), checked against the TDS the office booked on receipts.
//!
//! The entries are imported from a CSV saved from the TRACES statement. Nothing here posts to the books: it only shows which TDS
//! the office booked has been reported, and which reported TDS the office has not booked.

use crate::audit;
use crate::check::{date, text};
use rusqlite::{params, Connection};
use serde_json::{json, Value};

/// How many days apart a receipt and the deductor's entry may be and still be the same deduction.
const WINDOW_DAYS: i64 = 120;

/// Splits one CSV line into fields, honouring double quotes and doubled quotes inside them.
fn split_line(line: &str) -> Vec<String> {
    let mut fields = Vec::new();
    let mut current = String::new();
    let mut quoted = false;
    let mut chars = line.chars().peekable();
    while let Some(c) = chars.next() {
        match (c, quoted) {
            ('"', true) if chars.peek() == Some(&'"') => {
                current.push('"');
                chars.next();
            }
            ('"', _) => quoted = !quoted,
            (',', false) => fields.push(std::mem::take(&mut current).trim().to_string()),
            _ => current.push(c),
        }
    }
    fields.push(current.trim().to_string());
    fields
}

fn key(header: &str) -> String {
    header.to_ascii_lowercase().chars().filter(|c| c.is_ascii_alphanumeric()).collect()
}

/// Rupees with up to two decimals, commas and a rupee sign allowed, as paise. No floats.
pub fn parse_paise(text: &str) -> Result<i64, String> {
    let cleaned: String = text.chars().filter(|c| !matches!(c, ',' | '₹' | ' ')).collect();
    let bad = || format!("'{text}' is not an amount.");
    let (whole, frac) = cleaned.split_once('.').unwrap_or((cleaned.as_str(), ""));
    if whole.is_empty() && frac.is_empty() || frac.len() > 2 || !whole.chars().chain(frac.chars()).all(|c| c.is_ascii_digit()) || whole.len() > 13 {
        return Err(bad());
    }
    let whole: i64 = if whole.is_empty() { 0 } else { whole.parse().map_err(|_| bad())? };
    let frac: i64 = format!("{frac:0<2}").parse().map_err(|_| bad())?;
    Ok(whole * 100 + frac)
}

/// yyyy-mm-dd, dd-mm-yyyy, dd/mm/yyyy or dd-Mon-yyyy, as yyyy-mm-dd.
fn parse_day(text: &str) -> Result<String, String> {
    let t = text.trim();
    let parts: Vec<&str> = t.split(['-', '/', ' ']).collect();
    let months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    let iso = match parts.as_slice() {
        [y, m, d] if y.len() == 4 => format!("{y}-{m:0>2}-{d:0>2}"),
        [d, m, y] if y.len() == 4 => {
            let m = match months.iter().position(|name| m.to_ascii_lowercase().starts_with(name)) {
                Some(index) => format!("{:02}", index + 1),
                None => format!("{m:0>2}"),
            };
            format!("{y}-{m}-{d:0>2}")
        }
        _ => return Err(format!("'{text}' is not a date.")),
    };
    date(&iso, true)
}

struct Row {
    deductor: String,
    tan: String,
    section: String,
    day: String,
    amount: i64,
    tds: i64,
}

fn parse(csv: &str) -> Result<Vec<Row>, String> {
    let mut lines = csv.trim_start_matches('\u{feff}').lines().filter(|l| !l.trim().is_empty());
    let header = split_line(lines.next().ok_or("The file is empty.")?);
    let find = |pick: &dyn Fn(&str) -> bool| header.iter().position(|h| pick(&key(h)));
    let tan = find(&|h| h.contains("tan")).ok_or("The file needs a column for the deductor's TAN.")?;
    let deductor = find(&|h| !h.contains("tan") && (h.contains("deductor") || h.contains("name"))).ok_or("The file needs a column for the deductor's name.")?;
    let section = find(&|h| h.contains("section"));
    let day = find(&|h| h.contains("date")).ok_or("The file needs a date column.")?;
    let amount = find(&|h| h.contains("amount")).ok_or("The file needs a column for the amount paid or credited.")?;
    let tds = find(&|h| h.contains("tax") || h.contains("tds")).ok_or("The file needs a column for the tax deducted.")?;
    let mut rows = Vec::new();
    let mut problems = Vec::new();
    for (index, line) in lines.enumerate() {
        let f = split_line(line);
        let line_no = index + 2;
        let cell = |i: usize| f.get(i).map(String::as_str).unwrap_or("");
        let result = (|| -> Result<Row, String> {
            let tan = cell(tan).to_ascii_uppercase();
            if tan.len() != 10 || !tan.chars().all(|c| c.is_ascii_alphanumeric()) {
                return Err(format!("'{}' is not a TAN.", cell(tan_index(&header))));
            }
            let tds_paise = parse_paise(cell(tds))?;
            if tds_paise <= 0 {
                return Err("The tax deducted must be more than zero.".into());
            }
            Ok(Row {
                deductor: text(cell(deductor), 160, "deductor name", true)?,
                tan,
                section: text(section.map(cell).unwrap_or(""), 16, "section", false)?,
                day: parse_day(cell(day))?,
                amount: parse_paise(cell(amount))?,
                tds: tds_paise,
            })
        })();
        match result {
            Ok(row) => rows.push(row),
            Err(why) => problems.push(format!("Line {line_no}: {why}")),
        }
    }
    if !problems.is_empty() {
        let more = if problems.len() > 5 { format!(" ({} more)", problems.len() - 5) } else { String::new() };
        return Err(format!("{}{more} Nothing was imported.", problems.into_iter().take(5).collect::<Vec<_>>().join(" ")));
    }
    if rows.is_empty() {
        return Err("The file has no entries.".into());
    }
    if rows.len() > 20_000 {
        return Err("The file has too many entries.".into());
    }
    Ok(rows)
}

fn tan_index(header: &[String]) -> usize {
    header.iter().position(|h| key(h).contains("tan")).unwrap_or(0)
}

/// Imports entries, all or none. An entry already imported (the same TAN, date, amount and tax) is skipped, so the same statement can be imported twice.
pub fn import_csv(conn: &Connection, user_id: i64, csv: &str) -> Result<Value, String> {
    let rows = parse(csv)?;
    let tx = conn.unchecked_transaction().map_err(|e| e.to_string())?;
    let (mut added, mut skipped) = (0, 0);
    for r in &rows {
        let n = tx
            .execute(
                "INSERT OR IGNORE INTO tds_26as (deductor, tan, section, txn_date, amount_paise, tds_paise, imported_by) VALUES (?1,?2,?3,?4,?5,?6,?7)",
                params![r.deductor, r.tan, r.section, r.day, r.amount, r.tds, user_id],
            )
            .map_err(|e| e.to_string())?;
        if n == 1 { added += 1 } else { skipped += 1 }
    }
    audit(&tx, Some(user_id), "tds_26as_imported", &format!("{added} added, {skipped} already there"));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(json!({ "added": added, "already_there": skipped }))
}

/// Matches the TDS booked on receipts with the entries reported, for entries and receipts dated from `from` to `to`.
pub fn reconcile(conn: &Connection, from: &str, to: &str) -> Result<Value, String> {
    let (from, to) = (date(from, true)?, date(to, true)?);
    if from > to {
        return Err("The start date is after the end date.".into());
    }
    let entries: Vec<(i64, String, String, String, String, i64)> = conn
        .prepare("SELECT id, deductor, tan, section, txn_date, tds_paise FROM tds_26as WHERE txn_date BETWEEN ?1 AND ?2 ORDER BY txn_date, id")
        .map_err(|e| e.to_string())?
        .query_map(params![from, to], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?)))
        .map_err(|e| e.to_string())?
        .collect::<Result<_, _>>()
        .map_err(|e| e.to_string())?;
    let receipts: Vec<(i64, String, String, String, String, i64)> = conn
        .prepare(
            "SELECT r.id, r.number, c.name, UPPER(TRIM(c.tan)), r.received_date, r.tds_paise
             FROM effective_receipts r JOIN invoices i ON i.id = r.invoice_id JOIN projects p ON p.id = i.project_id JOIN clients c ON c.id = p.client_id
             WHERE r.tds_paise > 0 AND r.received_date BETWEEN ?1 AND ?2 ORDER BY r.received_date, r.id",
        )
        .map_err(|e| e.to_string())?
        .query_map(params![from, to], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?)))
        .map_err(|e| e.to_string())?
        .collect::<Result<_, _>>()
        .map_err(|e| e.to_string())?;
    let gap = |a: &str, b: &str| -> i64 {
        conn.query_row("SELECT ABS(CAST(julianday(?1) - julianday(?2) AS INTEGER))", params![a, b], |r| r.get(0)).unwrap_or(i64::MAX)
    };
    // every possible pairing, closest in time first, so each receipt and each entry is used once and the nearest dates win
    let mut pairs: Vec<(i64, usize, usize)> = Vec::new();
    for (ei, e) in entries.iter().enumerate() {
        for (ri, r) in receipts.iter().enumerate() {
            if !r.3.is_empty() && r.3 == e.2 && r.5 == e.5 {
                let days = gap(&e.4, &r.4);
                if days <= WINDOW_DAYS {
                    pairs.push((days, ei, ri));
                }
            }
        }
    }
    pairs.sort();
    let (mut entry_used, mut receipt_used) = (vec![false; entries.len()], vec![false; receipts.len()]);
    let mut matched = Vec::new();
    for (days, ei, ri) in pairs {
        if entry_used[ei] || receipt_used[ri] {
            continue;
        }
        entry_used[ei] = true;
        receipt_used[ri] = true;
        let (e, r) = (&entries[ei], &receipts[ri]);
        matched.push(json!({
            "entry_id": e.0, "receipt_id": r.0, "receipt_number": r.1, "client": r.2, "deductor": e.1, "tan": e.2, "section": e.3,
            "entry_date": e.4, "receipt_date": r.4, "tds_paise": e.5, "days_apart": days,
        }));
    }
    let only_reported: Vec<Value> = entries
        .iter()
        .enumerate()
        .filter(|(i, _)| !entry_used[*i])
        .map(|(_, e)| json!({ "entry_id": e.0, "deductor": e.1, "tan": e.2, "section": e.3, "entry_date": e.4, "tds_paise": e.5 }))
        .collect();
    let only_booked: Vec<Value> = receipts
        .iter()
        .enumerate()
        .filter(|(i, _)| !receipt_used[*i])
        .map(|(_, r)| {
            let why = if r.3.is_empty() { "The client has no TAN on file." } else { "No matching entry in 26AS." };
            json!({ "receipt_id": r.0, "receipt_number": r.1, "client": r.2, "tan": r.3, "receipt_date": r.4, "tds_paise": r.5, "why": why })
        })
        .collect();
    let total = |list: &[Value]| list.iter().map(|v| v["tds_paise"].as_i64().unwrap_or(0)).sum::<i64>();
    Ok(json!({
        "from": from, "to": to, "window_days": WINDOW_DAYS,
        "matched_paise": total(&matched), "only_reported_paise": total(&only_reported), "only_booked_paise": total(&only_booked),
        "matched": matched, "only_reported": only_reported, "only_booked": only_booked,
    }))
}

pub fn list(conn: &Connection) -> Result<Vec<Value>, String> {
    let mut stmt = conn.prepare("SELECT id, deductor, tan, section, txn_date, amount_paise, tds_paise FROM tds_26as ORDER BY txn_date DESC, id DESC LIMIT 2000").map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| {
            Ok(json!({ "id": r.get::<_, i64>(0)?, "deductor": r.get::<_, String>(1)?, "tan": r.get::<_, String>(2)?, "section": r.get::<_, String>(3)?,
                       "txn_date": r.get::<_, String>(4)?, "amount_paise": r.get::<_, i64>(5)?, "tds_paise": r.get::<_, i64>(6)? }))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::schema::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('ada','x','owner')", []).unwrap();
        c.execute("INSERT INTO clients (name, tan) VALUES ('Acme Builders', 'PUNA12345B')", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('No Tan Co')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1,'Villa')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (2,'Shop')", []).unwrap();
        for (project, n) in [(1, 1), (2, 2)] {
            c.execute(
                "INSERT INTO invoices (project_id, kind, status, number, issue_date, gst_mode, total_paise) VALUES (?1,'tax_invoice','issued',?2,'2026-04-01','none',100000000)",
                params![project, format!("INV-{n}")],
            )
            .unwrap();
        }
        c
    }

    fn receipt(c: &Connection, invoice: i64, number: &str, day: &str, tds: i64) {
        c.execute("INSERT INTO receipts (number, invoice_id, received_date, amount_paise, tds_paise, mode) VALUES (?1,?2,?3,?4,?5,'bank')", params![number, invoice, day, 9_000_000, tds]).unwrap();
    }

    const CSV: &str = "Name of Deductor,TAN of Deductor,Section,Transaction Date,Amount Paid / Credited,Tax Deducted\n\
        \"Acme Builders, Pune\",puna12345b,194J,15-Jun-2026,\"1,00,000.00\",\"10,000.00\"\n\
        Acme Builders,PUNA12345B,194J,2026-09-10,50000,5000.50\n\
        Stranger Ltd,MUMS99999Z,194C,01/08/2026,20000,200\n";

    #[test]
    fn amounts_and_dates_are_read_in_the_forms_the_statement_uses() {
        assert_eq!(parse_paise("1,00,000.00").unwrap(), 100_000_00);
        assert_eq!(parse_paise("₹ 5,000.5").unwrap(), 5_000_50);
        assert_eq!(parse_paise("200").unwrap(), 200_00);
        assert!(parse_paise("12.345").is_err() && parse_paise("abc").is_err() && parse_paise("").is_err() && parse_paise("-5").is_err());
        assert_eq!(parse_day("15-Jun-2026").unwrap(), "2026-06-15");
        assert_eq!(parse_day("01/08/2026").unwrap(), "2026-08-01");
        assert_eq!(parse_day("2026-9-1").unwrap(), "2026-09-01");
        assert!(parse_day("31/02/2026").is_err() && parse_day("tomorrow").is_err());
        assert_eq!(split_line("\"a, b\",\"say \"\"hi\"\"\",c"), vec!["a, b", "say \"hi\"", "c"]);
    }

    #[test]
    fn an_import_is_all_or_nothing_and_can_be_repeated() {
        let c = conn();
        assert_eq!(import_csv(&c, 1, CSV).unwrap(), json!({ "added": 3, "already_there": 0 }));
        assert_eq!(import_csv(&c, 1, CSV).unwrap(), json!({ "added": 0, "already_there": 3 }));
        assert_eq!(list(&c).unwrap().len(), 3);
        let bad = "Deductor,TAN,Date,Amount,Tax Deducted\nGood,PUNA12345B,2026-06-01,100,10\nBad,SHORT,2026-06-01,100,10\nWorse,PUNA12345B,32-13-2026,100,10\n";
        let err = import_csv(&c, 1, bad).unwrap_err();
        assert!(err.contains("Line 3") && err.contains("Line 4") && err.contains("Nothing was imported"), "{err}");
        assert_eq!(list(&c).unwrap().len(), 3);
        assert!(import_csv(&c, 1, "").is_err() && import_csv(&c, 1, "Deductor,TAN\nA,PUNA12345B\n").is_err());
        assert!(import_csv(&c, 1, "Deductor,TAN,Date,Amount,Tax Deducted\n").unwrap_err().contains("no entries"));
    }

    #[test]
    fn booked_tds_is_matched_with_what_the_deductor_reported() {
        let c = conn();
        import_csv(&c, 1, CSV).unwrap();
        receipt(&c, 1, "RC-1", "2026-06-12", 10_000_00); // matches the June entry
        receipt(&c, 1, "RC-2", "2026-09-08", 5_000_50); // matches the September entry
        receipt(&c, 1, "RC-3", "2026-07-01", 7_000_00); // not reported by the deductor
        receipt(&c, 2, "RC-4", "2026-08-01", 200_00); // client has no TAN on file
        let report = reconcile(&c, "2026-04-01", "2027-03-31").unwrap();
        assert_eq!(report["matched"].as_array().unwrap().len(), 2);
        assert_eq!(report["matched_paise"], 15_000_50);
        let booked = report["only_booked"].as_array().unwrap();
        assert_eq!(booked.len(), 2);
        assert!(booked.iter().any(|b| b["receipt_number"] == "RC-3" && b["why"] == "No matching entry in 26AS."));
        assert!(booked.iter().any(|b| b["receipt_number"] == "RC-4" && b["why"] == "The client has no TAN on file."));
        let reported = report["only_reported"].as_array().unwrap();
        assert_eq!((reported.len(), reported[0]["deductor"].as_str()), (1, Some("Stranger Ltd")));
        assert_eq!(report["only_booked_paise"], 7_200_00);
    }

    #[test]
    fn each_receipt_and_entry_is_used_once_and_far_apart_dates_do_not_match() {
        let c = conn();
        import_csv(&c, 1, "Deductor,TAN,Date,Amount,Tax Deducted\nAcme,PUNA12345B,2026-06-10,100000,10000\nAcme,PUNA12345B,2026-06-20,100000,10000\n").unwrap();
        receipt(&c, 1, "RC-1", "2026-06-19", 10_000_00);
        let report = reconcile(&c, "2026-04-01", "2027-03-31").unwrap();
        assert_eq!(report["matched"][0]["entry_date"], "2026-06-20", "the nearer entry is the one matched");
        assert_eq!(report["only_reported"].as_array().unwrap().len(), 1);
        let c2 = conn();
        import_csv(&c2, 1, "Deductor,TAN,Date,Amount,Tax Deducted\nAcme,PUNA12345B,2027-02-20,100000,10000\n").unwrap();
        receipt(&c2, 1, "RC-1", "2026-04-02", 10_000_00);
        let far = reconcile(&c2, "2026-04-01", "2027-03-31").unwrap();
        assert_eq!(far["matched"].as_array().unwrap().len(), 0, "more than {WINDOW_DAYS} days apart is not the same deduction");
        assert!(reconcile(&c2, "2027-01-01", "2026-01-01").is_err());
    }
}
