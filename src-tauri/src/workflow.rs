//! The path of a project from first set-up to the last payment, and the single next thing to do on it.
//!
//! This reads records that already exist (stages, the agreement and its milestones, invoices and their balances). It posts
//! nothing and changes nothing. Each step is `done`, `next` (the one action to take now), `waiting` (on someone else or on
//! a later event), or `skipped` (not needed). The same answer drives the project page and the Pulse list of what needs attention.
use crate::auth::Permission;
use crate::commands::{require, HostState, INTERNAL};
use rusqlite::{params, Connection, OptionalExtension};
use serde::Serialize;

#[derive(Serialize, Debug, Clone, PartialEq)]
pub struct Step {
    pub key: &'static str,
    pub label: &'static str,
    /// `done`, `next`, `waiting` or `skipped`.
    pub state: &'static str,
    pub detail: String,
}

/// What to do now. `page` is where the person goes, `target_id` the record to open or act on.
#[derive(Serialize, Debug, Clone, PartialEq)]
pub struct NextAction {
    /// `assign_stages`, `new_agreement`, `issue_agreement`, `issue_invoice`, `bill_stage`, `record_payment`, `reconcile`, `mark_stage`.
    pub kind: &'static str,
    pub label: String,
    pub page: &'static str,
    pub target_id: Option<i64>,
}

#[derive(Serialize, Debug, Clone)]
pub struct ProjectPath {
    pub project_id: i64,
    pub project: String,
    pub client: String,
    pub steps: Vec<Step>,
    pub next: Option<NextAction>,
    /// One line for the project header, for example "Stage 3 of 7: Working drawings".
    pub summary: String,
}

fn rupees(paise: i64) -> String {
    crate::billing::fmt_inr(paise)
}

struct Agreement {
    id: i64,
    number: String,
    status: String,
}

fn latest_agreement(conn: &Connection, project_id: i64) -> Result<Option<Agreement>, String> {
    // an issued or signed agreement outranks a draft
    conn.query_row(
        "SELECT id, COALESCE(number,''), status FROM documents WHERE project_id = ?1 AND kind = 'agreement' AND status IN ('draft','issued','signed')
         ORDER BY (status <> 'draft') DESC, id DESC LIMIT 1",
        params![project_id],
        |r| Ok(Agreement { id: r.get(0)?, number: r.get(1)?, status: r.get(2)? }),
    )
    .optional()
    .map_err(|e| e.to_string())
}

