//! The running workspace: the open company file, the signed-in session and the checks every command makes first.
//! An app (MynaHub, MynaOps) holds one of these and adds its own commands on top.

use crate::auth::{self, Permission, Role, Session, SessionInfo};
use rusqlite::{params, Connection};
use serde::Serialize;
use std::sync::Mutex;
use std::time::Instant;

/// Brings a company file that has just been opened to the schema the app needs.
pub type Prepare = fn(&mut Connection) -> Result<(), String>;

pub struct AppState {
    /// Brings a newly opened company file to the schema this app needs (the shared core plus the app's own track).
    pub prepare: Prepare,
    pub db: Mutex<Connection>,
    pub session: Mutex<Option<Session>>,
    /// The company file currently open, if any. While none is open `db` holds an empty placeholder.
    pub company: Mutex<Option<crate::company::ActiveCompany>>,
    /// Per-user app folder (recent companies list). Holds no business data.
    pub app_dir: std::path::PathBuf,
    /// Inactivity timeout in seconds, loaded from the settings table.
    pub idle_secs: std::sync::atomic::AtomicU64,
}

impl AppState {
    pub fn new(conn: Connection, app_dir: std::path::PathBuf, prepare: Prepare) -> Self {
        let idle: u64 = conn
            .query_row("SELECT value FROM settings WHERE key = 'idle_secs'", [], |r| r.get::<_, String>(0))
            .ok()
            .and_then(|v| v.parse().ok())
            .unwrap_or(auth::DEFAULT_IDLE.as_secs());
        crate::company::sweep_working(&app_dir);
        AppState { prepare, db: Mutex::new(conn), session: Mutex::new(None), company: Mutex::new(None), app_dir, idle_secs: std::sync::atomic::AtomicU64::new(idle) }
    }

    pub fn idle(&self) -> std::time::Duration {
        std::time::Duration::from_secs(self.idle_secs.load(std::sync::atomic::Ordering::Relaxed))
    }
}

pub const INTERNAL: &str = "Internal error.";

fn audit_state(state: &AppState, user_id: i64, event: &str) {
    if let Ok(conn) = state.db.lock() {
        crate::schema::audit(&conn, Some(user_id), event, "");
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
    pub company: Option<crate::company::CompanySummary>,
    pub setup_required: bool,
    pub locked: bool,
    pub user: Option<SessionInfo>,
}

pub fn read_auth_status(state: &AppState) -> Result<AuthStatus, String> {
    let company = crate::company::active_summary(&state);
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
    crate::schema::audit(conn, Some(conn.last_insert_rowid()), "owner_created", "");
    Ok(recovery)
}

pub fn start_session(state: &AppState, id: i64, username: String, role: &str) -> Result<(), String> {
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

pub fn check_credentials(conn: &Connection, username: &str, password: &str) -> Result<(i64, String, String), String> {
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
        crate::schema::audit(conn, Some(id), "login_failed", "");
        return Err(GENERIC.into());
    }
    conn.execute("UPDATE users SET failed_attempts = 0, locked_until = 0 WHERE id = ?1", params![id])
        .map_err(|e| e.to_string())?;
    Ok((id, uname, role))
}

pub fn login(state: &AppState, username: &str, password: &str) -> Result<SessionInfo, String> {
    if crate::company::active_summary(state).is_none() {
        return Err("Open or create a company first.".into());
    }
    let (id, uname, role) = {
        let conn = state.db.lock().map_err(|_| INTERNAL)?;
        let r = check_credentials(&conn, username.trim(), password)?;
        crate::schema::audit(&conn, Some(r.0), "login_ok", "");
        r
    };
    start_session(state, id, uname.clone(), &role)?;
    Ok(SessionInfo { username: uname, role })
}

/// Checks the signed-in person's own password again, before a sensitive change. It shares the throttling used at sign-in.
pub fn reauth(state: &AppState, password: &str) -> Result<(), String> {
    let username = {
        let g = state.session.lock().map_err(|_| INTERNAL)?;
        g.as_ref().ok_or("Not signed in.")?.username.clone()
    };
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    check_credentials(&conn, &username, password).map(|_| ()).map_err(|_| "Your password was not accepted.".to_string())
}

pub fn logout(state: &AppState) -> Result<(), String> {
    let mut g = state.session.lock().map_err(|_| INTERNAL)?;
    if let Some(s) = g.take() {
        audit_state(state, s.user_id, "logout");
    }
    Ok(())
}

/// Unlocks the current locked session; the password must belong to the same user.
pub fn unlock(state: &AppState, password: &str) -> Result<(), String> {
    let (uid, uname) = {
        let g = state.session.lock().map_err(|_| INTERNAL)?;
        let s = g.as_ref().ok_or("Not signed in.")?;
        (s.user_id, s.username.clone())
    };
    {
        let conn = state.db.lock().map_err(|_| INTERNAL)?;
        check_credentials(&conn, &uname, password)?;
        crate::schema::audit(&conn, Some(uid), "unlock", "");
    }
    let mut g = state.session.lock().map_err(|_| INTERNAL)?;
    if let Some(s) = g.as_mut() {
        s.locked = false;
        s.last_activity = Instant::now();
    }
    Ok(())
}

pub fn lock(state: &AppState) -> Result<(), String> {
    let mut g = state.session.lock().map_err(|_| INTERNAL)?;
    if let Some(s) = g.as_mut() {
        s.locked = true;
        audit_state(state, s.user_id, "lock");
    }
    Ok(())
}
