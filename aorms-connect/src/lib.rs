//! Sign in to AORMS with the suite's shared login and pull project details, for the office app.
//!
//! The contract is `docs/esti/AORMS-SUITE-SIGNIN.md` and `O-CoreOp-CLIENT-SPEC.md` in the AORMS repository (version `2026-10-aqc-1`).
//! Everything here is optional: signed out, the office app works as it always has and makes no network calls.
//!
//! Rules this crate keeps:
//! - The password is used once to sign in and is never stored, logged or returned.
//! - The refresh token lives in one OS credential shared with the other suite apps, and is re-read before and written back after
//!   every refresh, because the server rotates it (see `store`).
//! - A busy or failing server never signs anyone out. A refusal of the refresh token does.
//! - Every server answer is untrusted: sizes are capped and parsing never panics.
//! - A server address must be https, or http to this computer.

pub mod store;

use serde_json::{json, Value};
use std::io::Read;
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use store::{Shared, TokenStore};

/// The wire contract this build speaks. A server that reports another version is refused.
pub const CONTRACT: &str = "2026-10-aqc-1";
const MAX_BODY: u64 = 4 * 1024 * 1024;
pub const DEFAULT_BASE: &str = "https://aorms.in";

#[derive(Debug, Clone, PartialEq)]
pub enum ConnectError {
    /// The network or the server could not be reached.
    Offline(String),
    /// The server is busy or failing. Try again later; nothing is signed out.
    Unavailable(String),
    InvalidCredentials,
    /// The account has never signed in to the AORMS web portal.
    NotLinked(String),
    /// The studio has not approved this account yet.
    Pending(String),
    RateLimited { seconds: u64 },
    /// The studio's plan does not include connected mode. `reason` is the server's code.
    NotConnected { reason: String, message: String },
    /// A portal-only account cannot use a desktop app.
    RoleNotAllowed(String),
    /// Another computer signed in to this app.
    SessionReplaced,
    /// There is no sign-in, or the server ended it. Sign in again.
    SignedOut,
    /// The server speaks another version of the contract.
    Contract(String),
    /// The sign-in could not be read or saved on this computer.
    Store(String),
    /// Anything else the server refused, with its message.
    Server { status: u16, message: String },
}

impl std::fmt::Display for ConnectError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ConnectError::Offline(why) => write!(f, "AORMS could not be reached ({why}). Check the internet connection, then try again."),
            ConnectError::Unavailable(_) => write!(f, "AORMS is not available right now. Try again shortly."),
            ConnectError::InvalidCredentials => write!(f, "Invalid login credentials."),
            ConnectError::NotLinked(m) | ConnectError::Pending(m) | ConnectError::RoleNotAllowed(m) | ConnectError::Contract(m) | ConnectError::Store(m) => write!(f, "{m}"),
            ConnectError::RateLimited { seconds } => write!(f, "Too many attempts. Try again in {seconds} seconds."),
            ConnectError::NotConnected { message, .. } => write!(f, "{message}"),
            ConnectError::SessionReplaced => write!(f, "You signed in to this app on another computer. Sign in here to carry on."),
            ConnectError::SignedOut => write!(f, "You are not signed in to AORMS."),
            ConnectError::Server { message, .. } => write!(f, "{message}"),
        }
    }
}

impl std::error::Error for ConnectError {}

type Result<T> = std::result::Result<T, ConnectError>;

#[derive(Debug, Clone, PartialEq)]
pub struct Account {
    pub email: String,
    pub studio: String,
    pub role: String,
    pub plan: String,
}

#[derive(Debug, Clone, PartialEq)]
pub struct ProjectSummary {
    pub project_office_id: String,
    pub reference: String,
    pub title: String,
    pub status: String,
    pub city: String,
}

/// What the portal knows about a project, in the fields this app can use.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ProjectSeed {
    pub project_office_id: String,
    pub name: String,
    pub location: String,
    pub client_name: String,
    pub architect_name: String,
    pub company_name: String,
}

/// What `restore` found on start-up. It never takes another computer's session over.
#[derive(Debug, Clone, PartialEq)]
pub enum Restored {
    SignedOut,
    /// Signed in on this computer through another suite app, but this app has no session yet. Connecting is the user's choice.
    NeedsConnect { email: String },
    Connected(Account),
}

struct Config {
    supabase_url: String,
    supabase_anon_key: String,
}

pub struct Connect {
    base: String,
    app: String,
    label: String,
    agent: ureq::Agent,
    store: Arc<dyn TokenStore>,
    config: Option<Config>,
    access: Option<(String, u64)>,
    session_id: Option<String>,
}

fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

fn safe_address(url: &str) -> bool {
    if url.starts_with("https://") {
        return true;
    }
    ["http://localhost", "http://127.0.0.1", "http://[::1]"].iter().any(|prefix| {
        url.strip_prefix(prefix).is_some_and(|rest| rest.is_empty() || rest.starts_with(':') || rest.starts_with('/'))
    })
}

fn text<'a>(value: &'a Value, key: &str) -> &'a str {
    value.get(key).and_then(Value::as_str).unwrap_or("")
}

fn store_error(error: String) -> ConnectError {
    ConnectError::Store(error)
}

impl Connect {
    /// `app` is this app's name in the suite (its session is kept apart from the other apps'). `label` names this computer.
    pub fn new(base_url: &str, app: &str, label: &str, store: Arc<dyn TokenStore>) -> Result<Self> {
        let base = base_url.trim().trim_end_matches('/').to_string();
        if !safe_address(&base) {
            return Err(ConnectError::Contract("The AORMS address must start with https:// (or http:// to this computer).".into()));
        }
        let mut builder = ureq::AgentBuilder::new().timeout(Duration::from_secs(20)).redirects(0);
        if let Ok(tls) = native_tls::TlsConnector::new() {
            builder = builder.tls_connector(Arc::new(tls));
        }
        Ok(Self { base, app: app.into(), label: label.into(), agent: builder.build(), store, config: None, access: None, session_id: None })
    }

    fn api(&self, path: &str) -> String {
        format!("{}/api/aqc/v1{path}", self.base)
    }

    // ------------------------------------------------------------ the wire

    fn send(&self, method: &str, url: &str, headers: &[(&str, &str)], body: Option<&Value>) -> Result<(u16, Value)> {
        let mut request = self.agent.request(method, url);
        for (key, value) in headers {
            request = request.set(key, value);
        }
        let outcome = match body {
            Some(body) => request.send_json(body.clone()),
            None => request.call(),
        };
        let response = match outcome {
            Ok(response) => response,
            Err(ureq::Error::Status(_, response)) => response,
            Err(ureq::Error::Transport(error)) => return Err(ConnectError::Offline(error.to_string())),
        };
        let status = response.status();
        let mut bytes = Vec::new();
        response.into_reader().take(MAX_BODY + 1).read_to_end(&mut bytes).map_err(|e| ConnectError::Offline(e.to_string()))?;
        if bytes.len() as u64 > MAX_BODY {
            return Err(ConnectError::Server { status, message: "The server's answer was too large.".into() });
        }
        Ok((status, serde_json::from_slice(&bytes).unwrap_or(Value::Null)))
    }

    /// Turns a refusal into the error the user should see.
    fn refusal(status: u16, body: &Value) -> ConnectError {
        let code = [text(body, "error"), text(body, "code")].into_iter().find(|c| !c.is_empty()).unwrap_or("");
        let message = text(body, "message").to_string();
        let message_or = |fallback: &str| if message.is_empty() { fallback.to_string() } else { message.clone() };
        match (status, code) {
            (409, _) | (_, "session_replaced") => ConnectError::SessionReplaced,
            (401, "invalid_credentials") => ConnectError::InvalidCredentials,
            (401, _) => ConnectError::SignedOut,
            (403, "not_linked") => ConnectError::NotLinked(message_or("Sign in to AORMS on the web once with this account, then try again.")),
            (403, "pending") => ConnectError::Pending(message_or("Your studio hasn't approved your access yet.")),
            (403, "not_connected") => ConnectError::NotConnected {
                reason: text(body, "reason").to_string(),
                message: message_or("Your studio's plan does not include connected mode. This app carries on without it."),
            },
            (403, "role_not_allowed") => ConnectError::RoleNotAllowed(message_or("Portal accounts cannot use this app.")),
            (429, _) => ConnectError::RateLimited { seconds: body.get("retryAfterSeconds").and_then(Value::as_u64).unwrap_or(30) },
            (500..=599, _) => ConnectError::Unavailable(message),
            _ => ConnectError::Server { status, message: message_or("AORMS refused the request.") },
        }
    }

