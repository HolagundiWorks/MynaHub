use argon2::password_hash::rand_core::{OsRng, RngCore};
use argon2::password_hash::{PasswordHash, PasswordHasher, PasswordVerifier, SaltString};
use argon2::Argon2;
use serde::Serialize;
use std::time::{Duration, Instant};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Permission {
    View,
    Edit,
    Issue,
    ManageSettings,
    ManageUsers,
    ViewAudit,
    Backup,
    /// View ledgers and financial reports.
    Finance,
    /// Post journals and accounting entries.
    Post,
    ClosePeriod,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Role {
    Owner,
    Principal,
    Accounts,
    DocumentEditor,
    Auditor,
}

impl Role {
    pub fn parse(s: &str) -> Role {
        match s {
            "owner" => Role::Owner,
            "principal" => Role::Principal,
            "accounts" => Role::Accounts,
            "document_editor" => Role::DocumentEditor,
            _ => Role::Auditor,
        }
    }

    pub fn allows(self, p: Permission) -> bool {
        match p {
            Permission::View => true,
            Permission::Edit => self != Role::Auditor,
            Permission::Issue => matches!(self, Role::Owner | Role::Principal | Role::Accounts),
            Permission::ManageSettings => self == Role::Owner,
            Permission::ManageUsers | Permission::Backup => self == Role::Owner,
            Permission::ViewAudit => matches!(self, Role::Owner | Role::Auditor),
            Permission::Finance => self != Role::DocumentEditor,
            Permission::Post => matches!(self, Role::Owner | Role::Accounts),
            Permission::ClosePeriod => self == Role::Owner,
        }
    }
}

pub struct Session {
    pub user_id: i64,
    pub username: String,
    pub role: Role,
    pub locked: bool,
    pub last_activity: Instant,
}

#[derive(Serialize)]
pub struct SessionInfo {
    pub username: String,
    pub role: String,
}

pub const DEFAULT_IDLE: Duration = Duration::from_secs(5 * 60);

pub fn hash_password(pw: &str) -> Result<String, String> {
    let salt = SaltString::generate(&mut OsRng);
    Argon2::default()
        .hash_password(pw.as_bytes(), &salt)
        .map(|h| h.to_string())
        .map_err(|e| e.to_string())
}

pub fn verify_password(pw: &str, hash: &str) -> bool {
    PasswordHash::new(hash)
        .map(|h| Argon2::default().verify_password(pw.as_bytes(), &h).is_ok())
        .unwrap_or(false)
}

pub fn generate_recovery_code() -> String {
    let mut bytes = [0u8; 15];
    OsRng.fill_bytes(&mut bytes);
    let hex: String = bytes.iter().map(|b| format!("{b:02X}")).collect();
    hex.as_bytes()
        .chunks(5)
        .map(|c| std::str::from_utf8(c).unwrap())
        .collect::<Vec<_>>()
        .join("-")
}

pub fn validate_new_password(pw: &str) -> Result<(), String> {
    if pw.chars().count() < 12 {
        return Err("Password must be at least 12 characters.".into());
    }
    Ok(())
}

/// Delay in seconds after `n` consecutive failures: none for the first 4.
pub fn lockout_secs(n: i64) -> i64 {
    if n < 5 { 0 } else { (30i64 << (n - 5).min(5)).min(900) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hash_roundtrip() {
        let h = hash_password("correct horse battery").unwrap();
        assert!(verify_password("correct horse battery", &h));
        assert!(!verify_password("wrong", &h));
    }

    #[test]
    fn lockout_progression() {
        assert_eq!(lockout_secs(4), 0);
        assert_eq!(lockout_secs(5), 30);
        assert_eq!(lockout_secs(6), 60);
        assert_eq!(lockout_secs(50), 900);
    }

    #[test]
    fn only_owner_manages_settings() {
        assert!(Role::Owner.allows(Permission::ManageSettings));
        assert!(!Role::Auditor.allows(Permission::ManageSettings));
    }
}
