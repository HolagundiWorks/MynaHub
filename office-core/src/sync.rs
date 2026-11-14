//! Cloud sync, wave 1 (docs/hub-sync-design.md): clients, projects, project stages and tasks.
//!
//! This module never touches the network. It keeps the change list (`sync_dirty`, filled by triggers once sync is enabled), builds the
//! records to push, records what the cloud acknowledged, and applies records pulled from the cloud. The C# side (`Adms.Cloud`) does the HTTPS.
//! Not synced on purpose: PAN and GSTIN of clients, a project's workspace path, `created_by` users, and everything outside wave 1.

use argon2::password_hash::rand_core::{OsRng, RngCore};
use rusqlite::{params, Connection, OptionalExtension};
use serde::Deserialize;
use serde_json::{json, Value};

pub const KINDS: [&str; 4] = ["client", "project", "project_stage", "task"];

fn rank(kind: &str) -> usize {
    KINDS.iter().position(|k| *k == kind).unwrap_or(99)
}

fn table(kind: &str) -> Result<&'static str, String> {
    Ok(match kind {
        "client" => "clients",
        "project" => "projects",
        "project_stage" => "project_stages",
        "task" => "tasks",
        _ => return Err(format!("Unknown sync kind {kind}.")),
    })
}

fn e(err: rusqlite::Error) -> String {
    err.to_string()
}

pub fn new_sync_id() -> String {
    let mut b = [0u8; 16];
    OsRng.fill_bytes(&mut b);
    b[6] = (b[6] & 0x0f) | 0x40;
    b[8] = (b[8] & 0x3f) | 0x80;
    let h: String = b.iter().map(|x| format!("{x:02x}")).collect();
    format!("{}-{}-{}-{}-{}", &h[0..8], &h[8..12], &h[12..16], &h[16..20], &h[20..32])
}

fn state_get(conn: &Connection, key: &str) -> Result<Option<String>, String> {
    conn.query_row("SELECT value FROM sync_state WHERE key = ?1", [key], |r| r.get(0)).optional().map_err(e)
}

fn state_set(conn: &Connection, key: &str, value: &str) -> Result<(), String> {
    conn.execute("INSERT INTO sync_state (key, value) VALUES (?1, ?2) ON CONFLICT (key) DO UPDATE SET value = excluded.value", params![key, value]).map_err(e)?;
    Ok(())
}

/// Turns change tracking on for a cloud company id. The first time, every existing row is queued for the initial push.
pub fn enable(conn: &Connection, cloud_company: &str) -> Result<(), String> {
    if cloud_company.trim().is_empty() {
        return Err("A cloud company id is required.".into());
    }
    if let Some(existing) = state_get(conn, "cloud_company")? {
        if existing != cloud_company {
            return Err("This company file is already linked to a different cloud company.".into());
        }
    }
    let first = state_get(conn, "enabled")?.is_none();
    state_set(conn, "cloud_company", cloud_company)?;
    state_set(conn, "enabled", "1")?;
    if first {
        for kind in KINDS {
            let t = table(kind)?;
            conn.execute(
                &format!(
                    "INSERT OR IGNORE INTO sync_dirty (kind, local_id, deleted, v, changed_ms)
                     SELECT ?1, id, 0, (SELECT IFNULL(MAX(v), 0) FROM sync_dirty) + ROW_NUMBER() OVER (ORDER BY id), CAST(strftime('%s','now') AS INTEGER) * 1000 FROM {t}"
                ),
                [kind],
            )
            .map_err(e)?;
        }
    }
    Ok(())
}

pub fn disable(conn: &Connection) -> Result<(), String> {
    state_set(conn, "enabled", "0")
}

