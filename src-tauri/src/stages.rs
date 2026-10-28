//! Project stages.
//!
//! The consultancy and the number of floors choose a standard set. That set is the phase marker
//! and the payment schedule for a new or draft agreement. Marking a stage does not post a journal
//! and does not create an invoice. It records the phase and says whether the matching payment is due.
use crate::auth::Permission;
use crate::commands::{require, HostState, INTERNAL};
use crate::db;
use rusqlite::{params, Connection};
use serde::Serialize;

#[derive(Serialize)]
pub struct StageRow {
    pub id: i64,
    pub position: i64,
    pub name: String,
    pub share: String,
    pub marked_on: String,
    pub payment: String,
}

#[derive(Serialize)]
pub struct StagePlan {
    pub phase: String,
    pub floors: i64,
    pub stages: Vec<StageRow>,
    /// What assigning or marking changed, including the payment reminder.
    pub note: String,
}

/// Names and shares, in basis points, for one consultancy and one floor count. The shares add up to 100%.
pub fn standard_stages(consultancy: &str, floors: i64) -> Vec<(String, i64)> {
    let floors = floors.clamp(1, 40);
    match consultancy {
        "interior" => chain(
            &[("Appointment", 1_000), ("Concept and finishes", 2_000), ("Design development", 2_000)],
            &split("Working drawings", 3_000, floors),
            &[("Installation", 1_500), ("Handover", 500)],
        ),
        "landscape" => fixed(&[
            ("Appointment", 1_000),
            ("Concept", 2_000),
            ("Design development", 2_500),
            ("Working drawings and planting", 2_500),
            ("Site visits", 1_500),
            ("Handover", 500),
        ]),
        "urban" => fixed(&[("Appointment", 1_000), ("Study and concept", 2_500), ("Design", 3_000), ("Drawings", 2_000), ("Advice during implementation", 1_500)]),
        "site" => fixed(&[("Appointment", 1_000), ("Site study", 2_000), ("Layout", 3_000), ("Working drawings", 2_500), ("Site visits", 1_500)]),
        "conservation" => chain(
            &[("Survey and documentation", 2_000), ("Conservation concept", 1_500), ("Drawings for approval", 2_000)],
            &split("Working drawings", 3_000, floors),
            &[("Site visits", 1_500)],
        ),
        "retrofitting" => chain(
            &[("Survey", 1_500), ("Proposal", 1_500), ("Drawings for statutory approval", 2_000)],
            &split("Working drawings", 3_500, floors),
            &[("Site visits", 1_500)],
        ),
        "signage" => fixed(&[("Appointment", 1_500), ("Concept", 2_500), ("Design development", 3_000), ("Production drawings", 2_000), ("Installation", 1_000)]),
        _ => chain(
            &[("Appointment and brief", 1_000), ("Concept design", 1_500), ("Drawings for statutory approval", 2_000)],
            &split("Working drawings", 3_000, floors),
            &[("Periodic site visits", 1_500), ("Completion", 1_000)],
        ),
    }
}

fn fixed(rows: &[(&str, i64)]) -> Vec<(String, i64)> {
    rows.iter().map(|(name, bp)| ((*name).to_string(), *bp)).collect()
}

fn chain(before: &[(&str, i64)], middle: &[(String, i64)], after: &[(&str, i64)]) -> Vec<(String, i64)> {
    let mut rows = fixed(before);
    rows.extend(middle.iter().cloned());
    rows.extend(fixed(after));
    rows
}

/// One floor keeps the stage name. More floors split that share, and the last floor takes the remainder.
fn split(label: &str, pool: i64, floors: i64) -> Vec<(String, i64)> {
    let floors = floors.clamp(1, 40);
    if floors == 1 {
        return vec![(label.to_string(), pool)];
    }
    let each = pool / floors;
    let mut used = 0i64;
    let mut rows = Vec::new();
    for floor in 1..=floors {
        let share = if floor == floors { pool - used } else { each };
        used += share;
        if share > 0 {
            rows.push((format!("{label}, floor {floor}"), share));
        }
    }
    if rows.is_empty() {
        rows.push((label.to_string(), pool));
    }
    rows
}

pub fn share_text(bp: i64) -> String {
    let hundredths = bp.abs() % 100;
    let whole = format!("{}.{:02}", bp / 100, hundredths);
    whole.trim_end_matches('0').trim_end_matches('.').to_string()
}

fn date_ok(value: &str) -> Result<(), String> {
    let b = value.as_bytes();
    if b.len() != 10 || b[4] != b'-' || b[7] != b'-' || !b.iter().enumerate().all(|(i, c)| i == 4 || i == 7 || c.is_ascii_digit()) {
        return Err("Enter the date as YYYY-MM-DD.".into());
    }
    Ok(())
}

