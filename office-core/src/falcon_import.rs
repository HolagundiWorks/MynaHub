//! Importing a MynaOps CSV (contract v1, `docs/contracts/falcon-csv.md`) into the company file.
//!
//! It builds on MynaHub's existing approvals model (`suite_approvals`, see `approvals`): an **estimate version** is a row of kind
//! `estimate`, a **running bill** a row of kind `running_bill`; both are versioned, start as `draft`, and become `approved_locked`
//! only when someone approves them. The import never approves, and never edits a locked row: a change to an approved estimate
//! becomes the next draft version, a re-measure after an approved bill becomes a new draft bill.
//!
//! Tables the import needs around that (project link, item code mapping, the imported rows, the import log) are on the `aos` schema
//! track, see `migrations/aos_0001_falcon_import.sql`.
//!
//! The row key is (`project_id`, `drawing_revision`, `row_type`, `item_code` or `tag`). Each row also carries a hash of the values
//! MynaOps sent, so a re-import is recognised as the same, a different value as changed. `plan` only reads; `import` runs the same
//! plan and writes it in one transaction, or writes nothing.

use crate::falcon_csv::{self, Issue, Meta, Parsed};
use crate::hash::sha256_hex;
use crate::schema::audit;
use crate::track::{self, Track};
use rusqlite::{params, Connection, OptionalExtension};
use serde::Serialize;
use serde_json::{json, Value};
use std::collections::{BTreeMap, BTreeSet};

pub const TRACK: Track = Track { name: "aos", migrations: &[include_str!("../migrations/aos_0001_falcon_import.sql")] };
/// Tolerance when comparing a measured quantity with an estimate quantity.
const QTY_EPS: f64 = 0.0005;

/// Brings this company file to the track version the import needs. Safe to call every time.
pub fn ensure(conn: &mut Connection) -> Result<(), String> {
    track::migrate(conn, &TRACK)
}

fn sql<T>(r: rusqlite::Result<T>) -> Result<T, String> {
    r.map_err(|e| e.to_string())
}

// ---------------------------------------------------------------------------------------------------------------- mapping

#[derive(Debug, Serialize, Clone, PartialEq)]
pub struct MapEntry {
    pub falcon_code: String,
    pub kivo_item: String,
}

pub fn map_list(conn: &Connection) -> Result<Vec<MapEntry>, String> {
    let mut stmt = sql(conn.prepare("SELECT falcon_code, kivo_item FROM aos_falcon_itemmap ORDER BY falcon_code"))?;
    let rows = sql(stmt.query_map([], |r| Ok(MapEntry { falcon_code: r.get(0)?, kivo_item: r.get(1)? })))?;
    sql(rows.collect())
}

/// Maps a MynaOps item code to a MynaHub price-list item. An empty `kivo_item` removes the mapping.
pub fn map_set(conn: &Connection, actor: i64, falcon_code: &str, kivo_item: &str) -> Result<(), String> {
    let (code, item) = (falcon_code.trim(), kivo_item.trim());
    if code.is_empty() || code.len() > 80 {
        return Err("Enter the MynaOps item code.".into());
    }
    if item.len() > 80 {
        return Err("The MynaHub item code is too long.".into());
    }
    if item.is_empty() {
        sql(conn.execute("DELETE FROM aos_falcon_itemmap WHERE falcon_code = ?1", params![code]))?;
        audit(conn, Some(actor), "falcon_map_removed", code);
    } else {
        sql(conn.execute(
            "INSERT INTO aos_falcon_itemmap (falcon_code, kivo_item) VALUES (?1, ?2)
             ON CONFLICT(falcon_code) DO UPDATE SET kivo_item = excluded.kivo_item, updated_at = datetime('now')",
            params![code, item],
        ))?;
        audit(conn, Some(actor), "falcon_map_set", &format!("{code} -> {item}"));
    }
    Ok(())
}

fn map_load(conn: &Connection) -> Result<BTreeMap<String, String>, String> {
    Ok(map_list(conn)?.into_iter().map(|m| (m.falcon_code, m.kivo_item)).collect())
}

// ------------------------------------------------------------------------------------------------------------------ rows

/// One imported row: what is stored and compared.
#[derive(Debug, Clone)]
struct FileRow {
    row_type: &'static str,
    key: String,
    hash: String,
    payload: Value,
}

fn f(v: f64) -> String {
    format!("{v:.6}")
}

fn hashed(fields: &[String]) -> String {
    sha256_hex(fields.join("\u{1f}").as_bytes())
}

/// Round to what the contract carries, so 42.5 and 42.500 are the same number in the stored payload.
fn n(v: f64) -> f64 {
    (v * 1e6).round() / 1e6
}

fn mapped(map: &BTreeMap<String, String>, code: &str) -> Value {
    map.get(code).map(|k| json!(k)).unwrap_or(Value::Null)
}

