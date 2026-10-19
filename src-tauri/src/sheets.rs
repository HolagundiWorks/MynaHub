//! The document design system shared by invoices, letters, receipts and project reports.
//!
//! Families, all set in the IBM Plex scale from `typography`:
//! - Commercial: a 30/70 sheet. Identity in the narrow column, figures in the wide column, hairline rows.
//! - Correspondence: an asymmetric letterhead, a subject, a signature, and a contact rule.
//! - Editorial: a cover line and large section numbers with a thin divider, for project reports.
//! - Technical: a hairline schedule for drawing numbers, revisions and amounts.
//!
//! printpdf lays these out as tables. Flex and grid are not used. Letter-spacing is not used.
//! A paragraph inside a table cell is dropped, so cell text is inline with line breaks.
//! A border on a nested table is not stroked, so a hairline inside a table is a thin gap of ink
//! between white cells. Images need an explicit width and height in millimetres.
//!
//! Colour comes from the firm accent. A blank accent is near-black, so a sheet still prints in grayscale.
//! Issued PDFs stay frozen; a new accent applies only to documents rendered after it is saved.

use crate::typography::{self, token_style};

pub const INK: &str = "#1A1A1A";
pub const QUIET: &str = "#5C5C5C";

/// Blank stays blank. A six-digit hex colour is stored as `#RRGGBB`. Anything else is rejected.
pub fn normalize_accent(raw: &str) -> Result<String, String> {
    office_core::profile::normalize_accent(raw)
}

/// The colour rules and labels use. Blank or unreadable values fall back to near-black.
pub fn resolve_accent(raw: &str) -> String {
    normalize_accent(raw).ok().filter(|s| !s.is_empty()).unwrap_or_else(|| INK.to_string())
}

pub fn esc(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;").replace('\n', "<br/>")
}

pub fn display_date(iso: &str) -> String {
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

/// Shared type, ink and accent rules. Document CSS adds only its own geometry.
pub fn base_css(accent: &str) -> String {
    let accent = resolve_accent(accent);
    format!(
        "{base}
        .kicker {{ {meta} color: {accent}; }}
        .quiet {{ {meta} color: {quiet}; }}
        .display {{ {title} font-size: 28pt; }}
        .numeral {{ {title} font-size: 28pt; color: {accent}; }}
        .id {{ {id} }}",
        base = typography::css(),
        meta = token_style("meta"),
        title = token_style("title"),
        id = token_style("identifier"),
        quiet = QUIET,
    )
}

pub fn page(accent: &str, extra_css: &str, body: &str) -> String {
    format!(
        r#"<html><head><style>{css}{extra}</style></head><body>{body}</body></html>"#,
        css = base_css(accent),
        extra = extra_css,
        body = body,
    )
}

/// A numbered editorial section. `body` is already escaped HTML and must not wrap text in a paragraph.
pub fn section(number: &str, title: &str, body: &str) -> String {
    format!(
        r#"<table style="width: 178mm; table-layout: fixed; border-collapse: collapse; margin-top: 14pt;"><tr>
        <td style="width: 22mm; vertical-align: top;"><span class="numeral">{number}</span></td>
        <td style="width: 4mm; border-left: 0.6pt solid {ink};"></td>
        <td style="width: 152mm; vertical-align: top; padding-left: 4mm;"><span style="{section}">{title}</span><br/><br/>{body}</td>
        </tr></table>"#,
        number = esc(number),
        title = esc(title),
        body = body,
        ink = INK,
        section = token_style("section"),
    )
}

/// A hairline schedule. Widths are millimetres and must match the columns. `numeric` right-aligns a column.
pub fn schedule(widths_mm: &[u16], headers: &[&str], rows: &[Vec<String>], numeric: &[bool]) -> String {
    let total: u16 = widths_mm.iter().copied().sum();
    let cell = |text: &str, index: usize, head: bool| {
        let align = if numeric.get(index).copied().unwrap_or(false) { "text-align: right;" } else { "" };
        let face = if head { token_style("table-head") } else { token_style("table") };
        let width = widths_mm.get(index).copied().unwrap_or(20);
        format!(r#"<td style="width: {width}mm; background-color: #ffffff; padding: 4px 3px; {align}{face}">{text}</td>"#, text = esc(text))
    };
    let mut html = format!(
        r#"<table style="width: {total}mm; border-collapse: separate; border-spacing: 0 0.6px; background-color: {ink};">"#,
        ink = INK,
    );
    html.push_str("<tr>");
    for (index, header) in headers.iter().enumerate() {
        html.push_str(&cell(header, index, true));
    }
    html.push_str("</tr>");
    for row in rows {
        html.push_str("<tr>");
        for (index, value) in row.iter().enumerate() {
            html.push_str(&cell(value, index, false));
        }
        html.push_str("</tr>");
    }
    html.push_str("</table>");
    html
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_blank_accent_is_black_and_a_hex_colour_is_kept() {
        assert_eq!(normalize_accent("  ").unwrap(), "");
        assert_eq!(normalize_accent("#3e5240").unwrap(), "#3E5240");
        assert_eq!(normalize_accent("b04a32").unwrap(), "#B04A32");
        assert!(normalize_accent("red").is_err());
        assert!(normalize_accent("#fff").is_err());
        assert_eq!(resolve_accent(""), INK);
        assert_eq!(resolve_accent("nope"), INK);
        assert_eq!(resolve_accent("#1F4E8C"), "#1F4E8C");
    }

    #[test]
    fn a_section_keeps_its_number_and_escapes_the_title() {
        let html = section("01", "Site <north>", "Recorded");
        assert!(html.contains("01") && html.contains("Site &lt;north&gt;") && html.contains("Recorded"));
        assert!(!html.contains("<p>"));
    }
}