pub fn project_path(conn: &Connection, project_id: i64) -> Result<ProjectPath, String> {
    let (project, client, consultancy, floors): (String, String, String, i64) = conn
        .query_row(
            "SELECT p.name, c.name, p.consultancy_type, p.floors FROM projects p JOIN clients c ON c.id = p.client_id WHERE p.id = ?1",
            params![project_id],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)),
        )
        .map_err(|_| "Project not found.".to_string())?;

    let mut steps: Vec<Step> = Vec::new();
    let mut next: Option<NextAction> = None;
    let want = |slot: &mut Option<NextAction>, action: NextAction| {
        if slot.is_none() {
            *slot = Some(action);
        }
    };

    steps.push(Step { key: "setup", label: "Client and project", state: "done", detail: format!("{client}, {project}") });

    // stages
    let stages: Vec<(i64, String, String)> = {
        let mut stmt = conn.prepare("SELECT id, name, marked_on FROM project_stages WHERE project_id = ?1 ORDER BY position, id").map_err(|e| e.to_string())?;
        let rows = stmt.query_map(params![project_id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?))).map_err(|e| e.to_string())?;
        rows.collect::<Result<_, _>>().map_err(|e| e.to_string())?
    };
    if stages.is_empty() {
        steps.push(Step { key: "stages", label: "Stages", state: "next", detail: format!("Choose the standard stages for a {consultancy} consultancy on {floors} floor{}.", if floors == 1 { "" } else { "s" }) });
        want(&mut next, NextAction { kind: "assign_stages", label: "Assign the standard stages".into(), page: "projects", target_id: Some(project_id) });
    } else {
        let marked = stages.iter().filter(|s| !s.2.is_empty()).count();
        steps.push(Step { key: "stages", label: "Stages", state: "done", detail: format!("{marked} of {} reached", stages.len()) });
    }

    // agreement
    let agreement = latest_agreement(conn, project_id)?;
    match &agreement {
        None => {
            steps.push(Step { key: "agreement", label: "Agreement", state: "next", detail: "No agreement yet. It takes its payment schedule from the stages.".into() });
            want(&mut next, NextAction { kind: "new_agreement", label: "Draft the agreement".into(), page: "documents", target_id: Some(project_id) });
        }
        Some(a) if a.status == "draft" => {
            steps.push(Step { key: "agreement", label: "Agreement", state: "next", detail: "A draft exists. Issue it to fix the payment schedule.".into() });
            want(&mut next, NextAction { kind: "issue_agreement", label: "Open the draft agreement".into(), page: "documents", target_id: Some(a.id) });
        }
        Some(a) => {
            let signed = if a.status == "signed" { "signed copy on file" } else { "signed copy not yet recorded" };
            steps.push(Step { key: "agreement", label: "Agreement", state: "done", detail: format!("{} issued, {signed}.", a.number) });
        }
    }

    // billing and collection
    let issued = agreement.as_ref().filter(|a| a.status != "draft");
    let drafts: Vec<(i64, String)> = {
        let mut stmt = conn.prepare("SELECT id, COALESCE(number,'Draft') FROM invoices WHERE project_id = ?1 AND status = 'draft' AND kind = 'tax_invoice' ORDER BY id").map_err(|e| e.to_string())?;
        let rows = stmt.query_map(params![project_id], |r| Ok((r.get(0)?, r.get(1)?))).map_err(|e| e.to_string())?;
        rows.collect::<Result<_, _>>().map_err(|e| e.to_string())?
    };
    let open: Vec<(i64, String, i64)> = {
        let mut stmt = conn
            .prepare(
                "SELECT i.id, i.number, b.total_paise - b.received_paise - b.tds_paise - b.credited_paise - b.written_off_paise
                 FROM invoices i JOIN invoice_balances b ON b.invoice_id = i.id
                 WHERE i.project_id = ?1 AND i.status = 'issued' AND i.kind = 'tax_invoice'
                   AND b.total_paise - b.received_paise - b.tds_paise - b.credited_paise - b.written_off_paise > 0
                 ORDER BY i.id",
            )
            .map_err(|e| e.to_string())?;
        let rows = stmt.query_map(params![project_id], |r| Ok((r.get(0)?, r.get::<_, Option<String>>(1)?.unwrap_or_default(), r.get(2)?))).map_err(|e| e.to_string())?;
        rows.collect::<Result<_, _>>().map_err(|e| e.to_string())?
    };

    // milestones of the issued agreement
    let mut due: Vec<(i64, String, i64)> = Vec::new(); // reached but not billed
    let (mut total_ms, mut billed_ms) = (0usize, 0usize);
    let mut next_unreached: Option<String> = None;
    if let Some(a) = issued {
        let mut stmt = conn
            .prepare(
                "SELECT m.id, m.name, m.amount_paise, m.invoice_id,
                        COALESCE((SELECT s.marked_on FROM project_stages s WHERE s.project_id = ?2 AND s.name = m.name LIMIT 1), '')
                 FROM document_milestones m WHERE m.document_id = ?1 ORDER BY m.position, m.id",
            )
            .map_err(|e| e.to_string())?;
        let rows = stmt
            .query_map(params![a.id, project_id], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, String>(1)?, r.get::<_, i64>(2)?, r.get::<_, Option<i64>>(3)?, r.get::<_, String>(4)?)))
            .map_err(|e| e.to_string())?;
        for row in rows {
            let (id, name, amount, invoice_id, marked) = row.map_err(|e| e.to_string())?;
            total_ms += 1;
            if invoice_id.is_some() {
                billed_ms += 1;
            } else if !marked.is_empty() {
                due.push((id, name, amount));
            } else if next_unreached.is_none() {
                next_unreached = Some(name);
            }
        }
    }

    if !drafts.is_empty() {
        let (id, _) = &drafts[0];
        steps.push(Step { key: "invoice", label: "Invoices", state: "next", detail: format!("{} draft invoice{} to review and issue.", drafts.len(), if drafts.len() == 1 { "" } else { "s" }) });
        want(&mut next, NextAction { kind: "issue_invoice", label: "Review and issue the draft invoice".into(), page: "invoices", target_id: Some(*id) });
    } else if let Some((id, name, amount)) = due.first() {
        steps.push(Step { key: "invoice", label: "Invoices", state: "next", detail: format!("{name} is reached and not billed: {}.", rupees(*amount)) });
        want(&mut next, NextAction { kind: "bill_stage", label: format!("Bill {name} ({})", rupees(*amount)), page: "invoices", target_id: Some(*id) });
    } else if issued.is_none() {
        steps.push(Step { key: "invoice", label: "Invoices", state: "waiting", detail: "Invoices follow the issued agreement.".into() });
    } else if total_ms == 0 {
        steps.push(Step { key: "invoice", label: "Invoices", state: "skipped", detail: "The agreement has no payment schedule; raise invoices directly.".into() });
    } else if billed_ms == total_ms {
        steps.push(Step { key: "invoice", label: "Invoices", state: "done", detail: format!("All {total_ms} payments billed.") });
    } else {
        let upcoming = next_unreached.unwrap_or_default();
        steps.push(Step { key: "invoice", label: "Invoices", state: "waiting", detail: format!("{billed_ms} of {total_ms} billed. Mark {upcoming} as reached to bill it.") });
        // the project has stages still to be reached; point at the stage page so the person can mark it
        if !stages.is_empty() {
            if let Some(stage) = stages.iter().find(|s| s.2.is_empty()) {
                want(&mut next, NextAction { kind: "mark_stage", label: format!("When {} is reached, mark it", stage.1), page: "projects", target_id: Some(project_id) });
            }
        }
    }

    // collection (a payment can be due while later stages are still ahead, so it outranks marking a stage)
    if open.is_empty() {
        let state = if billed_ms > 0 || issued.is_some() { "done" } else { "waiting" };
        steps.push(Step { key: "payment", label: "Payments", state, detail: if billed_ms > 0 { "Nothing is outstanding.".into() } else { "No invoices have been issued yet.".into() } });
    } else {
        let owed: i64 = open.iter().map(|o| o.2).sum();
        let (id, number, _) = &open[0];
        steps.push(Step { key: "payment", label: "Payments", state: "next", detail: format!("{} outstanding across {} invoice{}.", rupees(owed), open.len(), if open.len() == 1 { "" } else { "s" }) });
        // collecting money outranks waiting for a stage, but not a draft agreement or a bill that is ready to raise
        let outranks = matches!(next.as_ref().map(|n| n.kind), Some("mark_stage") | None);
        if outranks {
            next = Some(NextAction { kind: "record_payment", label: format!("Record payment for {number}"), page: "invoices", target_id: Some(*id) });
        }
    }

    // bank: money that reached the bank but is not yet matched to a line of the statement
    let (unmatched, unmatched_total): (i64, i64) = conn
        .query_row(
            "SELECT COUNT(*), COALESCE(SUM(r.amount_paise), 0) FROM receipts r JOIN invoices i ON i.id = r.invoice_id
             WHERE i.project_id = ?1 AND r.mode <> 'cash' AND r.amount_paise > 0
               AND NOT EXISTS (SELECT 1 FROM receipt_reversals v WHERE v.receipt_id = r.id)
               AND NOT EXISTS (SELECT 1 FROM bank_txn_receipts m WHERE m.receipt_id = r.id)",
            params![project_id],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .map_err(|e| e.to_string())?;
    let any_bank_receipt: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM receipts r JOIN invoices i ON i.id = r.invoice_id WHERE i.project_id = ?1 AND r.mode <> 'cash' AND r.amount_paise > 0",
            params![project_id],
            |r| r.get(0),
        )
        .map_err(|e| e.to_string())?;
    if unmatched > 0 {
        steps.push(Step { key: "bank", label: "Bank", state: "next", detail: format!("{unmatched} receipt{} ({}) not yet matched to a bank statement line.", if unmatched == 1 { "" } else { "s" }, rupees(unmatched_total)) });
        // matching the bank outranks only the standing "mark a stage" hint
        if matches!(next.as_ref().map(|n| n.kind), Some("mark_stage") | None) {
            next = Some(NextAction { kind: "reconcile", label: "Match the receipts to the bank statement".into(), page: "bank", target_id: Some(project_id) });
        }
    } else if any_bank_receipt > 0 {
        steps.push(Step { key: "bank", label: "Bank", state: "done", detail: "Every bank receipt is matched to a statement line.".into() });
    } else {
        steps.push(Step { key: "bank", label: "Bank", state: "waiting", detail: "Appears once a payment that reaches the bank is recorded.".into() });
    }

    let reached = stages.iter().filter(|s| !s.2.is_empty()).count();
    let summary = if stages.is_empty() {
        "No stages yet".to_string()
    } else if reached == stages.len() {
        "All stages reached".to_string()
    } else {
        format!("Stage {} of {}: {}", reached + 1, stages.len(), stages[reached].1)
    };

    // a project with everything finished has no next action
    if next.as_ref().is_some_and(|n| n.kind == "mark_stage") && steps.iter().all(|s| s.state != "next") {
        // waiting on the work itself: keep the hint, it is not an overdue task
    }
    Ok(ProjectPath { project_id, project, client, steps, next, summary })
}

