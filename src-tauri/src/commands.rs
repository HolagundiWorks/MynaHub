use crate::auth::{self, Permission, Role, Session, SessionInfo};
use crate::db;
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};
use std::sync::Mutex;
use std::time::Instant;

pub struct AppState {
    pub db: Mutex<Connection>,
    pub session: Mutex<Option<Session>>,
    /// The company file currently open, if any. While none is open `db` holds an empty placeholder.
    pub company: Mutex<Option<crate::company_file::ActiveCompany>>,
    /// Per-user app folder (recent companies list). Holds no business data.
    pub app_dir: std::path::PathBuf,
    /// Inactivity timeout in seconds, loaded from the settings table.
    pub idle_secs: std::sync::atomic::AtomicU64,
}

impl AppState {
    pub fn new(conn: Connection, app_dir: std::path::PathBuf) -> Self {
        let idle: u64 = conn
            .query_row("SELECT value FROM settings WHERE key = 'idle_secs'", [], |r| r.get::<_, String>(0))
            .ok()
            .and_then(|v| v.parse().ok())
            .unwrap_or(auth::DEFAULT_IDLE.as_secs());
        AppState { db: Mutex::new(conn), session: Mutex::new(None), company: Mutex::new(None), app_dir, idle_secs: std::sync::atomic::AtomicU64::new(idle) }
    }

    pub fn idle(&self) -> std::time::Duration {
        std::time::Duration::from_secs(self.idle_secs.load(std::sync::atomic::Ordering::Relaxed))
    }
}

/// Borrowed application session shared by Tauri commands and the C ABI.
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

impl<'a, R: tauri::Runtime> tauri::ipc::CommandArg<'a, R> for HostState<'a> {
    fn from_command(command: tauri::ipc::CommandItem<'a, R>) -> Result<Self, tauri::ipc::InvokeError> {
        let state = tauri::State::<'a, AppState>::from_command(command)?;
        // `State` is a reference-sized wrapper around the managed session. Take the inner address
        // before the wrapper is dropped; the session itself lives for `'a`.
        let inner: &AppState = std::ops::Deref::deref(&state);
        let extended: &'a AppState = unsafe { &*std::ptr::from_ref(inner) };
        Ok(HostState(extended))
    }
}

pub const INTERNAL: &str = "Internal error.";

fn audit_state(state: &AppState, user_id: i64, event: &str) {
    if let Ok(conn) = state.db.lock() {
        db::audit(&conn, Some(user_id), event, "");
    }
}

fn now_secs() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// Every protected command goes through here: a session must exist, be unlocked,
/// not idle-expired, and the role must hold the permission. Returns the user id.
pub fn require(state: &AppState, perm: Permission) -> Result<i64, String> {
    let mut guard = state.session.lock().map_err(|_| INTERNAL)?;
    let s = guard.as_mut().ok_or("Not signed in.")?;
    if !s.locked && s.last_activity.elapsed() > state.idle() {
        s.locked = true;
        audit_state(state, s.user_id, "auto_lock");
    }
    if s.locked {
        return Err("Session is locked.".into());
    }
    if !s.role.allows(perm) {
        return Err("You do not have permission for this action.".into());
    }
    s.last_activity = Instant::now();
    Ok(s.user_id)
}

#[derive(Serialize)]
pub struct AuthStatus {
    pub company: Option<crate::company_file::CompanySummary>,
    pub setup_required: bool,
    pub locked: bool,
    pub user: Option<SessionInfo>,
}

pub fn read_auth_status(state: &AppState) -> Result<AuthStatus, String> {
    let company = crate::company_file::active_summary(&state);
    if company.is_none() {
        return Ok(AuthStatus { company: None, setup_required: false, locked: false, user: None });
    }
    let setup_required = {
        let conn = state.db.lock().map_err(|_| INTERNAL)?;
        let n: i64 = conn
            .query_row("SELECT COUNT(*) FROM users", [], |r| r.get(0))
            .map_err(|e| e.to_string())?;
        n == 0
    };
    let mut guard = state.session.lock().map_err(|_| INTERNAL)?;
    if let Some(s) = guard.as_mut() {
        if !s.locked && s.last_activity.elapsed() > state.idle() {
            s.locked = true;
        }
    }
    let (locked, user) = match guard.as_ref() {
        Some(s) => (
            s.locked,
            Some(SessionInfo { username: s.username.clone(), role: format!("{:?}", s.role) }),
        ),
        None => (false, None),
    };
    Ok(AuthStatus { company, setup_required, locked, user })
}

