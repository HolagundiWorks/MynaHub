//! The pieces of a C interface that every app shares, so a native shell (WinUI) can drive an app's Rust engine.
//!
//! Ownership: strings passed in are borrowed for the call. A `CResult`'s `data` and `error` belong to the caller after return and
//! are released with `free_result`, including on failure. A session handle is opaque; `0` is never valid; destroying a session closes
//! its company file and releases the lock. Panics are caught and returned as an internal error.
//!
//! Status codes: 0 ok, 1 domain error, 2 bad request, 3 invalid handle, 4 internal.
//! An app exports its own `extern "C"` functions (for example `office_call`) and passes its operations to `Registry::call`.

use crate::workspace::{AppState, Prepare};
use serde::Serialize;
use serde_json::Value;
use std::collections::HashMap;
use std::ffi::{c_char, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

pub const OK: i32 = 0;
pub const ERR_DOMAIN: i32 = 1;
pub const ERR_REQUEST: i32 = 2;
pub const ERR_HANDLE: i32 = 3;
pub const ERR_INTERNAL: i32 = 4;

#[repr(C)]
pub struct CResult {
    pub status: i32,
    pub data: *mut u8,
    pub data_len: usize,
    pub error: *mut c_char,
}

pub enum CallError {
    Domain(String),
    BadRequest(String),
}

impl From<String> for CallError {
    fn from(message: String) -> Self {
        CallError::Domain(message)
    }
}

/// An app's own operations. It returns the JSON (or other) bytes, or `BadRequest` for an operation it does not know.
pub type AppDispatch = fn(&AppState, &str, &Value) -> Result<Vec<u8>, CallError>;

pub fn ok(bytes: Vec<u8>) -> CResult {
    let (data, data_len) = if bytes.is_empty() {
        (std::ptr::null_mut(), 0)
    } else {
        let boxed = bytes.into_boxed_slice();
        let len = boxed.len();
        (Box::into_raw(boxed) as *mut u8, len)
    };
    CResult { status: OK, data, data_len, error: std::ptr::null_mut() }
}

pub fn fail(status: i32, message: &str) -> CResult {
    let cleaned: String = message.chars().filter(|c| *c != '\0').collect();
    let error = CString::new(cleaned).unwrap_or_else(|_| CString::new("Internal error.").expect("static error")).into_raw();
    CResult { status, data: std::ptr::null_mut(), data_len: 0, error }
}

/// # Safety
/// `result` must have come from this module and must not have been freed already.
pub unsafe fn free_result(result: CResult) {
    unsafe {
        if !result.data.is_null() && result.data_len > 0 {
            drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(result.data, result.data_len)));
        }
        if !result.error.is_null() {
            drop(CString::from_raw(result.error));
        }
    }
}

/// # Safety
/// `ptr` must be null or point to a NUL-terminated string that outlives the call.
pub unsafe fn cstr<'a>(ptr: *const c_char) -> Result<&'a str, String> {
    if ptr.is_null() {
        return Err("A required string was missing.".into());
    }
    unsafe { CStr::from_ptr(ptr) }.to_str().map_err(|_| "Text must be UTF-8.".into())
}

/// Runs `f`, turning a panic into an internal error so it never crosses the C boundary.
pub fn guard(f: impl FnOnce() -> CResult) -> CResult {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or_else(|_| fail(ERR_INTERNAL, "Internal error."))
}

pub fn field<'a>(args: &'a Value, key: &str) -> &'a str {
    args.get(key).and_then(Value::as_str).unwrap_or("")
}

/// One argument decoded into the type the operation wants; a missing key decodes as null (so `Option` is `None`).
pub fn arg<T: serde::de::DeserializeOwned>(args: &Value, key: &str) -> Result<T, CallError> {
    let value = args.get(key).cloned().unwrap_or(Value::Null);
    serde_json::from_value(value).map_err(|error| CallError::BadRequest(format!("{key} is invalid: {error}")))
}

pub fn json<T: Serialize>(value: &T) -> Result<Vec<u8>, CallError> {
    serde_json::to_vec(value).map_err(|_| CallError::Domain("Internal error.".into()))
}

