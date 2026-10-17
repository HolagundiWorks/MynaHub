//! Client statement, project fee position, and the project report. All three read records already in the file.

use crate::auth::Permission;
use crate::billing::fmt_inr;
use crate::commands::{HostState, require, INTERNAL};
use rusqlite::{params, Connection};
use serde::Serialize;

#[derive(Serialize)]
pub struct StatementLine {
    pub date: String,
    pub number: String,
    pub project: String,
    pub total_display: String,
    pub received_display: String,
    pub tds_display: String,
    pub credited_display: String,
    pub written_off_display: String,
    pub balance_display: String,
    pub due_date: String,
    pub bucket: String,
}

#[derive(Serialize)]
pub struct ReceiptLine {
    pub date: String,
    pub number: String,
    pub invoice: String,
    pub amount_display: String,
    pub tds_display: String,
    pub section: String,
}

#[derive(Serialize)]
pub struct AgeBand {
    pub bucket: String,
    pub balance_display: String,
}

#[derive(Serialize)]
pub struct ClientStatement {
    pub client_name: String,
    pub as_of: String,
    pub invoices: Vec<StatementLine>,
    pub receipts: Vec<ReceiptLine>,
    pub ageing: Vec<AgeBand>,
    pub total_display: String,
    pub received_display: String,
    pub tds_display: String,
    pub credited_display: String,
    pub balance_display: String,
}

#[derive(Serialize)]
pub struct FeeRow {
    pub client: String,
    pub project: String,
    pub consultancy: String,
    pub fee_display: String,
    pub billed_display: String,
    pub received_display: String,
    pub outstanding_display: String,
    pub next_stage: String,
    pub next_amount_display: String,
}

#[derive(Serialize)]
pub struct FeePosition {
    pub rows: Vec<FeeRow>,
    pub fee_display: String,
    pub billed_display: String,
    pub received_display: String,
    pub outstanding_display: String,
}

