//! Consultancy payouts / subcontracts for a freelance practice: money owed to other consultants on a project.
//! Not staff salaries. Posts nothing to the ledger yet — it is the schedule of work and pay; settlement stays a separate expense.

use rusqlite::{params, Connection};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::schema::audit;

const STATUSES: &[&str] = &["quoted", "agreed", "paid", "cancelled"];

#[derive(Deserialize)]
pub struct SubcontractInput {
    pub id: Option<i64>,
    pub project_id: i64,
    pub consultant: String,
    #[serde(default)]
    pub work: String,
    #[serde(default)]
    pub amount_paise: i64,
    #[serde(default = "default_status")]
    pub status: String,
    #[serde(default)]
    pub due_date: String,
    #[serde(default)]
    pub paid_date: String,
    #[serde(default)]
    pub notes: String,
}

fn default_status() -> String {
    "quoted".into()
}

fn text(raw: &str, max: usize, label: &str, required: bool) -> Result<String, String> {
    let t = raw.trim();
    if required && t.is_empty() {
        return Err(format!("Enter the {label}."));
    }
    if t.chars().count() > max {
        return Err(format!("Keep the {label} under {max} characters."));
    }
    Ok(t.into())
}

fn status(raw: &str) -> Result<&str, String> {
    STATUSES.iter().copied().find(|s| *s == raw).ok_or_else(|| "Status must be quoted, agreed, paid or cancelled.".into())
}

fn day(raw: &str) -> Result<String, String> {
    let d = raw.trim();
    if d.is_empty() {
        return Ok(String::new());
    }
    if d.len() != 10 || d.as_bytes().get(4) != Some(&b'-') {
        return Err("Dates must be YYYY-MM-DD.".into());
    }
    Ok(d.into())
}

pub fn save(conn: &Connection, user_id: i64, i: &SubcontractInput) -> Result<i64, String> {
    let consultant = text(&i.consultant, 160, "consultant's name", true)?;
    let work = text(&i.work, 500, "work description", false)?;
    let notes = text(&i.notes, 2000, "notes", false)?;
    let status = status(&i.status)?;
    let due_date = day(&i.due_date)?;
    let paid_date = day(&i.paid_date)?;
    if i.amount_paise < 0 {
        return Err("Amount cannot be negative.".into());
    }
    let n: i64 = conn.query_row("SELECT COUNT(*) FROM projects WHERE id = ?1", [i.project_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if n == 0 {
        return Err("Choose a project.".into());
    }
    if status == "paid" && paid_date.is_empty() {
        return Err("Enter the date the consultant was paid.".into());
    }
    match i.id {
        Some(id) => {
            let n = conn
                .execute(
                    "UPDATE subcontracts SET project_id=?2, consultant=?3, work=?4, amount_paise=?5, status=?6, due_date=?7, paid_date=?8, notes=?9, updated_at=datetime('now') WHERE id=?1",
                    params![id, i.project_id, consultant, work, i.amount_paise, status, due_date, paid_date, notes],
                )
                .map_err(|e| e.to_string())?;
            if n == 0 {
                return Err("That subcontract does not exist.".into());
            }
            audit(conn, Some(user_id), "subcontract_updated", &id.to_string());
            Ok(id)
        }
        None => {
            conn.execute(
                "INSERT INTO subcontracts (project_id, consultant, work, amount_paise, status, due_date, paid_date, notes, created_by)
                 VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9)",
                params![i.project_id, consultant, work, i.amount_paise, status, due_date, paid_date, notes, user_id],
            )
            .map_err(|e| e.to_string())?;
            let id = conn.last_insert_rowid();
            audit(conn, Some(user_id), "subcontract_created", &id.to_string());
            Ok(id)
        }
    }
}

pub fn list(conn: &Connection, project_id: Option<i64>) -> Result<Vec<Value>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT s.id, s.project_id, p.name, p.code, s.consultant, s.work, s.amount_paise, s.status, s.due_date, s.paid_date, s.notes
             FROM subcontracts s JOIN projects p ON p.id = s.project_id
             WHERE (?1 IS NULL OR s.project_id = ?1)
             ORDER BY CASE s.status WHEN 'quoted' THEN 0 WHEN 'agreed' THEN 1 WHEN 'paid' THEN 2 ELSE 3 END, s.due_date, s.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([project_id], |r| {
            let amount: i64 = r.get(6)?;
            Ok(json!({
                "id": r.get::<_, i64>(0)?,
                "project_id": r.get::<_, i64>(1)?,
                "project": r.get::<_, String>(2)?,
                "project_code": r.get::<_, String>(3)?,
                "consultant": r.get::<_, String>(4)?,
                "work": r.get::<_, String>(5)?,
                "amount_paise": amount,
                "amount_display": format!("{:.2}", amount as f64 / 100.0),
                "status": r.get::<_, String>(7)?,
                "due_date": r.get::<_, String>(8)?,
                "paid_date": r.get::<_, String>(9)?,
                "notes": r.get::<_, String>(10)?,
            }))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn records_a_consultant_payout_on_a_project() {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::schema::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('ada','x','owner')", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Client')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, code, name) VALUES (1, 'P1', 'House')", []).unwrap();
        let id = save(
            &c,
            1,
            &SubcontractInput {
                id: None,
                project_id: 1,
                consultant: "Structure Works".into(),
                work: "Structural design".into(),
                amount_paise: 50_000_00,
                status: "agreed".into(),
                due_date: "2026-11-01".into(),
                paid_date: String::new(),
                notes: String::new(),
            },
        )
        .unwrap();
        let rows = list(&c, Some(1)).unwrap();
        assert_eq!(rows[0]["id"], id);
        assert_eq!(rows[0]["consultant"], "Structure Works");
    }
}
