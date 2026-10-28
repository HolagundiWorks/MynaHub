//! Drawings and documents sent on a project. The log is the record; cancelling a line drops it from the list.

use crate::auth::Permission;
use crate::billing::validate_date;
use crate::commands::{HostState, require, INTERNAL};
use crate::db;
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};

#[derive(Deserialize)]
pub struct ItemInput {
    pub drawing_number: String,
    pub title: String,
    pub revision: String,
    pub copies: i64,
}

#[derive(Serialize)]
pub struct ItemRow {
    pub drawing_number: String,
    pub title: String,
    pub revision: String,
    pub copies: i64,
}

#[derive(Serialize)]
pub struct TransmittalRow {
    pub id: i64,
    pub project_id: i64,
    pub project: String,
    pub client: String,
    pub sent_date: String,
    pub recipient: String,
    pub purpose: String,
    pub medium: String,
    pub reference: String,
    pub acknowledged: bool,
    pub items: Vec<ItemRow>,
}

pub fn record(
    conn: &Connection,
    user_id: i64,
    project_id: i64,
    sent_date: &str,
    recipient: &str,
    purpose: &str,
    medium: &str,
    reference: &str,
    items: &[ItemInput],
) -> Result<i64, String> {
    validate_date(sent_date)?;
    let recipient = required(recipient, 120, "Enter who received the drawings.", "Keep the recipient to 120 characters.")?;
    let purpose = optional(purpose, 80, "Keep the purpose to 80 characters.")?;
    let medium = optional(medium, 40, "Keep how it was sent to 40 characters.")?;
    let reference = optional(reference, 80, "Keep the reference to 80 characters.")?;
    if items.is_empty() || items.len() > 40 {
        return Err("Add at least one drawing, and no more than 40.".into());
    }
    let exists: i64 = conn.query_row("SELECT COUNT(*) FROM projects WHERE id = ?1", params![project_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if exists == 0 {
        return Err("Choose a project.".into());
    }
    let mut clean = Vec::new();
    for (index, item) in items.iter().enumerate() {
        let title = item.title.trim();
        if title.is_empty() || title.chars().count() > 160 {
            return Err("Each drawing needs a title of up to 160 characters.".into());
        }
        let number = item.drawing_number.trim();
        if number.chars().count() > 40 {
            return Err("Keep the drawing number to 40 characters.".into());
        }
        let revision = item.revision.trim();
        if revision.chars().count() > 20 {
            return Err("Keep the revision to 20 characters.".into());
        }
        if !(1..=999).contains(&item.copies) {
            return Err("Enter the number of copies from 1 to 999.".into());
        }
        clean.push((index as i64 + 1, number.to_string(), title.to_string(), revision.to_string(), item.copies));
    }
    let tx = conn.unchecked_transaction().map_err(|e| e.to_string())?;
    tx.execute(
        "INSERT INTO transmittals (project_id, sent_date, recipient, purpose, medium, reference, created_by) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
        params![project_id, sent_date, recipient, purpose, medium, reference, user_id],
    )
    .map_err(|e| e.to_string())?;
    let id = tx.last_insert_rowid();
    for (position, number, title, revision, copies) in clean {
        tx.execute(
            "INSERT INTO transmittal_items (transmittal_id, position, drawing_number, title, revision, copies) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
            params![id, position, number, title, revision, copies],
        )
        .map_err(|e| e.to_string())?;
    }
    db::audit(&tx, Some(user_id), "transmittal_recorded", &id.to_string());
    tx.commit().map_err(|e| e.to_string())?;
    Ok(id)
}

pub fn list(conn: &Connection, project_id: Option<i64>) -> Result<Vec<TransmittalRow>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT t.id, t.project_id, p.name, c.name, t.sent_date, t.recipient, t.purpose, t.medium, t.reference, t.acknowledged
             FROM transmittals t JOIN projects p ON p.id = t.project_id JOIN clients c ON c.id = p.client_id
             WHERE t.cancelled = 0 AND (?1 IS NULL OR t.project_id = ?1)
             ORDER BY t.sent_date DESC, t.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let headers = stmt
        .query_map(params![project_id], |r| {
            Ok(TransmittalRow {
                id: r.get(0)?,
                project_id: r.get(1)?,
                project: r.get(2)?,
                client: r.get(3)?,
                sent_date: r.get(4)?,
                recipient: r.get(5)?,
                purpose: r.get(6)?,
                medium: r.get(7)?,
                reference: r.get(8)?,
                acknowledged: r.get::<_, i64>(9)? == 1,
                items: Vec::new(),
            })
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    let mut rows = Vec::new();
    for mut row in headers {
        let mut items = conn
            .prepare("SELECT drawing_number, title, revision, copies FROM transmittal_items WHERE transmittal_id = ?1 ORDER BY position")
            .map_err(|e| e.to_string())?;
        row.items = items
            .query_map(params![row.id], |r| {
                Ok(ItemRow { drawing_number: r.get(0)?, title: r.get(1)?, revision: r.get(2)?, copies: r.get(3)? })
            })
            .map_err(|e| e.to_string())?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|e| e.to_string())?;
        rows.push(row);
    }
    Ok(rows)
}

pub fn acknowledge(conn: &Connection, user_id: i64, id: i64) -> Result<(), String> {
    let changed = conn
        .execute("UPDATE transmittals SET acknowledged = 1 WHERE id = ?1 AND cancelled = 0 AND acknowledged = 0", params![id])
        .map_err(|e| e.to_string())?;
    if changed == 0 {
        return Err("That transmittal is not waiting for acknowledgement.".into());
    }
    db::audit(conn, Some(user_id), "transmittal_acknowledged", &id.to_string());
    Ok(())
}

pub fn cancel(conn: &Connection, user_id: i64, id: i64) -> Result<(), String> {
    let changed = conn.execute("UPDATE transmittals SET cancelled = 1 WHERE id = ?1 AND cancelled = 0", params![id]).map_err(|e| e.to_string())?;
    if changed == 0 {
        return Err("That transmittal is not in the log.".into());
    }
    db::audit(conn, Some(user_id), "transmittal_cancelled", &id.to_string());
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

pub fn transmittal_record(
    state: HostState<'_>,
    project_id: i64,
    sent_date: String,
    recipient: String,
    purpose: String,
    medium: String,
    reference: String,
    items: Vec<ItemInput>,
) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    record(&conn, uid, project_id, &sent_date, &recipient, &purpose, &medium, &reference, &items)
}

pub fn transmittal_list(state: HostState<'_>, project_id: Option<i64>) -> Result<Vec<TransmittalRow>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list(&conn, project_id)
}

pub fn transmittal_acknowledge(state: HostState<'_>, id: i64) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    acknowledge(&conn, uid, id)
}

pub fn transmittal_cancel(state: HostState<'_>, id: i64) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    cancel(&conn, uid, id)
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
        c
    }

    #[test]
    fn a_transmittal_lists_its_drawings_until_it_is_cancelled() {
        let c = conn();
        let id = record(
            &c,
            1,
            1,
            "2026-06-01",
            "Acme",
            "For approval",
            "Email",
            "TR-1",
            &[ItemInput { drawing_number: "A-01".into(), title: "Ground floor plan".into(), revision: "B".into(), copies: 2 }],
        )
        .unwrap();
        let rows = list(&c, Some(1)).unwrap();
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].items[0].title, "Ground floor plan");
        assert!(!rows[0].acknowledged);
        acknowledge(&c, 1, id).unwrap();
        assert!(list(&c, None).unwrap()[0].acknowledged);
        assert!(record(&c, 1, 1, "2026-06-01", "Acme", "", "", "", &[]).is_err());
        cancel(&c, 1, id).unwrap();
        assert!(list(&c, None).unwrap().is_empty());
    }
}