#[derive(Serialize, Debug)]
pub struct Attention {
    pub project_id: i64,
    pub project: String,
    pub client: String,
    pub summary: String,
    pub action: NextAction,
}

/// Projects that have something to do now, most pressing first: money to collect, then bills to raise, then set-up.
pub fn attention(conn: &Connection, limit: usize) -> Result<Vec<Attention>, String> {
    let ids: Vec<i64> = {
        let mut stmt = conn.prepare("SELECT id FROM projects WHERE archived = 0 AND status = 'active' ORDER BY id").map_err(|e| e.to_string())?;
        let rows = stmt.query_map([], |r| r.get(0)).map_err(|e| e.to_string())?;
        rows.collect::<Result<_, _>>().map_err(|e| e.to_string())?
    };
    let mut out = Vec::new();
    for id in ids {
        let path = project_path(conn, id)?;
        // "mark a stage when reached" is a standing hint on every running project, not something needing attention
        if let Some(action) = path.next.filter(|n| n.kind != "mark_stage") {
            out.push(Attention { project_id: id, project: path.project, client: path.client, summary: path.summary, action });
        }
    }
    let rank = |k: &str| match k {
        "record_payment" => 0,
        "bill_stage" => 1,
        "issue_invoice" => 2,
        "reconcile" => 3,
        "issue_agreement" => 4,
        "new_agreement" => 5,
        _ => 6,
    };
    out.sort_by_key(|a| (rank(a.action.kind), a.project_id));
    out.truncate(limit);
    Ok(out)
}

