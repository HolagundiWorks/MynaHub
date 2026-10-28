use crate::auth::Permission;
use crate::commands::{HostState, require, INTERNAL};
use crate::db;
use serde_json::Value;

pub use office_core::clients::*;

pub fn client_list(state: HostState<'_>, include_archived: bool) -> Result<Vec<Client>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list_clients(&conn, include_archived)
}

pub fn client_save(state: HostState<'_>, client: Client) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let id = save_client(&conn, &client)?;
    db::audit(&conn, Some(uid), if client.id.is_some() { "client_updated" } else { "client_created" }, &format!("client {id}"));
    Ok(id)
}

pub fn client_set_archived(state: HostState<'_>, id: i64, archived: bool) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    set_client_archived(&conn, id, archived)?;
    db::audit(&conn, Some(uid), if archived { "client_archived" } else { "client_restored" }, &format!("client {id}"));
    Ok(())
}

pub fn project_list(state: HostState<'_>, client_id: Option<i64>, include_archived: bool) -> Result<Vec<Project>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list_projects(&conn, client_id, include_archived)
}

pub fn project_save(state: HostState<'_>, project: Project) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let id = save_project(&conn, &project)?;
    db::audit(&conn, Some(uid), if project.id.is_some() { "project_updated" } else { "project_created" }, &format!("project {id}"));
    Ok(id)
}

pub fn project_set_archived(state: HostState<'_>, id: i64, archived: bool) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    set_project_archived(&conn, id, archived)?;
    db::audit(&conn, Some(uid), if archived { "project_archived" } else { "project_restored" }, &format!("project {id}"));
    Ok(())
}

pub fn project_brief_get(state: HostState<'_>, project_id: i64) -> Result<Value, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    get_brief(&conn, project_id)
}

pub fn project_brief_save(state: HostState<'_>, project_id: i64, brief: Value) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    save_brief(&conn, project_id, &brief)?;
    db::audit(&conn, Some(uid), "project_brief_saved", &format!("project {project_id}"));
    Ok(())
}
