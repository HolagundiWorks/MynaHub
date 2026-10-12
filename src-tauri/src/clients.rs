use crate::auth::Permission;
use crate::commands::{require, AppState, INTERNAL};
use crate::db;
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};
use tauri::State;

#[derive(Serialize, Deserialize)]
pub struct Client {
    pub id: Option<i64>,
    pub name: String,
    pub contact_person: String,
    pub email: String,
    pub phone: String,
    pub address: String,
    pub pan: String,
    pub gstin: String,
    #[serde(default)]
    pub archived: bool,
}

#[derive(Serialize, Deserialize)]
pub struct Project {
    pub id: Option<i64>,
    pub client_id: i64,
    pub code: String,
    pub name: String,
    pub site_address: String,
    pub status: String,
    #[serde(default)]
    pub archived: bool,
    #[serde(default)]
    pub client_name: String,
}

fn nonempty(s: &str, label: &str) -> Result<String, String> {
    let t = s.trim();
    if t.is_empty() {
        return Err(format!("{label} is required."));
    }
    if t.chars().count() > 200 {
        return Err(format!("{label} is too long."));
    }
    Ok(t.to_string())
}

fn map_unique(e: rusqlite::Error, what: &str) -> String {
    match e {
        rusqlite::Error::SqliteFailure(f, _) if f.code == rusqlite::ErrorCode::ConstraintViolation => {
            format!("{what} already exists.")
        }
        other => other.to_string(),
    }
}

pub fn list_clients(conn: &Connection, include_archived: bool) -> Result<Vec<Client>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT id, name, contact_person, email, phone, address, pan, gstin, archived FROM clients
             WHERE archived = 0 OR ?1 ORDER BY name",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![include_archived], |r| {
            Ok(Client {
                id: Some(r.get(0)?),
                name: r.get(1)?,
                contact_person: r.get(2)?,
                email: r.get(3)?,
                phone: r.get(4)?,
                address: r.get(5)?,
                pan: r.get(6)?,
                gstin: r.get(7)?,
                archived: r.get::<_, i64>(8)? != 0,
            })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn save_client(conn: &Connection, c: &Client) -> Result<i64, String> {
    let name = nonempty(&c.name, "Client name")?;
    match c.id {
        Some(id) => {
            let n = conn
                .execute(
                    "UPDATE clients SET name=?1, contact_person=?2, email=?3, phone=?4, address=?5, pan=?6, gstin=?7,
                     updated_at=datetime('now') WHERE id=?8",
                    params![name, c.contact_person.trim(), c.email.trim(), c.phone.trim(), c.address.trim(), c.pan.trim(), c.gstin.trim(), id],
                )
                .map_err(|e| map_unique(e, "A client with this name"))?;
            if n == 0 {
                return Err("Client not found.".into());
            }
            Ok(id)
        }
        None => {
            conn.execute(
                "INSERT INTO clients (name, contact_person, email, phone, address, pan, gstin) VALUES (?1,?2,?3,?4,?5,?6,?7)",
                params![name, c.contact_person.trim(), c.email.trim(), c.phone.trim(), c.address.trim(), c.pan.trim(), c.gstin.trim()],
            )
            .map_err(|e| map_unique(e, "A client with this name"))?;
            Ok(conn.last_insert_rowid())
        }
    }
}

pub fn set_client_archived(conn: &Connection, id: i64, archived: bool) -> Result<(), String> {
    let n = conn
        .execute("UPDATE clients SET archived=?1, updated_at=datetime('now') WHERE id=?2", params![archived, id])
        .map_err(|e| e.to_string())?;
    if n == 0 {
        return Err("Client not found.".into());
    }
    Ok(())
}

pub fn list_projects(conn: &Connection, client_id: Option<i64>, include_archived: bool) -> Result<Vec<Project>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT p.id, p.client_id, p.code, p.name, p.site_address, p.status, p.archived, c.name
             FROM projects p JOIN clients c ON c.id = p.client_id
             WHERE (p.archived = 0 OR ?1) AND (?2 IS NULL OR p.client_id = ?2)
             ORDER BY c.name, p.name",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![include_archived, client_id], |r| {
            Ok(Project {
                id: Some(r.get(0)?),
                client_id: r.get(1)?,
                code: r.get(2)?,
                name: r.get(3)?,
                site_address: r.get(4)?,
                status: r.get(5)?,
                archived: r.get::<_, i64>(6)? != 0,
                client_name: r.get(7)?,
            })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn save_project(conn: &Connection, p: &Project) -> Result<i64, String> {
    let name = nonempty(&p.name, "Project name")?;
    if !["active", "on_hold", "completed"].contains(&p.status.as_str()) {
        return Err("Invalid project status.".into());
    }
    let client_archived: Option<i64> = conn
        .query_row("SELECT archived FROM clients WHERE id=?1", params![p.client_id], |r| r.get(0))
        .ok();
    match client_archived {
        None => return Err("Client not found.".into()),
        Some(a) if a != 0 && p.id.is_none() => return Err("Cannot add a project to an archived client.".into()),
        _ => {}
    }
    match p.id {
        Some(id) => {
            let n = conn
                .execute(
                    "UPDATE projects SET client_id=?1, code=?2, name=?3, site_address=?4, status=?5, updated_at=datetime('now') WHERE id=?6",
                    params![p.client_id, p.code.trim(), name, p.site_address.trim(), p.status, id],
                )
                .map_err(|e| map_unique(e, "This client already has a project with this name; it"))?;
            if n == 0 {
                return Err("Project not found.".into());
            }
            Ok(id)
        }
        None => {
            conn.execute(
                "INSERT INTO projects (client_id, code, name, site_address, status) VALUES (?1,?2,?3,?4,?5)",
                params![p.client_id, p.code.trim(), name, p.site_address.trim(), p.status],
            )
            .map_err(|e| map_unique(e, "This client already has a project with this name; it"))?;
            Ok(conn.last_insert_rowid())
        }
    }
}

