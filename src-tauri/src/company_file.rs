//! The company file as this app uses it. The file format, locking, protection and open and create logic are in Office Core
//! (`office_core::company`); this file adds the commands the window calls.

use crate::commands::{HostState, INTERNAL};
use serde::Serialize;
use std::path::Path;

pub use office_core::company::*;

#[derive(Serialize)]
pub struct LegacyInfo {
    pub path: String,
}

#[derive(Serialize)]
pub struct Protection {
    pub protected: bool,
}

pub fn company_active(state: HostState<'_>) -> Result<Option<CompanySummary>, String> {
    Ok(active_summary(&state))
}

pub fn company_validate(path: String) -> Result<FileCheck, String> {
    validate(Path::new(&path))
}

pub fn company_recent_list(state: HostState<'_>) -> Result<Vec<RecentRow>, String> {
    Ok(load_recent(&state.app_dir).into_iter().map(|r| RecentRow { exists: Path::new(&r.path).is_file(), recent: r }).collect())
}

pub fn company_recent_remove(state: HostState<'_>, uuid: String) -> Result<(), String> {
    let mut list = load_recent(&state.app_dir);
    list.retain(|r| r.uuid != uuid);
    save_recent(&state.app_dir, &list);
    Ok(())
}

pub fn company_open(state: HostState<'_>, path: String, secret: String, unlock: String) -> Result<CompanySummary, String> {
    open_company(&state, &path, &secret, &unlock)
}

/// Creates a company file and its first owner, then opens it. Returns the one-time recovery code.
pub fn company_create(state: HostState<'_>, path: String, name: String, username: String, password: String, confirm: String) -> Result<(CompanySummary, String), String> {
    create_company(&state, &path, &name, &username, &password, &confirm)
}

/// Reports data left by the first release so the user can convert it into a company file.
pub fn company_legacy_info(state: HostState<'_>) -> Result<Option<LegacyInfo>, String> {
    Ok(legacy_paths(&state.app_dir).map(|(db, _)| LegacyInfo { path: path_str(&db) }))
}

pub fn company_convert_legacy(state: HostState<'_>, path: String) -> Result<CompanySummary, String> {
    ensure_closed(&state)?;
    let (db, files) = legacy_paths(&state.app_dir).ok_or_else(|| fail("No earlier data was found."))?;
    let file = convert_legacy(&db, &files, Path::new(&path), state.prepare)?;
    let (conn, summary) = open_file(&file, state.prepare)?;
    install(&state, conn, summary.clone(), None, true)?;
    remember(&state.app_dir, &summary);
    Ok(summary)
}

pub fn company_close(state: HostState<'_>) -> Result<(), String> {
    close(&state)
}

/// Encrypts the open company file. Returns a new recovery code, shown once. The password that confirmed this action opens the file.
pub fn company_protect(state: HostState<'_>, password: String) -> Result<String, String> {
    let uid = crate::commands::require(&state, crate::auth::Permission::ManageSettings)?;
    confirm_owner(&state, &password)?;
    protect_open(&state, uid, &password)
}

pub fn company_protection(state: HostState<'_>) -> Result<Protection, String> {
    crate::commands::require(&state, crate::auth::Permission::View)?;
    let protected = state.company.lock().map_err(|_| INTERNAL)?.as_ref().map(|c| c.seal.is_some()).unwrap_or(false);
    Ok(Protection { protected })
}

/// Issues a new recovery code for the signed-in owner. On a protected file it also wraps the file key.
pub fn company_rotate_recovery(state: HostState<'_>, password: String) -> Result<String, String> {
    let uid = crate::commands::require(&state, crate::auth::Permission::ManageSettings)?;
    confirm_owner(&state, &password)?;
    rotate_recovery(&state, uid, &password)
}
