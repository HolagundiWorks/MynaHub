//! Signing the suite's apps in as the same person.
//!
//! The suite app (OSAS) signs a person in once. For each app it is about to launch it asks for a **ticket**: 32 random bytes that work
//! once, for two minutes, and sign in the person who asked, with that person's role. The app redeems the ticket in its own session and
//! is signed in without the password ever being passed on. Only a hash of a ticket is stored. See `docs/suite-hub.md`.

use crate::auth::SessionInfo;
use crate::hash::sha256_hex;
use crate::workspace::{self, AppState, INTERNAL};
use rusqlite::{params, Connection};
use serde::Serialize;

/// How long a ticket can be redeemed after it is issued.
pub const TICKET_SECS: i64 = 120;

#[derive(Debug, Serialize)]
pub struct Ticket {
    pub ticket: String,
    pub expires_in: i64,
    pub username: String,
}

fn now_secs() -> i64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0)
}

fn clean_app(app: &str) -> Result<&str, String> {
    let app = app.trim();
    let ok = !app.is_empty() && app.len() <= 40 && app.chars().all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_' || c == '.');
    if ok {
        Ok(app)
    } else {
        Err("Name the app the ticket is for (letters, digits, dots, dashes).".into())
    }
}

/// A ticket for `app`, for the person signed in to this session. The session must be signed in and unlocked.
pub fn issue(state: &AppState, app: &str) -> Result<Ticket, String> {
    let app = clean_app(app)?;
    let user_id = workspace::require(state, crate::auth::Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let username: String = conn.query_row("SELECT username FROM users WHERE id = ?1", params![user_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    let secret: String = conn.query_row("SELECT lower(hex(randomblob(32)))", [], |r| r.get(0)).map_err(|e| e.to_string())?;
    let now = now_secs();
    conn.execute("DELETE FROM suite_tickets WHERE expires_at < ?1 OR used_at IS NOT NULL", params![now - 3600]).map_err(|e| e.to_string())?;
    conn.execute(
        "INSERT INTO suite_tickets (user_id, secret_hash, app, created_at, expires_at) VALUES (?1, ?2, ?3, ?4, ?5)",
        params![user_id, sha256_hex(secret.as_bytes()), app, now, now + TICKET_SECS],
    )
    .map_err(|e| e.to_string())?;
    crate::schema::audit(&conn, Some(user_id), "suite_ticket_issued", app);
    Ok(Ticket { ticket: secret, expires_in: TICKET_SECS, username })
}

const REFUSED: &str = "This sign-in has expired. Sign in again.";

fn take(conn: &Connection, secret: &str) -> Result<(i64, String, String), String> {
    let hash = sha256_hex(secret.trim().as_bytes());
    let now = now_secs();
    let row: Option<(i64, i64, String)> = conn
        .query_row("SELECT id, user_id, app FROM suite_tickets WHERE secret_hash = ?1 AND used_at IS NULL AND expires_at >= ?2", params![hash, now], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
        .ok();
    let Some((id, user_id, app)) = row else { return Err(REFUSED.into()) };
    // Marking it used and checking that this call is the one that did makes a ticket work exactly once, even if two apps race.
    let marked = conn.execute("UPDATE suite_tickets SET used_at = ?1 WHERE id = ?2 AND used_at IS NULL", params![now, id]).map_err(|e| e.to_string())?;
    if marked != 1 {
        return Err(REFUSED.into());
    }
    let (username, role, disabled): (String, String, i64) = conn
        .query_row("SELECT username, role, disabled FROM users WHERE id = ?1", params![user_id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
        .map_err(|_| REFUSED.to_string())?;
    if disabled != 0 {
        return Err(REFUSED.into());
    }
    Ok((user_id, format!("{username}\n{role}"), app))
}

/// Signs this session in as the person the ticket was issued to. A company must be open.
pub fn redeem(state: &AppState, secret: &str) -> Result<SessionInfo, String> {
    if crate::company::active_summary(state).is_none() {
        return Err("Open or create a company first.".into());
    }
    let (user_id, who, _app) = {
        let conn = state.db.lock().map_err(|_| INTERNAL)?;
        let taken = take(&conn, secret)?;
        crate::schema::audit(&conn, Some(taken.0), "login_ticket_ok", &taken.2);
        taken
    };
    let (username, role) = who.split_once('\n').unwrap_or((who.as_str(), ""));
    workspace::start_session(state, user_id, username.to_string(), role)?;
    Ok(SessionInfo { username: username.to_string(), role: role.to_string() })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{company, workspace};

    struct Temp(std::path::PathBuf);
    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    /// A company with a signed-in owner (the suite app's session), and a second, empty session on the same file (the app).
    fn two_sessions(tag: &str) -> (Temp, AppState, AppState) {
        let dir = std::env::temp_dir().join(format!("suite-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let osas = AppState::new(company::placeholder(), dir.clone(), crate::schema::migrate);
        company::create_company(&osas, &dir.join("Studio").to_string_lossy(), "Studio", "owner", "correct horse battery", "correct horse battery").unwrap();
        workspace::login(&osas, "owner", "correct horse battery").unwrap();
        let app = AppState::new(company::placeholder(), dir.join("app"), crate::schema::migrate);
        company::open_company(&app, &dir.join("Studio.osas").to_string_lossy(), "", "").unwrap();
        (Temp(dir), osas, app)
    }

    #[test]
    fn a_ticket_signs_the_app_in_as_the_same_person_once() {
        let (_dir, osas, app) = two_sessions("once");
        let ticket = issue(&osas, "O-CoreOp").unwrap();
        assert_eq!((ticket.ticket.len(), ticket.expires_in, ticket.username.as_str()), (64, 120, "owner"));
        assert_eq!(workspace::require(&app, crate::auth::Permission::View).unwrap_err(), "Not signed in.");
        let who = redeem(&app, &ticket.ticket).unwrap();
        assert_eq!((who.username.as_str(), who.role.as_str()), ("owner", "owner"));
        assert!(workspace::require(&app, crate::auth::Permission::ManageSettings).is_ok(), "the app has the owner's role");
        assert_eq!(redeem(&app, &ticket.ticket).err().unwrap(), REFUSED, "a ticket works once");
    }

    #[test]
    fn only_a_signed_in_session_can_ask_and_only_a_hash_is_kept() {
        let (_dir, osas, app) = two_sessions("ask");
        assert_eq!(issue(&app, "O-Studio").unwrap_err(), "Not signed in.");
        let ticket = issue(&osas, "O-Studio").unwrap();
        let stored: String = osas.db.lock().unwrap().query_row("SELECT secret_hash FROM suite_tickets", [], |r| r.get(0)).unwrap();
        assert_ne!(stored, ticket.ticket);
        assert_eq!(stored, sha256_hex(ticket.ticket.as_bytes()));
        assert!(issue(&osas, "bad app!").is_err());
        assert!(issue(&osas, "").is_err());
    }

    #[test]
    fn an_expired_unknown_or_blocked_ticket_is_refused() {
        let (_dir, osas, app) = two_sessions("refuse");
        assert_eq!(redeem(&app, "not a ticket").err().unwrap(), REFUSED);
        let old = issue(&osas, "O-Studio").unwrap();
        osas.db.lock().unwrap().execute("UPDATE suite_tickets SET expires_at = expires_at - 1000", []).unwrap();
        assert_eq!(redeem(&app, &old.ticket).err().unwrap(), REFUSED, "two minutes is the limit");
        let fresh = issue(&osas, "O-Studio").unwrap();
        osas.db.lock().unwrap().execute("UPDATE users SET disabled = 1", []).unwrap();
        assert_eq!(redeem(&app, &fresh.ticket).err().unwrap(), REFUSED, "a disabled person cannot be signed in by a ticket");
        assert!(workspace::require(&app, crate::auth::Permission::View).is_err());
    }

    #[test]
    fn a_company_must_be_open_to_redeem_and_both_steps_are_audited() {
        let (_dir, osas, app) = two_sessions("audit");
        let ticket = issue(&osas, "O-CoreOp").unwrap();
        let bare = AppState::new(company::placeholder(), std::env::temp_dir(), crate::schema::migrate);
        assert_eq!(redeem(&bare, &ticket.ticket).err().unwrap(), "Open or create a company first.");
        redeem(&app, &ticket.ticket).unwrap();
        let events: Vec<String> = {
            let conn = osas.db.lock().unwrap();
            let mut stmt = conn.prepare("SELECT event || ':' || detail FROM audit_log WHERE event IN ('suite_ticket_issued', 'login_ticket_ok') ORDER BY id").unwrap();
            stmt.query_map([], |r| r.get(0)).unwrap().map(|r| r.unwrap()).collect()
        };
        assert_eq!(events, ["suite_ticket_issued:O-CoreOp", "login_ticket_ok:O-CoreOp"]);
    }
}
