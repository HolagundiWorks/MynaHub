//! The company's users: who can sign in to the suite, and with which role.
//!
//! Users belong to the company file, so every app sees the same people. They are managed from O-Studio (the suite shell); CoreOp, Draft and
//! Form only sign in as one of them. Every change needs the administrator's own password again, and the last active owner can never be removed.

use crate::auth;
use crate::schema::audit;
use rusqlite::{params, Connection};
use serde::Serialize;

#[derive(Serialize, Debug, PartialEq, Eq)]
pub struct UserRow {
    pub id: i64,
    pub username: String,
    pub role: String,
    pub disabled: bool,
}

pub const ROLES: [&str; 5] = ["owner", "principal", "accounts", "document_editor", "auditor"];

pub fn list(conn: &Connection) -> Result<Vec<UserRow>, String> {
    let mut stmt = conn.prepare("SELECT id, username, role, disabled FROM users ORDER BY username").map_err(|e| e.to_string())?;
    let rows = stmt.query_map([], |r| Ok(UserRow { id: r.get(0)?, username: r.get(1)?, role: r.get(2)?, disabled: r.get::<_, i64>(3)? != 0 })).map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn create(conn: &Connection, actor: i64, username: &str, password: &str, role: &str) -> Result<i64, String> {
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
    audit(conn, Some(actor), "user_created", &format!("{username} as {role}"));
    Ok(id)
}

/// Changes a user's role and whether they can sign in. The last active owner can never be demoted or disabled.
pub fn update(conn: &Connection, actor: i64, id: i64, role: &str, disabled: bool) -> Result<(), String> {
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
    audit(conn, Some(actor), "user_updated", &format!("user {id}: role {role}, disabled {disabled}"));
    Ok(())
}

/// Sets a user's password (the administrator's reset for someone who has forgotten theirs) and clears any lockout from failed attempts.
/// The person's recovery code, if they have one, is not touched.
pub fn set_password(conn: &Connection, actor: i64, id: i64, password: &str) -> Result<(), String> {
    auth::validate_new_password(password)?;
    let username: String = conn.query_row("SELECT username FROM users WHERE id = ?1", params![id], |r| r.get(0)).map_err(|_| "User not found.".to_string())?;
    conn.execute("UPDATE users SET password_hash = ?1, failed_attempts = 0, locked_until = 0 WHERE id = ?2", params![auth::hash_password(password)?, id]).map_err(|e| e.to_string())?;
    audit(conn, Some(actor), "user_password_set", &username);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::schema::migrate(&mut c).unwrap();
        c
    }

    const PW: &str = "a long enough password";

    #[test]
    fn people_are_created_listed_and_a_taken_name_or_weak_password_is_refused() {
        let c = conn();
        let owner = create(&c, 1, "meera", PW, "owner").unwrap();
        create(&c, owner, "ravi", PW, "accounts").unwrap();
        let names: Vec<_> = list(&c).unwrap().into_iter().map(|u| (u.username, u.role, u.disabled)).collect();
        assert_eq!(names, [("meera".to_string(), "owner".to_string(), false), ("ravi".to_string(), "accounts".to_string(), false)]);
        assert_eq!(create(&c, owner, "ravi", PW, "accounts").unwrap_err(), "That username is already taken.");
        assert!(create(&c, owner, "short", "tiny", "accounts").is_err());
        assert!(create(&c, owner, "  ", PW, "accounts").is_err());
        assert_eq!(create(&c, owner, "x", PW, "boss").unwrap_err(), "Invalid role.");
    }

    #[test]
    fn the_last_active_owner_can_never_be_demoted_or_disabled() {
        let c = conn();
        let a = create(&c, 1, "meera", PW, "owner").unwrap();
        assert_eq!(update(&c, a, a, "accounts", false).unwrap_err(), "There must always be at least one active owner.");
        assert!(update(&c, a, a, "owner", true).is_err());
        let b = create(&c, a, "second", PW, "owner").unwrap();
        update(&c, a, a, "principal", false).unwrap();
        assert!(update(&c, b, b, "owner", true).is_err(), "now the second is the last one");
    }

    #[test]
    fn an_administrator_can_set_a_password_and_it_clears_a_lockout() {
        let c = conn();
        let id = create(&c, 1, "ravi", PW, "accounts").unwrap();
        c.execute("UPDATE users SET failed_attempts = 5, locked_until = 9999999999 WHERE id = ?1", params![id]).unwrap();
        assert!(crate::workspace::check_credentials(&c, "ravi", PW).is_err(), "locked out");
        set_password(&c, 1, id, "a brand new password!").unwrap();
        assert!(crate::workspace::check_credentials(&c, "ravi", "a brand new password!").is_ok());
        assert!(crate::workspace::check_credentials(&c, "ravi", PW).is_err(), "the old password no longer works");
        assert!(set_password(&c, 1, id, "tiny").is_err());
        assert_eq!(set_password(&c, 1, 999, "a brand new password!").unwrap_err(), "User not found.");
        let audited: i64 = c.query_row("SELECT COUNT(*) FROM audit_log WHERE event = 'user_password_set'", [], |r| r.get(0)).unwrap();
        assert_eq!(audited, 1);
    }
}
