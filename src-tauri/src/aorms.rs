//! The optional connection to AORMS, the studio's web portal. Signed out, the office app makes no network call and works as it
//! always has. Signed in, a project can be pulled from the portal into the office file: its client and its title and site address.
//!
//! Portal data reaches the file only on an explicit pull, and only as a client and a project; nothing local is deleted or
//! replaced beyond the project's name and site address. The sign-in is the suite's shared one (see the `aorms-connect` crate).

use crate::auth::Permission;
use crate::commands::{HostState, require, INTERNAL};
use crate::db;
use aorms_connect::store::TokenStore;
use aorms_connect::{Connect, ConnectError, ProjectSeed, Restored};
use rusqlite::{params, Connection};
use serde::Serialize;
use serde_json::{json, Value};
use std::sync::{Arc, LazyLock, Mutex};

/// This app's name in the suite. Its portal session is kept apart from the other apps', so signing in here does not end theirs.
const APP: &str = "aos";

static CLIENT: LazyLock<Mutex<Option<(String, Connect)>>> = LazyLock::new(|| Mutex::new(None));

fn token_store() -> Arc<dyn TokenStore> {
    #[cfg(windows)]
    {
        Arc::new(aorms_connect::store::WindowsStore::new())
    }
    #[cfg(not(windows))]
    {
        Arc::new(aorms_connect::store::MemoryStore::new())
    }
}

fn address(requested: Option<&str>) -> String {
    requested
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
        .or_else(|| std::env::var("AORMS_URL").ok().filter(|s| !s.trim().is_empty()))
        .unwrap_or_else(|| aorms_connect::DEFAULT_BASE.to_string())
}

fn machine() -> String {
    std::env::var("COMPUTERNAME").unwrap_or_else(|_| "this computer".into())
}

/// Runs `f` with the portal client for `base`, making it the first time. A different address starts a new client.
fn with_client<T>(base: &str, f: impl FnOnce(&mut Connect) -> Result<T, ConnectError>) -> Result<T, String> {
    let mut slot = CLIENT.lock().map_err(|_| INTERNAL.to_string())?;
    let stale = slot.as_ref().is_none_or(|(have, _)| have != base);
    if stale {
        let label = format!("O-Studio {} · {}", env!("CARGO_PKG_VERSION"), machine());
        *slot = Some((base.to_string(), Connect::new(base, APP, &label, token_store()).map_err(|e| e.to_string())?));
    }
    let (_, client) = slot.as_mut().ok_or(INTERNAL)?;
    f(client).map_err(|e| e.to_string())
}

#[derive(Serialize, Debug)]
pub struct AormsState {
    /// `signed_out`, `needs_connect` (signed in through another suite app) or `connected`.
    pub state: &'static str,
    pub email: String,
    pub studio: String,
    pub role: String,
    pub plan: String,
}

fn state_of(restored: Restored) -> AormsState {
    match restored {
        Restored::SignedOut => AormsState { state: "signed_out", email: String::new(), studio: String::new(), role: String::new(), plan: String::new() },
        Restored::NeedsConnect { email } => AormsState { state: "needs_connect", email, studio: String::new(), role: String::new(), plan: String::new() },
        Restored::Connected(a) => AormsState { state: "connected", email: a.email, studio: a.studio, role: a.role, plan: a.plan },
    }
}

fn connected(account: aorms_connect::Account) -> AormsState {
    state_of(Restored::Connected(account))
}

pub fn restore(base: Option<&str>) -> Result<AormsState, String> {
    with_client(&address(base), |c| c.restore()).map(state_of)
}

pub fn sign_in(base: Option<&str>, email: &str, password: &str) -> Result<AormsState, String> {
    with_client(&address(base), |c| c.sign_in(email, password)).map(connected)
}

pub fn connect(base: Option<&str>) -> Result<AormsState, String> {
    with_client(&address(base), |c| c.connect()).map(connected)
}

pub fn sign_out(base: Option<&str>) -> Result<(), String> {
    with_client(&address(base), |c| c.sign_out())
}