#[derive(Serialize)]
struct Created {
    summary: crate::company::CompanySummary,
    recovery: String,
}

/// The operations every app offers: opening and creating a company file, signing in, locking and signing out.
fn common(state: &AppState, op: &str, args: &Value) -> Result<Option<Vec<u8>>, CallError> {
    use crate::{company, workspace};
    let bytes = match op {
        "company.validate" => json(&company::validate(Path::new(field(args, "path")))?)?,
        "company.open" => json(&company::open_company(state, field(args, "path"), field(args, "secret"), field(args, "unlock"))?)?,
        "company.create" => {
            let (summary, recovery) = company::create_company(state, field(args, "path"), field(args, "name"), field(args, "username"), field(args, "password"), field(args, "confirm"))?;
            json(&Created { summary, recovery })?
        }
        "company.close" => {
            company::close(state)?;
            b"{}".to_vec()
        }
        "auth.status" => json(&workspace::read_auth_status(state)?)?,
        "auth.login" => json(&workspace::login(state, field(args, "username"), field(args, "password"))?)?,
        "auth.ticket" => json(&crate::suite::issue(state, field(args, "app"))?)?,
        "auth.login_ticket" => json(&crate::suite::redeem(state, field(args, "ticket"))?)?,
        "auth.unlock" => {
            workspace::unlock(state, field(args, "password"))?;
            b"{}".to_vec()
        }
        "auth.lock" => {
            workspace::lock(state)?;
            b"{}".to_vec()
        }
        "auth.logout" => {
            workspace::logout(state)?;
            b"{}".to_vec()
        }
        _ => return Ok(None),
    };
    Ok(Some(bytes))
}

/// The open sessions of one app. Make one per app, for example in a `LazyLock`.
pub struct Registry {
    next: AtomicU64,
    sessions: Mutex<HashMap<u64, Arc<AppState>>>,
}

impl Default for Registry {
    fn default() -> Self {
        Self::new()
    }
}

impl Registry {
    pub fn new() -> Self {
        Registry { next: AtomicU64::new(1), sessions: Mutex::new(HashMap::new()) }
    }

    fn sessions(&self) -> std::sync::MutexGuard<'_, HashMap<u64, Arc<AppState>>> {
        self.sessions.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// Starts a session with no company open. `app_dir` holds only the recent-files list and temporary working copies.
    pub fn create(&self, app_dir: &str, prepare: Prepare) -> CResult {
        if app_dir.trim().is_empty() {
            return fail(ERR_REQUEST, "Choose an application folder.");
        }
        let path = Path::new(app_dir);
        if let Err(error) = std::fs::create_dir_all(path) {
            return fail(ERR_DOMAIN, &format!("Could not use the application folder: {error}"));
        }
        let state = Arc::new(AppState::new(crate::company::placeholder(), path.to_path_buf(), prepare));
        let handle = self.next.fetch_add(1, Ordering::Relaxed);
        self.sessions().insert(handle, state);
        ok(format!("{{\"handle\":{handle}}}").into_bytes())
    }

    /// Ends a session, closing its company file and releasing the lock on it.
    pub fn destroy(&self, handle: u64) -> CResult {
        let Some(state) = self.sessions().remove(&handle) else {
            return fail(ERR_HANDLE, "That session is not open.");
        };
        match crate::company::close(&state) {
            Ok(()) => ok(b"{}".to_vec()),
            Err(message) => fail(ERR_DOMAIN, &message),
        }
    }

