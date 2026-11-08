//! Falcon CSV, contract v1 (`docs/contracts/falcon-csv.md`): a strict reader and validator. UI-free and database-free.
//!
//! A file is accepted whole or refused whole: every problem is collected with its line number and a reason, so the person can
//! fix the export instead of finding one error at a time.

use crate::hash::sha256_hex;
use serde::Serialize;
use std::collections::BTreeMap;

pub const SCHEMA: &str = "osas-falcon-csv/1";
pub const COLUMNS: [&str; 20] = [
    "schema", "row_type", "project_id", "drawing_revision", "package", "item_code", "description", "unit", "quantity", "rate", "amount",
    "measured_to_date", "this_period", "previous", "source", "kind", "tag", "size", "level", "note",
];
/// Stop collecting after this many problems; a file this broken needs fixing at the source.
const MAX_ISSUES: usize = 200;

#[derive(Debug, Serialize, Clone, PartialEq, Eq)]
pub struct Issue {
    /// 1-based line in the file; 0 for a problem with the file as a whole.
    pub line: usize,
    pub message: String,
}

#[derive(Debug, Serialize, Clone, PartialEq, Default)]
pub struct Meta {
    pub project_id: String,
    pub drawing_revision: i64,
    pub project_name: String,
    pub currency: String,
    pub exported: String,
    pub falcon: String,
}

#[derive(Debug, Serialize, Clone, PartialEq)]
pub struct EstimateRow {
    pub line: usize,
    pub package: String,
    pub item_code: String,
    pub description: String,
    pub unit: String,
    pub quantity: f64,
    pub rate: f64,
    pub amount: f64,
    pub source: String,
}

#[derive(Debug, Serialize, Clone, PartialEq)]
pub struct MeasuredRow {
    pub line: usize,
    pub item_code: String,
    pub description: String,
    pub unit: String,
    pub measured_to_date: f64,
    pub this_period: f64,
    pub previous: f64,
    pub source: String,
}

#[derive(Debug, Serialize, Clone, PartialEq)]
pub struct TakeoffRow {
    pub line: usize,
    pub kind: String,
    pub tag: String,
    pub size: String,
    pub unit: String,
    pub quantity: f64,
    pub level: String,
    pub source: String,
    pub item_code: String,
}

#[derive(Debug, Serialize, Clone, PartialEq)]
pub struct Parsed {
    pub sha256: String,
    pub meta: Meta,
    pub estimates: Vec<EstimateRow>,
    pub measured: Vec<MeasuredRow>,
    pub takeoffs: Vec<TakeoffRow>,
    /// The `total` row's check amount (sum of the estimate amounts).
    pub total_amount: f64,
}

/// A decimal in the contract's grammar: optional minus, digits, optional `.digits`. No separators, symbols or exponents.
fn number(text: &str) -> Option<f64> {
    let digits = text.strip_prefix('-').unwrap_or(text);
    let (whole, frac) = match digits.split_once('.') {
        Some((w, f)) => (w, Some(f)),
        None => (digits, None),
    };
    let ok = !whole.is_empty() && whole.bytes().all(|b| b.is_ascii_digit()) && frac.is_none_or(|f| !f.is_empty() && f.bytes().all(|b| b.is_ascii_digit()));
    if ok { text.parse::<f64>().ok().filter(|v| v.is_finite()) } else { None }
}

fn is_uuid(text: &str) -> bool {
    let parts: Vec<&str> = text.split('-').collect();
    parts.len() == 5 && [8, 4, 4, 4, 12].iter().zip(&parts).all(|(n, p)| p.len() == *n && p.bytes().all(|b| b.is_ascii_hexdigit()))
}

/// `falcon` or `tren:<kind 1 to 13>`.
fn valid_source(text: &str) -> bool {
    text == "falcon" || text.strip_prefix("tren:").and_then(|k| k.parse::<u8>().ok()).is_some_and(|k| (1..=13).contains(&k))
}

pub fn round2(v: f64) -> f64 {
    (v * 100.0).round() / 100.0
}

struct Cursor<'a> {
    rec: &'a csv::StringRecord,
    cols: &'a BTreeMap<String, usize>,
    line: usize,
    issues: &'a mut Vec<Issue>,
}

impl Cursor<'_> {
    fn bad(&mut self, message: String) {
        if self.issues.len() < MAX_ISSUES {
            self.issues.push(Issue { line: self.line, message });
        }
    }
    fn text(&self, col: &str) -> String {
        self.cols.get(col).and_then(|i| self.rec.get(*i)).unwrap_or("").trim().to_string()
    }
    fn need(&mut self, col: &str) -> String {
        let v = self.text(col);
        if v.is_empty() {
            self.bad(format!("'{col}' is required on this row."));
        }
        v
    }
    fn num(&mut self, col: &str) -> f64 {
        let v = self.text(col);
        if v.is_empty() {
            self.bad(format!("'{col}' is required on this row."));
            return 0.0;
        }
        number(&v).unwrap_or_else(|| {
            self.bad(format!("'{col}' is not a number: \"{v}\" (use digits and a '.' decimal point, no separators)."));
            0.0
        })
    }
    fn source(&mut self) -> String {
        let v = self.need("source");
        if !v.is_empty() && !valid_source(&v) {
            self.bad(format!("'source' must be 'falcon' or 'tren:<kind 1 to 13>', not \"{v}\"."));
        }
        v
    }
}

