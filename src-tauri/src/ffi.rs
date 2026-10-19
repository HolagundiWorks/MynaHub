//! Version 1 C ABI for the native Windows host.
//!
//! Ownership
//! - Strings passed in are borrowed for the call. The caller keeps them.
//! - `AdmsResult.data` and `AdmsResult.error` are owned by the caller after return
//!   and must be released with `adms_result_free`, including on failure.
//! - Session handles are opaque. `0` is never valid. Dropping a session closes the
//!   company file and releases its lock.
//! - No SQLite connection, Rust reference, or internal type is returned.
//! - Panics are caught so they do not cross this boundary when the build unwinds.
//!   The crate release profile aborts on panic; these functions still must not panic.
//!
//! Status codes: 0 ok, 1 domain error, 2 bad request, 3 invalid handle, 4 internal.
//!
//! `documents.sample_pdf` returns raw PDF bytes. Every other operation returns UTF-8 JSON.

use crate::commands::{self, AppState};
use crate::company_file;
use crate::pdf;
use serde::Serialize;
use serde_json::Value;
use std::collections::HashMap;
use std::ffi::{c_char, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, LazyLock, Mutex};

pub const ABI_VERSION: u32 = 1;
pub const ADMS_OK: i32 = 0;
pub const ADMS_ERR_DOMAIN: i32 = 1;
pub const ADMS_ERR_REQUEST: i32 = 2;
pub const ADMS_ERR_HANDLE: i32 = 3;
pub const ADMS_ERR_INTERNAL: i32 = 4;

#[repr(C)]
pub struct AdmsResult {
    pub status: i32,
    pub data: *mut u8,
    pub data_len: usize,
    pub error: *mut c_char,
}

struct Host {
    sessions: HashMap<u64, Arc<AppState>>,
}

static NEXT_HANDLE: AtomicU64 = AtomicU64::new(1);
static HOST: LazyLock<Mutex<Host>> = LazyLock::new(|| Mutex::new(Host { sessions: HashMap::new() }));

fn host() -> std::sync::MutexGuard<'static, Host> {
    HOST.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn ok(bytes: Vec<u8>) -> AdmsResult {
    let (data, data_len) = leak_bytes(bytes);
    AdmsResult { status: ADMS_OK, data, data_len, error: std::ptr::null_mut() }
}

fn fail(status: i32, message: &str) -> AdmsResult {
    AdmsResult { status, data: std::ptr::null_mut(), data_len: 0, error: leak_cstr(message) }
}

fn leak_bytes(bytes: Vec<u8>) -> (*mut u8, usize) {
    if bytes.is_empty() {
        return (std::ptr::null_mut(), 0);
    }
    let boxed = bytes.into_boxed_slice();
    let len = boxed.len();
    (Box::into_raw(boxed) as *mut u8, len)
}

fn leak_cstr(message: &str) -> *mut c_char {
    let cleaned: String = message.chars().filter(|c| *c != '\0').collect();
    CString::new(cleaned).unwrap_or_else(|_| CString::new("Internal error.").expect("static error")).into_raw()
}

unsafe fn free_result(result: AdmsResult) {
    unsafe {
        if !result.data.is_null() && result.data_len > 0 {
            drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(result.data, result.data_len)));
        }
        if !result.error.is_null() {
            drop(CString::from_raw(result.error));
        }
    }
}

unsafe fn cstr<'a>(ptr: *const c_char) -> Result<&'a str, String> {
    if ptr.is_null() {
        return Err("A required string was missing.".into());
    }
    unsafe { CStr::from_ptr(ptr) }.to_str().map_err(|_| "Text must be UTF-8.".into())
}

enum CallError {
    Domain(String),
    BadRequest(String),
}

fn lookup(handle: u64) -> Result<Arc<AppState>, i32> {
    match host().sessions.get(&handle).cloned() {
        Some(state) => Ok(state),
        None => Err(ADMS_ERR_HANDLE),
    }
}

fn json_args(text: &str) -> Result<Value, CallError> {
    if text.is_empty() {
        return Ok(Value::Object(Default::default()));
    }
    serde_json::from_str(text).map_err(|_| CallError::BadRequest("The request was not valid JSON.".into()))
}

fn field<'a>(args: &'a Value, key: &str) -> &'a str {
    args.get(key).and_then(Value::as_str).unwrap_or("")
}