pub fn status(conn: &Connection) -> Result<Value, String> {
    let pending: i64 = conn.query_row("SELECT COUNT(*) FROM sync_dirty", [], |r| r.get(0)).map_err(e)?;
    Ok(json!({
        "enabled": state_get(conn, "enabled")?.as_deref() == Some("1"),
        "cloud_company": state_get(conn, "cloud_company")?,
        "last_pulled_seq": state_get(conn, "last_pulled_seq")?.and_then(|s| s.parse::<i64>().ok()).unwrap_or(0),
        "pending": pending,
    }))
}

fn map_get(conn: &Connection, kind: &str, local_id: i64) -> Result<Option<(String, i64)>, String> {
    conn.query_row("SELECT sync_id, cloud_rev FROM sync_map WHERE kind = ?1 AND local_id = ?2", params![kind, local_id], |r| Ok((r.get(0)?, r.get(1)?)))
        .optional()
        .map_err(e)
}

fn map_ensure(conn: &Connection, kind: &str, local_id: i64) -> Result<(String, i64), String> {
    if let Some(found) = map_get(conn, kind, local_id)? {
        return Ok(found);
    }
    let id = new_sync_id();
    conn.execute("INSERT INTO sync_map (kind, local_id, sync_id) VALUES (?1, ?2, ?3)", params![kind, local_id, id]).map_err(e)?;
    Ok((id, 0))
}

fn local_of(conn: &Connection, kind: &str, sync_id: &str) -> Result<Option<i64>, String> {
    conn.query_row("SELECT local_id FROM sync_map WHERE kind = ?1 AND sync_id = ?2", params![kind, sync_id], |r| r.get(0)).optional().map_err(e)
}

fn body_of(conn: &Connection, kind: &str, id: i64) -> Result<Option<Value>, String> {
    match kind {
        "client" => conn
            .query_row("SELECT name, contact_person, email, phone, address, archived, created_at, updated_at FROM clients WHERE id = ?1", [id], |r| {
                Ok(json!({"name": r.get::<_, String>(0)?, "contact_person": r.get::<_, String>(1)?, "email": r.get::<_, String>(2)?, "phone": r.get::<_, String>(3)?,
                          "address": r.get::<_, String>(4)?, "archived": r.get::<_, i64>(5)?, "created_at": r.get::<_, String>(6)?, "updated_at": r.get::<_, String>(7)?}))
            })
            .optional()
            .map_err(e),
        "project" => {
            let row = conn
                .query_row(
                    "SELECT client_id, code, name, site_address, status, archived, summary, created_at, updated_at FROM projects WHERE id = ?1",
                    [id],
                    |r| Ok((r.get::<_, i64>(0)?, json!({"code": r.get::<_, String>(1)?, "name": r.get::<_, String>(2)?, "site_address": r.get::<_, String>(3)?,
                        "status": r.get::<_, String>(4)?, "archived": r.get::<_, i64>(5)?, "summary": r.get::<_, String>(6)?,
                        "created_at": r.get::<_, String>(7)?, "updated_at": r.get::<_, String>(8)?}))),
                )
                .optional()
                .map_err(e)?;
            match row {
                None => Ok(None),
                Some((client, mut body)) => {
                    body["client_sync_id"] = json!(map_ensure(conn, "client", client)?.0);
                    Ok(Some(body))
                }
            }
        }
        "project_stage" => {
            let row = conn
                .query_row("SELECT project_id, position, name, percent_bp, marked_on FROM project_stages WHERE id = ?1", [id], |r| {
                    Ok((r.get::<_, i64>(0)?, json!({"position": r.get::<_, i64>(1)?, "name": r.get::<_, String>(2)?, "percent_bp": r.get::<_, i64>(3)?, "marked_on": r.get::<_, String>(4)?})))
                })
                .optional()
                .map_err(e)?;
            match row {
                None => Ok(None),
                Some((project, mut body)) => {
                    body["project_sync_id"] = json!(map_ensure(conn, "project", project)?.0);
                    Ok(Some(body))
                }
            }
        }
        "task" => {
            let row = conn
                .query_row("SELECT project_id, title, notes, status, position, due_date, created_at, updated_at FROM tasks WHERE id = ?1", [id], |r| {
                    Ok((r.get::<_, Option<i64>>(0)?, json!({"title": r.get::<_, String>(1)?, "notes": r.get::<_, String>(2)?, "status": r.get::<_, String>(3)?,
                        "position": r.get::<_, i64>(4)?, "due_date": r.get::<_, String>(5)?, "created_at": r.get::<_, String>(6)?, "updated_at": r.get::<_, String>(7)?})))
                })
                .optional()
                .map_err(e)?;
            match row {
                None => Ok(None),
                Some((project, mut body)) => {
                    body["project_sync_id"] = match project {
                        Some(p) => json!(map_ensure(conn, "project", p)?.0),
                        None => Value::Null,
                    };
                    Ok(Some(body))
                }
            }
        }
        _ => Err(format!("Unknown sync kind {kind}.")),
    }
}

