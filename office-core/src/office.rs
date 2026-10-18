//! Staff roster, leave, and the office library.

use crate::audit as audit_log;
use crate::check::{date, one_of, text};
use rusqlite::{params, Connection};
use serde::Deserialize;
use serde_json::{json, Value};

const LEAVE_KINDS: [&str; 4] = ["casual", "sick", "earned", "unpaid"];
const LEAVE_STATUS: [&str; 3] = ["requested", "approved", "rejected"];
const LIBRARY_KINDS: [&str; 4] = ["specification", "standard", "compliance", "lesson"];

// ---------------------------------------------------------------- staff

#[derive(Deserialize)]
pub struct StaffInput {
    pub id: Option<i64>,
    pub name: String,
    pub role: String,
    pub specialisation: String,
    pub phone: String,
    pub email: String,
    pub joined_date: String,
    pub left_date: String,
}

pub fn staff_save(conn: &Connection, user_id: i64, i: &StaffInput) -> Result<i64, String> {
    let name = text(&i.name, 120, "name", true)?;
    let role = text(&i.role, 80, "role", false)?;
    let spec = text(&i.specialisation, 120, "specialisation", false)?;
    let phone = text(&i.phone, 40, "phone", false)?;
    let email = text(&i.email, 160, "email", false)?;
    let joined = date(&i.joined_date, false)?;
    let left = date(&i.left_date, false)?;
    if !left.is_empty() && !joined.is_empty() && left < joined {
        return Err("The leaving date is before the joining date.".into());
    }
    match i.id {
        Some(id) => {
            let n = conn
                .execute(
                    "UPDATE staff SET name=?2, role=?3, specialisation=?4, phone=?5, email=?6, joined_date=?7, left_date=?8 WHERE id = ?1",
                    params![id, name, role, spec, phone, email, joined, left],
                )
                .map_err(|e| e.to_string())?;
            if n == 0 {
                return Err("That person is not on the roster.".into());
            }
            audit_log(conn, Some(user_id), "staff_updated", &id.to_string());
            Ok(id)
        }
        None => {
            conn.execute(
                "INSERT INTO staff (name, role, specialisation, phone, email, joined_date, left_date, created_by) VALUES (?1,?2,?3,?4,?5,?6,?7,?8)",
                params![name, role, spec, phone, email, joined, left, user_id],
            )
            .map_err(|e| e.to_string())?;
            let id = conn.last_insert_rowid();
            audit_log(conn, Some(user_id), "staff_added", &id.to_string());
            Ok(id)
        }
    }
}

/// The roster with approved leave days taken in the current financial year (April to March).
pub fn staff_list(conn: &Connection) -> Result<Vec<Value>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT s.id, s.name, s.role, s.specialisation, s.phone, s.email, s.joined_date, s.left_date,
                    COALESCE((SELECT SUM(days) FROM leaves l WHERE l.staff_id = s.id AND l.status = 'approved' AND l.kind <> 'unpaid'
                              AND l.from_date >= date('now','localtime','start of year', CASE WHEN CAST(strftime('%m','now','localtime') AS INTEGER) >= 4 THEN '+3 months' ELSE '-9 months' END)), 0)
             FROM staff s ORDER BY (s.left_date <> ''), s.name COLLATE NOCASE",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| {
            let left: String = r.get(7)?;
            Ok(json!({
                "id": r.get::<_, i64>(0)?, "name": r.get::<_, String>(1)?, "role": r.get::<_, String>(2)?,
                "specialisation": r.get::<_, String>(3)?, "phone": r.get::<_, String>(4)?, "email": r.get::<_, String>(5)?,
                "joined_date": r.get::<_, String>(6)?, "left_date": left, "active": left.is_empty(),
                "leave_days_this_year": r.get::<_, i64>(8)?,
            }))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

// ---------------------------------------------------------------- leave

#[derive(Deserialize)]
pub struct LeaveInput {
    pub id: Option<i64>,
    pub staff_id: i64,
    pub kind: String,
    pub from_date: String,
    pub to_date: String,
    pub status: String,
    pub reason: String,
}

