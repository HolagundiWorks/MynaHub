//! CoreOp → Studio draft share (project documents).
//!
//! CoreOp writes envelopes via `doc_put` with kinds `aqc.estimate.draft` and
//! `aqc.ra.draft.<bill_id>` (see O-CoreOp `docs/STUDIO-DRAFT-SHARE.md`).
//! Studio lists those kinds, shows summary fields, then sets envelope `state`
//! to `approved_locked`. Corrections require a new CoreOp share (new revision),
//! never an in-place edit of a locked body.
//!
//! Parallel path: `approvals` / `suite_approvals` remains for tools that still
//! call `approval_*`; product CoreOp share uses this document path.

use crate::schema::audit;
use crate::share::{self, Document};
use rusqlite::{params, Connection};
use serde::Serialize;
use serde_json::{json, Value};

pub const ESTIMATE_DRAFT_KIND: &str = "aqc.estimate.draft";
pub const RA_DRAFT_KIND_PREFIX: &str = "aqc.ra.draft.";

pub const STATE_DRAFT: &str = "draft";
pub const STATE_PENDING: &str = "pending_approval";
pub const STATE_LOCKED: &str = "approved_locked";
pub const STATE_SUPERSEDED: &str = "superseded";

#[derive(Debug, Serialize, Clone, PartialEq)]
pub struct DraftInfo {
    pub project_id: i64,
    pub kind: String,
    pub revision: i64,
    pub state: String,
    pub artifact_kind: String,
    pub title: String,
    pub shared_by: String,
    pub shared_at: String,
    pub summary: Value,
    pub checksum_sha256: String,
    pub updated_by: String,
    pub updated_at: String,
    pub bytes: i64,
}

#[derive(Debug, Serialize, Clone, PartialEq)]
pub struct Draft {
    pub project_id: i64,
    pub kind: String,
    pub revision: i64,
    pub state: String,
    pub artifact_kind: String,
    pub shared_by: String,
    pub shared_at: String,
    pub summary: Value,
    pub body: Value,
    pub artifact_ref: Option<Value>,
    pub envelope: Value,
    pub checksum_sha256: String,
    pub updated_by: String,
    pub updated_at: String,
}

pub fn is_studio_draft_kind(kind: &str) -> bool {
    kind == ESTIMATE_DRAFT_KIND || kind.starts_with(RA_DRAFT_KIND_PREFIX)
}

fn parse_envelope(body: &str) -> Result<Value, String> {
    serde_json::from_str(body).map_err(|_| "That draft document is not valid JSON.".into())
}

fn envelope_field<'a>(env: &'a Value, key: &str) -> &'a Value {
    env.get(key).unwrap_or(&Value::Null)
}

fn str_field(env: &Value, key: &str) -> String {
    envelope_field(env, key).as_str().unwrap_or("").to_string()
}

fn title_from(env: &Value) -> String {
    env.get("summary")
        .and_then(|s| s.get("title"))
        .and_then(|t| t.as_str())
        .unwrap_or("")
        .to_string()
}

fn info_from_row(
    project_id: i64,
    kind: String,
    revision: i64,
    body: &str,
    checksum: String,
    updated_by: String,
    updated_at: String,
    bytes: i64,
) -> Result<DraftInfo, String> {
    let env = parse_envelope(body)?;
    Ok(DraftInfo {
        project_id,
        kind,
        revision,
        state: str_field(&env, "state"),
        artifact_kind: str_field(&env, "artifact_kind"),
        title: title_from(&env),
        shared_by: str_field(&env, "shared_by"),
        shared_at: str_field(&env, "shared_at"),
        summary: env.get("summary").cloned().unwrap_or(Value::Null),
        checksum_sha256: checksum,
        updated_by,
        updated_at,
        bytes,
    })
}

fn draft_from_doc(doc: Document) -> Result<Draft, String> {
    let env = parse_envelope(&doc.body)?;
    Ok(Draft {
        project_id: doc.project_id,
        kind: doc.kind,
        revision: doc.revision,
        state: str_field(&env, "state"),
        artifact_kind: str_field(&env, "artifact_kind"),
        shared_by: str_field(&env, "shared_by"),
        shared_at: str_field(&env, "shared_at"),
        summary: env.get("summary").cloned().unwrap_or(Value::Null),
        body: env.get("body").cloned().unwrap_or(Value::Null),
        artifact_ref: env.get("artifact_ref").cloned(),
        envelope: env,
        checksum_sha256: doc.checksum_sha256,
        updated_by: doc.updated_by,
        updated_at: doc.updated_at,
    })
}

