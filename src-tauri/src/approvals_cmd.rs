//! Studio bridge for suite approvals (CoreOp share → Studio approve/lock/revise).

use crate::commands::{require, AppState, HostState, INTERNAL};
use office_core::approvals::{self, Approval, ApprovalInfo};
use office_core::auth::Permission;

pub fn approval_list(
    state: HostState<'_>,
    project_id: Option<i64>,
    kind: Option<String>,
    status: Option<String>,
) -> Result<Vec<ApprovalInfo>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    approvals::list(&conn, project_id, kind.as_deref(), status.as_deref())
}

pub fn approval_get(state: HostState<'_>, id: i64) -> Result<Option<Approval>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    approvals::get(&conn, id)
}

pub fn approval_submit(
    state: HostState<'_>,
    project_id: i64,
    kind: String,
    title: String,
    body: String,
    supersedes_id: Option<i64>,
) -> Result<Approval, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    approvals::submit(&conn, uid, project_id, &kind, &title, &body, supersedes_id)
}

pub fn approval_approve(state: HostState<'_>, id: i64) -> Result<Approval, String> {
    let uid = require(&state, Permission::Issue)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    approvals::approve(&conn, uid, id)
}

pub fn approval_withdraw(state: HostState<'_>, id: i64) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    approvals::withdraw(&conn, uid, id)
}

pub fn agent_tools(state: HostState<'_>) -> Result<Vec<serde_json::Value>, String> {
    require(&state, Permission::View)?;
    Ok(office_agent::TOOLS
        .iter()
        .map(|t| serde_json::json!({ "name": t.name, "description": t.description, "input_schema": (t.input_schema)() }))
        .collect())
}

pub fn agent_call_state(state: &AppState, name: &str, arguments: &serde_json::Value) -> Result<serde_json::Value, String> {
    office_agent::call(state, name, arguments)
}
