//! Staff roster, leave, and the office library. The records live in Office Core; this file adds the session and permission checks.

use crate::auth::Permission;
use crate::commands::{HostState, require, INTERNAL};
use serde_json::Value;

pub use office_core::office::*;

pub fn staff_save_record(state: HostState<'_>, input: StaffInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    staff_save(&conn, uid, &input)
}

pub fn staff_list_records(state: HostState<'_>) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    staff_list(&conn)
}

pub fn leave_save_record(state: HostState<'_>, input: LeaveInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    leave_save(&conn, uid, &input)
}

pub fn leave_list_records(state: HostState<'_>) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    leave_list(&conn)
}

pub fn library_save_record(state: HostState<'_>, input: LibraryInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    library_save(&conn, uid, &input)
}

pub fn library_list_records(state: HostState<'_>, kind: Option<String>, search: Option<String>) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    library_list(&conn, kind.as_deref(), search.as_deref().unwrap_or(""))
}