/// The studio's projects on the portal, each marked with the local project it was pulled into, if any.
pub fn portal_projects(conn: &Connection, base: Option<&str>) -> Result<Value, String> {
    let projects = with_client(&address(base), |c| c.projects())?;
    let mut rows = Vec::new();
    for p in projects {
        let local: Option<i64> = conn
            .query_row("SELECT project_id FROM aorms_links WHERE project_office_id = ?1", params![p.project_office_id], |r| r.get(0))
            .ok();
        rows.push(json!({
            "project_office_id": p.project_office_id, "reference": p.reference, "title": p.title, "status": p.status, "city": p.city, "local_project_id": local,
        }));
    }
    Ok(Value::Array(rows))
}

pub fn pull(conn: &Connection, user_id: i64, base: Option<&str>, project_office_id: &str) -> Result<Value, String> {
    let seed = with_client(&address(base), |c| c.seed(project_office_id))?;
    apply_seed(conn, user_id, &seed)
}

fn clip(text: &str, max: usize) -> String {
    text.trim().chars().take(max).collect()
}

/// Puts a portal project into the office file: finds or adds its client by name, then adds the project, or updates the one an earlier
/// pull made. Only the project's name and site address follow the portal on a later pull.
pub fn apply_seed(conn: &Connection, user_id: i64, seed: &ProjectSeed) -> Result<Value, String> {
    let name = clip(&seed.name, 200);
    if name.is_empty() {
        return Err("The portal project has no title, so it cannot be pulled.".into());
    }
    let site = clip(&seed.location, 500);
    let existing: Option<i64> = conn
        .query_row("SELECT project_id FROM aorms_links WHERE project_office_id = ?1", params![seed.project_office_id], |r| r.get(0))
        .ok();
    let tx = conn.unchecked_transaction().map_err(|e| e.to_string())?;
    let result = if let Some(project_id) = existing {
        let n = tx
            .execute("UPDATE projects SET name = ?2, site_address = ?3, updated_at = datetime('now') WHERE id = ?1", params![project_id, name, site])
            .map_err(|e| e.to_string())?;
        if n == 0 {
            return Err("The project this was pulled into no longer exists.".into());
        }
        tx.execute("UPDATE aorms_links SET pulled_at = datetime('now') WHERE project_id = ?1", params![project_id]).map_err(|e| e.to_string())?;
        db::audit(&tx, Some(user_id), "aorms_project_refreshed", &format!("project {project_id}"));
        json!({ "project_id": project_id, "created": false })
    } else {
        let client_name = clip(&seed.client_name, 200);
        let client_id: i64 = if client_name.is_empty() {
            // a portal project with no client is filed under a placeholder client the user can rename
            find_or_add_client(&tx, "Client not named on AORMS", user_id)?
        } else {
            find_or_add_client(&tx, &client_name, user_id)?
        };
        let project = crate::clients::Project {
            id: None, client_id, code: String::new(), name, site_address: site, status: "active".into(), consultancy_type: "comprehensive".into(), floors: 1,
            phase: String::new(), archived: false, client_name: String::new(),
        };
        let project_id = crate::clients::save_project(&tx, &project)?;
        tx.execute("INSERT INTO aorms_links (project_id, project_office_id) VALUES (?1, ?2)", params![project_id, seed.project_office_id]).map_err(|e| e.to_string())?;
        db::audit(&tx, Some(user_id), "aorms_project_pulled", &format!("project {project_id}"));
        json!({ "project_id": project_id, "client_id": client_id, "created": true })
    };
    tx.commit().map_err(|e| e.to_string())?;
    Ok(result)
}

fn find_or_add_client(conn: &Connection, name: &str, user_id: i64) -> Result<i64, String> {
    if let Ok(id) = conn.query_row("SELECT id FROM clients WHERE name = ?1 COLLATE NOCASE", params![name], |r| r.get::<_, i64>(0)) {
        return Ok(id);
    }
    let client = crate::clients::Client {
        id: None, name: name.into(), contact_person: String::new(), email: String::new(), phone: String::new(), address: String::new(), pan: String::new(), gstin: String::new(),
        billing_address: String::new(), communication_preference: String::new(), kind: "individual".into(), city: String::new(), state: String::new(), legal_name: String::new(),
        tan: String::new(), gst_registration: String::new(), representatives: Vec::new(), archived: false,
    };
    let id = crate::clients::save_client(conn, &client)?;
    db::audit(conn, Some(user_id), "client_created", &format!("client {id} from AORMS"));
    Ok(id)
}