/// (estimate and take-off rows, measured rows) of a file, with item codes resolved through the mapping table.
fn file_rows(p: &Parsed, map: &BTreeMap<String, String>) -> (Vec<FileRow>, Vec<FileRow>) {
    let mut est: Vec<FileRow> = Vec::new();
    for e in &p.estimates {
        est.push(FileRow {
            row_type: "estimate",
            key: e.item_code.clone(),
            hash: hashed(&[e.package.clone(), e.item_code.clone(), e.description.clone(), e.unit.clone(), f(e.quantity), f(e.rate), f(e.amount), e.source.clone()]),
            payload: json!({"package": e.package, "item_code": e.item_code, "kivo_item": mapped(map, &e.item_code), "description": e.description,
                "unit": e.unit, "quantity": n(e.quantity), "rate": n(e.rate), "amount": n(e.amount), "source": e.source}),
        });
    }
    for t in &p.takeoffs {
        est.push(FileRow {
            row_type: "takeoff",
            key: t.tag.clone(),
            hash: hashed(&[t.kind.clone(), t.tag.clone(), t.size.clone(), t.unit.clone(), f(t.quantity), t.level.clone(), t.source.clone(), t.item_code.clone()]),
            payload: json!({"kind": t.kind, "tag": t.tag, "size": t.size, "unit": t.unit, "quantity": n(t.quantity), "level": t.level,
                "source": t.source, "item_code": t.item_code, "kivo_item": if t.item_code.is_empty() { Value::Null } else { mapped(map, &t.item_code) }}),
        });
    }
    let meas = p
        .measured
        .iter()
        .map(|m| FileRow {
            row_type: "measured",
            key: m.item_code.clone(),
            hash: hashed(&[m.item_code.clone(), m.description.clone(), m.unit.clone(), f(m.measured_to_date), f(m.this_period), f(m.previous), m.source.clone()]),
            payload: json!({"item_code": m.item_code, "kivo_item": mapped(map, &m.item_code), "description": m.description, "unit": m.unit,
                "measured_to_date": n(m.measured_to_date), "this_period": n(m.this_period), "previous": n(m.previous), "source": m.source}),
        })
        .collect();
    (est, meas)
}

type Stored = BTreeMap<(String, String), (String, Value)>;

fn stored_rows(conn: &Connection, approval_id: i64) -> Result<Stored, String> {
    let mut stmt = sql(conn.prepare("SELECT row_type, key, hash, payload FROM aos_falcon_row WHERE approval_id = ?1"))?;
    let rows = sql(stmt.query_map(params![approval_id], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?, r.get::<_, String>(2)?, r.get::<_, String>(3)?))))?;
    let mut out = Stored::new();
    for row in rows {
        let (t, k, h, p) = sql(row)?;
        out.insert((t, k), (h, serde_json::from_str(&p).unwrap_or(Value::Null)));
    }
    Ok(out)
}

#[derive(Debug, Serialize, Clone, Default, PartialEq)]
pub struct Counts {
    pub added: usize,
    pub changed: usize,
    pub unchanged: usize,
    pub removed: usize,
}

#[derive(Debug, Serialize, Clone)]
pub struct Diff {
    pub row_type: String,
    pub key: String,
    /// `added`, `changed` or `removed`
    pub change: String,
    pub old: Option<Value>,
    pub new: Option<Value>,
}

/// Compare a file's rows with what is stored. `track_removed` is for a snapshot (an estimate version), not for measured rows.
fn diff(stored: &Stored, file: &[FileRow], track_removed: bool) -> (Counts, Vec<Diff>) {
    let mut counts = Counts::default();
    let mut diffs = Vec::new();
    let mut seen: BTreeSet<(String, String)> = BTreeSet::new();
    for row in file {
        let id = (row.row_type.to_string(), row.key.clone());
        seen.insert(id.clone());
        match stored.get(&id) {
            None => {
                counts.added += 1;
                diffs.push(Diff { row_type: id.0, key: id.1, change: "added".into(), old: None, new: Some(row.payload.clone()) });
            }
            Some((hash, old)) if *hash != row.hash => {
                counts.changed += 1;
                diffs.push(Diff { row_type: id.0, key: id.1, change: "changed".into(), old: Some(old.clone()), new: Some(row.payload.clone()) });
            }
            Some(_) => counts.unchanged += 1,
        }
    }
    if track_removed {
        for (id, (_, old)) in stored {
            if !seen.contains(id) {
                counts.removed += 1;
                diffs.push(Diff { row_type: id.0.clone(), key: id.1.clone(), change: "removed".into(), old: Some(old.clone()), new: None });
            }
        }
    }
    (counts, diffs)
}

// -------------------------------------------------------------------------------------------------------------- the plan

#[derive(Debug, Clone)]
struct Ver {
    id: i64,
    version: i64,
    status: String,
    /// Set when the version came from a MynaOps import of this MynaOps project.
    drawing_revision: Option<i64>,
}

fn estimate_versions(conn: &Connection, project_id: i64, falcon: &str) -> Result<Vec<Ver>, String> {
    let mut stmt = sql(conn.prepare(
        "SELECT a.id, a.version, a.status, v.drawing_revision FROM suite_approvals a
         LEFT JOIN aos_falcon_version v ON v.approval_id = a.id AND v.falcon_project = ?2
         WHERE a.project_id = ?1 AND a.kind = 'estimate' ORDER BY a.version DESC",
    ))?;
    let rows = sql(stmt.query_map(params![project_id, falcon], |r| Ok(Ver { id: r.get(0)?, version: r.get(1)?, status: r.get(2)?, drawing_revision: r.get(3)? })))?;
    sql(rows.collect())
}

#[derive(Debug, Clone)]
enum EstAct {
    /// The file has no estimate or take-off rows.
    None,
    Unchanged,
    Update(i64),
    New { supersede_draft: Option<i64>, supersedes: Option<i64> },
}

#[derive(Debug, Clone)]
enum BillAct {
    None,
    Unchanged,
    Update(i64),
    /// A new bill; `supersede_draft` is an unapproved MynaOps bill of an older drawing revision that it replaces.
    New { supersede_draft: Option<i64> },
}

