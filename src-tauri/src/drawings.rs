//! Drawings (ShilpiDB): vector drawings kept in the company file, linked to projects. The engine is `office_core::drawings`; this file
//! adds the session and permission checks.

use crate::auth::Permission;
use crate::commands::{HostState, require, INTERNAL};
use serde_json::Value;

pub fn drawing_import(state: HostState<'_>, project_id: i64, name: String, path: String) -> Result<Value, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    office_core::drawings::import_dxf(&conn, uid, project_id, &name, &path)
}

pub fn drawing_list(state: HostState<'_>, project_id: Option<i64>) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    office_core::drawings::list(&conn, project_id)
}

pub fn drawing_view(state: HostState<'_>, id: i64, window: Option<[f64; 4]>) -> Result<Value, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    office_core::drawings::view(&conn, id, window)
}

pub fn drawing_at(state: HostState<'_>, id: i64, x: f64, y: f64, tolerance: f64) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    office_core::drawings::at_point(&conn, id, x, y, tolerance)
}

pub fn drawing_delete(state: HostState<'_>, id: i64) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    office_core::drawings::delete(&conn, uid, id)
}
