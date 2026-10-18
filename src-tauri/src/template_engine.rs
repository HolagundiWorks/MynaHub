//! The Template Studio engine: restricted, logic-less HTML templates with an allow-listed set of placeholders.
//!
//! A template is HTML (a fragment, no scripts) plus CSS, with these placeholders and nothing else:
//!   {{ company.name }}                 a field from the schema below, HTML-escaped
//!   {{ company.name | upper }}         with one allow-listed helper (upper, lower, date, default "text", multiline)
//!   {{#if client.name}} .. {{else}} .. {{/if}}
//!   {{#each fee_items}} {{ description }} {{/each}}   a repeated block over a list in the schema
//! There is no arithmetic, no expressions, no user-defined functions and no way to call anything. Money and totals come
//! from the application's domain logic as ready-formatted values. Unknown fields and malformed syntax are rejected
//! before a template can be published, and anything that gets through is escaped when output.
//!
//! The same module validates the HTML (tag and attribute allow-list, no scripts, handlers or remote resources) and the CSS
//! (no @import, no remote url(), no behaviours; properties the PDF renderer cannot honour are reported as warnings).
use crate::billing::esc;
use serde::Serialize;
use serde_json::{json, Value};

// ---------------------------------------------------------------- schema

#[derive(Clone, Copy, PartialEq, Eq, Debug, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Kind {
    Text,
    Date,
    Money,
    /// Free text from the person writing the document; rendered as escaped paragraphs.
    Block,
    List,
}

#[derive(Serialize, Clone, Copy)]
pub struct FieldDef {
    pub path: &'static str,
    pub group: &'static str,
    pub label: &'static str,
    pub kind: Kind,
    pub required: bool,
    pub example: &'static str,
    /// When the value exists; shown in the Fields panel.
    pub available: &'static str,
}

const fn f(path: &'static str, group: &'static str, label: &'static str, kind: Kind, required: bool, example: &'static str, available: &'static str) -> FieldDef {
    FieldDef { path, group, label, kind, required, example, available }
}

use Kind::*;

pub const FIELDS: &[FieldDef] = &[
    f("company.name", "Company", "Firm name", Text, true, "Studio Meridian Architects", "Always"),
    f("company.address", "Company", "Address", Text, false, "14 Residency Road, Bengaluru 560025", "When set in the company profile"),
    f("company.email", "Company", "Email", Text, false, "studio@meridian.example", "When set in the company profile"),
    f("company.phone", "Company", "Phone", Text, false, "+91 80 5550 1234", "When set in the company profile"),
    f("company.pan", "Company", "PAN", Text, false, "ABCDE1234F", "When set in the company profile"),
    f("company.gstin", "Company", "GSTIN", Text, false, "29ABCDE1234F1Z5", "When the firm is GST registered"),
    f("company.coa_registration_number", "Company", "COA registration number", Text, false, "CA/2001/12345", "When set in the company profile"),
    f("company.architect_name", "Company", "Architect name", Text, false, "A. Architect", "When set in the company profile"),
    f("client.name", "Client", "Client name", Text, true, "Acme Developers Pvt Ltd", "When a project is selected"),
    f("client.address", "Client", "Client address", Text, false, "Pune", "When a project is selected"),
    f("project.name", "Project", "Project name", Text, true, "Baner Villa", "When a project is selected"),
    f("project.code", "Project", "Project code", Text, false, "P-014", "When a project is selected"),
    f("project.site_address", "Project", "Site address", Text, false, "Plot 5, Baner, Pune", "When a project is selected"),
    f("fee.total", "Fee", "Total professional fee", Money, false, "₹5,00,000.00", "On agreements and fee documents"),
    f("fee_items", "Fee", "Fee milestones (repeat)", List, false, "", "When a milestone schedule is entered"),
    f("tax.scheme", "Tax", "GST registration", Text, false, "Regular", "Always"),
    f("payment.bank_details", "Payment", "Bank details", Text, false, "HDFC Bank, A/c 0123456789, IFSC HDFC0000123", "When set in the company profile"),
    f("payment.upi_id", "Payment", "UPI ID", Text, false, "studio@bank", "When set in the company profile"),
    f("document.number", "Document", "Document number", Text, false, "OUT/2026-27/0001", "Assigned when issued; PREVIEW before"),
    f("document.date", "Document", "Document date", Date, true, "2026-10-05", "Always"),
    f("document.title", "Document", "Title", Text, false, "Consultancy proposal", "When a title or subject is entered"),
    f("document.subject", "Document", "Subject", Text, false, "Proposal for architectural services", "When entered"),
    f("document.reference", "Document", "Reference", Text, false, "MA/2026/041", "When entered"),
    f("document.body", "Document", "Document text (written for each document)", Block, false, "Paragraphs typed when the document is drafted.", "When entered"),
    f("addressee.name", "Document", "Addressee", Text, false, "Acme Developers Pvt Ltd", "Defaults to the client"),
    f("addressee.address", "Document", "Addressee address", Text, false, "Pune", "Defaults to the client"),
    f("signatory.name", "Signatory", "Signatory name", Text, false, "A. Architect", "When an architect name is set"),
    f("signatory.firm", "Signatory", "Signing firm", Text, true, "Studio Meridian Architects", "Always"),
];

/// Fields available inside `{{#each <list>}}`.
pub const LISTS: &[(&str, &[(&str, &str)])] = &[("fee_items", &[("no", "Number"), ("description", "Milestone"), ("share", "Share of the fee"), ("formatted_amount", "Amount")])];

pub fn field(path: &str) -> Option<&'static FieldDef> {
    FIELDS.iter().find(|d| d.path == path)
}

const HELPERS: &[&str] = &["upper", "lower", "date", "default", "multiline"];

// ---------------------------------------------------------------- diagnostics

#[derive(Serialize, Clone, Debug, PartialEq)]
pub struct Diagnostic {
    pub severity: &'static str,
    /// Which source the problem is in: `html` or `css`.
    pub source: &'static str,
    pub line: usize,
    pub col: usize,
    pub message: String,
}