struct Baseline {
    quantities: BTreeMap<String, f64>,
    label: String,
    approved: bool,
}

/// The estimate quantities a bill is measured against: the approved estimate; failing that the estimate in this file, else the
/// newest version, each said so in the label.
fn baseline(conn: &Connection, project_id: Option<i64>, file_est: &BTreeMap<String, f64>) -> Result<Baseline, String> {
    let mut quantities = BTreeMap::new();
    if let Some(pid) = project_id {
        let locked: Option<(i64, i64)> = sql(conn
            .query_row("SELECT id, version FROM suite_approvals WHERE project_id = ?1 AND kind = 'estimate' AND status = 'approved_locked' ORDER BY version DESC LIMIT 1", params![pid], |r| Ok((r.get(0)?, r.get(1)?)))
            .optional())?;
        if let Some((id, version)) = locked {
            for ((t, k), (_, p)) in stored_rows(conn, id)? {
                if t == "estimate" {
                    quantities.insert(k, p["quantity"].as_f64().unwrap_or(0.0));
                }
            }
            return Ok(Baseline { quantities, label: format!("approved estimate v{version}"), approved: true });
        }
    }
    if !file_est.is_empty() {
        return Ok(Baseline { quantities: file_est.clone(), label: "the estimate in this file (no estimate is approved yet)".into(), approved: false });
    }
    if let Some(pid) = project_id {
        let latest: Option<(i64, i64)> = sql(conn
            .query_row("SELECT id, version FROM suite_approvals WHERE project_id = ?1 AND kind = 'estimate' ORDER BY version DESC LIMIT 1", params![pid], |r| Ok((r.get(0)?, r.get(1)?)))
            .optional())?;
        if let Some((id, version)) = latest {
            for ((t, k), (_, p)) in stored_rows(conn, id)? {
                if t == "estimate" {
                    quantities.insert(k, p["quantity"].as_f64().unwrap_or(0.0));
                }
            }
            return Ok(Baseline { quantities, label: format!("estimate v{version} (not approved)"), approved: false });
        }
    }
    Ok(Baseline { quantities, label: "no estimate".into(), approved: false })
}

/// What the last approved bills say each item had measured to date.
fn prior_measured(conn: &Connection, project_id: Option<i64>) -> Result<BTreeMap<String, f64>, String> {
    let mut out = BTreeMap::new();
    let Some(pid) = project_id else { return Ok(out) };
    let mut stmt = sql(conn.prepare(
        "SELECT r.key, r.payload FROM aos_falcon_row r JOIN suite_approvals a ON a.id = r.approval_id
         WHERE a.project_id = ?1 AND a.kind = 'running_bill' AND a.status = 'approved_locked' AND r.row_type = 'measured'
         ORDER BY a.version ASC",
    ))?;
    let rows = sql(stmt.query_map(params![pid], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?))))?;
    for row in rows {
        let (k, p) = sql(row)?;
        out.insert(k, serde_json::from_str::<Value>(&p).ok().and_then(|v| v["measured_to_date"].as_f64()).unwrap_or(0.0));
    }
    Ok(out)
}

/// The JSON body of a running bill, and the warnings it raises. A line that goes beyond the estimate is flagged, not refused.
fn bill_body(meta: &Meta, sha: &str, rows: &BTreeMap<String, Value>, base: &Baseline, prior: &BTreeMap<String, f64>) -> (String, Vec<String>) {
    let mut flags = Vec::new();
    let mut lines = Vec::new();
    for (code, p) in rows {
        let to_date = p["measured_to_date"].as_f64().unwrap_or(0.0);
        let est = base.quantities.get(code).copied();
        let mut line_flags: Vec<String> = Vec::new();
        match est {
            None => line_flags.push(format!("{code} is not in the {}.", base.label)),
            Some(q) if to_date > q + QTY_EPS => line_flags.push(format!("{code}: measured to date {to_date:.3} is more than the quantity in the {} ({q:.3}) by {:.3}.", base.label, to_date - q)),
            _ => {}
        }
        let before = prior.get(code).copied().unwrap_or(0.0);
        let falcon_prev = p["previous"].as_f64().unwrap_or(0.0);
        if (before - falcon_prev).abs() > QTY_EPS {
            line_flags.push(format!("{code}: MynaOps's previous {falcon_prev:.3} differs from the last approved bill in MynaHub ({before:.3})."));
        }
        let mut line = p.clone();
        line["estimate_quantity"] = est.map(|q| json!(q)).unwrap_or(Value::Null);
        line["exceeds_estimate"] = json!(est.is_some_and(|q| to_date > q + QTY_EPS));
        line["kivo_previous"] = json!(before);
        line["flags"] = json!(line_flags);
        flags.extend(line_flags);
        lines.push(line);
    }
    let body = json!({
        "schema": "osas-kivo-running-bill/1",
        "source": "falcon-csv",
        "falcon_project_id": meta.project_id,
        "drawing_revision": meta.drawing_revision,
        "file_sha256": sha,
        "measured_against": base.label,
        "estimate_approved": base.approved,
        "note": "The measured-to-date on each line is locked with this bill. A later re-measure makes a new bill.",
        "lines": lines,
        "flags": flags,
    });
    (body.to_string(), flags)
}

