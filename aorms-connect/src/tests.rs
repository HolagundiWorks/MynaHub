//! Tests against a small stand-in for the AORMS server, listening on this computer.

use super::*;
use crate::store::MemoryStore;
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::net::TcpListener;
use std::sync::Mutex;

#[derive(Clone, Debug)]
struct Req {
    method: String,
    path: String,
    headers: HashMap<String, String>,
    body: Value,
}

struct State {
    base: String,
    contract: String,
    login: (u16, Value),
    session: (u16, Value),
    session_get: (u16, Value),
    refresh_status: u16,
    valid_refresh: String,
    issued: u32,
    log: Vec<Req>,
    expires_in: i64,
}

fn route(req: &Req, st: &mut State) -> (u16, Value) {
    match (req.method.as_str(), req.path.split('?').next().unwrap_or("")) {
        ("GET", "/api/aqc/v1/config") => (200, json!({ "contract": st.contract, "supabaseUrl": st.base, "supabaseAnonKey": "anon-key", "endpoints": {}, "limits": {} })),
        ("POST", "/api/aqc/v1/auth/login") => st.login.clone(),
        ("POST", "/api/aqc/v1/session") => st.session.clone(),
        ("GET", "/api/aqc/v1/session") => st.session_get.clone(),
        ("POST", "/auth/v1/token") => {
            if st.refresh_status != 200 {
                return (st.refresh_status, json!({ "error": "unavailable" }));
            }
            if req.body["refresh_token"] != st.valid_refresh {
                return (400, json!({ "error": "invalid_grant" }));
            }
            st.issued += 1;
            st.valid_refresh = format!("refresh-{}", st.issued);
            (200, json!({ "access_token": format!("access-{}", st.issued), "refresh_token": st.valid_refresh, "expires_in": st.expires_in }))
        }
        ("GET", "/api/aqc/v1/projects") => (200, json!({ "projects": [
            { "projectOfficeId": "11111111-1111-1111-1111-111111111111", "ref": "P-1", "title": "Villa at Baner", "status": "ACTIVE", "city": "Pune", "aqc": null },
            { "projectOfficeId": "", "ref": "bad", "title": "No id" }
        ] })),
        ("GET", p) if p.starts_with("/api/aqc/v1/seed/") => (200, json!({ "project": {
            "name": "Villa at Baner", "location": "Plot 4, Baner, Pune", "client_name": "Rao family", "prepared_by_name": "A. Architect", "company_name": "Studio Co", "hub_project_id": "x"
        }, "parties": {} })),
        _ => (404, json!({ "error": "not_found" })),
    }
}

struct Server {
    base: String,
    state: Arc<Mutex<State>>,
}

impl Server {
    fn start() -> Server {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let base = format!("http://127.0.0.1:{}", listener.local_addr().unwrap().port());
        let state = Arc::new(Mutex::new(State {
            base: base.clone(),
            contract: CONTRACT.into(),
            login: (200, json!({ "accessToken": "access-0", "refreshToken": "refresh-0", "expiresAt": now() + 3600, "userId": "u1", "email": "owner@studio.in" })),
            session: (200, json!({ "sessionId": "session-1", "account": {}, "studio": { "name": "Studio Co" }, "role": "OWNER", "capabilities": {}, "entitlement": { "connected": true, "plan": "STUDIO" } })),
            session_get: (200, json!({ "sessionId": "session-1", "studio": { "name": "Studio Co" }, "role": "OWNER", "entitlement": { "plan": "STUDIO" } })),
            refresh_status: 200,
            valid_refresh: "refresh-0".into(),
            issued: 0,
            log: Vec::new(),
            expires_in: 3600,
        }));
        let shared = state.clone();
        std::thread::spawn(move || {
            for stream in listener.incoming() {
                let Ok(mut stream) = stream else { continue };
                let mut reader = BufReader::new(stream.try_clone().unwrap());
                let mut line = String::new();
                if reader.read_line(&mut line).is_err() {
                    continue;
                }
                let mut parts = line.split_whitespace();
                let (method, path) = (parts.next().unwrap_or("").to_string(), parts.next().unwrap_or("").to_string());
                let mut headers = HashMap::new();
                loop {
                    let mut h = String::new();
                    if reader.read_line(&mut h).is_err() || h.trim().is_empty() {
                        break;
                    }
                    if let Some((k, v)) = h.split_once(':') {
                        headers.insert(k.trim().to_ascii_lowercase(), v.trim().to_string());
                    }
                }
                let length: usize = headers.get("content-length").and_then(|v| v.parse().ok()).unwrap_or(0);
                let mut body = vec![0u8; length];
                let _ = reader.read_exact(&mut body);
                let req = Req { method, path, headers, body: serde_json::from_slice(&body).unwrap_or(Value::Null) };
                let (status, reply) = {
                    let mut st = shared.lock().unwrap();
                    st.log.push(req.clone());
                    route(&req, &mut st)
                };
                let text = if reply == json!("garbage") { "<<not json>>".to_string() } else { reply.to_string() };
                let _ = write!(stream, "HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{text}", text.len());
            }
        });
        Server { base, state }
    }