fn at(src: &str, offset: usize) -> (usize, usize) {
    let before = &src[..offset.min(src.len())];
    let line = before.matches('\n').count() + 1;
    let col = before.rsplit('\n').next().map(|l| l.chars().count()).unwrap_or(0) + 1;
    (line, col)
}

fn diag(severity: &'static str, source: &'static str, text: &str, offset: usize, message: impl Into<String>) -> Diagnostic {
    let (line, col) = at(text, offset);
    Diagnostic { severity, source, line, col, message: message.into() }
}

pub fn has_errors(d: &[Diagnostic]) -> bool {
    d.iter().any(|x| x.severity == "error")
}

// ---------------------------------------------------------------- template syntax

#[derive(Debug, Clone)]
enum Node {
    Text(String),
    Var { path: String, helpers: Vec<(String, Option<String>)>, pos: usize },
    Each { list: String, body: Vec<Node>, pos: usize },
    If { path: String, yes: Vec<Node>, no: Vec<Node>, pos: usize },
}

#[derive(Debug)]
enum Tok {
    Text(String),
    Tag(String, usize),
}

fn lex(src: &str) -> Result<Vec<Tok>, (usize, String)> {
    let mut out = Vec::new();
    let mut i = 0;
    while let Some(rel) = src[i..].find("{{") {
        let start = i + rel;
        if start > i {
            out.push(Tok::Text(src[i..start].to_string()));
        }
        if src[start..].starts_with("{{{") {
            return Err((start, "Triple braces ({{{ }}}) insert raw HTML and are not allowed. Use {{ }}.".into()));
        }
        let end = src[start + 2..].find("}}").ok_or((start, "A {{ placeholder is not closed with }}.".to_string()))?;
        out.push(Tok::Tag(src[start + 2..start + 2 + end].trim().to_string(), start));
        i = start + 2 + end + 2;
    }
    if i < src.len() {
        out.push(Tok::Text(src[i..].to_string()));
    }
    Ok(out)
}

fn parse_expr(tag: &str, pos: usize) -> Result<Node, (usize, String)> {
    let mut parts = tag.split('|').map(str::trim);
    let path = parts.next().unwrap_or("").to_string();
    if path.is_empty() || !path.chars().all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '.') {
        return Err((pos, format!("“{tag}” is not a valid placeholder. Use a field name such as company.name.")));
    }
    let mut helpers = Vec::new();
    for h in parts {
        let (name, arg) = match h.split_once(char::is_whitespace) {
            Some((n, a)) => (n.trim(), Some(a.trim())),
            None => (h, None),
        };
        if !HELPERS.contains(&name) {
            return Err((pos, format!("Unknown helper “{name}”. Allowed: {}.", HELPERS.join(", "))));
        }
        let arg = match arg {
            Some(a) if a.len() >= 2 && a.starts_with('"') && a.ends_with('"') => Some(a[1..a.len() - 1].to_string()),
            Some(a) => return Err((pos, format!("The argument {a} must be in double quotes."))),
            None => None,
        };
        if name == "default" && arg.is_none() {
            return Err((pos, "default needs a quoted fallback, for example | default \"Not stated\".".into()));
        }
        helpers.push((name.to_string(), arg));
    }
    Ok(Node::Var { path, helpers, pos })
}

fn parse(src: &str) -> Result<Vec<Node>, (usize, String)> {
    enum Frame {
        Root,
        Each { list: String, pos: usize },
        If { path: String, pos: usize, yes: Option<Vec<Node>> },
    }
    let mut stack: Vec<(Frame, Vec<Node>)> = vec![(Frame::Root, Vec::new())];
    for tok in lex(src)? {
        match tok {
            Tok::Text(t) => stack.last_mut().unwrap().1.push(Node::Text(t)),
            Tok::Tag(tag, pos) => {
                if let Some(rest) = tag.strip_prefix("#each") {
                    let list = rest.trim().to_string();
                    if list.is_empty() {
                        return Err((pos, "{{#each}} needs a list, for example {{#each fee_items}}.".into()));
                    }
                    stack.push((Frame::Each { list, pos }, Vec::new()));
                } else if let Some(rest) = tag.strip_prefix("#if") {
                    let path = rest.trim().to_string();
                    if path.is_empty() {
                        return Err((pos, "{{#if}} needs a field, for example {{#if company.gstin}}.".into()));
                    }
                    stack.push((Frame::If { path, pos, yes: None }, Vec::new()));
                } else if tag == "else" {
                    match stack.last_mut() {
                        Some((Frame::If { yes, .. }, body)) if yes.is_none() => {
                            *yes = Some(std::mem::take(body));
                        }
                        _ => return Err((pos, "{{else}} must be inside an {{#if}} and used once.".into())),
                    }
                } else if tag == "/each" || tag == "/if" {
                    let (frame, body) = stack.pop().unwrap();
                    let node = match (frame, tag.as_str()) {
                        (Frame::Each { list, pos }, "/each") => Node::Each { list, body, pos },
                        (Frame::If { path, pos, yes }, "/if") => match yes {
                            Some(y) => Node::If { path, yes: y, no: body, pos },
                            None => Node::If { path, yes: body, no: Vec::new(), pos },
                        },
                        (Frame::Root, _) => return Err((pos, format!("{{{{{tag}}}}} has nothing to close."))),
                        (Frame::Each { pos: p, .. }, _) => return Err((p, "{{#each}} is closed with {{/each}}, not {{/if}}.".into())),
                        (Frame::If { pos: p, .. }, _) => return Err((p, "{{#if}} is closed with {{/if}}, not {{/each}}.".into())),
                    };
                    stack.last_mut().unwrap().1.push(node);
                } else if tag.starts_with('#') || tag.starts_with('/') || tag.starts_with('>') || tag.starts_with('!') || tag.starts_with('&') || tag.starts_with('^') {
                    return Err((pos, format!("“{{{{{tag}}}}}” is not supported. Only each, if, else and plain fields are allowed.")));
                } else {
                    let node = parse_expr(&tag, pos)?;
                    stack.last_mut().unwrap().1.push(node);
                }
            }
        }
    }
    if stack.len() > 1 {
        let pos = match &stack.last().unwrap().0 {
            Frame::Each { pos, .. } | Frame::If { pos, .. } => *pos,
            Frame::Root => 0,
        };
        return Err((pos, "A {{#each}} or {{#if}} block is never closed.".into()));
    }
    Ok(stack.pop().unwrap().1)
}

