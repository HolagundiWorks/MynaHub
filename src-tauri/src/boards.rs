//! Project reports and presentations submitted for a project. A presentation is a 16:9 slide deck.
//! Images and PDF annexures stay inside the company file. Drawing marks are stored as fractions of the slide.

use crate::auth::Permission;
use crate::billing::sha2_lite::sha256_hex;
use crate::commands::{HostState, require, INTERNAL};
use crate::db;
use crate::pdf::{self, DeckMark};
use base64::Engine;
use rusqlite::{params, Connection, OptionalExtension};
use serde::{Deserialize, Serialize};

const MAX_IMAGE: usize = 4_000_000;
const MAX_PDF: usize = 12_000_000;
const MAX_SLIDES: usize = 24;
const MAX_MARKS: usize = 60;
const MAX_FIGURES: usize = 24;
const MAX_ANNEXURES: i64 = 12;
const MAX_MATERIALS: i64 = 200;

#[derive(Deserialize)]
pub struct MarkInput {
    pub kind: String,
    pub x1: f64,
    pub y1: f64,
    pub x2: f64,
    pub y2: f64,
    #[serde(default)]
    pub label: String,
}

#[derive(Deserialize)]
pub struct FigureInput {
    pub file_key: String,
    pub x: f64,
    pub y: f64,
    pub w: f64,
    pub h: f64,
    #[serde(default)]
    pub caption: String,
}

#[derive(Deserialize)]
pub struct SlideInput {
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub body: String,
    #[serde(default)]
    pub marks: Vec<MarkInput>,
    #[serde(default)]
    pub figures: Vec<FigureInput>,
}

#[derive(Deserialize)]
pub struct BoardInput {
    pub id: Option<i64>,
    pub project_id: i64,
    pub kind: String,
    pub title: String,
    #[serde(default)]
    pub body: String,
    #[serde(default)]
    pub slides: Vec<SlideInput>,
    #[serde(default)]
    pub figures: Vec<FigureInput>,
}

#[derive(Serialize)]
pub struct BoardRow {
    pub id: i64,
    pub project_id: i64,
    pub project: String,
    pub client: String,
    pub kind: String,
    pub title: String,
    pub status: String,
    pub updated_at: String,
}

#[derive(Serialize)]
pub struct MarkRow {
    pub kind: String,
    pub x1: f64,
    pub y1: f64,
    pub x2: f64,
    pub y2: f64,
    pub label: String,
}

#[derive(Serialize)]
pub struct FigureRow {
    pub id: i64,
    pub file_key: String,
    pub x: f64,
    pub y: f64,
    pub w: f64,
    pub h: f64,
    pub caption: String,
}

#[derive(Serialize)]
pub struct SlideRow {
    pub id: i64,
    pub position: i64,
    pub title: String,
    pub body: String,
    pub marks: Vec<MarkRow>,
    pub figures: Vec<FigureRow>,
}

#[derive(Serialize)]
pub struct AnnexureRow {
    pub id: i64,
    pub filename: String,
    pub bytes: i64,
}

#[derive(Serialize)]
pub struct BoardDetail {
    pub id: i64,
    pub project_id: i64,
    pub project: String,
    pub client: String,
    pub kind: String,
    pub title: String,
    pub body: String,
    pub status: String,
    pub slides: Vec<SlideRow>,
    pub figures: Vec<FigureRow>,
    pub annexures: Vec<AnnexureRow>,
}

#[derive(Serialize)]
pub struct MaterialRow {
    pub id: i64,
    pub name: String,
    pub category: String,
    pub file_key: String,
    pub bytes: i64,
}

#[derive(Serialize)]
pub struct StoredImage {
    pub file_key: String,
}

#[derive(Serialize)]
pub struct ImageFile {
    pub mime: String,
    pub data_base64: String,
}

#[derive(Serialize)]
pub struct AnnexureFile {
    pub filename: String,
    pub data_base64: String,
}

pub fn list(conn: &Connection, project_id: Option<i64>, kind: Option<String>) -> Result<Vec<BoardRow>, String> {
    let kind = match kind.as_deref() {
        None | Some("") => None,
        Some("report") => Some("report"),
        Some("presentation") => Some("presentation"),
        _ => return Err("Choose a report or a presentation.".into()),
    };
    let mut stmt = conn
        .prepare(
            "SELECT b.id, b.project_id, p.name, c.name, b.kind, b.title, b.status, b.updated_at
             FROM boards b JOIN projects p ON p.id = b.project_id JOIN clients c ON c.id = p.client_id
             WHERE (?1 IS NULL OR b.project_id = ?1) AND (?2 IS NULL OR b.kind = ?2)
             ORDER BY b.updated_at DESC, b.id DESC",
        )
        .map_err(|e| e.to_string())?;
    stmt.query_map(params![project_id, kind], |r| {
        Ok(BoardRow {
            id: r.get(0)?,
            project_id: r.get(1)?,
            project: r.get(2)?,
            client: r.get(3)?,
            kind: r.get(4)?,
            title: r.get(5)?,
            status: r.get(6)?,
            updated_at: r.get(7)?,
        })
    })
    .map_err(|e| e.to_string())?
    .collect::<Result<Vec<_>, _>>()
    .map_err(|e| e.to_string())
}