    fn client(&self, store: Arc<dyn TokenStore>) -> Connect {
        Connect::new(&self.base, "aos", "AOS 0.1 test", store).unwrap()
    }

    fn calls(&self, method: &str, path: &str) -> Vec<Req> {
        self.state.lock().unwrap().log.iter().filter(|r| r.method == method && r.path.starts_with(path)).cloned().collect()
    }
}

fn stored() -> Arc<MemoryStore> {
    Arc::new(MemoryStore::new())
}

#[test]
fn signing_in_starts_this_apps_own_session_and_keeps_no_password() {
    let server = Server::start();
    let store = stored();
    let mut connect = server.client(store.clone());
    let account = connect.sign_in("owner@studio.in", "a secret password").unwrap();
    assert_eq!((account.studio.as_str(), account.role.as_str(), account.plan.as_str(), account.email.as_str()), ("Studio Co", "OWNER", "STUDIO", "owner@studio.in"));
    let session = &server.calls("POST", "/api/aqc/v1/session")[0];
    assert_eq!(session.body["app"], "aos", "the session belongs to this app, so it does not replace another app's");
    assert_eq!(session.headers["authorization"], "Bearer access-0");
    assert_eq!(store.read_shared().unwrap().unwrap().refresh_token, "refresh-0");
    assert_eq!(store.read_session("aos").unwrap().as_deref(), Some("session-1"));
    assert_eq!(store.read_session("aad").unwrap(), None);
    let everything = format!("{:?}{:?}", store.read_shared().unwrap(), store.read_session("aos").unwrap());
    assert!(!everything.contains("a secret password"));

    let projects = connect.projects().unwrap();
    assert_eq!(projects.len(), 1, "a project without an id is left out");
    assert_eq!((projects[0].title.as_str(), projects[0].city.as_str()), ("Villa at Baner", "Pune"));
    let call = &server.calls("GET", "/api/aqc/v1/projects")[0];
    assert_eq!((call.headers["authorization"].as_str(), call.headers["x-aqc-session"].as_str()), ("Bearer access-0", "session-1"));
    let seed = connect.seed("11111111-1111-1111-1111-111111111111").unwrap();
    assert_eq!((seed.name.as_str(), seed.location.as_str(), seed.client_name.as_str(), seed.architect_name.as_str()), ("Villa at Baner", "Plot 4, Baner, Pune", "Rao family", "A. Architect"));
    assert!(connect.seed("../etc/passwd").is_err(), "a project id is checked before it goes in a path");
}