pub fn set_project_archived(conn: &Connection, id: i64, archived: bool) -> Result<(), String> {
    let n = conn
        .execute("UPDATE projects SET archived=?1, updated_at=datetime('now') WHERE id=?2", params![archived, id])
        .map_err(|e| e.to_string())?;
    if n == 0 {
        return Err("Project not found.".into());
    }
    Ok(())
}

#[tauri::command]
pub fn client_list(state: State<AppState>, include_archived: bool) -> Result<Vec<Client>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list_clients(&conn, include_archived)
}

#[tauri::command]
pub fn client_save(state: State<AppState>, client: Client) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let id = save_client(&conn, &client)?;
    db::audit(&conn, Some(uid), if client.id.is_some() { "client_updated" } else { "client_created" }, &format!("client {id}"));
    Ok(id)
}

#[tauri::command]
pub fn client_set_archived(state: State<AppState>, id: i64, archived: bool) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    set_client_archived(&conn, id, archived)?;
    db::audit(&conn, Some(uid), if archived { "client_archived" } else { "client_restored" }, &format!("client {id}"));
    Ok(())
}

#[tauri::command]
pub fn project_list(state: State<AppState>, client_id: Option<i64>, include_archived: bool) -> Result<Vec<Project>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list_projects(&conn, client_id, include_archived)
}

#[tauri::command]
pub fn project_save(state: State<AppState>, project: Project) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let id = save_project(&conn, &project)?;
    db::audit(&conn, Some(uid), if project.id.is_some() { "project_updated" } else { "project_created" }, &format!("project {id}"));
    Ok(id)
}

#[tauri::command]
pub fn project_set_archived(state: State<AppState>, id: i64, archived: bool) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    set_project_archived(&conn, id, archived)?;
    db::audit(&conn, Some(uid), if archived { "project_archived" } else { "project_restored" }, &format!("project {id}"));
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        db::migrate(&mut c).unwrap();
        c
    }

    fn client(name: &str) -> Client {
        Client { id: None, name: name.into(), contact_person: "".into(), email: "".into(), phone: "".into(), address: "".into(), pan: "".into(), gstin: "".into(), archived: false }
    }

    fn project(client_id: i64, name: &str) -> Project {
        Project { id: None, client_id, code: "".into(), name: name.into(), site_address: "".into(), status: "active".into(), archived: false, client_name: "".into() }
    }

    #[test]
    fn duplicate_client_name_rejected_case_insensitively() {
        let c = conn();
        save_client(&c, &client("Acme")).unwrap();
        assert!(save_client(&c, &client("ACME")).unwrap_err().contains("already exists"));
        assert!(save_client(&c, &client("  ")).is_err());
    }

    #[test]
    fn client_has_many_projects_without_duplicates() {
        let c = conn();
        let id = save_client(&c, &client("Acme")).unwrap();
        save_project(&c, &project(id, "Villa")).unwrap();
        save_project(&c, &project(id, "Office")).unwrap();
        assert!(save_project(&c, &project(id, "villa")).is_err());
        assert_eq!(list_projects(&c, Some(id), false).unwrap().len(), 2);
    }

    #[test]
    fn archive_hides_but_keeps_records_and_blocks_new_projects() {
        let c = conn();
        let id = save_client(&c, &client("Acme")).unwrap();
        let pid = save_project(&c, &project(id, "Villa")).unwrap();
        set_client_archived(&c, id, true).unwrap();
        assert!(list_clients(&c, false).unwrap().is_empty());
        assert_eq!(list_clients(&c, true).unwrap().len(), 1);
        assert!(save_project(&c, &project(id, "New")).is_err());
        set_project_archived(&c, pid, true).unwrap();
        assert!(list_projects(&c, None, false).unwrap().is_empty());
    }

    #[test]
    fn project_requires_valid_status_and_existing_client() {
        let c = conn();
        let mut p = project(999, "X");
        assert!(save_project(&c, &p).is_err());
        let id = save_client(&c, &client("Acme")).unwrap();
        p.client_id = id;
        p.status = "bogus".into();
        assert!(save_project(&c, &p).is_err());
    }
}