#[tauri::command]
pub fn auth_status(state: HostState<'_>) -> Result<AuthStatus, String> {
    read_auth_status(&state)
}

/// Creates the first (owner) account. Returns the one-time recovery code.
#[tauri::command]
pub fn auth_setup(
    state: HostState<'_>,
    username: String,
    password: String,
    confirm: String,
) -> Result<String, String> {
    if crate::company_file::active_summary(&state).is_none() {
        return Err("Open or create a company first.".into());
    }
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    create_owner(&conn, &username, &password, &confirm)
}

/// Creates the first owner account in an empty company and returns the one-time recovery code.
pub fn create_owner(conn: &Connection, username: &str, password: &str, confirm: &str) -> Result<String, String> {
    let username = username.trim().to_string();
    if username.is_empty() {
        return Err("Username is required.".into());
    }
    if password != confirm {
        return Err("Passwords do not match.".into());
    }
    auth::validate_new_password(password)?;
    let n: i64 = conn
        .query_row("SELECT COUNT(*) FROM users", [], |r| r.get(0))
        .map_err(|e| e.to_string())?;
    if n > 0 {
        return Err("Setup has already been completed.".into());
    }
    let recovery = auth::generate_recovery_code();
    conn.execute(
        "INSERT INTO users (username, password_hash, recovery_hash, role) VALUES (?1, ?2, ?3, 'owner')",
        params![username, auth::hash_password(password)?, auth::hash_password(&recovery)?],
    )
    .map_err(|e| e.to_string())?;
    db::audit(conn, Some(conn.last_insert_rowid()), "owner_created", "");
    Ok(recovery)
}

fn start_session(state: &AppState, id: i64, username: String, role: &str) -> Result<(), String> {
    *state.session.lock().map_err(|_| INTERNAL)? = Some(Session {
        user_id: id,
        username,
        role: Role::parse(role),
        locked: false,
        last_activity: Instant::now(),
    });
    Ok(())
}

type UserRow = (i64, String, String, String, i64, i64, i64);

pub(crate) fn check_credentials(conn: &Connection, username: &str, password: &str) -> Result<(i64, String, String), String> {
    const GENERIC: &str = "Invalid username or password.";
    let row: Result<UserRow, _> = conn.query_row(
        "SELECT id, username, password_hash, role, disabled, failed_attempts, locked_until FROM users WHERE username = ?1",
        params![username],
        |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?, r.get(6)?)),
    );
    let Ok((id, uname, hash, role, disabled, failed, locked_until)) = row else {
        // Spend comparable time for unknown users so response timing doesn't reveal them.
        let _ = auth::hash_password(password);
        return Err(GENERIC.into());
    };
    let now = now_secs();
    if locked_until > now {
        return Err(format!("Too many attempts. Try again in {} seconds.", locked_until - now));
    }
    if disabled != 0 || !auth::verify_password(password, &hash) {
        let n = failed + 1;
        conn.execute(
            "UPDATE users SET failed_attempts = ?1, locked_until = ?2 WHERE id = ?3",
            params![n, now + auth::lockout_secs(n), id],
        )
        .map_err(|e| e.to_string())?;
        db::audit(conn, Some(id), "login_failed", "");
        return Err(GENERIC.into());
    }
    conn.execute("UPDATE users SET failed_attempts = 0, locked_until = 0 WHERE id = ?1", params![id])
        .map_err(|e| e.to_string())?;
    Ok((id, uname, role))
}

pub fn login(state: &AppState, username: &str, password: &str) -> Result<SessionInfo, String> {
    if crate::company_file::active_summary(state).is_none() {
        return Err("Open or create a company first.".into());
    }
    let (id, uname, role) = {
        let conn = state.db.lock().map_err(|_| INTERNAL)?;
        let r = check_credentials(&conn, username.trim(), password)?;
        db::audit(&conn, Some(r.0), "login_ok", "");
        r
    };
    start_session(state, id, uname.clone(), &role)?;
    Ok(SessionInfo { username: uname, role })
}

#[tauri::command]
pub fn auth_login(state: HostState<'_>, username: String, password: String) -> Result<SessionInfo, String> {
    login(&state, &username, &password)
}

