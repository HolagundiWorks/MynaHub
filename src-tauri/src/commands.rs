use base64::Engine;
use crate::auth::{Permission, SessionInfo};
use crate::db;
use rusqlite::{params, Connection};
use serde::Serialize;

pub use office_core::workspace::*;

/// Borrowed application session shared by bridge/FFI entry points.
pub struct HostState<'a>(&'a AppState);

impl std::ops::Deref for HostState<'_> {
    type Target = AppState;
    fn deref(&self) -> &AppState {
        self.0
    }
}

impl<'a> HostState<'a> {
    pub fn new(state: &'a AppState) -> Self {
        Self(state)
    }
}

pub fn auth_status(state: HostState<'_>) -> Result<AuthStatus, String> {
    read_auth_status(&state)
}

/// Creates the first (owner) account. Returns the one-time recovery code.
pub fn auth_setup(state: HostState<'_>, username: String, password: String, confirm: String) -> Result<String, String> {
    if crate::company_file::active_summary(&state).is_none() {
        return Err("Open or create a company first.".into());
    }
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    create_owner(&conn, &username, &password, &confirm)
}

pub fn auth_login(state: HostState<'_>, username: String, password: String) -> Result<SessionInfo, String> {
    login(&state, &username, &password)
}

/// Unlocks the current locked session; the password must belong to the same user.
pub fn auth_unlock(state: HostState<'_>, password: String) -> Result<(), String> {
    unlock(&state, &password)
}

pub fn auth_lock(state: HostState<'_>) -> Result<(), String> {
    lock(&state)
}

pub fn auth_logout(state: HostState<'_>) -> Result<(), String> {
    logout(&state)
}

pub use office_core::profile::CompanyProfile;

pub fn read_profile(state: &AppState) -> Result<CompanyProfile, String> {
    require(state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    read_profile_row(&conn)
}

fn read_profile_row(conn: &Connection) -> Result<CompanyProfile, String> {
    office_core::profile::read(conn)
}

pub fn company_get_profile(state: HostState<'_>) -> Result<CompanyProfile, String> {
    require(&state, Permission::View)?;
    read_profile(&state)
}

pub fn company_save_profile(state: HostState<'_>, profile: CompanyProfile) -> Result<(), String> {
    let uid = require(&state, Permission::ManageSettings)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    office_core::profile::save(&conn, uid, &profile)
}

/// Square mark and wide lockup stored on the firm. Empty strings mean that shape has not been stored.
#[derive(Clone)]
pub(crate) struct StoredLogos {
    pub square: String,
    pub wide: String,
}

pub(crate) fn company_logos_b64(conn: &Connection) -> StoredLogos {
    StoredLogos { square: logo_b64(conn, "logo_key"), wide: logo_b64(conn, "logo_wide_key") }
}

fn logo_b64(conn: &Connection, column: &str) -> String {
    let sql = match column {
        "logo_key" => "SELECT logo_key FROM company_profile WHERE id = 1",
        "logo_wide_key" => "SELECT logo_wide_key FROM company_profile WHERE id = 1",
        _ => return String::new(),
    };
    let key: String = conn.query_row(sql, [], |r| r.get(0)).unwrap_or_default();
    if key.is_empty() {
        return String::new();
    }
    match crate::store::get(conn, &key) {
        Ok(bytes) => base64::engine::general_purpose::STANDARD.encode(bytes),
        Err(_) => String::new(),
    }
}

#[derive(Serialize)]
pub struct CompanyMark {
    pub present: bool,
    pub mime: String,
    pub data_base64: String,
}

#[derive(Serialize)]
pub struct CompanyLogos {
    pub square: CompanyMark,
    pub wide: CompanyMark,
}

fn logo_mime(data: &[u8]) -> Result<&'static str, String> {
    if data.len() > 1_500_000 {
        return Err("The logo must be smaller than 1.5 MB.".into());
    }
    if data.starts_with(b"\x89PNG\r\n\x1a\n") {
        return Ok("image/png");
    }
    if data.len() >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF {
        return Ok("image/jpeg");
    }
    Err("Choose a PNG or JPEG logo.".into())
}

fn mark_of(data: &str) -> CompanyMark {
    if data.is_empty() {
        return CompanyMark { present: false, mime: String::new(), data_base64: String::new() };
    }
    let mime = base64::engine::general_purpose::STANDARD
        .decode(data)
        .ok()
        .and_then(|bytes| logo_mime(&bytes).ok())
        .unwrap_or("image/png")
        .to_string();
    CompanyMark { present: true, mime, data_base64: data.to_string() }
}

fn logo_shape(shape: &str) -> Result<&'static str, String> {
    match shape {
        "square" => Ok("square"),
        "wide" => Ok("wide"),
        _ => Err("Choose a square logo or a rectangle logo.".into()),
    }
}

pub fn company_logo(state: HostState<'_>) -> Result<CompanyLogos, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let logos = company_logos_b64(&conn);
    Ok(CompanyLogos { square: mark_of(&logos.square), wide: mark_of(&logos.wide) })
}