fn note_field(note: &str, key: &str) -> String {
    note.split(';').filter_map(|p| p.split_once('=')).find(|(k, _)| k.trim() == key).map(|(_, v)| v.trim().to_string()).unwrap_or_default()
}

/// Reads and checks a whole file. `Err` lists every problem; nothing is returned for a file with any problem.
pub fn parse(bytes: &[u8]) -> Result<Parsed, Vec<Issue>> {
    let sha256 = sha256_hex(bytes);
    let whole = |m: &str| Err(vec![Issue { line: 0, message: m.to_string() }]);
    let body = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF][..]).unwrap_or(bytes);
    let Ok(text) = std::str::from_utf8(body) else { return whole("The file is not UTF-8 text.") };
    if text.trim().is_empty() {
        return whole("The file is empty.");
    }
    let mut reader = csv::ReaderBuilder::new().has_headers(false).flexible(true).from_reader(text.as_bytes());
    let mut issues: Vec<Issue> = Vec::new();
    let mut records: Vec<(usize, csv::StringRecord)> = Vec::new();
    for result in reader.records() {
        match result {
            Ok(rec) => records.push((rec.position().map(|p| p.line() as usize).unwrap_or(0), rec)),
            Err(e) => {
                let line = e.position().map(|p| p.line() as usize).unwrap_or(0);
                return Err(vec![Issue { line, message: format!("Not valid CSV: {e}") }]);
            }
        }
    }
    let Some(((header_line, header), rows)) = records.split_first().map(|(h, r)| ((h.0, &h.1), r)) else { return whole("The file is empty.") };
    let cols: BTreeMap<String, usize> = header.iter().enumerate().map(|(i, c)| (c.trim().to_string(), i)).collect();
    let missing: Vec<&str> = COLUMNS.iter().copied().filter(|c| !cols.contains_key(*c)).collect();
    if !missing.is_empty() {
        return Err(vec![Issue { line: header_line, message: format!("The header is missing column(s): {}. This is not a Falcon export, or it is from a different version.", missing.join(", ")) }]);
    }

    let mut meta: Option<(usize, Meta)> = None;
    let mut total: Option<(usize, f64, f64)> = None;
    let mut estimates: Vec<EstimateRow> = Vec::new();
    let mut measured: Vec<MeasuredRow> = Vec::new();
    let mut takeoffs: Vec<TakeoffRow> = Vec::new();
    let mut seen: BTreeMap<(&'static str, String), usize> = BTreeMap::new();
    let mut data_rows = 0usize;
    // line, project_id and drawing_revision text of every row, for the consistency check against the meta row
    let mut row_ids: Vec<(usize, String, String)> = Vec::new();
    let mut order: Vec<(usize, &'static str)> = Vec::new();

    for (line, rec) in rows {
        let line = *line;
        let mut c = Cursor { rec, cols: &cols, line, issues: &mut issues };
        if rec.len() != header.len() {
            c.bad(format!("This row has {} fields but the header has {}.", rec.len(), header.len()));
            continue;
        }
        let schema = c.text("schema");
        if schema != SCHEMA {
            c.bad(format!("Unsupported schema \"{schema}\"; this version of Kivo reads {SCHEMA}."));
            continue;
        }
        let row_type = c.text("row_type");
        let project = c.text("project_id");
        let revision = c.text("drawing_revision");
        row_ids.push((line, project.clone(), revision.clone()));
        let kind: &'static str = match row_type.as_str() {
            "meta" => "meta",
            "estimate" => "estimate",
            "measured" => "measured",
            "takeoff" => "takeoff",
            "total" => "total",
            other => {
                c.bad(format!("Unknown row_type \"{other}\"."));
                continue;
            }
        };
        order.push((line, kind));
        match kind {
            "meta" => {
                if meta.is_some() {
                    c.bad("A second meta row; there must be exactly one.".into());
                    continue;
                }
                if !is_uuid(&project) {
                    c.bad(format!("'project_id' is not a project UUID: \"{project}\"."));
                }
                let rev = match revision.parse::<i64>() {
                    Ok(r) if r >= 0 && revision.bytes().all(|b| b.is_ascii_digit()) => r,
                    _ => {
                        c.bad(format!("'drawing_revision' is not a whole number: \"{revision}\"."));
                        0
                    }
                };
                let note = c.text("note");
                let currency = note_field(&note, "currency");
                if currency.is_empty() {
                    c.bad("The meta note has no currency=... entry.".into());
                }
                let name = c.text("description");
                meta = Some((line, Meta { project_id: project.to_lowercase(), drawing_revision: rev, project_name: name, currency, exported: note_field(&note, "exported"), falcon: note_field(&note, "falcon") }));
            }
            "estimate" => {
                data_rows += 1;
                let item_code = c.need("item_code");
                let description = c.need("description");
                let unit = c.need("unit");
                let (quantity, rate, amount) = (c.num("quantity"), c.num("rate"), c.num("amount"));
                let source = c.source();
                if (amount - round2(quantity * rate)).abs() > 0.01 + 1e-9 {
                    c.bad(format!("amount {amount:.2} does not equal quantity x rate = {:.2} (item {item_code}).", round2(quantity * rate)));
                }
                if !item_code.is_empty() {
                    if let Some(first) = seen.insert(("estimate", item_code.clone()), line) {
                        c.bad(format!("Duplicate estimate item_code {item_code} (first on line {first})."));
                    }
                }
                let package = c.text("package");
                estimates.push(EstimateRow { line, package, item_code, description, unit, quantity, rate, amount, source });
            }
            "measured" => {
                data_rows += 1;
                let item_code = c.need("item_code");
                let unit = c.need("unit");
                let (to_date, period, previous) = (c.num("measured_to_date"), c.num("this_period"), c.num("previous"));
                let source = c.source();
                if (to_date - (previous + period)).abs() > 0.0005 + 1e-9 {
                    c.bad(format!("measured_to_date {to_date} does not equal previous {previous} + this_period {period} (item {item_code})."));
                }
                if !item_code.is_empty() {
                    if let Some(first) = seen.insert(("measured", item_code.clone()), line) {
                        c.bad(format!("Duplicate measured item_code {item_code} (first on line {first})."));
                    }
                }
                let description = c.text("description");
                measured.push(MeasuredRow { line, item_code, description, unit, measured_to_date: to_date, this_period: period, previous, source });
            }
            "takeoff" => {
                data_rows += 1;
                let (kind_name, tag, unit) = (c.need("kind"), c.need("tag"), c.need("unit"));
                let quantity = c.num("quantity");
                let source = c.source();
                if !tag.is_empty() {
                    if let Some(first) = seen.insert(("takeoff", tag.clone()), line) {
                        c.bad(format!("Duplicate take-off tag {tag} (first on line {first})."));
                    }
                }
                let (size, level, item_code) = (c.text("size"), c.text("level"), c.text("item_code"));
                takeoffs.push(TakeoffRow { line, kind: kind_name, tag, size, unit, quantity, level, source, item_code });
            }
            _ => {
                if total.is_some() {
                    c.bad("A second total row; there must be exactly one.".into());
                    continue;
                }
                let (amount, count) = (c.num("amount"), c.num("quantity"));
                total = Some((line, amount, count));
            }
        }
    }

    let mut file_issue = |line: usize, message: String| {
        if issues.len() < MAX_ISSUES {
            issues.push(Issue { line, message });
        }
    };
    match &meta {
        None => file_issue(0, "The file has no meta row.".into()),
        Some((meta_line, m)) => {
            if order.first().map(|(l, _)| *l) != Some(*meta_line) {
                file_issue(*meta_line, "The meta row must be the first data row.".into());
            }
            for (line, project, revision) in &row_ids {
                if project.to_lowercase() != m.project_id {
                    file_issue(*line, format!("project_id \"{project}\" differs from the meta row's {}.", m.project_id));
                } else if revision.parse::<i64>().ok() != Some(m.drawing_revision) {
                    file_issue(*line, format!("drawing_revision \"{revision}\" differs from the meta row's {}.", m.drawing_revision));
                }
            }
        }
    }
    match total {
        None => file_issue(0, "The file has no total row, so it may be cut short.".into()),
        Some((total_line, amount, count)) => {
            if order.last().map(|(l, _)| *l) != Some(total_line) {
                file_issue(total_line, "The total row must be the last data row.".into());
            }
            let sum = round2(estimates.iter().map(|e| e.amount).sum());
            if (amount - sum).abs() > 0.01 + 1e-9 {
                file_issue(total_line, format!("total amount {amount:.2} does not equal the sum of the estimate amounts {sum:.2}."));
            }
            if (count - data_rows as f64).abs() > 1e-9 {
                file_issue(total_line, format!("total quantity {count} does not equal the number of data rows {data_rows}."));
            }
        }
    }
    if !issues.is_empty() {
        issues.sort_by_key(|i| i.line);
        return Err(issues);
    }
    let total_amount = total.map(|t| t.1).unwrap_or(0.0);
    Ok(Parsed { sha256, meta: meta.map(|m| m.1).unwrap_or_default(), estimates, measured, takeoffs, total_amount })
}
