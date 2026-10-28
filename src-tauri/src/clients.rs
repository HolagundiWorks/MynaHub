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
    let creating = project.id.is_none();
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let id = save_project(&conn, &project)?;
    db::audit(&conn, Some(uid), if creating { "project_created" } else { "project_updated" }, &format!("project {id}"));
    // New projects get a suite folder: draft + form + shilpi, linked by osas.project.json.
    if creating {
        if let Ok(guard) = state.company.lock() {
            if let Some(active) = guard.as_ref() {
                let company_path = std::path::PathBuf::from(&active.summary.path);
                let _ = office_core::project_workspace::scaffold(
                    &conn,
                    office_core::project_workspace::ScaffoldInput {
                        company_path: &company_path,
                        project_id: id,
                        code: &project.code,
                        name: &project.name,
                        floors: project.floors,
                    },
                );
            }
        }
    }
    Ok(id)
}

pub fn project_workspace_get(state: HostState<'_>, project_id: i64) -> Result<Option<office_core::project_workspace::WorkspaceInfo>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    office_core::project_workspace::get(&conn, project_id)
}

pub fn project_workspace_ensure(state: HostState<'_>, project_id: i64) -> Result<office_core::project_workspace::WorkspaceInfo, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let (code, name, floors): (String, String, i64) = conn
        .query_row(
            "SELECT code, name, floors FROM projects WHERE id=?1",
            rusqlite::params![project_id],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .map_err(|_| "Project not found.".to_string())?;
    let company_path = {
        let guard = state.company.lock().map_err(|_| INTERNAL)?;
        let active = guard.as_ref().ok_or("Open a company file first.")?;
        std::path::PathBuf::from(&active.summary.path)
    };
    let info = office_core::project_workspace::scaffold(
        &conn,
        office_core::project_workspace::ScaffoldInput {
            company_path: &company_path,
            project_id,
            code: &code,
            name: &name,
            floors,
        },
    )?;
    db::audit(&conn, Some(uid), "project_workspace_ensured", &format!("project {project_id}"));
    Ok(info)
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