/// Lists CoreOp draft documents across projects (or one project). Optional `state` filter
/// matches envelope `state` (`draft`, `pending_approval`, `approved_locked`, `superseded`).
pub fn list(conn: &Connection, project_id: Option<i64>, state: Option<&str>) -> Result<Vec<DraftInfo>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT project_id, kind, revision, body, checksum_sha256, updated_by, updated_at, length(body)
             FROM project_documents
             WHERE (kind = ?1 OR kind LIKE ?2)
               AND (?3 IS NULL OR project_id = ?3)
             ORDER BY updated_at DESC, kind",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(
            params![ESTIMATE_DRAFT_KIND, format!("{RA_DRAFT_KIND_PREFIX}%"), project_id],
            |r| {
                Ok((
                    r.get::<_, i64>(0)?,
                    r.get::<_, String>(1)?,
                    r.get::<_, i64>(2)?,
                    r.get::<_, String>(3)?,
                    r.get::<_, String>(4)?,
                    r.get::<_, String>(5)?,
                    r.get::<_, String>(6)?,
                    r.get::<_, i64>(7)?,
                ))
            },
        )
        .map_err(|e| e.to_string())?;

    let mut out = Vec::new();
    for row in rows {
        let (pid, kind, revision, body, checksum, by, at, bytes) = row.map_err(|e| e.to_string())?;
        let info = info_from_row(pid, kind, revision, &body, checksum, by, at, bytes)?;
        if let Some(want) = state {
            if info.state != want {
                continue;
            }
        }
        out.push(info);
    }
    Ok(out)
}

pub fn get(conn: &Connection, project_id: i64, kind: &str) -> Result<Option<Draft>, String> {
    if !is_studio_draft_kind(kind) {
        return Err("That kind is not a Studio draft document.".into());
    }
    match share::doc_get(conn, project_id, kind)? {
        Some(doc) => Ok(Some(draft_from_doc(doc)?)),
        None => Ok(None),
    }
}

fn username(conn: &Connection, user_id: i64) -> String {
    conn.query_row("SELECT username FROM users WHERE id = ?1", params![user_id], |r| r.get(0))
        .unwrap_or_else(|_| format!("user:{user_id}"))
}

fn set_state(
    conn: &Connection,
    actor: i64,
    project_id: i64,
    kind: &str,
    expected_revision: i64,
    new_state: &str,
    audit_event: &str,
) -> Result<Draft, String> {
    if !is_studio_draft_kind(kind) {
        return Err("That kind is not a Studio draft document.".into());
    }
    let doc = share::doc_get(conn, project_id, kind)?.ok_or_else(|| "That draft was not found.".to_string())?;
    if doc.revision != expected_revision {
        return Err("This draft was changed since you opened it. Reload it before saving.".into());
    }
    let mut env = parse_envelope(&doc.body)?;
    let current = str_field(&env, "state");
    match new_state {
        STATE_LOCKED => {
            if current != STATE_DRAFT && current != STATE_PENDING {
                return Err("Only a draft (or pending) share can be approved and locked.".into());
            }
        }
        STATE_PENDING => {
            if current != STATE_DRAFT {
                return Err("Only a draft can be marked pending approval.".into());
            }
        }
        STATE_SUPERSEDED => {
            if current != STATE_LOCKED {
                return Err("Only an approved locked draft can be marked superseded.".into());
            }
        }
        _ => return Err("Unknown draft state.".into()),
    }
    let obj = env.as_object_mut().ok_or_else(|| "That draft document is not a JSON object.".to_string())?;
    obj.insert("state".into(), json!(new_state));
    if new_state == STATE_LOCKED {
        let decided_at: String = conn
            .query_row("SELECT strftime('%Y-%m-%dT%H:%M:%fZ', 'now')", [], |r| r.get(0))
            .unwrap_or_else(|_| "now".into());
        obj.insert("decided_by".into(), json!(username(conn, actor)));
        obj.insert("decided_at".into(), json!(decided_at));
    }
    let by = username(conn, actor);
    let body = serde_json::to_string(&env).map_err(|e| e.to_string())?;
    let (revision, _) = share::doc_put_unchecked(conn, project_id, kind, expected_revision, &body, &by)?;
    audit(conn, Some(actor), audit_event, &format!("project {project_id} {kind} → {new_state} revision {revision}"));
    get(conn, project_id, kind)?.ok_or_else(|| "That draft was not found after save.".into())
}

/// Studio approve: envelope `state` → `approved_locked`. Body is unchanged.
pub fn approve(conn: &Connection, actor: i64, project_id: i64, kind: &str, expected_revision: i64) -> Result<Draft, String> {
    set_state(conn, actor, project_id, kind, expected_revision, STATE_LOCKED, "studio_draft_approved")
}

/// Optional inbox step before approve.
pub fn mark_pending(conn: &Connection, actor: i64, project_id: i64, kind: &str, expected_revision: i64) -> Result<Draft, String> {
    set_state(conn, actor, project_id, kind, expected_revision, STATE_PENDING, "studio_draft_pending")
}

/// Marks a locked share superseded when a revise cycle starts in Studio.
/// Product revise still requires a new CoreOp `doc_put` with `state: draft`.
pub fn mark_superseded(conn: &Connection, actor: i64, project_id: i64, kind: &str, expected_revision: i64) -> Result<Draft, String> {
    set_state(conn, actor, project_id, kind, expected_revision, STATE_SUPERSEDED, "studio_draft_superseded")
}