pub fn company_logo_set(state: HostState<'_>, shape: String, data_base64: String) -> Result<(), String> {
    let uid = require(&state, Permission::ManageSettings)?;
    let shape = logo_shape(&shape)?;
    let bytes = base64::engine::general_purpose::STANDARD.decode(data_base64.trim()).map_err(|_| "The logo could not be read.".to_string())?;
    let _mime = logo_mime(&bytes)?;
    let key = format!(
        "logo-{shape}-{}",
        std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0)
    );
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    crate::store::put(&conn, &key, &bytes)?;
    let updated = if shape == "square" {
        conn.execute("UPDATE company_profile SET logo_key=?1, updated_at=datetime('now') WHERE id=1", params![key])
    } else {
        conn.execute("UPDATE company_profile SET logo_wide_key=?1, updated_at=datetime('now') WHERE id=1", params![key])
    };
    updated.map_err(|e| e.to_string())?;
    db::audit(&conn, Some(uid), "company_logo_saved", shape);
    Ok(())
}

pub fn company_logo_clear(state: HostState<'_>, shape: String) -> Result<(), String> {
    let uid = require(&state, Permission::ManageSettings)?;
    let shape = logo_shape(&shape)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let updated = if shape == "square" {
        conn.execute("UPDATE company_profile SET logo_key='', updated_at=datetime('now') WHERE id=1", [])
    } else {
        conn.execute("UPDATE company_profile SET logo_wide_key='', updated_at=datetime('now') WHERE id=1", [])
    };
    updated.map_err(|e| e.to_string())?;
    db::audit(&conn, Some(uid), "company_logo_cleared", shape);
    Ok(())
}

#[cfg(test)]
mod tests {
    use crate::auth::{self, Role, Session};
    use std::time::Instant;
    use super::*;

    fn state() -> AppState {
        let mut conn = Connection::open_in_memory().unwrap();
        db::migrate(&mut conn).unwrap();
        AppState::new(conn, std::env::temp_dir(), crate::db::migrate)
    }

    fn sign_in(s: &AppState, role: Role) {
        *s.session.lock().unwrap() =
            Some(Session { user_id: 1, username: "u".into(), role, locked: false, last_activity: Instant::now() });
    }

    #[test]
    fn unauthenticated_is_rejected() {
        assert!(require(&state(), Permission::View).is_err());
    }

    #[test]
    fn locked_session_is_rejected() {
        let s = state();
        sign_in(&s, Role::Owner);
        s.session.lock().unwrap().as_mut().unwrap().locked = true;
        assert!(require(&s, Permission::View).is_err());
    }

    #[test]
    fn idle_session_auto_locks() {
        let s = state();
        sign_in(&s, Role::Owner);
        s.session.lock().unwrap().as_mut().unwrap().last_activity =
            Instant::now() - auth::DEFAULT_IDLE - std::time::Duration::from_secs(1);
        assert!(require(&s, Permission::View).is_err());
    }

    #[test]
    fn role_gates_settings() {
        let s = state();
        sign_in(&s, Role::Auditor);
        assert!(require(&s, Permission::View).is_ok());
        assert!(require(&s, Permission::ManageSettings).is_err());
    }

    #[test]
    fn login_throttles_after_repeated_failures() {
        let s = state();
        let conn = s.db.lock().unwrap();
        conn.execute(
            "INSERT INTO users (username, password_hash, role) VALUES ('o', ?1, 'owner')",
            params![auth::hash_password("correct horse battery").unwrap()],
        )
        .unwrap();
        for _ in 0..5 {
            assert!(check_credentials(&conn, "o", "bad").is_err());
        }
        // Even the right password is refused while throttled.
        let err = check_credentials(&conn, "o", "correct horse battery").unwrap_err();
        assert!(err.contains("Too many attempts"));
    }

    #[test]
    fn company_logo_is_stored_inside_the_file_and_replaced_as_a_new_copy() {
        let s = state();
        sign_in(&s, Role::Owner);
        let png = b"\x89PNG\r\n\x1a\nrest-of-file";
        let encoded = base64::engine::general_purpose::STANDARD.encode(png);
        company_logo_set(HostState::new(&s), "square".into(), encoded.clone()).unwrap();
        company_logo_set(HostState::new(&s), "wide".into(), encoded.clone()).unwrap();
        let logo = company_logo(HostState::new(&s)).unwrap();
        assert!(logo.square.present && logo.wide.present);
        assert_eq!(logo.square.mime, "image/png");
        assert_eq!(logo.wide.data_base64, encoded);
        assert!(company_logo_set(HostState::new(&s), "square".into(), base64::engine::general_purpose::STANDARD.encode(b"not an image")).is_err());
        assert!(company_logo_set(HostState::new(&s), "round".into(), encoded.clone()).is_err());
        company_logo_clear(HostState::new(&s), "square".into()).unwrap();
        let left = company_logo(HostState::new(&s)).unwrap();
        assert!(!left.square.present);
        assert!(left.wide.present);
    }
}
