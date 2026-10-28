//! Site records (instructions, inspections, snags), statutory permits and enquiries. Plain records kept beside the books; none posts.

use crate::auth::Permission;
use crate::commands::{HostState, require, INTERNAL};
use crate::db;
use office_core::check::{date, one_of, text, today};
use rusqlite::{params, Connection};
use serde::Deserialize;
use serde_json::{json, Value};

const SITE_KINDS: [&str; 3] = ["instruction", "inspection", "snag"];
const SITE_STATUS: [&str; 3] = ["open", "resolved", "closed"];
const PERMIT_STATUS: [&str; 5] = ["planned", "applied", "queried", "approved", "rejected"];
const ENQUIRY_STATUS: [&str; 5] = ["new", "meeting", "proposal", "won", "lost"];

fn project_exists(conn: &Connection, id: i64) -> Result<(), String> {
    let n: i64 = conn.query_row("SELECT COUNT(*) FROM projects WHERE id = ?1", params![id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if n == 0 {
        return Err("Choose a project.".into());
    }
    Ok(())
}

// ---------------------------------------------------------------- site records

#[derive(Deserialize)]
pub struct SiteInput {
    pub id: Option<i64>,
    pub project_id: i64,
    pub kind: String,
    pub record_date: String,
    pub title: String,
    pub details: String,
    pub location: String,
    pub assigned_to: String,
    pub due_date: String,
    pub status: String,
}

pub fn site_save(conn: &Connection, user_id: i64, i: &SiteInput) -> Result<i64, String> {
    project_exists(conn, i.project_id)?;
    let kind = one_of(&i.kind, &SITE_KINDS, "record type")?;
    let status = one_of(&i.status, &SITE_STATUS, "status")?;
    let day = date(&i.record_date, true)?;
    let title = text(&i.title, 160, "title", true)?;
    let details = text(&i.details, 2000, "details", false)?;
    let location = text(&i.location, 120, "location", false)?;
    let assigned = text(&i.assigned_to, 120, "person responsible", false)?;
    let due = date(&i.due_date, false)?;
    if !due.is_empty() && due < day {
        return Err("The due date is before the date of the record.".into());
    }
    let resolved = if status == "open" { String::new() } else { today(conn) };
    match i.id {
        Some(id) => {
            let n = conn
                .execute(
                    "UPDATE site_records SET kind=?2, record_date=?3, title=?4, details=?5, location=?6, assigned_to=?7, due_date=?8, status=?9,
                     resolved_date = CASE WHEN ?9 = 'open' THEN '' WHEN status = 'open' THEN ?10 ELSE resolved_date END
                     WHERE id = ?1 AND project_id = ?11",
                    params![id, kind, day, title, details, location, assigned, due, status, resolved, i.project_id],
                )
                .map_err(|e| e.to_string())?;
            if n == 0 {
                return Err("That site record is not in this project.".into());
            }
            db::audit(conn, Some(user_id), "site_record_updated", &id.to_string());
            Ok(id)
        }
        None => {
            conn.execute(
                "INSERT INTO site_records (project_id, kind, record_date, title, details, location, assigned_to, due_date, status, resolved_date, created_by)
                 VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11)",
                params![i.project_id, kind, day, title, details, location, assigned, due, status, resolved, user_id],
            )
            .map_err(|e| e.to_string())?;
            let id = conn.last_insert_rowid();
            db::audit(conn, Some(user_id), "site_record_added", &id.to_string());
            Ok(id)
        }
    }
}

pub fn site_list(conn: &Connection, project_id: Option<i64>, kind: Option<&str>) -> Result<Vec<Value>, String> {
    let today = today(conn);
    let mut stmt = conn
        .prepare(
            "SELECT s.id, s.project_id, p.name, s.kind, s.record_date, s.title, s.details, s.location, s.assigned_to, s.due_date, s.status, s.resolved_date
             FROM site_records s JOIN projects p ON p.id = s.project_id
             WHERE (?1 IS NULL OR s.project_id = ?1) AND (?2 IS NULL OR s.kind = ?2)
             ORDER BY (s.status = 'open') DESC, s.record_date DESC, s.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![project_id, kind], |r| {
            let status: String = r.get(10)?;
            let due: String = r.get(9)?;
            let overdue = status == "open" && !due.is_empty() && due < today;
            Ok(json!({
                "id": r.get::<_, i64>(0)?, "project_id": r.get::<_, i64>(1)?, "project": r.get::<_, String>(2)?,
                "kind": r.get::<_, String>(3)?, "record_date": r.get::<_, String>(4)?, "title": r.get::<_, String>(5)?,
                "details": r.get::<_, String>(6)?, "location": r.get::<_, String>(7)?, "assigned_to": r.get::<_, String>(8)?,
                "due_date": due, "status": status, "resolved_date": r.get::<_, String>(11)?, "overdue": overdue,
            }))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

// ---------------------------------------------------------------- permits

#[derive(Deserialize)]
pub struct PermitInput {
    pub id: Option<i64>,
    pub project_id: i64,
    pub authority: String,
    pub kind: String,
    pub reference: String,
    pub status: String,
    pub applied_date: String,
    pub decision_date: String,
    pub valid_until: String,
    pub notes: String,
}

pub fn permit_save(conn: &Connection, user_id: i64, i: &PermitInput) -> Result<i64, String> {
    project_exists(conn, i.project_id)?;
    let authority = text(&i.authority, 120, "authority", true)?;
    let kind = text(&i.kind, 120, "permit or approval", true)?;
    let reference = text(&i.reference, 80, "reference", false)?;
    let status = one_of(&i.status, &PERMIT_STATUS, "status")?;
    let applied = date(&i.applied_date, false)?;
    let decision = date(&i.decision_date, false)?;
    let valid = date(&i.valid_until, false)?;
    let notes = text(&i.notes, 1000, "notes", false)?;
    if status != "planned" && applied.is_empty() {
        return Err("Enter the date the application was made.".into());
    }
    if !decision.is_empty() && !applied.is_empty() && decision < applied {
        return Err("The decision date is before the application date.".into());
    }
    if (status == "approved" || status == "rejected") && decision.is_empty() {
        return Err("Enter the date of the decision.".into());
    }
    match i.id {
        Some(id) => {
            let n = conn
                .execute(
                    "UPDATE permits SET authority=?2, kind=?3, reference=?4, status=?5, applied_date=?6, decision_date=?7, valid_until=?8, notes=?9
                     WHERE id = ?1 AND project_id = ?10",
                    params![id, authority, kind, reference, status, applied, decision, valid, notes, i.project_id],
                )
                .map_err(|e| e.to_string())?;
            if n == 0 {
                return Err("That permit is not in this project.".into());
            }
            db::audit(conn, Some(user_id), "permit_updated", &id.to_string());
            Ok(id)
        }
        None => {
            conn.execute(
                "INSERT INTO permits (project_id, authority, kind, reference, status, applied_date, decision_date, valid_until, notes, created_by)
                 VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10)",
                params![i.project_id, authority, kind, reference, status, applied, decision, valid, notes, user_id],
            )
            .map_err(|e| e.to_string())?;
            let id = conn.last_insert_rowid();
            db::audit(conn, Some(user_id), "permit_added", &id.to_string());
            Ok(id)
        }
    }
}

/// Permits with a flag for approvals that have lapsed or lapse within 60 days.
pub fn permit_list(conn: &Connection, project_id: Option<i64>) -> Result<Vec<Value>, String> {
    let today = today(conn);
    let soon: String = conn.query_row("SELECT date(?1, '+60 days')", params![today], |r| r.get(0)).map_err(|e| e.to_string())?;
    let mut stmt = conn
        .prepare(
            "SELECT m.id, m.project_id, p.name, m.authority, m.kind, m.reference, m.status, m.applied_date, m.decision_date, m.valid_until, m.notes
             FROM permits m JOIN projects p ON p.id = m.project_id
             WHERE (?1 IS NULL OR m.project_id = ?1)
             ORDER BY (m.status IN ('applied','queried')) DESC, m.valid_until = '', m.valid_until, m.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![project_id], |r| {
            let status: String = r.get(6)?;
            let valid: String = r.get(9)?;
            let watch = if status == "approved" && !valid.is_empty() {
                if valid < today { "lapsed" } else if valid <= soon { "lapsing" } else { "" }
            } else {
                ""
            };
            Ok(json!({
                "id": r.get::<_, i64>(0)?, "project_id": r.get::<_, i64>(1)?, "project": r.get::<_, String>(2)?,
                "authority": r.get::<_, String>(3)?, "kind": r.get::<_, String>(4)?, "reference": r.get::<_, String>(5)?,
                "status": status, "applied_date": r.get::<_, String>(7)?, "decision_date": r.get::<_, String>(8)?,
                "valid_until": valid, "notes": r.get::<_, String>(10)?, "watch": watch,
            }))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

// ---------------------------------------------------------------- enquiries

#[derive(Deserialize)]
pub struct EnquiryInput {
    pub id: Option<i64>,
    pub received_date: String,
    pub name: String,
    pub contact: String,
    pub source: String,
    pub brief: String,
    pub status: String,
    pub follow_up: String,
    pub notes: String,
}

pub fn enquiry_save(conn: &Connection, user_id: i64, i: &EnquiryInput) -> Result<i64, String> {
    let day = date(&i.received_date, true)?;
    let name = text(&i.name, 120, "name", true)?;
    let contact = text(&i.contact, 160, "contact", false)?;
    let source = text(&i.source, 80, "source", false)?;
    let brief = text(&i.brief, 2000, "brief", false)?;
    let status = one_of(&i.status, &ENQUIRY_STATUS, "status")?;
    let follow = date(&i.follow_up, false)?;
    let notes = text(&i.notes, 1000, "notes", false)?;
    match i.id {
        Some(id) => {
            let n = conn
                .execute(
                    "UPDATE enquiries SET received_date=?2, name=?3, contact=?4, source=?5, brief=?6, status=?7, follow_up=?8, notes=?9 WHERE id = ?1",
                    params![id, day, name, contact, source, brief, status, follow, notes],
                )
                .map_err(|e| e.to_string())?;
            if n == 0 {
                return Err("That enquiry is not in the list.".into());
            }
            db::audit(conn, Some(user_id), "enquiry_updated", &id.to_string());
            Ok(id)
        }
        None => {
            conn.execute(
                "INSERT INTO enquiries (received_date, name, contact, source, brief, status, follow_up, notes, created_by) VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9)",
                params![day, name, contact, source, brief, status, follow, notes, user_id],
            )
            .map_err(|e| e.to_string())?;
            let id = conn.last_insert_rowid();
            db::audit(conn, Some(user_id), "enquiry_added", &id.to_string());
            Ok(id)
        }
    }
}

/// Turns an enquiry into a client record (once) and marks it won.
pub fn enquiry_convert(conn: &Connection, user_id: i64, id: i64) -> Result<i64, String> {
    let (name, contact, existing): (String, String, Option<i64>) = conn
        .query_row("SELECT name, contact, client_id FROM enquiries WHERE id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
        .map_err(|_| "That enquiry is not in the list.".to_string())?;
    if let Some(client) = existing {
        return Ok(client);
    }
    let tx = conn.unchecked_transaction().map_err(|e| e.to_string())?;
    tx.execute("INSERT INTO clients (name, email) VALUES (?1, ?2)", params![name, if contact.contains('@') { contact } else { String::new() }])
        .map_err(|e| e.to_string())?;
    let client = tx.last_insert_rowid();
    tx.execute("UPDATE enquiries SET client_id = ?2, status = 'won' WHERE id = ?1", params![id, client]).map_err(|e| e.to_string())?;
    db::audit(&tx, Some(user_id), "enquiry_converted", &id.to_string());
    tx.commit().map_err(|e| e.to_string())?;
    Ok(client)
}

pub fn enquiry_list(conn: &Connection) -> Result<Vec<Value>, String> {
    let today = today(conn);
    let mut stmt = conn
        .prepare(
            "SELECT id, received_date, name, contact, source, brief, status, follow_up, notes, client_id FROM enquiries
             ORDER BY (status IN ('won','lost')), follow_up = '', follow_up, received_date DESC, id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| {
            let status: String = r.get(6)?;
            let follow: String = r.get(7)?;
            let due = !follow.is_empty() && follow <= today && status != "won" && status != "lost";
            Ok(json!({
                "id": r.get::<_, i64>(0)?, "received_date": r.get::<_, String>(1)?, "name": r.get::<_, String>(2)?,
                "contact": r.get::<_, String>(3)?, "source": r.get::<_, String>(4)?, "brief": r.get::<_, String>(5)?,
                "status": status, "follow_up": follow, "notes": r.get::<_, String>(8)?, "client_id": r.get::<_, Option<i64>>(9)?,
                "follow_up_due": due,
            }))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

pub fn site_record_save(state: HostState<'_>, input: SiteInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    site_save(&conn, uid, &input)
}

pub fn site_record_list(state: HostState<'_>, project_id: Option<i64>, kind: Option<String>) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    site_list(&conn, project_id, kind.as_deref())
}

pub fn permit_save_record(state: HostState<'_>, input: PermitInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    permit_save(&conn, uid, &input)
}

pub fn permit_list_records(state: HostState<'_>, project_id: Option<i64>) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    permit_list(&conn, project_id)
}

pub fn enquiry_save_record(state: HostState<'_>, input: EnquiryInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    enquiry_save(&conn, uid, &input)
}

pub fn enquiry_list_records(state: HostState<'_>) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    enquiry_list(&conn)
}

pub fn enquiry_convert_to_client(state: HostState<'_>, id: i64) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    enquiry_convert(&conn, uid, id)
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

    fn snag(status: &str, due: &str) -> SiteInput {
        SiteInput {
            id: None, project_id: 1, kind: "snag".into(), record_date: "2026-01-10".into(), title: "Crack in lintel".into(),
            details: String::new(), location: "Ground floor".into(), assigned_to: "Contractor".into(), due_date: due.into(), status: status.into(),
        }
    }

    #[test]
    fn a_snag_is_overdue_until_it_is_resolved() {
        let c = conn();
        let id = site_save(&c, 1, &snag("open", "2026-01-20")).unwrap();
        assert_eq!(site_list(&c, Some(1), Some("snag")).unwrap()[0]["overdue"], true);
        let mut done = snag("resolved", "2026-01-20");
        done.id = Some(id);
        site_save(&c, 1, &done).unwrap();
        let row = &site_list(&c, None, None).unwrap()[0];
        assert_eq!(row["overdue"], false);
        assert_ne!(row["resolved_date"], "");
        assert!(site_save(&c, 1, &snag("open", "2026-01-01")).is_err());
        assert!(site_save(&c, 1, &SiteInput { kind: "other".into(), ..snag("open", "") }).is_err());
        assert!(site_save(&c, 1, &SiteInput { project_id: 99, ..snag("open", "") }).is_err());
    }

    fn permit(status: &str, applied: &str, decision: &str, valid: &str) -> PermitInput {
        PermitInput {
            id: None, project_id: 1, authority: "Municipal corporation".into(), kind: "Building permission".into(), reference: "BP/12".into(),
            status: status.into(), applied_date: applied.into(), decision_date: decision.into(), valid_until: valid.into(), notes: String::new(),
        }
    }

    #[test]
    fn permits_need_dates_for_their_status_and_flag_lapsed_approvals() {
        let c = conn();
        assert!(permit_save(&c, 1, &permit("applied", "", "", "")).is_err());
        assert!(permit_save(&c, 1, &permit("approved", "2026-01-01", "", "")).is_err());
        assert!(permit_save(&c, 1, &permit("approved", "2026-02-01", "2026-01-01", "")).is_err());
        permit_save(&c, 1, &permit("approved", "2020-01-01", "2020-02-01", "2021-02-01")).unwrap();
        permit_save(&c, 1, &permit("planned", "", "", "")).unwrap();
        let rows = permit_list(&c, Some(1)).unwrap();
        assert_eq!(rows.iter().filter(|r| r["watch"] == "lapsed").count(), 1);
    }

    #[test]
    fn an_enquiry_becomes_a_client_once() {
        let c = conn();
        let id = enquiry_save(
            &c, 1,
            &EnquiryInput {
                id: None, received_date: "2026-03-01".into(), name: "Rao family".into(), contact: "rao@example.com".into(), source: "Referral".into(),
                brief: "Farmhouse".into(), status: "meeting".into(), follow_up: "2026-03-05".into(), notes: String::new(),
            },
        )
        .unwrap();
        assert_eq!(enquiry_list(&c).unwrap()[0]["follow_up_due"], true);
        let client = enquiry_convert(&c, 1, id).unwrap();
        assert_eq!(enquiry_convert(&c, 1, id).unwrap(), client);
        let row = &enquiry_list(&c).unwrap()[0];
        assert_eq!(row["status"], "won");
        assert_eq!(row["follow_up_due"], false);
        let n: i64 = c.query_row("SELECT COUNT(*) FROM clients WHERE name = 'Rao family'", [], |r| r.get(0)).unwrap();
        assert_eq!(n, 1);
    }
}