/// CoreOp shares drafts through `doc_put` with Edit permission. Approval states are Studio-only (Issue permission).
pub fn validate_coreop_share_body(body: &str) -> Result<(), String> {
    let env = parse_envelope(body)?;
    if str_field(&env, "state") != STATE_DRAFT {
        return Err(
            "Approval states can only be set from O-Studio (Practice → Approvals), not through a direct document save.".into(),
        );
    }
    if envelope_field(&env, "v").as_u64() != Some(1) {
        return Err("Draft envelope version must be 1.".into());
    }
    let kind = str_field(&env, "artifact_kind");
    if kind != "estimate" && kind != "ra_bill" {
        return Err("Draft artifact_kind must be estimate or ra_bill.".into());
    }
    if !env.get("summary").map(|s| s.is_object()).unwrap_or(false) {
        return Err("Draft envelope must include a summary object.".into());
    }
    if !env.get("body").map(|b| b.is_object()).unwrap_or(false) {
        return Err("Draft envelope must include a body object.".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::share::doc_put;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::schema::migrate(&mut c).unwrap();
        c.execute("INSERT INTO clients (id, name) VALUES (1, 'Client')", []).unwrap();
        c.execute("INSERT INTO projects (id, client_id, name) VALUES (7, 1, 'House')", []).unwrap();
        c.execute(
            "INSERT INTO users (id, username, password_hash, role) VALUES (1, 'owner', 'x', 'principal')",
            [],
        )
        .unwrap();
        c
    }

    fn estimate_envelope(state: &str) -> String {
        json!({
            "v": 1,
            "state": state,
            "artifact_kind": "estimate",
            "shared_at": "2026-10-28T12:00:00.0000000Z",
            "shared_by": "estimator",
            "summary": { "title": "Tower", "currency": "INR", "grand_total": 42.0 },
            "body": { "total": 42 }
        })
        .to_string()
    }

    fn ra_kind() -> String {
        format!("{RA_DRAFT_KIND_PREFIX}{}", "a".repeat(32))
    }

    #[test]
    fn coreop_cannot_set_approval_state_through_doc_put() {
        let c = conn();
        let locked = estimate_envelope(STATE_LOCKED);
        assert!(doc_put(&c, 7, ESTIMATE_DRAFT_KIND, 0, &locked, "bad").unwrap_err().contains("Approval states"));
    }

    #[test]
    fn lists_filters_and_approves_coreop_estimate_draft() {
        let c = conn();
        doc_put(&c, 7, ESTIMATE_DRAFT_KIND, 0, &estimate_envelope(STATE_DRAFT), "estimator").unwrap();
        doc_put(&c, 7, "aqc.project", 0, r#"{"accounts":{"bills":[]}}"#, "estimator").unwrap();
        let listed = list(&c, None, Some(STATE_DRAFT)).unwrap();
        assert_eq!(listed.len(), 1);
        assert_eq!(listed[0].kind, ESTIMATE_DRAFT_KIND);
        assert_eq!(listed[0].title, "Tower");
        let locked = approve(&c, 1, 7, ESTIMATE_DRAFT_KIND, 1).unwrap();
        assert_eq!(locked.state, STATE_LOCKED);
        assert_eq!(locked.revision, 2);
        assert_eq!(locked.body["total"], 42);
        assert!(approve(&c, 1, 7, ESTIMATE_DRAFT_KIND, 2).unwrap_err().contains("Only a draft"));
        assert_eq!(list(&c, Some(7), Some(STATE_LOCKED)).unwrap().len(), 1);
    }

    #[test]
    fn ra_draft_kind_with_guid_fits_and_locks() {
        let c = conn();
        let kind = ra_kind();
        assert!(kind.len() > 40, "GUID-based RA kinds exceed the old 40-char limit");
        let body = json!({
            "v": 1,
            "state": "draft",
            "artifact_kind": "ra_bill",
            "shared_at": "2026-10-28T12:00:00.0000000Z",
            "shared_by": "qs",
            "summary": { "title": "RA-1", "bill_id": "a".repeat(32), "currency": "INR", "gross": 10, "net": 9 },
            "body": { "id": "a".repeat(32) }
        })
        .to_string();
        doc_put(&c, 7, &kind, 0, &body, "qs").unwrap();
        let got = get(&c, 7, &kind).unwrap().unwrap();
        assert_eq!(got.artifact_kind, "ra_bill");
        let pending = mark_pending(&c, 1, 7, &kind, 1).unwrap();
        assert_eq!(pending.state, STATE_PENDING);
        let locked = approve(&c, 1, 7, &kind, 2).unwrap();
        assert_eq!(locked.state, STATE_LOCKED);
        let superseded = mark_superseded(&c, 1, 7, &kind, 3).unwrap();
        assert_eq!(superseded.state, STATE_SUPERSEDED);
    }

    #[test]
    fn stale_revision_on_approve_is_refused() {
        let c = conn();
        doc_put(&c, 7, ESTIMATE_DRAFT_KIND, 0, &estimate_envelope(STATE_DRAFT), "a").unwrap();
        assert!(approve(&c, 1, 7, ESTIMATE_DRAFT_KIND, 0).unwrap_err().contains("changed"));
    }
}