pub fn client_statement(conn: &Connection, client_id: i64, as_of: &str) -> Result<ClientStatement, String> {
    let as_of = as_of.trim();
    if ymd(as_of).is_none() {
        return Err("Enter the date as year-month-day.".into());
    }
    let client_name: String = conn
        .query_row("SELECT name FROM clients WHERE id = ?1", params![client_id], |r| r.get(0))
        .map_err(|_| "Choose a client.".to_string())?;
    let mut stmt = conn
        .prepare(
            "SELECT i.issue_date, COALESCE(i.number, ''), p.name, i.total_paise, b.received_paise, b.tds_paise, b.credited_paise, b.written_off_paise, i.due_date
             FROM invoices i
             JOIN invoice_balances b ON b.invoice_id = i.id
             JOIN projects p ON p.id = i.project_id
             WHERE p.client_id = ?1 AND i.kind = 'tax_invoice' AND i.status = 'issued'
             ORDER BY i.issue_date, i.id",
        )
        .map_err(|e| e.to_string())?;
    let found = stmt
        .query_map(params![client_id], |r| {
            Ok((
                r.get::<_, String>(0)?,
                r.get::<_, String>(1)?,
                r.get::<_, String>(2)?,
                r.get::<_, i64>(3)?,
                r.get::<_, i64>(4)?,
                r.get::<_, i64>(5)?,
                r.get::<_, i64>(6)?,
                r.get::<_, i64>(7)?,
                r.get::<_, String>(8)?,
            ))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    let mut invoices = Vec::new();
    let mut bands = [0i64; 5];
    let (mut total, mut received, mut tds, mut credited, mut balance) = (0i64, 0i64, 0i64, 0i64, 0i64);
    for (date, number, project, gross, paid, tax, credit, written, due) in found {
        let open = gross - paid - tax - credit - written;
        let bucket = age_bucket(&due, as_of, open);
        if open > 0 {
            bands[band_index(bucket)] += open;
        }
        total += gross;
        received += paid;
        tds += tax;
        credited += credit;
        balance += open;
        invoices.push(StatementLine {
            date,
            number,
            project,
            total_display: fmt_inr(gross),
            received_display: fmt_inr(paid),
            tds_display: fmt_inr(tax),
            credited_display: fmt_inr(credit),
            written_off_display: fmt_inr(written),
            balance_display: fmt_inr(open),
            due_date: due,
            bucket: bucket.to_string(),
        });
    }
    let mut receipts_stmt = conn
        .prepare(
            "SELECT r.received_date, r.number, COALESCE(i.number, ''), r.amount_paise, r.tds_paise, r.tds_section
             FROM effective_receipts r
             JOIN invoices i ON i.id = r.invoice_id
             JOIN projects p ON p.id = i.project_id
             WHERE p.client_id = ?1 AND i.kind = 'tax_invoice' AND i.status = 'issued'
             ORDER BY r.received_date, r.id",
        )
        .map_err(|e| e.to_string())?;
    let receipts = receipts_stmt
        .query_map(params![client_id], |r| {
            Ok(ReceiptLine {
                date: r.get(0)?,
                number: r.get(1)?,
                invoice: r.get(2)?,
                amount_display: fmt_inr(r.get(3)?),
                tds_display: fmt_inr(r.get(4)?),
                section: r.get(5)?,
            })
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    let labels = ["Current", "1–30 days", "31–60 days", "61–90 days", "Over 90 days"];
    let ageing = labels
        .iter()
        .zip(bands)
        .map(|(bucket, amount)| AgeBand { bucket: (*bucket).to_string(), balance_display: fmt_inr(amount) })
        .collect();
    Ok(ClientStatement {
        client_name,
        as_of: as_of.to_string(),
        invoices,
        receipts,
        ageing,
        total_display: fmt_inr(total),
        received_display: fmt_inr(received),
        tds_display: fmt_inr(tds),
        credited_display: fmt_inr(credited),
        balance_display: fmt_inr(balance),
    })
}

pub fn fee_position(conn: &Connection) -> Result<FeePosition, String> {
    let mut stmt = conn
        .prepare(
            "SELECT p.id, c.name, p.name, p.consultancy_type,
                    (SELECT d.id FROM documents d WHERE d.project_id = p.id AND d.kind = 'agreement' AND d.status IN ('issued','signed') ORDER BY d.id DESC LIMIT 1),
                    (SELECT d.fee_paise FROM documents d WHERE d.project_id = p.id AND d.kind = 'agreement' AND d.status IN ('issued','signed') ORDER BY d.id DESC LIMIT 1),
                    COALESCE((SELECT SUM(i.total_paise) FROM invoices i WHERE i.project_id = p.id AND i.kind = 'tax_invoice' AND i.status = 'issued'), 0),
                    COALESCE((SELECT SUM(b.received_paise + b.tds_paise) FROM invoice_balances b JOIN invoices i ON i.id = b.invoice_id WHERE i.project_id = p.id AND i.kind = 'tax_invoice' AND i.status = 'issued'), 0),
                    COALESCE((SELECT SUM(b.total_paise - b.received_paise - b.tds_paise - b.credited_paise - b.written_off_paise) FROM invoice_balances b JOIN invoices i ON i.id = b.invoice_id WHERE i.project_id = p.id AND i.kind = 'tax_invoice' AND i.status = 'issued'), 0)
             FROM projects p JOIN clients c ON c.id = p.client_id
             WHERE p.archived = 0
             ORDER BY c.name, p.name",
        )
        .map_err(|e| e.to_string())?;
    let found = stmt
        .query_map([], |r| {
            Ok((
                r.get::<_, i64>(0)?,
                r.get::<_, String>(1)?,
                r.get::<_, String>(2)?,
                r.get::<_, String>(3)?,
                r.get::<_, Option<i64>>(4)?,
                r.get::<_, Option<i64>>(5)?,
                r.get::<_, i64>(6)?,
                r.get::<_, i64>(7)?,
                r.get::<_, i64>(8)?,
            ))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    let mut rows = Vec::new();
    let (mut fee_sum, mut billed_sum, mut received_sum, mut open_sum) = (0i64, 0i64, 0i64, 0i64);
    for (_project_id, client, project, consultancy, doc_id, fee, billed, received, outstanding) in found {
        let fee = fee.unwrap_or(0);
        let (next_stage, next_amount) = next_milestone(conn, doc_id)?;
        fee_sum += fee;
        billed_sum += billed;
        received_sum += received;
        open_sum += outstanding;
        rows.push(FeeRow {
            client,
            project,
            consultancy,
            fee_display: fmt_inr(fee),
            billed_display: fmt_inr(billed),
            received_display: fmt_inr(received),
            outstanding_display: fmt_inr(outstanding),
            next_stage: if next_stage.is_empty() { if doc_id.is_none() { "No issued agreement".into() } else { "Fee fully billed".into() } } else { next_stage },
            next_amount_display: fmt_inr(next_amount),
        });
    }
    Ok(FeePosition {
        rows,
        fee_display: fmt_inr(fee_sum),
        billed_display: fmt_inr(billed_sum),
        received_display: fmt_inr(received_sum),
        outstanding_display: fmt_inr(open_sum),
    })
}

fn next_milestone(conn: &Connection, doc_id: Option<i64>) -> Result<(String, i64), String> {
    let Some(doc_id) = doc_id else {
        return Ok((String::new(), 0));
    };
    let row = conn.query_row(
        "SELECT m.name, m.amount_paise FROM document_milestones m
         LEFT JOIN invoices i ON i.id = m.invoice_id
         WHERE m.document_id = ?1 AND (m.invoice_id IS NULL OR COALESCE(i.status, '') <> 'issued')
         ORDER BY m.position LIMIT 1",
        params![doc_id],
        |r| Ok((r.get::<_, String>(0)?, r.get::<_, i64>(1)?)),
    );
    match row {
        Ok(pair) => Ok(pair),
        Err(rusqlite::Error::QueryReturnedNoRows) => Ok((String::new(), 0)),
        Err(error) => Err(error.to_string()),
    }
}

fn age_bucket(due: &str, as_of: &str, balance: i64) -> &'static str {
    if balance <= 0 {
        return "Settled";
    }
    let Some(due_days) = ymd(due).map(|(y, m, d)| civil_days(y, m, d)) else {
        return "Current";
    };
    let Some(as_of_days) = ymd(as_of).map(|(y, m, d)| civil_days(y, m, d)) else {
        return "Current";
    };
    let late = as_of_days - due_days;
    if late <= 0 {
        "Current"
    } else if late <= 30 {
        "1–30 days"
    } else if late <= 60 {
        "31–60 days"
    } else if late <= 90 {
        "61–90 days"
    } else {
        "Over 90 days"
    }
}

fn band_index(bucket: &str) -> usize {
    match bucket {
        "1–30 days" => 1,
        "31–60 days" => 2,
        "61–90 days" => 3,
        "Over 90 days" => 4,
        _ => 0,
    }
}

fn ymd(value: &str) -> Option<(i32, u32, u32)> {
    let mut parts = value.split('-');
    let year: i32 = parts.next()?.parse().ok()?;
    let month: u32 = parts.next()?.parse().ok()?;
    let day: u32 = parts.next()?.parse().ok()?;
    if parts.next().is_some() || !(1..=12).contains(&month) || !(1..=31).contains(&day) {
        return None;
    }
    Some((year, month, day))
}

/// Days since the civil epoch. Howard Hinnant's algorithm.
fn civil_days(year: i32, month: u32, day: u32) -> i64 {
    let mut y = year as i64;
    let m = month as i64;
    let d = day as i64;
    if m <= 2 {
        y -= 1;
    }
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = y - era * 400;
    let doy = (153 * (if m > 2 { m - 3 } else { m + 9 }) + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146097 + doe - 719468
}

struct ProjectFacts {
    code: String,
    name: String,
    site: String,
    status: String,
    consultancy: String,
    client: String,
    client_address: String,
}

fn project_facts(conn: &Connection, project_id: i64) -> Result<ProjectFacts, String> {
    conn.query_row(
        "SELECT p.code, p.name, p.site_address, p.status, p.consultancy_type, c.name, c.address
         FROM projects p JOIN clients c ON c.id = p.client_id WHERE p.id = ?1",
        params![project_id],
        |r| {
            Ok(ProjectFacts {
                code: r.get(0)?,
                name: r.get(1)?,
                site: r.get(2)?,
                status: r.get(3)?,
                consultancy: r.get(4)?,
                client: r.get(5)?,
                client_address: r.get(6)?,
            })
        },
    )
    .map_err(|_| "Choose a project.".to_string())
}

fn status_label(status: &str) -> &str {
    match status {
        "on_hold" => "On hold",
        "completed" => "Completed",
        "active" => "Active",
        _ => status,
    }
}

fn hours_label(minutes: i64) -> String {
    if minutes <= 0 {
        return "No hours recorded.".into();
    }
    let hours = minutes / 60;
    let rest = minutes % 60;
    if hours == 0 {
        format!("{rest} min")
    } else if rest == 0 {
        format!("{hours} h")
    } else {
        format!("{hours} h {rest} min")
    }
}

fn kind_label(kind: &str) -> &str {
    match kind {
        "tax_invoice" => "Tax invoice",
        "proforma" => "Pro forma",
        "credit_note" => "Credit note",
        _ => kind,
    }
}

/// Editorial project report. Figures come from the issued agreement, invoice balances, time entries and transmittals.
/// A role without finance sees the project, the hours and the drawings, and not the fees.
pub fn project_report_html(conn: &Connection, project_id: i64, finance: bool) -> Result<String, String> {
    use crate::sheets::{self, esc};
    let facts = project_facts(conn, project_id)?;
    let (firm, address, email, phone, accent, letterhead): (String, String, String, String, String, String) = conn
        .query_row("SELECT name, address, email, phone, accent_hex, letterhead_style FROM company_profile WHERE id = 1", [], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?)))
        .map_err(|e| e.to_string())?;
    let prepared: String = conn.query_row("SELECT date('now')", [], |r| r.get(0)).unwrap_or_default();
    let mut identity = vec![format!("Client<br/>{}", esc(&facts.client))];
    if !facts.client_address.is_empty() {
        identity.push(esc(&facts.client_address));
    }
    if !facts.code.is_empty() {
        identity.push(format!("Project code<br/><span class=\"id\">{}</span>", esc(&facts.code)));
    }
    if !facts.site.is_empty() {
        identity.push(format!("Site<br/>{}", esc(&facts.site)));
    }
    identity.push(format!("Status<br/>{}", esc(status_label(&facts.status))));
    identity.push(format!("Consultancy<br/>{}", esc(&crate::clients::consultancy_label(&facts.consultancy))));
    let stage_plan = crate::stages::plan(conn, project_id).unwrap_or(crate::stages::StagePlan { phase: String::new(), floors: 1, stages: Vec::new(), note: String::new() });
    if !stage_plan.phase.is_empty() {
        identity.push(format!("Phase<br/>{}", esc(&stage_plan.phase)));
    }
    let stage_rows: Vec<Vec<String>> = stage_plan
        .stages
        .iter()
        .map(|stage| {
            let marked = if stage.marked_on.is_empty() { "Open".to_string() } else { sheets::display_date(&stage.marked_on) };
            vec![stage.name.clone(), format!("{}%", stage.share), marked, stage.payment.clone()]
        })
        .collect();
    let stage_table = if stage_rows.is_empty() {
        String::new()
    } else {
        format!("<br/>{}", sheets::schedule(&[70, 24, 36, 48], &["Stage", "Share", "Phase", "Payment"], &stage_rows, &[false, true, false, false]))
    };

    let minutes: i64 = conn
        .query_row("SELECT COALESCE(SUM(minutes), 0) FROM time_entries WHERE project_id = ?1 AND cancelled = 0", params![project_id], |r| r.get(0))
        .map_err(|e| e.to_string())?;

    let mut drawing_rows = Vec::new();
    let mut stmt = conn
        .prepare(
            "SELECT t.sent_date, t.recipient, i.drawing_number, i.title, i.revision, i.copies
             FROM transmittals t JOIN transmittal_items i ON i.transmittal_id = t.id
             WHERE t.project_id = ?1 AND t.cancelled = 0
             ORDER BY t.sent_date, t.id, i.position",
        )
        .map_err(|e| e.to_string())?;
    let found = stmt.query_map(params![project_id], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?, r.get::<_, String>(2)?, r.get::<_, String>(3)?, r.get::<_, String>(4)?, r.get::<_, i64>(5)?))).map_err(|e| e.to_string())?;
    for row in found {
        let (date, recipient, number, title, revision, copies) = row.map_err(|e| e.to_string())?;
        drawing_rows.push(vec![sheets::display_date(&date), recipient, number, title, revision, copies.to_string()]);
    }
    drop(stmt);
    let drawings = if drawing_rows.is_empty() {
        "No drawings have been recorded on a transmittal.".to_string()
    } else {
        "Sent on the transmittal log.".to_string()
    };
    let drawing_table = if drawing_rows.is_empty() {
        String::new()
    } else {
        format!("<br/>{}", sheets::schedule(&[28, 32, 24, 52, 18, 20], &["Sent", "To", "Number", "Title", "Rev", "Copies"], &drawing_rows, &[false, false, false, false, false, true]))
    };

    let fee_body = if !finance {
        ("Fee and invoice figures are on the accounts reports.".to_string(), String::new())
    } else {
        let (fee, billed, received, outstanding, stage): (i64, i64, i64, i64, String) = conn
            .query_row(
                "SELECT COALESCE((SELECT d.fee_paise FROM documents d WHERE d.project_id = p.id AND d.kind = 'agreement' AND d.status IN ('issued','signed') ORDER BY d.id DESC LIMIT 1), 0),
                        COALESCE((SELECT SUM(i.total_paise) FROM invoices i WHERE i.project_id = p.id AND i.kind = 'tax_invoice' AND i.status = 'issued'), 0),
                        COALESCE((SELECT SUM(b.received_paise + b.tds_paise) FROM invoice_balances b JOIN invoices i ON i.id = b.invoice_id WHERE i.project_id = p.id AND i.kind = 'tax_invoice' AND i.status = 'issued'), 0),
                        COALESCE((SELECT SUM(b.total_paise - b.received_paise - b.tds_paise - b.credited_paise - b.written_off_paise) FROM invoice_balances b JOIN invoices i ON i.id = b.invoice_id WHERE i.project_id = p.id AND i.kind = 'tax_invoice' AND i.status = 'issued'), 0),
                        COALESCE((SELECT d.id FROM documents d WHERE d.project_id = p.id AND d.kind = 'agreement' AND d.status IN ('issued','signed') ORDER BY d.id DESC LIMIT 1), 0)
                 FROM projects p WHERE p.id = ?1",
                params![project_id],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get::<_, i64>(4)?.to_string())),
            )
            .map_err(|e| e.to_string())?;
        let doc_id = stage.parse::<i64>().unwrap_or(0);
        let (next_stage, next_amount) = next_milestone(conn, if doc_id == 0 { None } else { Some(doc_id) })?;
        let mut lines = vec![
            format!("Agreed fee<br/>{}", if doc_id == 0 { "No issued agreement".to_string() } else { fmt_inr(fee) }),
            format!("Billed<br/>{}", fmt_inr(billed)),
            format!("Received, including TDS<br/>{}", fmt_inr(received)),
            format!("Outstanding<br/>{}", fmt_inr(outstanding)),
        ];
        if doc_id != 0 {
            let stage_line = if next_stage.is_empty() { "Fee fully billed".to_string() } else { format!("{next_stage} · {}", fmt_inr(next_amount)) };
            lines.push(format!("Next stage<br/>{stage_line}"));
        }
        let mut invoices = Vec::new();
        let mut listed = conn
            .prepare("SELECT COALESCE(number, ''), issue_date, kind, total_paise FROM invoices WHERE project_id = ?1 AND status = 'issued' ORDER BY issue_date, id")
            .map_err(|e| e.to_string())?;
        let rows = listed.query_map(params![project_id], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?, r.get::<_, String>(2)?, r.get::<_, i64>(3)?))).map_err(|e| e.to_string())?;
        for row in rows {
            let (number, date, kind, total) = row.map_err(|e| e.to_string())?;
            invoices.push(vec![number, sheets::display_date(&date), kind_label(&kind).to_string(), fmt_inr(total)]);
        }
        let mut body = lines.join("<br/><br/>");
        let table = if invoices.is_empty() {
            body.push_str("<br/><br/>No issued invoices.");
            String::new()
        } else {
            format!("<br/>{}", sheets::schedule(&[40, 46, 46, 46], &["Number", "Date", "Kind", "Total"], &invoices, &[false, false, false, true]))
        };
        (body, table)
    };
    let (fee_text, invoice_table) = fee_body;

    let mut contact = Vec::new();
    if !email.is_empty() {
        contact.push(esc(&email));
    }
    if !phone.is_empty() {
        contact.push(esc(&phone));
    }
    let code = if facts.code.is_empty() { String::new() } else { format!(r#"<span class="id">{}</span><br/>"#, esc(&facts.code)) };
    let cover = format!(
        r#"<table style="width: 178mm; table-layout: fixed; border-collapse: collapse;"><tr>
        <td style="width: 120mm; vertical-align: top;"><span class="kicker">PROJECT REPORT</span><br/><span class="display">{name}</span><br/><span class="quiet">{firm}</span></td>
        <td style="width: 58mm; vertical-align: top; text-align: right;">{code}<span class="quiet">{prepared}</span></td>
        </tr></table>"#,
        name = esc(&facts.name),
        firm = esc(&firm),
        prepared = esc(&sheets::display_date(&prepared)),
    );
    let mut foot = vec![esc(&firm)];
    if !address.is_empty() {
        foot.push(esc(&address));
    }
    if !contact.is_empty() {
        foot.push(contact.join(" · "));
    }
    let body = format!(
        "{cover}{identity}{stages}{fee}{invoices}{time}{drawings}{issued}<table style=\"width: 178mm; border-collapse: collapse; margin-top: 16pt;\"><tr><td class=\"quiet\" style=\"border-top: 0.6pt solid {ink}; padding-top: 6pt;\">{foot}</td></tr></table>",
        identity = sheets::section("01", "Project", &identity.join("<br/><br/>")),
        stages = stage_table,
        fee = sheets::section("02", "Fee", &fee_text),
        invoices = invoice_table,
        time = sheets::section("03", "Time", &hours_label(minutes)),
        drawings = sheets::section("04", "Drawings issued", &drawings),
        issued = drawing_table,
        ink = sheets::INK,
        foot = foot.join("<br/>"),
    );
    let top = if letterhead == "minimal" { "16mm" } else { "22mm" };
    Ok(sheets::page(&accent, &format!("body {{ margin: {top} 16mm 20mm 16mm; }}"), &body))
}

pub fn project_report_pdf(conn: &Connection, project_id: i64, finance: bool) -> Result<Vec<u8>, String> {
    let logos = crate::commands::company_logos_b64(conn);
    let images = crate::pdf::logo_images(&logos.square, &logos.wide);
    let mut html = project_report_html(conn, project_id, finance)?;
    let marks = crate::pdf::letterhead_marks(&logos.square, &logos.wide);
    if !marks.is_empty() {
        if let Some(at) = html.find("<body>") {
            html.insert_str(at + "<body>".len(), &marks);
        }
    }
    let letterhead: String = conn.query_row("SELECT letterhead_style FROM company_profile WHERE id = 1", [], |r| r.get(0)).unwrap_or_else(|_| "classic".into());
    let rendered = if letterhead == "minimal" {
        crate::pdf::render_html_with_qr_and_images(&html, &crate::pdf::bundled_fonts(), &images, None)?
    } else {
        crate::pdf::render_letter_sheet(&html, &crate::pdf::bundled_fonts(), &images)?
    };
    Ok(rendered.bytes)
}

#[derive(Serialize)]
pub struct LedgerMovement {
    pub date: String,
    pub particular: String,
    pub project: String,
    pub debit_display: String,
    pub credit_display: String,
    pub balance_display: String,
}

#[derive(Serialize)]
pub struct PartyLedger {
    pub client_name: String,
    pub project_name: String,
    pub note: String,
    pub opening_display: String,
    pub closing_display: String,
    pub lines: Vec<LedgerMovement>,
}

struct Movement {
    date: String,
    order: i64,
    particular: String,
    project: String,
    debit: i64,
    credit: i64,
}

/// The client's own ledger, or one project's ledger when a project is chosen.
/// Invoices debit the client. Receipts, credit notes and write-offs credit the client.
/// The total rolls into Accounts receivable.
pub fn party_ledger(conn: &Connection, client_id: i64, project_id: Option<i64>, from: &str, to: &str) -> Result<PartyLedger, String> {
    let from = from.trim();
    let to = to.trim();
    if ymd(from).is_none() || ymd(to).is_none() {
        return Err("Enter the dates as year-month-day.".into());
    }
    if from > to {
        return Err("The start date is after the end date.".into());
    }
    let client_name: String = conn.query_row("SELECT name FROM clients WHERE id = ?1", params![client_id], |r| r.get(0)).map_err(|_| "Choose a client.".to_string())?;
    let project_count: i64 = conn.query_row("SELECT COUNT(*) FROM projects WHERE client_id = ?1 AND archived = 0", params![client_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    let project_name = match project_id {
        Some(id) => conn
            .query_row("SELECT name FROM projects WHERE id = ?1 AND client_id = ?2", params![id, client_id], |r| r.get(0))
            .map_err(|_| "That project does not belong to this client.".to_string())?,
        None => "All projects".to_string(),
    };
    let note = if project_id.is_some() {
        "This is the project ledger. The client ledger is the total of this client's projects, and that total rolls into Accounts receivable."
    } else if project_count > 1 {
        "This is the client ledger, across every project. Choose a project to see that project's own ledger. The total rolls into Accounts receivable."
    } else {
        "This is the client ledger. It rolls into Accounts receivable."
    };
    let mut moves = Vec::new();
    let mut invoices = conn
        .prepare(
            "SELECT i.issue_date, i.id, COALESCE(i.number, 'Draft'), p.name, i.total_paise
             FROM invoices i JOIN projects p ON p.id = i.project_id
             WHERE p.client_id = ?1 AND (?2 IS NULL OR p.id = ?2) AND i.kind = 'tax_invoice' AND i.status = 'issued'",
        )
        .map_err(|e| e.to_string())?;
    for row in invoices.query_map(params![client_id, project_id], |r| Ok((r.get::<_, String>(0)?, r.get::<_, i64>(1)?, r.get::<_, String>(2)?, r.get::<_, String>(3)?, r.get::<_, i64>(4)?))).map_err(|e| e.to_string())? {
        let (date, id, number, project, amount) = row.map_err(|e| e.to_string())?;
        moves.push(Movement { date, order: id, particular: format!("Invoice {number}"), project, debit: amount, credit: 0 });
    }
    drop(invoices);
    let mut credits = conn
        .prepare(
            "SELECT c.issue_date, c.id, c.number, COALESCE(i.number, ''), p.name, c.total_paise
             FROM credit_notes c JOIN invoices i ON i.id = c.invoice_id JOIN projects p ON p.id = i.project_id
             WHERE p.client_id = ?1 AND (?2 IS NULL OR p.id = ?2)",
        )
        .map_err(|e| e.to_string())?;
    for row in credits.query_map(params![client_id, project_id], |r| Ok((r.get::<_, String>(0)?, r.get::<_, i64>(1)?, r.get::<_, String>(2)?, r.get::<_, String>(3)?, r.get::<_, String>(4)?, r.get::<_, i64>(5)?))).map_err(|e| e.to_string())? {
        let (date, id, number, invoice, project, amount) = row.map_err(|e| e.to_string())?;
        moves.push(Movement { date, order: id, particular: format!("Credit note {number} against {invoice}"), project, debit: 0, credit: amount });
    }
    drop(credits);
    let mut receipts = conn
        .prepare(
            "SELECT r.received_date, r.id, r.number, COALESCE(i.number, ''), p.name, r.amount_paise + r.tds_paise
             FROM effective_receipts r JOIN invoices i ON i.id = r.invoice_id JOIN projects p ON p.id = i.project_id
             WHERE p.client_id = ?1 AND (?2 IS NULL OR p.id = ?2)",
        )
        .map_err(|e| e.to_string())?;
    for row in receipts.query_map(params![client_id, project_id], |r| Ok((r.get::<_, String>(0)?, r.get::<_, i64>(1)?, r.get::<_, String>(2)?, r.get::<_, String>(3)?, r.get::<_, String>(4)?, r.get::<_, i64>(5)?))).map_err(|e| e.to_string())? {
        let (date, id, number, invoice, project, amount) = row.map_err(|e| e.to_string())?;
        moves.push(Movement { date, order: id, particular: format!("Receipt {number} against {invoice}"), project, debit: 0, credit: amount });
    }
    drop(receipts);
    let mut writeoffs = conn
        .prepare(
            "SELECT w.writeoff_date, w.id, COALESCE(i.number, ''), p.name, w.amount_paise, w.reason
             FROM invoice_writeoffs w JOIN invoices i ON i.id = w.invoice_id JOIN projects p ON p.id = i.project_id
             WHERE p.client_id = ?1 AND (?2 IS NULL OR p.id = ?2) AND w.id NOT IN (SELECT writeoff_id FROM writeoff_reversals)",
        )
        .map_err(|e| e.to_string())?;
    for row in writeoffs.query_map(params![client_id, project_id], |r| Ok((r.get::<_, String>(0)?, r.get::<_, i64>(1)?, r.get::<_, String>(2)?, r.get::<_, String>(3)?, r.get::<_, i64>(4)?, r.get::<_, String>(5)?))).map_err(|e| e.to_string())? {
        let (date, id, invoice, project, amount, reason) = row.map_err(|e| e.to_string())?;
        moves.push(Movement { date, order: id, particular: format!("Write-off of {invoice}. {reason}"), project, debit: 0, credit: amount });
    }
    drop(writeoffs);
    moves.sort_by(|a, b| a.date.cmp(&b.date).then(a.order.cmp(&b.order)).then(a.particular.cmp(&b.particular)));
    let mut running = 0i64;
    let mut opening = 0i64;
    let mut lines = Vec::new();
    for movement in moves {
        if movement.date.as_str() < from {
            opening += movement.debit - movement.credit;
            running = opening;
            continue;
        }
        if movement.date.as_str() > to {
            continue;
        }
        running += movement.debit - movement.credit;
        lines.push(LedgerMovement {
            date: movement.date,
            particular: movement.particular,
            project: movement.project,
            debit_display: if movement.debit > 0 { fmt_inr(movement.debit) } else { String::new() },
            credit_display: if movement.credit > 0 { fmt_inr(movement.credit) } else { String::new() },
            balance_display: fmt_inr(running),
        });
    }
    Ok(PartyLedger {
        client_name,
        project_name,
        note: note.to_string(),
        opening_display: fmt_inr(opening),
        closing_display: fmt_inr(running),
        lines,
    })
}

#[tauri::command]
pub fn party_ledger_report(state: HostState<'_>, client_id: i64, project_id: Option<i64>, from: String, to: String) -> Result<PartyLedger, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    party_ledger(&conn, client_id, project_id, &from, &to)
}

#[tauri::command]
pub fn client_statement_report(state: HostState<'_>, client_id: i64, as_of: String) -> Result<ClientStatement, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    client_statement(&conn, client_id, &as_of)
}

#[tauri::command]
pub fn project_fee_position(state: HostState<'_>) -> Result<FeePosition, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    fee_position(&conn)
}

#[tauri::command]
pub fn project_report(state: HostState<'_>, project_id: i64) -> Result<String, String> {
    use base64::Engine;
    require(&state, Permission::View)?;
    let finance = state.session.lock().map_err(|_| INTERNAL)?.as_ref().is_some_and(|s| s.role.allows(Permission::Finance));
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    Ok(base64::engine::general_purpose::STANDARD.encode(project_report_pdf(&conn, project_id, finance)?))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::db::migrate(&mut c).unwrap();
        c
    }

    #[test]
    fn statement_ages_the_open_balance_and_lists_the_receipt() {
        let c = conn();
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1, 'Villa')", []).unwrap();
        c.execute(
            "INSERT INTO invoices (project_id, kind, status, number, issue_date, due_date, gst_mode, subtotal_paise, tax_paise, total_paise)
             VALUES (1, 'tax_invoice', 'issued', 'INV-1', '2026-01-01', '2026-01-01', 'none', 100000, 0, 100000)",
            [],
        )
        .unwrap();
        c.execute(
            "INSERT INTO receipts (number, invoice_id, received_date, amount_paise, tds_paise, tds_section, mode)
             VALUES ('RCT-1', 1, '2026-01-20', 40000, 10000, '194J', 'bank')",
            [],
        )
        .unwrap();
        let statement = client_statement(&c, 1, "2026-02-15").unwrap();
        assert_eq!(statement.invoices.len(), 1);
        assert_eq!(statement.invoices[0].bucket, "31–60 days");
        assert_eq!(statement.balance_display, fmt_inr(50_000));
        assert_eq!(statement.receipts.len(), 1);
        assert_eq!(statement.receipts[0].section, "194J");
        assert_eq!(statement.ageing[2].balance_display, fmt_inr(50_000));
    }

    #[test]
    fn fee_position_uses_the_issued_agreement_and_the_unbilled_stage() {
        let c = conn();
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name, consultancy_type) VALUES (1, 'Villa', 'interior')", []).unwrap();
        c.execute(
            "INSERT INTO documents (kind, project_id, status, number, doc_date, fee_paise) VALUES ('agreement', 1, 'draft', 'AGR-D', '2026-01-01', 999)",
            [],
        )
        .unwrap();
        c.execute(
            "INSERT INTO documents (kind, project_id, status, number, doc_date, fee_paise) VALUES ('agreement', 1, 'issued', 'AGR-1', '2026-01-02', 200000)",
            [],
        )
        .unwrap();
        c.execute(
            "INSERT INTO document_milestones (document_id, position, name, percent_bp, amount_paise) VALUES (2, 1, 'Design', 5000, 100000), (2, 2, 'Completion', 5000, 100000)",
            [],
        )
        .unwrap();
        c.execute(
            "INSERT INTO invoices (project_id, kind, status, number, issue_date, due_date, gst_mode, subtotal_paise, tax_paise, total_paise)
             VALUES (1, 'tax_invoice', 'issued', 'INV-1', '2026-02-01', '2026-02-15', 'none', 100000, 0, 100000)",
            [],
        )
        .unwrap();
        c.execute("UPDATE document_milestones SET invoice_id = 1 WHERE id = 1", []).unwrap();
        let position = fee_position(&c).unwrap();
        assert_eq!(position.rows.len(), 1);
        assert_eq!(position.rows[0].consultancy, "interior");
        assert_eq!(position.rows[0].fee_display, fmt_inr(200_000));
        assert_eq!(position.rows[0].billed_display, fmt_inr(100_000));
        assert_eq!(position.rows[0].next_stage, "Completion");
        assert_eq!(position.rows[0].next_amount_display, fmt_inr(100_000));
    }

    #[test]
    fn a_project_report_uses_recorded_facts_and_does_not_invent_drawings() {
        let c = conn();
        c.execute("UPDATE company_profile SET name = 'Studio X', accent_hex = '#3E5240'", []).unwrap();
        c.execute("INSERT INTO clients (name, address) VALUES ('Acme', 'Pune')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, code, name, site_address, consultancy_type) VALUES (1, 'V-01', 'Villa', 'Koregaon', 'interior')", []).unwrap();
        c.execute("INSERT INTO time_entries (project_id, person, work_date, minutes) VALUES (1, 'A. Architect', '2026-10-01', 90)", []).unwrap();
        let html = project_report_html(&c, 1, true).unwrap();
        assert!(html.contains("Villa") && html.contains("Acme") && html.contains("Interior architecture"));
        assert!(html.contains("1 h 30 min") && html.contains("No drawings have been recorded"));
        assert!(html.contains("#3E5240") && html.contains("No issued agreement"));
        assert!(!html.contains("lorem") && !html.contains("Sample drawing"));
        let quiet = project_report_html(&c, 1, false).unwrap();
        assert!(quiet.contains("accounts reports") && !quiet.contains("Agreed fee"));
        let bytes = project_report_pdf(&c, 1, true).unwrap();
        assert!(bytes.starts_with(b"%PDF"));
        if let Ok(dir) = std::env::var("AO_PREVIEW_DIR") {
            std::fs::write(std::path::Path::new(&dir).join("project_report.pdf"), &bytes).unwrap();
        }
        assert!(project_report_html(&c, 99, true).is_err());
    }
}