    fn load_config(&mut self) -> Result<()> {
        if self.config.is_some() {
            return Ok(());
        }
        let (status, body) = self.send("GET", &self.api("/config"), &[], None)?;
        if status != 200 {
            return Err(Self::refusal(status, &body));
        }
        if text(&body, "contract") != CONTRACT {
            return Err(ConnectError::Contract("This version of the app does not match AORMS. Update the app.".into()));
        }
        let supabase_url = text(&body, "supabaseUrl").trim_end_matches('/').to_string();
        if !safe_address(&supabase_url) || text(&body, "supabaseAnonKey").is_empty() {
            return Err(ConnectError::Contract("AORMS returned sign-in settings this app cannot use.".into()));
        }
        self.config = Some(Config { supabase_url, supabase_anon_key: text(&body, "supabaseAnonKey").to_string() });
        Ok(())
    }

    // ------------------------------------------------------------ tokens

    /// A usable access token, refreshing first when it is about to expire. Follows the suite's rotation rules.
    fn access_token(&mut self) -> Result<String> {
        if let Some((token, expires)) = &self.access {
            if *expires > now() + 60 {
                return Ok(token.clone());
            }
        }
        self.refresh()
    }

    fn refresh(&mut self) -> Result<String> {
        self.load_config()?;
        let mut shared = self.store.read_shared().map_err(store_error)?.ok_or(ConnectError::SignedOut)?;
        for attempt in 0..2 {
            let (url, key) = {
                let config = self.config.as_ref().ok_or(ConnectError::SignedOut)?;
                (format!("{}/auth/v1/token?grant_type=refresh_token", config.supabase_url), config.supabase_anon_key.clone())
            };
            let (status, body) = self.send("POST", &url, &[("apikey", &key)], Some(&json!({ "refresh_token": shared.refresh_token })))?;
            match status {
                200 => {
                    let access = text(&body, "access_token").to_string();
                    let rotated = text(&body, "refresh_token").to_string();
                    if access.is_empty() || rotated.is_empty() {
                        return Err(ConnectError::Unavailable("The sign-in answer was incomplete.".into()));
                    }
                    let expires = body.get("expires_at").and_then(Value::as_u64).unwrap_or_else(|| now() + body.get("expires_in").and_then(Value::as_u64).unwrap_or(3600));
                    // Written back at once: another app that refreshes next needs this token, not the one just used.
                    self.store.write_shared(&Shared { v: 1, refresh_token: rotated, email: shared.email.clone(), updated_at: now() }).map_err(store_error)?;
                    self.access = Some((access.clone(), expires));
                    return Ok(access);
                }
                400 | 401 => {
                    // Refused: another app may have rotated the token while this one was waiting. Look once, retry once.
                    let latest = self.store.read_shared().map_err(store_error)?;
                    match latest {
                        Some(latest) if attempt == 0 && latest.refresh_token != shared.refresh_token => shared = latest,
                        _ => {
                            self.access = None;
                            return Err(ConnectError::SignedOut);
                        }
                    }
                }
                _ => return Err(Self::refusal(status, &body)),
            }
        }
        Err(ConnectError::SignedOut)
    }

    /// A call to the portal with the sign-in and this app's session, refreshing once if the access token has gone stale.
    fn call(&mut self, method: &str, path: &str, body: Option<&Value>) -> Result<Value> {
        let session = match &self.session_id {
            Some(id) => id.clone(),
            None => self.store.read_session(&self.app).map_err(store_error)?.ok_or(ConnectError::SignedOut)?,
        };
        for attempt in 0..2 {
            let token = self.access_token()?;
            let bearer = format!("Bearer {token}");
            let (status, value) = self.send(method, &self.api(path), &[("Authorization", &bearer), ("x-aqc-session", &session)], body)?;
            if status == 401 && attempt == 0 {
                self.access = None;
                continue;
            }
            if (200..300).contains(&status) {
                self.session_id = Some(session);
                return Ok(value);
            }
            return Err(Self::refusal(status, &value));
        }
        Err(ConnectError::SignedOut)
    }

    // ------------------------------------------------------------ sign in and out

    /// Signs in with the AORMS password and starts this app's session. The password is used once and not kept.
    pub fn sign_in(&mut self, email: &str, password: &str) -> Result<Account> {
        self.load_config()?;
        let (status, body) = self.send("POST", &self.api("/auth/login"), &[], Some(&json!({ "email": email.trim(), "password": password })))?;
        if status != 200 {
            return Err(Self::refusal(status, &body));
        }
        let (access, refresh) = (text(&body, "accessToken").to_string(), text(&body, "refreshToken").to_string());
        if access.is_empty() || refresh.is_empty() {
            return Err(ConnectError::Unavailable("The sign-in answer was incomplete.".into()));
        }
        let expires = body.get("expiresAt").and_then(Value::as_u64).unwrap_or_else(|| now() + 3600);
        let address = if text(&body, "email").is_empty() { email.trim().to_string() } else { text(&body, "email").to_string() };
        self.store.write_shared(&Shared { v: 1, refresh_token: refresh, email: address, updated_at: now() }).map_err(store_error)?;
        self.access = Some((access, expires));
        self.start_session()
    }

