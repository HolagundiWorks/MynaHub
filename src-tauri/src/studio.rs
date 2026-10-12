//! Template Studio: HTML/CSS templates with drafts, immutable published versions, validation and preview.
//! The PDF is produced by the same renderer used for issued documents, from the same resolved HTML, so a preview is the real output.
use crate::auth::Permission;
use crate::commands::{require, AppState, INTERNAL};
use crate::template_engine::{self as eng, Diagnostic};
use crate::{db, pdf};
use base64::Engine;
use rusqlite::{params, Connection};
use serde::Serialize;
use serde_json::{json, Value};
use tauri::State;

const SKELETON_HTML: &str = "<header class=\"document-header\">
  <div class=\"firm-name\">{{ company.name }}</div>
  <div class=\"meta\">{{ company.address }}</div>
  <div class=\"rule\"></div>
</header>
<h1>{{ document.title | default \"Document title\" }}</h1>
<p class=\"meta\"><span class=\"doc-ref\">{{ document.number }}</span> &nbsp; {{ document.date | date }}</p>
<main>{{ document.body }}</main>";

const SKELETON_CSS: &str = "@page { size: A4; margin: 18mm 16mm 20mm 16mm; }
.firm-name { font-family: 'IBM Plex Sans SemiBold'; font-size: 16pt; font-weight: 400; }
.meta { font-size: 8.5pt; color: #393939; }
.rule { border-top: 1pt solid #161616; margin-top: 6pt; margin-bottom: 10pt; }
.doc-ref { font-family: 'IBM Plex Mono'; font-size: 9pt; }";

#[derive(Serialize, Debug)]
pub struct VersionInfo {
    pub version: i64,
    pub published_at: String,
    pub change_summary: String,
}

#[derive(Serialize, Debug)]
pub struct StudioState {
    pub id: i64,
    pub name: String,
    pub family: String,
    pub kind: String,
    pub built_in: bool,
    pub archived: bool,
    pub published_version: i64,
    /// A draft exists and is what `html` and `css` hold; otherwise they are the latest published version.
    pub has_draft: bool,
    pub html: String,
    pub css: String,
    pub versions: Vec<VersionInfo>,
}

fn template_row(conn: &Connection, id: i64) -> Result<(String, String, String, bool, bool, String), String> {
    conn.query_row("SELECT name, family, kind, built_in, archived, format FROM templates WHERE id = ?1", params![id], |r| {
        Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get::<_, i64>(3)? == 1, r.get::<_, i64>(4)? == 1, r.get(5)?))
    })
    .map_err(|_| "Template not found.".to_string())
}

fn require_html(conn: &Connection, id: i64) -> Result<(String, String, String, bool, bool), String> {
    let (name, family, kind, built_in, archived, format) = template_row(conn, id)?;
    if format != "html" {
        return Err("This is a wording template. It is edited on the template page, not in the HTML studio.".into());
    }
    Ok((name, family, kind, built_in, archived))
}

pub fn open(conn: &Connection, id: i64) -> Result<StudioState, String> {
    let (name, family, kind, built_in, archived) = require_html(conn, id)?;
    let (published_version, html, css): (i64, String, String) = conn
        .query_row(
            "SELECT version, COALESCE(html_source,''), COALESCE(css_source,'') FROM template_versions WHERE template_id = ?1 ORDER BY version DESC LIMIT 1",
            params![id],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .map_err(|e| e.to_string())?;
    let draft: Option<(String, String)> = conn.query_row("SELECT html_source, css_source FROM template_drafts WHERE template_id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?))).ok();
    let mut stmt = conn.prepare("SELECT version, published_at, change_summary FROM template_versions WHERE template_id = ?1 ORDER BY version DESC").map_err(|e| e.to_string())?;
    let versions = stmt
        .query_map(params![id], |r| Ok(VersionInfo { version: r.get(0)?, published_at: r.get(1)?, change_summary: r.get(2)? }))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    let has_draft = draft.is_some();
    let (html, css) = draft.unwrap_or((html, css));
    Ok(StudioState { id, name, family, kind, built_in, archived, published_version, has_draft, html, css, versions })
}

fn editable(conn: &Connection, id: i64) -> Result<(), String> {
    let (_, _, _, built_in, archived) = require_html(conn, id)?;
    if built_in {
        return Err("Built-in templates are not edited. Make a copy and edit the copy.".into());
    }
    if archived {
        return Err("Restore this template before editing it.".into());
    }
    Ok(())
}

pub fn save_draft(conn: &Connection, actor: i64, id: i64, html: &str, css: &str) -> Result<(), String> {
    editable(conn, id)?;
    if html.len() > 400_000 || css.len() > 200_000 {
        return Err("The template is too large to save.".into());
    }
    let base: i64 = conn.query_row("SELECT MAX(version) FROM template_versions WHERE template_id = ?1", params![id], |r| r.get(0)).map_err(|e| e.to_string())?;
    conn.execute(
        "INSERT INTO template_drafts (template_id, html_source, css_source, based_on_version) VALUES (?1,?2,?3,?4)
         ON CONFLICT(template_id) DO UPDATE SET html_source = excluded.html_source, css_source = excluded.css_source, updated_at = datetime('now')",
        params![id, html, css, base],
    )
    .map_err(|e| e.to_string())?;
    db::audit(conn, Some(actor), "template_draft_saved", &format!("template {id}"));
    Ok(())
}

/// Publishes the draft as the next immutable version. Templates with errors cannot be published.
pub fn publish(conn: &mut Connection, actor: i64, id: i64, summary: &str) -> Result<i64, String> {
    editable(conn, id)?;
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let (html, css): (String, String) = tx
        .query_row("SELECT html_source, css_source FROM template_drafts WHERE template_id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|_| "There is no draft to publish. Save your changes first.".to_string())?;
    let problems: Vec<Diagnostic> = eng::validate(&html, &css).into_iter().filter(|d| d.severity == "error").collect();
    if let Some(first) = problems.first() {
        return Err(format!("Fix {} error{} before publishing. First: line {} ({}): {}", problems.len(), if problems.len() == 1 { "" } else { "s" }, first.line, first.source.to_uppercase(), first.message));
    }
    let (latest, cur_html, cur_css): (i64, String, String) = tx
        .query_row(
            "SELECT version, COALESCE(html_source,''), COALESCE(css_source,'') FROM template_versions WHERE template_id = ?1 ORDER BY version DESC LIMIT 1",
            params![id],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .map_err(|e| e.to_string())?;
    if cur_html == html && cur_css == css {
        return Err("Nothing has changed since the latest published version.".into());
    }
    tx.execute(
        "INSERT INTO template_versions (template_id, version, body, html_source, css_source, change_summary, published_by) VALUES (?1,?2,'',?3,?4,?5,?6)",
        params![id, latest + 1, html, css, summary.trim(), actor],
    )
    .map_err(|e| e.to_string())?;
    tx.execute("DELETE FROM template_drafts WHERE template_id = ?1", params![id]).map_err(|e| e.to_string())?;
    db::audit(&tx, Some(actor), "template_published", &format!("template {id} v{}", latest + 1));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(latest + 1)
}

/// Puts an earlier version into the draft. Nothing published is changed; publishing the draft makes a new version.
pub fn restore(conn: &Connection, actor: i64, id: i64, version: i64) -> Result<(), String> {
    editable(conn, id)?;
    let (html, css): (String, String) = conn
        .query_row("SELECT COALESCE(html_source,''), COALESCE(css_source,'') FROM template_versions WHERE template_id = ?1 AND version = ?2", params![id, version], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|_| "That version was not found.".to_string())?;
    conn.execute(
        "INSERT INTO template_drafts (template_id, html_source, css_source, based_on_version) VALUES (?1,?2,?3,?4)
         ON CONFLICT(template_id) DO UPDATE SET html_source = excluded.html_source, css_source = excluded.css_source, based_on_version = excluded.based_on_version, updated_at = datetime('now')",
        params![id, html, css, version],
    )
    .map_err(|e| e.to_string())?;
    db::audit(conn, Some(actor), "template_version_restored_to_draft", &format!("template {id} from v{version}"));
    Ok(())
}

pub fn discard_draft(conn: &Connection, actor: i64, id: i64) -> Result<(), String> {
    editable(conn, id)?;
    conn.execute("DELETE FROM template_drafts WHERE template_id = ?1", params![id]).map_err(|e| e.to_string())?;
    db::audit(conn, Some(actor), "template_draft_discarded", &format!("template {id}"));
    Ok(())
}

pub fn create_blank(conn: &mut Connection, actor: i64, name: &str, family: &str, kind: &str) -> Result<i64, String> {
    if name.trim().is_empty() {
        return Err("Give the template a name.".into());
    }
    if !["consultancy", "billing", "administration"].contains(&family) || !["letter", "agreement"].contains(&kind) {
        return Err("Invalid template family or type.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    tx.execute("INSERT INTO templates (kind, name, family, layout, format) VALUES (?1,?2,?3,'letter','html')", params![kind, name.trim(), family]).map_err(|_| "A template with this name already exists.".to_string())?;
    let id = tx.last_insert_rowid();
    tx.execute(
        "INSERT INTO template_versions (template_id, version, body, html_source, css_source, change_summary, published_by) VALUES (?1,1,'',?2,?3,'First version',?4)",
        params![id, SKELETON_HTML, SKELETON_CSS, actor],
    )
    .map_err(|e| e.to_string())?;
    db::audit(&tx, Some(actor), "template_created", name.trim());
    tx.commit().map_err(|e| e.to_string())?;
    Ok(id)
}

#[derive(Serialize)]
pub struct StudioPreview {
    pub pdf_base64: String,
    pub pages: usize,
    pub landscape: bool,
    pub diagnostics: Vec<Diagnostic>,
    pub missing_required: Vec<String>,
}

fn company_value(conn: &Connection) -> Result<Value, String> {
    conn.query_row("SELECT name, address, email, phone, pan, gstin, coa_registration, architect_name FROM company_profile WHERE id = 1", [], |r| {
        Ok(json!({ "name": r.get::<_, String>(0)?, "address": r.get::<_, String>(1)?, "email": r.get::<_, String>(2)?, "phone": r.get::<_, String>(3)?,
                   "pan": r.get::<_, String>(4)?, "gstin": r.get::<_, String>(5)?, "coa_registration_number": r.get::<_, String>(6)?, "architect_name": r.get::<_, String>(7)? }))
    })
    .map_err(|e| e.to_string())
}

/// Renders HTML, CSS and data to PDF with the template's own paper and margins.
pub fn render_pdf(html: &str, css: &str, data: &Value) -> Result<(pdf::Rendered, eng::PageSetup, Vec<Diagnostic>), String> {
    let mut notes = Vec::new();
    let (setup, css) = eng::split_page_rule(css, &mut notes);
    let fragment = eng::render_fragment(html, data)?;
    let rendered = pdf::render_html_with_page(&eng::full_document(&fragment, &css), &pdf::bundled_fonts(), &setup)?;
    Ok((rendered, setup, notes))
}

pub fn preview(conn: &Connection, html: &str, css: &str, profile: &str) -> Result<StudioPreview, String> {
    let mut diagnostics = eng::validate(html, css);
    if eng::has_errors(&diagnostics) {
        return Ok(StudioPreview { pdf_base64: String::new(), pages: 0, landscape: false, diagnostics, missing_required: Vec::new() });
    }
    let company = if profile == "company" { Some(company_value(conn)?) } else { None };
    let data = eng::sample_data(profile, company.as_ref());
    let (rendered, setup, _) = render_pdf(html, css, &data)?;
    let missing_required = eng::missing_required(html, &data);
    for m in &missing_required {
        diagnostics.push(Diagnostic { severity: "warning", source: "html", line: 1, col: 1, message: format!("The required field {m} has no value in this sample data.") });
    }
    if rendered.pages > 12 {
        diagnostics.push(Diagnostic { severity: "warning", source: "html", line: 1, col: 1, message: format!("This renders to {} pages with the selected sample data. Check for content that is far longer than intended.", rendered.pages) });
    }
    Ok(StudioPreview { pdf_base64: base64::engine::general_purpose::STANDARD.encode(&rendered.bytes), pages: rendered.pages, landscape: setup.landscape(), diagnostics, missing_required })
}

// ---------------------------------------------------------------- commands

#[tauri::command]
pub fn studio_fields(state: State<AppState>) -> Result<Vec<eng::FieldDef>, String> {
    require(&state, Permission::View)?;
    Ok(eng::FIELDS.to_vec())
}

#[tauri::command]
pub fn studio_open(state: State<AppState>, template_id: i64) -> Result<StudioState, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    open(&conn, template_id)
}

#[tauri::command]
pub fn studio_validate(state: State<AppState>, html: String, css: String) -> Result<Vec<Diagnostic>, String> {
    require(&state, Permission::View)?;
    Ok(eng::validate(&html, &css))
}

#[tauri::command]
pub fn studio_preview(state: State<AppState>, html: String, css: String, profile: String) -> Result<StudioPreview, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    preview(&conn, &html, &css, &profile)
}

#[tauri::command]
pub fn studio_save_draft(state: State<AppState>, template_id: i64, html: String, css: String) -> Result<(), String> {
    let uid = require(&state, Permission::ManageSettings)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    save_draft(&conn, uid, template_id, &html, &css)
}

#[tauri::command]
pub fn studio_publish(state: State<AppState>, template_id: i64, summary: String) -> Result<i64, String> {
    let uid = require(&state, Permission::ManageSettings)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    publish(&mut conn, uid, template_id, &summary)
}

#[tauri::command]
pub fn studio_restore(state: State<AppState>, template_id: i64, version: i64) -> Result<(), String> {
    let uid = require(&state, Permission::ManageSettings)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    restore(&conn, uid, template_id, version)
}

#[tauri::command]
pub fn studio_discard_draft(state: State<AppState>, template_id: i64) -> Result<(), String> {
    let uid = require(&state, Permission::ManageSettings)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    discard_draft(&conn, uid, template_id)
}

#[tauri::command]
pub fn studio_create(state: State<AppState>, name: String, family: String, kind: String) -> Result<i64, String> {
    let uid = require(&state, Permission::ManageSettings)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    create_blank(&mut conn, uid, &name, &family, &kind)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn setup() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('o','x','owner')", []).unwrap();
        c.execute("UPDATE company_profile SET name='Studio X', address='Mumbai', architect_name='A. Architect'", []).unwrap();
        c
    }

    fn built_in(c: &Connection, key: &str) -> i64 {
        c.query_row("SELECT id FROM templates WHERE template_key = ?1", params![key], |r| r.get(0)).unwrap()
    }

    #[test]
    fn built_ins_are_read_only_and_copies_version_without_touching_the_original() {
        let mut c = setup();
        let orig = built_in(&c, "html-consultancy-proposal");
        assert!(save_draft(&c, 1, orig, "<p>x</p>", "").unwrap_err().contains("Make a copy"));
        let copy = crate::documents::duplicate_template(&mut c, 1, orig, "Our proposal").unwrap();
        let s = open(&c, copy).unwrap();
        assert!(!s.built_in && !s.has_draft && s.published_version == 1 && s.html.contains("fee_items") && s.css.contains("@page"));
        // draft, then publish
        assert!(publish(&mut c, 1, copy, "x").unwrap_err().contains("no draft"));
        save_draft(&c, 1, copy, &format!("{}<p>Extra {{{{ client.name }}}}</p>", s.html), &s.css).unwrap();
        assert!(open(&c, copy).unwrap().has_draft);
        assert_eq!(publish(&mut c, 1, copy, "Added a client line").unwrap(), 2);
        let after = open(&c, copy).unwrap();
        assert!(!after.has_draft && after.published_version == 2 && after.versions.len() == 2 && after.versions[0].change_summary == "Added a client line");
        // the original is untouched
        assert_eq!(open(&c, orig).unwrap().published_version, 1);
        // published versions are immutable
        assert!(c.execute("UPDATE template_versions SET html_source = 'x'", []).is_err());
        assert!(c.execute("DELETE FROM template_versions", []).is_err());
        // nothing changed
        save_draft(&c, 1, copy, &after.html, &after.css).unwrap();
        assert!(publish(&mut c, 1, copy, "").unwrap_err().contains("Nothing has changed"));
    }

    #[test]
    fn templates_with_errors_cannot_be_published_and_restore_makes_a_draft() {
        let mut c = setup();
        let id = create_blank(&mut c, 1, "Mine", "administration", "letter").unwrap();
        save_draft(&c, 1, id, "<p>{{ nope.field }}</p><script>x()</script>", "").unwrap();
        let err = publish(&mut c, 1, id, "bad").unwrap_err();
        assert!(err.contains("Fix 3 errors") && err.contains("line 1"), "{err}");
        save_draft(&c, 1, id, "<p>{{ client.name }}</p>", "p { color: #161616; }").unwrap();
        assert_eq!(publish(&mut c, 1, id, "v2").unwrap(), 2);
        restore(&c, 1, id, 1).unwrap();
        let s = open(&c, id).unwrap();
        assert!(s.has_draft && s.html.contains("document.title"), "restoring v1 puts it in the draft");
        assert_eq!(s.published_version, 2, "nothing published changed");
        assert!(restore(&c, 1, id, 99).is_err());
        discard_draft(&c, 1, id).unwrap();
        assert!(!open(&c, id).unwrap().has_draft);
        assert!(create_blank(&mut c, 1, "mine", "administration", "letter").is_err(), "names are unique ignoring case");
    }

    #[test]
    fn wording_templates_are_not_opened_in_the_studio_and_archived_ones_are_locked() {
        let mut c = setup();
        assert!(open(&c, built_in(&c, "meeting-minutes")).unwrap_err().contains("wording template"));
        let id = create_blank(&mut c, 1, "Mine", "administration", "letter").unwrap();
        crate::documents::set_template_archived(&c, 1, id, true).unwrap();
        assert!(save_draft(&c, 1, id, "<p>x</p>", "").unwrap_err().contains("Restore"));
    }

    #[test]
    fn preview_renders_the_real_pdf_reports_errors_and_never_stores_anything() {
        let mut c = setup();
        let t = open(&c, built_in(&c, "html-consultancy-proposal")).unwrap();
        let p = preview(&c, &t.html, &t.css, "short").unwrap();
        assert!(!p.pdf_base64.is_empty() && p.pages == 1 && !p.landscape);
        let long = preview(&c, &t.html, &t.css, "long").unwrap();
        assert!(long.pages >= 3);
        // errors stop the render and are reported with positions instead of failing
        let bad = preview(&c, "<p>{{ nope }}</p>", "", "short").unwrap();
        assert!(bad.pdf_base64.is_empty() && bad.diagnostics.iter().any(|d| d.severity == "error" && d.line == 1));
        // landscape through @page
        let land = preview(&c, "<p>{{ company.name }}</p>", "@page { size: A4 landscape; }", "short").unwrap();
        assert!(land.landscape);
        // the company profile is used for the "company" profile; empty required fields are flagged
        c.execute("UPDATE company_profile SET name = ''", []).unwrap();
        let m = preview(&c, "<p>{{ company.name }}</p>", "", "company").unwrap();
        assert_eq!(m.missing_required, vec!["company.name"]);
        assert_eq!(c.query_row("SELECT COUNT(*) FROM documents", [], |r| r.get::<_, i64>(0)).unwrap(), 0);
        let _ = create_blank(&mut c, 1, "x", "billing", "letter");
    }

    #[test]
    fn rendering_is_deterministic() {
        let data = eng::sample_data("short", None);
        let t = crate::builtin_html::LIBRARY.iter().find(|t| t.key == "html-letterhead-classic").unwrap();
        let a = render_pdf(t.html, t.css, &data).unwrap().0.bytes.len();
        let b = render_pdf(t.html, t.css, &data).unwrap().0.bytes.len();
        assert_eq!(a, b);
    }
}
