//! Where the sign-in is kept. The suite shares one refresh token between its desktop apps in a single OS credential, and each app
//! keeps its own session id in an entry of its own. Neither the password nor the access token is ever stored.

use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::Mutex;

/// The shared credential's contents: `{ "v": 1, "refreshToken", "email", "updatedAt" }`, UTF-8 JSON.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Shared {
    pub v: u32,
    #[serde(rename = "refreshToken")]
    pub refresh_token: String,
    pub email: String,
    /// Unix seconds. A rotated token is written back at once, so another app that reads this finds the newest one.
    #[serde(rename = "updatedAt")]
    pub updated_at: u64,
}

pub trait TokenStore: Send + Sync {
    fn read_shared(&self) -> Result<Option<Shared>, String>;
    fn write_shared(&self, shared: &Shared) -> Result<(), String>;
    fn clear_shared(&self) -> Result<(), String>;
    fn read_session(&self, app: &str) -> Result<Option<String>, String>;
    fn write_session(&self, app: &str, session_id: &str) -> Result<(), String>;
    fn clear_session(&self, app: &str) -> Result<(), String>;
}

/// Keeps everything in memory. For tests, and for an app that must not touch the OS credential store.
#[derive(Default)]
pub struct MemoryStore {
    shared: Mutex<Option<Shared>>,
    sessions: Mutex<HashMap<String, String>>,
}

impl MemoryStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl TokenStore for MemoryStore {
    fn read_shared(&self) -> Result<Option<Shared>, String> {
        Ok(self.shared.lock().map_err(|_| "store poisoned")?.clone())
    }
    fn write_shared(&self, shared: &Shared) -> Result<(), String> {
        *self.shared.lock().map_err(|_| "store poisoned")? = Some(shared.clone());
        Ok(())
    }
    fn clear_shared(&self) -> Result<(), String> {
        *self.shared.lock().map_err(|_| "store poisoned")? = None;
        Ok(())
    }
    fn read_session(&self, app: &str) -> Result<Option<String>, String> {
        Ok(self.sessions.lock().map_err(|_| "store poisoned")?.get(app).cloned())
    }
    fn write_session(&self, app: &str, session_id: &str) -> Result<(), String> {
        self.sessions.lock().map_err(|_| "store poisoned")?.insert(app.to_string(), session_id.to_string());
        Ok(())
    }
    fn clear_session(&self, app: &str) -> Result<(), String> {
        self.sessions.lock().map_err(|_| "store poisoned")?.remove(app);
        Ok(())
    }
}

#[cfg(windows)]
pub use windows_store::WindowsStore;

#[cfg(windows)]
mod windows_store {
    use super::{Shared, TokenStore};
    use windows_sys::Win32::Foundation::{GetLastError, FILETIME};
    use windows_sys::Win32::Security::Credentials::{CredDeleteW, CredFree, CredReadW, CredWriteW, CREDENTIALW, CRED_PERSIST_LOCAL_MACHINE, CRED_TYPE_GENERIC};

    const ERROR_NOT_FOUND: u32 = 1168;

    /// Windows Credential Manager generic credentials. The shared sign-in is at the target `AORMS/Connect` (user name = the e-mail),
    /// and each app's session id at `AORMS/Connect/<app>`.
    pub struct WindowsStore {
        root: String,
    }

    impl WindowsStore {
        /// The suite's real credential.
        pub fn new() -> Self {
            Self { root: "AORMS/Connect".into() }
        }

        /// A different root, so a test never touches the real sign-in.
        pub fn with_root(root: &str) -> Self {
            Self { root: root.into() }
        }

        fn app_target(&self, app: &str) -> String {
            format!("{}/{app}", self.root)
        }
    }

    impl Default for WindowsStore {
        fn default() -> Self {
            Self::new()
        }
    }

