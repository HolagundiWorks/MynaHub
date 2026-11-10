//! Bridge for the MynaOps CSV import (estimates and running bills). The rules live in `office_core::falcon_import`.

use crate::commands::{require, HostState, INTERNAL};
use office_core::auth::Permission;
use office_core::falcon_import::{self, ImportResult, MapEntry, Preview};

/// A MynaOps export is a few hundred kilobytes at most; refuse anything that is clearly not one.
const MAX_FILE: u64 = 16 * 1024 * 1024;

fn read_file(path: &str) -> Result<(Vec<u8>, String), String> {
    let p = std::path::Path::new(path);
    let len = std::fs::metadata(p).map_err(|_| "That file could not be opened.".to_string())?.len();
    if len > MAX_FILE {
        return Err("That file is too large to be a MynaOps export.".into());
    }
    let bytes = std::fs::read(p).map_err(|_| "That file could not be read.".to_string())?;
    let name = p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    Ok((bytes, name))
}

pub fn falcon_csv_preview(state: HostState<'_>, path: String, project_id: Option<i64>) -> Result<Preview, String> {
    require(&state, Permission::Edit)?;
    let (bytes, _) = read_file(&path)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    falcon_import::ensure(&mut conn)?;
    falcon_import::preview(&conn, &bytes, project_id)
}

pub fn falcon_csv_import(state: HostState<'_>, path: String, project_id: Option<i64>) -> Result<ImportResult, String> {
    let uid = require(&state, Permission::Edit)?;
    let (bytes, name) = read_file(&path)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    falcon_import::import(&mut conn, uid, &bytes, &name, project_id)
}

pub fn falcon_csv_map_list(state: HostState<'_>) -> Result<Vec<MapEntry>, String> {
    require(&state, Permission::View)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    falcon_import::ensure(&mut conn)?;
    falcon_import::map_list(&conn)
}

pub fn falcon_csv_map_set(state: HostState<'_>, falcon_code: String, kivo_item: String) -> Result<Vec<MapEntry>, String> {
    let uid = require(&state, Permission::Edit)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    falcon_import::ensure(&mut conn)?;
    falcon_import::map_set(&conn, uid, &falcon_code, &kivo_item)?;
    falcon_import::map_list(&conn)
}