fn floors_ok(floors: i64) -> Result<i64, String> {
    if (1..=40).contains(&floors) {
        Ok(floors)
    } else {
        Err("Enter the number of floors from 1 to 40.".into())
    }
}

pub fn plan(conn: &Connection, project_id: i64) -> Result<StagePlan, String> {
    let floors: i64 = conn
        .query_row("SELECT floors FROM projects WHERE id = ?1", params![project_id], |r| r.get(0))
        .map_err(|_| "Project not found.".to_string())?;
    let mut stmt = conn
        .prepare("SELECT id, position, name, percent_bp, marked_on FROM project_stages WHERE project_id = ?1 ORDER BY position, id")
        .map_err(|e| e.to_string())?;
    let found = stmt
        .query_map(params![project_id], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, i64>(1)?, r.get::<_, String>(2)?, r.get::<_, i64>(3)?, r.get::<_, String>(4)?)))
        .map_err(|e| e.to_string())?;
    let mut stages = Vec::new();
    for row in found {
        let (id, position, name, bp, marked) = row.map_err(|e| e.to_string())?;
        let payment = payment_for(conn, project_id, &name)?;
        stages.push(StageRow { id, position, share: share_text(bp), marked_on: marked, payment, name });
    }
    let phase = if stages.is_empty() {
        String::new()
    } else {
        stages.iter().find(|s| s.marked_on.is_empty()).map(|s| s.name.clone()).unwrap_or_else(|| "Complete".to_string())
    };
    Ok(StagePlan { phase, floors, stages, note: String::new() })
}

/// The stage name and its percentage, for an agreement whose own milestone list is empty.
pub fn milestone_percents(conn: &Connection, project_id: i64) -> Result<Vec<(String, String)>, String> {
    let mut stmt = conn
        .prepare("SELECT name, percent_bp FROM project_stages WHERE project_id = ?1 ORDER BY position, id")
        .map_err(|e| e.to_string())?;
    let rows = stmt.query_map(params![project_id], |r| Ok((r.get::<_, String>(0)?, r.get::<_, i64>(1)?))).map_err(|e| e.to_string())?;
    let mut out = Vec::new();
    for row in rows {
        let (name, bp) = row.map_err(|e| e.to_string())?;
        out.push((name, share_text(bp)));
    }
    Ok(out)
}

fn payment_for(conn: &Connection, project_id: i64, stage: &str) -> Result<String, String> {
    let agreement: Option<(i64, i64)> = conn
        .query_row(
            "SELECT id, fee_paise FROM documents WHERE project_id = ?1 AND kind = 'agreement' AND status IN ('issued','signed') ORDER BY id DESC LIMIT 1",
            params![project_id],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .ok();
    let Some((doc_id, _)) = agreement else {
        return Ok("No issued agreement".into());
    };
    let found: Option<(i64, Option<i64>, Option<String>, Option<i64>)> = conn
        .query_row(
            "SELECT m.amount_paise, m.invoice_id, i.number,
                    (SELECT b.total_paise - b.received_paise - b.tds_paise - b.credited_paise - b.written_off_paise
                     FROM invoice_balances b WHERE b.invoice_id = m.invoice_id)
             FROM document_milestones m LEFT JOIN invoices i ON i.id = m.invoice_id
             WHERE m.document_id = ?1 AND m.name = ?2",
            params![doc_id, stage],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)),
        )
        .ok();
    let Some((amount, invoice_id, number, open)) = found else {
        return Ok("Not on the issued agreement".into());
    };
    let amount = crate::billing::fmt_inr(amount);
    if invoice_id.is_none() {
        return Ok(format!("Due {amount}"));
    }
    let open = open.unwrap_or(0);
    if open > 0 {
        Ok(format!("Open {} {amount}", number.unwrap_or_default()))
    } else {
        Ok(format!("Settled {}", number.unwrap_or_default()))
    }
}

fn reminder(conn: &Connection, project_id: i64, stage: &str) -> Result<String, String> {
    let payment = payment_for(conn, project_id, stage)?;
    let message = if payment.starts_with("Due ") {
        format!("{stage} is marked. Bill this stage: {payment}.")
    } else if payment.starts_with("Open ") {
        format!("{stage} is marked. The payment is still open: {payment}.")
    } else if payment.starts_with("Settled ") {
        format!("{stage} is marked. The payment is settled.")
    } else if payment == "No issued agreement" {
        format!("{stage} is marked. There is no issued agreement yet, so this stage has no bill.")
    } else {
        format!("{stage} is marked. The issued agreement has no payment line with this name.")
    };
    Ok(message)
}

