//! Bills and estimates shared into Studio for approval.
//!
//! CoreOp (or any Edit caller) **submits** a draft. Studio **approves** it, which locks the body.
//! Corrections require a new version that **supersedes** the locked record — never an in-place edit.

use crate::hash::sha256_hex;
use crate::schema::audit;
use rusqlite::{params, Connection, OptionalExtension};
use serde::{Deserialize, Serialize};

pub const KINDS: [&str; 2] = ["estimate", "running_bill"];
pub const MAX_BODY: usize = 16 * 1024 * 1024;

#[derive(Debug, Serialize, Deserialize, Clone, PartialEq, Eq)]
pub struct Approval {
    pub id: i64,
    pub project_id: i64,
    pub kind: String,
    pub title: String,
    pub status: String,
    pub version: i64,
    pub body: String,
    pub checksum_sha256: String,
    pub supersedes_id: Option<i64>,
    pub submitted_by: String,
    pub submitted_at: String,
    pub decided_by: Option<String>,
    pub decided_at: Option<String>,
}

#[derive(Debug, Serialize, PartialEq, Eq)]
pub struct ApprovalInfo {
    pub id: i64,
    pub project_id: i64,
    pub kind: String,
    pub title: String,
    pub status: String,
    pub version: i64,
    pub checksum_sha256: String,
    pub supersedes_id: Option<i64>,
    pub submitted_by: String,
    pub submitted_at: String,
    pub decided_by: Option<String>,
    pub decided_at: Option<String>,
}

fn check_kind(kind: &str) -> Result<(), String> {
    if KINDS.contains(&kind) {
        Ok(())
    } else {
        Err("Kind must be estimate or running_bill.".into())
    }
}

fn map_row(r: &rusqlite::Row<'_>) -> Result<Approval, rusqlite::Error> {
    Ok(Approval {
        id: r.get(0)?,
        project_id: r.get(1)?,
        kind: r.get(2)?,
        title: r.get(3)?,
        status: r.get(4)?,
        version: r.get(5)?,
        body: r.get(6)?,
        checksum_sha256: r.get(7)?,
        supersedes_id: r.get(8)?,
        submitted_by: r.get(9)?,
        submitted_at: r.get(10)?,
        decided_by: r.get(11)?,
        decided_at: r.get(12)?,
    })
}

fn map_info(r: &rusqlite::Row<'_>) -> Result<ApprovalInfo, rusqlite::Error> {
    Ok(ApprovalInfo {
        id: r.get(0)?,
        project_id: r.get(1)?,
        kind: r.get(2)?,
        title: r.get(3)?,
        status: r.get(4)?,
        version: r.get(5)?,
        checksum_sha256: r.get(6)?,
        supersedes_id: r.get(7)?,
        submitted_by: r.get(8)?,
        submitted_at: r.get(9)?,
        decided_by: r.get(10)?,
        decided_at: r.get(11)?,
    })
}

