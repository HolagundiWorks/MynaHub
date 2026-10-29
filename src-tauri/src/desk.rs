//! Fee proposals, meetings, attendance, solo tasks / subcontracts, and the 26AS check.
//! The records are in Office Core; this file adds the session and permission checks.
//! Proposals, meetings, attendance, tasks and subcontracts need View to read and Edit to change.
//! The 26AS entries are tax records, so they need Finance.

use crate::auth::Permission;
use crate::commands::{HostState, require, INTERNAL};
use office_core::{practice, subcontracts, tasks, tds26as};
use serde_json::Value;

pub fn proposal_save_record(state: HostState<'_>, input: practice::ProposalInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    practice::proposal_save(&conn, uid, &input)
}

pub fn proposal_list_records(state: HostState<'_>, client_id: Option<i64>) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    practice::proposal_list(&conn, client_id)
}

/// O-CoreOp estimate totals saved on the company-file project (for fee proposals and invoices).
pub fn project_estimate_summary(state: HostState<'_>, project_id: i64) -> Result<Value, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    // A project with a ShilpiDB file takes its estimate from that file.
    if let Some(from_file) = office_core::project_shilpi::estimate_summary(&conn, project_id) {
        return Ok(from_file);
    }
    office_core::share::estimate_summary(&conn, project_id)
}

/// O-CoreOp running-bill (RA) totals saved on the company-file project.
pub fn project_ra_summary(state: HostState<'_>, project_id: i64) -> Result<Value, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    office_core::share::ra_summary(&conn, project_id)
}

pub fn proposal_set_status_record(state: HostState<'_>, id: i64, status: String) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    practice::proposal_set_status(&conn, uid, id, &status)
}

pub fn meeting_save_record(state: HostState<'_>, input: practice::MeetingInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    practice::meeting_save(&conn, uid, &input)
}

pub fn meeting_list_records(state: HostState<'_>, project_id: Option<i64>) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    practice::meeting_list(&conn, project_id)
}

pub fn attendance_mark_record(state: HostState<'_>, staff_id: i64, day: String, status: String, note: Option<String>) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    practice::attendance_mark(&conn, uid, staff_id, &day, &status, note.as_deref().unwrap_or(""))
}

pub fn attendance_day_records(state: HostState<'_>, day: String) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    practice::attendance_day(&conn, &day)
}

pub fn attendance_month_records(state: HostState<'_>, month: String) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    practice::attendance_month(&conn, &month)
}

pub fn tds26as_import_csv(state: HostState<'_>, csv: String) -> Result<Value, String> {
    let uid = require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    tds26as::import_csv(&conn, uid, &csv)
}

pub fn tds26as_reconcile_report(state: HostState<'_>, from: String, to: String) -> Result<Value, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    tds26as::reconcile(&conn, &from, &to)
}

pub fn tds26as_list_entries(state: HostState<'_>) -> Result<Vec<Value>, String> {
    require(&state, Permission::Finance)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    tds26as::list(&conn)
}

pub fn task_list_records(state: HostState<'_>, project_id: Option<i64>) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let rows = tasks::list(&conn, project_id)?;
    Ok(office_core::project_shilpi::merge_tasks(&conn, project_id, rows))
}

pub fn task_save_record(state: HostState<'_>, input: tasks::TaskInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    tasks::save(&conn, uid, &input)
}

pub fn task_set_status_record(state: HostState<'_>, id: i64, status: String, position: Option<i64>) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    tasks::set_status(&conn, uid, id, &status, position)
}

pub fn task_delete_record(state: HostState<'_>, id: i64) -> Result<(), String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    tasks::delete(&conn, uid, id)
}

pub fn subcontract_list_records(state: HostState<'_>, project_id: Option<i64>) -> Result<Vec<Value>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    subcontracts::list(&conn, project_id)
}

pub fn subcontract_save_record(state: HostState<'_>, input: subcontracts::SubcontractInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    subcontracts::save(&conn, uid, &input)
}
