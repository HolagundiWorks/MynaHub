//! Version 1 of the C interface. The sessions, results and ownership rules are those of `office_core::host`.
//!
//! Every operation returns UTF-8 JSON. `office_call` takes a session handle, an operation name such as `client_list` or
//! `auth.login`, and the arguments as a JSON object.

use office_core::host::{self, CResult};
use std::ffi::c_char;
use std::sync::LazyLock;

pub const ABI_VERSION: u32 = 1;

static REGISTRY: LazyLock<host::Registry> = LazyLock::new(host::Registry::new);

#[unsafe(no_mangle)]
pub extern "C" fn office_api_version() -> u32 {
    ABI_VERSION
}

/// # Safety
/// `result` must have been returned by an `office_*` function and not freed already.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn office_result_free(result: CResult) {
    unsafe { host::free_result(result) }
}

/// # Safety
/// `app_dir` must be a NUL-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn office_session_create(app_dir: *const c_char) -> CResult {
    host::guard(|| match unsafe { host::cstr(app_dir) } {
        Ok(dir) => REGISTRY.create(dir, office_core::schema::migrate),
        Err(message) => host::fail(host::ERR_REQUEST, &message),
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn office_session_destroy(handle: u64) -> CResult {
    host::guard(|| REGISTRY.destroy(handle))
}

/// # Safety
/// `op` must be a NUL-terminated UTF-8 string, and `args_json` null or a NUL-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn office_call(handle: u64, op: *const c_char, args_json: *const c_char) -> CResult {
    host::guard(|| {
        let op = match unsafe { host::cstr(op) } {
            Ok(op) => op,
            Err(message) => return host::fail(host::ERR_REQUEST, &message),
        };
        let args = if args_json.is_null() {
            "{}"
        } else {
            match unsafe { host::cstr(args_json) } {
                Ok(args) => args,
                Err(message) => return host::fail(host::ERR_REQUEST, &message),
            }
        };
        REGISTRY.call(handle, op, args, crate::bridge::dispatch)
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::{CStr, CString};

    fn taken(result: CResult) -> (i32, Vec<u8>, String) {
        let data = if result.data.is_null() { Vec::new() } else { unsafe { std::slice::from_raw_parts(result.data, result.data_len).to_vec() } };
        let error = if result.error.is_null() { String::new() } else { unsafe { CStr::from_ptr(result.error).to_string_lossy().into_owned() } };
        let status = result.status;
        unsafe { office_result_free(result) };
        (status, data, error)
    }

    #[test]
    fn the_c_interface_creates_a_company_signs_in_and_reads_the_shared_records() {
        let dir = std::env::temp_dir().join(format!("office-ffi-c-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        assert_eq!(office_api_version(), 1);
        let path = CString::new(dir.to_string_lossy().as_bytes()).unwrap();
        let (status, data, error) = taken(unsafe { office_session_create(path.as_ptr()) });
        assert_eq!(status, host::OK, "{error}");
        let handle = serde_json::from_slice::<serde_json::Value>(&data).unwrap()["handle"].as_u64().unwrap();
        let call = |op: &str, args: &str| {
            let (op, args) = (CString::new(op).unwrap(), CString::new(args).unwrap());
            taken(unsafe { office_call(handle, op.as_ptr(), args.as_ptr()) })
        };
        let made = serde_json::json!({ "path": dir.join("Studio").to_string_lossy(), "name": "Studio", "username": "owner", "password": "correct horse battery", "confirm": "correct horse battery" });
        assert_eq!(call("company.create", &made.to_string()).0, host::OK);
        assert_eq!(call("client_list", "{\"include_archived\":false}").0, host::ERR_DOMAIN, "not signed in yet");
        assert_eq!(call("auth.login", "{\"username\":\"owner\",\"password\":\"correct horse battery\"}").0, host::OK);
        let (status, data, _) = call("client_list", "{\"include_archived\":false}");
        assert_eq!((status, data), (host::OK, b"[]".to_vec()));
        assert_eq!(call("project_brief_get", "{}").0, host::ERR_DOMAIN.max(host::ERR_REQUEST), "a missing argument is refused");
        assert_eq!(taken(office_session_destroy(handle)).0, host::OK);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