fn check_nodes(nodes: &[Node], src: &str, list: Option<&str>, out: &mut Vec<Diagnostic>) {
    let known = |p: &str| -> bool {
        field(p).is_some() || list.and_then(|l| LISTS.iter().find(|(n, _)| *n == l)).is_some_and(|(_, items)| items.iter().any(|(n, _)| *n == p))
    };
    for n in nodes {
        match n {
            Node::Text(_) => {}
            Node::Var { path, pos, .. } => {
                if !known(path) {
                    out.push(diag("error", "html", src, *pos, format!("Unknown field “{path}”. Pick one from the Fields panel.")));
                } else if field(path).is_some_and(|d| d.kind == List) {
                    out.push(diag("error", "html", src, *pos, format!("“{path}” is a list. Repeat it with {{{{#each {path}}}}}.")));
                }
            }
            Node::Each { list: l, body, pos } => {
                if list.is_some() {
                    out.push(diag("error", "html", src, *pos, "Repeated blocks cannot be nested."));
                } else if !LISTS.iter().any(|(n, _)| n == l) {
                    out.push(diag("error", "html", src, *pos, format!("“{l}” is not a list. Available: {}.", LISTS.iter().map(|(n, _)| *n).collect::<Vec<_>>().join(", "))));
                } else {
                    check_nodes(body, src, Some(l), out);
                }
            }
            Node::If { path, yes, no, pos } => {
                if !known(path) {
                    out.push(diag("error", "html", src, *pos, format!("Unknown field “{path}” in {{{{#if}}}}.")));
                }
                check_nodes(yes, src, list, out);
                check_nodes(no, src, list, out);
            }
        }
    }
}

// ---------------------------------------------------------------- HTML validation

const TAGS: &[&str] = &[
    "div", "span", "p", "h1", "h2", "h3", "h4", "h5", "h6", "header", "footer", "main", "section", "article", "address", "blockquote", "table", "thead", "tbody",
    "tfoot", "tr", "th", "td", "caption", "colgroup", "col", "ul", "ol", "li", "dl", "dt", "dd", "strong", "b", "em", "i", "u", "small", "sub", "sup", "br", "hr", "img",
];
const VOID: &[&str] = &["br", "hr", "img", "col"];
const ATTRS: &[&str] = &["class", "id", "colspan", "rowspan", "style", "src", "alt", "width", "height", "align"];

fn is_safe_data_image(v: &str) -> bool {
    let v = v.trim().to_ascii_lowercase();
    (v.starts_with("data:image/png;base64,") || v.starts_with("data:image/jpeg;base64,")) && v.len() < 700_000
}

/// Checks the HTML around the placeholders. Placeholders are blanked first so their braces are never read as markup.
fn check_html(src: &str, out: &mut Vec<Diagnostic>) {
    // blank out {{ ... }} so "<" or quotes inside a placeholder cannot confuse the tag scan (offsets are preserved)
    let mut text = String::with_capacity(src.len());
    let mut i = 0;
    while let Some(rel) = src[i..].find("{{") {
        let s = i + rel;
        text.push_str(&src[i..s]);
        match src[s + 2..].find("}}") {
            Some(e) => {
                text.push_str(&" ".repeat(src[s..s + 2 + e + 2].len()));
                i = s + 2 + e + 2;
            }
            None => {
                text.push_str(&src[s..]);
                i = src.len();
            }
        }
    }
    text.push_str(&src[i..]);
    let bytes = text.as_bytes();
    let mut open: Vec<(String, usize)> = Vec::new();
    let mut p = 0;
    while p < bytes.len() {
        if bytes[p] != b'<' {
            p += 1;
            continue;
        }
        let rest = &text[p..];
        if rest.starts_with("<!--") {
            match rest.find("-->") {
                Some(e) => {
                    p += e + 3;
                    continue;
                }
                None => {
                    out.push(diag("error", "html", src, p, "A comment is never closed."));
                    return;
                }
            }
        }
        let Some(end) = tag_end(rest) else {
            out.push(diag("error", "html", src, p, "A tag is not closed with >."));
            return;
        };
        let inner = &rest[1..end];
        let closing = inner.starts_with('/');
        let body = inner.trim_start_matches('/').trim_end_matches('/');
        let name: String = body.chars().take_while(|c| c.is_ascii_alphanumeric()).collect::<String>().to_ascii_lowercase();
        if name.is_empty() || inner.starts_with('!') || inner.starts_with('?') {
            out.push(diag("error", "html", src, p, "Doctype, processing instructions and stray < are not allowed. Write a fragment, not a full page."));
            p += end + 1;
            continue;
        }
        if !TAGS.contains(&name.as_str()) {
            let why = match name.as_str() {
                "script" | "iframe" | "object" | "embed" | "link" | "meta" | "base" | "form" | "input" | "button" | "style" | "svg" | "video" | "audio" | "a" => {
                    "is not allowed in templates (no scripts, links, embeds or forms; put styles in the CSS tab)"
                }
                _ => "is not an allowed tag",
            };
            out.push(diag("error", "html", src, p, format!("<{name}> {why}.")));
        } else if closing {
            match open.iter().rposition(|(n, _)| *n == name) {
                Some(idx) => {
                    open.truncate(idx);
                }
                None => out.push(diag("warning", "html", src, p, format!("</{name}> has no matching opening tag."))),
            }
        } else {
            check_attrs(&body[name.len()..], &name, src, p, out);
            if !VOID.contains(&name.as_str()) && !inner.ends_with('/') {
                open.push((name, p));
            }
        }
        p += end + 1;
    }
    for (n, pos) in open {
        out.push(diag("warning", "html", src, pos, format!("<{n}> is never closed.")));
    }
}

/// Index of the `>` that ends the tag at the start of `s`, skipping quoted attribute values.
fn tag_end(s: &str) -> Option<usize> {
    let mut q: Option<char> = None;
    for (i, c) in s.char_indices() {
        match (q, c) {
            (Some(open), c) if c == open => q = None,
            (Some(_), _) => {}
            (None, '"') | (None, '\'') => q = Some(c),
            (None, '>') => return Some(i),
            _ => {}
        }
    }
    None
}