#[tauri::command]
pub fn aorms_restore(state: HostState<'_>, base_url: Option<String>) -> Result<AormsState, String> {
    require(&state, Permission::View)?;
    restore(base_url.as_deref())
}

#[tauri::command]
pub fn aorms_sign_in(state: HostState<'_>, email: String, password: String, base_url: Option<String>) -> Result<AormsState, String> {
    require(&state, Permission::Edit)?;
    sign_in(base_url.as_deref(), &email, &password)
}

#[tauri::command]
pub fn aorms_connect(state: HostState<'_>, base_url: Option<String>) -> Result<AormsState, String> {
    require(&state, Permission::Edit)?;
    connect(base_url.as_deref())
}

#[tauri::command]
pub fn aorms_sign_out(state: HostState<'_>, base_url: Option<String>) -> Result<(), String> {
    require(&state, Permission::View)?;
    sign_out(base_url.as_deref())
}

#[tauri::command]
pub fn aorms_projects(state: HostState<'_>, base_url: Option<String>) -> Result<Value, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    portal_projects(&conn, base_url.as_deref())
}

#[tauri::command]
pub fn aorms_pull(state: HostState<'_>, project_office_id: String, base_url: Option<String>) -> Result<Value, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    pull(&conn, uid, base_url.as_deref(), &project_office_id)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('ada','x','owner')", []).unwrap();
        c
    }

    fn seed(name: &str, client: &str) -> ProjectSeed {
        ProjectSeed {
            project_office_id: "11111111-1111-1111-1111-111111111111".into(), name: name.into(), location: "Plot 4, Baner".into(), client_name: client.into(),
            ..Default::default()
        }
    }

    #[test]
    fn a_pulled_project_adds_its_client_once_and_a_later_pull_updates_it() {
        let c = conn();
        let first = apply_seed(&c, 1, &seed("Villa", "Rao family")).unwrap();
        assert_eq!(first["created"], true);
        let again = apply_seed(&c, 1, &seed("Villa renamed", "Rao family")).unwrap();
        assert_eq!((again["created"].clone(), again["project_id"].clone()), (json!(false), first["project_id"].clone()));
        let (name, site, clients): (String, String, i64) = c
            .query_row("SELECT p.name, p.site_address, (SELECT COUNT(*) FROM clients) FROM projects p", [], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
            .unwrap();
        assert_eq!((name.as_str(), site.as_str(), clients), ("Villa renamed", "Plot 4, Baner", 1));
        // a second portal project for the same client does not duplicate the client, whatever the letter case
        let mut other = seed("Farmhouse", "RAO FAMILY");
        other.project_office_id = "22222222-2222-2222-2222-222222222222".into();
        apply_seed(&c, 1, &other).unwrap();
        assert_eq!(c.query_row("SELECT COUNT(*) FROM clients", [], |r| r.get::<_, i64>(0)).unwrap(), 1);
        assert_eq!(c.query_row("SELECT COUNT(*) FROM aorms_links", [], |r| r.get::<_, i64>(0)).unwrap(), 2);
    }

    #[test]
    fn nothing_local_is_lost_and_a_bad_portal_record_is_refused() {
        let c = conn();
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name, code) VALUES (1, 'Local job', 'L-1')", []).unwrap();
        apply_seed(&c, 1, &seed("Villa", "")).unwrap();
        assert_eq!(c.query_row("SELECT COUNT(*) FROM projects WHERE name = 'Local job' AND code = 'L-1'", [], |r| r.get::<_, i64>(0)).unwrap(), 1);
        assert!(apply_seed(&c, 1, &seed("   ", "Rao")).unwrap_err().contains("no title"));
        let long = "x".repeat(5000);
        apply_seed(&c, 1, &ProjectSeed { project_office_id: "33333333-3333-3333-3333-333333333333".into(), name: long.clone(), location: long.clone(), client_name: long, ..Default::default() }).unwrap();
        assert!(c.query_row("SELECT MAX(LENGTH(name)) FROM projects", [], |r| r.get::<_, i64>(0)).unwrap() <= 200, "an oversized portal value is cut, not stored whole");
    }

    #[test]
    fn a_portal_address_that_is_not_https_is_refused_before_any_network_call() {
        assert!(restore(Some("http://example.com")).unwrap_err().contains("https"));
        assert!(sign_in(Some("ftp://aorms.in"), "a@b.in", "pw").is_err());
    }
}