fn estimate_body(meta: &Meta, sha: &str, rows: &[FileRow]) -> String {
    let lines: Vec<&Value> = rows.iter().filter(|r| r.row_type == "estimate").map(|r| &r.payload).collect();
    let takeoff: Vec<&Value> = rows.iter().filter(|r| r.row_type == "takeoff").map(|r| &r.payload).collect();
    let total = falcon_csv::round2(lines.iter().map(|l| l["amount"].as_f64().unwrap_or(0.0)).sum());
    json!({
        "schema": "osas-kivo-estimate/1",
        "source": "falcon-csv",
        "falcon_project_id": meta.project_id,
        "project_name": meta.project_name,
        "drawing_revision": meta.drawing_revision,
        "currency": meta.currency,
        "exported": meta.exported,
        "falcon_version": meta.falcon,
        "file_sha256": sha,
        "total": total,
        "total_paise": (total * 100.0).round() as i64,
        "lines": lines,
        "takeoff": takeoff,
    })
    .to_string()
}

struct Plan {
    project_id: Option<i64>,
    issues: Vec<Issue>,
    est_rows: Vec<FileRow>,
    est_act: EstAct,
    est_counts: Counts,
    est_diffs: Vec<Diff>,
    est_baseline_version: Option<i64>,
    meas_act: BillAct,
    meas_counts: Counts,
    meas_diffs: Vec<Diff>,
    /// What the bill will hold after the import: its measured rows by item code.
    bill_final: BTreeMap<String, Value>,
    bill_changed: Vec<FileRow>,
    bill_body: Option<String>,
    flags: Vec<String>,
    unmapped: Vec<String>,
    current_total: Option<f64>,
    current_version: Option<(i64, String)>,
}

fn issue(message: impl Into<String>) -> Issue {
    Issue { line: 0, message: message.into() }
}