fn check_attrs(attrs: &str, tag: &str, src: &str, pos: usize, out: &mut Vec<Diagnostic>) {
    let mut rest = attrs.trim_start();
    while !rest.is_empty() {
        let name: String = rest.chars().take_while(|c| !c.is_whitespace() && *c != '=' && *c != '/').collect();
        if name.is_empty() {
            break;
        }
        rest = rest[name.len()..].trim_start();
        let mut value = String::new();
        if let Some(r) = rest.strip_prefix('=') {
            let r = r.trim_start();
            if let Some(q) = r.chars().next().filter(|c| *c == '"' || *c == '\'') {
                let inner = &r[1..];
                let e = inner.find(q).unwrap_or(inner.len());
                value = inner[..e].to_string();
                rest = inner[(e + 1).min(inner.len())..].trim_start();
            } else {
                let e = r.find(char::is_whitespace).unwrap_or(r.len());
                value = r[..e].to_string();
                rest = r[e..].trim_start();
            }
        }
        let lname = name.to_ascii_lowercase();
        if lname.starts_with("on") {
            out.push(diag("error", "html", src, pos, format!("Event handler “{name}” is not allowed; templates cannot run scripts.")));
        } else if !ATTRS.contains(&lname.as_str()) {
            out.push(diag("error", "html", src, pos, format!("Attribute “{name}” is not allowed on <{tag}>.")));
        } else if lname == "src" && !(tag == "img" && is_safe_data_image(&value)) {
            out.push(diag("error", "html", src, pos, "Images must be embedded PNG or JPEG data (data:image/png;base64,…). Remote and file addresses are blocked."));
        } else if lname == "style" {
            let mut sub = Vec::new();
            check_css(&format!("x {{ {value} }}"), &mut sub);
            for mut d in sub {
                d.source = "html";
                d.line = at(src, pos).0;
                d.col = at(src, pos).1;
                out.push(d);
            }
        }
        let lv = value.trim().to_ascii_lowercase();
        if lv.contains("javascript:") || lv.starts_with("http:") || lv.starts_with("https:") || lv.starts_with("file:") || lv.starts_with("//") {
            out.push(diag("error", "html", src, pos, "Remote, file and script addresses are blocked."));
        }
    }
}

// ---------------------------------------------------------------- CSS validation and page setup

/// Properties the PDF renderer is known not to honour. They are reported as warnings so nobody expects them in print.
const UNSUPPORTED_PROPS: &[&str] = &[
    "grid", "grid-template", "grid-template-columns", "grid-template-rows", "grid-gap", "gap", "transform", "filter", "box-shadow", "text-shadow", "animation", "transition",
    "position", "float", "clip-path", "backdrop-filter", "mix-blend-mode", "background-image", "columns", "column-count",
];
const APPROVED_FAMILIES: &[&str] = &["ibm plex sans", "ibm plex sans medium", "ibm plex sans semibold", "ibm plex serif", "ibm plex serif semibold", "ibm plex mono", "ibm plex sans devanagari", "sans-serif", "serif", "monospace", "inherit"];

fn strip_comments(css: &str) -> String {
    let mut out = String::with_capacity(css.len());
    let mut rest = css;
    while let Some(s) = rest.find("/*") {
        out.push_str(&rest[..s]);
        match rest[s + 2..].find("*/") {
            Some(e) => {
                // keep newlines so line numbers stay right
                out.push_str(&rest[s..s + 2 + e + 2].chars().map(|c| if c == '\n' { '\n' } else { ' ' }).collect::<String>());
                rest = &rest[s + 2 + e + 2..];
            }
            None => {
                rest = "";
            }
        }
    }
    out.push_str(rest);
    out
}

pub fn check_css(css: &str, out: &mut Vec<Diagnostic>) {
    let text = strip_comments(css);
    let lower = text.to_ascii_lowercase();
    let find = |needle: &str| lower.find(needle);
    if let Some(p) = find("@import") {
        out.push(diag("error", "css", css, p, "@import is blocked: templates cannot load other files."));
    }
    if let Some(p) = find("@font-face") {
        out.push(diag("error", "css", css, p, "@font-face is blocked. ADMS bundles IBM Plex; choose a bundled family instead."));
    }
    for bad in ["expression(", "behavior:", "-moz-binding", "javascript:"] {
        if let Some(p) = find(bad) {
            out.push(diag("error", "css", css, p, format!("“{bad}” is not allowed.")));
        }
    }
    let mut i = 0;
    while let Some(rel) = lower[i..].find("url(") {
        let s = i + rel;
        let inner = lower[s + 4..].split(')').next().unwrap_or("").trim().trim_matches(|c| c == '"' || c == '\'').to_string();
        if !is_safe_data_image(&inner) {
            out.push(diag("error", "css", css, s, "url() may only embed PNG or JPEG data. Remote and file addresses are blocked."));
        }
        i = s + 4;
    }
    if lower.matches('{').count() != lower.matches('}').count() {
        out.push(diag("error", "css", css, 0, "The braces { } do not balance."));
    }
    if let Some(p) = find("@media") {
        out.push(diag("warning", "css", css, p, "@media rules are not applied by the PDF renderer."));
    }
    // declarations: property: value;
    let mut offset = 0;
    for chunk in text.split(['{', '}', ';']) {
        let chunk_start = offset;
        offset += chunk.len() + 1;
        let Some((prop, value)) = chunk.split_once(':') else { continue };
        let prop_t = prop.trim().to_ascii_lowercase();
        if prop_t.is_empty() || prop_t.contains(char::is_whitespace) || prop_t.starts_with('@') || prop_t.contains('.') || prop_t.contains('#') {
            continue;
        }
        let pos = chunk_start + prop.len() - prop.trim_start().len();
        if UNSUPPORTED_PROPS.contains(&prop_t.as_str()) && !(prop_t == "position" && value.trim() == "static") {
            out.push(diag("warning", "css", css, pos, format!("“{prop_t}” is not supported by the PDF renderer and will be ignored.")));
        }
        if prop_t == "display" && value.trim().to_ascii_lowercase().starts_with("grid") {
            out.push(diag("warning", "css", css, pos, "display: grid is not supported by the PDF renderer; use tables or block layout."));
        }
        if prop_t == "font-family" {
            for fam in value.split(',') {
                let fam = fam.trim().trim_matches(|c| c == '"' || c == '\'').to_ascii_lowercase();
                if !fam.is_empty() && !APPROVED_FAMILIES.contains(&fam.as_str()) {
                    out.push(diag("warning", "css", css, pos, format!("“{fam}” is not a bundled font. Use IBM Plex Sans, Serif or Mono; other fonts fall back to IBM Plex Sans.")));
                }
            }
        }
        if prop_t == "font-size" {
            let v = value.trim();
            if let Some(n) = v.strip_suffix("pt").and_then(|n| n.trim().parse::<f32>().ok()) {
                if n < 8.0 {
                    out.push(diag("warning", "css", css, pos, format!("{n}pt is below the 8 pt minimum for printed text.")));
                }
            }
        }
    }
}