#[test]
fn every_refusal_is_explained_in_words() {
    let server = Server::start();
    let cases: [(u16, Value, ConnectError); 6] = [
        (401, json!({ "error": "invalid_credentials" }), ConnectError::InvalidCredentials),
        (403, json!({ "error": "not_linked" }), ConnectError::NotLinked("Sign in to AORMS on the web once with this account, then try again.".into())),
        (403, json!({ "error": "pending" }), ConnectError::Pending("Your studio hasn't approved your access yet.".into())),
        (429, json!({ "error": "rate_limited", "retryAfterSeconds": 45 }), ConnectError::RateLimited { seconds: 45 }),
        (503, json!({ "error": "unavailable" }), ConnectError::Unavailable(String::new())),
        (403, json!("garbage"), ConnectError::Server { status: 403, message: "AORMS refused the request.".into() }),
    ];
    for (status, body, expected) in cases {
        server.state.lock().unwrap().login = (status, body);
        let mut connect = server.client(stored());
        assert_eq!(connect.sign_in("a@b.in", "pw").unwrap_err(), expected);
    }
    server.state.lock().unwrap().login = (200, json!({ "accessToken": "a", "refreshToken": "r", "expiresAt": now() + 3600, "email": "a@b.in" }));
    for (status, body) in [
        (403, json!({ "error": "not_connected", "reason": "PLAN_NOT_CONNECTED", "message": "Your studio is on the free plan." })),
        (403, json!({ "error": "role_not_allowed" })),
    ] {
        server.state.lock().unwrap().session = (status, body);
        let mut connect = server.client(stored());
        let error = connect.sign_in("a@b.in", "pw").unwrap_err();
        match error {
            ConnectError::NotConnected { reason, message } => assert_eq!((reason.as_str(), message.as_str()), ("PLAN_NOT_CONNECTED", "Your studio is on the free plan.")),
            ConnectError::RoleNotAllowed(message) => assert!(message.contains("Portal accounts")),
            other => panic!("unexpected {other:?}"),
        }
    }
}

#[test]
fn the_address_and_the_contract_are_checked() {
    assert!(Connect::new("http://example.com", "aos", "x", stored()).is_err(), "plain http to another computer is refused");
    assert!(Connect::new("http://127.0.0.1.evil.com", "aos", "x", stored()).is_err());
    assert!(Connect::new("ftp://aorms.in", "aos", "x", stored()).is_err());
    assert!(Connect::new("https://aorms.in/", "aos", "x", stored()).is_ok());
    assert!(Connect::new("http://localhost:3000", "aos", "x", stored()).is_ok());
    let server = Server::start();
    server.state.lock().unwrap().contract = "2030-01-other".into();
    let mut connect = server.client(stored());
    assert!(matches!(connect.sign_in("a@b.in", "pw").unwrap_err(), ConnectError::Contract(_)));
    assert!(server.calls("POST", "/api/aqc/v1/auth/login").is_empty(), "no password is sent to a server that speaks another contract");
}

#[test]
fn an_expired_access_token_is_refreshed_and_the_rotated_token_is_written_back() {
    let server = Server::start();
    server.state.lock().unwrap().login = (200, json!({ "accessToken": "access-0", "refreshToken": "refresh-0", "expiresAt": now() - 10, "email": "owner@studio.in" }));
    let store = stored();
    let mut connect = server.client(store.clone());
    connect.sign_in("owner@studio.in", "pw").unwrap();
    assert_eq!(server.calls("POST", "/auth/v1/token").len(), 1, "the session start already needed a fresh token");
    assert_eq!(store.read_shared().unwrap().unwrap().refresh_token, "refresh-1");
    connect.projects().unwrap();
    assert_eq!(store.read_shared().unwrap().unwrap().refresh_token, "refresh-1", "a token that is still good is not refreshed again");
    let refresh = &server.calls("POST", "/auth/v1/token")[0];
    assert_eq!(refresh.headers["apikey"], "anon-key");
    assert_eq!(refresh.body["refresh_token"], "refresh-0");
}

/// A store whose first read is stale, as when another app rotates the token while this one is about to refresh.
struct Racing {
    inner: MemoryStore,
    stale: Mutex<Option<Shared>>,
}

impl TokenStore for Racing {
    fn read_shared(&self) -> std::result::Result<Option<Shared>, String> {
        if let Some(stale) = self.stale.lock().unwrap().take() {
            return Ok(Some(stale));
        }
        self.inner.read_shared()
    }
    fn write_shared(&self, shared: &Shared) -> std::result::Result<(), String> {
        self.inner.write_shared(shared)
    }
    fn clear_shared(&self) -> std::result::Result<(), String> {
        self.inner.clear_shared()
    }
    fn read_session(&self, app: &str) -> std::result::Result<Option<String>, String> {
        self.inner.read_session(app)
    }
    fn write_session(&self, app: &str, id: &str) -> std::result::Result<(), String> {
        self.inner.write_session(app, id)
    }
    fn clear_session(&self, app: &str) -> std::result::Result<(), String> {
        self.inner.clear_session(app)
    }
}