pub fn get(conn: &Connection, id: i64) -> Result<BoardDetail, String> {
    let (project_id, project, client, kind, title, body, status): (i64, String, String, String, String, String, String) = conn
        .query_row(
            "SELECT b.project_id, p.name, c.name, b.kind, b.title, b.body, b.status
             FROM boards b JOIN projects p ON p.id = b.project_id JOIN clients c ON c.id = p.client_id WHERE b.id = ?1",
            params![id],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?, r.get(6)?)),
        )
        .optional()
        .map_err(|e| e.to_string())?
        .ok_or_else(|| "That report or presentation is not in this company.".to_string())?;
    Ok(BoardDetail {
        id,
        project_id,
        project,
        client,
        kind,
        title,
        body,
        status,
        slides: slides_of(conn, id)?,
        figures: figures_of(conn, id, None)?,
        annexures: annexures_of(conn, id)?,
    })
}

fn slides_of(conn: &Connection, board_id: i64) -> Result<Vec<SlideRow>, String> {
    let mut stmt = conn
        .prepare("SELECT id, position, title, body FROM board_slides WHERE board_id = ?1 ORDER BY position")
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![board_id], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, i64>(1)?, r.get::<_, String>(2)?, r.get::<_, String>(3)?)))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    let mut slides = Vec::new();
    for (id, position, title, body) in rows {
        slides.push(SlideRow { id, position, title, body, marks: marks_of(conn, id)?, figures: figures_of(conn, board_id, Some(id))? });
    }
    Ok(slides)
}

fn marks_of(conn: &Connection, slide_id: i64) -> Result<Vec<MarkRow>, String> {
    let mut stmt = conn
        .prepare("SELECT kind, x1, y1, x2, y2, label FROM board_marks WHERE slide_id = ?1 ORDER BY id")
        .map_err(|e| e.to_string())?;
    stmt.query_map(params![slide_id], |r| {
        Ok(MarkRow { kind: r.get(0)?, x1: r.get(1)?, y1: r.get(2)?, x2: r.get(3)?, y2: r.get(4)?, label: r.get(5)? })
    })
    .map_err(|e| e.to_string())?
    .collect::<Result<Vec<_>, _>>()
    .map_err(|e| e.to_string())
}

fn figures_of(conn: &Connection, board_id: i64, slide_id: Option<i64>) -> Result<Vec<FigureRow>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT id, file_key, x, y, w, h, caption FROM board_figures
             WHERE board_id = ?1 AND ((?2 IS NULL AND slide_id IS NULL) OR slide_id = ?2) ORDER BY id",
        )
        .map_err(|e| e.to_string())?;
    stmt.query_map(params![board_id, slide_id], |r| {
        Ok(FigureRow { id: r.get(0)?, file_key: r.get(1)?, x: r.get(2)?, y: r.get(3)?, w: r.get(4)?, h: r.get(5)?, caption: r.get(6)? })
    })
    .map_err(|e| e.to_string())?
    .collect::<Result<Vec<_>, _>>()
    .map_err(|e| e.to_string())
}

fn annexures_of(conn: &Connection, board_id: i64) -> Result<Vec<AnnexureRow>, String> {
    let mut stmt = conn.prepare("SELECT id, filename, bytes FROM board_annexures WHERE board_id = ?1 ORDER BY id").map_err(|e| e.to_string())?;
    stmt.query_map(params![board_id], |r| Ok(AnnexureRow { id: r.get(0)?, filename: r.get(1)?, bytes: r.get(2)? }))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())
}