/// Paper and margins from an `@page { size: A4 landscape; margin: 18mm 16mm; }` rule. The renderer does not read @page itself,
/// so the rule is read here, applied to the page setup, and removed from the CSS handed to the renderer.
#[derive(Clone, Debug, PartialEq, Serialize, serde::Deserialize)]
pub struct PageSetup {
    pub width_mm: f32,
    pub height_mm: f32,
    pub margin_top: f32,
    pub margin_right: f32,
    pub margin_bottom: f32,
    pub margin_left: f32,
}

impl Default for PageSetup {
    fn default() -> Self {
        PageSetup { width_mm: 210.0, height_mm: 297.0, margin_top: 18.0, margin_right: 16.0, margin_bottom: 20.0, margin_left: 16.0 }
    }
}

impl PageSetup {
    pub fn landscape(&self) -> bool {
        self.width_mm > self.height_mm
    }
}

fn mm(v: &str) -> Option<f32> {
    let v = v.trim();
    if let Some(n) = v.strip_suffix("mm") {
        n.trim().parse().ok()
    } else if let Some(n) = v.strip_suffix("cm") {
        n.trim().parse::<f32>().ok().map(|x| x * 10.0)
    } else if let Some(n) = v.strip_suffix("pt") {
        n.trim().parse::<f32>().ok().map(|x| x * 25.4 / 72.0)
    } else if let Some(n) = v.strip_suffix("in") {
        n.trim().parse::<f32>().ok().map(|x| x * 25.4)
    } else {
        None
    }
}

/// Returns the page setup and the CSS with the @page rule removed. Problems are reported as warnings.
pub fn split_page_rule(css: &str, out: &mut Vec<Diagnostic>) -> (PageSetup, String) {
    let mut setup = PageSetup::default();
    let text = strip_comments(css);
    let Some(start) = text.to_ascii_lowercase().find("@page") else { return (setup, css.to_string()) };
    let Some(open) = text[start..].find('{').map(|o| start + o) else { return (setup, css.to_string()) };
    let Some(close) = text[open..].find('}').map(|o| open + o) else { return (setup, css.to_string()) };
    for decl in text[open + 1..close].split(';') {
        let Some((p, v)) = decl.split_once(':') else { continue };
        let (p, v) = (p.trim().to_ascii_lowercase(), v.trim().to_ascii_lowercase());
        match p.as_str() {
            "size" => {
                let mut landscape = false;
                let (mut w, mut h) = (setup.width_mm, setup.height_mm);
                for word in v.split_whitespace() {
                    match word {
                        "a4" => (w, h) = (210.0, 297.0),
                        "a3" => (w, h) = (297.0, 420.0),
                        "a5" => (w, h) = (148.0, 210.0),
                        "landscape" => landscape = true,
                        "portrait" => landscape = false,
                        other => out.push(diag("warning", "css", css, start, format!("Paper size “{other}” is not supported. Use A4, A3 or A5 with portrait or landscape."))),
                    }
                }
                if landscape {
                    std::mem::swap(&mut w, &mut h);
                }
                setup.width_mm = w;
                setup.height_mm = h;
            }
            "margin" => {
                let vals: Vec<f32> = v.split_whitespace().filter_map(mm).collect();
                let (t, r, b, l) = match vals.as_slice() {
                    [a] => (*a, *a, *a, *a),
                    [a, b] => (*a, *b, *a, *b),
                    [a, b, c] => (*a, *b, *c, *b),
                    [a, b, c, d] => (*a, *b, *c, *d),
                    _ => {
                        out.push(diag("warning", "css", css, start, "The @page margin could not be read; use millimetres, for example margin: 18mm 16mm;"));
                        continue;
                    }
                };
                setup.margin_top = t;
                setup.margin_right = r;
                setup.margin_bottom = b;
                setup.margin_left = l;
            }
            _ => {}
        }
    }
    for m in [setup.margin_top, setup.margin_right, setup.margin_bottom, setup.margin_left] {
        if !(0.0..=60.0).contains(&m) {
            out.push(diag("warning", "css", css, start, "Margins outside 0 to 60 mm are not supported; the default margins are used."));
            setup = PageSetup { width_mm: setup.width_mm, height_mm: setup.height_mm, ..PageSetup::default() };
            break;
        }
    }
    let remaining = format!("{}{}", &text[..start], &text[close + 1..]);
    (setup, remaining)
}

// ---------------------------------------------------------------- validation entry point

/// Everything that can be found wrong with a template without rendering it.
pub fn validate(html: &str, css: &str) -> Vec<Diagnostic> {
    let mut out = Vec::new();
    if html.len() > 400_000 || css.len() > 200_000 {
        out.push(diag("error", "html", html, 0, "The template is too large (limit 400 KB of HTML and 200 KB of CSS)."));
        return out;
    }
    match parse(html) {
        Ok(nodes) => check_nodes(&nodes, html, None, &mut out),
        Err((pos, msg)) => out.push(diag("error", "html", html, pos, msg)),
    }
    check_html(html, &mut out);
    check_css(css, &mut out);
    let _ = split_page_rule(css, &mut out);
    if html.trim().is_empty() {
        out.push(diag("error", "html", html, 0, "The template is empty."));
    }
    out.sort_by_key(|d| (d.source != "html", d.line, d.col));
    out.dedup();
    out
}