pub fn apply(conn: &mut Connection, project_id: i64, consultancy: &str, floors: i64) -> Result<StagePlan, String> {
    let key = normalize_consultancy(consultancy)?;
    let floors = floors_ok(floors)?;
    let marked: i64 = conn
        .query_row("SELECT COUNT(*) FROM project_stages WHERE project_id = ?1 AND marked_on != ''", params![project_id], |r| r.get(0))
        .map_err(|_| "Project not found.".to_string())?;
    if marked > 0 {
        return Err("A stage is already marked. Clear the latest mark before assigning a new set.".into());
    }
    let exists: i64 = conn.query_row("SELECT COUNT(*) FROM projects WHERE id = ?1", params![project_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if exists == 0 {
        return Err("Project not found.".into());
    }
    let stages = standard_stages(&key, floors);
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    tx.execute("UPDATE projects SET consultancy_type = ?1, floors = ?2, updated_at = datetime('now') WHERE id = ?3", params![key, floors, project_id])
        .map_err(|e| e.to_string())?;
    tx.execute("DELETE FROM project_stages WHERE project_id = ?1", params![project_id]).map_err(|e| e.to_string())?;
    for (position, (name, bp)) in stages.iter().enumerate() {
        tx.execute(
            "INSERT INTO project_stages (project_id, position, name, percent_bp) VALUES (?1,?2,?3,?4)",
            params![project_id, position as i64, name, bp],
        )
        .map_err(|e| e.to_string())?;
    }
    let note = sync_draft(&tx, project_id, &stages)?;
    tx.commit().map_err(|e| e.to_string())?;
    let mut plan = plan(conn, project_id)?;
    plan.note = note;
    Ok(plan)
}

fn normalize_consultancy(value: &str) -> Result<String, String> {
    let key = value.trim();
    let known = ["comprehensive", "interior", "landscape", "urban", "site", "conservation", "retrofitting", "signage"];
    if known.contains(&key) {
        return Ok(key.to_string());
    }
    Err("Choose a consultancy type from the Council of Architecture schedule.".into())
}

fn sync_draft(conn: &Connection, project_id: i64, stages: &[(String, i64)]) -> Result<String, String> {
    let draft: Option<(i64, i64)> = conn
        .query_row(
            "SELECT id, fee_paise FROM documents WHERE project_id = ?1 AND kind = 'agreement' AND status = 'draft' ORDER BY id DESC LIMIT 1",
            params![project_id],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .ok();
    let issued: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM documents WHERE project_id = ?1 AND kind = 'agreement' AND status IN ('issued','signed')",
            params![project_id],
            |r| r.get(0),
        )
        .unwrap_or(0);
    let issued_note = if issued > 0 { " The issued agreement keeps the schedule it was issued with." } else { "" };
    let Some((doc_id, fee)) = draft else {
        return Ok(format!("These stages are the payment schedule for the next agreement.{issued_note}"));
    };
    conn.execute("DELETE FROM document_milestones WHERE document_id = ?1", params![doc_id]).map_err(|e| e.to_string())?;
    let mut amounts: Vec<i64> = stages.iter().map(|(_, bp)| (fee * bp + 5_000) / 10_000).collect();
    if stages.iter().map(|(_, bp)| bp).sum::<i64>() == 10_000 {
        let sum: i64 = amounts.iter().sum();
        if let Some(last) = amounts.last_mut() {
            *last += fee - sum;
        }
    }
    for (position, ((name, bp), amount)) in stages.iter().zip(amounts).enumerate() {
        conn.execute(
            "INSERT INTO document_milestones (document_id, position, name, percent_bp, amount_paise) VALUES (?1,?2,?3,?4,?5)",
            params![doc_id, position as i64, name, bp, amount],
        )
        .map_err(|e| e.to_string())?;
    }
    Ok(format!("The draft agreement now uses these stages.{issued_note}"))
}

pub fn mark(conn: &Connection, stage_id: i64, date: &str) -> Result<StagePlan, String> {
    date_ok(date)?;
    let (project_id, position, name, marked): (i64, i64, String, String) = conn
        .query_row("SELECT project_id, position, name, marked_on FROM project_stages WHERE id = ?1", params![stage_id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)))
        .map_err(|_| "Stage not found.".to_string())?;
    if !marked.is_empty() {
        return Err("This stage is already marked.".into());
    }
    let earlier: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM project_stages WHERE project_id = ?1 AND position < ?2 AND marked_on = ''",
            params![project_id, position],
            |r| r.get(0),
        )
        .map_err(|e| e.to_string())?;
    if earlier > 0 {
        return Err("Mark the stages in order. An earlier stage is still open.".into());
    }
    conn.execute("UPDATE project_stages SET marked_on = ?1 WHERE id = ?2", params![date, stage_id]).map_err(|e| e.to_string())?;
    let note = reminder(conn, project_id, &name)?;
    let mut plan = plan(conn, project_id)?;
    plan.note = note;
    Ok(plan)
}

