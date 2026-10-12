//! Letters (outward and inward register), consultancy agreements and the versioned templates they start from.
//! A template supplies wording with {{fields}}; a document owns its own content. Issuing freezes the content,
//! the template version used, the rendered PDF and its hash.

use crate::auth::Permission;
use crate::billing::{allocate_number, esc, fmt_inr, parse_decimal, sha2_lite::sha256_hex, validate_date, InvoiceInput, LineInput};
use crate::commands::{require, AppState, INTERNAL};
use crate::{db, pdf};
use base64::Engine;
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};
use tauri::State;

// ---------- fields ----------

pub const FIELDS: &[&str] = &[
    "company.name", "company.address", "company.email", "company.phone", "company.pan", "company.gstin", "company.coa", "company.architect",
    "client.name", "client.address", "project.name", "project.code", "project.site",
    "document.number", "document.date", "document.subject", "document.reference",
    "party.name", "party.address", "agreement.fee",
];

/// Every `{{field}}` must be in the catalogue; returns the names found.
pub fn validate_fields(body: &str) -> Result<Vec<String>, String> {
    let mut found = Vec::new();
    let mut rest = body;
    while let Some(start) = rest.find("{{") {
        let after = &rest[start + 2..];
        let end = after.find("}}").ok_or("A {{field}} is not closed with }}.")?;
        let name = after[..end].trim();
        if !FIELDS.contains(&name) {
            return Err(format!("Unknown field {{{{{name}}}}}. Available: {}.", FIELDS.join(", ")));
        }
        found.push(name.to_string());
        rest = &after[end + 2..];
    }
    Ok(found)
}

struct Ctx {
    company_name: String, company_address: String, company_email: String, company_phone: String, company_pan: String,
    company_gstin: String, company_coa: String, company_architect: String,
    client_name: Option<String>, client_address: String, project_name: Option<String>, project_code: String, project_site: String,
    number: String, date: String, subject: String, reference: String, party_name: String, party_address: String, fee_display: String,
}

fn field_value(c: &Ctx, name: &str) -> Result<String, String> {
    let need = |v: &Option<String>, what: &str| v.clone().ok_or_else(|| format!("This text uses {{{{{name}}}}} but no {what} is selected."));
    Ok(match name {
        "company.name" => c.company_name.clone(),
        "company.address" => c.company_address.clone(),
        "company.email" => c.company_email.clone(),
        "company.phone" => c.company_phone.clone(),
        "company.pan" => c.company_pan.clone(),
        "company.gstin" => c.company_gstin.clone(),
        "company.coa" => c.company_coa.clone(),
        "company.architect" => c.company_architect.clone(),
        "client.name" => need(&c.client_name, "project")?,
        "client.address" => { need(&c.client_name, "project")?; c.client_address.clone() }
        "project.name" => need(&c.project_name, "project")?,
        "project.code" => { need(&c.project_name, "project")?; c.project_code.clone() }
        "project.site" => { need(&c.project_name, "project")?; c.project_site.clone() }
        "document.number" => c.number.clone(),
        "document.date" => c.date.clone(),
        "document.subject" => c.subject.clone(),
        "document.reference" => if c.reference.is_empty() { String::new() } else { format!("Ref: {}", c.reference) },
        "party.name" => c.party_name.clone(),
        "party.address" => c.party_address.clone(),
        "agreement.fee" => c.fee_display.clone(),
        _ => return Err(format!("Unknown field {name}.")),
    })
}

fn substitute(body: &str, c: &Ctx) -> Result<String, String> {
    validate_fields(body)?;
    let mut out = String::new();
    let mut rest = body;
    while let Some(start) = rest.find("{{") {
        out.push_str(&rest[..start]);
        let after = &rest[start + 2..];
        let end = after.find("}}").unwrap();
        out.push_str(&field_value(c, after[..end].trim())?);
        rest = &after[end + 2..];
    }
    out.push_str(rest);
    Ok(out)
}

/// Plain text with blank-line paragraphs, "# " headings and "- " bullets.
fn text_to_html(text: &str) -> String {
    let mut html = String::new();
    for block in text.replace("\r\n", "\n").split("\n\n") {
        let block = block.trim();
        if block.is_empty() {
            continue;
        }
        if let Some(h) = block.strip_prefix("# ") {
            html.push_str(&format!("<h3>{}</h3>", esc(h.trim())));
        } else if block.lines().all(|l| l.trim_start().starts_with("- ")) {
            for l in block.lines() {
                html.push_str(&format!("<p>• {}</p>", esc(l.trim_start().trim_start_matches("- "))));
            }
        } else if block.starts_with("# ") || block.lines().next().map(|l| l.starts_with("# ")).unwrap_or(false) {
            // heading followed by text in the same block
            let mut it = block.lines();
            let first = it.next().unwrap();
            html.push_str(&format!("<h3>{}</h3>", esc(first.trim_start_matches("# ").trim())));
            let rest: Vec<&str> = it.collect();
            if !rest.is_empty() {
                html.push_str(&format!("<p>{}</p>", esc(&rest.join("\n"))));
            }
        } else {
            html.push_str(&format!("<p>{}</p>", esc(block)));
        }
    }
    html
}

// ---------- model ----------