fn plan(conn: &Connection, p: &Parsed, given_project: Option<i64>) -> Result<Plan, String> {
    let map = map_load(conn)?;
    let (est_rows, meas_rows) = file_rows(p, &map);
    let falcon = p.meta.project_id.as_str();
    let rev = p.meta.drawing_revision;

    let mut unmapped: BTreeSet<String> = BTreeSet::new();
    let codes = p.estimates.iter().map(|e| &e.item_code).chain(p.measured.iter().map(|m| &m.item_code)).chain(p.takeoffs.iter().map(|t| &t.item_code));
    for code in codes.filter(|c| !c.is_empty()) {
        if !map.contains_key(code) {
            unmapped.insert(code.clone());
        }
    }

    let mut plan = Plan {
        project_id: None,
        issues: Vec::new(),
        est_rows,
        est_act: EstAct::None,
        est_counts: Counts::default(),
        est_diffs: Vec::new(),
        est_baseline_version: None,
        meas_act: BillAct::None,
        meas_counts: Counts::default(),
        meas_diffs: Vec::new(),
        bill_final: BTreeMap::new(),
        bill_changed: Vec::new(),
        bill_body: None,
        flags: Vec::new(),
        unmapped: unmapped.into_iter().collect(),
        current_total: None,
        current_version: None,
    };

    // Which MynaHub project is this MynaOps project?
    let linked: Option<i64> = sql(conn.query_row("SELECT project_id FROM aos_falcon_link WHERE falcon_project = ?1", params![falcon], |r| r.get(0)).optional())?;
    plan.project_id = match (linked, given_project) {
        (Some(l), Some(g)) if l != g => {
            plan.issues.push(issue(format!("This MynaOps project is already linked to MynaHub project {l}; it cannot be imported into project {g}.")));
            None
        }
        (Some(l), _) => Some(l),
        (None, Some(g)) => {
            let exists: i64 = sql(conn.query_row("SELECT COUNT(*) FROM projects WHERE id = ?1", params![g], |r| r.get(0)))?;
            if exists == 0 {
                plan.issues.push(issue("That project is not in this company file."));
                None
            } else {
                Some(g)
            }
        }
        (None, None) => None,
    };
    if !plan.issues.is_empty() {
        return Ok(plan);
    }
    let pid = plan.project_id;

    // Estimate version
    let versions = match pid {
        Some(pid) => estimate_versions(conn, pid, falcon)?,
        None => Vec::new(),
    };
    if let Some(latest) = versions.first() {
        plan.current_version = Some((latest.version, latest.status.clone()));
        let rows = stored_rows(conn, latest.id)?;
        let total: f64 = rows.iter().filter(|((t, _), _)| t == "estimate").map(|(_, (_, p))| p["amount"].as_f64().unwrap_or(0.0)).sum();
        plan.current_total = Some(falcon_csv::round2(total));
    }
    if !plan.est_rows.is_empty() {
        let latest = versions.first();
        let same_rev = versions.iter().find(|v| v.drawing_revision == Some(rev));
        let base = same_rev.or(latest);
        plan.est_baseline_version = base.map(|b| b.version);
        let stored = match base {
            Some(b) => stored_rows(conn, b.id)?,
            None => Stored::new(),
        };
        let (counts, diffs) = diff(&stored, &plan.est_rows, true);
        plan.est_counts = counts;
        plan.est_diffs = diffs;
        let identical = plan.est_counts.added + plan.est_counts.changed + plan.est_counts.removed == 0;
        plan.est_act = match (same_rev, latest) {
            (Some(s), Some(l)) if s.id != l.id => {
                if identical {
                    EstAct::Unchanged
                } else {
                    plan.issues.push(issue(format!(
                        "MynaHub already has estimate v{} from a newer drawing revision (r{}); this file is r{rev} and differs from the r{rev} estimate. Export the current drawing revision instead.",
                        l.version,
                        l.drawing_revision.unwrap_or(0)
                    )));
                    EstAct::None
                }
            }
            (Some(_), _) if identical => EstAct::Unchanged,
            (Some(s), _) if s.status == "draft" => EstAct::Update(s.id),
            _ => {
                // a new version: either a new drawing revision, or a change to a version that is already approved
                let newer = latest.is_some_and(|l| l.drawing_revision.is_some_and(|r| r > rev));
                if newer {
                    plan.issues.push(issue(format!(
                        "MynaHub already has an estimate from drawing revision r{}; this file is the older r{rev}.",
                        latest.and_then(|l| l.drawing_revision).unwrap_or(0)
                    )));
                    EstAct::None
                } else {
                    let supersede_draft = match latest {
                        Some(l) if l.status == "draft" => {
                            if l.drawing_revision.is_none() {
                                plan.issues.push(issue(format!("Estimate v{} is a draft that did not come from MynaOps. Approve or withdraw it before importing.", l.version)));
                            }
                            Some(l.id)
                        }
                        _ => None,
                    };
                    let supersedes = match pid {
                        Some(pid) => sql(conn
                            .query_row("SELECT id FROM suite_approvals WHERE project_id = ?1 AND kind = 'estimate' AND status = 'approved_locked' ORDER BY version DESC LIMIT 1", params![pid], |r| r.get(0))
                            .optional())?,
                        None => None,
                    };
                    EstAct::New { supersede_draft, supersedes }
                }
            }
        };
    }

    // Running bill
    let file_est: BTreeMap<String, f64> = p.estimates.iter().map(|e| (e.item_code.clone(), e.quantity)).collect();
    if !meas_rows.is_empty() {
        let mut latest: Stored = Stored::new();
        let mut draft_rows: Option<(i64, Stored)> = None;
        let mut replaces: Option<i64> = None;
        if let Some(pid) = pid {
            let mut stmt = sql(conn.prepare(
                "SELECT r.key, r.hash, r.payload FROM aos_falcon_row r JOIN aos_falcon_bill b ON b.approval_id = r.approval_id
                 WHERE b.falcon_project = ?1 AND b.drawing_revision = ?2 AND r.row_type = 'measured' ORDER BY r.approval_id ASC",
            ))?;
            let rows = sql(stmt.query_map(params![falcon, rev], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?, r.get::<_, String>(2)?))))?;
            for row in rows {
                let (k, h, pl) = sql(row)?;
                latest.insert(("measured".into(), k), (h, serde_json::from_str(&pl).unwrap_or(Value::Null)));
            }
            let open: Option<(i64, i64)> = sql(conn
                .query_row("SELECT id, version FROM suite_approvals WHERE project_id = ?1 AND kind = 'running_bill' AND status = 'draft' ORDER BY version DESC LIMIT 1", params![pid], |r| Ok((r.get(0)?, r.get(1)?)))
                .optional())?;
            if let Some((id, version)) = open {
                let theirs: Option<i64> = sql(conn
                    .query_row("SELECT drawing_revision FROM aos_falcon_bill WHERE approval_id = ?1 AND falcon_project = ?2", params![id, falcon], |r| r.get(0))
                    .optional())?;
                match theirs {
                    Some(r) if r == rev => draft_rows = Some((id, stored_rows(conn, id)?)),
                    Some(_) => replaces = Some(id),
                    None => plan.issues.push(issue(format!("There is already a draft running bill (RA {version}) for this project that did not come from this MynaOps project. Approve or withdraw it before importing."))),
                }
            }
        }
        let (counts, diffs) = diff(&latest, &meas_rows, false);
        plan.meas_counts = counts;
        plan.meas_diffs = diffs;
        plan.bill_changed = meas_rows
            .iter()
            .filter(|r| latest.get(&("measured".to_string(), r.key.clone())).is_none_or(|(h, _)| *h != r.hash))
            .cloned()
            .collect();
        if plan.bill_changed.is_empty() {
            plan.meas_act = BillAct::Unchanged;
        } else {
            let mut union: BTreeMap<String, Value> = BTreeMap::new();
            plan.meas_act = match draft_rows {
                Some((id, existing)) => {
                    for ((_, k), (_, pl)) in existing {
                        union.insert(k, pl);
                    }
                    BillAct::Update(id)
                }
                None => BillAct::New { supersede_draft: replaces },
            };
            for r in &plan.bill_changed {
                union.insert(r.key.clone(), r.payload.clone());
            }
            plan.bill_final = union;
        }
    }
    if !plan.bill_final.is_empty() {
        let base = baseline(conn, pid, &file_est)?;
        let prior = prior_measured(conn, pid)?;
        let (body, flags) = bill_body(&p.meta, &p.sha256, &plan.bill_final, &base, &prior);
        plan.bill_body = Some(body);
        plan.flags = flags;
    }
    Ok(plan)
}

// --------------------------------------------------------------------------------------------------------------- preview

#[derive(Debug, Serialize, Default)]
pub struct RowCounts {
    pub estimate: usize,
    pub measured: usize,
    pub takeoff: usize,
}

#[derive(Debug, Serialize, Default)]
pub struct Section {
    /// `none`, `unchanged`, `update_draft` or `new_version` (estimate) / `new_bill` (bill)
    pub action: String,
    pub baseline_version: Option<i64>,
    pub counts: Counts,
    pub differing: Vec<Diff>,
}