#[test]
fn a_refused_token_is_retried_once_with_the_one_another_app_wrote() {
    let server = Server::start();
    server.state.lock().unwrap().valid_refresh = "refresh-other-app".into();
    let racing = Arc::new(Racing { inner: MemoryStore::new(), stale: Mutex::new(Some(Shared { v: 1, refresh_token: "refresh-old".into(), email: "a@b.in".into(), updated_at: 1 })) });
    racing.inner.write_shared(&Shared { v: 1, refresh_token: "refresh-other-app".into(), email: "a@b.in".into(), updated_at: 2 }).unwrap();
    racing.inner.write_session("aos", "session-1").unwrap();
    let mut connect = server.client(racing.clone());
    connect.projects().unwrap();
    let tokens: Vec<Value> = server.calls("POST", "/auth/v1/token").iter().map(|r| r.body["refresh_token"].clone()).collect();
    assert_eq!(tokens, vec![json!("refresh-old"), json!("refresh-other-app")]);
}

#[test]
fn a_dead_sign_in_ends_but_a_busy_server_does_not_sign_anyone_out() {
    let server = Server::start();
    let store = stored();
    store.write_shared(&Shared { v: 1, refresh_token: "refresh-dead".into(), email: "a@b.in".into(), updated_at: 1 }).unwrap();
    store.write_session("aos", "session-1").unwrap();
    let mut connect = server.client(store.clone());
    assert_eq!(connect.projects().unwrap_err(), ConnectError::SignedOut, "the same token on a second look means the sign-in has ended");
    assert_eq!(server.calls("POST", "/auth/v1/token").len(), 1);

    server.state.lock().unwrap().refresh_status = 503;
    let mut busy = server.client(store.clone());
    assert!(matches!(busy.projects().unwrap_err(), ConnectError::Unavailable(_)));
    assert!(store.read_shared().unwrap().is_some(), "the saved sign-in is kept");
    assert_eq!(store.read_session("aos").unwrap().as_deref(), Some("session-1"));
}

#[test]
fn starting_up_finds_an_existing_sign_in_without_taking_a_session_over() {
    let server = Server::start();
    let store = stored();
    assert_eq!(server.client(store.clone()).restore().unwrap(), Restored::SignedOut);
    store.write_shared(&Shared { v: 1, refresh_token: "refresh-0".into(), email: "owner@studio.in".into(), updated_at: 1 }).unwrap();
    assert_eq!(server.client(store.clone()).restore().unwrap(), Restored::NeedsConnect { email: "owner@studio.in".into() });
    assert!(server.calls("POST", "/api/aqc/v1/session").is_empty(), "no session is started without the user asking");

    let mut connect = server.client(store.clone());
    let account = connect.connect().unwrap();
    assert_eq!(account.studio, "Studio Co");
    let mut later = server.client(store.clone());
    assert!(matches!(later.restore().unwrap(), Restored::Connected(a) if a.plan == "STUDIO"));

    server.state.lock().unwrap().session_get = (409, json!({ "error": "session_replaced" }));
    assert_eq!(server.client(store.clone()).restore().unwrap_err(), ConnectError::SessionReplaced);
    assert!(ConnectError::SessionReplaced.to_string().contains("another computer"));
}

#[test]
fn signing_out_clears_the_shared_sign_in_and_this_apps_session() {
    let server = Server::start();
    let store = stored();
    let mut connect = server.client(store.clone());
    connect.sign_in("owner@studio.in", "pw").unwrap();
    store.write_session("aad", "other-apps-session").unwrap();
    connect.sign_out().unwrap();
    assert_eq!(store.read_shared().unwrap(), None);
    assert_eq!(store.read_session("aos").unwrap(), None);
    assert_eq!(store.read_session("aad").unwrap().as_deref(), Some("other-apps-session"), "another app's session entry is not touched");
    assert_eq!(connect.projects().unwrap_err(), ConnectError::SignedOut);
}
