use crate::auth::Permission;
use crate::commands::{HostState, require, INTERNAL};
use crate::{db, pdf};
use base64::Engine;
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};
use sha2_lite::sha256_hex;

// ---------- money ----------

/// Parses a non-negative decimal string into an integer scaled by 10^scale (no floats).
pub fn parse_decimal(s: &str, scale: u32) -> Result<i64, String> {
    let s = s.trim().replace(',', "");
    if s.is_empty() {
        return Err("Enter a number.".into());
    }
    let (int, frac) = s.split_once('.').unwrap_or((&s, ""));
    if int.is_empty() && frac.is_empty() || !int.chars().all(|c| c.is_ascii_digit()) || !frac.chars().all(|c| c.is_ascii_digit()) {
        return Err(format!("'{s}' is not a valid number."));
    }
    if frac.len() > scale as usize {
        return Err(format!("At most {scale} decimal places allowed."));
    }
    let mult = 10i64.pow(scale);
    let int_v: i64 = if int.is_empty() { 0 } else { int.parse().map_err(|_| "Number too large.")? };
    let frac_v: i64 = if frac.is_empty() { 0 } else { frac.parse::<i64>().unwrap() * 10i64.pow(scale - frac.len() as u32) };
    int_v.checked_mul(mult).and_then(|v| v.checked_add(frac_v)).ok_or_else(|| "Number too large.".into())
}

/// Indian digit grouping, e.g. 12345678 paise -> "₹1,23,456.78".
pub fn fmt_inr(paise: i64) -> String {
    let neg = paise < 0;
    let p = paise.abs();
    let (rupees, ps) = (p / 100, p % 100);
    let digits = rupees.to_string();
    let grouped = if digits.len() <= 3 {
        digits
    } else {
        let (head, tail) = digits.split_at(digits.len() - 3);
        let mut parts = Vec::new();
        let mut h = head;
        while h.len() > 2 {
            let (a, b) = h.split_at(h.len() - 2);
            parts.push(b.to_string());
            h = a;
        }
        if !h.is_empty() {
            parts.push(h.to_string());
        }
        parts.reverse();
        format!("{},{}", parts.join(","), tail)
    };
    format!("{}₹{}.{:02}", if neg { "-" } else { "" }, grouped, ps)
}

fn div_round(n: i64, d: i64) -> i64 {
    (n + d / 2) / d
}

// ---------- input / calculation ----------

/// Regular scheme: 18% (basis points). Composition (services): 6%.
pub const REGULAR_BP: i64 = 1800;
pub const COMPOSITION_BP: i64 = 600;

pub fn company_scheme(conn: &Connection) -> Result<String, String> {
    conn.query_row("SELECT gst_scheme FROM company_profile WHERE id = 1", [], |r| r.get(0)).map_err(|e| e.to_string())
}

