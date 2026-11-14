//! Cloud sync operations for the WinUI host: change list, acknowledgements and pulled records (office-core `sync`).
//! No network here; Adms.Cloud talks to Neon. Linking a company needs ManageSettings (the owner); the daily calls need Edit.

use crate::auth::Permission;
use crate::commands::{require, HostState, INTERNAL};
use office_core::sync;
use serde_json::Value;

pub fn sync_status(state: HostState<'_>) -> Result<Value, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    sync::status(&conn)
}

pub fn sync_enable(state: HostState<'_>, cloud_company: String) -> Result<Value, String> {
    let uid = require(&state, Permission::ManageSettings)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    sync::enable(&conn, &cloud_company)?;
    office_core::audit(&conn, Some(uid), "cloud_sync_enabled", "");
    sync::status(&conn)
}

pub fn sync_disable(state: HostState<'_>) -> Result<Value, String> {
    let uid = require(&state, Permission::ManageSettings)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    sync::disable(&conn)?;
    office_core::audit(&conn, Some(uid), "cloud_sync_disabled", "");
    sync::status(&conn)
}

pub fn sync_collect(state: HostState<'_>, limit: Option<usize>) -> Result<Vec<Value>, String> {
    require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    sync::collect_push(&conn, limit.unwrap_or(200).clamp(1, 500))
}

pub fn sync_ack(state: HostState<'_>, items: Vec<sync::Ack>) -> Result<Value, String> {
    require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    sync::ack(&conn, &items)
}

pub fn sync_apply(state: HostState<'_>, from_seq: i64, records: Vec<sync::Remote>) -> Result<Value, String> {
    require(&state, Permission::Edit)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    sync::apply_pull(&mut conn, from_seq, records)
}