pub fn unmark(conn: &Connection, stage_id: i64) -> Result<StagePlan, String> {
    let (project_id, position, marked): (i64, i64, String) = conn
        .query_row("SELECT project_id, position, marked_on FROM project_stages WHERE id = ?1", params![stage_id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
        .map_err(|_| "Stage not found.".to_string())?;
    if marked.is_empty() {
        return Err("This stage is not marked.".into());
    }
    let later: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM project_stages WHERE project_id = ?1 AND position > ?2 AND marked_on != ''",
            params![project_id, position],
            |r| r.get(0),
        )
        .map_err(|e| e.to_string())?;
    if later > 0 {
        return Err("Clear the latest marked stage first.".into());
    }
    conn.execute("UPDATE project_stages SET marked_on = '' WHERE id = ?1", params![stage_id]).map_err(|e| e.to_string())?;
    plan(conn, project_id)
}

pub fn project_stages(state: HostState<'_>, project_id: i64) -> Result<StagePlan, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    plan(&conn, project_id)
}

pub fn project_stages_apply(state: HostState<'_>, project_id: i64, consultancy: String, floors: i64) -> Result<StagePlan, String> {
    let uid = require(&state, Permission::Edit)?;
    let mut conn = state.db.lock().map_err(|_| INTERNAL)?;
    let plan = apply(&mut conn, project_id, &consultancy, floors)?;
    db::audit(&conn, Some(uid), "project_stages_assigned", &format!("project {project_id}"));
    Ok(plan)
}

pub fn project_stage_mark(state: HostState<'_>, stage_id: i64, marked_on: String) -> Result<StagePlan, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let plan = mark(&conn, stage_id, &marked_on)?;
    db::audit(&conn, Some(uid), "project_stage_marked", &format!("stage {stage_id}"));
    Ok(plan)
}

pub fn project_stage_unmark(state: HostState<'_>, stage_id: i64) -> Result<StagePlan, String> {
    let uid = require(&state, Permission::Edit)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    let plan = unmark(&conn, stage_id)?;
    db::audit(&conn, Some(uid), "project_stage_unmarked", &format!("stage {stage_id}"));
    Ok(plan)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name, consultancy_type) VALUES (1, 'Villa', 'comprehensive')", []).unwrap();
        c
    }

    #[test]
    fn the_standard_set_follows_the_consultancy_and_the_floors_and_adds_up() {
        let one = standard_stages("comprehensive", 1);
        assert_eq!(one.iter().map(|(_, bp)| bp).sum::<i64>(), 10_000);
        assert_eq!(one.iter().filter(|(name, _)| name.starts_with("Working drawings")).count(), 1);
        let three = standard_stages("comprehensive", 3);
        assert_eq!(three.iter().map(|(_, bp)| bp).sum::<i64>(), 10_000);
        assert_eq!(three.iter().filter(|(name, _)| name.starts_with("Working drawings")).count(), 3);
        let garden = standard_stages("landscape", 4);
        assert_eq!(garden.iter().filter(|(name, _)| name.contains("floor")).count(), 0);
        assert_eq!(garden.iter().map(|(_, bp)| bp).sum::<i64>(), 10_000);
        assert!(standard_stages("interior", 2).iter().any(|(name, _)| name == "Working drawings, floor 2"));
    }

    #[test]
    fn marking_a_stage_reminds_the_payment_and_does_not_create_an_invoice() {
        let mut c = conn();
        let plan = apply(&mut c, 1, "comprehensive", 2).unwrap();
        assert_eq!(plan.phase, "Appointment and brief");
        assert!(plan.note.contains("next agreement"));
        c.execute(
            "INSERT INTO documents (kind, project_id, status, number, doc_date, fee_paise, subject) VALUES ('agreement', 1, 'issued', 'AGR-1', '2026-10-01', 1000000, 'Agreement')",
            [],
        )
        .unwrap();
        let stages = standard_stages("comprehensive", 2);
        for (position, (name, bp)) in stages.iter().enumerate() {
            let amount = (1_000_000 * bp + 5_000) / 10_000;
            c.execute(
                "INSERT INTO document_milestones (document_id, position, name, percent_bp, amount_paise) VALUES (1, ?1, ?2, ?3, ?4)",
                params![position as i64, name, bp, amount],
            )
            .unwrap();
        }
        let first = plan.stages[0].id;
        let marked = mark(&c, first, "2026-10-17").unwrap();
        assert!(marked.note.contains("Bill this stage"), "{}", marked.note);
        assert_eq!(marked.phase, plan.stages[1].name);
        assert!(mark(&c, plan.stages[2].id, "2026-10-18").is_err());
        let invoices: i64 = c.query_row("SELECT COUNT(*) FROM invoices", [], |r| r.get(0)).unwrap();
        assert_eq!(invoices, 0, "marking a stage does not raise an invoice");
        let again = apply(&mut c, 1, "interior", 1);
        assert!(again.is_err());
    }
}
