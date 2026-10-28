//! Studio bridge for CoreOp draft documents (`aqc.estimate.draft` / `aqc.ra.draft.*`).

use crate::commands::{require, HostState, INTERNAL};
use office_core::auth::Permission;
use office_core::studio_drafts::{self, Draft, DraftInfo};

pub fn studio_draft_list(
    state: HostState<'_>,
    project_id: Option<i64>,
    state_filter: Option<String>,
) -> Result<Vec<DraftInfo>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    studio_drafts::list(&conn, project_id, state_filter.as_deref())
}

pub fn studio_draft_get(state: HostState<'_>, project_id: i64, kind: String) -> Result<Option<Draft>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    studio_drafts::get(&conn, project_id, &kind)
}

pub fn studio_draft_approve(
    state: HostState<'_>,
    project_id: i64,
    kind: String,
    expected_revision: i64,
) -> Result<Draft, String> {
    let uid = require(&state, Permission::Issue)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    studio_drafts::approve(&conn, uid, project_id, &kind, expected_revision)
}

pub fn studio_draft_mark_pending(
    state: HostState<'_>,
    project_id: i64,
    kind: String,
    expected_revision: i64,
) -> Result<Draft, String> {
    let uid = require(&state, Permission::Issue)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    studio_drafts::mark_pending(&conn, uid, project_id, &kind, expected_revision)
}

pub fn studio_draft_mark_superseded(
    state: HostState<'_>,
    project_id: i64,
    kind: String,
    expected_revision: i64,
) -> Result<Draft, String> {
    let uid = require(&state, Permission::Issue)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    studio_drafts::mark_superseded(&conn, uid, project_id, &kind, expected_revision)
}