// ---------------------------------------------------------------- rendering

fn lookup<'a>(root: &'a Value, item: Option<&'a Value>, path: &str) -> Option<&'a Value> {
    if !path.contains('.') {
        if let Some(v) = item.and_then(|i| i.get(path)) {
            return Some(v);
        }
    }
    let mut cur = root;
    for seg in path.split('.') {
        cur = cur.get(seg)?;
    }
    Some(cur)
}

fn text_of(v: Option<&Value>) -> String {
    match v {
        Some(Value::String(s)) => s.clone(),
        Some(Value::Number(n)) => n.to_string(),
        Some(Value::Bool(b)) => b.to_string(),
        _ => String::new(),
    }
}

fn truthy(v: Option<&Value>) -> bool {
    match v {
        Some(Value::String(s)) => !s.trim().is_empty(),
        Some(Value::Bool(b)) => *b,
        Some(Value::Number(n)) => n.as_f64().is_some_and(|x| x != 0.0),
        Some(Value::Array(a)) => !a.is_empty(),
        Some(Value::Object(o)) => !o.is_empty(),
        _ => false,
    }
}

fn long_date(iso: &str) -> String {
    const M: [&str; 12] = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    let p: Vec<&str> = iso.split('-').collect();
    match p.as_slice() {
        [y, m, d] if y.len() == 4 => match (m.parse::<usize>(), d.parse::<u32>()) {
            (Ok(mi), Ok(di)) if (1..=12).contains(&mi) => format!("{di:02} {} {y}", M[mi - 1]),
            _ => iso.to_string(),
        },
        _ => iso.to_string(),
    }
}

fn paragraphs(text: &str) -> String {
    text.replace("\r\n", "\n")
        .split("\n\n")
        .map(str::trim)
        .filter(|p| !p.is_empty())
        .map(|p| format!("<p>{}</p>", esc(p).replace('\n', "<br/>")))
        .collect()
}

fn render_nodes(nodes: &[Node], root: &Value, item: Option<&Value>, out: &mut String) {
    for n in nodes {
        match n {
            Node::Text(t) => out.push_str(t),
            Node::Var { path, helpers, .. } => {
                let is_block = field(path).is_some_and(|d| d.kind == Block);
                let mut s = text_of(lookup(root, item, path));
                let mut escaped_already = false;
                for (h, arg) in helpers {
                    match h.as_str() {
                        "upper" => s = s.to_uppercase(),
                        "lower" => s = s.to_lowercase(),
                        "date" => s = long_date(&s),
                        "default" => {
                            if s.trim().is_empty() {
                                s = arg.clone().unwrap_or_default();
                            }
                        }
                        "multiline" => {
                            s = esc(&s).replace('\n', "<br/>");
                            escaped_already = true;
                        }
                        _ => {}
                    }
                }
                if escaped_already {
                    out.push_str(&s);
                } else if is_block {
                    out.push_str(&paragraphs(&s));
                } else {
                    out.push_str(&esc(&s));
                }
            }
            Node::Each { list, body, .. } => {
                if let Some(Value::Array(items)) = lookup(root, item, list) {
                    for it in items {
                        render_nodes(body, root, Some(it), out);
                    }
                }
            }
            Node::If { path, yes, no, .. } => {
                if truthy(lookup(root, item, path)) {
                    render_nodes(yes, root, item, out);
                } else {
                    render_nodes(no, root, item, out);
                }
            }
        }
    }
}

/// Resolves the placeholders. The result is an HTML fragment in which every inserted value is escaped.
pub fn render_fragment(html: &str, data: &Value) -> Result<String, String> {
    let nodes = parse(html).map_err(|(pos, msg)| format!("Line {}: {msg}", at(html, pos).0))?;
    let mut out = String::new();
    render_nodes(&nodes, data, None, &mut out);
    Ok(out)
}

/// Required fields that have no value in the data, so the author is told before a document goes out with a gap.
pub fn missing_required(html: &str, data: &Value) -> Vec<String> {
    let Ok(nodes) = parse(html) else { return Vec::new() };
    fn walk(nodes: &[Node], data: &Value, out: &mut Vec<String>) {
        for n in nodes {
            match n {
                Node::Var { path, .. } => {
                    if field(path).is_some_and(|d| d.required) && !truthy(lookup(data, None, path)) && !out.contains(path) {
                        out.push(path.clone());
                    }
                }
                Node::Each { body, .. } => walk(body, data, out),
                Node::If { yes, no, .. } => {
                    walk(yes, data, out);
                    walk(no, data, out);
                }
                Node::Text(_) => {}
            }
        }
    }
    let mut out = Vec::new();
    walk(&nodes, data, &mut out);
    out
}

/// The complete HTML handed to the PDF renderer: the fixed ADMS type system first, then the template's own CSS.
pub fn full_document(fragment: &str, css: &str) -> String {
    format!("<html><head><style>{}\n{}</style></head><body>{}</body></html>", crate::typography::css(), css, fragment)
}

// ---------------------------------------------------------------- sample data

