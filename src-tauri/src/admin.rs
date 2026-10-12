//! Account, security, audit and backup operations.
//! Sensitive actions (user changes, backup, security settings) require the signed-in user's password again.

use crate::auth::{self, Permission};
use crate::billing::sha2_lite::sha256_hex;
use crate::commands::{check_credentials, require, AppState, INTERNAL};
use crate::db;
use rusqlite::{params, Connection};
use serde::Serialize;
use tauri::State;

fn session_username(state: &AppState) -> Result<String, String> {
    let g = state.session.lock().map_err(|_| INTERNAL)?;
    Ok(g.as_ref().ok_or("Not signed in.")?.username.clone())
}

/// Re-checks the current user's password (shares the throttling used at login).
pub(crate) fn reauth(state: &AppState, password: &str) -> Result<(), String> {
    let username = session_username(state)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    check_credentials(&conn, &username, password).map(|_| ()).map_err(|_| "Your password was not accepted.".to_string())
}

// ---------- password change / recovery ----------

#[tauri::command]
pub fn auth_change_password(state: State<AppState>, current: String, new: String, confirm: String) -> Result<(), String> {
    let uid = require(&state, Permission::View)?;
    if new != confirm {
        return Err("The new passwords do not match.".into());
    }
    auth::validate_new_password(&new)?;
    reauth(&state, &current)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    conn.execute("UPDATE users SET password_hash = ?1 WHERE id = ?2", params![auth::hash_password(&new)?, uid]).map_err(|e| e.to_string())?;
    db::audit(&conn, Some(uid), "password_changed", "");
    Ok(())
}

/// Resets a password using the one-time recovery code, and returns a fresh recovery code.
pub fn recover(conn: &Connection, username: &str, code: &str, new: &str) -> Result<String, String> {
    const GENERIC: &str = "The username or recovery code was not accepted.";
    auth::validate_new_password(new)?;
    let row: Result<(i64, Option<String>, i64, i64), _> = conn.query_row(
        "SELECT id, recovery_hash, failed_attempts, locked_until FROM users WHERE username = ?1 AND disabled = 0",
        params![username.trim()],
        |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)),
    );
    let Ok((id, hash, failed, locked_until)) = row else {
        let _ = auth::hash_password(code);
        return Err(GENERIC.into());
    };
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0);
    if locked_until > now {
        return Err(format!("Too many attempts. Try again in {} seconds.", locked_until - now));
    }
    let normalized = code.trim().to_uppercase();
    let ok = hash.as_deref().map(|h| auth::verify_password(&normalized, h)).unwrap_or(false);
    if !ok {
        let n = failed + 1;
        conn.execute("UPDATE users SET failed_attempts = ?1, locked_until = ?2 WHERE id = ?3", params![n, now + auth::lockout_secs(n), id]).map_err(|e| e.to_string())?;
        db::audit(conn, Some(id), "recovery_failed", "");
        return Err(GENERIC.into());
    }
    let fresh = auth::generate_recovery_code();
    conn.execute(
        "UPDATE users SET password_hash = ?1, recovery_hash = ?2, failed_attempts = 0, locked_until = 0 WHERE id = ?3",
        params![auth::hash_password(new)?, auth::hash_password(&fresh)?, id],
    )
    .map_err(|e| e.to_string())?;
    db::audit(conn, Some(id), "password_recovered", "");
    Ok(fresh)
}

#[tauri::command]
pub fn auth_recover(state: State<AppState>, username: String, code: String, new: String, confirm: String) -> Result<String, String> {
    if new != confirm {
        return Err("The new passwords do not match.".into());
    }
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    recover(&conn, &username, &code, &new)
}

// ---------- security settings ----------

#[tauri::command]
pub fn security_get(state: State<AppState>) -> Result<u64, String> {
    require(&state, Permission::View)?;
    Ok(state.idle().as_secs() / 60)
}