#[derive(Serialize, Deserialize, Clone)]
pub struct MilestoneInput {
    pub name: String,
    /// Percentage of the fee, e.g. "30" or "12.5".
    pub percent: String,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct DocInput {
    pub id: Option<i64>,
    pub kind: String,
    pub project_id: Option<i64>,
    pub party_name: String,
    pub party_address: String,
    pub subject: String,
    pub reference: String,
    pub doc_date: String,
    pub body: String,
    pub template_version_id: Option<i64>,
    /// Agreed professional fee in rupees (agreements).
    pub fee: String,
    pub milestones: Vec<MilestoneInput>,
}

#[derive(Serialize, Deserialize)]
pub struct DocSnapshot {
    pub number: String,
    pub kind: String,
    pub doc_date: String,
    pub subject: String,
    pub reference: String,
    pub party_name: String,
    pub party_address: String,
    pub text: String,
    pub company_name: String,
    pub company_address: String,
    pub company_contact: String,
    pub company_ids: String,
    pub architect: String,
    pub fee_display: String,
    pub milestones: Vec<(String, String, String)>,
}

fn kind_ok(k: &str) -> bool {
    ["letter_out", "letter_in", "agreement"].contains(&k)
}

fn parse_milestones(fee_paise: i64, ms: &[MilestoneInput]) -> Result<Vec<(String, i64, i64)>, String> {
    let mut out = Vec::new();
    let mut total_bp = 0i64;
    for (i, m) in ms.iter().enumerate() {
        let name = m.name.trim();
        if name.is_empty() {
            return Err(format!("Milestone {}: give it a name.", i + 1));
        }
        let bp = parse_decimal(&m.percent, 2).map_err(|e| format!("Milestone {}: percentage. {e}", i + 1))?;
        if bp == 0 || bp > 10_000 {
            return Err(format!("Milestone {}: the percentage must be between 0 and 100.", i + 1));
        }
        total_bp += bp;
        out.push((name.to_string(), bp, (fee_paise * bp + 5_000) / 10_000));
    }
    if total_bp > 10_000 {
        return Err("The milestone percentages add up to more than 100%.".into());
    }
    // When the schedule is complete, the last milestone absorbs rounding so the amounts add up to the fee exactly.
    if total_bp == 10_000 {
        let sum: i64 = out.iter().map(|m| m.2).sum();
        if let Some(last) = out.last_mut() {
            last.2 += fee_paise - sum;
        }
    }
    Ok(out)
}

fn build_ctx(conn: &Connection, i: &DocInput, number: &str, fee_paise: i64) -> Result<Ctx, String> {
    let (company_name, company_address, company_email, company_phone, company_pan, company_gstin, company_coa, company_architect): (String, String, String, String, String, String, String, String) = conn
        .query_row(
            "SELECT name, address, email, phone, pan, gstin, coa_registration, architect_name FROM company_profile WHERE id = 1",
            [],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?, r.get(6)?, r.get(7)?)),
        )
        .map_err(|e| e.to_string())?;
    let (mut client_name, mut client_address, mut project_name, mut project_code, mut project_site) = (None, String::new(), None, String::new(), String::new());
    if let Some(pid) = i.project_id {
        let (pn, pc, ps, cn, ca): (String, String, String, String, String) = conn
            .query_row(
                "SELECT p.name, p.code, p.site_address, c.name, c.address FROM projects p JOIN clients c ON c.id = p.client_id WHERE p.id = ?1",
                params![pid],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?)),
            )
            .map_err(|_| "Project not found.".to_string())?;
        client_name = Some(cn);
        client_address = ca;
        project_name = Some(pn);
        project_code = pc;
        project_site = ps;
    }
    let party_name = if i.party_name.trim().is_empty() { client_name.clone().unwrap_or_default() } else { i.party_name.trim().to_string() };
    let party_address = if i.party_address.trim().is_empty() { client_address.clone() } else { i.party_address.trim().to_string() };
    Ok(Ctx {
        company_name, company_address, company_email, company_phone, company_pan, company_gstin, company_coa, company_architect,
        client_name, client_address, project_name, project_code, project_site,
        number: number.to_string(), date: i.doc_date.clone(), subject: i.subject.trim().to_string(), reference: i.reference.trim().to_string(),
        party_name, party_address, fee_display: fmt_inr(fee_paise),
    })
}

// ---------- drafts ----------

pub fn save_draft(conn: &mut Connection, actor: i64, i: &DocInput) -> Result<i64, String> {
    if i.kind == "letter_in" {
        return Err("Inward letters are registered, not drafted.".into());
    }
    if !kind_ok(&i.kind) {
        return Err("Invalid document type.".into());
    }
    validate_date(&i.doc_date)?;
    validate_fields(&i.body)?;
    let fee = if i.fee.trim().is_empty() { 0 } else { parse_decimal(&i.fee, 2).map_err(|e| format!("Fee. {e}"))? };
    if i.kind == "agreement" && i.project_id.is_none() {
        return Err("Choose the project this agreement is for.".into());
    }
    let ms = if i.kind == "agreement" { parse_milestones(fee, &i.milestones)? } else { Vec::new() };
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    if let Some(pid) = i.project_id {
        let ok: i64 = tx.query_row("SELECT COUNT(*) FROM projects WHERE id = ?1 AND archived = 0", params![pid], |r| r.get(0)).map_err(|e| e.to_string())?;
        if ok == 0 {
            return Err("Choose an active project.".into());
        }
    }
    let id = match i.id {
        Some(id) => {
            let status: String = tx.query_row("SELECT status FROM documents WHERE id = ?1", params![id], |r| r.get(0)).map_err(|_| "Document not found.".to_string())?;
            if status != "draft" {
                return Err("Only drafts can be edited. Create a revision instead.".into());
            }
            tx.execute(
                "UPDATE documents SET kind=?1, project_id=?2, party_name=?3, party_address=?4, subject=?5, reference=?6, doc_date=?7, body=?8,
                 template_version_id=?9, fee_paise=?10, updated_at=datetime('now') WHERE id=?11",
                params![i.kind, i.project_id, i.party_name.trim(), i.party_address.trim(), i.subject.trim(), i.reference.trim(), i.doc_date, i.body, i.template_version_id, fee, id],
            )
            .map_err(|e| e.to_string())?;
            tx.execute("DELETE FROM document_milestones WHERE document_id = ?1", params![id]).map_err(|e| e.to_string())?;
            id
        }
        None => {
            tx.execute(
                "INSERT INTO documents (kind, project_id, party_name, party_address, subject, reference, doc_date, body, template_version_id, fee_paise, created_by)
                 VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11)",
                params![i.kind, i.project_id, i.party_name.trim(), i.party_address.trim(), i.subject.trim(), i.reference.trim(), i.doc_date, i.body, i.template_version_id, fee, actor],
            )
            .map_err(|e| e.to_string())?;
            tx.last_insert_rowid()
        }
    };
    for (pos, (name, bp, amount)) in ms.iter().enumerate() {
        tx.execute(
            "INSERT INTO document_milestones (document_id, position, name, percent_bp, amount_paise) VALUES (?1,?2,?3,?4,?5)",
            params![id, pos as i64, name, bp, amount],
        )
        .map_err(|e| e.to_string())?;
    }
    tx.commit().map_err(|e| e.to_string())?;
    Ok(id)
}

// ---------- issuing ----------

fn series_kind(kind: &str) -> &'static str {
    match kind {
        "letter_out" => "letter_out",
        "letter_in" => "letter_in",
        _ => "agreement",
    }
}

fn fixed_text(i: &DocInput) -> String {
    i.body.clone()
}