#[derive(Serialize, Deserialize, Clone)]
pub struct LineInput {
    pub description: String,
    pub qty: String,
    pub rate: String,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct InvoiceInput {
    pub id: Option<i64>,
    pub project_id: i64,
    pub kind: String,
    pub issue_date: String,
    pub due_date: String,
    pub gst_mode: String,
    /// GST rate as a percentage string, e.g. "18" or "12.5".
    pub gst_rate: String,
    pub notes: String,
    pub lines: Vec<LineInput>,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct CalcLine {
    pub description: String,
    pub qty_milli: i64,
    pub rate_paise: i64,
    pub amount_paise: i64,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct Totals {
    pub lines: Vec<CalcLine>,
    pub subtotal_paise: i64,
    pub cgst_paise: i64,
    pub sgst_paise: i64,
    pub igst_paise: i64,
    pub tax_paise: i64,
    pub total_paise: i64,
    pub gst_rate_bp: i64,
    /// Mode actually applied after the company scheme is taken into account.
    pub gst_mode: String,
    /// Composition scheme only: 6% liability on this bill. Paid by the firm, NOT added to the total.
    pub composition_paise: i64,
}

/// `scheme` is the company's declared GST registration (none / composition / regular).
pub fn calculate(input: &InvoiceInput, scheme: &str) -> Result<Totals, String> {
    if input.lines.is_empty() {
        return Err("Add at least one line.".into());
    }
    let mut lines = Vec::new();
    let mut subtotal = 0i64;
    for (i, l) in input.lines.iter().enumerate() {
        let description = l.description.trim();
        if description.is_empty() {
            return Err(format!("Line {}: description is required.", i + 1));
        }
        let qty_milli = parse_decimal(&l.qty, 3).map_err(|e| format!("Line {}: quantity. {e}", i + 1))?;
        let rate_paise = parse_decimal(&l.rate, 2).map_err(|e| format!("Line {}: rate. {e}", i + 1))?;
        let amount = div_round(qty_milli.checked_mul(rate_paise).ok_or("Amount too large.")?, 1000);
        subtotal = subtotal.checked_add(amount).ok_or("Amount too large.")?;
        lines.push(CalcLine { description: description.to_string(), qty_milli, rate_paise, amount_paise: amount });
    }
    let (mode, rate_bp, cgst, sgst, igst, composition) = match scheme {
        "none" => ("none", 0, 0, 0, 0, 0),
        // Composition: nothing is charged to the client; the firm owes 6% (3% CGST + 3% SGST) of turnover.
        "composition" => ("none", 0, 0, 0, 0, div_round(subtotal * COMPOSITION_BP, 10_000)),
        "regular" => {
            let total = div_round(subtotal * REGULAR_BP, 10_000);
            if input.gst_mode == "inter" {
                ("inter", REGULAR_BP, 0, 0, total, 0)
            } else {
                // 9% + 9%; the second half absorbs rounding so the pair sums exactly.
                let cgst = total / 2;
                ("intra", REGULAR_BP, cgst, total - cgst, 0, 0)
            }
        }
        _ => return Err("Invalid GST scheme.".into()),
    };
    let tax = cgst + sgst + igst;
    Ok(Totals { lines, subtotal_paise: subtotal, cgst_paise: cgst, sgst_paise: sgst, igst_paise: igst, tax_paise: tax, total_paise: subtotal + tax, gst_rate_bp: rate_bp, gst_mode: mode.to_string(), composition_paise: composition })
}

// ---------- dates / numbering ----------

pub fn validate_date(s: &str) -> Result<(), String> {
    parse_date(s).map(|_| ())
}

fn parse_date(s: &str) -> Result<(i32, u32, u32), String> {
    let bad = || format!("'{s}' is not a valid date (use YYYY-MM-DD).");
    let parts: Vec<&str> = s.split('-').collect();
    if parts.len() != 3 || parts[0].len() != 4 || parts[1].len() != 2 || parts[2].len() != 2 || !parts.iter().all(|p| p.bytes().all(|b| b.is_ascii_digit())) {
        return Err(bad());
    }
    let (y, m, d): (i32, u32, u32) = (parts[0].parse().map_err(|_| bad())?, parts[1].parse().map_err(|_| bad())?, parts[2].parse().map_err(|_| bad())?);
    // Business dates only: this also keeps financial-year labels and numbers well formed.
    if !(1990..=2100).contains(&y) {
        return Err(bad());
    }
    let leap = (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;
    let dim = match m { 1 | 3 | 5 | 7 | 8 | 10 | 12 => 31, 4 | 6 | 9 | 11 => 30, 2 => if leap { 29 } else { 28 }, _ => return Err(bad()) };
    if d == 0 || d > dim {
        return Err(bad());
    }
    Ok((y, m, d))
}

/// Indian financial year (April-March), e.g. 2026-10-12 -> "2026-27".
pub fn financial_year(date: &str) -> Result<String, String> {
    let (y, m, _) = parse_date(date)?;
    let start = if m >= 4 { y } else { y - 1 };
    Ok(format!("{}-{:02}", start, (start + 1) % 100))
}

fn series_prefix(kind: &str) -> &'static str {
    match kind {
        "proforma" => "PI",
        "receipt" => "RCT",
        "credit_note" => "CN",
        "letter_out" => "OUT",
        "letter_in" => "IN",
        "agreement" => "AGR",
        _ => "INV",
    }
}

pub(crate) fn allocate_number(conn: &Connection, kind: &str, issue_date: &str) -> Result<String, String> {
    let fy = financial_year(issue_date)?;
    let prefix = series_prefix(kind);
    conn.execute(
        "INSERT INTO number_series (kind, fy, prefix, last_seq) VALUES (?1, ?2, ?3, 0) ON CONFLICT(kind, fy) DO NOTHING",
        params![kind, fy, prefix],
    )
    .map_err(|e| e.to_string())?;
    conn.execute("UPDATE number_series SET last_seq = last_seq + 1 WHERE kind = ?1 AND fy = ?2", params![kind, fy])
        .map_err(|e| e.to_string())?;
    let seq: i64 = conn
        .query_row("SELECT last_seq FROM number_series WHERE kind = ?1 AND fy = ?2", params![kind, fy], |r| r.get(0))
        .map_err(|e| e.to_string())?;
    Ok(format!("{prefix}/{fy}/{seq:04}"))
}

// ---------- persistence ----------

fn validate_header(input: &InvoiceInput) -> Result<(), String> {
    parse_date(&input.issue_date)?;
    if !input.due_date.trim().is_empty() {
        parse_date(&input.due_date)?;
    }
    if !["tax_invoice", "proforma"].contains(&input.kind.as_str()) {
        return Err("Invalid document type.".into());
    }
    Ok(())
}

pub fn save_draft(conn: &mut Connection, input: &InvoiceInput) -> Result<i64, String> {
    validate_header(input)?;
    let t = calculate(input, &company_scheme(conn)?)?;
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let project_ok: i64 = tx
        .query_row("SELECT COUNT(*) FROM projects WHERE id = ?1 AND archived = 0", params![input.project_id], |r| r.get(0))
        .map_err(|e| e.to_string())?;
    if project_ok == 0 {
        return Err("Choose an active project.".into());
    }
    let id = match input.id {
        Some(id) => {
            let status: String = tx
                .query_row("SELECT status FROM invoices WHERE id = ?1", params![id], |r| r.get(0))
                .map_err(|_| "Invoice not found.".to_string())?;
            if status != "draft" {
                return Err("Only drafts can be edited.".into());
            }
            tx.execute(
                "UPDATE invoices SET project_id=?1, kind=?2, issue_date=?3, due_date=?4, gst_mode=?5, gst_rate_bp=?6, notes=?7,
                 subtotal_paise=?8, tax_paise=?9, total_paise=?10, updated_at=datetime('now') WHERE id=?11",
                params![input.project_id, input.kind, input.issue_date, input.due_date.trim(), t.gst_mode, t.gst_rate_bp, input.notes, t.subtotal_paise, t.tax_paise, t.total_paise, id],
            )
            .map_err(|e| e.to_string())?;
            tx.execute("DELETE FROM invoice_lines WHERE invoice_id = ?1", params![id]).map_err(|e| e.to_string())?;
            id
        }
        None => {
            tx.execute(
                "INSERT INTO invoices (project_id, kind, issue_date, due_date, gst_mode, gst_rate_bp, notes, subtotal_paise, tax_paise, total_paise)
                 VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10)",
                params![input.project_id, input.kind, input.issue_date, input.due_date.trim(), t.gst_mode, t.gst_rate_bp, input.notes, t.subtotal_paise, t.tax_paise, t.total_paise],
            )
            .map_err(|e| e.to_string())?;
            tx.last_insert_rowid()
        }
    };
    for (i, l) in t.lines.iter().enumerate() {
        tx.execute(
            "INSERT INTO invoice_lines (invoice_id, position, description, qty_milli, rate_paise, amount_paise) VALUES (?1,?2,?3,?4,?5,?6)",
            params![id, i as i64, l.description, l.qty_milli, l.rate_paise, l.amount_paise],
        )
        .map_err(|e| e.to_string())?;
    }
    tx.commit().map_err(|e| e.to_string())?;
    Ok(id)
}

#[derive(Serialize)]
pub struct InvoiceRow {
    pub id: i64,
    pub number: Option<String>,
    pub kind: String,
    pub status: String,
    pub client_name: String,
    pub project_name: String,
    pub issue_date: String,
    pub total_paise: i64,
    pub total_display: String,
    pub paid_display: String,
    pub tds_display: String,
    pub balance_paise: i64,
    pub balance_display: String,
    pub credited_display: String,
    pub paid_paise: i64,
    pub tds_paise: i64,
    pub credited_paise: i64,
    pub due_date: String,
    /// Number of the tax invoice raised from this pro forma, if any.
    pub converted_to: Option<String>,
    /// Number of the pro forma this invoice was raised from, if any.
    pub from_proforma: Option<String>,
}

pub fn list(conn: &Connection) -> Result<Vec<InvoiceRow>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT i.id, i.number, i.kind, i.status, c.name, p.name, i.issue_date, i.total_paise,
                    b.received_paise, b.tds_paise, b.credited_paise, b.written_off_paise,
                    (SELECT COALESCE(t.number, 'Draft') FROM invoices t WHERE t.source_proforma_id = i.id AND t.status <> 'cancelled'),
                    (SELECT s.number FROM invoices s WHERE s.id = i.source_proforma_id), i.due_date
             FROM invoices i JOIN invoice_balances b ON b.invoice_id = i.id JOIN projects p ON p.id = i.project_id JOIN clients c ON c.id = p.client_id
             ORDER BY i.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| {
            let total: i64 = r.get(7)?;
            let (paid, tds, credited, written): (i64, i64, i64, i64) = (r.get(8)?, r.get(9)?, r.get(10)?, r.get(11)?);
            let balance = total - paid - tds - credited - written;
            Ok(InvoiceRow {
                id: r.get(0)?, number: r.get(1)?, kind: r.get(2)?, status: r.get(3)?, client_name: r.get(4)?, project_name: r.get(5)?,
                issue_date: r.get(6)?, total_paise: total, total_display: fmt_inr(total),
                paid_display: fmt_inr(paid), tds_display: fmt_inr(tds), balance_paise: balance, balance_display: fmt_inr(balance),
                credited_display: fmt_inr(credited), paid_paise: paid, tds_paise: tds, credited_paise: credited, due_date: r.get(14)?,
                converted_to: r.get(12)?, from_proforma: r.get(13)?,
            })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

#[derive(Serialize)]
pub struct InvoiceDetail {
    pub status: String,
    pub number: Option<String>,
    pub input: InvoiceInput,
}

fn paise_to_plain(p: i64) -> String {
    format!("{}.{:02}", p / 100, p % 100)
}

fn milli_to_plain(q: i64) -> String {
    let s = format!("{}.{:03}", q / 1000, q % 1000);
    s.trim_end_matches('0').trim_end_matches('.').to_string()
}

pub fn get(conn: &Connection, id: i64) -> Result<InvoiceDetail, String> {
    let row = conn
        .query_row(
            "SELECT status, number, project_id, kind, issue_date, due_date, gst_mode, gst_rate_bp, notes FROM invoices WHERE id = ?1",
            params![id],
            |r| Ok((r.get::<_, String>(0)?, r.get::<_, Option<String>>(1)?, r.get::<_, i64>(2)?, r.get::<_, String>(3)?, r.get::<_, String>(4)?, r.get::<_, String>(5)?, r.get::<_, String>(6)?, r.get::<_, i64>(7)?, r.get::<_, String>(8)?)),
        )
        .map_err(|_| "Invoice not found.".to_string())?;
    let mut stmt = conn
        .prepare("SELECT description, qty_milli, rate_paise FROM invoice_lines WHERE invoice_id = ?1 ORDER BY position")
        .map_err(|e| e.to_string())?;
    let lines = stmt
        .query_map(params![id], |r| Ok(LineInput { description: r.get(0)?, qty: milli_to_plain(r.get(1)?), rate: paise_to_plain(r.get(2)?) }))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(InvoiceDetail {
        status: row.0,
        number: row.1,
        input: InvoiceInput { id: Some(id), project_id: row.2, kind: row.3, issue_date: row.4, due_date: row.5, gst_mode: row.6, gst_rate: paise_to_plain(row.7), notes: row.8, lines },
    })
}

/// Creates a draft tax invoice from an issued pro forma (same project, lines and notes). The pro forma is left untouched.
pub fn convert_proforma(conn: &mut Connection, id: i64, today: &str) -> Result<i64, String> {
    let d = get(conn, id)?;
    if d.input.kind != "proforma" || d.status != "issued" {
        return Err("Only an issued pro forma invoice can be converted.".into());
    }
    let existing: Option<String> = conn
        .query_row(
            "SELECT COALESCE(number, 'a draft') FROM invoices WHERE source_proforma_id = ?1 AND status <> 'cancelled'",
            params![id],
            |r| r.get(0),
        )
        .ok();
    if let Some(n) = existing {
        return Err(format!("This pro forma has already been converted ({n})."));
    }
    let mut input = d.input.clone();
    input.id = None;
    input.kind = "tax_invoice".into();
    input.issue_date = today.to_string();
    input.due_date = String::new();
    let new_id = save_draft(conn, &input)?;
    conn.execute("UPDATE invoices SET source_proforma_id = ?1 WHERE id = ?2", params![id, new_id]).map_err(|e| e.to_string())?;
    Ok(new_id)
}

// ---------- document snapshot & HTML ----------

#[derive(Serialize, Deserialize)]
pub struct Snapshot {
    pub number: String,
    pub kind: String,
    pub issue_date: String,
    pub due_date: String,
    pub gst_mode: String,
    pub scheme: String,
    pub notes: String,
    pub company_name: String,
    pub company_address: String,
    pub company_pan: String,
    pub company_gstin: String,
    pub company_coa: String,
    pub bank_details: String,
    #[serde(default)]
    pub company_email: String,
    #[serde(default)]
    pub company_phone: String,
    #[serde(default)]
    pub architect_name: String,
    pub client_name: String,
    pub client_address: String,
    pub client_gstin: String,
    #[serde(default)]
    pub client_email: String,
    #[serde(default)]
    pub client_phone: String,
    #[serde(default)]
    pub client_pan: String,
    #[serde(default)]
    pub client_tan: String,
    #[serde(default)]
    pub client_state: String,
    pub project_name: String,
    pub totals: Totals,
    /// Credit notes: the number of the invoice being credited.
    #[serde(default)]
    pub against: String,
    /// UPI payment link encoded in the QR printed on the document (empty = no QR).
    #[serde(default)]
    pub upi_uri: String,
    /// Square mark and wide lockup used while rendering. Kept off the stored snapshot; the issued PDF already contains them.
    #[serde(skip)]
    pub logo_square_b64: String,
    #[serde(skip)]
    pub logo_wide_b64: String,
    /// Firm accent at issue, `#RRGGBB` or blank. Older snapshots have none and print in black.
    #[serde(default)]
    pub accent_hex: String,
}

fn primary_account_text(conn: &Connection) -> Result<Option<String>, String> {
    let row: Option<(String, String, String, String)> = conn
        .query_row(
            "SELECT bank_name, account_holder, account_number, ifsc FROM bank_accounts WHERE is_primary = 1 AND active = 1",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)),
        )
        .ok();
    Ok(row.and_then(|(bank, holder, number, ifsc)| {
        let mut lines = Vec::new();
        if !holder.is_empty() {
            lines.push(format!("Account name: {holder}"));
        }
        if !bank.is_empty() {
            lines.push(format!("Bank: {bank}"));
        }
        if !number.is_empty() {
            lines.push(format!("Account no.: {number}"));
        }
        if !ifsc.is_empty() {
            lines.push(format!("IFSC: {ifsc}"));
        }
        if lines.is_empty() { None } else { Some(lines.join("\n")) }
    }))
}

fn build_snapshot(conn: &Connection, input: &InvoiceInput, number: &str) -> Result<Snapshot, String> {
    let scheme = company_scheme(conn)?;
    let totals = calculate(input, &scheme)?;
    let (company_name, company_address, company_email, company_phone, company_pan, company_gstin, company_coa, bank_details, architect_name, accent_hex) = conn
        .query_row(
            "SELECT name, address, email, phone, pan, gstin, coa_registration, bank_details, architect_name, accent_hex FROM company_profile WHERE id = 1",
            [],
            |r| {
                Ok((
                    r.get::<_, String>(0)?,
                    r.get::<_, String>(1)?,
                    r.get::<_, String>(2)?,
                    r.get::<_, String>(3)?,
                    r.get::<_, String>(4)?,
                    r.get::<_, String>(5)?,
                    r.get::<_, String>(6)?,
                    r.get::<_, String>(7)?,
                    r.get::<_, String>(8)?,
                    r.get::<_, String>(9)?,
                ))
            },
        )
        .map_err(|e| e.to_string())?;
    // The primary active bank account, when one exists, supplies the payment details printed on the document.
    let bank_details = primary_account_text(conn)?.unwrap_or(bank_details);
    let (project_name, client_name, client_address, client_gstin, client_email, client_phone, client_pan, client_tan, client_city, client_state) = conn
        .query_row(
            "SELECT p.name, CASE WHEN TRIM(c.legal_name) = '' THEN c.name ELSE c.legal_name END,
                    CASE WHEN TRIM(c.billing_address) = '' THEN c.address ELSE c.billing_address END,
                    c.gstin, c.email, c.phone, c.pan, c.tan, c.city, c.state
             FROM projects p JOIN clients c ON c.id = p.client_id WHERE p.id = ?1",
            params![input.project_id],
            |r| {
                Ok((
                    r.get::<_, String>(0)?,
                    r.get::<_, String>(1)?,
                    r.get::<_, String>(2)?,
                    r.get::<_, String>(3)?,
                    r.get::<_, String>(4)?,
                    r.get::<_, String>(5)?,
                    r.get::<_, String>(6)?,
                    r.get::<_, String>(7)?,
                    r.get::<_, String>(8)?,
                    r.get::<_, String>(9)?,
                ))
            },
        )
        .map_err(|_| "Project not found.".to_string())?;
    let client_address = join_place(&client_address, &client_city, &client_state);
    let logos = crate::commands::company_logos_b64(conn);
    let upi_id: String = conn.query_row("SELECT upi_id FROM company_profile WHERE id = 1", [], |r| r.get(0)).map_err(|e| e.to_string())?;
    let upi_uri = if upi_id.trim().is_empty() {
        String::new()
    } else {
        format!(
            "upi://pay?pa={}&pn={}&am={}&cu=INR&tn={}",
            pct_encode(upi_id.trim()), pct_encode(&company_name), paise_to_plain(totals.total_paise), pct_encode(number)
        )
    };
    Ok(Snapshot {
        against: String::new(),
        upi_uri,
        number: number.to_string(),
        kind: input.kind.clone(),
        issue_date: input.issue_date.clone(),
        due_date: input.due_date.clone(),
        gst_mode: totals.gst_mode.clone(),
        scheme,
        notes: input.notes.clone(),
        company_name, company_address, company_pan, company_gstin, company_coa, bank_details,
        company_email, company_phone, architect_name,
        client_name, client_address, client_gstin, client_email, client_phone, client_pan, client_tan, client_state, project_name,
        totals,
        logo_square_b64: logos.square,
        logo_wide_b64: logos.wide,
        accent_hex,
    })
}

fn join_place(address: &str, city: &str, state: &str) -> String {
    let mut text = address.trim().to_string();
    for part in [city.trim(), state.trim()] {
        if part.is_empty() || text.to_lowercase().contains(&part.to_lowercase()) {
            continue;
        }
        if text.is_empty() {
            text = part.to_string();
        } else {
            text.push_str(", ");
            text.push_str(part);
        }
    }
    text
}

fn pct_encode(s: &str) -> String {
    let mut out = String::new();
    for b in s.bytes() {
        if b.is_ascii_alphanumeric() || b"-._~".contains(&b) {
            out.push(b as char);
        } else {
            out.push_str(&format!("%{b:02X}"));
        }
    }
    out
}

pub(crate) fn esc(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;").replace('\n', "<br/>")
}

fn display_date(iso: &str) -> String {
    const MONTHS: [&str; 12] = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    let mut parts = iso.split('-');
    let (Some(year), Some(month), Some(day)) = (parts.next(), parts.next(), parts.next()) else {
        return iso.to_string();
    };
    if parts.next().is_some() {
        return iso.to_string();
    }
    let Ok(month) = month.parse::<usize>() else {
        return iso.to_string();
    };
    if !(1..=12).contains(&month) {
        return iso.to_string();
    }
    let day = day.trim_start_matches('0');
    let day = if day.is_empty() { "0" } else { day };
    format!("{day} {} {year}", MONTHS[month - 1])
}

fn gst_state(gstin: &str) -> Option<&'static str> {
    let code = gstin.trim().get(..2)?;
    Some(match code {
        "01" => "Jammu and Kashmir",
        "02" => "Himachal Pradesh",
        "03" => "Punjab",
        "04" => "Chandigarh",
        "05" => "Uttarakhand",
        "06" => "Haryana",
        "07" => "Delhi",
        "08" => "Rajasthan",
        "09" => "Uttar Pradesh",
        "10" => "Bihar",
        "11" => "Sikkim",
        "12" => "Arunachal Pradesh",
        "13" => "Nagaland",
        "14" => "Manipur",
        "15" => "Mizoram",
        "16" => "Tripura",
        "17" => "Meghalaya",
        "18" => "Assam",
        "19" => "West Bengal",
        "20" => "Jharkhand",
        "21" => "Odisha",
        "22" => "Chhattisgarh",
        "23" => "Madhya Pradesh",
        "24" => "Gujarat",
        "26" => "Dadra and Nagar Haveli and Daman and Diu",
        "27" => "Maharashtra",
        "29" => "Karnataka",
        "30" => "Goa",
        "31" => "Lakshadweep",
        "32" => "Kerala",
        "33" => "Tamil Nadu",
        "34" => "Puducherry",
        "35" => "Andaman and Nicobar Islands",
        "36" => "Telangana",
        "37" => "Andhra Pradesh",
        "38" => "Ladakh",
        _ => return None,
    })
}