#[derive(Debug, Serialize, Default)]
pub struct Preview {
    pub refused: bool,
    pub issues: Vec<Issue>,
    pub file_sha256: String,
    pub falcon_project_id: String,
    pub project_name: String,
    pub drawing_revision: i64,
    pub currency: String,
    pub exported: String,
    pub falcon_version: String,
    pub rows: RowCounts,
    pub file_estimate_total: f64,
    pub current_estimate_total: Option<f64>,
    pub current_estimate_version: Option<i64>,
    pub current_estimate_status: Option<String>,
    pub linked_project_id: Option<i64>,
    pub suggested_project_id: Option<i64>,
    pub needs_project: bool,
    pub unmapped: Vec<String>,
    pub estimate: Section,
    pub bill: Section,
    pub flags: Vec<String>,
    /// This exact file (same SHA-256) was imported before.
    pub already_imported: bool,
    /// Importing would change nothing.
    pub noop: bool,
}

fn est_action_name(a: &EstAct) -> &'static str {
    match a {
        EstAct::None => "none",
        EstAct::Unchanged => "unchanged",
        EstAct::Update(_) => "update_draft",
        EstAct::New { .. } => "new_version",
    }
}

fn bill_action_name(a: &BillAct) -> &'static str {
    match a {
        BillAct::None => "none",
        BillAct::Unchanged => "unchanged",
        BillAct::Update(_) => "update_draft",
        BillAct::New { .. } => "new_bill",
    }
}

fn refused_preview(sha: String, issues: Vec<Issue>) -> Preview {
    Preview { refused: true, issues, file_sha256: sha, ..Default::default() }
}

/// Reads and checks the file and says what an import would do. Writes nothing.
pub fn preview(conn: &Connection, bytes: &[u8], project_id: Option<i64>) -> Result<Preview, String> {
    let parsed = match falcon_csv::parse(bytes) {
        Ok(p) => p,
        Err(issues) => return Ok(refused_preview(sha256_hex(bytes), issues)),
    };
    let plan = plan(conn, &parsed, project_id)?;
    if !plan.issues.is_empty() {
        let mut out = refused_preview(parsed.sha256.clone(), plan.issues);
        out.falcon_project_id = parsed.meta.project_id.clone();
        out.project_name = parsed.meta.project_name.clone();
        out.drawing_revision = parsed.meta.drawing_revision;
        return Ok(out);
    }
    let linked: Option<i64> = sql(conn.query_row("SELECT project_id FROM aos_falcon_link WHERE falcon_project = ?1", params![parsed.meta.project_id], |r| r.get(0)).optional())?;
    let suggested: Option<i64> = if linked.is_some() {
        None
    } else {
        let ids: Vec<i64> = {
            let mut stmt = sql(conn.prepare("SELECT id FROM projects WHERE name = ?1 COLLATE NOCASE AND archived = 0"))?;
            let rows = sql(stmt.query_map(params![parsed.meta.project_name], |r| r.get(0)))?;
            sql(rows.collect())?
        };
        if ids.len() == 1 { Some(ids[0]) } else { None }
    };
    let already: i64 = sql(conn.query_row("SELECT COUNT(*) FROM aos_falcon_import WHERE file_sha256 = ?1 AND refused = 0", params![parsed.sha256], |r| r.get(0)))?;
    let noop = plan.project_id.is_some() && matches!(plan.est_act, EstAct::None | EstAct::Unchanged) && matches!(plan.meas_act, BillAct::None | BillAct::Unchanged);
    Ok(Preview {
        refused: false,
        issues: Vec::new(),
        file_sha256: parsed.sha256.clone(),
        falcon_project_id: parsed.meta.project_id.clone(),
        project_name: parsed.meta.project_name.clone(),
        drawing_revision: parsed.meta.drawing_revision,
        currency: parsed.meta.currency.clone(),
        exported: parsed.meta.exported.clone(),
        falcon_version: parsed.meta.falcon.clone(),
        rows: RowCounts { estimate: parsed.estimates.len(), measured: parsed.measured.len(), takeoff: parsed.takeoffs.len() },
        file_estimate_total: falcon_csv::round2(parsed.estimates.iter().map(|e| e.amount).sum()),
        current_estimate_total: plan.current_total,
        current_estimate_version: plan.current_version.as_ref().map(|v| v.0),
        current_estimate_status: plan.current_version.as_ref().map(|v| v.1.clone()),
        linked_project_id: plan.project_id.filter(|_| linked.is_some()),
        suggested_project_id: suggested,
        needs_project: plan.project_id.is_none(),
        unmapped: plan.unmapped.clone(),
        estimate: Section { action: est_action_name(&plan.est_act).into(), baseline_version: plan.est_baseline_version, counts: plan.est_counts.clone(), differing: plan.est_diffs.clone() },
        bill: Section { action: bill_action_name(&plan.meas_act).into(), baseline_version: None, counts: plan.meas_counts.clone(), differing: plan.meas_diffs.clone() },
        flags: plan.flags.clone(),
        already_imported: already > 0,
        noop,
    })
}

// --------------------------------------------------------------------------------------------------------------- import