fn build_snapshot(conn: &Connection, i: &DocInput, number: &str) -> Result<DocSnapshot, String> {
    let fee = if i.fee.trim().is_empty() { 0 } else { parse_decimal(&i.fee, 2)? };
    let ctx = build_ctx(conn, i, number, fee)?;
    let text = substitute(&fixed_text(i), &ctx)?;
    let ms = if i.kind == "agreement" { parse_milestones(fee, &i.milestones)? } else { Vec::new() };
    let contact = [ctx.company_email.as_str(), ctx.company_phone.as_str()].iter().filter(|s| !s.is_empty()).cloned().collect::<Vec<_>>().join("  |  ");
    let mut ids = Vec::new();
    if !ctx.company_pan.is_empty() { ids.push(format!("PAN: {}", ctx.company_pan)); }
    if !ctx.company_gstin.is_empty() { ids.push(format!("GSTIN: {}", ctx.company_gstin)); }
    if !ctx.company_coa.is_empty() { ids.push(format!("COA Reg. No.: {}", ctx.company_coa)); }
    Ok(DocSnapshot {
        number: number.to_string(), kind: i.kind.clone(), doc_date: i.doc_date.clone(), subject: ctx.subject.clone(), reference: ctx.reference.clone(),
        party_name: ctx.party_name.clone(), party_address: ctx.party_address.clone(), text,
        company_name: ctx.company_name.clone(), company_address: ctx.company_address.clone(), company_contact: contact, company_ids: ids.join("  |  "),
        architect: ctx.company_architect.clone(), fee_display: fmt_inr(fee),
        milestones: ms.iter().map(|(n, bp, a)| (n.clone(), format!("{}%", crate::documents::pct(*bp)), fmt_inr(*a))).collect(),
    })
}

pub fn pct(bp: i64) -> String {
    let s = format!("{}.{:02}", bp / 100, bp % 100);
    s.trim_end_matches('0').trim_end_matches('.').to_string()
}