    pub fn call(&self, handle: u64, op: &str, args_json: &str, app: AppDispatch) -> CResult {
        let Some(state) = self.sessions().get(&handle).cloned() else {
            return fail(ERR_HANDLE, "That session is not open.");
        };
        if op.is_empty() {
            return fail(ERR_REQUEST, "The operation name was missing.");
        }
        let args: Value = if args_json.is_empty() {
            Value::Object(Default::default())
        } else {
            match serde_json::from_str(args_json) {
                Ok(args) => args,
                Err(_) => return fail(ERR_REQUEST, "The request was not valid JSON."),
            }
        };
        let outcome = match common(&state, op, &args) {
            Ok(Some(bytes)) => Ok(bytes),
            Ok(None) => app(&state, op, &args),
            Err(error) => Err(error),
        };
        match outcome {
            Ok(bytes) => ok(bytes),
            Err(CallError::Domain(message)) => fail(ERR_DOMAIN, &message),
            Err(CallError::BadRequest(message)) => fail(ERR_REQUEST, &message),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn taken(result: CResult) -> (i32, Vec<u8>, String) {
        let data = if result.data.is_null() { Vec::new() } else { unsafe { std::slice::from_raw_parts(result.data, result.data_len).to_vec() } };
        let error = if result.error.is_null() { String::new() } else { unsafe { CStr::from_ptr(result.error).to_string_lossy().into_owned() } };
        let status = result.status;
        unsafe { free_result(result) };
        (status, data, error)
    }

    fn echo(_: &AppState, op: &str, args: &Value) -> Result<Vec<u8>, CallError> {
        match op {
            "app.echo" => Ok(args.to_string().into_bytes()),
            "app.fail" => Err(CallError::Domain("Not allowed.".into())),
            _ => Err(CallError::BadRequest("Unknown operation.".into())),
        }
    }

    #[test]
    fn a_session_signs_in_and_reaches_the_app_and_is_then_released() {
        let dir = std::env::temp_dir().join(format!("office-host-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let registry = Registry::new();
        let (status, data, error) = taken(registry.create(&dir.to_string_lossy(), crate::schema::migrate));
        assert_eq!(status, OK, "{error}");
        let handle = serde_json::from_slice::<Value>(&data).unwrap()["handle"].as_u64().unwrap();

        let made = serde_json::json!({ "path": dir.join("Studio").to_string_lossy(), "name": "Studio", "username": "owner", "password": "correct horse battery", "confirm": "correct horse battery" });
        let (status, data, error) = taken(registry.call(handle, "company.create", &made.to_string(), echo));
        assert_eq!(status, OK, "{error}");
        let created: Value = serde_json::from_slice(&data).unwrap();
        assert!(created["recovery"].as_str().unwrap().len() > 10);

        let (status, data, _) = taken(registry.call(handle, "auth.status", "{}", echo));
        assert_eq!(status, OK);
        let state: Value = serde_json::from_slice(&data).unwrap();
        assert_eq!(state["company"]["name"], "Studio");
        assert!(state["user"].is_null());

        let login = serde_json::json!({ "username": "owner", "password": "wrong password here" });
        assert_eq!(taken(registry.call(handle, "auth.login", &login.to_string(), echo)).0, ERR_DOMAIN);
        let login = serde_json::json!({ "username": "owner", "password": "correct horse battery" });
        assert_eq!(taken(registry.call(handle, "auth.login", &login.to_string(), echo)).0, OK);

        let (status, data, _) = taken(registry.call(handle, "app.echo", "{\"a\":1}", echo));
        assert_eq!((status, data), (OK, b"{\"a\":1}".to_vec()));
        assert_eq!(taken(registry.call(handle, "app.fail", "{}", echo)).0, ERR_DOMAIN);
        assert_eq!(taken(registry.call(handle, "app.unknown", "{}", echo)).0, ERR_REQUEST);
        assert_eq!(taken(registry.call(handle, "app.echo", "not json", echo)).0, ERR_REQUEST);
        assert_eq!(taken(registry.call(handle, "", "{}", echo)).0, ERR_REQUEST);
        assert_eq!(taken(registry.call(99, "app.echo", "{}", echo)).0, ERR_HANDLE);

        assert_eq!(taken(registry.destroy(handle)).0, OK);
        assert_eq!(taken(registry.destroy(handle)).0, ERR_HANDLE);
        assert!(crate::company::open_file(&dir.join("Studio.osas"), crate::schema::migrate).is_ok(), "destroying the session releases the file");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_panic_is_returned_as_an_internal_error() {
        let result = guard(|| panic!("boom"));
        assert_eq!(taken(result).0, ERR_INTERNAL);
    }
}