/// The next records to push, parents before children, at most `limit`. Each has `v`; hand it back to `ack` so a later edit is never cleared.
pub fn collect_push(conn: &Connection, limit: usize) -> Result<Vec<Value>, String> {
    let mut stmt = conn.prepare("SELECT kind, local_id, deleted, v, changed_ms FROM sync_dirty").map_err(e)?;
    let mut rows: Vec<(String, i64, bool, i64, i64)> = stmt
        .query_map([], |r| Ok((r.get(0)?, r.get(1)?, r.get::<_, i64>(2)? != 0, r.get(3)?, r.get(4)?)))
        .map_err(e)?
        .collect::<Result<_, _>>()
        .map_err(e)?;
    rows.sort_by_key(|(kind, _, _, v, _)| (rank(kind), *v));
    let mut out = Vec::new();
    for (kind, local_id, deleted, v, ms) in rows.into_iter().take(limit) {
        let (sync_id, cloud_rev) = map_ensure(conn, &kind, local_id)?;
        let body = if deleted { None } else { body_of(conn, &kind, local_id)? };
        let gone = deleted || body.is_none();
        out.push(json!({"kind": kind, "sync_id": sync_id, "base_rev": cloud_rev, "updated_ms": ms, "deleted": gone, "body": body.unwrap_or(json!({})), "v": v}));
    }
    Ok(out)
}

#[derive(Deserialize)]
pub struct Ack {
    pub kind: String,
    pub sync_id: String,
    pub v: i64,
    pub status: String,
    #[serde(default)]
    pub rev: i64,
}

/// Records the cloud's answer. created / updated / merged: remember the new revision. rejected_older: the cloud's newer version wins and arrives
/// on the next pull (our version is kept in the cloud conflict list). forbidden: stays queued. A row edited again since `collect_push` stays queued.
pub fn ack(conn: &Connection, items: &[Ack]) -> Result<Value, String> {
    let (mut done, mut kept) = (0, 0);
    for a in items {
        let Some(local_id) = local_of(conn, &a.kind, &a.sync_id)? else { continue };
        match a.status.as_str() {
            "created" | "updated" | "merged" => {
                conn.execute("UPDATE sync_map SET cloud_rev = ?1 WHERE kind = ?2 AND local_id = ?3", params![a.rev, a.kind, local_id]).map_err(e)?;
            }
            "rejected_older" => {}
            _ => {
                kept += 1;
                continue;
            }
        }
        let n = conn.execute("DELETE FROM sync_dirty WHERE kind = ?1 AND local_id = ?2 AND v = ?3", params![a.kind, local_id, a.v]).map_err(e)?;
        if n == 0 {
            kept += 1;
        } else {
            done += 1;
        }
    }
    Ok(json!({"cleared": done, "still_queued": kept}))
}

#[derive(Deserialize)]
pub struct Remote {
    pub kind: String,
    pub sync_id: String,
    pub rev: i64,
    pub seq: i64,
    #[serde(default)]
    pub deleted: bool,
    #[serde(default)]
    pub body: Value,
}