    fn wide(text: &str) -> Vec<u16> {
        text.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn read(target: &str) -> Result<Option<Vec<u8>>, String> {
        let name = wide(target);
        let mut found: *mut CREDENTIALW = std::ptr::null_mut();
        // SAFETY: `name` is NUL-terminated and outlives the call; on success `found` is released with CredFree below.
        let ok = unsafe { CredReadW(name.as_ptr(), CRED_TYPE_GENERIC, 0, &mut found) };
        if ok == 0 {
            let error = unsafe { GetLastError() };
            return if error == ERROR_NOT_FOUND { Ok(None) } else { Err(format!("The saved sign-in could not be read (error {error}).")) };
        }
        // SAFETY: Windows returned a valid credential; the blob pointer and size describe its bytes.
        let bytes = unsafe {
            let credential = &*found;
            let slice = if credential.CredentialBlob.is_null() || credential.CredentialBlobSize == 0 {
                &[][..]
            } else {
                std::slice::from_raw_parts(credential.CredentialBlob, credential.CredentialBlobSize as usize)
            };
            let copy = slice.to_vec();
            CredFree(found as *const _);
            copy
        };
        Ok(Some(bytes))
    }

    fn write(target: &str, user: &str, blob: &[u8]) -> Result<(), String> {
        let name = wide(target);
        let user = wide(user);
        let mut credential = CREDENTIALW {
            Flags: 0,
            Type: CRED_TYPE_GENERIC,
            TargetName: name.as_ptr() as *mut u16,
            Comment: std::ptr::null_mut(),
            LastWritten: FILETIME { dwLowDateTime: 0, dwHighDateTime: 0 },
            CredentialBlobSize: blob.len() as u32,
            CredentialBlob: blob.as_ptr() as *mut u8,
            Persist: CRED_PERSIST_LOCAL_MACHINE,
            AttributeCount: 0,
            Attributes: std::ptr::null_mut(),
            TargetAlias: std::ptr::null_mut(),
            UserName: user.as_ptr() as *mut u16,
        };
        // SAFETY: every pointer refers to a buffer that lives until the call returns, and CredWriteW copies what it keeps.
        let ok = unsafe { CredWriteW(&mut credential, 0) };
        if ok == 0 {
            return Err(format!("The sign-in could not be saved (error {}).", unsafe { GetLastError() }));
        }
        Ok(())
    }

    fn delete(target: &str) -> Result<(), String> {
        let name = wide(target);
        // SAFETY: `name` is NUL-terminated and outlives the call.
        let ok = unsafe { CredDeleteW(name.as_ptr(), CRED_TYPE_GENERIC, 0) };
        if ok == 0 {
            let error = unsafe { GetLastError() };
            if error != ERROR_NOT_FOUND {
                return Err(format!("The saved sign-in could not be removed (error {error})."));
            }
        }
        Ok(())
    }

    impl TokenStore for WindowsStore {
        fn read_shared(&self) -> Result<Option<Shared>, String> {
            match read(&self.root)? {
                None => Ok(None),
                // A blob this build cannot read is treated as no sign-in rather than as an error that blocks the app.
                Some(bytes) => Ok(serde_json::from_slice::<Shared>(&bytes).ok().filter(|s| s.v == 1 && !s.refresh_token.is_empty())),
            }
        }

        fn write_shared(&self, shared: &Shared) -> Result<(), String> {
            let blob = serde_json::to_vec(shared).map_err(|e| e.to_string())?;
            write(&self.root, &shared.email, &blob)
        }

        fn clear_shared(&self) -> Result<(), String> {
            delete(&self.root)
        }

        fn read_session(&self, app: &str) -> Result<Option<String>, String> {
            Ok(read(&self.app_target(app))?.and_then(|b| String::from_utf8(b).ok()).filter(|s| !s.is_empty()))
        }

        fn write_session(&self, app: &str, session_id: &str) -> Result<(), String> {
            write(&self.app_target(app), app, session_id.as_bytes())
        }

        fn clear_session(&self, app: &str) -> Result<(), String> {
            delete(&self.app_target(app))
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn the_credential_manager_round_trips_the_shared_sign_in_and_the_session_id() {
            let store = WindowsStore::with_root(&format!("AORMS-Test/Connect-{}", std::process::id()));
            assert_eq!(store.read_shared().unwrap(), None);
            let shared = Shared { v: 1, refresh_token: "refresh-1".into(), email: "a@example.com".into(), updated_at: 1_700_000_000 };
            store.write_shared(&shared).unwrap();
            assert_eq!(store.read_shared().unwrap(), Some(shared.clone()));
            let newer = Shared { refresh_token: "refresh-2".into(), updated_at: 1_700_000_100, ..shared };
            store.write_shared(&newer).unwrap();
            assert_eq!(store.read_shared().unwrap().unwrap().refresh_token, "refresh-2");
            assert_eq!(store.read_session("aos").unwrap(), None);
            store.write_session("aos", "session-1").unwrap();
            assert_eq!(store.read_session("aos").unwrap().as_deref(), Some("session-1"));
            assert_eq!(store.read_session("aad").unwrap(), None, "each app has its own entry");
            store.clear_session("aos").unwrap();
            store.clear_shared().unwrap();
            store.clear_shared().unwrap();
            assert_eq!((store.read_shared().unwrap(), store.read_session("aos").unwrap()), (None, None));
        }
    }
}