/// Sample profiles for the preview: a short realistic set, a long one that stresses wrapping and pagination, and the firm's own
/// company details with sample project data.
pub fn sample_data(profile: &str, company: Option<&Value>) -> Value {
    let long = profile == "long";
    let mut v = json!({
        "company": { "name": "Studio Meridian Architects", "address": "14 Residency Road, Bengaluru 560025", "email": "studio@meridian.example", "phone": "+91 80 5550 1234",
                     "pan": "ABCDE1234F", "gstin": "29ABCDE1234F1Z5", "coa_registration_number": "CA/2001/12345", "architect_name": "A. Architect" },
        "client": { "name": "Acme Developers Pvt Ltd", "address": "Plot 12, Baner Road, Pune 411045" },
        "project": { "name": "Baner Villa", "code": "P-014", "site_address": "Plot 5, Baner Hills Layout, Pune" },
        "fee": { "total": "₹5,00,000.00" },
        "fee_items": [
            { "no": "1", "description": "On appointment", "share": "30%", "formatted_amount": "₹1,50,000.00" },
            { "no": "2", "description": "On approval of concept design", "share": "30%", "formatted_amount": "₹1,50,000.00" },
            { "no": "3", "description": "On submission of working drawings", "share": "40%", "formatted_amount": "₹2,00,000.00" }
        ],
        "tax": { "scheme": "Regular GST registration" },
        "payment": { "bank_details": "HDFC Bank, Current A/c 0123456789, IFSC HDFC0000123", "upi_id": "studio@bank" },
        "document": { "number": "PREVIEW", "date": "2026-10-05", "title": "", "subject": "Proposal for architectural consultancy services",
                      "reference": "MA/2026/041",
                      "body": "Thank you for considering our practice for this project.\n\nWe set out below our understanding of the brief and our proposal for the services." },
        "addressee": { "name": "Acme Developers Pvt Ltd", "address": "Plot 12, Baner Road, Pune 411045" },
        "signatory": { "name": "A. Architect", "firm": "Studio Meridian Architects" }
    });
    if long {
        let name = "The Honourable Trustees of the Shri Venkateshwara Charitable and Educational Society (Registered), represented by its Managing Trustee";
        let addr = "No. 1204, Third Floor, Gayatri Complex, Opposite Municipal Corporation Office, Dr. Rajkumar Road, Rajajinagar, Bengaluru, Karnataka 560010";
        v["client"] = json!({ "name": name, "address": addr });
        v["addressee"] = json!({ "name": name, "address": addr });
        v["project"]["name"] = json!("Design and Execution Drawings for the Multi-Specialty Hospital, Staff Quarters and Medical College Campus");
        v["project"]["site_address"] = json!(addr);
        let para = "The scope of services includes concept design, schematic design, design development, statutory submissions, working drawings, coordination with structural, MEP and landscape consultants, and handover documentation. ";
        v["document"]["body"] = json!(format!("{p}\n\n{p}\n\n{p}\n\n{p}", p = para.repeat(4)));
        let items: Vec<Value> = (1..=24)
            .map(|i| json!({ "no": i.to_string(), "description": format!("Stage {i}: submission and review of drawings, schedules and statutory documents for the specified block"),
                              "share": "4%", "formatted_amount": format!("₹{},00,000.00", i) }))
            .collect();
        v["fee_items"] = json!(items);
    }
    if profile == "company" {
        if let Some(c) = company {
            v["company"] = c.clone();
            v["signatory"] = json!({ "name": c["architect_name"], "firm": c["name"] });
        }
    }
    v
}

#[cfg(test)]
mod tests {
    use super::*;

    fn errors(html: &str, css: &str) -> Vec<String> {
        validate(html, css).into_iter().filter(|d| d.severity == "error").map(|d| d.message).collect()
    }

    #[test]
    fn placeholders_render_escaped_and_missing_values_vanish() {
        let data = json!({ "company": { "name": "A & B <Architects>" }, "client": { "name": "" } });
        let out = render_fragment("<p>{{ company.name }}|{{ client.name }}|{{ client.name | default \"Not stated\" }}|{{ company.name | upper }}</p>", &data).unwrap();
        assert_eq!(out, "<p>A &amp; B &lt;Architects&gt;||Not stated|A &amp; B &lt;ARCHITECTS&gt;</p>");
        assert!(!out.contains("undefined") && !out.contains("null") && !out.contains("{{"));
    }

    #[test]
    fn conditionals_and_repeated_blocks_work_with_item_fields() {
        let data = sample_data("short", None);
        let t = "{{#if company.gstin}}GST {{ company.gstin }}{{else}}no gst{{/if}}|{{#each fee_items}}[{{ no }}:{{ description }}:{{ formatted_amount }}]{{/each}}";
        let out = render_fragment(t, &data).unwrap();
        assert!(out.starts_with("GST 29ABCDE1234F1Z5|[1:On appointment:₹1,50,000.00]"), "{out}");
        assert_eq!(out.matches('[').count(), 3);
        let mut none = sample_data("short", None);
        none["company"]["gstin"] = json!("");
        assert!(render_fragment(t, &none).unwrap().starts_with("no gst|"));
    }

    #[test]
    fn free_text_is_escaped_into_paragraphs_and_cannot_inject_markup() {
        let mut d = sample_data("short", None);
        d["document"]["body"] = json!("First <script>alert(1)</script> line\nsecond line\n\n<img src=x onerror=alert(2)>");
        let out = render_fragment("{{ document.body }}", &d).unwrap();
        assert!(out.starts_with("<p>First &lt;script&gt;") && out.contains("<br/>second line") && out.contains("</p><p>&lt;img"), "{out}");
        assert!(!out.contains("<script") && !out.contains("<img"));
        // hostile values in ordinary fields are escaped too
        d["client"]["name"] = json!("\"><svg onload=alert(1)>");
        let out = render_fragment("<p>{{ client.name }}</p>", &d).unwrap();
        assert_eq!(out, "<p>&quot;&gt;&lt;svg onload=alert(1)&gt;</p>");
    }

    #[test]
    fn unknown_fields_helpers_and_malformed_syntax_are_rejected() {
        assert!(errors("<p>{{ company.secret }}</p>", "").iter().any(|m| m.contains("Unknown field")));
        assert!(errors("<p>{{ company.name | exec }}</p>", "").iter().any(|m| m.contains("Unknown helper")));
        assert!(errors("<p>{{ company.name </p>", "").iter().any(|m| m.contains("not closed")));
        assert!(errors("{{#each fee_items}}<p>x</p>", "").iter().any(|m| m.contains("never closed")));
        assert!(errors("{{/if}}", "").iter().any(|m| m.contains("nothing to close")));
        assert!(errors("{{#each company.name}}x{{/each}}", "").iter().any(|m| m.contains("not a list")));
        assert!(errors("{{{ company.name }}}", "").iter().any(|m| m.contains("raw HTML")));
        assert!(errors("{{> partial}}", "").iter().any(|m| m.contains("not supported")));
        assert!(errors("<p>{{ fee_items }}</p>", "").iter().any(|m| m.contains("is a list")));
        assert!(errors("{{#each fee_items}}{{ nope }}{{/each}}", "").iter().any(|m| m.contains("Unknown field")));
        assert!(errors("<p>{{ company.name | default Not }}</p>", "").iter().any(|m| m.contains("double quotes")));
        // no arithmetic or calls are possible: these are simply invalid field names
        assert!(!errors("{{ 1 + 1 }}", "").is_empty() && !errors("{{ company.name() }}", "").is_empty());
    }

