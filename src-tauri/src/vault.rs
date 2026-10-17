//! Optional protection for a company file at rest.
//! A protected file starts with `AOSENC1` and holds a random file key wrapped twice:
//! once with the owner's password, and once with the recovery code. The SQLite bytes
//! are encrypted with that file key. An ordinary company file is left unchanged.

use aes_gcm::aead::{Aead, KeyInit};
use aes_gcm::{Aes256Gcm, Nonce};
use argon2::password_hash::rand_core::{OsRng, RngCore};
use argon2::{Algorithm, Argon2, Params, Version};

pub const MAGIC: &[u8; 8] = b"AOSENC1\n";
const SALT: usize = 16;
const NONCE: usize = 12;
const WRAP: usize = 48;
pub const HEADER: usize = 8 + (SALT + NONCE + WRAP) * 2 + NONCE;

#[derive(Clone)]
pub struct Envelope {
    pub file_key: [u8; 32],
    pub password_salt: [u8; SALT],
    pub password_nonce: [u8; NONCE],
    pub password_wrap: [u8; WRAP],
    pub recovery_salt: [u8; SALT],
    pub recovery_nonce: [u8; NONCE],
    pub recovery_wrap: [u8; WRAP],
}

pub fn is_protected(path: &std::path::Path) -> bool {
    let mut head = [0u8; 8];
    let Ok(mut file) = std::fs::File::open(path) else { return false };
    use std::io::Read;
    file.read_exact(&mut head).is_ok() && &head == MAGIC
}

pub fn seal(sqlite: &[u8], password: &str, recovery: &str) -> Result<(Vec<u8>, Envelope), String> {
    if password.is_empty() {
        return Err("Enter the password that opens this company file.".into());
    }
    let recovery = normalize_recovery(recovery);
    if recovery.is_empty() {
        return Err("A recovery code is required to protect the company file.".into());
    }
    let mut envelope = Envelope {
        file_key: random(),
        password_salt: random(),
        password_nonce: [0; NONCE],
        password_wrap: [0; WRAP],
        recovery_salt: random(),
        recovery_nonce: [0; NONCE],
        recovery_wrap: [0; WRAP],
    };
    let (nonce, wrap) = wrap_key(&envelope.file_key, password, &envelope.password_salt)?;
    envelope.password_nonce = nonce;
    envelope.password_wrap = wrap;
    let (nonce, wrap) = wrap_key(&envelope.file_key, &recovery, &envelope.recovery_salt)?;
    envelope.recovery_nonce = nonce;
    envelope.recovery_wrap = wrap;
    Ok((pack(sqlite, &envelope)?, envelope))
}

pub fn open_with(bytes: &[u8], secret: &str, unlock: &str) -> Result<(Vec<u8>, Envelope), String> {
    let envelope = read_header(bytes)?;
    let recovery = unlock == "recovery";
    let secret = if recovery { normalize_recovery(secret) } else { secret.to_string() };
    if secret.is_empty() {
        return Err(if recovery { "Enter the recovery code.".into() } else { "Enter the password that opens this company file.".into() });
    }
    let (salt, nonce, wrap) = if recovery {
        (&envelope.recovery_salt, &envelope.recovery_nonce, &envelope.recovery_wrap)
    } else {
        (&envelope.password_salt, &envelope.password_nonce, &envelope.password_wrap)
    };
    let file_key = unwrap_key(secret.as_bytes(), salt, nonce, wrap).map_err(|_| {
        if recovery { "That recovery code was not accepted.".to_string() } else { "That password was not accepted.".to_string() }
    })?;
    let mut opened = envelope;
    opened.file_key = file_key;
    let plain = decrypt_body(bytes, &opened.file_key)?;
    Ok((plain, opened))
}

pub fn decrypt_with_key(bytes: &[u8], file_key: &[u8; 32]) -> Result<Vec<u8>, String> {
    let _ = read_header(bytes)?;
    decrypt_body(bytes, file_key)
}

/// Rewrites the SQLite bytes with a new body nonce. The wrapped keys stay as they are.
pub fn pack(sqlite: &[u8], envelope: &Envelope) -> Result<Vec<u8>, String> {
    let body_nonce: [u8; NONCE] = random();
    let body = crypt(&envelope.file_key, &body_nonce, sqlite, true)?;
    let mut out = Vec::with_capacity(HEADER + body.len());
    out.extend_from_slice(MAGIC);
    out.extend_from_slice(&envelope.password_salt);
    out.extend_from_slice(&envelope.password_nonce);
    out.extend_from_slice(&envelope.password_wrap);
    out.extend_from_slice(&envelope.recovery_salt);
    out.extend_from_slice(&envelope.recovery_nonce);
    out.extend_from_slice(&envelope.recovery_wrap);
    out.extend_from_slice(&body_nonce);
    out.extend_from_slice(&body);
    Ok(out)
}

pub fn rewrap_password(envelope: &mut Envelope, password: &str) -> Result<(), String> {
    if password.is_empty() {
        return Err("Enter the password that opens this company file.".into());
    }
    envelope.password_salt = random();
    let (nonce, wrap) = wrap_key(&envelope.file_key, password, &envelope.password_salt)?;
    envelope.password_nonce = nonce;
    envelope.password_wrap = wrap;
    Ok(())
}