    /// Starts (or takes back) this app's session using the sign-in already on this computer. Never silent: the caller asks the user first.
    pub fn connect(&mut self) -> Result<Account> {
        self.access_token()?;
        self.start_session()
    }

    fn start_session(&mut self) -> Result<Account> {
        let token = self.access_token()?;
        let bearer = format!("Bearer {token}");
        let (status, body) = self.send("POST", &self.api("/session"), &[("Authorization", &bearer)], Some(&json!({ "clientLabel": self.label, "app": self.app })))?;
        if status != 200 && status != 201 {
            return Err(Self::refusal(status, &body));
        }
        let id = text(&body, "sessionId").to_string();
        if id.is_empty() {
            return Err(ConnectError::Unavailable("The session answer was incomplete.".into()));
        }
        self.store.write_session(&self.app, &id).map_err(store_error)?;
        self.session_id = Some(id);
        self.account_from(&body)
    }

    fn account_from(&self, body: &Value) -> Result<Account> {
        let email = self.store.read_shared().map_err(store_error)?.map(|s| s.email).unwrap_or_default();
        Ok(Account {
            email,
            studio: body.get("studio").map(|s| text(s, "name").to_string()).unwrap_or_default(),
            role: text(body, "role").to_string(),
            plan: body.get("entitlement").map(|e| text(e, "plan").to_string()).unwrap_or_default(),
        })
    }

    /// Looks for an existing sign-in when the app starts. Never signs in, and never takes a session over.
    pub fn restore(&mut self) -> Result<Restored> {
        let Some(shared) = self.store.read_shared().map_err(store_error)? else {
            return Ok(Restored::SignedOut);
        };
        if self.store.read_session(&self.app).map_err(store_error)?.is_none() {
            return Ok(Restored::NeedsConnect { email: shared.email });
        }
        match self.call("GET", "/session", None) {
            Ok(body) => Ok(Restored::Connected(self.account_from(&body)?)),
            Err(ConnectError::SignedOut) => Ok(Restored::SignedOut),
            Err(other) => Err(other),
        }
    }

    /// Ends the shared sign-in on this computer. The other suite apps find it empty and ask to sign in.
    pub fn sign_out(&mut self) -> Result<()> {
        self.access = None;
        self.session_id = None;
        self.store.clear_session(&self.app).map_err(store_error)?;
        self.store.clear_shared().map_err(store_error)
    }

    // ------------------------------------------------------------ projects

    pub fn projects(&mut self) -> Result<Vec<ProjectSummary>> {
        let body = self.call("GET", "/projects", None)?;
        Ok(body
            .get("projects")
            .and_then(Value::as_array)
            .map(|list| {
                list.iter()
                    .map(|p| ProjectSummary {
                        project_office_id: text(p, "projectOfficeId").to_string(),
                        reference: text(p, "ref").to_string(),
                        title: text(p, "title").to_string(),
                        status: text(p, "status").to_string(),
                        city: text(p, "city").to_string(),
                    })
                    .filter(|p| !p.project_office_id.is_empty() && !p.title.is_empty())
                    .collect()
            })
            .unwrap_or_default())
    }

    /// The project's details, as the portal holds them.
    pub fn seed(&mut self, project_office_id: &str) -> Result<ProjectSeed> {
        if project_office_id.is_empty() || project_office_id.len() > 64 || !project_office_id.chars().all(|c| c.is_ascii_hexdigit() || c == '-') {
            return Err(ConnectError::Server { status: 400, message: "That is not a project id.".into() });
        }
        let body = self.call("GET", &format!("/seed/{project_office_id}"), None)?;
        let project = body.get("project").cloned().unwrap_or(Value::Null);
        Ok(ProjectSeed {
            project_office_id: project_office_id.to_string(),
            name: text(&project, "name").to_string(),
            location: text(&project, "location").to_string(),
            client_name: text(&project, "client_name").to_string(),
            architect_name: text(&project, "prepared_by_name").to_string(),
            company_name: text(&project, "company_name").to_string(),
        })
    }
}

#[cfg(test)]
mod tests;