pub fn save(conn: &Connection, user_id: i64, input: &BoardInput) -> Result<i64, String> {
    let kind = match input.kind.as_str() {
        "report" | "presentation" => input.kind.as_str(),
        _ => return Err("Choose a report or a presentation.".into()),
    };
    let title = required(&input.title, 140, "Give it a title.", "Keep the title to 140 characters.")?;
    let body = optional(&input.body, 12_000, "Keep the text to 12,000 characters.")?;
    let project: i64 = conn
        .query_row("SELECT COUNT(*) FROM projects WHERE id = ?1 AND archived = 0", params![input.project_id], |r| r.get(0))
        .map_err(|e| e.to_string())?;
    if project == 0 {
        return Err("Choose an active project.".into());
    }
    if kind == "report" {
        if !input.slides.is_empty() {
            return Err("A report is one text, with images and annexures.".into());
        }
        if input.figures.len() > MAX_FIGURES {
            return Err("A report can hold up to 24 images.".into());
        }
    } else if input.slides.is_empty() || input.slides.len() > MAX_SLIDES {
        return Err("A presentation needs at least one slide, and no more than 24.".into());
    }
    let tx = conn.unchecked_transaction().map_err(|e| e.to_string())?;
    let id = match input.id {
        Some(id) => {
            let status: String = tx
                .query_row("SELECT status FROM boards WHERE id = ?1", params![id], |r| r.get(0))
                .optional()
                .map_err(|e| e.to_string())?
                .ok_or_else(|| "That report or presentation is not in this company.".to_string())?;
            if status != "draft" {
                return Err("This has been submitted. Reopen it to change it.".into());
            }
            tx.execute(
                "UPDATE boards SET project_id = ?1, kind = ?2, title = ?3, body = ?4, updated_at = datetime('now') WHERE id = ?5",
                params![input.project_id, kind, title, body, id],
            )
            .map_err(|e| e.to_string())?;
            id
        }
        None => {
            tx.execute(
                "INSERT INTO boards (project_id, kind, title, body, created_by) VALUES (?1, ?2, ?3, ?4, ?5)",
                params![input.project_id, kind, title, body, user_id],
            )
            .map_err(|e| e.to_string())?;
            tx.last_insert_rowid()
        }
    };
    tx.execute("DELETE FROM board_figures WHERE board_id = ?1", params![id]).map_err(|e| e.to_string())?;
    tx.execute("DELETE FROM board_slides WHERE board_id = ?1", params![id]).map_err(|e| e.to_string())?;
    if kind == "report" {
        for figure in &input.figures {
            insert_figure(&tx, id, None, figure)?;
        }
    } else {
        for (index, slide) in input.slides.iter().enumerate() {
            let slide_title = optional(&slide.title, 140, "Keep a slide title to 140 characters.")?;
            let slide_body = optional(&slide.body, 4_000, "Keep a slide's text to 4,000 characters.")?;
            if slide.marks.len() > MAX_MARKS || slide.figures.len() > MAX_FIGURES {
                return Err("A slide can hold up to 60 marks and 24 images.".into());
            }
            tx.execute(
                "INSERT INTO board_slides (board_id, position, title, body) VALUES (?1, ?2, ?3, ?4)",
                params![id, index as i64 + 1, slide_title, slide_body],
            )
            .map_err(|e| e.to_string())?;
            let slide_id = tx.last_insert_rowid();
            for mark in &slide.marks {
                insert_mark(&tx, slide_id, mark)?;
            }
            for figure in &slide.figures {
                insert_figure(&tx, id, Some(slide_id), figure)?;
            }
        }
    }
    db::audit(&tx, Some(user_id), "board_saved", &id.to_string());
    tx.commit().map_err(|e| e.to_string())?;
    Ok(id)
}

fn insert_mark(conn: &Connection, slide_id: i64, mark: &MarkInput) -> Result<(), String> {
    let kind = match mark.kind.as_str() {
        "line" | "arrow" | "pointer" => mark.kind.as_str(),
        _ => return Err("A mark is a line, an arrow, or a pointer.".into()),
    };
    for value in [mark.x1, mark.y1, mark.x2, mark.y2] {
        if !(0.0..=1.0).contains(&value) {
            return Err("A mark has to stay on the slide.".into());
        }
    }
    let label = optional(&mark.label, 80, "Keep a pointer label to 80 characters.")?;
    conn.execute(
        "INSERT INTO board_marks (slide_id, kind, x1, y1, x2, y2, label) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
        params![slide_id, kind, mark.x1, mark.y1, mark.x2, mark.y2, label],
    )
    .map(|_| ())
    .map_err(|e| e.to_string())
}

