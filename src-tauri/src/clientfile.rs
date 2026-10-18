//! Everything the practice holds for one client, in one read: invoices, payments, what was sent and written (letters,
//! agreements, transmittals), and reports and presentations. It reads existing records and changes nothing.
//! Money (invoices and payments) is left out for a role that may not see the books.
use crate::auth::Permission;
use crate::billing::fmt_inr;
use crate::commands::{require, HostState, INTERNAL};
use rusqlite::{params, Connection};
use serde::Serialize;

#[derive(Serialize, Debug)]
pub struct InvoiceLine {
    pub id: i64,
    pub number: String,
    pub project: String,
    pub kind: String,
    pub status: String,
    pub issue_date: String,
    pub due_date: String,
    pub total_display: String,
    /// What is still owed; blank for drafts, pro formas and cancelled invoices.
    pub balance_display: String,
}

#[derive(Serialize, Debug)]
pub struct PaymentLine {
    pub date: String,
    pub number: String,
    pub invoice_id: i64,
    pub invoice: String,
    pub project: String,
    pub mode: String,
    pub reference: String,
    pub amount_display: String,
    pub tds_display: String,
    pub reversed: bool,
}

#[derive(Serialize, Debug)]
pub struct CommunicationLine {
    /// `letter_out`, `letter_in`, `agreement` or `transmittal`.
    pub kind: String,
    pub id: i64,
    pub date: String,
    pub number: String,
    pub subject: String,
    pub project: String,
    pub status: String,
}

#[derive(Serialize, Debug)]
pub struct ReportLine {
    pub id: i64,
    pub kind: String,
    pub title: String,
    pub project: String,
    pub status: String,
    pub updated: String,
}

#[derive(Serialize, Debug)]
pub struct ClientActivity {
    pub client_id: i64,
    pub name: String,
    /// False when the role may not see invoices and payments; those lists are then empty.
    pub finance: bool,
    pub billed_display: String,
    pub received_display: String,
    pub outstanding_display: String,
    pub invoices: Vec<InvoiceLine>,
    pub payments: Vec<PaymentLine>,
    pub communications: Vec<CommunicationLine>,
    pub reports: Vec<ReportLine>,
}