pub fn leave_save(conn: &Connection, user_id: i64, i: &LeaveInput) -> Result<i64, String> {
    let kind = one_of(&i.kind, &LEAVE_KINDS, "kind of leave")?;
    let status = one_of(&i.status, &LEAVE_STATUS, "status")?;
    let from = date(&i.from_date, true)?;
    let to = date(&i.to_date, true)?;
    if to < from {
        return Err("The last day is before the first day.".into());
    }
    let reason = text(&i.reason, 300, "reason", false)?;
    let known: i64 = conn.query_row("SELECT COUNT(*) FROM staff WHERE id = ?1", params![i.staff_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if known == 0 {
        return Err("Choose a person from the roster.".into());
    }
    let days: i64 = conn.query_row("SELECT CAST(julianday(?2) - julianday(?1) AS INTEGER) + 1", params![from, to], |r| r.get(0)).map_err(|e| e.to_string())?;
    if days > 366 {
        return Err("Leave cannot run for more than a year.".into());
    }
    let overlap: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM leaves WHERE staff_id = ?1 AND status <> 'rejected' AND from_date <= ?3 AND to_date >= ?2 AND id <> COALESCE(?4, 0)",
            params![i.staff_id, from, to, i.id],
            |r| r.get(0),
        )
        .map_err(|e| e.to_string())?;
    if overlap > 0 && status != "rejected" {
        return Err("This person already has leave on some of those days.".into());
    }
    match i.id {
        Some(id) => {
            let n = conn
                .execute(
                    "UPDATE leaves SET kind=?2, from_date=?3, to_date=?4, days=?5, status=?6, reason=?7 WHERE id = ?1 AND staff_id = ?8",
                    params![id, kind, from, to, days, status, reason, i.staff_id],
                )
                .map_err(|e| e.to_string())?;
            if n == 0 {
                return Err("That leave is not on this person's record.".into());
            }
            audit_log(conn, Some(user_id), "leave_updated", &id.to_string());
            Ok(id)
        }
        None => {
            conn.execute(
                "INSERT INTO leaves (staff_id, kind, from_date, to_date, days, status, reason, created_by) VALUES (?1,?2,?3,?4,?5,?6,?7,?8)",
                params![i.staff_id, kind, from, to, days, status, reason, user_id],
            )
            .map_err(|e| e.to_string())?;
            let id = conn.last_insert_rowid();
            audit_log(conn, Some(user_id), "leave_added", &id.to_string());
            Ok(id)
        }
    }
}

pub fn leave_list(conn: &Connection) -> Result<Vec<Value>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT l.id, l.staff_id, s.name, l.kind, l.from_date, l.to_date, l.days, l.status, l.reason
             FROM leaves l JOIN staff s ON s.id = l.staff_id ORDER BY (l.status = 'requested') DESC, l.from_date DESC, l.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| {
            Ok(json!({
                "id": r.get::<_, i64>(0)?, "staff_id": r.get::<_, i64>(1)?, "staff": r.get::<_, String>(2)?, "kind": r.get::<_, String>(3)?,
                "from_date": r.get::<_, String>(4)?, "to_date": r.get::<_, String>(5)?, "days": r.get::<_, i64>(6)?,
                "status": r.get::<_, String>(7)?, "reason": r.get::<_, String>(8)?,
            }))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

// ---------------------------------------------------------------- library

#[derive(Deserialize)]
pub struct LibraryInput {
    pub id: Option<i64>,
    pub kind: String,
    pub title: String,
    pub category: String,
    pub reference: String,
    pub body: String,
}

pub fn library_save(conn: &Connection, user_id: i64, i: &LibraryInput) -> Result<i64, String> {
    let kind = one_of(&i.kind, &LIBRARY_KINDS, "kind of entry")?;
    let title = text(&i.title, 160, "title", true)?;
    let category = text(&i.category, 80, "category", false)?;
    let reference = text(&i.reference, 120, "reference", false)?;
    let body = text(&i.body, 8000, "text", false)?;
    match i.id {
        Some(id) => {
            let n = conn
                .execute(
                    "UPDATE library_items SET kind=?2, title=?3, category=?4, reference=?5, body=?6 WHERE id = ?1",
                    params![id, kind, title, category, reference, body],
                )
                .map_err(|e| e.to_string())?;
            if n == 0 {
                return Err("That entry is not in the library.".into());
            }
            audit_log(conn, Some(user_id), "library_updated", &id.to_string());
            Ok(id)
        }
        None => {
            conn.execute(
                "INSERT INTO library_items (kind, title, category, reference, body, created_by) VALUES (?1,?2,?3,?4,?5,?6)",
                params![kind, title, category, reference, body, user_id],
            )
            .map_err(|e| e.to_string())?;
            let id = conn.last_insert_rowid();
            audit_log(conn, Some(user_id), "library_added", &id.to_string());
            Ok(id)
        }
    }
}

/// Library entries, optionally narrowed to a kind and to a search word found in the title, category, reference or text.
pub fn library_list(conn: &Connection, kind: Option<&str>, search: &str) -> Result<Vec<Value>, String> {
    let like = format!("%{}%", search.trim().replace('%', "").replace('_', ""));
    let mut stmt = conn
        .prepare(
            "SELECT id, kind, title, category, reference, body FROM library_items
             WHERE (?1 IS NULL OR kind = ?1) AND (title LIKE ?2 OR category LIKE ?2 OR reference LIKE ?2 OR body LIKE ?2)
             ORDER BY kind, category COLLATE NOCASE, title COLLATE NOCASE",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![kind, like], |r| {
            Ok(json!({
                "id": r.get::<_, i64>(0)?, "kind": r.get::<_, String>(1)?, "title": r.get::<_, String>(2)?,
                "category": r.get::<_, String>(3)?, "reference": r.get::<_, String>(4)?, "body": r.get::<_, String>(5)?,
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

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::schema::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('ada','x','owner')", []).unwrap();
        c
    }

    fn person(name: &str) -> StaffInput {
        StaffInput { id: None, name: name.into(), role: "Architect".into(), specialisation: String::new(), phone: String::new(), email: String::new(), joined_date: "2024-01-01".into(), left_date: String::new() }
    }

    fn leave(from: &str, to: &str, status: &str) -> LeaveInput {
        LeaveInput { id: None, staff_id: 1, kind: "casual".into(), from_date: from.into(), to_date: to.into(), status: status.into(), reason: String::new() }
    }

    #[test]
    fn leave_counts_days_and_refuses_overlaps() {
        let c = conn();
        staff_save(&c, 1, &person("Meera")).unwrap();
        assert!(staff_save(&c, 1, &StaffInput { left_date: "2023-01-01".into(), ..person("Bad") }).is_err());
        let id = leave_save(&c, 1, &leave("2026-05-04", "2026-05-06", "approved")).unwrap();
        assert_eq!(leave_list(&c).unwrap()[0]["days"], 3);
        assert!(leave_save(&c, 1, &leave("2026-05-06", "2026-05-08", "requested")).is_err());
        assert!(leave_save(&c, 1, &leave("2026-05-08", "2026-05-01", "requested")).is_err());
        // a rejected request does not block the same days, and editing a leave does not clash with itself
        leave_save(&c, 1, &leave("2026-05-06", "2026-05-08", "rejected")).unwrap();
        leave_save(&c, 1, &LeaveInput { id: Some(id), ..leave("2026-05-04", "2026-05-05", "approved") }).unwrap();
        assert_eq!(leave_list(&c).unwrap().iter().map(|l| l["days"].as_i64().unwrap()).sum::<i64>(), 5);
        assert!(leave_save(&c, 1, &LeaveInput { staff_id: 9, ..leave("2026-06-01", "2026-06-01", "requested") }).is_err());
        assert_eq!(staff_list(&c).unwrap().len(), 1);
    }

    #[test]
    fn the_library_filters_by_kind_and_search_word() {
        let c = conn();
        for (kind, title, body) in [("specification", "Vitrified tiles", "600x600 matt"), ("standard", "Door schedule", "Sizes and ratings"), ("lesson", "Waterproofing", "Test the tanks before tiling")] {
            library_save(&c, 1, &LibraryInput { id: None, kind: kind.into(), title: title.into(), category: String::new(), reference: String::new(), body: body.into() }).unwrap();
        }
        assert_eq!(library_list(&c, None, "").unwrap().len(), 3);
        assert_eq!(library_list(&c, Some("lesson"), "").unwrap().len(), 1);
        assert_eq!(library_list(&c, None, "tiling").unwrap()[0]["title"], "Waterproofing");
        assert!(library_list(&c, None, "100%").unwrap().is_empty());
        assert!(library_save(&c, 1, &LibraryInput { id: None, kind: "other".into(), title: "x".into(), category: String::new(), reference: String::new(), body: String::new() }).is_err());
    }
}