#[derive(Debug, Serialize, Default)]
pub struct ImportResult {
    pub file_sha256: String,
    pub project_id: Option<i64>,
    pub drawing_revision: Option<i64>,
    pub added: usize,
    pub changed: usize,
    pub unchanged: usize,
    /// Problems that made the whole file be refused (0 when the file was accepted).
    pub refused: usize,
    pub unmapped: Vec<String>,
    /// Lines of the estimate removed from a draft because the new file no longer has them (also counted in `changed`).
    pub removed: usize,
    /// Nothing was written.
    pub noop: bool,
    pub issues: Vec<Issue>,
    pub flags: Vec<String>,
    pub estimate_action: String,
    pub estimate_approval_id: Option<i64>,
    pub estimate_version: Option<i64>,
    pub bill_action: String,
    pub bill_approval_id: Option<i64>,
    pub bill_version: Option<i64>,
}

fn log_refused(conn: &Connection, actor: i64, sha: &str, name: &str, issues: &[Issue]) {
    let _ = conn.execute(
        "INSERT INTO aos_falcon_import (user_id, file_name, file_sha256, refused) VALUES (?1, ?2, ?3, ?4)",
        params![actor, name, sha, issues.len() as i64],
    );
    audit(conn, Some(actor), "falcon_csv_refused", &format!("{name} sha256={sha} problems={}", issues.len()));
}

fn insert_rows(tx: &Connection, approval_id: i64, rows: &[FileRow]) -> Result<(), String> {
    for r in rows {
        sql(tx.execute(
            "INSERT INTO aos_falcon_row (approval_id, row_type, key, hash, payload) VALUES (?1, ?2, ?3, ?4, ?5)
             ON CONFLICT(approval_id, row_type, key) DO UPDATE SET hash = excluded.hash, payload = excluded.payload",
            params![approval_id, r.row_type, r.key, r.hash, r.payload.to_string()],
        ))?;
    }
    Ok(())
}

fn new_approval(tx: &Connection, project_id: i64, kind: &str, title: &str, body: &str, supersedes: Option<i64>, by: &str) -> Result<(i64, i64), String> {
    let version: i64 = sql(tx.query_row("SELECT COALESCE(MAX(version), 0) + 1 FROM suite_approvals WHERE project_id = ?1 AND kind = ?2", params![project_id, kind], |r| r.get(0)))?;
    sql(tx.execute(
        "INSERT INTO suite_approvals (project_id, kind, title, status, version, body, checksum_sha256, supersedes_id, submitted_by)
         VALUES (?1, ?2, ?3, 'draft', ?4, ?5, ?6, ?7, ?8)",
        params![project_id, kind, title, version, body, sha256_hex(body.as_bytes()), supersedes, by],
    ))?;
    Ok((tx.last_insert_rowid(), version))
}

fn rewrite_draft(tx: &Connection, id: i64, body: &str) -> Result<(), String> {
    let n = sql(tx.execute("UPDATE suite_approvals SET body = ?2, checksum_sha256 = ?3 WHERE id = ?1 AND status = 'draft'", params![id, body, sha256_hex(body.as_bytes())]))?;
    if n != 1 {
        return Err("The draft changed while importing. Try again.".into());
    }
    Ok(())
}