fn insert_figure(conn: &Connection, board_id: i64, slide_id: Option<i64>, figure: &FigureInput) -> Result<(), String> {
    let key = figure.file_key.trim();
    if !owned_key(key) || !crate::store::exists(conn, key) {
        return Err("That image is not in this company file.".into());
    }
    for value in [figure.x, figure.y] {
        if !(0.0..=1.0).contains(&value) {
            return Err("An image has to stay on the slide.".into());
        }
    }
    if !(0.05..=1.0).contains(&figure.w) || !(0.05..=1.0).contains(&figure.h) {
        return Err("An image needs a size on the slide.".into());
    }
    let caption = optional(&figure.caption, 140, "Keep an image caption to 140 characters.")?;
    conn.execute(
        "INSERT INTO board_figures (board_id, slide_id, file_key, x, y, w, h, caption) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
        params![board_id, slide_id, key, figure.x, figure.y, figure.w, figure.h, caption],
    )
    .map(|_| ())
    .map_err(|e| e.to_string())
}

pub fn store_image(conn: &Connection, data: &[u8]) -> Result<String, String> {
    let mime = image_mime(data)?;
    let _ = mime;
    keep(conn, "boards", data)
}

pub fn submit(conn: &Connection, user_id: i64, id: i64) -> Result<(), String> {
    let changed = conn
        .execute("UPDATE boards SET status = 'submitted', submitted_at = datetime('now'), updated_at = datetime('now') WHERE id = ?1 AND status = 'draft'", params![id])
        .map_err(|e| e.to_string())?;
    if changed == 0 {
        return Err("That report or presentation is not a draft.".into());
    }
    db::audit(conn, Some(user_id), "board_submitted", &id.to_string());
    Ok(())
}

pub fn reopen(conn: &Connection, user_id: i64, id: i64) -> Result<(), String> {
    let changed = conn
        .execute("UPDATE boards SET status = 'draft', submitted_at = NULL, updated_at = datetime('now') WHERE id = ?1 AND status = 'submitted'", params![id])
        .map_err(|e| e.to_string())?;
    if changed == 0 {
        return Err("That report or presentation is not submitted.".into());
    }
    db::audit(conn, Some(user_id), "board_reopened", &id.to_string());
    Ok(())
}

