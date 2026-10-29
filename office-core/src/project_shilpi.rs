//! A project's ShilpiDB file: the one place its estimate, running billing, tasks, quantities and purchase orders are read from.
//!
//! The company file keeps only the path of the file. The figures are read from the file each time they are asked for and are never
//! copied into the company file, so a change made to the project file shows on the next read.

use rusqlite::{params, Connection};
use serde_json::{json, Value};
use std::path::Path;

/// The linked file's path, empty when none is linked.
pub fn path(conn: &Connection, project_id: i64) -> Result<String, String> {
    conn.query_row("SELECT shilpi_path FROM projects WHERE id=?1", params![project_id], |r| r.get(0))
        .map_err(|_| "Project not found.".to_string())
}

/// Links the file at `path` to the project (an empty path unlinks it) after checking that it can be read. Returns the stored path.
pub fn set(conn: &Connection, project_id: i64, path: &str) -> Result<String, String> {
    let path = path.trim().to_string();
    if !path.is_empty() {
        let found = Path::new(&path);
        if !found.is_file() {
            return Err("That ShilpiDB file was not found.".into());
        }
        shilpi_project::read_file(found).map_err(|e| format!("That file cannot be used: {e}"))?;
    }
    let changed = conn
        .execute("UPDATE projects SET shilpi_path=?1 WHERE id=?2", params![path, project_id])
        .map_err(|_| "Internal error.".to_string())?;
    if changed == 0 {
        return Err("Project not found.".into());
    }
    Ok(path)
}

/// The project's figures read from its file, with totals.
pub fn read(conn: &Connection, project_id: i64) -> Result<Value, String> {
    let file = path(conn, project_id)?;
    if file.is_empty() {
        return Ok(json!({ "linked": false }));
    }
    Ok(summary(&file))
}

pub fn summary(file: &str) -> Value {
    let data = match shilpi_project::read_file(Path::new(file)) {
        Ok(data) => data,
        Err(e) => {
            let why = if e.kind() == std::io::ErrorKind::NotFound {
                "The linked ShilpiDB file was not found. Choose it again.".to_string()
            } else {
                format!("The linked ShilpiDB file cannot be read: {e}")
            };
            return json!({ "linked": true, "path": file, "problem": why });
        }
    };
    let estimate: f64 = data.estimate.iter().map(|e| e.amount()).sum();
    let billing: f64 = data.billing.iter().map(|b| b.amount).sum();
    let billed: f64 = data.billing.iter().filter(|b| b.status != "pending").map(|b| b.amount).sum();
    let paid: f64 = data.billing.iter().filter(|b| b.status == "paid").map(|b| b.amount).sum();
    let orders: f64 = data.orders.iter().map(|o| o.amount).sum();
    let open_tasks = data.tasks.iter().filter(|t| t.status != "done").count();
    json!({
        "linked": true,
        "path": file,
        "other_records": data.other_records,
        "totals": { "estimate": estimate, "billing": billing, "billed": billed, "paid": paid, "orders": orders, "open_tasks": open_tasks },
        "estimate": data.estimate.iter().map(|e| json!({ "code": e.code, "description": e.description, "unit": e.unit, "quantity": e.quantity, "rate": e.rate, "amount": e.amount() })).collect::<Vec<_>>(),
        "billing": data.billing.iter().map(|b| json!({ "stage": b.stage, "description": b.description, "percent": b.percent, "amount": b.amount, "status": b.status, "date": b.date })).collect::<Vec<_>>(),
        "tasks": data.tasks.iter().map(|t| json!({ "title": t.title, "assignee": t.assignee, "due": t.due, "status": t.status, "percent": t.percent })).collect::<Vec<_>>(),
        "quantities": data.quantities.iter().map(|q| json!({ "item": q.item, "description": q.description, "unit": q.unit, "quantity": q.quantity, "source": q.source })).collect::<Vec<_>>(),
        "orders": data.orders.iter().map(|o| json!({ "number": o.number, "vendor": o.vendor, "date": o.date, "description": o.description, "amount": o.amount, "status": o.status })).collect::<Vec<_>>(),
    })
}

/// The linked projects whose file can be read, as (id, code, name, data). A project whose file is missing or damaged is left out, so
/// its company-file records keep showing instead of nothing.
fn linked(conn: &Connection, only: Option<i64>) -> Vec<(i64, String, String, shilpi_project::ProjectData)> {
    let Ok(mut stmt) = conn.prepare("SELECT id, code, name, shilpi_path FROM projects WHERE shilpi_path<>'' AND (?1 IS NULL OR id=?1)") else {
        return Vec::new();
    };
    let rows = stmt
        .query_map(params![only], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, String>(1)?, r.get::<_, String>(2)?, r.get::<_, String>(3)?)))
        .map(|it| it.flatten().collect::<Vec<_>>())
        .unwrap_or_default();
    rows.into_iter()
        .filter_map(|(id, code, name, file)| shilpi_project::read_file(Path::new(&file)).ok().map(|d| (id, code, name, d)))
        .collect()
}

/// Replaces the company-file tasks of every project that has a readable ShilpiDB file with that file's tasks (read-only, `id` 0).
pub fn merge_tasks(conn: &Connection, project_id: Option<i64>, mut rows: Vec<Value>) -> Vec<Value> {
    for (id, code, name, data) in linked(conn, project_id) {
        rows.retain(|r| r.get("project_id").and_then(Value::as_i64) != Some(id));
        for t in &data.tasks {
            let status = match t.status.as_str() {
                "done" => "done",
                "doing" => "doing",
                _ => "todo",
            };
            rows.push(json!({
                "id": 0, "project_id": id, "project": name, "project_code": code, "title": t.title, "notes": t.assignee,
                "assignee": t.assignee, "status": status, "due_date": t.due, "percent": t.percent, "read_only": true,
            }));
        }
    }
    rows
}

/// The estimate total from the project's ShilpiDB file, in the shape `project_estimate_summary` returns; None when the project has no
/// readable file with estimate items.
pub fn estimate_summary(conn: &Connection, project_id: i64) -> Option<Value> {
    let (_, _, _, data) = linked(conn, Some(project_id)).into_iter().next()?;
    if data.estimate.is_empty() {
        return None;
    }
    let total: f64 = data.estimate.iter().map(|e| e.amount()).sum();
    Some(json!({
        "present": true,
        "grand_total_rupees": total,
        "grand_total_paise": (total * 100.0).round() as i64,
        "rate_book_version_name": "ShilpiDB estimate",
        "document_revision": "from the project file",
        "source": "shilpidb",
    }))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_missing_file_is_reported_not_raised() {
        let v = summary("Z:/no/such/file.vdb");
        assert_eq!(v["linked"], true);
        assert!(v["problem"].as_str().unwrap().contains("not found"));
    }
}