    #[test]
    fn hostile_html_is_rejected() {
        for (html, why) in [
            ("<script>alert(1)</script>", "script"),
            ("<p onclick=\"x()\">a</p>", "Event handler"),
            ("<img src=\"https://evil.example/x.png\">", "Remote"),
            ("<img src=\"file:///C:/Users/secret.png\">", "Remote"),
            ("<img src=\"//evil.example/x.png\">", "Remote"),
            ("<iframe src=\"http://localhost:1420\"></iframe>", "iframe"),
            ("<a href=\"javascript:alert(1)\">x</a>", "not allowed"),
            ("<link rel=\"stylesheet\" href=\"http://x\">", "link"),
            ("<p style=\"background:url(http://evil.example/a.png)\">x</p>", "url()"),
            ("<object data=\"x\"></object>", "object"),
            ("<svg onload=\"x()\"></svg>", "svg"),
            ("<form action=\"x\"></form>", "form"),
            ("<!DOCTYPE html>", "Doctype"),
            ("<p data-x=\"1\">a</p>", "not allowed on"),
        ] {
            assert!(errors(html, "").iter().any(|m| m.contains(why)), "{html} should be rejected for {why}; got {:?}", errors(html, ""));
        }
        // an embedded image is allowed
        assert!(errors("<img src=\"data:image/png;base64,iVBORw0KGgo=\" alt=\"logo\">", "").is_empty());
    }

    #[test]
    fn hostile_css_is_rejected_and_unsupported_css_is_flagged() {
        assert!(errors("<p>x</p>", "@import url('http://evil.example/a.css');").iter().any(|m| m.contains("@import")));
        assert!(errors("<p>x</p>", "p { background: url(https://evil.example/a.png); }").iter().any(|m| m.contains("url()")));
        assert!(errors("<p>x</p>", "p { background: url(file:///C:/x.png); }").iter().any(|m| m.contains("url()")));
        assert!(errors("<p>x</p>", "@font-face { font-family: X; src: url(http://x/f.woff); }").iter().any(|m| m.contains("@font-face")));
        assert!(errors("<p>x</p>", "p { width: expression(alert(1)); }").iter().any(|m| m.contains("expression")));
        assert!(errors("<p>x</p>", "p { color: red;").iter().any(|m| m.contains("balance")));
        let d = validate("<p>x</p>", "p { box-shadow: 0 0 4px #000; display: grid; font-family: Comic Sans MS; font-size: 6pt; } @media print { p { color: red } }");
        let w: Vec<&str> = d.iter().filter(|x| x.severity == "warning").map(|x| x.message.as_str()).collect();
        assert!(w.iter().any(|m| m.contains("box-shadow")) && w.iter().any(|m| m.contains("comic sans")) && w.iter().any(|m| m.contains("display: grid")) && w.iter().any(|m| m.contains("8 pt")) && w.iter().any(|m| m.contains("@media")), "{w:?}");
        assert!(!has_errors(&d));
    }

    #[test]
    fn diagnostics_carry_line_and_column() {
        let d = validate("<p>ok</p>\n<p>{{ nope.field }}</p>", "");
        let e = d.iter().find(|x| x.message.contains("Unknown field")).unwrap();
        assert_eq!((e.source, e.line, e.col), ("html", 2, 4));
        let c = validate("<p>x</p>", "p { color: red; }\n@import 'x';");
        assert_eq!(c.iter().find(|x| x.message.contains("@import")).unwrap().line, 2);
    }

    #[test]
    fn page_rule_sets_paper_and_margins_and_is_removed_from_the_css() {
        let mut d = Vec::new();
        let (s, rest) = split_page_rule("@page { size: A4 landscape; margin: 12mm 10mm; }\np { color: red; }", &mut d);
        assert!(s.landscape() && (s.width_mm, s.height_mm) == (297.0, 210.0));
        assert_eq!((s.margin_top, s.margin_right, s.margin_bottom, s.margin_left), (12.0, 10.0, 12.0, 10.0));
        assert!(!rest.contains("@page") && rest.contains("p { color: red; }"));
        let (s2, _) = split_page_rule("p { color: red; }", &mut d);
        assert_eq!(s2, PageSetup::default());
        let (s3, _) = split_page_rule("@page { size: A3; margin: 99mm; }", &mut d);
        assert_eq!((s3.width_mm, s3.height_mm), (297.0, 420.0));
        assert_eq!(s3.margin_top, PageSetup::default().margin_top);
        assert!(!d.is_empty());
    }

    #[test]
    fn required_fields_are_reported_when_empty() {
        let mut d = sample_data("short", None);
        d["client"]["name"] = json!("");
        assert_eq!(missing_required("<p>{{ client.name }} {{ company.name }} {{ company.email }}</p>", &d), vec!["client.name"]);
    }

    #[test]
    fn dates_format_and_unicode_survives() {
        let d = sample_data("short", None);
        assert_eq!(render_fragment("{{ document.date | date }}", &d).unwrap(), "05 Oct 2026");
        assert_eq!(render_fragment("{{ fee.total }} {{ project.name | upper }}", &json!({"fee": {"total": "₹5,00,000.00"}, "project": {"name": "वास्तुकार"}})).unwrap(), "₹5,00,000.00 वास्तुकार");
    }

    #[test]
    fn every_schema_field_is_unique_and_has_a_group() {
        let mut seen = std::collections::HashSet::new();
        for d in FIELDS {
            assert!(seen.insert(d.path), "duplicate {}", d.path);
            assert!(["Company", "Client", "Project", "Fee", "Tax", "Payment", "Document", "Signatory"].contains(&d.group), "{}", d.group);
        }
    }
}
