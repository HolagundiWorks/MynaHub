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

/// Max kind length. Must fit CoreOp `aqc.ra.draft.<bill_id>` where bill_id is up to 64 hex chars
/// (`Guid.ToString("N")` is 32 → kind length 45).
pub const MAX_KIND_LEN: usize = 80;

fn check_kind(kind: &str) -> Result<(), String> {
    let ok = !kind.is_empty()
        && kind.len() <= MAX_KIND_LEN
        && kind.starts_with(|c: char| c.is_ascii_lowercase())
        && kind.chars().all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '.' || c == '_');
    if ok {
        Ok(())
    } else {
        Err(format!(
            "A document kind is lower-case letters, digits, dots and underscores, starting with a letter (up to {MAX_KIND_LEN} characters)."
        ))
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

/// What O-Studio shows against a fee proposal: the last estimate O-CoreOp saved on this project in the company file.
///
/// Reads `project_documents` kind `aqc.project` and picks `last_estimate.grand_total` (rupees) plus any `artifact_ref` /
/// top-level `project_ref`. Returns a JSON object with `present: false` when there is no document or no estimate yet.
pub fn estimate_summary(conn: &Connection, project_id: i64) -> Result<serde_json::Value, String> {
    project_exists(conn, project_id)?;
    let Some(doc) = doc_get(conn, project_id, "aqc.project")? else {
        return Ok(serde_json::json!({
            "present": false,
            "project_id": project_id,
            "reason": "O-CoreOp has not saved a quantities document for this project yet."
        }));
    };
    let body: serde_json::Value = serde_json::from_str(&doc.body).map_err(|_| "The O-CoreOp project document could not be read.".to_string())?;
    let Some(estimate) = body.get("last_estimate").filter(|v| !v.is_null()) else {
        return Ok(serde_json::json!({
            "present": false,
            "project_id": project_id,
            "document_revision": doc.revision,
            "updated_by": doc.updated_by,
            "updated_at": doc.updated_at,
            "reason": "O-CoreOp has not calculated an estimate for this project yet."
        }));
    };
    let grand = estimate.get("grand_total").and_then(|v| v.as_f64()).unwrap_or(0.0);
    let base = estimate.get("base_total").and_then(|v| v.as_f64()).unwrap_or(0.0);
    // Fee proposals store money in paise; round half-up from rupees.
    let grand_paise = (grand * 100.0).round() as i64;
    let base_paise = (base * 100.0).round() as i64;
    Ok(serde_json::json!({
        "present": true,
        "project_id": project_id,
        "document_revision": doc.revision,
        "updated_by": doc.updated_by,
        "updated_at": doc.updated_at,
        "grand_total_rupees": grand,
        "grand_total_paise": grand_paise,
        "base_total_rupees": base,
        "base_total_paise": base_paise,
        "rate_book_version_id": estimate.get("rate_book_version_id").and_then(|v| v.as_str()).unwrap_or(""),
        "rate_book_version_name": estimate.get("rate_book_version_name").and_then(|v| v.as_str()).unwrap_or(""),
        "artifact_ref": estimate.get("artifact_ref").cloned().unwrap_or(serde_json::Value::Null),
        "project_ref": body.get("project_ref").cloned().unwrap_or(serde_json::Value::Null),
    }))
}

/// What O-Studio shows next to fee / invoice screens: running-account (RA) bills O-CoreOp saved on this project.
///
/// Reads `aqc.project` → `accounts.bills[]`. Gross/net are recomputed from lines + rates (CoreOp stores
/// percentages, not totals). Money is returned in paise like [`estimate_summary`].
pub fn ra_summary(conn: &Connection, project_id: i64) -> Result<serde_json::Value, String> {
    project_exists(conn, project_id)?;
    let Some(doc) = doc_get(conn, project_id, "aqc.project")? else {
        return Ok(serde_json::json!({
            "present": false,
            "project_id": project_id,
            "reason": "O-CoreOp has not saved a quantities document for this project yet."
        }));
    };
    let body: serde_json::Value = serde_json::from_str(&doc.body).map_err(|_| "The O-CoreOp project document could not be read.".to_string())?;
    let Some(bills) = body.pointer("/accounts/bills").and_then(|v| v.as_array()).filter(|a| !a.is_empty()) else {
        return Ok(serde_json::json!({
            "present": false,
            "project_id": project_id,
            "document_revision": doc.revision,
            "updated_by": doc.updated_by,
            "updated_at": doc.updated_at,
            "reason": "O-CoreOp has not saved any running bills for this project yet."
        }));
    };
    let mut certified = 0i64;
    let mut last: Option<&serde_json::Value> = None;
    let mut last_net = 0.0f64;
    let mut last_gross = 0.0f64;
    for bill in bills {
        let certified_flag = bill.get("certified").and_then(|v| v.as_i64()).unwrap_or(0) != 0
            || bill.get("certified").and_then(|v| v.as_bool()).unwrap_or(false);
        if certified_flag {
            certified += 1;
        }
        let gross: f64 = bill
            .get("lines")
            .and_then(|v| v.as_array())
            .into_iter()
            .flatten()
            .filter_map(|l| l.get("amount").and_then(|a| a.as_f64()).or_else(|| {
                let qty = l.get("qty").and_then(|q| q.as_f64())?;
                let rate = l.get("rate").and_then(|r| r.as_f64())?;
                Some(qty * rate)
            }))
            .sum();
        let gst_pct = bill.get("gst_pct").and_then(|v| v.as_f64()).unwrap_or(0.0);
        let retention_pct = bill.get("retention_pct").and_then(|v| v.as_f64()).unwrap_or(0.0);
        let tds_pct = bill.get("tds_pct").and_then(|v| v.as_f64()).unwrap_or(0.0);
        let cess_pct = bill.get("cess_pct").and_then(|v| v.as_f64()).unwrap_or(0.0);
        let gst_tds_pct = bill.get("gst_tds_pct").and_then(|v| v.as_f64()).unwrap_or(0.0);
        let other = bill.get("other_deductions").and_then(|v| v.as_f64()).unwrap_or(0.0);
        let advance = bill.get("advance_recovery").and_then(|v| v.as_f64()).unwrap_or(0.0);
        let invoice = gross + gross * gst_pct / 100.0;
        let deductions = gross * (retention_pct + tds_pct + cess_pct + gst_tds_pct) / 100.0 + other + advance;
        let net = invoice - deductions;
        last = Some(bill);
        last_gross = gross;
        last_net = net;
    }
    let last_number = last.and_then(|b| b.get("number").and_then(|n| n.as_str())).unwrap_or("");
    Ok(serde_json::json!({
        "present": true,
        "project_id": project_id,
        "document_revision": doc.revision,
        "updated_by": doc.updated_by,
        "updated_at": doc.updated_at,
        "bill_count": bills.len(),
        "certified_count": certified,
        "last_bill_number": last_number,
        "last_gross_rupees": last_gross,
        "last_gross_paise": (last_gross * 100.0).round() as i64,
        "last_net_rupees": last_net,
        "last_net_paise": (last_net * 100.0).round() as i64,
        "project_ref": body.get("project_ref").cloned().unwrap_or(serde_json::Value::Null),
    }))
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
        for bad in ["", "Aqc", "1abc", "a b", &"a".repeat(MAX_KIND_LEN + 1)] {
            assert!(doc_put(&c, 7, bad, 0, "x", "a").is_err(), "{bad:?}");
        }
        assert_eq!(doc_put(&c, 99, "aqc.project", 0, "x", "a").unwrap_err(), "Project not found.");
        doc_put(&c, 7, "aqc.rates", 0, "r", "a").unwrap();
        doc_put(&c, 7, "aqc.project", 0, "p", "a").unwrap();
        assert_eq!(doc_list(&c, 7).unwrap().iter().map(|d| d.kind.as_str()).collect::<Vec<_>>(), ["aqc.project", "aqc.rates"]);
        // CoreOp RA draft kinds: prefix + 32-char Guid "N" form (exceeds the old 40-char limit).
        let ra = format!("aqc.ra.draft.{}", "b".repeat(32));
        assert!(ra.len() > 40 && ra.len() <= MAX_KIND_LEN);
        doc_put(&c, 7, &ra, 0, "bill", "a").unwrap();
        assert!(doc_list(&c, 7).unwrap().iter().any(|d| d.kind == ra));
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

    #[test]
    fn estimate_summary_reads_coreop_last_estimate_totals_and_refs() {
        let c = conn();
        let missing = estimate_summary(&c, 7).unwrap();
        assert_eq!(missing["present"], false);
        let body = r#"{
            "project_ref": { "v": 1, "company_uuid": "u-1", "project_id": 7 },
            "last_estimate": {
                "grand_total": 1234.56,
                "base_total": 1000.0,
                "rate_book_version_name": "DSR 2024",
                "artifact_ref": { "v": 1, "kind": "estimate", "id": "last_estimate", "revision": 2,
                    "checksum_sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "project": { "v": 1, "company_uuid": "u-1", "project_id": 7 }, "made_from": [] }
            }
        }"#;
        doc_put(&c, 7, "aqc.project", 0, body, "coreop").unwrap();
        let got = estimate_summary(&c, 7).unwrap();
        assert_eq!(got["present"], true);
        assert_eq!(got["grand_total_paise"], 123456);
        assert_eq!(got["rate_book_version_name"], "DSR 2024");
        assert_eq!(got["artifact_ref"]["id"], "last_estimate");
        assert_eq!(got["project_ref"]["project_id"], 7);
        assert_eq!(got["document_revision"], 1);
    }

    #[test]
    fn ra_summary_reads_coreop_accounts_bills() {
        let c = conn();
        assert_eq!(ra_summary(&c, 7).unwrap()["present"], false);
        let body = r#"{
            "project_ref": { "v": 1, "company_uuid": "u-1", "project_id": 7 },
            "accounts": {
                "bills": [
                    {
                        "number": "RA-1", "certified": 1, "gst_pct": 0, "retention_pct": 5,
                        "tds_pct": 0, "cess_pct": 0, "gst_tds_pct": 0, "other_deductions": 0, "advance_recovery": 0,
                        "lines": [ { "amount": 10000.0 } ]
                    },
                    {
                        "number": "RA-2", "certified": 0, "gst_pct": 18, "retention_pct": 5,
                        "tds_pct": 1, "cess_pct": 1, "gst_tds_pct": 0, "other_deductions": 100.0, "advance_recovery": 0,
                        "lines": [ { "qty": 10.0, "rate": 200.0 } ]
                    }
                ]
            }
        }"#;
        doc_put(&c, 7, "aqc.project", 0, body, "coreop").unwrap();
        let got = ra_summary(&c, 7).unwrap();
        assert_eq!(got["present"], true);
        assert_eq!(got["bill_count"], 2);
        assert_eq!(got["certified_count"], 1);
        assert_eq!(got["last_bill_number"], "RA-2");
        // gross 2000; invoice 2360; deductions 5%+1%+1% of 2000 + 100 = 240; net 2120
        assert_eq!(got["last_gross_paise"], 200000);
        assert_eq!(got["last_net_paise"], 212000);
    }
}