fn collect<T>(conn: &Connection, sql: &str, client_id: i64, map: impl Fn(&rusqlite::Row) -> rusqlite::Result<T>) -> Result<Vec<T>, String> {
    let mut stmt = conn.prepare(sql).map_err(|e| e.to_string())?;
    let rows = stmt.query_map(params![client_id], |r| map(r)).map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn client_activity(conn: &Connection, client_id: i64, finance: bool) -> Result<ClientActivity, String> {
    let name: String = conn.query_row("SELECT name FROM clients WHERE id = ?1", params![client_id], |r| r.get(0)).map_err(|_| "Client not found.".to_string())?;

    let (mut invoices, mut payments) = (Vec::new(), Vec::new());
    let (mut billed, mut received, mut outstanding) = (0i64, 0i64, 0i64);
    if finance {
        // totals come from issued tax invoices only: drafts, pro formas and cancelled invoices are not money owed
        (billed, received, outstanding) = conn
            .query_row(
                "SELECT COALESCE(SUM(i.total_paise),0), COALESCE(SUM(b.received_paise + b.tds_paise),0),
                        COALESCE(SUM(b.total_paise - b.received_paise - b.tds_paise - b.credited_paise - b.written_off_paise),0)
                 FROM invoices i JOIN invoice_balances b ON b.invoice_id = i.id JOIN projects p ON p.id = i.project_id
                 WHERE p.client_id = ?1 AND i.kind = 'tax_invoice' AND i.status = 'issued'",
                params![client_id],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
            )
            .map_err(|e| e.to_string())?;
        invoices = collect(
            conn,
            "SELECT i.id, COALESCE(i.number,''), p.name, i.kind, i.status, i.issue_date, i.due_date, i.total_paise,
                    b.total_paise - b.received_paise - b.tds_paise - b.credited_paise - b.written_off_paise
             FROM invoices i JOIN invoice_balances b ON b.invoice_id = i.id JOIN projects p ON p.id = i.project_id
             WHERE p.client_id = ?1 ORDER BY i.issue_date DESC, i.id DESC",
            client_id,
            |r| {
                let (kind, status): (String, String) = (r.get(3)?, r.get(4)?);
                let open: i64 = r.get(8)?;
                Ok(InvoiceLine {
                    id: r.get(0)?, number: r.get(1)?, project: r.get(2)?, issue_date: r.get(5)?, due_date: r.get(6)?, total_display: fmt_inr(r.get(7)?),
                    balance_display: if kind == "tax_invoice" && status == "issued" { fmt_inr(open) } else { String::new() }, kind, status,
                })
            },
        )?;
        payments = collect(
            conn,
            "SELECT r.received_date, r.number, i.id, COALESCE(i.number,''), p.name, r.mode, r.reference, r.amount_paise, r.tds_paise,
                    EXISTS (SELECT 1 FROM receipt_reversals v WHERE v.receipt_id = r.id)
             FROM receipts r JOIN invoices i ON i.id = r.invoice_id JOIN projects p ON p.id = i.project_id
             WHERE p.client_id = ?1 ORDER BY r.received_date DESC, r.id DESC",
            client_id,
            |r| {
                Ok(PaymentLine {
                    date: r.get(0)?, number: r.get(1)?, invoice_id: r.get(2)?, invoice: r.get(3)?, project: r.get(4)?, mode: r.get(5)?, reference: r.get(6)?,
                    amount_display: fmt_inr(r.get(7)?), tds_display: fmt_inr(r.get(8)?), reversed: r.get::<_, i64>(9)? != 0,
                })
            },
        )?;
    }

    // letters and agreements for the client's projects, plus letters addressed to the client by name with no project
    let mut communications: Vec<CommunicationLine> = collect(
        conn,
        "SELECT d.kind, d.id, d.doc_date, COALESCE(d.number,''), d.subject, COALESCE(p.name,''), d.status
         FROM documents d LEFT JOIN projects p ON p.id = d.project_id
         WHERE d.status <> 'cancelled' AND (p.client_id = ?1 OR (d.project_id IS NULL AND d.party_name = (SELECT name FROM clients WHERE id = ?1)))",
        client_id,
        |r| Ok(CommunicationLine { kind: r.get(0)?, id: r.get(1)?, date: r.get(2)?, number: r.get(3)?, subject: r.get(4)?, project: r.get(5)?, status: r.get(6)? }),
    )?;
    communications.extend(collect(
        conn,
        "SELECT 'transmittal', t.id, t.sent_date, t.reference, t.purpose || CASE WHEN t.recipient <> '' THEN ' (to ' || t.recipient || ')' ELSE '' END, p.name,
                CASE t.acknowledged WHEN 1 THEN 'acknowledged' ELSE 'sent' END
         FROM transmittals t JOIN projects p ON p.id = t.project_id WHERE p.client_id = ?1 AND t.cancelled = 0",
        client_id,
        |r| Ok(CommunicationLine { kind: r.get(0)?, id: r.get(1)?, date: r.get(2)?, number: r.get(3)?, subject: r.get(4)?, project: r.get(5)?, status: r.get(6)? }),
    )?);
    communications.sort_by(|a, b| b.date.cmp(&a.date).then(b.id.cmp(&a.id)));

    let reports = collect(
        conn,
        "SELECT b.id, b.kind, b.title, p.name, b.status, b.updated_at FROM boards b JOIN projects p ON p.id = b.project_id
         WHERE p.client_id = ?1 ORDER BY b.updated_at DESC, b.id DESC",
        client_id,
        |r| Ok(ReportLine { id: r.get(0)?, kind: r.get(1)?, title: r.get(2)?, project: r.get(3)?, status: r.get(4)?, updated: r.get(5)? }),
    )?;

    Ok(ClientActivity {
        client_id, name, finance, billed_display: fmt_inr(billed), received_display: fmt_inr(received), outstanding_display: fmt_inr(outstanding),
        invoices, payments, communications, reports,
    })
}

#[tauri::command]
pub fn client_activity_get(state: HostState<'_>, client_id: i64) -> Result<ClientActivity, String> {
    require(&state, Permission::View)?;
    // a role without access to the books still sees letters and reports, never money
    let finance = require(&state, Permission::Finance).is_ok();
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    client_activity(&conn, client_id, finance)
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
        c.execute("INSERT INTO clients (name) VALUES ('Other')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name, consultancy_type, floors) VALUES (1, 'Villa', 'comprehensive', 1)", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name, consultancy_type, floors) VALUES (2, 'Shop', 'comprehensive', 1)", []).unwrap();
        c.execute("UPDATE company_profile SET name = 'Studio X', gst_scheme = 'none'", []).unwrap();
        c
    }

    #[test]
    fn a_clients_file_gathers_invoices_payments_letters_and_reports_for_that_client_only() {
        let mut c = setup();
        stages::apply(&mut c, 1, "comprehensive", 1).unwrap();
        let input = documents::DocInput {
            id: None, kind: "agreement".into(), project_id: Some(1), party_name: String::new(), party_address: String::new(), subject: "Agreement".into(),
            reference: String::new(), doc_date: "2026-10-05".into(), body: "Terms.".into(), template_version_id: None, fee: "100000".into(),
            milestones: stages::milestone_percents(&c, 1).unwrap().into_iter().map(|(name, percent)| documents::MilestoneInput { name, percent }).collect(),
        };
        let agreement = documents::save_draft(&mut c, 1, &input).unwrap();
        documents::issue(&mut c, 1, agreement).unwrap();
        let plan = stages::plan(&c, 1).unwrap();
        stages::mark(&c, plan.stages[0].id, "2026-10-10").unwrap();
        let milestone: i64 = c.query_row("SELECT id FROM document_milestones ORDER BY position LIMIT 1", [], |r| r.get(0)).unwrap();
        let invoice = documents::bill_milestone(&mut c, 1, milestone, "2026-10-11").unwrap();
        billing::issue(&mut c, invoice).unwrap();
        let total: i64 = c.query_row("SELECT total_paise FROM invoices WHERE id = ?1", params![invoice], |r| r.get(0)).unwrap();
        receipts::record(&mut c, 1, &receipts::ReceiptInput {
            invoice_id: invoice, received_date: "2026-10-12".into(), amount: format!("{}.{:02}", (total / 2) / 100, (total / 2) % 100), tds: String::new(), tds_section: String::new(),
            mode: "bank".into(), reference: "UTR1".into(), notes: String::new(), bank_account_id: None,
        }).unwrap();
        // a letter for the project, a transmittal, a report, and a letter to a different client that must not appear
        c.execute("INSERT INTO documents (kind, project_id, status, number, doc_date, subject) VALUES ('letter_out', 1, 'issued', 'OUT/1', '2026-10-14', 'Site meeting')", []).unwrap();
        c.execute("INSERT INTO documents (kind, project_id, status, number, doc_date, subject) VALUES ('letter_out', 2, 'issued', 'OUT/2', '2026-10-15', 'Not this client')", []).unwrap();
        c.execute("INSERT INTO transmittals (project_id, sent_date, recipient, purpose, reference) VALUES (1, '2026-10-16', 'Structural consultant', 'For approval', 'TR-1')", []).unwrap();
        c.execute("INSERT INTO boards (project_id, kind, title) VALUES (1, 'report', 'Stage 1 report')", []).unwrap();
        c.execute("INSERT INTO boards (project_id, kind, title) VALUES (2, 'presentation', 'Another client')", []).unwrap();

        let a = client_activity(&c, 1, true).unwrap();
        assert_eq!(a.name, "Acme");
        assert_eq!(a.invoices.len(), 1);
        assert_eq!(a.invoices[0].status, "issued");
        assert!(a.invoices[0].balance_display.starts_with('₹'));
        assert_eq!(a.payments.len(), 1);
        assert_eq!((a.payments[0].mode.as_str(), a.payments[0].reversed), ("bank", false));
        let subjects: Vec<&str> = a.communications.iter().map(|x| x.subject.as_str()).collect();
        assert!(subjects.contains(&"Site meeting") && subjects.iter().any(|s| s.starts_with("For approval")) && subjects.contains(&"Agreement"), "{subjects:?}");
        assert!(!subjects.contains(&"Not this client"));
        // newest first
        assert!(a.communications.windows(2).all(|w| w[0].date >= w[1].date));
        assert_eq!(a.reports.len(), 1);
        assert_eq!(a.reports[0].title, "Stage 1 report");
        assert_ne!(a.outstanding_display, a.billed_display, "half the invoice is paid");
        // the other client sees none of it
        let b = client_activity(&c, 2, true).unwrap();
        assert!(b.invoices.is_empty() && b.payments.is_empty() && b.reports.len() == 1 && b.communications.len() == 1);
    }

    #[test]
    fn a_role_without_the_books_gets_letters_and_reports_but_no_money() {
        let mut c = setup();
        c.execute("INSERT INTO documents (kind, project_id, status, number, doc_date, subject) VALUES ('letter_out', 1, 'issued', 'OUT/1', '2026-10-14', 'Hello')", []).unwrap();
        c.execute("INSERT INTO invoices (project_id, kind, status, gst_mode, total_paise, issue_date) VALUES (1, 'tax_invoice', 'draft', 'none', 100, '2026-10-01')", []).unwrap();
        let a = client_activity(&c, 1, false).unwrap();
        assert!(!a.finance && a.invoices.is_empty() && a.payments.is_empty());
        assert_eq!(a.communications.len(), 1);
        assert_eq!(a.billed_display, fmt_inr(0));
    }

    #[test]
    fn drafts_and_pro_formas_are_listed_but_never_counted_as_owed() {
        let c = setup();
        c.execute("INSERT INTO invoices (project_id, kind, status, gst_mode, total_paise, issue_date) VALUES (1, 'tax_invoice', 'draft', 'none', 500000, '2026-10-01')", []).unwrap();
        let a = client_activity(&c, 1, true).unwrap();
        assert_eq!(a.invoices.len(), 1);
        assert_eq!(a.invoices[0].balance_display, "");
        assert_eq!(a.outstanding_display, fmt_inr(0));
    }

    #[test]
    fn an_unknown_client_is_an_error() {
        assert!(client_activity(&setup(), 99, true).is_err());
    }
}