fn s<'a>(b: &'a Value, k: &str) -> &'a str {
    b.get(k).and_then(Value::as_str).unwrap_or("")
}
fn n(b: &Value, k: &str) -> i64 {
    b.get(k).and_then(Value::as_i64).unwrap_or(0)
}

enum Outcome {
    Applied,
    Deferred,
    SkippedDirty,
}

fn is_dirty(conn: &Connection, kind: &str, local: Option<i64>) -> Result<bool, String> {
    let Some(id) = local else { return Ok(false) };
    conn.query_row("SELECT 1 FROM sync_dirty WHERE kind = ?1 AND local_id = ?2", params![kind, id], |_| Ok(())).optional().map(|o| o.is_some()).map_err(e)
}

fn put_map(conn: &Connection, kind: &str, local_id: i64, sync_id: &str, rev: i64) -> Result<(), String> {
    conn.execute("DELETE FROM sync_map WHERE kind = ?1 AND local_id = ?2 AND sync_id <> ?3", params![kind, local_id, sync_id]).map_err(e)?;
    conn.execute(
        "INSERT INTO sync_map (kind, local_id, sync_id, cloud_rev) VALUES (?1, ?2, ?3, ?4)
         ON CONFLICT (kind, local_id) DO UPDATE SET sync_id = excluded.sync_id, cloud_rev = excluded.cloud_rev",
        params![kind, local_id, sync_id, rev],
    )
    .map_err(e)?;
    Ok(())
}

fn unmapped_match(conn: &Connection, kind: &str, sql: &str, args: &[&dyn rusqlite::ToSql]) -> Result<Option<i64>, String> {
    let found: Option<i64> = conn.query_row(sql, args, |r| r.get(0)).optional().map_err(e)?;
    match found {
        Some(id) if map_get(conn, kind, id)?.is_none() => Ok(Some(id)),
        _ => Ok(None),
    }
}