#[tauri::command]
pub fn security_set_idle_minutes(state: State<AppState>, minutes: u64, password: String) -> Result<(), String> {
    let uid = require(&state, Permission::ManageSettings)?;
    if ![1, 5, 10, 15, 30].contains(&minutes) {
        return Err("Choose 1, 5, 10, 15 or 30 minutes.".into());
    }
    reauth(&state, &password)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    conn.execute("INSERT INTO settings (key, value) VALUES ('idle_secs', ?1) ON CONFLICT(key) DO UPDATE SET value = excluded.value", params![(minutes * 60).to_string()])
        .map_err(|e| e.to_string())?;
    state.idle_secs.store(minutes * 60, std::sync::atomic::Ordering::Relaxed);
    db::audit(&conn, Some(uid), "idle_timeout_changed", &format!("{minutes} minutes"));
    Ok(())
}

// ---------- users ----------

#[derive(Serialize)]
pub struct UserRow {
    pub id: i64,
    pub username: String,
    pub role: String,
    pub disabled: bool,
}

const ROLES: [&str; 5] = ["owner", "principal", "accounts", "document_editor", "auditor"];

pub fn list_users(conn: &Connection) -> Result<Vec<UserRow>, String> {
    let mut stmt = conn.prepare("SELECT id, username, role, disabled FROM users ORDER BY username").map_err(|e| e.to_string())?;
    let rows = stmt.query_map([], |r| Ok(UserRow { id: r.get(0)?, username: r.get(1)?, role: r.get(2)?, disabled: r.get::<_, i64>(3)? != 0 })).map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn create_user(conn: &Connection, actor: i64, username: &str, password: &str, role: &str) -> Result<i64, String> {
    let username = username.trim();
    if username.is_empty() {
        return Err("Username is required.".into());
    }
    if !ROLES.contains(&role) {
        return Err("Invalid role.".into());
    }
    auth::validate_new_password(password)?;
    conn.execute("INSERT INTO users (username, password_hash, role) VALUES (?1, ?2, ?3)", params![username, auth::hash_password(password)?, role])
        .map_err(|e| match e {
            rusqlite::Error::SqliteFailure(f, _) if f.code == rusqlite::ErrorCode::ConstraintViolation => "That username is already taken.".to_string(),
            other => other.to_string(),
        })?;
    let id = conn.last_insert_rowid();
    db::audit(conn, Some(actor), "user_created", &format!("{username} as {role}"));
    Ok(id)
}

/// Changes role and/or disabled flag. The last active owner can never be demoted or disabled.
pub fn update_user(conn: &Connection, actor: i64, id: i64, role: &str, disabled: bool) -> Result<(), String> {
    if !ROLES.contains(&role) {
        return Err("Invalid role.".into());
    }
    let (cur_role, cur_disabled): (String, i64) = conn
        .query_row("SELECT role, disabled FROM users WHERE id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|_| "User not found.".to_string())?;
    let was_active_owner = cur_role == "owner" && cur_disabled == 0;
    let stays_active_owner = role == "owner" && !disabled;
    if was_active_owner && !stays_active_owner {
        let other_owners: i64 = conn
            .query_row("SELECT COUNT(*) FROM users WHERE role = 'owner' AND disabled = 0 AND id <> ?1", params![id], |r| r.get(0))
            .map_err(|e| e.to_string())?;
        if other_owners == 0 {
            return Err("There must always be at least one active owner.".into());
        }
    }
    conn.execute("UPDATE users SET role = ?1, disabled = ?2 WHERE id = ?3", params![role, disabled, id]).map_err(|e| e.to_string())?;
    db::audit(conn, Some(actor), "user_updated", &format!("user {id}: role {role}, disabled {disabled}"));
    Ok(())
}

#[tauri::command]
pub fn user_list(state: State<AppState>) -> Result<Vec<UserRow>, String> {
    require(&state, Permission::ManageUsers)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    list_users(&conn)
}

#[tauri::command]
pub fn user_create(state: State<AppState>, username: String, password: String, role: String, admin_password: String) -> Result<i64, String> {
    let uid = require(&state, Permission::ManageUsers)?;
    reauth(&state, &admin_password)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    create_user(&conn, uid, &username, &password, &role)
}

#[tauri::command]
pub fn user_update(state: State<AppState>, id: i64, role: String, disabled: bool, admin_password: String) -> Result<(), String> {
    let uid = require(&state, Permission::ManageUsers)?;
    reauth(&state, &admin_password)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    update_user(&conn, uid, id, &role, disabled)
}

// ---------- audit ----------

#[derive(Serialize)]
pub struct AuditRow {
    pub at: String,
    pub user: String,
    pub event: String,
    pub detail: String,
}

#[tauri::command]
pub fn audit_list(state: State<AppState>, limit: i64) -> Result<Vec<AuditRow>, String> {
    require(&state, Permission::ViewAudit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let mut stmt = conn
        .prepare("SELECT a.at, COALESCE(u.username, ''), a.event, a.detail FROM audit_log a LEFT JOIN users u ON u.id = a.user_id ORDER BY a.id DESC LIMIT ?1")
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![limit.clamp(1, 1000)], |r| Ok(AuditRow { at: r.get(0)?, user: r.get(1)?, event: r.get(2)?, detail: r.get(3)? }))
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

// ---------- backup ----------

#[derive(Serialize)]
pub struct BackupInfo {
    pub filename: String,
    pub sha256: String,
    pub bytes: u64,
    pub folder: String,
}

/// Consistent snapshot of the whole company (records, issued documents, attachments, audit history) via VACUUM INTO,
/// verified by opening it and running an integrity check. A backup is itself a complete company file.
pub fn backup(conn: &Connection, dir: &std::path::Path, actor: i64) -> Result<BackupInfo, String> {
    std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    let stamp: String = conn.query_row("SELECT strftime('%Y%m%d-%H%M%S','now')", [], |r| r.get(0)).map_err(|e| e.to_string())?;
    let mut path = dir.join(format!("backup-{stamp}.adms"));
    let mut n = 1;
    while path.exists() {
        path = dir.join(format!("backup-{stamp}-{n}.adms"));
        n += 1;
    }
    conn.execute("VACUUM INTO ?1", params![path.to_string_lossy()]).map_err(|e| format!("Backup failed: {e}"))?;
    let check = Connection::open_with_flags(&path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY).map_err(|e| e.to_string())?;
    let result: String = check.query_row("PRAGMA integrity_check", [], |r| r.get(0)).map_err(|e| e.to_string())?;
    if result != "ok" {
        let _ = std::fs::remove_file(&path);
        return Err("The backup failed its integrity check and was discarded.".into());
    }
    drop(check);
    let bytes = std::fs::read(&path).map_err(|e| e.to_string())?;
    let info = BackupInfo {
        filename: path.file_name().unwrap().to_string_lossy().to_string(),
        sha256: sha256_hex(&bytes),
        bytes: bytes.len() as u64,
        folder: dir.to_string_lossy().to_string(),
    };
    db::audit(conn, Some(actor), "backup_created", &format!("{} ({})", info.filename, info.sha256));
    Ok(info)
}

#[derive(Serialize)]
pub struct BackupFile {
    pub filename: String,
    pub bytes: u64,
}

pub fn list_backups(dir: &std::path::Path) -> Result<Vec<BackupFile>, String> {
    let mut out = Vec::new();
    if let Ok(rd) = std::fs::read_dir(dir) {
        for e in rd.flatten() {
            let name = e.file_name().to_string_lossy().to_string();
            if name.starts_with("backup-") && (name.ends_with(".adms") || name.ends_with(".aorms")) {
                out.push(BackupFile { filename: name, bytes: e.metadata().map(|m| m.len()).unwrap_or(0) });
            }
        }
    }
    out.sort_by(|a, b| b.filename.cmp(&a.filename));
    Ok(out)
}

/// Replaces the live data with a backup. A fresh backup of the current data is taken first, the chosen file is
/// integrity-checked and must not come from a newer version of the app, and the schema is brought up to date afterwards.
/// Everything is restored together, including issued documents and attachments, because they live in the same file.
pub fn restore(conn: &mut Connection, dir: &std::path::Path, actor: i64, filename: &str) -> Result<String, String> {
    if filename.contains(['/', '\\']) || !filename.starts_with("backup-") || !(filename.ends_with(".adms") || filename.ends_with(".aorms")) {
        return Err("Choose a backup from the list.".into());
    }
    let path = dir.join(filename);
    if !path.is_file() {
        return Err("That backup file was not found.".into());
    }
    let src = Connection::open_with_flags(&path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY).map_err(|e| e.to_string())?;
    let check: String = src.query_row("PRAGMA integrity_check", [], |r| r.get(0)).map_err(|_| "The backup file is not a valid database.".to_string())?;
    if check != "ok" {
        return Err("The backup failed its integrity check and was not restored.".into());
    }
    let version: usize = src.pragma_query_value(None, "user_version", |r| r.get(0)).map_err(|e| e.to_string())?;
    if version > crate::db::latest_version() {
        return Err("This backup was made by a newer version of the app. Update the app before restoring it.".into());
    }
    let uuid_of = |c: &Connection| c.query_row("SELECT company_uuid FROM company_file WHERE id = 1", [], |r| r.get::<_, String>(0)).ok();
    if let (Some(a), Some(b)) = (uuid_of(&src), uuid_of(conn)) {
        if a != b {
            return Err("That backup belongs to a different company and was not restored.".into());
        }
    }
    let pre = backup(conn, dir, actor)?;
    {
        let copy = rusqlite::backup::Backup::new(&src, conn).map_err(|e| e.to_string())?;
        copy.run_to_completion(100, std::time::Duration::ZERO, None).map_err(|e| format!("Restore failed: {e}"))?;
    }
    crate::db::migrate(conn)?;
    db::audit(conn, Some(actor), "backup_restored", &format!("{filename} (previous data saved as {})", pre.filename));
    Ok(pre.filename)
}

#[tauri::command]
pub fn backup_list(state: State<AppState>) -> Result<Vec<BackupFile>, String> {
    require(&state, Permission::Backup)?;
    list_backups(&crate::company_file::backup_dir(&state)?)
}

/// Restores a backup and signs everyone out (the restored data has its own users).
#[tauri::command]
pub fn backup_restore(state: State<AppState>, filename: String, password: String) -> Result<String, String> {
    let uid = require(&state, Permission::Backup)?;
    reauth(&state, &password)?;
    let pre = {
        let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
        restore(&mut conn, &crate::company_file::backup_dir(&state)?, uid, &filename)?
    };
    *state.session.lock().map_err(|_| INTERNAL)? = None;
    Ok(pre)
}

#[tauri::command]
pub fn backup_create(state: State<AppState>, password: String) -> Result<BackupInfo, String> {
    let uid = require(&state, Permission::Backup)?;
    reauth(&state, &password)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    backup(&conn, &crate::company_file::backup_dir(&state)?, uid)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        db::migrate(&mut c).unwrap();
        c
    }

    #[test]
    fn recovery_code_resets_password_once_and_rotates() {
        let c = conn();
        let code = auth::generate_recovery_code();
        c.execute(
            "INSERT INTO users (username, password_hash, recovery_hash, role) VALUES ('owner', ?1, ?2, 'owner')",
            params![auth::hash_password("old password 123").unwrap(), auth::hash_password(&code).unwrap()],
        )
        .unwrap();
        assert!(recover(&c, "owner", "WRONG", "new password 123").is_err());
        assert!(recover(&c, "owner", &code, "short").is_err());
        let fresh = recover(&c, "owner", &code.to_lowercase(), "new password 123").unwrap();
        assert_ne!(fresh, code);
        let hash: String = c.query_row("SELECT password_hash FROM users", [], |r| r.get(0)).unwrap();
        assert!(auth::verify_password("new password 123", &hash));
        // the used code no longer works; the new one does
        assert!(recover(&c, "owner", &code, "another password 1").is_err());
        assert!(recover(&c, "owner", &fresh, "another password 1").is_ok());
    }

    #[test]
    fn repeated_bad_recovery_codes_are_throttled() {
        let c = conn();
        c.execute("INSERT INTO users (username, password_hash, recovery_hash, role) VALUES ('o', ?1, ?2, 'owner')", params![auth::hash_password("x").unwrap(), auth::hash_password("RIGHT").unwrap()]).unwrap();
        for _ in 0..5 {
            assert!(recover(&c, "o", "bad", "valid password 12").is_err());
        }
        assert!(recover(&c, "o", "RIGHT", "valid password 12").unwrap_err().contains("Too many"));
    }

    #[test]
    fn last_owner_cannot_be_demoted_or_disabled() {
        let c = conn();
        let a = create_user(&c, 1, "alice", "alice password 1", "owner").unwrap();
        assert!(update_user(&c, a, a, "auditor", false).is_err());
        assert!(update_user(&c, a, a, "owner", true).is_err());
        let b = create_user(&c, a, "bob", "bob password 123", "owner").unwrap();
        update_user(&c, a, a, "accounts", false).unwrap();
        assert!(update_user(&c, b, b, "owner", true).is_err());
        assert!(create_user(&c, a, "Bob", "bob password 123", "accounts").unwrap_err().contains("taken"));
        assert!(create_user(&c, a, "eve", "short", "accounts").is_err());
        assert!(create_user(&c, a, "eve", "eve password 123", "superuser").is_err());
    }

    #[test]
    fn restore_brings_back_old_data_after_taking_a_safety_backup() {
        let mut c = conn();
        c.execute("INSERT INTO clients (name) VALUES ('Before')", []).unwrap();
        let root = std::env::temp_dir().join(format!("ao_rs_{}", std::process::id())).join("backups");
        let b = backup(&c, &root, 1).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('After')", []).unwrap();
        assert!(restore(&mut c, &root, 1, "../evil.sqlite").is_err());
        assert!(restore(&mut c, &root, 1, "backup-nope.adms").is_err());
        let pre = restore(&mut c, &root, 1, &b.filename).unwrap();
        let names: Vec<String> = c.prepare("SELECT name FROM clients").unwrap().query_map([], |r| r.get(0)).unwrap().map(|r| r.unwrap()).collect();
        assert_eq!(names, vec!["Before"]);
        // the data as it was just before the restore is kept and contains the later change
        let safety = Connection::open(root.join(&pre)).unwrap();
        let n: i64 = safety.query_row("SELECT COUNT(*) FROM clients", [], |r| r.get(0)).unwrap();
        assert_eq!(n, 2);
        assert_eq!(list_backups(&root).unwrap().len(), 2);
        // a backup from a newer app is refused
        let newer = root.join("backup-99999999-000000.adms");
        std::fs::copy(root.join(&b.filename), &newer).unwrap();
        {
            let n = Connection::open(&newer).unwrap();
            n.pragma_update(None, "user_version", 999).unwrap();
        }
        assert!(restore(&mut c, &root, 1, "backup-99999999-000000.adms").unwrap_err().contains("newer version"));
        let _ = std::fs::remove_dir_all(root.parent().unwrap());
    }

    #[test]
    fn backup_produces_a_verified_copy_with_the_data() {
        let c = conn();
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        let root = std::env::temp_dir().join(format!("ao_bk_{}", std::process::id())).join("backups");
        let info = backup(&c, &root, 1).unwrap();
        let copy = Connection::open(root.join(&info.filename)).unwrap();
        let n: i64 = copy.query_row("SELECT COUNT(*) FROM clients", [], |r| r.get(0)).unwrap();
        assert_eq!(n, 1);
        assert_eq!(info.sha256.len(), 64);
        let _ = std::fs::remove_dir_all(root.parent().unwrap());
    }
}