/// Imports a MynaOps CSV. A file with any problem is refused whole (`refused > 0`, nothing written). Otherwise everything is written in
/// one transaction. `project_id` names the MynaHub project when this MynaOps project is not linked to one yet.
pub fn import(conn: &mut Connection, actor: i64, bytes: &[u8], file_name: &str, project_id: Option<i64>) -> Result<ImportResult, String> {
    ensure(conn)?;
    let by: String = sql(conn.query_row("SELECT username FROM users WHERE id = ?1", params![actor], |r| r.get(0))).map_err(|_| "Not signed in.".to_string())?;
    let sha = sha256_hex(bytes);
    let refuse = |conn: &Connection, issues: Vec<Issue>| {
        log_refused(conn, actor, &sha, file_name, &issues);
        Ok(ImportResult { file_sha256: sha.clone(), refused: issues.len(), issues, ..Default::default() })
    };
    let parsed = match falcon_csv::parse(bytes) {
        Ok(p) => p,
        Err(issues) => return refuse(conn, issues),
    };
    let plan = plan(conn, &parsed, project_id)?;
    if !plan.issues.is_empty() {
        return refuse(conn, plan.issues);
    }
    let Some(pid) = plan.project_id else {
        return refuse(conn, vec![issue("Choose the MynaHub project this MynaOps project belongs to.")]);
    };
    let meta = &parsed.meta;
    let falcon = meta.project_id.as_str();
    let rev = meta.drawing_revision;

    let tx = sql(conn.transaction())?;
    sql(tx.execute("INSERT INTO aos_falcon_link (falcon_project, project_id) VALUES (?1, ?2) ON CONFLICT(falcon_project) DO NOTHING", params![falcon, pid]))?;

    let mut result = ImportResult {
        file_sha256: sha.clone(),
        project_id: Some(pid),
        drawing_revision: Some(rev),
        unmapped: plan.unmapped.clone(),
        flags: plan.flags.clone(),
        estimate_action: est_action_name(&plan.est_act).into(),
        bill_action: bill_action_name(&plan.meas_act).into(),
        ..Default::default()
    };
    let name = if meta.project_name.is_empty() { "Project".to_string() } else { meta.project_name.clone() };

    match &plan.est_act {
        EstAct::None => {}
        EstAct::Unchanged => {
            let id: Option<i64> = sql(tx
                .query_row(
                    "SELECT a.id FROM suite_approvals a JOIN aos_falcon_version v ON v.approval_id = a.id
                     WHERE a.project_id = ?1 AND v.falcon_project = ?2 AND v.drawing_revision = ?3 ORDER BY a.version DESC LIMIT 1",
                    params![pid, falcon, rev],
                    |r| r.get(0),
                )
                .optional())?;
            result.estimate_approval_id = id;
        }
        EstAct::Update(id) => {
            let body = estimate_body(meta, &sha, &plan.est_rows);
            sql(tx.execute("DELETE FROM aos_falcon_row WHERE approval_id = ?1", params![id]))?;
            insert_rows(&tx, *id, &plan.est_rows)?;
            rewrite_draft(&tx, *id, &body)?;
            result.estimate_approval_id = Some(*id);
        }
        EstAct::New { supersede_draft, supersedes } => {
            let body = estimate_body(meta, &sha, &plan.est_rows);
            let title = format!("{name} estimate (MynaOps, drawing r{rev})");
            let supersedes = *supersedes;
            if let Some(old) = supersede_draft {
                sql(tx.execute("UPDATE suite_approvals SET status = 'superseded' WHERE id = ?1 AND status = 'draft'", params![old]))?;
            }
            let (id, version) = new_approval(&tx, pid, "estimate", &title, &body, supersedes, &by)?;
            sql(tx.execute("INSERT INTO aos_falcon_version (approval_id, falcon_project, drawing_revision) VALUES (?1, ?2, ?3)", params![id, falcon, rev]))?;
            insert_rows(&tx, id, &plan.est_rows)?;
            result.estimate_approval_id = Some(id);
            result.estimate_version = Some(version);
            audit(&tx, Some(actor), "approval_submitted", &format!("estimate {id} v{version} (MynaOps CSV)"));
        }
    }
    if let Some(id) = result.estimate_approval_id {
        result.estimate_version = sql(tx.query_row("SELECT version FROM suite_approvals WHERE id = ?1", params![id], |r| r.get(0)).optional())?;
    }

    match &plan.meas_act {
        BillAct::None | BillAct::Unchanged => {}
        BillAct::Update(id) => {
            insert_rows(&tx, *id, &plan.bill_changed)?;
            if let Some(body) = &plan.bill_body {
                rewrite_draft(&tx, *id, body)?;
            }
            result.bill_approval_id = Some(*id);
        }
        BillAct::New { supersede_draft } => {
            let body = plan.bill_body.clone().unwrap_or_default();
            if let Some(old) = supersede_draft {
                sql(tx.execute("UPDATE suite_approvals SET status = 'superseded' WHERE id = ?1 AND status = 'draft'", params![old]))?;
            }
            let next: i64 = sql(tx.query_row("SELECT COALESCE(MAX(version), 0) + 1 FROM suite_approvals WHERE project_id = ?1 AND kind = 'running_bill'", params![pid], |r| r.get(0)))?;
            let title = format!("{name} running bill {next} (MynaOps, drawing r{rev})");
            let (id, version) = new_approval(&tx, pid, "running_bill", &title, &body, None, &by)?;
            sql(tx.execute("INSERT INTO aos_falcon_bill (approval_id, falcon_project, drawing_revision) VALUES (?1, ?2, ?3)", params![id, falcon, rev]))?;
            insert_rows(&tx, id, &plan.bill_changed)?;
            result.bill_approval_id = Some(id);
            result.bill_version = Some(version);
            audit(&tx, Some(actor), "approval_submitted", &format!("running_bill {id} v{version} (MynaOps CSV)"));
        }
    }
    if let (Some(id), None) = (result.bill_approval_id, result.bill_version) {
        result.bill_version = sql(tx.query_row("SELECT version FROM suite_approvals WHERE id = ?1", params![id], |r| r.get(0)).optional())?;
    }

    result.added = plan.est_counts.added + plan.meas_counts.added;
    result.changed = plan.est_counts.changed + plan.meas_counts.changed + plan.est_counts.removed;
    result.unchanged = plan.est_counts.unchanged + plan.meas_counts.unchanged;
    result.removed = plan.est_counts.removed;
    result.noop = matches!(plan.est_act, EstAct::None | EstAct::Unchanged) && matches!(plan.meas_act, BillAct::None | BillAct::Unchanged);

    sql(tx.execute(
        "INSERT INTO aos_falcon_import (user_id, project_id, file_name, file_sha256, drawing_revision, added, changed, unchanged, refused, unmapped)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, 0, ?9)",
        params![actor, pid, file_name, sha, rev, result.added as i64, result.changed as i64, result.unchanged as i64, result.unmapped.join(",")],
    ))?;
    audit(
        &tx,
        Some(actor),
        if result.noop { "falcon_csv_import_nochange" } else { "falcon_csv_import" },
        &format!(
            "{file_name} sha256={sha} project={pid} drawing=r{rev} added={} changed={} unchanged={} refused=0 unmapped={}",
            result.added,
            result.changed,
            result.unchanged,
            result.unmapped.len()
        ),
    );
    sql(tx.commit())?;
    Ok(result)
}

/// The estimate or bill (approval) rows an import attached to a version: the priced basis and the measured lines.
pub fn rows_of(conn: &Connection, approval_id: i64, row_type: &str) -> Result<Vec<Value>, String> {
    Ok(stored_rows(conn, approval_id)?.into_iter().filter(|((t, _), _)| t == row_type).map(|(_, (_, p))| p).collect())
}

#[cfg(test)]
mod tests;