fn to_json<T: Serialize>(value: &T) -> Result<Vec<u8>, CallError> {
    serde_json::to_vec(value).map_err(|_| CallError::Domain("Internal error.".into()))
}

fn dispatch(state: &AppState, op: &str, args: &Value) -> Result<Vec<u8>, CallError> {
    match crate::bridge::invoke(state, op, args) {
        Ok(Some(bytes)) => return Ok(bytes),
        Ok(None) => {}
        Err(crate::bridge::BridgeError::Domain(message)) => return Err(CallError::Domain(message)),
        Err(crate::bridge::BridgeError::BadRequest(message)) => return Err(CallError::BadRequest(message)),
    }
    match op {
        "company.validate" => {
            let check = company_file::validate(Path::new(field(args, "path"))).map_err(CallError::Domain)?;
            to_json(&check)
        }
        "company.open" => to_json(&company_file::open_company(state, field(args, "path"), field(args, "secret"), field(args, "unlock")).map_err(CallError::Domain)?),
        "company.create" => {
            let (summary, recovery) = company_file::create_company(
                state,
                field(args, "path"),
                field(args, "name"),
                field(args, "username"),
                field(args, "password"),
                field(args, "confirm"),
            )
            .map_err(CallError::Domain)?;
            to_json(&Created { summary, recovery })
        }
        "company.close" => {
            company_file::close(state).map_err(CallError::Domain)?;
            Ok(b"{}".to_vec())
        }
        "company.profile" => to_json(&commands::read_profile(state).map_err(CallError::Domain)?),
        "auth.status" => to_json(&commands::read_auth_status(state).map_err(CallError::Domain)?),
        "auth.login" => to_json(&commands::login(state, field(args, "username"), field(args, "password")).map_err(CallError::Domain)?),
        "auth.logout" => {
            commands::logout(state).map_err(CallError::Domain)?;
            Ok(b"{}".to_vec())
        }
        "documents.sample_pdf" => render_sample(state).map_err(CallError::Domain),
        "" => Err(CallError::BadRequest("The operation name was missing.".into())),
        _ => Err(CallError::BadRequest("That operation is not part of this ABI version.".into())),
    }
}

#[derive(Serialize)]
struct Created {
    summary: company_file::CompanySummary,
    recovery: String,
}

fn render_sample(state: &AppState) -> Result<Vec<u8>, String> {
    let profile = commands::read_profile(state)?;
    let qr = profile.upi_id.trim();
    let qr = if qr.is_empty() { None } else { Some(qr) };
    let html = format!(
        "<html><head><style>{}</style></head><body>\
         <h1>{}</h1><h2>Company file preview</h2>\
         <p>This preview is not an issued document.</p>\
         <p>No.: <span class=\"id\">PREVIEW</span></p>\
         <p>{}</p><p>वास्तुकार सलाहकार</p>\
         <table class=\"items\"><tr><th>Item</th><th class=\"num\">Amount</th></tr>\
         <tr><td>Design</td><td class=\"num\">₹50,000.00</td></tr></table>\
         </body></html>",
        crate::typography::css(),
        escape(&profile.name),
        escape(&profile.address),
    );
    let rendered = pdf::render_html_with_qr(&html, &pdf::bundled_fonts(), qr)?;
    if !rendered.bytes.starts_with(b"%PDF") {
        return Err("The PDF engine returned an unexpected result.".into());
    }
    Ok(rendered.bytes)
}

fn escape(value: &str) -> String {
    value.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;")
}