pub fn project_path_get(state: HostState<'_>, project_id: i64) -> Result<ProjectPath, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    project_path(&conn, project_id)
}

pub fn project_attention(state: HostState<'_>) -> Result<Vec<Attention>, String> {
    require(&state, Permission::View)?;
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    attention(&conn, 8)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{billing, documents, receipts, stages};

    fn setup() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('o','x','owner')", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name, consultancy_type, floors) VALUES (1, 'Villa', 'comprehensive', 1)", []).unwrap();
        c.execute("UPDATE company_profile SET name = 'Studio X', gst_scheme = 'none'", []).unwrap();
        c
    }

    fn next_kind(c: &Connection) -> Option<&'static str> {
        project_path(c, 1).unwrap().next.map(|n| n.kind)
    }

    fn state_of(c: &Connection, key: &str) -> &'static str {
        project_path(c, 1).unwrap().steps.iter().find(|s| s.key == key).unwrap().state
    }

    #[test]
    fn a_new_project_points_at_stages_then_the_agreement_then_billing_then_payment() {
        let mut c = setup();
        // 1. nothing but the project: choose stages
        assert_eq!(next_kind(&c), Some("assign_stages"));
        stages::apply(&mut c, 1, "comprehensive", 1).unwrap();
        // 2. stages chosen: draft the agreement
        assert_eq!(next_kind(&c), Some("new_agreement"));
        let input = documents::DocInput {
            id: None, kind: "agreement".into(), project_id: Some(1), party_name: String::new(), party_address: String::new(), subject: "Agreement".into(),
            reference: String::new(), doc_date: "2026-10-05".into(), body: "Terms.".into(), template_version_id: None, fee: "100000".into(),
            milestones: stages::milestone_percents(&c, 1).unwrap().into_iter().map(|(name, percent)| documents::MilestoneInput { name, percent }).collect(),
        };
        let doc = documents::save_draft(&mut c, 1, &input).unwrap();
        // 3. a draft agreement: open and issue it
        let n = project_path(&c, 1).unwrap().next.unwrap();
        assert_eq!((n.kind, n.target_id), ("issue_agreement", Some(doc)));
        documents::issue(&mut c, 1, doc).unwrap();
        // 4. issued, but no stage reached: nothing to bill yet, only the standing hint
        assert_eq!(next_kind(&c), Some("mark_stage"));
        assert_eq!(state_of(&c, "agreement"), "done");
        assert_eq!(state_of(&c, "invoice"), "waiting");
        // 5. the first stage is reached: it is due to be billed
        let plan = stages::plan(&c, 1).unwrap();
        stages::mark(&c, plan.stages[0].id, "2026-10-10").unwrap();
        let n = project_path(&c, 1).unwrap().next.unwrap();
        assert_eq!(n.kind, "bill_stage");
        assert!(n.label.contains("Appointment") || n.label.starts_with("Bill "), "{}", n.label);
        let milestone = n.target_id.unwrap();
        // 6. billing it makes a draft invoice, which is then the thing to review and issue
        let invoice = documents::bill_milestone(&mut c, 1, milestone, "2026-10-11").unwrap();
        let n = project_path(&c, 1).unwrap().next.unwrap();
        assert_eq!((n.kind, n.target_id), ("issue_invoice", Some(invoice)));
        // 7. issued and unpaid: record the payment
        billing::issue(&mut c, invoice).unwrap();
        let n = project_path(&c, 1).unwrap().next.unwrap();
        assert_eq!((n.kind, n.target_id), ("record_payment", Some(invoice)));
        assert_eq!(state_of(&c, "payment"), "next");
        // 8. paid in full: money is no longer the next thing; the project waits for the next stage again
        let total: i64 = c.query_row("SELECT total_paise FROM invoices WHERE id = ?1", params![invoice], |r| r.get(0)).unwrap();
        receipts::record(&mut c, 1, &receipts::ReceiptInput {
            invoice_id: invoice, received_date: "2026-10-12".into(), amount: format!("{}.{:02}", total / 100, total % 100), tds: String::new(), tds_section: String::new(),
            mode: "bank".into(), reference: "UTR1".into(), notes: String::new(), bank_account_id: None,
        }).unwrap();
        assert_eq!(state_of(&c, "payment"), "done");
        // the money went through the bank, so matching it to the statement is what is left
        assert_eq!(next_kind(&c), Some("reconcile"));
        assert_eq!(state_of(&c, "bank"), "next");
        // the engine never posted or billed anything on its own
        assert_eq!(c.query_row("SELECT COUNT(*) FROM invoices", [], |r| r.get::<_, i64>(0)).unwrap(), 1);
    }

    #[test]
    fn collecting_money_outranks_waiting_for_a_stage_but_not_a_bill_that_is_ready() {
        let mut c = setup();
        stages::apply(&mut c, 1, "comprehensive", 1).unwrap();
        let input = documents::DocInput {
            id: None, kind: "agreement".into(), project_id: Some(1), party_name: String::new(), party_address: String::new(), subject: "Agreement".into(),
            reference: String::new(), doc_date: "2026-10-05".into(), body: "Terms.".into(), template_version_id: None, fee: "100000".into(),
            milestones: stages::milestone_percents(&c, 1).unwrap().into_iter().map(|(name, percent)| documents::MilestoneInput { name, percent }).collect(),
        };
        let doc = documents::save_draft(&mut c, 1, &input).unwrap();
        documents::issue(&mut c, 1, doc).unwrap();
        let plan = stages::plan(&c, 1).unwrap();
        stages::mark(&c, plan.stages[0].id, "2026-10-10").unwrap();
        let first = project_path(&c, 1).unwrap().next.unwrap().target_id.unwrap();
        let inv = documents::bill_milestone(&mut c, 1, first, "2026-10-11").unwrap();
        billing::issue(&mut c, inv).unwrap();
        stages::mark(&c, plan.stages[1].id, "2026-10-20").unwrap();
        // an invoice is unpaid AND the second stage is now billable: raising the bill comes first
        assert_eq!(next_kind(&c), Some("bill_stage"));
        let second = project_path(&c, 1).unwrap().next.unwrap().target_id.unwrap();
        let inv2 = documents::bill_milestone(&mut c, 1, second, "2026-10-21").unwrap();
        assert_eq!(next_kind(&c), Some("issue_invoice"));
        billing::issue(&mut c, inv2).unwrap();
        // both issued and unpaid, no stage newly reached: collect, oldest first
        let n = project_path(&c, 1).unwrap().next.unwrap();
        assert_eq!((n.kind, n.target_id), ("record_payment", Some(inv)));
    }

    #[test]
    fn a_bank_receipt_not_yet_matched_is_the_next_step_until_it_is_matched() {
        let mut c = setup();
        stages::apply(&mut c, 1, "comprehensive", 1).unwrap();
        let input = documents::DocInput {
            id: None, kind: "agreement".into(), project_id: Some(1), party_name: String::new(), party_address: String::new(), subject: "Agreement".into(),
            reference: String::new(), doc_date: "2026-10-05".into(), body: "Terms.".into(), template_version_id: None, fee: "100000".into(),
            milestones: stages::milestone_percents(&c, 1).unwrap().into_iter().map(|(name, percent)| documents::MilestoneInput { name, percent }).collect(),
        };
        let doc = documents::save_draft(&mut c, 1, &input).unwrap();
        documents::issue(&mut c, 1, doc).unwrap();
        let plan = stages::plan(&c, 1).unwrap();
        stages::mark(&c, plan.stages[0].id, "2026-10-10").unwrap();
        let ms = project_path(&c, 1).unwrap().next.unwrap().target_id.unwrap();
        let inv = documents::bill_milestone(&mut c, 1, ms, "2026-10-11").unwrap();
        billing::issue(&mut c, inv).unwrap();
        assert_eq!(state_of(&c, "bank"), "waiting", "no receipt yet");
        let total: i64 = c.query_row("SELECT total_paise FROM invoices WHERE id = ?1", params![inv], |r| r.get(0)).unwrap();
        let text = |paise: i64| format!("{}.{:02}", paise / 100, paise % 100);
        let pay = |c: &mut Connection, paise: i64, mode: &str, reference: &str| {
            receipts::record(c, 1, &receipts::ReceiptInput {
                invoice_id: inv, received_date: "2026-10-12".into(), amount: text(paise), tds: String::new(), tds_section: String::new(),
                mode: mode.into(), reference: reference.into(), notes: String::new(), bank_account_id: None,
            }).unwrap()
        };
        let quarter = total / 4;
        // a cash receipt never needs the bank
        pay(&mut c, quarter, "cash", "");
        assert_eq!(state_of(&c, "bank"), "waiting");
        // a bank receipt does, but a payment still owed is the more pressing thing
        pay(&mut c, quarter, "bank", "UTR-1");
        assert_eq!(next_kind(&c), Some("record_payment"), "half the invoice is still owed");
        assert_eq!(state_of(&c, "bank"), "next");
        // once fully paid, matching the bank is what is left
        pay(&mut c, total - 2 * quarter, "bank", "UTR-2");
        let n = project_path(&c, 1).unwrap().next.unwrap();
        assert_eq!(n.kind, "reconcile");
        assert_eq!(state_of(&c, "bank"), "next");
        // matching every bank receipt to a statement line clears it
        c.execute("INSERT INTO bank_batches (filename, file_sha256, row_count) VALUES ('s.csv','x',2)", []).unwrap();
        for (i, receipt) in c.prepare("SELECT id FROM receipts WHERE mode = 'bank'").unwrap().query_map([], |r| r.get::<_, i64>(0)).unwrap().map(|r| r.unwrap()).collect::<Vec<_>>().into_iter().enumerate() {
            c.execute("INSERT INTO bank_transactions (batch_id, row_no, txn_date, credit_paise, raw_row, dedupe_key, status) VALUES (1, ?1, '2026-10-12', 100, 'r', ?2, 'matched')", params![i as i64, format!("k{i}")]).unwrap();
            let txn: i64 = c.last_insert_rowid();
            c.execute("INSERT INTO bank_txn_receipts (txn_id, receipt_id) VALUES (?1, ?2)", params![txn, receipt]).unwrap();
        }
        assert_eq!(state_of(&c, "bank"), "done");
        assert_eq!(next_kind(&c), Some("mark_stage"));
    }

    #[test]
    fn pulse_lists_only_projects_with_something_to_do_and_ranks_money_first() {
        let mut c = setup();
        c.execute("INSERT INTO projects (client_id, name, consultancy_type, floors) VALUES (1, 'Office', 'comprehensive', 1)", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name, status) VALUES (1, 'Old', 'completed')", []).unwrap();
        let list = attention(&c, 8).unwrap();
        assert_eq!(list.len(), 2, "completed projects are left out");
        assert!(list.iter().all(|a| a.action.kind == "assign_stages"));
        // a project that is only waiting on a stage is not "needing attention"
        stages::apply(&mut c, 1, "comprehensive", 1).unwrap();
        let input = documents::DocInput {
            id: None, kind: "agreement".into(), project_id: Some(1), party_name: String::new(), party_address: String::new(), subject: "A".into(), reference: String::new(),
            doc_date: "2026-10-05".into(), body: "T".into(), template_version_id: None, fee: "50000".into(),
            milestones: stages::milestone_percents(&c, 1).unwrap().into_iter().map(|(name, percent)| documents::MilestoneInput { name, percent }).collect(),
        };
        let d = documents::save_draft(&mut c, 1, &input).unwrap();
        documents::issue(&mut c, 1, d).unwrap();
        let list = attention(&c, 8).unwrap();
        assert_eq!(list.len(), 1);
        assert_eq!(list[0].project, "Office");
        assert!(attention(&c, 0).unwrap().is_empty());
    }

    #[test]
    fn an_unknown_project_is_an_error() {
        assert!(project_path(&setup(), 99).is_err());
    }
}
