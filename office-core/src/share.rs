//! What two apps need to work in the same company file at the same time.
//!
//! * **Change detection.** `data_version` goes up whenever another connection has written to the file, so an app can poll it and
//!   reload what it shows. It does not change for the app's own writes.
//! * **Schema compatibility.** An app that is older than the file must stop rather than guess. `schema_ok` says whether this build
//!   understands the file as it is now (another app may have upgraded it after this one opened it).
//! * **Project documents.** An app keeps a document per project and kind (for example O-CoreOp's quantities, kind `aqc.project`).
//!   Each save names the revision it was based on; if someone else saved since, the save is refused instead of overwriting.
//!   The last ten revisions are kept so a bad save can be undone.

use crate::hash::sha256_hex;
use rusqlite::{params, Connection, OptionalExtension, Transaction, TransactionBehavior};
use serde::Serialize;

/// Revisions of one document kept in the history.
pub const KEEP_REVISIONS: i64 = 10;
/// The largest document accepted, in bytes.
pub const MAX_BODY: usize = 64 * 1024 * 1024;

#[derive(Debug, Serialize)]
pub struct DataVersion {
    pub version: i64,
    pub schema_ok: bool,
}

pub fn data_version(conn: &Connection) -> Result<DataVersion, String> {
    let version: i64 = conn.query_row("PRAGMA data_version", [], |r| r.get(0)).map_err(|e| e.to_string())?;
    let schema: usize = conn.pragma_query_value(None, "user_version", |r| r.get(0)).map_err(|e| e.to_string())?;
    Ok(DataVersion { version, schema_ok: schema <= crate::schema::latest_version() })
}

/// What an app polls for: has anyone else written, is the file still one this build understands, and where is a document now.
/// It reads no records and does not count as activity, so polling it never keeps an idle session from locking.
#[derive(Debug, Serialize)]
pub struct Watch {
    pub version: i64,
    pub schema_ok: bool,
    /// The revision of the document asked about, or 0 when there is none (or none was asked about).
    pub revision: i64,
}

pub fn watch(conn: &Connection, project_id: Option<i64>, kind: &str) -> Result<Watch, String> {
    let base = data_version(conn)?;
    let revision = match project_id {
        Some(id) => {
            check_kind(kind)?;
            conn.query_row("SELECT revision FROM project_documents WHERE project_id = ?1 AND kind = ?2", params![id, kind], |r| r.get(0)).optional().map_err(|e| e.to_string())?.unwrap_or(0)
        }
        None => 0,
    };
    Ok(Watch { version: base.version, schema_ok: base.schema_ok, revision })
}

#[derive(Debug, Serialize, PartialEq, Eq)]
pub struct Document {
    pub project_id: i64,
    pub kind: String,
    pub revision: i64,
    pub body: String,
    pub checksum_sha256: String,
    pub updated_by: String,
    pub updated_at: String,
}

#[derive(Debug, Serialize, PartialEq, Eq)]
pub struct DocumentInfo {
    pub kind: String,
    pub revision: i64,
    pub checksum_sha256: String,
    pub updated_by: String,
    pub updated_at: String,
    pub bytes: i64,
}

fn check_kind(kind: &str) -> Result<(), String> {
    let ok = !kind.is_empty()
        && kind.len() <= 40
        && kind.starts_with(|c: char| c.is_ascii_lowercase())
        && kind.chars().all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '.' || c == '_');
    if ok {
        Ok(())
    } else {
        Err("A document kind is lower-case letters, digits, dots and underscores, starting with a letter (up to 40 characters).".into())
    }
}

fn project_exists(conn: &Connection, project_id: i64) -> Result<(), String> {
    let found: bool = conn
        .query_row("SELECT 1 FROM projects WHERE id = ?1", params![project_id], |_| Ok(true))
        .optional()
        .map_err(|e| e.to_string())?
        .unwrap_or(false);
    if found {
        Ok(())
    } else {
        Err("Project not found.".into())
    }
}