fn apply_one(conn: &Connection, r: &Remote) -> Result<Outcome, String> {
    let t = table(&r.kind)?;
    let local = local_of(conn, &r.kind, &r.sync_id)?;
    if is_dirty(conn, &r.kind, local)? {
        return Ok(Outcome::SkippedDirty);
    }
    if r.deleted {
        if let Some(id) = local {
            match r.kind.as_str() {
                // Clients and projects are archived, never deleted: issued documents point at them.
                "client" | "project" => {
                    conn.execute(&format!("UPDATE {t} SET archived = 1 WHERE id = ?1"), [id]).map_err(e)?;
                }
                _ => {
                    conn.execute(&format!("DELETE FROM {t} WHERE id = ?1"), [id]).map_err(e)?;
                }
            }
            conn.execute("UPDATE sync_map SET cloud_rev = ?1 WHERE kind = ?2 AND local_id = ?3", params![r.rev, r.kind, id]).map_err(e)?;
        }
        return Ok(Outcome::Applied);
    }
    let b = &r.body;
    let id = match r.kind.as_str() {
        "client" => {
            let id = match local {
                Some(id) => Some(id),
                None => unmapped_match(conn, "client", "SELECT id FROM clients WHERE name = ?1 COLLATE NOCASE", &[&s(b, "name")])?,
            };
            match id {
                Some(id) => {
                    conn.execute(
                        "UPDATE clients SET name=?1, contact_person=?2, email=?3, phone=?4, address=?5, archived=?6, updated_at=?7 WHERE id=?8",
                        params![s(b, "name"), s(b, "contact_person"), s(b, "email"), s(b, "phone"), s(b, "address"), n(b, "archived"), s(b, "updated_at"), id],
                    )
                    .map_err(e)?;
                    id
                }
                None => {
                    conn.execute(
                        "INSERT INTO clients (name, contact_person, email, phone, address, archived, created_at, updated_at) VALUES (?1,?2,?3,?4,?5,?6,?7,?8)",
                        params![s(b, "name"), s(b, "contact_person"), s(b, "email"), s(b, "phone"), s(b, "address"), n(b, "archived"), s(b, "created_at"), s(b, "updated_at")],
                    )
                    .map_err(e)?;
                    conn.last_insert_rowid()
                }
            }
        }
        "project" => {
            let Some(client) = local_of(conn, "client", s(b, "client_sync_id"))? else { return Ok(Outcome::Deferred) };
            let id = match local {
                Some(id) => Some(id),
                None => unmapped_match(conn, "project", "SELECT id FROM projects WHERE client_id = ?1 AND name = ?2 COLLATE NOCASE", &[&client, &s(b, "name")])?,
            };
            match id {
                Some(id) => {
                    conn.execute(
                        "UPDATE projects SET client_id=?1, code=?2, name=?3, site_address=?4, status=?5, archived=?6, summary=?7, updated_at=?8 WHERE id=?9",
                        params![client, s(b, "code"), s(b, "name"), s(b, "site_address"), s(b, "status"), n(b, "archived"), s(b, "summary"), s(b, "updated_at"), id],
                    )
                    .map_err(e)?;
                    id
                }
                None => {
                    conn.execute(
                        "INSERT INTO projects (client_id, code, name, site_address, status, archived, summary, created_at, updated_at) VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9)",
                        params![client, s(b, "code"), s(b, "name"), s(b, "site_address"), s(b, "status"), n(b, "archived"), s(b, "summary"), s(b, "created_at"), s(b, "updated_at")],
                    )
                    .map_err(e)?;
                    conn.last_insert_rowid()
                }
            }
        }
        "project_stage" => {
            let Some(project) = local_of(conn, "project", s(b, "project_sync_id"))? else { return Ok(Outcome::Deferred) };
            let id = match local {
                Some(id) => Some(id),
                None => unmapped_match(conn, "project_stage", "SELECT id FROM project_stages WHERE project_id = ?1 AND position = ?2", &[&project, &n(b, "position")])?,
            };
            match id {
                Some(id) => {
                    conn.execute(
                        "UPDATE project_stages SET project_id=?1, position=?2, name=?3, percent_bp=?4, marked_on=?5 WHERE id=?6",
                        params![project, n(b, "position"), s(b, "name"), n(b, "percent_bp"), s(b, "marked_on"), id],
                    )
                    .map_err(e)?;
                    id
                }
                None => {
                    conn.execute(
                        "INSERT INTO project_stages (project_id, position, name, percent_bp, marked_on) VALUES (?1,?2,?3,?4,?5)",
                        params![project, n(b, "position"), s(b, "name"), n(b, "percent_bp"), s(b, "marked_on")],
                    )
                    .map_err(e)?;
                    conn.last_insert_rowid()
                }
            }
        }
        "task" => {
            let project: Option<i64> = match b.get("project_sync_id").and_then(Value::as_str) {
                Some(p) => match local_of(conn, "project", p)? {
                    Some(id) => Some(id),
                    None => return Ok(Outcome::Deferred),
                },
                None => None,
            };
            match local {
                Some(id) => {
                    conn.execute(
                        "UPDATE tasks SET project_id=?1, title=?2, notes=?3, status=?4, position=?5, due_date=?6, updated_at=?7 WHERE id=?8",
                        params![project, s(b, "title"), s(b, "notes"), s(b, "status"), n(b, "position"), s(b, "due_date"), s(b, "updated_at"), id],
                    )
                    .map_err(e)?;
                    id
                }
                None => {
                    conn.execute(
                        "INSERT INTO tasks (project_id, title, notes, status, position, due_date, created_at, updated_at) VALUES (?1,?2,?3,?4,?5,?6,?7,?8)",
                        params![project, s(b, "title"), s(b, "notes"), s(b, "status"), n(b, "position"), s(b, "due_date"), s(b, "created_at"), s(b, "updated_at")],
                    )
                    .map_err(e)?;
                    conn.last_insert_rowid()
                }
            }
        }
        other => return Err(format!("Unknown sync kind {other}.")),
    };
    put_map(conn, &r.kind, id, &r.sync_id, r.rev)?;
    Ok(Outcome::Applied)
}