pub fn doc_html(s: &DocSnapshot) -> String {
    let title = match s.kind.as_str() {
        "agreement" => format!("<h2>{}</h2>", esc(if s.subject.is_empty() { "AGREEMENT" } else { &s.subject })),
        _ => String::new(),
    };
    let to = if s.kind == "letter_out" && !s.party_name.is_empty() {
        format!("<p>To,<br/>{}<br/>{}</p>", esc(&s.party_name), esc(&s.party_address))
    } else {
        String::new()
    };
    let milestones = if s.milestones.is_empty() {
        String::new()
    } else {
        let mut rows = String::new();
        for (i, (n, p, a)) in s.milestones.iter().enumerate() {
            rows.push_str(&format!("<tr><td>{}</td><td>{}</td><td>{}</td><td>{}</td></tr>", i + 1, esc(n), esc(p), esc(a)));
        }
        format!("<h3>Fee schedule — total {}</h3><table class=\"items\"><tr><th>#</th><th>Milestone</th><th>Share</th><th>Amount</th></tr>{rows}</table>", esc(&s.fee_display))
    };
    let signer = if s.architect.is_empty() { String::new() } else { format!("<br/>{}", esc(&s.architect)) };
    let signature = if s.kind == "agreement" {
        format!(
            "<table class=\"items\"><tr><td>For {co}{signer}<br/><br/><br/>Signature &amp; date</td><td>For the Client<br/><br/><br/><br/>Signature &amp; date</td></tr></table>",
            co = esc(&s.company_name), signer = signer
        )
    } else {
        format!("<p><br/>For {}{}<br/>Authorised signatory</p>", esc(&s.company_name), signer)
    };
    format!(
        r#"<html><head><style>
        body {{ font-family: DejaVu; font-size: 10px; color: #161616; }}
        h1 {{ font-size: 18px; margin-bottom: 2px; }}
        h2 {{ font-size: 14px; margin-bottom: 2px; }}
        h3 {{ font-size: 11px; margin-bottom: 2px; }}
        .items {{ width: 100%; border-collapse: collapse; margin-top: 8px; }}
        .items th, .items td {{ border: 1px solid #8d8d8d; padding: 4px; }}
        .items th {{ background-color: #e0e0e0; }}
        </style></head><body>
        <h1>{company}</h1>
        <p>{address}<br/>{contact}<br/>{ids}</p>
        <p>No.: {number} &nbsp; Date: {date}</p>
        {title}{to}
        {body}
        {milestones}
        {signature}
        </body></html>"#,
        company = esc(&s.company_name), address = esc(&s.company_address), contact = esc(&s.company_contact), ids = esc(&s.company_ids),
        number = esc(&s.number), date = esc(&s.doc_date), title = title, to = to, body = text_to_html(&s.text), milestones = milestones, signature = signature,
    )
}

fn root_number(n: &str) -> String {
    match n.rfind("-R") {
        Some(i) if n[i + 2..].chars().all(|c| c.is_ascii_digit()) && !n[i + 2..].is_empty() => n[..i].to_string(),
        _ => n.to_string(),
    }
}

pub fn issue(conn: &mut Connection, files_root: &std::path::Path, actor: i64, id: i64) -> Result<String, String> {
    let d = get(conn, id)?;
    if d.status != "draft" {
        return Err("Only drafts can be issued.".into());
    }
    let i = &d.input;
    validate_date(&i.doc_date)?;
    if i.kind == "letter_out" {
        if i.subject.trim().is_empty() {
            return Err("Enter the subject of the letter.".into());
        }
        if i.party_name.trim().is_empty() && i.project_id.is_none() {
            return Err("Enter who the letter is addressed to.".into());
        }
    }
    if i.kind == "agreement" {
        let fee = parse_decimal(&i.fee, 2).unwrap_or(0);
        if fee == 0 {
            return Err("Enter the agreed fee.".into());
        }
        let total: i64 = i.milestones.iter().map(|m| parse_decimal(&m.percent, 2).unwrap_or(0)).sum();
        if total != 10_000 {
            return Err("The milestone percentages must add up to exactly 100%.".into());
        }
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let number = match d.revision_of {
        Some(prev) => {
            let prev_number: String = tx.query_row("SELECT COALESCE(number,'') FROM documents WHERE id = ?1", params![prev], |r| r.get(0)).map_err(|e| e.to_string())?;
            let root = root_number(&prev_number);
            let n: i64 = tx.query_row("SELECT COUNT(*) FROM documents WHERE number LIKE ?1 || '-R%'", params![root], |r| r.get(0)).map_err(|e| e.to_string())?;
            format!("{root}-R{}", n + 1)
        }
        None => allocate_number(&tx, series_kind(&i.kind), &i.doc_date)?,
    };
    let snapshot = build_snapshot(&tx, i, &number)?;
    let rendered = pdf::render_html(&doc_html(&snapshot), &pdf::bundled_fonts())?;
    let sha = sha256_hex(&rendered.bytes);
    let key = format!("documents/{}.pdf", number.replace('/', "_"));
    let path = files_root.join(&key);
    std::fs::create_dir_all(path.parent().unwrap()).map_err(|e| e.to_string())?;
    if path.exists() {
        return Err("A PDF for this number already exists; refusing to overwrite.".into());
    }
    std::fs::write(&path, &rendered.bytes).map_err(|e| e.to_string())?;
    let json = serde_json::to_string(&snapshot).map_err(|e| e.to_string())?;
    let res = tx
        .execute(
            "UPDATE documents SET status='issued', number=?1, snapshot_json=?2, pdf_key=?3, pdf_sha256=?4, issued_at=datetime('now'), updated_at=datetime('now')
             WHERE id=?5 AND status='draft'",
            params![number, json, key, sha, id],
        )
        .map_err(|e| e.to_string())
        .and_then(|n| if n == 1 { Ok(()) } else { Err("The document was already issued.".to_string()) })
        .and_then(|_| {
            db::audit(&tx, Some(actor), "document_issued", &format!("{number} ({})", i.kind));
            tx.commit().map_err(|e| e.to_string())
        });
    if let Err(e) = res {
        let _ = std::fs::remove_file(&path);
        return Err(e);
    }
    Ok(number)
}

#[derive(Deserialize)]
pub struct InwardInput {
    pub project_id: Option<i64>,
    pub party_name: String,
    pub subject: String,
    pub reference: String,
    pub doc_date: String,
    pub notes: String,
}

/// Enters a received letter in the inward register and gives it a register number.
pub fn register_inward(conn: &mut Connection, actor: i64, i: &InwardInput) -> Result<(i64, String), String> {
    validate_date(&i.doc_date)?;
    if i.party_name.trim().is_empty() || i.subject.trim().is_empty() {
        return Err("Enter who sent the letter and its subject.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    if let Some(pid) = i.project_id {
        let ok: i64 = tx.query_row("SELECT COUNT(*) FROM projects WHERE id = ?1", params![pid], |r| r.get(0)).map_err(|e| e.to_string())?;
        if ok == 0 {
            return Err("Project not found.".into());
        }
    }
    let number = allocate_number(&tx, "letter_in", &i.doc_date)?;
    tx.execute(
        "INSERT INTO documents (kind, number, project_id, party_name, subject, reference, doc_date, body, status, created_by, issued_at)
         VALUES ('letter_in', ?1, ?2, ?3, ?4, ?5, ?6, ?7, 'issued', ?8, datetime('now'))",
        params![number, i.project_id, i.party_name.trim(), i.subject.trim(), i.reference.trim(), i.doc_date, i.notes.trim(), actor],
    )
    .map_err(|e| e.to_string())?;
    let id = tx.last_insert_rowid();
    db::audit(&tx, Some(actor), "inward_registered", &number);
    tx.commit().map_err(|e| e.to_string())?;
    Ok((id, number))
}

// ---------- reading ----------

#[derive(Serialize)]
pub struct MilestoneRow {
    pub id: i64,
    pub name: String,
    pub percent: String,
    pub amount_display: String,
    pub invoice_id: Option<i64>,
    pub invoice_number: Option<String>,
}

#[derive(Serialize)]
pub struct DocDetail {
    pub status: String,
    pub number: Option<String>,
    pub revision_of: Option<i64>,
    pub input: DocInput,
    pub milestone_rows: Vec<MilestoneRow>,
}

pub fn get(conn: &Connection, id: i64) -> Result<DocDetail, String> {
    let row = conn
        .query_row(
            "SELECT status, number, revision_of, kind, project_id, party_name, party_address, subject, reference, doc_date, body, template_version_id, fee_paise FROM documents WHERE id = ?1",
            params![id],
            |r| {
                Ok((r.get::<_, String>(0)?, r.get::<_, Option<String>>(1)?, r.get::<_, Option<i64>>(2)?, r.get::<_, String>(3)?, r.get::<_, Option<i64>>(4)?,
                    r.get::<_, String>(5)?, r.get::<_, String>(6)?, r.get::<_, String>(7)?, r.get::<_, String>(8)?, r.get::<_, String>(9)?, r.get::<_, String>(10)?,
                    r.get::<_, Option<i64>>(11)?, r.get::<_, i64>(12)?))
            },
        )
        .map_err(|_| "Document not found.".to_string())?;
    let mut stmt = conn
        .prepare(
            "SELECT m.id, m.name, m.percent_bp, m.amount_paise, m.invoice_id, i.number FROM document_milestones m LEFT JOIN invoices i ON i.id = m.invoice_id
             WHERE m.document_id = ?1 ORDER BY m.position",
        )
        .map_err(|e| e.to_string())?;
    let ms: Vec<MilestoneRow> = stmt
        .query_map(params![id], |r| Ok(MilestoneRow { id: r.get(0)?, name: r.get(1)?, percent: pct(r.get(2)?), amount_display: fmt_inr(r.get(3)?), invoice_id: r.get(4)?, invoice_number: r.get(5)? }))
        .map_err(|e| e.to_string())?
        .collect::<Result<_, _>>()
        .map_err(|e| e.to_string())?;
    let fee = row.12;
    Ok(DocDetail {
        status: row.0, number: row.1, revision_of: row.2,
        input: DocInput {
            id: Some(id), kind: row.3, project_id: row.4, party_name: row.5, party_address: row.6, subject: row.7, reference: row.8, doc_date: row.9, body: row.10,
            template_version_id: row.11, fee: format!("{}.{:02}", fee / 100, fee % 100),
            milestones: ms.iter().map(|m| MilestoneInput { name: m.name.clone(), percent: m.percent.clone() }).collect(),
        },
        milestone_rows: ms,
    })
}

#[derive(Serialize)]
pub struct DocRow {
    pub id: i64,
    pub kind: String,
    pub number: Option<String>,
    pub status: String,
    pub party_name: String,
    pub subject: String,
    pub reference: String,
    pub doc_date: String,
    pub project_name: String,
    pub client_name: String,
    pub signed: bool,
}

/// `q` matches number, subject, party and reference (case-insensitive, no wildcards).
pub fn list(conn: &Connection, kind: Option<&str>, q: Option<&str>) -> Result<Vec<DocRow>, String> {
    let q = q.map(|s| s.trim().to_lowercase()).filter(|s| !s.is_empty());
    let mut stmt = conn
        .prepare(
            "SELECT d.id, d.kind, d.number, d.status, d.party_name, d.subject, d.reference, d.doc_date, COALESCE(p.name,''), COALESCE(c.name,''),
                    EXISTS (SELECT 1 FROM document_attachments a WHERE a.document_id = d.id AND a.kind = 'signed_copy')
             FROM documents d LEFT JOIN projects p ON p.id = d.project_id LEFT JOIN clients c ON c.id = p.client_id
             WHERE (?1 IS NULL OR d.kind = ?1)
               AND (?2 IS NULL OR instr(lower(COALESCE(d.number,'') || ' ' || d.subject || ' ' || d.party_name || ' ' || d.reference || ' ' || COALESCE(c.name,'') || ' ' || COALESCE(p.name,'')), ?2) > 0)
             ORDER BY d.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![kind, q], |r| {
            Ok(DocRow {
                id: r.get(0)?, kind: r.get(1)?, number: r.get(2)?, status: r.get(3)?, party_name: r.get(4)?, subject: r.get(5)?, reference: r.get(6)?,
                doc_date: r.get(7)?, project_name: r.get(8)?, client_name: r.get(9)?, signed: r.get::<_, i64>(10)? != 0,
            })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn pdf_bytes(conn: &Connection, files_root: &std::path::Path, id: i64) -> Result<Vec<u8>, String> {
    let (status, key, sha): (String, Option<String>, Option<String>) = conn
        .query_row("SELECT status, pdf_key, pdf_sha256 FROM documents WHERE id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
        .map_err(|_| "Document not found.".to_string())?;
    if status == "draft" {
        let d = get(conn, id)?;
        let snap = build_snapshot(conn, &d.input, "DRAFT")?;
        return Ok(pdf::render_html(&doc_html(&snap), &pdf::bundled_fonts())?.bytes);
    }
    let key = key.ok_or("This register entry has no PDF (inward letters keep their scanned attachment instead).")?;
    let bytes = std::fs::read(files_root.join(key)).map_err(|_| "The stored PDF could not be read.".to_string())?;
    if Some(sha256_hex(&bytes)) != sha {
        return Err("The stored PDF failed its integrity check.".into());
    }
    Ok(bytes)
}

// ---------- revisions, status, milestones ----------

/// Copies an issued or signed document into a new draft; the original stays untouched.
pub fn revise(conn: &mut Connection, actor: i64, id: i64) -> Result<i64, String> {
    let d = get(conn, id)?;
    if d.input.kind == "letter_in" {
        return Err("Inward letters cannot be revised.".into());
    }
    if d.status == "draft" || d.status == "cancelled" {
        return Err("Only issued or signed documents can be revised.".into());
    }
    let mut input = d.input.clone();
    input.id = None;
    let new_id = save_draft(conn, actor, &input)?;
    conn.execute("UPDATE documents SET revision_of = ?1 WHERE id = ?2", params![id, new_id]).map_err(|e| e.to_string())?;
    Ok(new_id)
}

pub fn mark_signed(conn: &Connection, actor: i64, id: i64) -> Result<(), String> {
    let (kind, status): (String, String) = conn.query_row("SELECT kind, status FROM documents WHERE id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?))).map_err(|_| "Document not found.".to_string())?;
    if kind != "agreement" || status != "issued" {
        return Err("Only an issued agreement can be marked as signed.".into());
    }
    let n: i64 = conn.query_row("SELECT COUNT(*) FROM document_attachments WHERE document_id = ?1 AND kind = 'signed_copy'", params![id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if n == 0 {
        return Err("Attach the signed copy first.".into());
    }
    conn.execute("UPDATE documents SET status='signed', updated_at=datetime('now') WHERE id = ?1", params![id]).map_err(|e| e.to_string())?;
    db::audit(conn, Some(actor), "agreement_signed", &format!("document {id}"));
    Ok(())
}

pub fn cancel(conn: &Connection, actor: i64, id: i64, reason: &str) -> Result<(), String> {
    if reason.trim().is_empty() {
        return Err("Give a reason.".into());
    }
    let (kind, status): (String, String) = conn.query_row("SELECT kind, status FROM documents WHERE id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?))).map_err(|_| "Document not found.".to_string())?;
    if kind == "letter_in" || status != "issued" {
        return Err("Only an issued letter or agreement can be cancelled (a signed agreement stays on record).".into());
    }
    conn.execute("UPDATE documents SET status='cancelled', updated_at=datetime('now') WHERE id = ?1", params![id]).map_err(|e| e.to_string())?;
    db::audit(conn, Some(actor), "document_cancelled", &format!("document {id}: {}", reason.trim()));
    Ok(())
}

/// Creates a draft invoice for one agreement milestone and links it, so the same milestone is never billed twice.
pub fn bill_milestone(conn: &mut Connection, actor: i64, milestone_id: i64, invoice_date: &str) -> Result<i64, String> {
    let (doc_id, name, bp, amount, invoice_id): (i64, String, i64, i64, Option<i64>) = conn
        .query_row("SELECT document_id, name, percent_bp, amount_paise, invoice_id FROM document_milestones WHERE id = ?1", params![milestone_id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?)))
        .map_err(|_| "Milestone not found.".to_string())?;
    if invoice_id.is_some() {
        return Err("This milestone has already been billed.".into());
    }
    let (number, status, project_id): (Option<String>, String, Option<i64>) = conn
        .query_row("SELECT number, status, project_id FROM documents WHERE id = ?1", params![doc_id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
        .map_err(|e| e.to_string())?;
    if status != "issued" && status != "signed" {
        return Err("Issue the agreement before billing its milestones.".into());
    }
    let project_id = project_id.ok_or("The agreement has no project.")?;
    let input = InvoiceInput {
        id: None, project_id, kind: "tax_invoice".into(), issue_date: invoice_date.to_string(), due_date: String::new(), gst_mode: "intra".into(), gst_rate: String::new(),
        notes: String::new(),
        lines: vec![LineInput { description: format!("{} – {} ({}%)", number.unwrap_or_default(), name, pct(bp)), qty: "1".into(), rate: format!("{}.{:02}", amount / 100, amount % 100) }],
    };
    let inv = crate::billing::save_draft(conn, &input)?;
    conn.execute("UPDATE document_milestones SET invoice_id = ?1 WHERE id = ?2", params![inv, milestone_id]).map_err(|e| e.to_string())?;
    db::audit(conn, Some(actor), "milestone_billed", &format!("milestone {milestone_id} -> draft invoice {inv}"));
    Ok(inv)
}

// ---------- attachments ----------

const MAX_ATTACHMENT: usize = 15 * 1024 * 1024;
const ALLOWED_EXT: &[&str] = &["pdf", "png", "jpg", "jpeg", "docx", "xlsx", "txt"];

#[derive(Serialize)]
pub struct AttachmentRow {
    pub id: i64,
    pub kind: String,
    pub filename: String,
    pub bytes: i64,
    pub uploaded_at: String,
}

fn safe_name(name: &str) -> String {
    let base = name.rsplit(['/', '\\']).next().unwrap_or("file");
    let cleaned: String = base.chars().map(|c| if c.is_ascii_alphanumeric() || c == '.' || c == '-' || c == '_' { c } else { '_' }).collect();
    cleaned.trim_matches('.').to_string()
}

pub fn add_attachment(conn: &Connection, files_root: &std::path::Path, actor: i64, doc_id: i64, kind: &str, filename: &str, data: &[u8]) -> Result<i64, String> {
    if kind != "attachment" && kind != "signed_copy" {
        return Err("Invalid attachment type.".into());
    }
    if data.is_empty() || data.len() > MAX_ATTACHMENT {
        return Err("The file must be between 1 byte and 15 MB.".into());
    }
    let name = safe_name(filename);
    let ext = name.rsplit('.').next().unwrap_or("").to_lowercase();
    if name.is_empty() || !ALLOWED_EXT.contains(&ext.as_str()) {
        return Err(format!("Allowed file types: {}.", ALLOWED_EXT.join(", ")));
    }
    let (dkind, status): (String, String) = conn.query_row("SELECT kind, status FROM documents WHERE id = ?1", params![doc_id], |r| Ok((r.get(0)?, r.get(1)?))).map_err(|_| "Document not found.".to_string())?;
    if kind == "signed_copy" && (dkind != "agreement" || status != "issued") {
        return Err("A signed copy can only be attached to an issued agreement.".into());
    }
    let sha = sha256_hex(data);
    let key = format!("attachments/doc{doc_id}_{}_{name}", &sha[..10]);
    let path = files_root.join(&key);
    std::fs::create_dir_all(path.parent().unwrap()).map_err(|e| e.to_string())?;
    if !path.exists() {
        std::fs::write(&path, data).map_err(|e| e.to_string())?;
    }
    conn.execute(
        "INSERT INTO document_attachments (document_id, kind, filename, file_key, sha256, bytes, uploaded_by) VALUES (?1,?2,?3,?4,?5,?6,?7)",
        params![doc_id, kind, name, key, sha, data.len() as i64, actor],
    )
    .map_err(|_| "This exact file is already attached to a document.".to_string())?;
    let id = conn.last_insert_rowid();
    db::audit(conn, Some(actor), "document_attachment_added", &format!("document {doc_id}: {name} ({kind})"));
    Ok(id)
}

pub fn list_attachments(conn: &Connection, doc_id: i64) -> Result<Vec<AttachmentRow>, String> {
    let mut stmt = conn.prepare("SELECT id, kind, filename, bytes, uploaded_at FROM document_attachments WHERE document_id = ?1 ORDER BY id").map_err(|e| e.to_string())?;
    let rows = stmt.query_map(params![doc_id], |r| Ok(AttachmentRow { id: r.get(0)?, kind: r.get(1)?, filename: r.get(2)?, bytes: r.get(3)?, uploaded_at: r.get(4)? })).map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn attachment_bytes(conn: &Connection, files_root: &std::path::Path, id: i64) -> Result<(String, Vec<u8>), String> {
    let (name, key, sha): (String, String, String) = conn
        .query_row("SELECT filename, file_key, sha256 FROM document_attachments WHERE id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
        .map_err(|_| "Attachment not found.".to_string())?;
    let bytes = std::fs::read(files_root.join(key)).map_err(|_| "The stored file could not be read.".to_string())?;
    if sha256_hex(&bytes) != sha {
        return Err("The stored file failed its integrity check.".into());
    }
    Ok((name, bytes))
}

// ---------- templates ----------

#[derive(Serialize)]
pub struct TemplateRow {
    pub id: i64,
    pub kind: String,
    pub name: String,
    pub latest_version: i64,
    pub latest_version_id: i64,
    pub body: String,
}

pub fn list_templates(conn: &Connection, kind: Option<&str>) -> Result<Vec<TemplateRow>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT t.id, t.kind, t.name, v.version, v.id, v.body FROM templates t
             JOIN template_versions v ON v.template_id = t.id AND v.version = (SELECT MAX(version) FROM template_versions WHERE template_id = t.id)
             WHERE (?1 IS NULL OR t.kind = ?1) ORDER BY t.kind, t.name",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt.query_map(params![kind], |r| Ok(TemplateRow { id: r.get(0)?, kind: r.get(1)?, name: r.get(2)?, latest_version: r.get(3)?, latest_version_id: r.get(4)?, body: r.get(5)? })).map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

#[derive(Serialize)]
pub struct VersionRow {
    pub version: i64,
    pub published_at: String,
    pub body: String,
}

pub fn template_versions(conn: &Connection, template_id: i64) -> Result<Vec<VersionRow>, String> {
    let mut stmt = conn.prepare("SELECT version, published_at, body FROM template_versions WHERE template_id = ?1 ORDER BY version DESC").map_err(|e| e.to_string())?;
    let rows = stmt.query_map(params![template_id], |r| Ok(VersionRow { version: r.get(0)?, published_at: r.get(1)?, body: r.get(2)? })).map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn create_template(conn: &mut Connection, actor: i64, kind: &str, name: &str, body: &str) -> Result<i64, String> {
    if kind != "letter" && kind != "agreement" {
        return Err("Invalid template type.".into());
    }
    if name.trim().is_empty() {
        return Err("Give the template a name.".into());
    }
    validate_fields(body)?;
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    tx.execute("INSERT INTO templates (kind, name) VALUES (?1, ?2)", params![kind, name.trim()]).map_err(|_| "A template with this name already exists.".to_string())?;
    let id = tx.last_insert_rowid();
    tx.execute("INSERT INTO template_versions (template_id, version, body, published_by) VALUES (?1, 1, ?2, ?3)", params![id, body, actor]).map_err(|e| e.to_string())?;
    db::audit(&tx, Some(actor), "template_created", name.trim());
    tx.commit().map_err(|e| e.to_string())?;
    Ok(id)
}

/// Publishes the next version. Earlier versions, and documents issued from them, are unaffected.
pub fn publish_template(conn: &mut Connection, actor: i64, template_id: i64, body: &str) -> Result<i64, String> {
    validate_fields(body)?;
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let (latest, current): (i64, String) = tx
        .query_row("SELECT version, body FROM template_versions WHERE template_id = ?1 ORDER BY version DESC LIMIT 1", params![template_id], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|_| "Template not found.".to_string())?;
    if current == body {
        return Err("Nothing has changed since the latest version.".into());
    }
    tx.execute("INSERT INTO template_versions (template_id, version, body, published_by) VALUES (?1,?2,?3,?4)", params![template_id, latest + 1, body, actor]).map_err(|e| e.to_string())?;
    db::audit(&tx, Some(actor), "template_published", &format!("template {template_id} v{}", latest + 1));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(latest + 1)
}

// ---------- commands ----------

#[tauri::command]
pub fn doc_fields(state: State<AppState>) -> Result<Vec<String>, String> {
    require(&state, Permission::View)?;
    Ok(FIELDS.iter().map(|s| s.to_string()).collect())
}

#[tauri::command]
pub fn doc_list(state: State<AppState>, kind: Option<String>, q: Option<String>) -> Result<Vec<DocRow>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list(&conn, kind.as_deref(), q.as_deref())
}

#[tauri::command]
pub fn doc_get(state: State<AppState>, id: i64) -> Result<DocDetail, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    get(&conn, id)
}

#[tauri::command]
pub fn doc_save_draft(state: State<AppState>, input: DocInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    let id = save_draft(&mut conn, uid, &input)?;
    db::audit(&conn, Some(uid), "document_draft_saved", &format!("document {id}"));
    Ok(id)
}

#[tauri::command]
pub fn doc_issue(state: State<AppState>, id: i64) -> Result<String, String> {
    let uid = require(&state, Permission::Issue)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    issue(&mut conn, &state.files_root, uid, id)
}

#[tauri::command]
pub fn doc_register_inward(state: State<AppState>, input: InwardInput) -> Result<String, String> {
    let uid = require(&state, Permission::Edit)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    Ok(register_inward(&mut conn, uid, &input)?.1)
}

#[tauri::command]
pub fn doc_pdf(state: State<AppState>, id: i64) -> Result<String, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    Ok(base64::engine::general_purpose::STANDARD.encode(pdf_bytes(&conn, &state.files_root, id)?))
}

#[tauri::command]
pub fn doc_revise(state: State<AppState>, id: i64) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    revise(&mut conn, uid, id)
}

#[tauri::command]
pub fn doc_mark_signed(state: State<AppState>, id: i64) -> Result<(), String> {
    let uid = require(&state, Permission::Issue)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    mark_signed(&conn, uid, id)
}

#[tauri::command]
pub fn doc_cancel(state: State<AppState>, id: i64, reason: String) -> Result<(), String> {
    let uid = require(&state, Permission::Issue)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    cancel(&conn, uid, id, &reason)
}

#[tauri::command]
pub fn doc_bill_milestone(state: State<AppState>, milestone_id: i64, invoice_date: String) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    bill_milestone(&mut conn, uid, milestone_id, &invoice_date)
}

#[tauri::command]
pub fn doc_attach(state: State<AppState>, doc_id: i64, kind: String, filename: String, base64_data: String) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let data = base64::engine::general_purpose::STANDARD.decode(base64_data.as_bytes()).map_err(|_| "The file could not be read.".to_string())?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    add_attachment(&conn, &state.files_root, uid, doc_id, &kind, &filename, &data)
}

#[tauri::command]
pub fn doc_attachments(state: State<AppState>, doc_id: i64) -> Result<Vec<AttachmentRow>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list_attachments(&conn, doc_id)
}

#[derive(Serialize)]
pub struct AttachmentData {
    pub filename: String,
    pub base64_data: String,
}

#[tauri::command]
pub fn doc_attachment_data(state: State<AppState>, id: i64) -> Result<AttachmentData, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let (filename, bytes) = attachment_bytes(&conn, &state.files_root, id)?;
    Ok(AttachmentData { filename, base64_data: base64::engine::general_purpose::STANDARD.encode(bytes) })
}

#[tauri::command]
pub fn template_list(state: State<AppState>, kind: Option<String>) -> Result<Vec<TemplateRow>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list_templates(&conn, kind.as_deref())
}

#[tauri::command]
pub fn template_versions_list(state: State<AppState>, template_id: i64) -> Result<Vec<VersionRow>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    template_versions(&conn, template_id)
}

#[tauri::command]
pub fn template_create(state: State<AppState>, kind: String, name: String, body: String) -> Result<i64, String> {
    let uid = require(&state, Permission::ManageSettings)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    create_template(&mut conn, uid, &kind, &name, &body)
}

#[tauri::command]
pub fn template_publish(state: State<AppState>, template_id: i64, body: String) -> Result<i64, String> {
    let uid = require(&state, Permission::ManageSettings)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    publish_template(&mut conn, uid, template_id, &body)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn setup() -> (Connection, std::path::PathBuf) {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('o','x','owner')", []).unwrap();
        c.execute("INSERT INTO clients (name, address) VALUES ('Acme Developers','Pune')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name, site_address) VALUES (1,'Villa','Plot 5, Baner')", []).unwrap();
        c.execute("UPDATE company_profile SET name='Studio X', address='Mumbai', architect_name='A. Architect', gst_scheme='regular'", []).unwrap();
        let dir = std::env::temp_dir().join(format!("ao_doc_{}_{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()));
        (c, dir)
    }

    fn letter() -> DocInput {
        DocInput {
            id: None, kind: "letter_out".into(), project_id: Some(1), party_name: "".into(), party_address: "".into(), subject: "Drawings submitted".into(), reference: "SK/12".into(),
            doc_date: "2026-10-12".into(), body: "Dear Sir,\n\nSub: {{document.subject}}\n\nWe write regarding {{project.name}} for {{client.name}}.".into(), template_version_id: Some(1),
            fee: "".into(), milestones: vec![],
        }
    }

    fn agreement() -> DocInput {
        DocInput {
            id: None, kind: "agreement".into(), project_id: Some(1), party_name: "".into(), party_address: "".into(), subject: "Consultancy agreement".into(), reference: "".into(),
            doc_date: "2026-10-12".into(), body: "# 1. Fee\nThe fee is {{agreement.fee}} for {{project.name}}.".into(), template_version_id: Some(2),
            fee: "100000".into(),
            milestones: vec![MilestoneInput { name: "Concept".into(), percent: "30".into() }, MilestoneInput { name: "Drawings".into(), percent: "40".into() }, MilestoneInput { name: "Completion".into(), percent: "30".into() }],
        }
    }

    #[test]
    fn unknown_or_unclosed_fields_are_rejected() {
        assert!(validate_fields("Hello {{client.name}} and {{project.name}}").is_ok());
        assert!(validate_fields("{{client.secret}}").unwrap_err().contains("Unknown field"));
        assert!(validate_fields("{{client.name").unwrap_err().contains("not closed"));
        let (mut c, _d) = setup();
        let mut l = letter();
        l.body = "x {{bogus}}".into();
        assert!(save_draft(&mut c, 1, &l).is_err());
    }

    #[test]
    fn letters_number_per_series_and_freeze_when_issued() {
        let (mut c, dir) = setup();
        let a = save_draft(&mut c, 1, &letter()).unwrap();
        let b = save_draft(&mut c, 1, &letter()).unwrap();
        assert_eq!(issue(&mut c, &dir, 1, a).unwrap(), "OUT/2026-27/0001");
        assert_eq!(issue(&mut c, &dir, 1, b).unwrap(), "OUT/2026-27/0002");
        if let Ok(p) = std::env::var("AO_PREVIEW_DIR") {
            std::fs::write(std::path::Path::new(&p).join("letter.pdf"), pdf_bytes(&c, &dir, a).unwrap()).unwrap();
        }
        let (_, n) = register_inward(&mut c, 1, &InwardInput { project_id: Some(1), party_name: "Municipal Corp".into(), subject: "Approval".into(), reference: "MC/9".into(), doc_date: "2026-10-13".into(), notes: "".into() }).unwrap();
        assert_eq!(n, "IN/2026-27/0001");
        // frozen: no edits, no re-issue, no SQL tampering; the PDF is the stored one
        let mut edit = letter();
        edit.id = Some(a);
        assert!(save_draft(&mut c, 1, &edit).is_err());
        assert!(issue(&mut c, &dir, 1, a).is_err());
        assert!(c.execute("UPDATE documents SET body = 'x' WHERE id = ?1", params![a]).is_err());
        assert!(c.execute("DELETE FROM documents WHERE id = ?1", params![a]).is_err());
        c.execute("UPDATE clients SET name = 'Renamed'", []).unwrap();
        let bytes = pdf_bytes(&c, &dir, a).unwrap();
        assert!(bytes.starts_with(b"%PDF"));
        let snap: DocSnapshot = serde_json::from_str(&c.query_row("SELECT snapshot_json FROM documents WHERE id = ?1", params![a], |r| r.get::<_, String>(0)).unwrap()).unwrap();
        assert!(snap.text.contains("Acme Developers"), "wording was resolved at issue time and is not affected by later edits");
        assert_eq!(list(&c, Some("letter_in"), None).unwrap().len(), 1);
        assert_eq!(list(&c, None, Some("municipal")).unwrap().len(), 1);
        assert_eq!(list(&c, None, Some("%")).unwrap().len(), 0, "search has no wildcards");
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn issue_requires_the_basics() {
        let (mut c, dir) = setup();
        let mut l = letter();
        l.subject = "".into();
        let id = save_draft(&mut c, 1, &l).unwrap();
        assert!(issue(&mut c, &dir, 1, id).unwrap_err().contains("subject"));
        let mut a = agreement();
        a.milestones[2].percent = "20".into();
        let aid = save_draft(&mut c, 1, &a).unwrap();
        assert!(issue(&mut c, &dir, 1, aid).unwrap_err().contains("100%"));
        a.id = Some(aid);
        a.milestones[2].percent = "40".into();
        assert!(save_draft(&mut c, 1, &a).unwrap_err().contains("more than 100"));
        // a field that needs a project, without a project, stops the issue
        let mut np = letter();
        np.project_id = None;
        np.party_name = "Someone".into();
        let npid = save_draft(&mut c, 1, &np).unwrap();
        assert!(issue(&mut c, &dir, 1, npid).unwrap_err().contains("no project"));
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn agreement_milestones_add_up_bill_once_and_revisions_are_numbered() {
        let (mut c, dir) = setup();
        let mut a = agreement();
        a.fee = "100000.01".into();
        let id = save_draft(&mut c, 1, &a).unwrap();
        let d = get(&c, id).unwrap();
        assert_eq!(d.milestone_rows.len(), 3);
        assert_eq!(issue(&mut c, &dir, 1, id).unwrap(), "AGR/2026-27/0001");
        if let Ok(p) = std::env::var("AO_PREVIEW_DIR") {
            std::fs::write(std::path::Path::new(&p).join("agreement.pdf"), pdf_bytes(&c, &dir, id).unwrap()).unwrap();
        }
        let amounts: i64 = c.query_row("SELECT SUM(amount_paise) FROM document_milestones WHERE document_id = ?1", params![id], |r| r.get(0)).unwrap();
        assert_eq!(amounts, 10_000_001, "rounding is absorbed so milestones add up to the fee");
        // bill the first milestone: a draft invoice, linked, only once
        let ms = get(&c, id).unwrap().milestone_rows[0].id;
        let inv = bill_milestone(&mut c, 1, ms, "2026-10-20").unwrap();
        assert!(bill_milestone(&mut c, 1, ms, "2026-10-20").unwrap_err().contains("already"));
        let detail = crate::billing::get(&c, inv).unwrap();
        assert_eq!(detail.input.lines[0].rate, "30000.00");
        assert!(detail.input.lines[0].description.contains("AGR/2026-27/0001 – Concept"));
        assert!(c.execute("UPDATE document_milestones SET amount_paise = 1", []).is_err());
        // revisions: new draft, numbered R1 then R2 against the same root
        let r1 = revise(&mut c, 1, id).unwrap();
        assert_eq!(issue(&mut c, &dir, 1, r1).unwrap(), "AGR/2026-27/0001-R1");
        let r2 = revise(&mut c, 1, r1).unwrap();
        assert_eq!(issue(&mut c, &dir, 1, r2).unwrap(), "AGR/2026-27/0001-R2");
        // signed copy workflow
        assert!(mark_signed(&c, 1, id).unwrap_err().contains("signed copy"));
        assert!(add_attachment(&c, &dir, 1, id, "signed_copy", "evil.exe", b"x").is_err());
        add_attachment(&c, &dir, 1, id, "signed_copy", "../signed agreement.pdf", b"%PDF-1.4 signed").unwrap();
        mark_signed(&c, 1, id).unwrap();
        assert_eq!(get(&c, id).unwrap().status, "signed");
        let atts = list_attachments(&c, id).unwrap();
        assert_eq!(atts[0].filename, "signed_agreement.pdf");
        assert_eq!(attachment_bytes(&c, &dir, atts[0].id).unwrap().1, b"%PDF-1.4 signed");
        assert!(c.execute("DELETE FROM document_attachments", []).is_err());
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn templates_version_immutably() {
        let (mut c, _dir) = setup();
        let all = list_templates(&c, None).unwrap();
        assert_eq!(all.len(), 2);
        let letter_t = all.iter().find(|t| t.kind == "letter").unwrap();
        assert_eq!(letter_t.latest_version, 1);
        assert!(publish_template(&mut c, 1, letter_t.id, &letter_t.body).unwrap_err().contains("Nothing has changed"));
        assert!(publish_template(&mut c, 1, letter_t.id, "Hi {{nope}}").is_err());
        assert_eq!(publish_template(&mut c, 1, letter_t.id, "Dear {{party.name}},\n\nNew wording.").unwrap(), 2);
        assert_eq!(template_versions(&c, letter_t.id).unwrap().len(), 2);
        assert!(c.execute("UPDATE template_versions SET body = 'x'", []).is_err());
        assert!(c.execute("DELETE FROM template_versions", []).is_err());
        assert!(create_template(&mut c, 1, "letter", "standard LETTER", "x").is_err(), "names are unique ignoring case");
        assert!(create_template(&mut c, 1, "letter", "Site visit report", "Hello {{client.name}}").is_ok());
    }

    #[test]
    fn text_conversion_escapes_and_structures() {
        let h = text_to_html("# Heading\n\nA <b>para</b> & more\n\n- one\n- two");
        assert!(h.contains("<h3>Heading</h3>"));
        assert!(h.contains("A &lt;b&gt;para&lt;/b&gt; &amp; more"));
        assert!(h.contains("• one") && h.contains("• two"));
    }
}