fn call_inner(handle: u64, op: &str, args_json: &str) -> AdmsResult {
    let state = match lookup(handle) {
        Ok(state) => state,
        Err(status) => return fail(status, "That session is not open."),
    };
    let args = match json_args(args_json) {
        Ok(args) => args,
        Err(CallError::BadRequest(message)) => return fail(ADMS_ERR_REQUEST, &message),
        Err(CallError::Domain(message)) => return fail(ADMS_ERR_DOMAIN, &message),
    };
    match dispatch(&state, op, &args) {
        Ok(bytes) => ok(bytes),
        Err(CallError::Domain(message)) => fail(ADMS_ERR_DOMAIN, &message),
        Err(CallError::BadRequest(message)) => fail(ADMS_ERR_REQUEST, &message),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn adms_api_version() -> u32 {
    ABI_VERSION
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn adms_result_free(result: AdmsResult) {
    unsafe { free_result(result) }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn adms_session_create(app_dir: *const c_char) -> AdmsResult {
    let created = catch_unwind(AssertUnwindSafe(|| {
        let dir = match unsafe { cstr(app_dir) } {
            Ok(dir) if !dir.trim().is_empty() => dir.to_string(),
            Ok(_) => return fail(ADMS_ERR_REQUEST, "Choose an application folder."),
            Err(message) => return fail(ADMS_ERR_REQUEST, &message),
        };
        let path = Path::new(&dir);
        if let Err(error) = std::fs::create_dir_all(path) {
            return fail(ADMS_ERR_DOMAIN, &format!("Could not use the application folder: {error}"));
        }
        let state = Arc::new(AppState::new(company_file::placeholder(), path.to_path_buf(), crate::db::migrate));
        let handle = NEXT_HANDLE.fetch_add(1, Ordering::Relaxed);
        host().sessions.insert(handle, state);
        ok(format!("{{\"handle\":{handle}}}").into_bytes())
    }));
    match created {
        Ok(result) => result,
        Err(_) => fail(ADMS_ERR_INTERNAL, "Internal error."),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn adms_session_destroy(handle: u64) -> AdmsResult {
    let destroyed = catch_unwind(AssertUnwindSafe(|| {
        let Some(state) = host().sessions.remove(&handle) else {
            return fail(ADMS_ERR_HANDLE, "That session is not open.");
        };
        if let Err(message) = company_file::close(&state) {
            return fail(ADMS_ERR_DOMAIN, &message);
        }
        ok(b"{}".to_vec())
    }));
    match destroyed {
        Ok(result) => result,
        Err(_) => fail(ADMS_ERR_INTERNAL, "Internal error."),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn adms_call(handle: u64, op: *const c_char, args_json: *const c_char) -> AdmsResult {
    let called = catch_unwind(AssertUnwindSafe(|| {
        let op = match unsafe { cstr(op) } {
            Ok(op) => op.to_string(),
            Err(message) => return fail(ADMS_ERR_REQUEST, &message),
        };
        let args = if args_json.is_null() {
            "{}".to_string()
        } else {
            match unsafe { cstr(args_json) } {
                Ok(args) => args.to_string(),
                Err(message) => return fail(ADMS_ERR_REQUEST, &message),
            }
        };
        call_inner(handle, &op, &args)
    }));
    match called {
        Ok(result) => result,
        Err(_) => fail(ADMS_ERR_INTERNAL, "Internal error."),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::company_file::NewCompany;
    use std::ffi::CString;

    struct Temp {
        dir: std::path::PathBuf,
    }

    impl Temp {
        fn new(tag: &str) -> Self {
            let dir = std::env::temp_dir().join(format!("adms-ffi-{tag}-{}", std::process::id()));
            let _ = std::fs::remove_dir_all(&dir);
            std::fs::create_dir_all(&dir).unwrap();
            Self { dir }
        }
    }

    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.dir);
        }
    }

    fn taken(result: AdmsResult) -> (i32, Vec<u8>, String) {
        let status = result.status;
        let data = if result.data.is_null() || result.data_len == 0 {
            Vec::new()
        } else {
            unsafe { std::slice::from_raw_parts(result.data, result.data_len).to_vec() }
        };
        let error = if result.error.is_null() {
            String::new()
        } else {
            unsafe { CStr::from_ptr(result.error).to_string_lossy().into_owned() }
        };
        unsafe { adms_result_free(result) };
        (status, data, error)
    }

    fn create_session(dir: &Path) -> u64 {
        let path = CString::new(dir.to_string_lossy().as_bytes()).unwrap();
        let (status, data, error) = taken(unsafe { adms_session_create(path.as_ptr()) });
        assert_eq!(status, ADMS_OK, "{error}");
        let json: Value = serde_json::from_slice(&data).unwrap();
        json["handle"].as_u64().unwrap()
    }

    fn call(handle: u64, op: &str, args: &str) -> (i32, Vec<u8>, String) {
        let op = CString::new(op).unwrap();
        let args = CString::new(args).unwrap();
        taken(unsafe { adms_call(handle, op.as_ptr(), args.as_ptr()) })
    }

    #[test]
    fn version_is_stable_and_an_empty_result_can_be_released() {
        assert_eq!(adms_api_version(), ABI_VERSION);
        unsafe {
            adms_result_free(AdmsResult { status: 0, data: std::ptr::null_mut(), data_len: 0, error: std::ptr::null_mut() });
        }
    }

    #[test]
    fn a_missing_session_and_an_unknown_operation_are_rejected() {
        let (status, _, error) = call(0, "company.validate", "{}");
        assert_eq!(status, ADMS_ERR_HANDLE, "{error}");
        let temp = Temp::new("bad-op");
        let handle = create_session(&temp.dir);
        let (status, _, error) = call(handle, "sqlite.query", "{}");
        assert_eq!(status, ADMS_ERR_REQUEST, "{error}");
        let (status, _, _) = taken(unsafe { adms_session_destroy(handle) });
        assert_eq!(status, ADMS_OK);
        let (status, _, _) = taken(unsafe { adms_session_destroy(handle) });
        assert_eq!(status, ADMS_ERR_HANDLE);
    }

    #[test]
    fn open_login_profile_and_pdf_round_trip_and_release_the_file() {
        let temp = Temp::new("round");
        let company = temp.dir.join("Studio");
        let (_path, _recovery) = company_file::create_file(
            &company,
            &NewCompany { name: "वास्तु कार्यालय", username: "owner", password: "correct horse battery", confirm: "correct horse battery" },
            crate::db::migrate,
        )
        .unwrap();
        let path = temp.dir.join("Studio.adms");
        let handle = create_session(&temp.dir);
        let (status, _, error) = call(handle, "documents.sample_pdf", "{}");
        assert_eq!(status, ADMS_ERR_DOMAIN, "{error}");
        assert!(error.contains("Not signed in") || error.contains("Open or create") || error.contains("permission") || error.contains("company"), "{error}");

        let args = serde_json::json!({ "path": path }).to_string();
        let (status, data, error) = call(handle, "company.open", &args);
        assert_eq!(status, ADMS_OK, "{error}");
        let summary: Value = serde_json::from_slice(&data).unwrap();
        assert_eq!(summary["name"], "वास्तु कार्यालय");

        let (status, _, error) = call(handle, "company.open", &args);
        assert_eq!(status, ADMS_ERR_DOMAIN, "{error}");
        assert!(error.contains("Close the current company"), "{error}");

        let other = create_session(&temp.dir);
        // A second app (a second session) may have the same company open; they share the file.
        let (status, data, error) = call(other, "company.open", &args);
        assert_eq!(status, ADMS_OK, "{error}");
        let shared: Value = serde_json::from_slice(&data).unwrap();
        assert_eq!(shared["uuid"], summary["uuid"]);
        assert_eq!(taken(unsafe { adms_session_destroy(other) }).0, ADMS_OK);

        let login = serde_json::json!({ "username": "owner", "password": "correct horse battery" }).to_string();
        let (status, data, error) = call(handle, "auth.login", &login);
        assert_eq!(status, ADMS_OK, "{error}");
        let session: Value = serde_json::from_slice(&data).unwrap();
        assert_eq!(session["username"], "owner");

        let (status, data, error) = call(handle, "client_list", "{\"include_archived\":false}");
        assert_eq!(status, ADMS_OK, "{error}");
        let clients: serde_json::Value = serde_json::from_slice(&data).unwrap();
        assert!(clients.as_array().unwrap().is_empty());

        let (status, data, error) = call(handle, "company.profile", "{}");
        assert_eq!(status, ADMS_OK, "{error}");
        let profile: Value = serde_json::from_slice(&data).unwrap();
        assert_eq!(profile["name"], "वास्तु कार्यालय");

        let (status, data, error) = call(handle, "documents.sample_pdf", "{}");
        assert_eq!(status, ADMS_OK, "{error}");
        assert!(data.starts_with(b"%PDF"), "pdf bytes");
        let text = String::from_utf8_lossy(&data);
        assert!(text.contains("IBMPlexSans"), "bundled sans face should be embedded");
        assert!(text.contains("Devanagari"), "devanagari face should be embedded");

        assert_eq!(taken(unsafe { adms_session_destroy(handle) }).0, ADMS_OK);
        let reopened = company_file::open_file(&path, crate::db::migrate);
        assert!(reopened.is_ok(), "destroying the session must release the company file");
        drop(reopened);
    }
}
