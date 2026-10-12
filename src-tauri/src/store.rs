//! Documents (issued PDFs, attachments) are stored inside the company database, never beside it.
use crate::billing::sha2_lite::sha256_hex;
use rusqlite::{params, Connection, OptionalExtension};

/// Stores `data` under `key`. A key is written once; reusing it is refused rather than overwritten.
pub fn put(conn: &Connection, key: &str, data: &[u8]) -> Result<(), String> {
    conn.execute(
        "INSERT INTO stored_files (key, sha256, bytes, data) VALUES (?1, ?2, ?3, ?4)",
        params![key, sha256_hex(data), data.len() as i64, data],
    )
    .map(|_| ())
    .map_err(|_| "A stored file with this name already exists; refusing to overwrite.".to_string())
}

pub fn exists(conn: &Connection, key: &str) -> bool {
    conn.query_row("SELECT 1 FROM stored_files WHERE key = ?1", params![key], |_| Ok(())).optional().ok().flatten().is_some()
}

/// Reads a stored file and checks it against the hash recorded when it was written.
pub fn get(conn: &Connection, key: &str) -> Result<Vec<u8>, String> {
    let (sha, data): (String, Vec<u8>) = conn
        .query_row("SELECT sha256, data FROM stored_files WHERE key = ?1", params![key], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|_| "The stored file could not be read.".to_string())?;
    if sha256_hex(&data) != sha {
        return Err("The stored file failed its integrity check.".into());
    }
    Ok(data)
}