pub fn add_annexure(conn: &Connection, user_id: i64, board_id: i64, filename: &str, data: &[u8]) -> Result<i64, String> {
    ensure_draft(conn, board_id)?;
    if data.len() > MAX_PDF || !data.starts_with(b"%PDF-") {
        return Err("Choose a PDF smaller than 12 MB.".into());
    }
    let name = filename.trim();
    let name = name.rsplit(['/', '\\']).next().unwrap_or(name).trim();
    if name.is_empty() || name.chars().count() > 120 {
        return Err("The PDF needs a file name of up to 120 characters.".into());
    }
    let count: i64 = conn.query_row("SELECT COUNT(*) FROM board_annexures WHERE board_id = ?1", params![board_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if count >= MAX_ANNEXURES {
        return Err("A submission can hold up to 12 PDF annexures.".into());
    }
    let key = keep(conn, "annexures", data)?;
    conn.execute(
        "INSERT INTO board_annexures (board_id, filename, file_key, bytes) VALUES (?1, ?2, ?3, ?4)",
        params![board_id, name, key, data.len() as i64],
    )
    .map_err(|e| e.to_string())?;
    let id = conn.last_insert_rowid();
    db::audit(conn, Some(user_id), "board_annexure", &board_id.to_string());
    Ok(id)
}

pub fn annexure_file(conn: &Connection, id: i64) -> Result<AnnexureFile, String> {
    let (filename, key): (String, String) = conn
        .query_row("SELECT filename, file_key FROM board_annexures WHERE id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|_| "That annexure is not in this company.".to_string())?;
    let bytes = crate::store::get(conn, &key)?;
    Ok(AnnexureFile { filename, data_base64: base64::engine::general_purpose::STANDARD.encode(bytes) })
}

pub fn figure_file(conn: &Connection, id: i64) -> Result<ImageFile, String> {
    let key: String = conn
        .query_row("SELECT file_key FROM board_figures WHERE id = ?1", params![id], |r| r.get(0))
        .map_err(|_| "That image is not on this report or presentation.".to_string())?;
    image_file(conn, &key)
}

pub fn materials(conn: &Connection) -> Result<Vec<MaterialRow>, String> {
    let mut stmt = conn.prepare("SELECT id, name, category, file_key, bytes FROM materials ORDER BY name, id").map_err(|e| e.to_string())?;
    stmt.query_map([], |r| Ok(MaterialRow { id: r.get(0)?, name: r.get(1)?, category: r.get(2)?, file_key: r.get(3)?, bytes: r.get(4)? }))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())
}

pub fn add_material(conn: &Connection, user_id: i64, name: &str, category: &str, data: &[u8]) -> Result<i64, String> {
    let name = required(name, 80, "Name the material.", "Keep the material name to 80 characters.")?;
    let category = optional(category, 40, "Keep the category to 40 characters.")?;
    image_mime(data)?;
    let count: i64 = conn.query_row("SELECT COUNT(*) FROM materials", [], |r| r.get(0)).map_err(|e| e.to_string())?;
    if count >= MAX_MATERIALS {
        return Err("The materials library can hold 200 images.".into());
    }
    let key = keep(conn, "materials", data)?;
    conn.execute("INSERT INTO materials (name, category, file_key, bytes) VALUES (?1, ?2, ?3, ?4)", params![name, category, key, data.len() as i64])
        .map_err(|e| e.to_string())?;
    let id = conn.last_insert_rowid();
    db::audit(conn, Some(user_id), "material_added", &id.to_string());
    Ok(id)
}

pub fn material_file(conn: &Connection, id: i64) -> Result<ImageFile, String> {
    let key: String = conn.query_row("SELECT file_key FROM materials WHERE id = ?1", params![id], |r| r.get(0)).map_err(|_| "That material is not in the library.".to_string())?;
    image_file(conn, &key)
}

pub fn remove_material(conn: &Connection, user_id: i64, id: i64) -> Result<(), String> {
    let key: String = conn.query_row("SELECT file_key FROM materials WHERE id = ?1", params![id], |r| r.get(0)).map_err(|_| "That material is not in the library.".to_string())?;
    let used: i64 = conn.query_row("SELECT COUNT(*) FROM board_figures WHERE file_key = ?1", params![key], |r| r.get(0)).map_err(|e| e.to_string())?;
    if used > 0 {
        return Err("This material is on a report or a presentation, so it stays in the library.".into());
    }
    conn.execute("DELETE FROM materials WHERE id = ?1", params![id]).map_err(|e| e.to_string())?;
    db::audit(conn, Some(user_id), "material_removed", &id.to_string());
    Ok(())
}

pub fn pdf(conn: &Connection, id: i64) -> Result<Vec<u8>, String> {
    let board = get(conn, id)?;
    let firm: String = conn.query_row("SELECT name FROM company_profile WHERE id = 1", [], |r| r.get(0)).unwrap_or_default();
    if board.kind == "report" {
        let logos = crate::commands::company_logos_b64(conn);
        let mut images = pdf::logo_images(&logos.square, &logos.wide);
        for (index, figure) in board.figures.iter().enumerate() {
            let bytes = crate::store::get(conn, &figure.file_key)?;
            images.insert(format!("figure-{index}"), printpdf::Base64OrRaw::Raw(bytes));
        }
        let letterhead: String = conn.query_row("SELECT letterhead_style FROM company_profile WHERE id = 1", [], |r| r.get(0)).unwrap_or_else(|_| "classic".into());
        let html = report_html(&firm, &board, &logos.square, &logos.wide, &letterhead);
        let rendered = if letterhead == "minimal" {
            pdf::render_html_with_qr_and_images(&html, &pdf::bundled_fonts(), &images, None)?
        } else {
            pdf::render_letter_sheet(&html, &pdf::bundled_fonts(), &images)?
        };
        return Ok(rendered.bytes);
    }
    let (html, images, marks) = deck_html(&firm, &board, conn)?;
    Ok(pdf::render_deck(&html, &images, &marks)?.bytes)
}

fn report_html(firm: &str, board: &BoardDetail, square: &str, wide: &str, letterhead: &str) -> String {
    let mut body = String::new();
    let parts: Vec<&str> = board.body.split("[[image]]").collect();
    for (index, part) in parts.iter().enumerate() {
        for line in part.lines() {
            let line = line.trim_end();
            if line.is_empty() {
                body.push_str("<br/>");
            } else {
                body.push_str(&format!("<p>{}</p>", esc(line)));
            }
        }
        if let Some(figure) = board.figures.get(index) {
            if index + 1 < parts.len() {
                body.push_str(&figure_html(figure, index));
            }
        }
    }
    for (index, figure) in board.figures.iter().enumerate().skip(parts.len().saturating_sub(1)) {
        body.push_str(&figure_html(figure, index));
    }
    let annex = annex_html(&board.annexures);
    let marks = pdf::letterhead_marks(square, wide);
    let top = if letterhead == "minimal" { "16mm" } else { "22mm" };
    format!(
        r#"<html><head><style>{css} body {{ margin: {top} 16mm 22mm 16mm; }} h1 {{ color: #1a1a1a; }}</style></head><body>
        {marks}
        <table style="width: 178mm; border-collapse: collapse;"><tr>
        <td style="width: 90mm; vertical-align: top;"><span style="font-family: 'IBM Plex Sans SemiBold'; font-size: 12pt;">{firm}</span></td>
        <td style="width: 88mm; vertical-align: top; text-align: right;" class="meta">{client}<br/>{project}</td>
        </tr><tr><td colspan="2" style="border-bottom: 0.6pt solid #1a1a1a; padding-bottom: 8pt;"></td></tr></table>
        <h1>{title}</h1>
        {body}{annex}</body></html>"#,
        css = crate::typography::css(),
        firm = esc(firm),
        title = esc(&board.title),
        client = esc(&board.client),
        project = esc(&board.project),
    )
}

fn figure_html(figure: &FigureRow, index: usize) -> String {
    let caption = if figure.caption.is_empty() { String::new() } else { format!("<p class=\"meta\">{}</p>", esc(&figure.caption)) };
    format!(
        "<img src=\"figure-{index}\" style=\"width: {w:.0}mm; height: {h:.0}mm;\" />{caption}",
        w = (figure.w * 160.0).clamp(20.0, 160.0),
        h = (figure.h * 90.0).clamp(16.0, 90.0),
    )
}

fn annex_html(annexures: &[AnnexureRow]) -> String {
    if annexures.is_empty() {
        return String::new();
    }
    let mut rows = String::new();
    for annex in annexures {
        rows.push_str(&format!("<p>{}</p>", esc(&annex.filename)));
    }
    format!("<h2>Annexures</h2><p class=\"meta\">These PDFs are kept with the submission.</p>{rows}")
}

fn deck_html(firm: &str, board: &BoardDetail, conn: &Connection) -> Result<(String, std::collections::BTreeMap<String, printpdf::Base64OrRaw>, Vec<Vec<DeckMark>>), String> {
    use printpdf::Base64OrRaw;
    let mut images = std::collections::BTreeMap::new();
    let mut marks = Vec::new();
    let mut sections = String::new();
    for (index, slide) in board.slides.iter().enumerate() {
        let class = if index == 0 { "" } else { " class=\"next\"" };
        let mut figures = String::new();
        let mut row: Vec<String> = Vec::new();
        let mut band_y = f64::MIN;
        let flush = |row: &mut Vec<String>, figures: &mut String| {
            if row.is_empty() {
                return;
            }
            figures.push_str("<table style=\"width: 310mm; border-collapse: collapse;\"><tr>");
            for cell in row.drain(..) {
                figures.push_str(&cell);
            }
            figures.push_str("</tr></table>");
        };
        for (n, figure) in slide.figures.iter().enumerate() {
            let key = format!("s{index}f{n}");
            let bytes = crate::store::get(conn, &figure.file_key)?;
            images.insert(key.clone(), Base64OrRaw::Raw(bytes));
            if (figure.y - band_y).abs() > 0.12 && !row.is_empty() {
                flush(&mut row, &mut figures);
            }
            band_y = figure.y;
            let gap = (figure.x * 310.0).clamp(0.0, 260.0);
            let w = (figure.w * 310.0).clamp(18.0, 310.0);
            let h = (figure.h * 160.0).clamp(14.0, 160.0);
            let caption = if figure.caption.is_empty() { String::new() } else { format!("<br/><span class=\"meta\">{}</span>", esc(&figure.caption)) };
            row.push(format!(
                "<td style=\"width: {gap:.0}mm;\"></td><td style=\"width: {w:.0}mm; vertical-align: top;\"><img src=\"{key}\" style=\"width: {w:.0}mm; height: {h:.0}mm;\" />{caption}</td>"
            ));
        }
        flush(&mut row, &mut figures);
        let title = if slide.title.is_empty() { board.title.as_str() } else { slide.title.as_str() };
        sections.push_str(&format!(
            "<div{class}><p class=\"meta\">{firm} · {project}</p><h1>{title}</h1>{body}{figures}</div>",
            firm = esc(firm),
            project = esc(&board.project),
            title = esc(title),
            body = paragraphs(&slide.body),
        ));
        marks.push(
            slide
                .marks
                .iter()
                .map(|mark| DeckMark {
                    kind: mark.kind.clone(),
                    x1: mark.x1 as f32,
                    y1: mark.y1 as f32,
                    x2: mark.x2 as f32,
                    y2: mark.y2 as f32,
                    label: mark.label.clone(),
                })
                .collect(),
        );
    }
    let html = format!(
        r#"<html><head><style>
        body {{ font-family: 'IBM Plex Sans'; font-size: 14pt; color: #1a1a1a; }}
        h1 {{ font-family: 'IBM Plex Sans SemiBold'; font-size: 26pt; font-weight: 400; margin: 0 0 6pt 0; color: #1a1a1a; }}
        .meta {{ font-family: 'IBM Plex Sans'; font-size: 9pt; color: #5c5c5c; }}
        .next {{ page-break-before: always; }}
        p {{ margin: 0 0 4pt 0; }}
        </style></head><body>{sections}</body></html>"#
    );
    Ok((html, images, marks))
}

fn paragraphs(text: &str) -> String {
    let mut html = String::new();
    for line in text.lines() {
        let line = line.trim_end();
        if line.is_empty() {
            html.push_str("<br/>");
        } else {
            html.push_str(&format!("<p>{}</p>", esc(line)));
        }
    }
    html
}

fn image_file(conn: &Connection, key: &str) -> Result<ImageFile, String> {
    if !owned_key(key) {
        return Err("That image is not in this company file.".into());
    }
    let bytes = crate::store::get(conn, key)?;
    Ok(ImageFile { mime: image_mime(&bytes)?.to_string(), data_base64: base64::engine::general_purpose::STANDARD.encode(bytes) })
}

fn owned_key(key: &str) -> bool {
    key.starts_with("boards/") || key.starts_with("materials/")
}

fn keep(conn: &Connection, folder: &str, data: &[u8]) -> Result<String, String> {
    let key = format!("{folder}/{}", sha256_hex(data));
    if !crate::store::exists(conn, &key) {
        crate::store::put(conn, &key, data)?;
    }
    Ok(key)
}

fn image_mime(data: &[u8]) -> Result<&'static str, String> {
    if data.len() > MAX_IMAGE {
        return Err("The image must be smaller than 4 MB.".into());
    }
    if data.starts_with(b"\x89PNG\r\n\x1a\n") {
        return Ok("image/png");
    }
    if data.len() >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF {
        return Ok("image/jpeg");
    }
    Err("Choose a PNG or JPEG image.".into())
}

fn ensure_draft(conn: &Connection, id: i64) -> Result<(), String> {
    let status: String = conn.query_row("SELECT status FROM boards WHERE id = ?1", params![id], |r| r.get(0)).map_err(|_| "That report or presentation is not in this company.".to_string())?;
    if status != "draft" {
        return Err("This has been submitted. Reopen it to change it.".into());
    }
    Ok(())
}

fn required(value: &str, max: usize, empty: &str, long: &str) -> Result<String, String> {
    let text = value.trim();
    if text.is_empty() {
        return Err(empty.into());
    }
    if text.chars().count() > max {
        return Err(long.into());
    }
    Ok(text.to_string())
}

fn optional(value: &str, max: usize, long: &str) -> Result<String, String> {
    let text = value.trim();
    if text.chars().count() > max {
        return Err(long.into());
    }
    Ok(text.to_string())
}

fn esc(value: &str) -> String {
    value.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;")
}

fn decode(data: &str) -> Result<Vec<u8>, String> {
    base64::engine::general_purpose::STANDARD.decode(data.trim()).map_err(|_| "The file could not be read.".to_string())
}

#[tauri::command]
pub fn board_list(state: HostState<'_>, project_id: Option<i64>, kind: Option<String>) -> Result<Vec<BoardRow>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list(&conn, project_id, kind)
}

#[tauri::command]
pub fn board_get(state: HostState<'_>, id: i64) -> Result<BoardDetail, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    get(&conn, id)
}

#[tauri::command]
pub fn board_save(state: HostState<'_>, board: BoardInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    save(&conn, uid, &board)
}

#[tauri::command]
pub fn board_submit(state: HostState<'_>, id: i64) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    submit(&conn, uid, id)
}

#[tauri::command]
pub fn board_reopen(state: HostState<'_>, id: i64) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    reopen(&conn, uid, id)
}

#[tauri::command]
pub fn board_store_image(state: HostState<'_>, data_base64: String) -> Result<StoredImage, String> {
    require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    Ok(StoredImage { file_key: store_image(&conn, &decode(&data_base64)?)? })
}

#[tauri::command]
pub fn board_pdf(state: HostState<'_>, id: i64) -> Result<String, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    Ok(base64::engine::general_purpose::STANDARD.encode(pdf(&conn, id)?))
}

#[tauri::command]
pub fn board_annexure_add(state: HostState<'_>, board_id: i64, filename: String, data_base64: String) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    add_annexure(&conn, uid, board_id, &filename, &decode(&data_base64)?)
}

#[tauri::command]
pub fn board_annexure_open(state: HostState<'_>, id: i64) -> Result<AnnexureFile, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    annexure_file(&conn, id)
}

#[tauri::command]
pub fn board_figure_open(state: HostState<'_>, id: i64) -> Result<ImageFile, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    figure_file(&conn, id)
}

#[tauri::command]
pub fn material_list(state: HostState<'_>) -> Result<Vec<MaterialRow>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    materials(&conn)
}

#[tauri::command]
pub fn material_add(state: HostState<'_>, name: String, category: String, data_base64: String) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    add_material(&conn, uid, &name, &category, &decode(&data_base64)?)
}

#[tauri::command]
pub fn material_open(state: HostState<'_>, id: i64) -> Result<ImageFile, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    material_file(&conn, id)
}

#[tauri::command]
pub fn material_remove(state: HostState<'_>, id: i64) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    remove_material(&conn, uid, id)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('ada','x','owner')", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1,'Villa')", []).unwrap();
        c.execute("UPDATE company_profile SET name = 'Studio X'", []).unwrap();
        c
    }

    fn png() -> &'static [u8] {
        b"\x89PNG\r\n\x1a\nrest-of-the-image"
    }

    #[test]
    fn a_presentation_keeps_its_slide_marks_and_exports_a_widescreen_pdf() {
        let c = conn();
        let key = store_image(&c, png()).unwrap();
        let id = save(
            &c,
            1,
            &BoardInput {
                id: None,
                project_id: 1,
                kind: "presentation".into(),
                title: "Concept".into(),
                body: String::new(),
                slides: vec![
                    SlideInput {
                        title: "Site".into(),
                        body: "The approach from the west.".into(),
                        marks: vec![MarkInput { kind: "arrow".into(), x1: 0.2, y1: 0.7, x2: 0.55, y2: 0.4, label: String::new() }],
                        figures: vec![FigureInput { file_key: key.clone(), x: 0.08, y: 0.35, w: 0.4, h: 0.4, caption: "Stone".into() }],
                    },
                    SlideInput {
                        title: "Mood".into(),
                        body: "Warm plaster, dark timber.".into(),
                        marks: vec![MarkInput { kind: "pointer".into(), x1: 0.1, y1: 0.2, x2: 0.4, y2: 0.5, label: "Timber".into() }],
                        figures: vec![],
                    },
                ],
                figures: vec![],
            },
        )
        .unwrap();
        let board = get(&c, id).unwrap();
        assert_eq!(board.slides.len(), 2);
        assert_eq!(board.slides[0].marks[0].kind, "arrow");
        assert_eq!(board.slides[0].figures[0].caption, "Stone");
        let bytes = pdf(&c, id).unwrap();
        assert!(bytes.starts_with(b"%PDF"));
        let rendered = {
            let detail = get(&c, id).unwrap();
            let (html, images, marks) = deck_html("Studio X", &detail, &c).unwrap();
            pdf::render_deck(&html, &images, &marks).unwrap()
        };
        assert_eq!(rendered.pages, 2, "a two-slide deck is two 16:9 pages");
        submit(&c, 1, id).unwrap();
        assert!(save(&c, 1, &BoardInput { id: Some(id), project_id: 1, kind: "presentation".into(), title: "Changed".into(), body: String::new(), slides: vec![SlideInput { title: "Only".into(), body: String::new(), marks: vec![], figures: vec![] }], figures: vec![] }).unwrap_err().contains("submitted"));
        reopen(&c, 1, id).unwrap();
        assert_eq!(get(&c, id).unwrap().status, "draft");
    }

    #[test]
    fn a_report_places_images_in_the_text_and_keeps_a_pdf_annexure() {
        let c = conn();
        let key = store_image(&c, png()).unwrap();
        let id = save(
            &c,
            1,
            &BoardInput {
                id: None,
                project_id: 1,
                kind: "report".into(),
                title: "Design report".into(),
                body: "The brief.\n[[image]]\nWhat follows.".into(),
                slides: vec![],
                figures: vec![FigureInput { file_key: key, x: 0.0, y: 0.0, w: 0.5, h: 0.4, caption: "Model".into() }],
            },
        )
        .unwrap();
        let annex = add_annexure(&c, 1, id, "survey.pdf", b"%PDF-1.4 survey").unwrap();
        let opened = annexure_file(&c, annex).unwrap();
        assert_eq!(opened.filename, "survey.pdf");
        assert!(add_annexure(&c, 1, id, "notes.txt", b"hello").is_err());
        let bytes = pdf(&c, id).unwrap();
        assert!(bytes.starts_with(b"%PDF"));
        let material = add_material(&c, 1, "Lime plaster", "Finish", png()).unwrap();
        assert_eq!(materials(&c).unwrap()[0].name, "Lime plaster");
        let file = material_file(&c, material).unwrap();
        assert_eq!(file.mime, "image/png");
    }
}
