//! Hours recorded against a project. A time line is a record only; it does not post a journal.

use crate::auth::Permission;
use crate::billing::{parse_decimal, validate_date};
use crate::commands::{HostState, require, INTERNAL};
use crate::db;
use rusqlite::{params, Connection};
use serde::Serialize;

#[derive(Serialize)]
pub struct TimeRow {
    pub id: i64,
    pub project_id: i64,
    pub project: String,
    pub client: String,
    pub person: String,
    pub work_date: String,
    pub hours_display: String,
    pub minutes: i64,
    pub note: String,
}

fn hours_to_minutes(hours: &str) -> Result<i64, String> {
    let hundredths = parse_decimal(hours, 2).map_err(|_| "Enter the hours as a number, for example 1.5.".to_string())?;
    if hundredths <= 0 || hundredths > 2_400 {
        return Err("Enter between a few minutes and 24 hours.".into());
    }
    let minutes = hundredths * 60 / 100;
    if minutes <= 0 {
        return Err("Enter between a few minutes and 24 hours.".into());
    }
    Ok(minutes)
}

fn hours_display(minutes: i64) -> String {
    format!("{}.{:02}", minutes / 60, (minutes % 60) * 100 / 60)
}

pub fn record(conn: &Connection, user_id: i64, project_id: i64, person: &str, work_date: &str, hours: &str, note: &str) -> Result<i64, String> {
    validate_date(work_date)?;
    let minutes = hours_to_minutes(hours)?;
    let note = note.trim();
    if note.chars().count() > 500 {
        return Err("Keep the note to 500 characters.".into());
    }
    let exists: i64 = conn.query_row("SELECT COUNT(*) FROM projects WHERE id = ?1", params![project_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if exists == 0 {
        return Err("Choose a project.".into());
    }
    let person = if person.trim().is_empty() {
        conn.query_row("SELECT username FROM users WHERE id = ?1", params![user_id], |r| r.get(0)).unwrap_or_else(|_| "Staff".to_string())
    } else {
        let name = person.trim().to_string();
        if name.chars().count() > 80 {
            return Err("Keep the name to 80 characters.".into());
        }
        name
    };
    conn.execute(
        "INSERT INTO time_entries (project_id, person, work_date, minutes, note, created_by) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
        params![project_id, person, work_date, minutes, note, user_id],
    )
    .map_err(|e| e.to_string())?;
    let id = conn.last_insert_rowid();
    db::audit(conn, Some(user_id), "time_recorded", &id.to_string());
    Ok(id)
}

pub fn list(conn: &Connection, project_id: Option<i64>) -> Result<Vec<TimeRow>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT t.id, t.project_id, p.name, c.name, t.person, t.work_date, t.minutes, t.note
             FROM time_entries t JOIN projects p ON p.id = t.project_id JOIN clients c ON c.id = p.client_id
             WHERE t.cancelled = 0 AND (?1 IS NULL OR t.project_id = ?1)
             ORDER BY t.work_date DESC, t.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![project_id], |r| {
            let minutes: i64 = r.get(6)?;
            Ok(TimeRow {
                id: r.get(0)?,
                project_id: r.get(1)?,
                project: r.get(2)?,
                client: r.get(3)?,
                person: r.get(4)?,
                work_date: r.get(5)?,
                hours_display: hours_display(minutes),
                minutes,
                note: r.get(7)?,
            })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

#[derive(Serialize)]
pub struct UseRow {
    pub client: String,
    pub project: String,
    pub hours_display: String,
    pub minutes: i64,
    pub fee_display: String,
    pub per_hour_display: String,
    pub share_bp: i64,
}

#[derive(Serialize)]
pub struct Utilization {
    pub rows: Vec<UseRow>,
    pub hours_display: String,
    pub fee_display: String,
}

/// Hours on each project beside the latest issued agreement fee. Fee per hour is that fee divided by the recorded hours.
pub fn utilization(conn: &Connection) -> Result<Utilization, String> {
    let mut stmt = conn
        .prepare(
            "SELECT c.name, p.name,
                    COALESCE((SELECT SUM(t.minutes) FROM time_entries t WHERE t.project_id = p.id AND t.cancelled = 0), 0),
                    COALESCE((SELECT d.fee_paise FROM documents d WHERE d.project_id = p.id AND d.kind = 'agreement' AND d.status IN ('issued','signed') ORDER BY d.id DESC LIMIT 1), 0)
             FROM projects p JOIN clients c ON c.id = p.client_id
             WHERE p.archived = 0
               AND (EXISTS (SELECT 1 FROM time_entries t WHERE t.project_id = p.id AND t.cancelled = 0)
                    OR EXISTS (SELECT 1 FROM documents d WHERE d.project_id = p.id AND d.kind = 'agreement' AND d.status IN ('issued','signed')))
             ORDER BY c.name, p.name",
        )
        .map_err(|e| e.to_string())?;
    let found = stmt
        .query_map([], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?, r.get::<_, i64>(2)?, r.get::<_, i64>(3)?)))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    let total_minutes: i64 = found.iter().map(|row| row.2).sum();
    let total_fee: i64 = found.iter().map(|row| row.3).sum();
    let rows = found
        .into_iter()
        .map(|(client, project, minutes, fee)| {
            let per_hour = if minutes <= 0 {
                "No hours recorded".to_string()
            } else if fee <= 0 {
                "No issued agreement".to_string()
            } else {
                crate::billing::fmt_inr(fee.saturating_mul(60) / minutes)
            };
            UseRow {
                client,
                project,
                hours_display: hours_display(minutes),
                minutes,
                fee_display: crate::billing::fmt_inr(fee),
                per_hour_display: per_hour,
                share_bp: if total_minutes <= 0 { 0 } else { minutes.saturating_mul(10_000) / total_minutes },
            }
        })
        .collect();
    Ok(Utilization { rows, hours_display: hours_display(total_minutes), fee_display: crate::billing::fmt_inr(total_fee) })
}

pub fn cancel(conn: &Connection, user_id: i64, id: i64) -> Result<(), String> {
    let changed = conn
        .execute("UPDATE time_entries SET cancelled = 1 WHERE id = ?1 AND cancelled = 0", params![id])
        .map_err(|e| e.to_string())?;
    if changed == 0 {
        return Err("That time line is not in the list.".into());
    }
    db::audit(conn, Some(user_id), "time_cancelled", &id.to_string());
    Ok(())
}

pub fn time_entry_record(state: HostState<'_>, project_id: i64, person: String, work_date: String, hours: String, note: String) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    record(&conn, uid, project_id, &person, &work_date, &hours, &note)
}

pub fn time_entry_list(state: HostState<'_>, project_id: Option<i64>) -> Result<Vec<TimeRow>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list(&conn, project_id)
}

pub fn time_entry_cancel(state: HostState<'_>, id: i64) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    cancel(&conn, uid, id)
}

pub fn time_utilization(state: HostState<'_>) -> Result<Utilization, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    utilization(&conn)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_hour_and_a_half_is_listed_and_a_cancelled_line_drops_off() {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('ada','x','owner')", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1,'Villa')", []).unwrap();
        let id = record(&c, 1, 1, "", "2026-05-02", "1.50", "Site visit").unwrap();
        let rows = list(&c, None).unwrap();
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].person, "ada");
        assert_eq!(rows[0].minutes, 90);
        assert_eq!(rows[0].hours_display, "1.50");
        assert!(record(&c, 1, 1, "Ada", "2026-05-02", "0", "").is_err());
        cancel(&c, 1, id).unwrap();
        assert!(list(&c, Some(1)).unwrap().is_empty());
    }

    #[test]
    fn fee_per_hour_uses_the_issued_agreement_and_the_recorded_hours() {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('ada','x','owner')", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1,'Villa')", []).unwrap();
        c.execute(
            "INSERT INTO documents (kind, number, project_id, doc_date, fee_paise, status) VALUES ('agreement', 'AGR-1', 1, '2026-05-01', 150000, 'issued')",
            [],
        )
        .unwrap();
        record(&c, 1, 1, "Ada", "2026-05-02", "1.50", "Site visit").unwrap();
        let use_of = utilization(&c).unwrap();
        assert_eq!(use_of.rows.len(), 1);
        assert_eq!(use_of.rows[0].minutes, 90);
        assert_eq!(use_of.rows[0].per_hour_display, crate::billing::fmt_inr(100_000));
        assert_eq!(use_of.rows[0].share_bp, 10_000);
    }
}