pub fn rewrap_recovery(envelope: &mut Envelope, recovery: &str) -> Result<(), String> {
    let recovery = normalize_recovery(recovery);
    if recovery.is_empty() {
        return Err("A recovery code is required.".into());
    }
    envelope.recovery_salt = random();
    let (nonce, wrap) = wrap_key(&envelope.file_key, &recovery, &envelope.recovery_salt)?;
    envelope.recovery_nonce = nonce;
    envelope.recovery_wrap = wrap;
    Ok(())
}

pub fn normalize_recovery(code: &str) -> String {
    code.trim().to_uppercase()
}

fn read_header(bytes: &[u8]) -> Result<Envelope, String> {
    if bytes.len() < HEADER || bytes.get(..8) != Some(MAGIC.as_slice()) {
        return Err("This company file is protected, and the protected copy could not be read.".into());
    }
    let mut at = 8;
    let take = |at: &mut usize, n: usize| {
        let start = *at;
        *at += n;
        &bytes[start..*at]
    };
    let mut envelope = Envelope {
        file_key: [0; 32],
        password_salt: [0; SALT],
        password_nonce: [0; NONCE],
        password_wrap: [0; WRAP],
        recovery_salt: [0; SALT],
        recovery_nonce: [0; NONCE],
        recovery_wrap: [0; WRAP],
    };
    envelope.password_salt.copy_from_slice(take(&mut at, SALT));
    envelope.password_nonce.copy_from_slice(take(&mut at, NONCE));
    envelope.password_wrap.copy_from_slice(take(&mut at, WRAP));
    envelope.recovery_salt.copy_from_slice(take(&mut at, SALT));
    envelope.recovery_nonce.copy_from_slice(take(&mut at, NONCE));
    envelope.recovery_wrap.copy_from_slice(take(&mut at, WRAP));
    Ok(envelope)
}

fn decrypt_body(bytes: &[u8], file_key: &[u8; 32]) -> Result<Vec<u8>, String> {
    if bytes.len() < HEADER {
        return Err("This protected company file is damaged.".into());
    }
    let nonce: [u8; NONCE] = bytes[HEADER - NONCE..HEADER].try_into().map_err(|_| "This protected company file is damaged.".to_string())?;
    let plain = crypt(file_key, &nonce, &bytes[HEADER..], false)?;
    if !plain.starts_with(b"SQLite format 3\0") {
        return Err("This protected company file is damaged.".into());
    }
    Ok(plain)
}

fn wrap_key(file_key: &[u8; 32], secret: &str, salt: &[u8; SALT]) -> Result<([u8; NONCE], [u8; WRAP]), String> {
    let kek = derive(secret.as_bytes(), salt)?;
    let nonce: [u8; NONCE] = random();
    let cipher = crypt(&kek, &nonce, file_key, true)?;
    let mut wrap = [0u8; WRAP];
    if cipher.len() != WRAP {
        return Err("The company file could not be protected.".into());
    }
    wrap.copy_from_slice(&cipher);
    Ok((nonce, wrap))
}

fn unwrap_key(secret: &[u8], salt: &[u8; SALT], nonce: &[u8; NONCE], wrap: &[u8; WRAP]) -> Result<[u8; 32], String> {
    let kek = derive(secret, salt)?;
    let plain = crypt(&kek, nonce, wrap, false)?;
    plain.try_into().map_err(|_| "That secret was not accepted.".to_string())
}

fn derive(secret: &[u8], salt: &[u8]) -> Result<[u8; 32], String> {
    let params = Params::new(19_456, 2, 1, Some(32)).map_err(|e| e.to_string())?;
    let argon = Argon2::new(Algorithm::Argon2id, Version::V0x13, params);
    let mut key = [0u8; 32];
    argon.hash_password_into(secret, salt, &mut key).map_err(|_| "The company file could not be protected.".to_string())?;
    Ok(key)
}

fn crypt(key: &[u8; 32], nonce: &[u8; NONCE], data: &[u8], encrypting: bool) -> Result<Vec<u8>, String> {
    let cipher = Aes256Gcm::new_from_slice(key).map_err(|_| "The company file could not be protected.".to_string())?;
    let nonce = Nonce::from_slice(nonce);
    if encrypting {
        cipher.encrypt(nonce, data).map_err(|_| "The company file could not be protected.".to_string())
    } else {
        cipher.decrypt(nonce, data).map_err(|_| "That secret was not accepted.".to_string())
    }
}

fn random<const N: usize>() -> [u8; N] {
    let mut bytes = [0u8; N];
    OsRng.fill_bytes(&mut bytes);
    bytes
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn password_and_recovery_code_both_open_the_same_bytes() {
        let sqlite = b"SQLite format 3\0practice";
        let (sealed, _) = seal(sqlite, "correct horse battery", "abcde-fghij").unwrap();
        assert!(sealed.starts_with(MAGIC));
        assert!(!sealed.windows(15).any(|w| w == b"SQLite format 3"));
        let (plain, _) = open_with(&sealed, "correct horse battery", "password").unwrap();
        assert_eq!(plain, sqlite);
        let (plain, _) = open_with(&sealed, "  abcde-fghij ", "recovery").unwrap();
        assert_eq!(plain, sqlite);
        assert!(open_with(&sealed, "wrong password here", "password").is_err());
        assert!(open_with(&sealed, "abcde-fghij", "password").is_err());
    }
}