/// Unlocks the current locked session; the password must belong to the same user.
#[tauri::command]
pub fn auth_unlock(state: HostState<'_>, password: String) -> Result<(), String> {
    let (uid, uname) = {
        let g = state.session.lock().map_err(|_| INTERNAL)?;
        let s = g.as_ref().ok_or("Not signed in.")?;
        (s.user_id, s.username.clone())
    };
    {
        let conn = state.db.lock().map_err(|_| INTERNAL)?;
        check_credentials(&conn, &uname, &password)?;
        db::audit(&conn, Some(uid), "unlock", "");
    }
    let mut g = state.session.lock().map_err(|_| INTERNAL)?;
    if let Some(s) = g.as_mut() {
        s.locked = false;
        s.last_activity = Instant::now();
    }
    Ok(())
}

#[tauri::command]
pub fn auth_lock(state: HostState<'_>) -> Result<(), String> {
    let mut g = state.session.lock().map_err(|_| INTERNAL)?;
    if let Some(s) = g.as_mut() {
        s.locked = true;
        audit_state(&state, s.user_id, "lock");
    }
    Ok(())
}

pub fn logout(state: &AppState) -> Result<(), String> {
    let mut g = state.session.lock().map_err(|_| INTERNAL)?;
    if let Some(s) = g.take() {
        audit_state(state, s.user_id, "logout");
    }
    Ok(())
}

#[tauri::command]
pub fn auth_logout(state: HostState<'_>) -> Result<(), String> {
    logout(&state)
}

#[derive(Serialize, Deserialize)]
pub struct CompanyProfile {
    pub name: String,
    pub address: String,
    pub email: String,
    pub phone: String,
    pub pan: String,
    pub gstin: String,
    pub coa_registration: String,
    #[serde(default)]
    pub bank_details: String,
    #[serde(default)]
    pub gst_scheme: String,
    #[serde(default)]
    pub upi_id: String,
    #[serde(default)]
    pub architect_name: String,
    /// `classic` or `minimal`: how the firm's details are set at the top of issued documents.
    #[serde(default)]
    pub letterhead_style: String,
}

pub fn read_profile(state: &AppState) -> Result<CompanyProfile, String> {
    require(state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    read_profile_row(&conn)
}

fn read_profile_row(conn: &Connection) -> Result<CompanyProfile, String> {
    conn.query_row(
        "SELECT name, address, email, phone, pan, gstin, coa_registration, bank_details, gst_scheme, upi_id, architect_name, letterhead_style FROM company_profile WHERE id = 1",
        [],
        |r| {
            Ok(CompanyProfile {
                name: r.get(0)?,
                address: r.get(1)?,
                email: r.get(2)?,
                phone: r.get(3)?,
                pan: r.get(4)?,
                gstin: r.get(5)?,
                coa_registration: r.get(6)?,
                bank_details: r.get(7)?,
                gst_scheme: r.get(8)?,
                upi_id: r.get(9)?,
                architect_name: r.get(10)?,
                letterhead_style: r.get(11)?,
            })
        },
    )
    .map_err(|e| e.to_string())
}

#[tauri::command]
pub fn company_get_profile(state: HostState<'_>) -> Result<CompanyProfile, String> {
    require(&state, Permission::View)?;
    read_profile(&state)
}

#[tauri::command]
pub fn company_save_profile(state: HostState<'_>, profile: CompanyProfile) -> Result<(), String> {
    let uid = require(&state, Permission::ManageSettings)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    conn.execute(
        "UPDATE company_profile SET name=?1, address=?2, email=?3, phone=?4, pan=?5, gstin=?6, coa_registration=?7, bank_details=?8, gst_scheme=?9, upi_id=?10, architect_name=?11, letterhead_style=?12, updated_at=datetime('now') WHERE id=1",
        params![profile.name, profile.address, profile.email, profile.phone, profile.pan, profile.gstin, profile.coa_registration, profile.bank_details, profile.gst_scheme, profile.upi_id.trim(), profile.architect_name.trim(), if profile.letterhead_style == "minimal" { "minimal" } else { "classic" }],
    )
    .map_err(|e| e.to_string())?;
    db::audit(&conn, Some(uid), "company_profile_saved", "");
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn state() -> AppState {
        let mut conn = Connection::open_in_memory().unwrap();
        db::migrate(&mut conn).unwrap();
        AppState::new(conn, std::env::temp_dir())
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
}