pub fn doc_get(conn: &Connection, project_id: i64, kind: &str) -> Result<Option<Document>, String> {
    check_kind(kind)?;
    conn.query_row(
        "SELECT revision, body, checksum_sha256, updated_by, updated_at FROM project_documents WHERE project_id = ?1 AND kind = ?2",
        params![project_id, kind],
        |r| {
            Ok(Document { project_id, kind: kind.to_string(), revision: r.get(0)?, body: r.get(1)?, checksum_sha256: r.get(2)?, updated_by: r.get(3)?, updated_at: r.get(4)? })
        },
    )
    .optional()
    .map_err(|e| e.to_string())
}

/// The documents a project has, without their bodies.
pub fn doc_list(conn: &Connection, project_id: i64) -> Result<Vec<DocumentInfo>, String> {
    let mut stmt = conn
        .prepare("SELECT kind, revision, checksum_sha256, updated_by, updated_at, length(body) FROM project_documents WHERE project_id = ?1 ORDER BY kind")
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![project_id], |r| {
            Ok(DocumentInfo { kind: r.get(0)?, revision: r.get(1)?, checksum_sha256: r.get(2)?, updated_by: r.get(3)?, updated_at: r.get(4)?, bytes: r.get(5)? })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<Vec<_>, _>>().map_err(|e| e.to_string())
}

/// Saves a document. `expected_revision` is the revision the caller loaded (0 when it has never been saved). If another app has saved
/// since, nothing is written and the message says so. Returns the new revision and the checksum of what was stored.
pub fn doc_put(conn: &Connection, project_id: i64, kind: &str, expected_revision: i64, body: &str, by: &str) -> Result<(i64, String), String> {
    check_kind(kind)?;
    if body.len() > MAX_BODY {
        return Err("That document is too large to store.".into());
    }
    project_exists(conn, project_id)?;
    let tx = Transaction::new_unchecked(conn, TransactionBehavior::Immediate).map_err(|e| e.to_string())?;
    let current: Option<i64> = tx
        .query_row("SELECT revision FROM project_documents WHERE project_id = ?1 AND kind = ?2", params![project_id, kind], |r| r.get(0))
        .optional()
        .map_err(|e| e.to_string())?;
    let at = current.unwrap_or(0);
    if at != expected_revision {
        return Err("This project was changed in another app since you opened it. Reload it before saving, so nothing is overwritten.".into());
    }
    let checksum = sha256_hex(body.as_bytes());
    let revision = at + 1;
    if current.is_some() {
        tx.execute(
            "INSERT INTO project_document_history (project_id, kind, revision, body, checksum_sha256, updated_by, updated_at)
             SELECT project_id, kind, revision, body, checksum_sha256, updated_by, updated_at FROM project_documents WHERE project_id = ?1 AND kind = ?2",
            params![project_id, kind],
        )
        .map_err(|e| e.to_string())?;
        tx.execute(
            "DELETE FROM project_document_history WHERE project_id = ?1 AND kind = ?2 AND revision <= ?3",
            params![project_id, kind, revision - 1 - KEEP_REVISIONS],
        )
        .map_err(|e| e.to_string())?;
        tx.execute(
            "UPDATE project_documents SET revision = ?3, body = ?4, checksum_sha256 = ?5, updated_by = ?6, updated_at = datetime('now') WHERE project_id = ?1 AND kind = ?2",
            params![project_id, kind, revision, body, checksum, by],
        )
        .map_err(|e| e.to_string())?;
    } else {
        tx.execute(
            "INSERT INTO project_documents (project_id, kind, revision, body, checksum_sha256, updated_by) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
            params![project_id, kind, revision, body, checksum, by],
        )
        .map_err(|e| e.to_string())?;
    }
    tx.commit().map_err(|e| e.to_string())?;
    Ok((revision, checksum))
}

/// An earlier revision of a document, from the history.
pub fn doc_revision(conn: &Connection, project_id: i64, kind: &str, revision: i64) -> Result<Option<Document>, String> {
    check_kind(kind)?;
    if let Some(current) = doc_get(conn, project_id, kind)? {
        if current.revision == revision {
            return Ok(Some(current));
        }
    }
    conn.query_row(
        "SELECT body, checksum_sha256, updated_by, updated_at FROM project_document_history WHERE project_id = ?1 AND kind = ?2 AND revision = ?3",
        params![project_id, kind, revision],
        |r| Ok(Document { project_id, kind: kind.to_string(), revision, body: r.get(0)?, checksum_sha256: r.get(1)?, updated_by: r.get(2)?, updated_at: r.get(3)? }),
    )
    .optional()
    .map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::schema::migrate(&mut c).unwrap();
        c.execute("INSERT INTO clients (id, name) VALUES (1, 'Client')", []).unwrap();
        c.execute("INSERT INTO projects (id, client_id, name) VALUES (7, 1, 'House')", []).unwrap();
        c
    }

    #[test]
    fn a_document_is_saved_by_revision_and_reads_back_with_its_checksum() {
        let c = conn();
        assert!(doc_get(&c, 7, "aqc.project").unwrap().is_none());
        let (r1, sum1) = doc_put(&c, 7, "aqc.project", 0, "{\"a\":1}", "owner").unwrap();
        assert_eq!(r1, 1);
        assert_eq!(sum1, sha256_hex(b"{\"a\":1}"));
        let (r2, _) = doc_put(&c, 7, "aqc.project", 1, "{\"a\":2}", "owner").unwrap();
        assert_eq!(r2, 2);
        let got = doc_get(&c, 7, "aqc.project").unwrap().unwrap();
        assert_eq!((got.revision, got.body.as_str(), got.updated_by.as_str()), (2, "{\"a\":2}", "owner"));
        assert_eq!(doc_list(&c, 7).unwrap().len(), 1);
        assert_eq!(doc_list(&c, 7).unwrap()[0].bytes, 7);
    }

    #[test]
    fn a_save_based_on_an_old_revision_is_refused_and_changes_nothing() {
        let c = conn();
        doc_put(&c, 7, "aqc.project", 0, "one", "a").unwrap();
        doc_put(&c, 7, "aqc.project", 1, "two", "b").unwrap();
        let stale = doc_put(&c, 7, "aqc.project", 1, "three", "a").unwrap_err();
        assert!(stale.contains("changed in another app"), "{stale}");
        assert_eq!(doc_get(&c, 7, "aqc.project").unwrap().unwrap().body, "two");
        assert!(doc_put(&c, 7, "aqc.project", 0, "new", "a").is_err(), "a first save over an existing document is refused too");
    }

    #[test]
    fn earlier_revisions_are_kept_for_the_last_ten_saves() {
        let c = conn();
        let mut revision = 0;
        for i in 1..=15 {
            revision = doc_put(&c, 7, "aqc.project", revision, &format!("body {i}"), "a").unwrap().0;
        }
        assert_eq!(revision, 15);
        assert_eq!(doc_revision(&c, 7, "aqc.project", 15).unwrap().unwrap().body, "body 15");
        assert_eq!(doc_revision(&c, 7, "aqc.project", 5).unwrap().unwrap().body, "body 5");
        assert!(doc_revision(&c, 7, "aqc.project", 4).unwrap().is_none(), "older than ten revisions back is dropped");
    }

    #[test]
    fn kinds_and_projects_are_checked() {
        let c = conn();
        for bad in ["", "Aqc", "1abc", "a b", &"a".repeat(41)] {
            assert!(doc_put(&c, 7, bad, 0, "x", "a").is_err(), "{bad:?}");
        }
        assert_eq!(doc_put(&c, 99, "aqc.project", 0, "x", "a").unwrap_err(), "Project not found.");
        doc_put(&c, 7, "aqc.rates", 0, "r", "a").unwrap();
        doc_put(&c, 7, "aqc.project", 0, "p", "a").unwrap();
        assert_eq!(doc_list(&c, 7).unwrap().iter().map(|d| d.kind.as_str()).collect::<Vec<_>>(), ["aqc.project", "aqc.rates"]);
    }

    #[test]
    fn watching_reports_the_document_revision_without_reading_it() {
        let c = conn();
        assert_eq!(watch(&c, Some(7), "aqc.project").unwrap().revision, 0);
        doc_put(&c, 7, "aqc.project", 0, "x", "a").unwrap();
        let w = watch(&c, Some(7), "aqc.project").unwrap();
        assert_eq!((w.revision, w.schema_ok), (1, true));
        assert_eq!(watch(&c, None, "aqc.project").unwrap().revision, 0);
        assert!(watch(&c, Some(7), "Bad Kind").is_err());
    }

    #[test]
    fn the_data_version_and_schema_check_report_on_the_file() {
        let c = conn();
        assert!(data_version(&c).unwrap().schema_ok);
    }
}