/// Submit a draft from CoreOp (or peer). When `supersedes_id` names an approved_locked row of the same
/// project+kind, the new draft is the next version; the locked row is not changed until this draft is approved.
pub fn submit(
    conn: &Connection,
    actor: i64,
    project_id: i64,
    kind: &str,
    title: &str,
    body: &str,
    supersedes_id: Option<i64>,
) -> Result<Approval, String> {
    check_kind(kind)?;
    let title = title.trim();
    if title.is_empty() {
        return Err("Enter a title for this draft.".into());
    }
    if body.len() > MAX_BODY {
        return Err("This draft is too large to share.".into());
    }
    let exists: i64 = conn
        .query_row("SELECT COUNT(*) FROM projects WHERE id = ?1", params![project_id], |r| r.get(0))
        .map_err(|e| e.to_string())?;
    if exists == 0 {
        return Err("That project is not in this company file.".into());
    }
    let by: String = conn
        .query_row("SELECT username FROM users WHERE id = ?1", params![actor], |r| r.get(0))
        .map_err(|_| "Not signed in.".to_string())?;

    let version = if let Some(prev) = supersedes_id {
        let (p_project, p_kind, p_status, p_version): (i64, String, String, i64) = conn
            .query_row(
                "SELECT project_id, kind, status, version FROM suite_approvals WHERE id = ?1",
                params![prev],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)),
            )
            .map_err(|_| "The record to revise was not found.".to_string())?;
        if p_project != project_id || p_kind != kind {
            return Err("A revision must stay on the same project and kind.".into());
        }
        if p_status != "approved_locked" {
            return Err("Only an approved, locked record can be revised. Submit a new draft instead.".into());
        }
        p_version + 1
    } else {
        let max: Option<i64> = conn
            .query_row(
                "SELECT MAX(version) FROM suite_approvals WHERE project_id = ?1 AND kind = ?2",
                params![project_id, kind],
                |r| r.get(0),
            )
            .map_err(|e| e.to_string())?;
        max.unwrap_or(0) + 1
    };

    let open_draft: Option<i64> = conn
        .query_row(
            "SELECT id FROM suite_approvals WHERE project_id = ?1 AND kind = ?2 AND status = 'draft'",
            params![project_id, kind],
            |r| r.get(0),
        )
        .optional()
        .map_err(|e| e.to_string())?;
    if open_draft.is_some() {
        return Err("There is already a draft for this project and kind. Approve or withdraw it before sharing another.".into());
    }

    let checksum = sha256_hex(body.as_bytes());
    conn.execute(
        "INSERT INTO suite_approvals (project_id, kind, title, status, version, body, checksum_sha256, supersedes_id, submitted_by)
         VALUES (?1, ?2, ?3, 'draft', ?4, ?5, ?6, ?7, ?8)",
        params![project_id, kind, title, version, body, checksum, supersedes_id, by],
    )
    .map_err(|e| e.to_string())?;
    let id = conn.last_insert_rowid();
    audit(conn, Some(actor), "approval_submitted", &format!("{kind} {id} v{version}"));
    get(conn, id)?.ok_or_else(|| "Internal error.".into())
}

pub fn get(conn: &Connection, id: i64) -> Result<Option<Approval>, String> {
    conn.query_row(
        "SELECT id, project_id, kind, title, status, version, body, checksum_sha256, supersedes_id,
                submitted_by, submitted_at, decided_by, decided_at
         FROM suite_approvals WHERE id = ?1",
        params![id],
        map_row,
    )
    .optional()
    .map_err(|e| e.to_string())
}

pub fn list(conn: &Connection, project_id: Option<i64>, kind: Option<&str>, status: Option<&str>) -> Result<Vec<ApprovalInfo>, String> {
    if let Some(k) = kind {
        check_kind(k)?;
    }
    if let Some(s) = status {
        if !matches!(s, "draft" | "approved_locked" | "superseded") {
            return Err("Status must be draft, approved_locked or superseded.".into());
        }
    }
    let sql = "SELECT id, project_id, kind, title, status, version, checksum_sha256, supersedes_id,
                      submitted_by, submitted_at, decided_by, decided_at
               FROM suite_approvals
               WHERE (?1 IS NULL OR project_id = ?1)
                 AND (?2 IS NULL OR kind = ?2)
                 AND (?3 IS NULL OR status = ?3)
               ORDER BY submitted_at DESC, id DESC";
    let mut stmt = conn.prepare(sql).map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![project_id, kind, status], map_info)
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