/// Applies records pulled from the cloud (any order). A record whose parent has not arrived yet is deferred and the cursor stops before it;
/// a record this PC has changed and not yet pushed is left alone (push first, the cloud decides). Returns the cursor to pull from next.
pub fn apply_pull(conn: &mut Connection, from_seq: i64, records: Vec<Remote>) -> Result<Value, String> {
    let mut records = records;
    records.sort_by_key(|r| (rank(&r.kind), r.seq));
    let tx = conn.transaction().map_err(e)?;
    state_set(&tx, "applying", "1")?;
    let (mut applied, mut deferred, mut skipped) = (0, 0, 0);
    let mut max_seq = from_seq;
    let mut floor: Option<i64> = None;
    for r in &records {
        max_seq = max_seq.max(r.seq);
        tx.execute_batch("SAVEPOINT rec").map_err(e)?;
        let outcome = apply_one(&tx, r);
        match outcome {
            Ok(Outcome::Applied) => {
                tx.execute_batch("RELEASE rec").map_err(e)?;
                applied += 1;
            }
            Ok(Outcome::SkippedDirty) => {
                tx.execute_batch("RELEASE rec").map_err(e)?;
                skipped += 1;
            }
            Ok(Outcome::Deferred) | Err(_) => {
                tx.execute_batch("ROLLBACK TO rec; RELEASE rec").map_err(e)?;
                deferred += 1;
                floor = Some(floor.map_or(r.seq - 1, |f| f.min(r.seq - 1)));
            }
        }
    }
    // Skipped rows were seen and are older than our pending edit; they do not hold the cursor back.
    let next = floor.map_or(max_seq, |f| f.max(from_seq));
    state_set(&tx, "last_pulled_seq", &next.to_string())?;
    state_set(&tx, "applying", "0")?;
    tx.commit().map_err(e)?;
    Ok(json!({"applied": applied, "deferred": deferred, "skipped_local_changes": skipped, "next_seq": next}))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schema::migrate;

    fn db() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        migrate(&mut c).unwrap();
        c
    }

    fn client(c: &Connection, name: &str) -> i64 {
        c.execute("INSERT INTO clients (name, pan, gstin) VALUES (?1, 'ABCDE1234F', '29ABCDE1234F1Z5')", [name]).unwrap();
        c.last_insert_rowid()
    }
    fn project(c: &Connection, client: i64, name: &str) -> i64 {
        c.execute("INSERT INTO projects (client_id, name) VALUES (?1, ?2)", params![client, name]).unwrap();
        c.last_insert_rowid()
    }
    fn dirty(c: &Connection) -> i64 {
        c.query_row("SELECT COUNT(*) FROM sync_dirty", [], |r| r.get(0)).unwrap()
    }
    fn remote(kind: &str, id: &str, seq: i64, body: Value) -> Remote {
        Remote { kind: kind.into(), sync_id: id.into(), rev: 1, seq, deleted: false, body }
    }

    #[test]
    fn nothing_is_tracked_until_sync_is_enabled_then_existing_rows_are_queued() {
        let c = db();
        let k = client(&c, "Acme");
        assert_eq!(dirty(&c), 0);
        enable(&c, "co-1").unwrap();
        assert_eq!(dirty(&c), 1);
        project(&c, k, "Tower");
        assert_eq!(dirty(&c), 2);
        assert!(enable(&c, "other").is_err(), "a file links to one cloud company");
        enable(&c, "co-1").unwrap();
        assert_eq!(dirty(&c), 2, "enabling again does not requeue");
    }

    #[test]
    fn a_push_has_parents_first_global_ids_and_no_tax_ids() {
        let c = db();
        let k = client(&c, "Acme");
        let p = project(&c, k, "Tower");
        c.execute("INSERT INTO project_stages (project_id, position, name, percent_bp) VALUES (?1, 1, 'Concept', 5000)", [p]).unwrap();
        enable(&c, "co-1").unwrap();
        let out = collect_push(&c, 100).unwrap();
        let kinds: Vec<&str> = out.iter().map(|r| r["kind"].as_str().unwrap()).collect();
        assert_eq!(kinds, ["client", "project", "project_stage"]);
        let text = serde_json::to_string(&out).unwrap();
        assert!(!text.contains("ABCDE1234F") && !text.contains("29ABCDE"), "PAN and GSTIN stay on this PC");
        assert_eq!(out[1]["body"]["client_sync_id"], out[0]["sync_id"]);
        assert_eq!(out[2]["body"]["project_sync_id"], out[1]["sync_id"]);
        assert_eq!(out[0]["sync_id"].as_str().unwrap().len(), 36);
    }

    #[test]
    fn an_acknowledgement_never_clears_a_newer_edit() {
        let c = db();
        let k = client(&c, "Acme");
        enable(&c, "co-1").unwrap();
        let first = collect_push(&c, 10).unwrap();
        c.execute("UPDATE clients SET phone = '555' WHERE id = ?1", [k]).unwrap(); // edited while the push was in flight
        let acks: Vec<Ack> = first.iter().map(|r| Ack { kind: r["kind"].as_str().unwrap().into(), sync_id: r["sync_id"].as_str().unwrap().into(), v: r["v"].as_i64().unwrap(), status: "created".into(), rev: 1 }).collect();
        let res = ack(&c, &acks).unwrap();
        assert_eq!(res["still_queued"], 1);
        assert_eq!(dirty(&c), 1);
        let again = collect_push(&c, 10).unwrap();
        assert_eq!(again[0]["base_rev"], 1, "the next push names the revision the cloud acknowledged");
        assert_eq!(again[0]["body"]["phone"], "555");
    }

    #[test]
    fn forbidden_stays_queued_and_rejected_older_is_cleared() {
        let c = db();
        client(&c, "A");
        client(&c, "B");
        enable(&c, "co-1").unwrap();
        let out = collect_push(&c, 10).unwrap();
        let mk = |r: &Value, status: &str| Ack { kind: "client".into(), sync_id: r["sync_id"].as_str().unwrap().into(), v: r["v"].as_i64().unwrap(), status: status.into(), rev: 3 };
        ack(&c, &[mk(&out[0], "forbidden"), mk(&out[1], "rejected_older")]).unwrap();
        assert_eq!(dirty(&c), 1);
    }

    #[test]
    fn pulling_builds_a_company_on_an_empty_file_without_echoing_it_back() {
        let mut c = db();
        enable(&c, "co-1").unwrap();
        let recs = vec![
            remote("task", "t1", 4, json!({"project_sync_id": "p1", "title": "Site visit", "status": "doing", "position": 1, "created_at": "2026-01-01", "updated_at": "2026-01-02"})),
            remote("project_stage", "s1", 3, json!({"project_sync_id": "p1", "position": 1, "name": "Concept", "percent_bp": 5000})),
            remote("project", "p1", 2, json!({"client_sync_id": "c1", "name": "Tower", "status": "active", "created_at": "2026-01-01", "updated_at": "2026-01-01"})),
            remote("client", "c1", 1, json!({"name": "Acme", "email": "a@example.test", "created_at": "2026-01-01", "updated_at": "2026-01-01"})),
        ];
        let res = apply_pull(&mut c, 0, recs).unwrap();
        assert_eq!(res["applied"], 4);
        assert_eq!(res["next_seq"], 4);
        let t: (String, String) = c.query_row("SELECT t.title, p.name FROM tasks t JOIN projects p ON p.id = t.project_id", [], |r| Ok((r.get(0)?, r.get(1)?))).unwrap();
        assert_eq!(t, ("Site visit".into(), "Tower".into()));
        assert_eq!(dirty(&c), 0, "applying remote rows must not queue them for pushing");
        assert_eq!(status(&c).unwrap()["last_pulled_seq"], 4);
    }

    #[test]
    fn a_remote_client_with_the_same_name_is_adopted_not_duplicated() {
        let mut c = db();
        let local = client(&c, "Acme");
        enable(&c, "co-1").unwrap();
        c.execute("DELETE FROM sync_dirty", []).unwrap();
        apply_pull(&mut c, 0, vec![remote("client", "c9", 1, json!({"name": "ACME", "phone": "999", "updated_at": "2026-02-02"}))]).unwrap();
        let n: i64 = c.query_row("SELECT COUNT(*) FROM clients", [], |r| r.get(0)).unwrap();
        assert_eq!(n, 1);
        let phone: String = c.query_row("SELECT phone FROM clients WHERE id = ?1", [local], |r| r.get(0)).unwrap();
        assert_eq!(phone, "999");
        assert_eq!(local_of(&c, "client", "c9").unwrap(), Some(local));
    }

    #[test]
    fn a_child_before_its_parent_is_deferred_and_holds_the_cursor() {
        let mut c = db();
        let res = apply_pull(&mut c, 10, vec![remote("task", "t1", 12, json!({"project_sync_id": "missing", "title": "x", "status": "todo"}))]).unwrap();
        assert_eq!(res["deferred"], 1);
        assert_eq!(res["next_seq"], 11, "the cursor stops just before the deferred record");
    }

    #[test]
    fn a_local_unpushed_edit_is_not_overwritten_by_a_pull() {
        let mut c = db();
        let k = client(&c, "Acme");
        enable(&c, "co-1").unwrap();
        let sid = map_ensure(&c, "client", k).unwrap().0;
        let res = apply_pull(&mut c, 0, vec![remote("client", &sid, 5, json!({"name": "Acme", "phone": "from-cloud"}))]).unwrap();
        assert_eq!(res["skipped_local_changes"], 1);
        let phone: String = c.query_row("SELECT phone FROM clients WHERE id = ?1", [k], |r| r.get(0)).unwrap();
        assert_eq!(phone, "");
    }

    #[test]
    fn tombstones_archive_clients_and_projects_and_remove_tasks_both_ways() {
        let mut c = db();
        let k = client(&c, "Acme");
        let p = project(&c, k, "Tower");
        c.execute("INSERT INTO tasks (project_id, title) VALUES (?1, 'T')", [p]).unwrap();
        let t = c.last_insert_rowid();
        enable(&c, "co-1").unwrap();
        let out = collect_push(&c, 10).unwrap();
        let acks: Vec<Ack> = out.iter().map(|r| Ack { kind: r["kind"].as_str().unwrap().into(), sync_id: r["sync_id"].as_str().unwrap().into(), v: r["v"].as_i64().unwrap(), status: "created".into(), rev: 1 }).collect();
        ack(&c, &acks).unwrap();
        assert_eq!(dirty(&c), 0);

        // local delete becomes a tombstone
        c.execute("DELETE FROM tasks WHERE id = ?1", [t]).unwrap();
        let del = collect_push(&c, 10).unwrap();
        assert_eq!(del.len(), 1);
        assert_eq!(del[0]["deleted"], true);
        c.execute("DELETE FROM sync_dirty", []).unwrap();

        // remote tombstones: the project is archived, not deleted
        let psid = map_get(&c, "project", p).unwrap().unwrap().0;
        let mut r = remote("project", &psid, 9, json!({}));
        r.deleted = true;
        r.rev = 2;
        apply_pull(&mut c, 0, vec![r]).unwrap();
        let archived: i64 = c.query_row("SELECT archived FROM projects WHERE id = ?1", [p], |r| r.get(0)).unwrap();
        assert_eq!(archived, 1);
    }
}