fn rail_block(label: &str, body: &str) -> String {
    if body.is_empty() {
        String::new()
    } else {
        format!("<span class=\"kicker\">{label}</span><br/>{body}<br/><br/>")
    }
}

pub fn invoice_html(s: &Snapshot) -> String {
    let title = if s.kind == "credit_note" {
        "CREDIT NOTE"
    } else if s.kind == "proforma" {
        "PRO FORMA"
    } else {
        match s.scheme.as_str() {
            "regular" => "TAX INVOICE",
            "composition" => "BILL OF SUPPLY",
            _ => "INVOICE",
        }
    };
    let mut rows = String::new();
    for l in &s.totals.lines {
        rows.push_str(&format!(
            "<tr><td style=\"background-color: #ffffff; padding: 4px 3px;\">{}</td><td class=\"num\" style=\"background-color: #ffffff; padding: 4px 3px;\">{}</td><td class=\"num\" style=\"background-color: #ffffff; padding: 4px 3px;\">{}</td><td class=\"num\" style=\"background-color: #ffffff; padding: 4px 3px;\">{}</td></tr>",
            esc(&l.description), fmt_inr(l.rate_paise), esc(&milli_to_plain(l.qty_milli)), fmt_inr(l.amount_paise)
        ));
    }
    let t = &s.totals;
    let half = |bp: i64| format!("{}", milli_to_plain(bp * 10 / 2));
    let mut tax_rows = String::new();
    match s.gst_mode.as_str() {
        "intra" => {
            tax_rows.push_str(&format!("<tr><td>CGST {}%</td><td class=\"num\">{}</td></tr>", half(t.gst_rate_bp), fmt_inr(t.cgst_paise)));
            tax_rows.push_str(&format!("<tr><td>SGST {}%</td><td class=\"num\">{}</td></tr>", half(t.gst_rate_bp), fmt_inr(t.sgst_paise)));
        }
        "inter" => tax_rows.push_str(&format!("<tr><td>IGST {}%</td><td class=\"num\">{}</td></tr>", milli_to_plain(t.gst_rate_bp * 10), fmt_inr(t.igst_paise))),
        _ => {}
    }
    let is_credit = s.kind == "credit_note";
    let mut client_lines = vec![esc(&s.client_name)];
    if !s.client_address.is_empty() {
        client_lines.push(esc(&s.client_address));
    }
    if !s.client_gstin.is_empty() {
        client_lines.push(format!("GSTIN {}", esc(&s.client_gstin)));
    }
    if !s.client_pan.is_empty() {
        client_lines.push(format!("PAN {}", esc(&s.client_pan)));
    }
    if !s.client_tan.is_empty() {
        client_lines.push(format!("TAN {}", esc(&s.client_tan)));
    }
    if !s.client_phone.is_empty() {
        client_lines.push(esc(&s.client_phone));
    }
    if !s.client_email.is_empty() {
        client_lines.push(esc(&s.client_email));
    }
    if !s.project_name.is_empty() {
        client_lines.push(format!("Project: {}", esc(&s.project_name)));
    }
    let mut firm_meta = Vec::new();
    if !s.company_gstin.is_empty() {
        firm_meta.push(format!("GSTIN {}", esc(&s.company_gstin)));
    }
    if !s.company_pan.is_empty() {
        firm_meta.push(format!("PAN {}", esc(&s.company_pan)));
    }
    if !s.company_coa.is_empty() {
        firm_meta.push(format!("COA {}", esc(&s.company_coa)));
    }
    let firm_meta = if firm_meta.is_empty() { String::new() } else { format!("<span class=\"meta\">{}</span><br/><br/>", firm_meta.join("<br/>")) };
    let mark = if !s.logo_square_b64.is_empty() {
        "<img src=\"company-logo-square\" style=\"width: 18mm; height: 18mm;\" /><br/><br/>".to_string()
    } else if !s.logo_wide_b64.is_empty() {
        "<img src=\"company-logo-wide\" style=\"width: 48mm; height: 12mm;\" /><br/><br/>".to_string()
    } else {
        String::new()
    };
    let payment = if is_credit || (s.bank_details.is_empty() && s.upi_uri.is_empty()) {
        String::new()
    } else {
        let mut body = esc(&s.bank_details);
        if !s.upi_uri.is_empty() {
            if !body.is_empty() {
                body.push_str("<br/>");
            }
            body.push_str("Scan to pay by UPI<br/><table style=\"border-collapse: collapse;\"><tr><td style=\"padding: 4pt 0 0 0;\"><img src=\"upi-qr\" style=\"width: 28mm; height: 28mm;\" /></td></tr></table>");
        }
        rail_block("PAYMENT", &body)
    };
    let mut terms = Vec::new();
    if s.kind == "proforma" {
        terms.push("This is a pro forma invoice. It is a request for payment and not a tax invoice.".to_string());
    }
    if s.scheme == "composition" {
        terms.push("Composition taxable person, not eligible to collect tax on supplies.".to_string());
    }
    if !s.notes.is_empty() {
        terms.push(esc(&s.notes));
    }
    let terms_label = if is_credit { "REASON" } else { "TERMS" };
    let terms = rail_block(terms_label, &terms.join("<br/>"));
    let thanks = if is_credit { "CREDIT APPLIED" } else { "THANK YOU<br/>FOR YOUR BUSINESS" };
    let against = if is_credit && !s.against.is_empty() { format!("<br/>Against {}", esc(&s.against)) } else { String::new() };
    let due = if s.due_date.is_empty() { String::new() } else { format!("<br/>Due: {}", esc(&display_date(&s.due_date))) };
    let supply_name = if s.scheme == "regular" && s.gst_mode == "inter" {
        gst_state(&s.client_gstin).map(|state| state.to_string()).or_else(|| {
            let state = s.client_state.trim();
            if state.is_empty() { None } else { Some(state.to_string()) }
        })
    } else if s.scheme == "regular" || s.scheme == "composition" {
        gst_state(&s.company_gstin).map(|state| state.to_string())
    } else {
        None
    };
    let supply = supply_name.map(|state| format!("<br/>Place of supply: {state}")).unwrap_or_default();
    let sac = if s.scheme == "regular" || s.scheme == "composition" { "<br/><span class=\"meta\">SAC 9983</span>".to_string() } else { String::new() };
    let sign = if s.architect_name.trim().is_empty() {
        String::new()
    } else {
        format!("<span class=\"sign\">{}</span>", esc(&s.architect_name))
    };
    let mut foot = vec![esc(&s.company_name)];
    if !s.company_address.is_empty() {
        foot.push(esc(&s.company_address));
    }
    let mut contact = Vec::new();
    if !s.company_phone.is_empty() {
        contact.push(esc(&s.company_phone));
    }
    if !s.company_email.is_empty() {
        contact.push(esc(&s.company_email));
    }
    if !contact.is_empty() {
        foot.push(contact.join(" · "));
    }
    format!(
        r#"<html><head><style>{css}
        body {{ margin: 0; padding: 0; }}
        table.sheet {{ width: 210mm; table-layout: fixed; border-collapse: collapse; }}
        td.rail {{ width: 30%; vertical-align: top; background-color: #ffffff; padding: 12pt 8pt 14pt 8pt; }}
        td.main {{ width: 70%; vertical-align: top; background-color: #ffffff; padding: 14pt 10pt 14pt 8pt; }}
        .kicker {{ {kicker} color: {accent}; }}
        .firm {{ {firm} }}
        .inv {{ {title_face} font-size: 26pt; text-align: right; }}
        .due {{ {due_face} font-size: 18pt; }}
        table.stack {{ width: 141mm; border-collapse: collapse; }}
        .lines {{ width: 141mm; border-collapse: separate; border-spacing: 0 1px; background-color: #1a1a1a; {table} }}
        .lines th, .lines td {{ padding: 4px 3px; background-color: #ffffff; }}
        .lines th {{ {head} }}
        .sums {{ width: 70mm; border-collapse: collapse; {table} }}
        .sums td {{ padding: 2pt 1pt; }}
        .boxed {{ border-collapse: separate; border-spacing: 0 1.25px; background-color: #1a1a1a; {table} }}
        .boxed td {{ background-color: #ffffff; padding: 4px 3px; {head} }}
        .sign {{ {firm} font-size: 14pt; }}
        .thanks {{ {firm} font-size: 13pt; }}
        .foot {{ {meta} border-top: 1px solid #1a1a1a; padding-top: 6pt; }}
        .right {{ text-align: right; }}
        </style></head><body>
        <table class="sheet"><tr>
        <td class="rail">{mark}<span class="firm">{company}</span><br/>{firm_meta}{invoice_to}{payment}{terms}</td>
        <td class="main" rowspan="2"><table class="stack"><tr><td class="inv">{title}<br/><span class="meta">Invoice no. <span class="id">{number}</span>{against}</span></td></tr><tr><td style="padding-top: 8pt;">Date: {date}{due}{supply}{sac}</td></tr><tr><td style="padding-top: 8pt;"><span class="kicker">Total due</span><br/><span class="due">{total}</span></td></tr><tr><td style="padding-top: 10pt;"><table class="lines"><tr><th style="width: 75mm; background-color: #ffffff; padding: 4px 3px;">DESCRIPTION</th><th class="num" style="width: 26mm; background-color: #ffffff; padding: 4px 3px;">PRICE</th><th class="num" style="width: 16mm; background-color: #ffffff; padding: 4px 3px;">QTY</th><th class="num" style="width: 24mm; background-color: #ffffff; padding: 4px 3px;">TOTAL</th></tr>{rows}</table></td></tr><tr><td style="padding-top: 8pt;"><table style="width: 141mm; border-collapse: collapse;"><tr><td style="width: 56mm; vertical-align: bottom;">{sign}</td><td style="width: 85mm;"><table class="sums"><tr><td>Subtotal</td><td class="num">{subtotal}</td></tr>{tax_rows}</table><table class="boxed" style="width: 70mm;"><tr><td>TOTAL</td><td class="num">{total}</td></tr></table></td></tr></table></td></tr><tr><td class="foot" style="padding-top: 10pt;">{foot}</td></tr></table></td>
        </tr><tr><td class="rail" style="vertical-align: bottom;"><span class="thanks">{thanks}</span></td></tr></table>
        </body></html>"#,
        css = crate::typography::css(),
        accent = crate::sheets::resolve_accent(&s.accent_hex),
        kicker = crate::typography::token_style("meta"),
        firm = crate::typography::token_style("firm"),
        title_face = crate::typography::token_style("title"),
        due_face = crate::typography::token_style("firm"),
        table = crate::typography::token_style("table"),
        head = crate::typography::token_style("table-head"),
        meta = crate::typography::token_style("meta"),
        mark = mark,
        company = esc(&s.company_name),
        firm_meta = firm_meta,
        invoice_to = rail_block("INVOICE TO", &client_lines.join("<br/>")),
        payment = payment,
        terms = terms,
        thanks = thanks,
        title = title,
        number = esc(&s.number),
        against = against,
        date = esc(&display_date(&s.issue_date)),
        due = due,
        supply = supply,
        sac = sac,
        total = fmt_inr(t.total_paise),
        rows = rows,
        sign = sign,
        subtotal = fmt_inr(t.subtotal_paise),
        tax_rows = tax_rows,
        foot = foot.join("<br/>"),
    )
}

fn render(s: &Snapshot) -> Result<pdf::Rendered, String> {
    let qr = if s.upi_uri.is_empty() || s.kind == "credit_note" { None } else { Some(s.upi_uri.as_str()) };
    let mut images = pdf::logo_images(&s.logo_square_b64, &s.logo_wide_b64);
    let overlay = match qr {
        Some(data) => match pdf::qr_png(data) {
            Ok(png) => {
                images.insert("upi-qr".to_string(), printpdf::Base64OrRaw::Raw(png));
                None
            }
            Err(_) => Some(data),
        },
        None => None,
    };
    // 30% of A4. A hairline is painted on this edge; the fixed table meets it.
    pdf::render_invoice_sheet(&invoice_html(s), &pdf::bundled_fonts(), &images, overlay, 63.0 * 72.0 / 25.4)
}

// ---------- issue ----------

#[cfg(test)]
pub fn issue(conn: &mut Connection, id: i64) -> Result<String, String> {
    issue_by(conn, None, id)
}

pub fn issue_by(conn: &mut Connection, actor: Option<i64>, id: i64) -> Result<String, String> {
    let detail = get(conn, id)?;
    if detail.status != "draft" {
        return Err("Only drafts can be issued.".into());
    }
    validate_header(&detail.input)?;
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let number = allocate_number(&tx, &detail.input.kind, &detail.input.issue_date)?;
    let snapshot = build_snapshot(&tx, &detail.input, &number)?;
    let rendered = render(&snapshot)?;
    let sha = sha256_hex(&rendered.bytes);
    let key = format!("issued-pdfs/{}.pdf", number.replace('/', "_"));
    crate::store::put(&tx, &key, &rendered.bytes)?;
    let snapshot_json = serde_json::to_string(&snapshot).map_err(|e| e.to_string())?;
    let res = tx
        .execute(
            "UPDATE invoices SET status='issued', number=?1, snapshot_json=?2, pdf_key=?3, pdf_sha256=?4, issued_at=datetime('now'),
             subtotal_paise=?5, tax_paise=?6, total_paise=?7 WHERE id=?8 AND status='draft'",
            params![number, snapshot_json, key, sha, snapshot.totals.subtotal_paise, snapshot.totals.tax_paise, snapshot.totals.total_paise, id],
        )
        .map_err(|e| e.to_string())
        .and_then(|n| if n == 1 { Ok(()) } else { Err("Invoice was already issued.".to_string()) })
        .and_then(|_| crate::ledger::post_invoice(&tx, actor, id).map(|_| ()))
        .and_then(|_| tx.commit().map_err(|e| e.to_string()));
    res?;
    Ok(number)
}

pub fn pdf_bytes(conn: &Connection, id: i64) -> Result<Vec<u8>, String> {
    let (status, key, sha): (String, Option<String>, Option<String>) = conn
        .query_row("SELECT status, pdf_key, pdf_sha256 FROM invoices WHERE id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
        .map_err(|_| "Invoice not found.".to_string())?;
    if status == "draft" {
        let d = get(conn, id)?;
        let snap = build_snapshot(conn, &d.input, "DRAFT")?;
        return Ok(render(&snap)?.bytes);
    }
    let bytes = crate::store::get(conn, &key.ok_or("Missing PDF.")?)?;
    if Some(sha256_hex(&bytes)) != sha {
        return Err("The stored PDF failed its integrity check.".into());
    }
    Ok(bytes)
}

// ---------- cancellation & credit notes ----------

/// Cancels an issued document. The number stays used and the PDF stays on file; the document just stops counting.
pub fn cancel(conn: &mut Connection, actor: i64, id: i64, reason: &str) -> Result<(), String> {
    if reason.trim().is_empty() {
        return Err("Give a reason for the cancellation.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let (status, number): (String, Option<String>) = tx
        .query_row("SELECT status, number FROM invoices WHERE id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|_| "Invoice not found.".to_string())?;
    if status != "issued" {
        return Err("Only issued documents can be cancelled (a draft can simply be edited).".into());
    }
    let b = crate::receipts::balance(&tx, id)?;
    if b.received_paise + b.tds_paise > 0 || b.credited_paise > 0 || b.written_off_paise > 0 {
        return Err("This invoice has payments, credit notes or a write-off against it. Reverse those first, or issue a credit note instead.".into());
    }
    let converted: i64 = tx
        .query_row("SELECT COUNT(*) FROM invoices WHERE source_proforma_id = ?1 AND status <> 'cancelled'", params![id], |r| r.get(0))
        .map_err(|e| e.to_string())?;
    if converted > 0 {
        return Err("A tax invoice has been raised from this pro forma. Cancel that invoice first.".into());
    }
    tx.execute("UPDATE invoices SET status='cancelled', cancel_reason=?1, cancelled_at=datetime('now') WHERE id=?2 AND status='issued'", params![reason.trim(), id])
        .map_err(|e| e.to_string())?;
    crate::ledger::reverse_source(&tx, Some(actor), "invoice", id, reason)?;
    db::audit(&tx, Some(actor), "invoice_cancelled", &format!("{}: {}", number.unwrap_or_default(), reason.trim()));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(())
}

#[derive(Serialize)]
pub struct CreditNoteRow {
    pub id: i64,
    pub number: String,
    pub issue_date: String,
    pub reason: String,
    pub total_display: String,
}

pub fn list_credit_notes(conn: &Connection, invoice_id: i64) -> Result<Vec<CreditNoteRow>, String> {
    let mut stmt = conn
        .prepare("SELECT id, number, issue_date, reason, total_paise FROM credit_notes WHERE invoice_id = ?1 ORDER BY id")
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![invoice_id], |r| Ok(CreditNoteRow { id: r.get(0)?, number: r.get(1)?, issue_date: r.get(2)?, reason: r.get(3)?, total_display: fmt_inr(r.get(4)?) }))
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

/// Issues a credit note against an issued tax invoice. It reduces the amount still owed and is never edited afterwards.
pub fn issue_credit_note(conn: &mut Connection, actor: i64, invoice_id: i64, issue_date: &str, reason: &str, lines: Vec<LineInput>) -> Result<String, String> {
    validate_date(issue_date)?;
    if reason.trim().is_empty() {
        return Err("Give the reason for the credit note.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let (status, kind, snap_json): (String, String, Option<String>) = tx
        .query_row("SELECT status, kind, snapshot_json FROM invoices WHERE id = ?1", params![invoice_id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
        .map_err(|_| "Invoice not found.".to_string())?;
    if status != "issued" || kind != "tax_invoice" {
        return Err("Credit notes can only be raised against issued invoices.".into());
    }
    let mut snap: Snapshot = serde_json::from_str(&snap_json.ok_or("The invoice has no stored snapshot.")?).map_err(|e| e.to_string())?;
    let logos = crate::commands::company_logos_b64(&tx);
    snap.logo_square_b64 = logos.square;
    snap.logo_wide_b64 = logos.wide;
    let input = InvoiceInput {
        id: None, project_id: 0, kind: "tax_invoice".into(), issue_date: issue_date.into(), due_date: String::new(),
        gst_mode: snap.gst_mode.clone(), gst_rate: String::new(), notes: String::new(), lines,
    };
    let totals = calculate(&input, &snap.scheme)?;
    if totals.total_paise <= 0 {
        return Err("The credit note total must be more than zero.".into());
    }
    let b = crate::receipts::balance(&tx, invoice_id)?;
    if totals.total_paise > b.balance_paise {
        return Err(format!(
            "A credit note cannot exceed the amount still owed on the invoice ({}). Reverse a payment first if a refund is involved.",
            b.balance_display
        ));
    }
    let number = allocate_number(&tx, "credit_note", issue_date)?;
    snap.against = std::mem::replace(&mut snap.number, number.clone());
    snap.kind = "credit_note".into();
    snap.issue_date = issue_date.into();
    snap.due_date = String::new();
    snap.notes = reason.trim().to_string();
    snap.upi_uri = String::new();
    snap.totals = totals.clone();
    let rendered = render(&snap)?;
    let sha = sha256_hex(&rendered.bytes);
    let key = format!("credit-notes/{}.pdf", number.replace('/', "_"));
    crate::store::put(&tx, &key, &rendered.bytes)?;
    let snapshot_json = serde_json::to_string(&snap).map_err(|e| e.to_string())?;
    let res = tx
        .execute(
            "INSERT INTO credit_notes (number, invoice_id, issue_date, reason, gst_mode, gst_rate_bp, subtotal_paise, tax_paise, total_paise, snapshot_json, pdf_key, pdf_sha256, issued_by)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13)",
            params![number, invoice_id, issue_date, reason.trim(), totals.gst_mode, totals.gst_rate_bp, totals.subtotal_paise, totals.tax_paise, totals.total_paise, snapshot_json, key, sha, actor],
        )
        .map_err(|e| e.to_string())
        .and_then(|_| {
            let cn_id = tx.last_insert_rowid();
            crate::ledger::post_credit_note(&tx, Some(actor), cn_id)?;
            db::audit(&tx, Some(actor), "credit_note_issued", &format!("{number} against invoice {invoice_id}"));
            tx.commit().map_err(|e| e.to_string())
        });
    res?;
    Ok(number)
}

pub fn credit_note_pdf(conn: &Connection, id: i64) -> Result<Vec<u8>, String> {
    let (key, sha): (String, String) = conn
        .query_row("SELECT pdf_key, pdf_sha256 FROM credit_notes WHERE id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|_| "Credit note not found.".to_string())?;
    let bytes = crate::store::get(conn, &key)?;
    if sha256_hex(&bytes) != sha {
        return Err("The stored PDF failed its integrity check.".into());
    }
    Ok(bytes)
}

// ---------- commands ----------

pub fn invoice_cancel(state: HostState<'_>, id: i64, reason: String) -> Result<(), String> {
    let uid = require(&state, Permission::Issue)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    cancel(&mut conn, uid, id, &reason)
}

pub fn credit_note_issue(state: HostState<'_>, invoice_id: i64, issue_date: String, reason: String, lines: Vec<LineInput>) -> Result<String, String> {
    let uid = require(&state, Permission::Issue)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    issue_credit_note(&mut conn, uid, invoice_id, &issue_date, &reason, lines)
}

pub fn credit_note_list(state: HostState<'_>, invoice_id: i64) -> Result<Vec<CreditNoteRow>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list_credit_notes(&conn, invoice_id)
}

pub fn credit_note_pdf_data(state: HostState<'_>, id: i64) -> Result<String, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    Ok(base64::engine::general_purpose::STANDARD.encode(credit_note_pdf(&conn, id)?))
}

pub fn invoice_calculate(state: HostState<'_>, input: InvoiceInput) -> Result<Totals, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    calculate(&input, &company_scheme(&conn)?)
}

pub fn invoice_list(state: HostState<'_>) -> Result<Vec<InvoiceRow>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list(&conn)
}

pub fn invoice_get(state: HostState<'_>, id: i64) -> Result<InvoiceDetail, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    get(&conn, id)
}

pub fn invoice_save_draft(state: HostState<'_>, input: InvoiceInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    let id = save_draft(&mut conn, &input)?;
    db::audit(&conn, Some(uid), "invoice_draft_saved", &format!("invoice {id}"));
    Ok(id)
}

pub fn invoice_issue(state: HostState<'_>, id: i64) -> Result<String, String> {
    let uid = require(&state, Permission::Issue)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    let number = issue_by(&mut conn, Some(uid), id)?;
    db::audit(&conn, Some(uid), "invoice_issued", &format!("invoice {id} as {number}"));
    Ok(number)
}

pub fn invoice_convert_proforma(state: HostState<'_>, id: i64, today: String) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    let new_id = convert_proforma(&mut conn, id, &today)?;
    db::audit(&conn, Some(uid), "proforma_converted", &format!("invoice {id} -> draft {new_id}"));
    Ok(new_id)
}

/// Returns the PDF as base64 (a rendered draft preview, or the stored issued PDF).
pub fn invoice_pdf(state: HostState<'_>, id: i64) -> Result<String, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let bytes = pdf_bytes(&conn, id)?;
    Ok(base64::engine::general_purpose::STANDARD.encode(bytes))
}

pub(crate) use office_core::hash as sha2_lite;

#[cfg(test)]
mod tests {
    use super::*;

    fn line(d: &str, q: &str, r: &str) -> LineInput {
        LineInput { description: d.into(), qty: q.into(), rate: r.into() }
    }

    fn input(mode: &str, rate: &str, lines: Vec<LineInput>) -> InvoiceInput {
        InvoiceInput { id: None, project_id: 1, kind: "tax_invoice".into(), issue_date: "2026-10-12".into(), due_date: "".into(), gst_mode: mode.into(), gst_rate: rate.into(), notes: "".into(), lines }
    }

    fn db_with_project() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('o','x','owner')", []).unwrap();
        c.execute("INSERT INTO clients (name, address) VALUES ('Acme','Pune')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1,'Villa')", []).unwrap();
        c.execute("UPDATE company_profile SET name='Studio X', gstin='27ABCDE1234F1Z5', gst_scheme='regular'", []).unwrap();
        c
    }

    #[test]
    fn decimals_parse_exactly() {
        assert_eq!(parse_decimal("12500.50", 2).unwrap(), 1_250_050);
        assert_eq!(parse_decimal("1,25,000", 2).unwrap(), 12_500_000);
        assert_eq!(parse_decimal("2.5", 3).unwrap(), 2500);
        assert!(parse_decimal("1.234", 2).is_err());
        assert!(parse_decimal("-5", 2).is_err());
        assert!(parse_decimal("abc", 2).is_err());
        assert!(parse_decimal("", 2).is_err());
    }

    #[test]
    fn inr_grouping() {
        assert_eq!(fmt_inr(12_345_678), "₹1,23,456.78");
        assert_eq!(fmt_inr(99_900), "₹999.00");
        assert_eq!(fmt_inr(100_000), "₹1,000.00");
        assert_eq!(fmt_inr(0), "₹0.00");
    }

    #[test]
    fn financial_year_boundaries() {
        assert_eq!(financial_year("2026-03-31").unwrap(), "2025-26");
        assert_eq!(financial_year("2026-04-01").unwrap(), "2026-27");
        assert_eq!(financial_year("2099-12-31").unwrap(), "2099-00");
        assert!(financial_year("2026-02-30").is_err());
    }

    #[test]
    fn intra_state_gst_splits_exactly() {
        let t = calculate(&input("intra", "18", vec![line("Fee", "1", "10001.00")]), "regular").unwrap();
        assert_eq!(t.subtotal_paise, 1_000_100);
        assert_eq!(t.cgst_paise + t.sgst_paise, t.tax_paise);
        assert_eq!(t.tax_paise, 180_018);
        assert_eq!(t.total_paise, 1_180_118);
        assert_eq!(t.igst_paise, 0);
    }

    #[test]
    fn inter_state_and_no_gst() {
        let t = calculate(&input("inter", "18", vec![line("Fee", "2", "500")]), "regular").unwrap();
        assert_eq!((t.igst_paise, t.cgst_paise, t.total_paise), (18_000, 0, 118_000));
        let n = calculate(&input("none", "", vec![line("Fee", "1", "500")]), "none").unwrap();
        assert_eq!((n.tax_paise, n.total_paise), (0, 50_000));
    }

    #[test]
    fn composition_charges_nothing_but_tracks_six_percent() {
        let t = calculate(&input("intra", "18", vec![line("Fee", "1", "100000")]), "composition").unwrap();
        assert_eq!((t.tax_paise, t.total_paise, t.gst_mode.as_str()), (0, 10_000_000, "none"));
        assert_eq!(t.composition_paise, 600_000);
    }

    #[test]
    fn scheme_overrides_whatever_the_form_sends() {
        // Unregistered firm: a client-side "inter" request must not add tax.
        let n = calculate(&input("inter", "18", vec![line("Fee", "1", "1000")]), "none").unwrap();
        assert_eq!((n.tax_paise, n.gst_mode.as_str()), (0, "none"));
        // Regular: 18% total, 9 + 9 intra-state.
        let r = calculate(&input("none", "0", vec![line("Fee", "1", "1000")]), "regular").unwrap();
        assert_eq!((r.cgst_paise, r.sgst_paise, r.gst_mode.as_str()), (9_000, 9_000, "intra"));
    }

    #[test]
    fn rejects_bad_lines() {
        assert!(calculate(&input("none", "", vec![]), "none").is_err());
        assert!(calculate(&input("none", "", vec![line(" ", "1", "5")]), "none").is_err());
        assert!(calculate(&input("none", "", vec![line("x", "abc", "5")]), "none").is_err());
    }

    #[test]
    fn issue_allocates_sequential_numbers_and_freezes_invoice() {
        let mut c = db_with_project();
        let dir = std::env::temp_dir().join(format!("ao_test_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let a = save_draft(&mut c, &input("intra", "18", vec![line("Stage 1", "1", "100000")])).unwrap();
        let b = save_draft(&mut c, &input("none", "", vec![line("Stage 2", "1", "5000")])).unwrap();
        assert_eq!(issue(&mut c, a).unwrap(), "INV/2026-27/0001");
        assert_eq!(issue(&mut c, b).unwrap(), "INV/2026-27/0002");
        // issued: cannot be edited, re-issued or altered at the SQL level
        let mut edit = input("none", "", vec![line("Changed", "1", "1")]);
        edit.id = Some(a);
        assert!(save_draft(&mut c, &edit).is_err());
        assert!(issue(&mut c, a).is_err());
        assert!(c.execute("UPDATE invoices SET total_paise = 1 WHERE id = ?1", params![a]).is_err());
        assert!(c.execute("DELETE FROM invoice_lines WHERE invoice_id = ?1", params![a]).is_err());
        assert!(c.execute("DELETE FROM invoices WHERE id = ?1", params![a]).is_err());
        // stored PDF verifies, and later master-data edits don't change it
        c.execute("UPDATE clients SET name = 'Renamed'", []).unwrap();
        let bytes = pdf_bytes(&c, a).unwrap();
        assert!(bytes.starts_with(b"%PDF"));
        // the stored document cannot be edited or removed through the database...
        assert!(c.execute("UPDATE stored_files SET data = x'00'", []).is_err());
        assert!(c.execute("DELETE FROM stored_files", []).is_err());
        // ...and tampering that bypasses those guards is still detected on read
        c.execute_batch("DROP TRIGGER stored_files_immutable; UPDATE stored_files SET data = x'74616d7065726564';").unwrap();
        assert!(pdf_bytes(&c, a).unwrap_err().contains("integrity"));
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn numbers_restart_each_financial_year() {
        let mut c = db_with_project();
        let dir = std::env::temp_dir().join(format!("ao_test_fy_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let mut early = input("none", "", vec![line("x", "1", "1")]);
        early.issue_date = "2027-03-31".into();
        let mut late = early.clone();
        late.issue_date = "2027-04-01".into();
        let a = save_draft(&mut c, &early).unwrap();
        let b = save_draft(&mut c, &late).unwrap();
        assert_eq!(issue(&mut c, a).unwrap(), "INV/2026-27/0001");
        assert_eq!(issue(&mut c, b).unwrap(), "INV/2027-28/0001");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn proforma_converts_once_into_a_draft_tax_invoice() {
        let mut c = db_with_project();
        let dir = std::env::temp_dir().join(format!("ao_pi_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let mut pi = input("intra", "18", vec![line("Advance", "1", "25000")]);
        pi.kind = "proforma".into();
        let id = save_draft(&mut c, &pi).unwrap();
        assert!(convert_proforma(&mut c, id, "2026-10-13").is_err(), "drafts cannot be converted");
        assert_eq!(issue(&mut c, id).unwrap(), "PI/2026-27/0001");
        let tax = convert_proforma(&mut c, id, "2026-10-13").unwrap();
        assert!(convert_proforma(&mut c, id, "2026-10-13").unwrap_err().contains("already been converted"));
        let d = get(&c, tax).unwrap();
        assert_eq!((d.status.as_str(), d.input.kind.as_str(), d.input.lines.len()), ("draft", "tax_invoice", 1));
        // tax invoices use their own number series
        assert_eq!(issue(&mut c, tax).unwrap(), "INV/2026-27/0001");
        let rows = list(&c).unwrap();
        assert_eq!(rows.iter().find(|r| r.id == id).unwrap().converted_to.as_deref(), Some("INV/2026-27/0001"));
        // a tax invoice is not convertible
        assert!(convert_proforma(&mut c, tax, "2026-10-13").is_err());
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn html_escapes_user_text() {
        let c = db_with_project();
        let mut i = input("none", "", vec![line("<b>x</b> & y", "1", "1")]);
        i.notes = "<script>".into();
        let html = invoice_html(&build_snapshot(&c, &i, "N").unwrap());
        assert!(html.contains("&lt;b&gt;x&lt;/b&gt; &amp; y"));
        assert!(!html.contains("<script>"));
        assert!(html.contains("INVOICE TO") && html.contains("Total due") && html.contains("DESCRIPTION"));
    }

    #[test]
    fn a_regular_invoice_names_the_sac_and_the_place_of_supply() {
        let c = db_with_project();
        c.execute("UPDATE clients SET gstin = '29ABCDE1234F1Z5' WHERE id = 1", []).unwrap();
        let fee = input("inter", "", vec![line("Fee", "1", "1000")]);
        let inter = invoice_html(&build_snapshot(&c, &fee, "INV/1").unwrap());
        assert!(inter.contains("SAC 9983") && inter.contains("Place of supply: Karnataka") && inter.contains("IGST"));
        let intra = invoice_html(&build_snapshot(&c, &input("intra", "", vec![line("Fee", "1", "1000")]), "INV/2").unwrap());
        assert!(intra.contains("Place of supply: Maharashtra") && intra.contains("CGST") && intra.contains("SGST"));
        let mut proforma = input("intra", "", vec![line("Advance", "1", "1000")]);
        proforma.kind = "proforma".into();
        let request = invoice_html(&build_snapshot(&c, &proforma, "PI/1").unwrap());
        assert!(request.contains("PRO FORMA") && request.contains("not a tax invoice") && request.contains("SAC 9983"));
        c.execute("UPDATE company_profile SET gst_scheme = 'none', gstin = ''", []).unwrap();
        let plain = invoice_html(&build_snapshot(&c, &fee, "INV/3").unwrap());
        assert!(!plain.contains("SAC 9983") && !plain.contains("Place of supply"));
    }
    #[test]
    fn cancel_blocks_when_money_is_involved_and_freezes_the_row() {
        let mut c = db_with_project();
        let dir = std::env::temp_dir().join(format!("ao_cx_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let id = save_draft(&mut c, &input("none", "", vec![line("Fee", "1", "1000")])).unwrap();
        assert!(cancel(&mut c, 1, id, "x").is_err(), "drafts are not cancelled");
        issue(&mut c, id).unwrap();
        assert!(cancel(&mut c, 1, id, " ").is_err());
        cancel(&mut c, 1, id, "Raised in error").unwrap();
        assert_eq!(get(&c, id).unwrap().status, "cancelled");
        assert!(c.execute("UPDATE invoices SET total_paise = 1 WHERE id = ?1", params![id]).is_err());
        assert!(cancel(&mut c, 1, id, "again").is_err());
        // a paid invoice cannot be cancelled
        let paid = save_draft(&mut c, &input("none", "", vec![line("Fee", "1", "1000")])).unwrap();
        issue(&mut c, paid).unwrap();
        crate::receipts::record(&mut c, 1, &crate::receipts::ReceiptInput { invoice_id: paid, received_date: "2026-10-20".into(), amount: "500".into(), tds: "".into(), tds_section: "".into(), mode: "bank".into(), reference: "".into(), notes: "".into(), bank_account_id: None }).unwrap();
        assert!(cancel(&mut c, 1, paid, "oops").unwrap_err().contains("payments"));
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn credit_note_reduces_the_balance_and_is_capped() {
        let mut c = db_with_project();
        let dir = std::env::temp_dir().join(format!("ao_cn_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let id = save_draft(&mut c, &input("intra", "", vec![line("Fee", "1", "10000")])).unwrap();
        issue(&mut c, id).unwrap();
        // invoice total 11,800 (10,000 + 18%). Credit 2,000 + 18% = 2,360.
        let n = issue_credit_note(&mut c, 1, id, "2026-10-20", "Scope reduced", vec![line("Reduction", "1", "2000")]).unwrap();
        assert_eq!(n, "CN/2026-27/0001");
        let b = crate::receipts::balance(&c, id).unwrap();
        assert_eq!((b.credited_paise, b.balance_paise), (236_000, 944_000));
        assert_eq!(list_credit_notes(&c, id).unwrap().len(), 1);
        assert!(credit_note_pdf(&c, list_credit_notes(&c, id).unwrap()[0].id).unwrap().starts_with(b"%PDF"));
        // more than what is still owed is refused; nothing is consumed by the failed attempt
        assert!(issue_credit_note(&mut c, 1, id, "2026-10-21", "Too much", vec![line("x", "1", "9000")]).unwrap_err().contains("cannot exceed"));
        assert!(issue_credit_note(&mut c, 1, id, "2026-10-21", " ", vec![line("x", "1", "1")]).is_err());
        assert!(c.execute("DELETE FROM credit_notes", []).is_err());
        // a credited invoice cannot be cancelled
        assert!(cancel(&mut c, 1, id, "x").unwrap_err().contains("credit notes"));
        // the invoice list shows the credit
        let row = list(&c).unwrap().into_iter().find(|r| r.id == id).unwrap();
        assert_eq!((row.balance_paise, row.credited_display.as_str()), (944_000, "₹2,360.00"));
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn upi_qr_is_rendered_when_a_upi_id_is_set() {
        let mut c = db_with_project();
        c.execute("UPDATE company_profile SET upi_id = 'studio@bank'", []).unwrap();
        let snap = build_snapshot(&c, &input("none", "", vec![line("Fee", "1", "1000")]), "INV/2026-27/0001").unwrap();
        assert!(snap.upi_uri.starts_with("upi://pay?pa=studio%40bank&pn=Studio%20X&am=1180.00&cu=INR"));
        let html = invoice_html(&snap);
        assert!(html.contains("pay by UPI"));
        let r = render(&snap).unwrap();
        assert!(r.bytes.starts_with(b"%PDF"));
        if let Ok(dir) = std::env::var("AO_PREVIEW_DIR") {
            std::fs::write(std::path::Path::new(&dir).join("invoice_qr.pdf"), &r.bytes).unwrap();
        }
        // credit notes never carry a QR
        let mut cn = build_snapshot(&c, &input("none", "", vec![line("Fee", "1", "1000")]), "CN/1").unwrap();
        cn.kind = "credit_note".into();
        assert!(!invoice_html(&cn).contains("pay by UPI"));
    }
}