/// Studio approve: lock the draft. If it supersedes a locked row, that row becomes superseded.
pub fn approve(conn: &Connection, actor: i64, id: i64) -> Result<Approval, String> {
    let row = get(conn, id)?.ok_or_else(|| "That draft was not found.".to_string())?;
    if row.status != "draft" {
        return Err("Only a draft can be approved.".into());
    }
    let by: String = conn
        .query_row("SELECT username FROM users WHERE id = ?1", params![actor], |r| r.get(0))
        .map_err(|_| "Not signed in.".to_string())?;
    if let Some(prev) = row.supersedes_id {
        conn.execute(
            "UPDATE suite_approvals SET status = 'superseded', decided_by = ?2, decided_at = datetime('now')
             WHERE id = ?1 AND status = 'approved_locked'",
            params![prev, by],
        )
        .map_err(|e| e.to_string())?;
    }
    conn.execute(
        "UPDATE suite_approvals SET status = 'approved_locked', decided_by = ?2, decided_at = datetime('now') WHERE id = ?1",
        params![id, by],
    )
    .map_err(|e| e.to_string())?;
    audit(conn, Some(actor), "approval_approved", &format!("{} {id}", row.kind));
    get(conn, id)?.ok_or_else(|| "Internal error.".into())
}

/// Withdraw a draft that was never approved (submitter or Studio).
pub fn withdraw(conn: &Connection, actor: i64, id: i64) -> Result<(), String> {
    let row = get(conn, id)?.ok_or_else(|| "That draft was not found.".to_string())?;
    if row.status != "draft" {
        return Err("Only a draft can be withdrawn. An approved record stays locked; submit a revised version instead.".into());
    }
    conn.execute("DELETE FROM suite_approvals WHERE id = ?1 AND status = 'draft'", params![id])
        .map_err(|e| e.to_string())?;
    audit(conn, Some(actor), "approval_withdrawn", &format!("{} {id}", row.kind));
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schema;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        schema::migrate(&mut c).unwrap();
        c.execute("INSERT INTO company_file (id, format, company_uuid, display_name) VALUES (1, 'ADMS', 'u', 'Studio')", [])
            .unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('owner', 'x', 'owner')", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Client')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1, 'Tower')", []).unwrap();
        c
    }

    #[test]
    fn submit_approve_lock_and_revise() {
        let c = conn();
        let draft = submit(&c, 1, 1, "estimate", "Tower estimate", "{\"total\":100}", None).unwrap();
        assert_eq!(draft.status, "draft");
        assert_eq!(draft.version, 1);
        assert!(submit(&c, 1, 1, "estimate", "dup", "{}", None).is_err());

        let locked = approve(&c, 1, draft.id).unwrap();
        assert_eq!(locked.status, "approved_locked");
        assert!(approve(&c, 1, draft.id).is_err());

        let rev = submit(&c, 1, 1, "estimate", "Tower estimate rev", "{\"total\":110}", Some(draft.id)).unwrap();
        assert_eq!(rev.version, 2);
        assert_eq!(rev.supersedes_id, Some(draft.id));
        let again = approve(&c, 1, rev.id).unwrap();
        assert_eq!(again.status, "approved_locked");
        let old = get(&c, draft.id).unwrap().unwrap();
        assert_eq!(old.status, "superseded");
    }

    #[test]
    fn cannot_revise_unlocked_or_wrong_kind() {
        let c = conn();
        let draft = submit(&c, 1, 1, "running_bill", "RA-1", "{}", None).unwrap();
        assert!(submit(&c, 1, 1, "running_bill", "x", "{}", Some(draft.id)).unwrap_err().contains("approved"));
        let locked = approve(&c, 1, draft.id).unwrap();
        assert!(submit(&c, 1, 1, "estimate", "x", "{}", Some(locked.id)).unwrap_err().contains("same project"));
    }

    #[test]
    fn withdraw_draft_only() {
        let c = conn();
        let draft = submit(&c, 1, 1, "estimate", "T", "{}", None).unwrap();
        withdraw(&c, 1, draft.id).unwrap();
        assert!(get(&c, draft.id).unwrap().is_none());
        let draft = submit(&c, 1, 1, "estimate", "T", "{}", None).unwrap();
        approve(&c, 1, draft.id).unwrap();
        assert!(withdraw(&c, 1, draft.id).is_err());
    }
}
