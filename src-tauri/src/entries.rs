//! Day-to-day accounting entries that become journals: expenses, rent and interest income, salaries, fixed assets,
//! tax payments, manual journals and bank-statement classification. Each entry posts exactly one journal.

use crate::billing::{fmt_inr, parse_decimal, validate_date};
use crate::db;
use crate::ledger::{acct, cr, dr, money_account, post, reverse_journal, Line};
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};

fn money(s: &str, label: &str) -> Result<i64, String> {
    if s.trim().is_empty() {
        return Ok(0);
    }
    parse_decimal(s, 2).map_err(|e| format!("{label}. {e}"))
}

fn plain(p: i64) -> String {
    format!("{}.{:02}", p / 100, p % 100)
}

fn scheme(conn: &Connection) -> Result<String, String> {
    crate::billing::company_scheme(conn)
}

/// "cash", "bank" (primary account) or "bank:<id>".
fn via_account(conn: &Connection, via: &str) -> Result<i64, String> {
    match via {
        "cash" => acct(conn, "1000"),
        "bank" => money_account(conn, "bank", None),
        v => match v.strip_prefix("bank:").and_then(|n| n.parse::<i64>().ok()) {
            Some(id) => money_account(conn, "bank", Some(id)),
            None => Err("Choose how it was paid (cash or a bank account).".into()),
        },
    }
}

// ---------- vendors and properties ----------

#[derive(Serialize, Deserialize)]
pub struct Vendor {
    pub id: Option<i64>,
    pub name: String,
    pub pan: String,
    pub active: bool,
}

pub fn list_vendors(conn: &Connection) -> Result<Vec<Vendor>, String> {
    let mut stmt = conn.prepare("SELECT id, name, pan, active FROM vendors ORDER BY name").map_err(|e| e.to_string())?;
    let rows = stmt.query_map([], |r| Ok(Vendor { id: Some(r.get(0)?), name: r.get(1)?, pan: r.get(2)?, active: r.get::<_, i64>(3)? != 0 })).map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn save_vendor(conn: &Connection, v: &Vendor) -> Result<i64, String> {
    if v.name.trim().is_empty() {
        return Err("Enter the vendor name.".into());
    }
    let dup = |_| "A vendor with this name already exists.".to_string();
    match v.id {
        Some(id) => {
            conn.execute("UPDATE vendors SET name=?1, pan=?2, active=?3 WHERE id=?4", params![v.name.trim(), v.pan.trim(), v.active, id]).map_err(dup)?;
            Ok(id)
        }
        None => {
            conn.execute("INSERT INTO vendors (name, pan) VALUES (?1,?2)", params![v.name.trim(), v.pan.trim()]).map_err(dup)?;
            Ok(conn.last_insert_rowid())
        }
    }
}

#[derive(Serialize, Deserialize)]
pub struct Property {
    pub id: Option<i64>,
    pub name: String,
    /// "commercial" or "residential".
    pub kind: String,
    pub address: String,
    pub active: bool,
}

pub fn list_properties(conn: &Connection) -> Result<Vec<Property>, String> {
    let mut stmt = conn.prepare("SELECT id, name, kind, address, active FROM properties ORDER BY name").map_err(|e| e.to_string())?;
    let rows = stmt.query_map([], |r| Ok(Property { id: Some(r.get(0)?), name: r.get(1)?, kind: r.get(2)?, address: r.get(3)?, active: r.get::<_, i64>(4)? != 0 })).map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn save_property(conn: &Connection, p: &Property) -> Result<i64, String> {
    if p.name.trim().is_empty() {
        return Err("Enter the property name.".into());
    }
    if p.kind != "commercial" && p.kind != "residential" {
        return Err("Choose commercial or residential.".into());
    }
    let dup = |_| "A property with this name already exists.".to_string();
    match p.id {
        Some(id) => {
            conn.execute("UPDATE properties SET name=?1, kind=?2, address=?3, active=?4 WHERE id=?5", params![p.name.trim(), p.kind, p.address.trim(), p.active, id]).map_err(dup)?;
            Ok(id)
        }
        None => {
            conn.execute("INSERT INTO properties (name, kind, address) VALUES (?1,?2,?3)", params![p.name.trim(), p.kind, p.address.trim()]).map_err(dup)?;
            Ok(conn.last_insert_rowid())
        }
    }
}

// ---------- expenses ----------

#[derive(Deserialize)]
pub struct ExpenseInput {
    pub date: String,
    pub vendor_id: Option<i64>,
    pub bill_ref: String,
    pub description: String,
    pub expense_account_id: i64,
    /// Amount before GST.
    pub amount: String,
    /// GST charged by the vendor.
    pub gst: String,
    /// Tax deducted at source from the vendor's payment.
    pub tds: String,
    /// "cash", "bank", "bank:<id>" or "payable" (to be paid later).
    pub paid_via: String,
}

pub fn record_expense(conn: &mut Connection, actor: i64, e: &ExpenseInput) -> Result<i64, String> {
    validate_date(&e.date)?;
    if e.description.trim().is_empty() {
        return Err("Describe the expense.".into());
    }
    let (amount, gst, tds) = (money(&e.amount, "Amount")?, money(&e.gst, "GST")?, money(&e.tds, "TDS")?);
    if amount == 0 {
        return Err("Enter the amount.".into());
    }
    if tds > amount {
        return Err("TDS cannot be more than the amount.".into());
    }
    let tx = conn.transaction().map_err(|x| x.to_string())?;
    let kind: String = tx.query_row("SELECT type FROM accounts WHERE id = ?1", params![e.expense_account_id], |r| r.get(0)).map_err(|_| "Choose an expense account.".to_string())?;
    if kind != "expense" {
        return Err("Choose an expense account.".into());
    }
    // Only a regular GST registration can claim input credit; otherwise GST paid is part of the cost.
    let claim = scheme(&tx)? == "regular";
    let cost = if claim { amount } else { amount + gst };
    let payee = if e.paid_via == "payable" { acct(&tx, "2000")? } else { via_account(&tx, &e.paid_via)? };
    let lines = vec![
        dr(e.expense_account_id, cost),
        dr(acct(&tx, "1220")?, if claim { gst } else { 0 }),
        cr(acct(&tx, "2200")?, tds),
        cr(payee, amount + gst - tds),
    ];
    // Journal first needs an id for the unique source key, so insert the expense row after posting with a provisional link.
    let journal = post(&tx, Some(actor), &e.date, &format!("Expense: {}", e.description.trim()), "expense_pending", None, lines)?;
    tx.execute(
        "INSERT INTO expenses (date, vendor_id, bill_ref, description, expense_account_id, amount_paise, gst_paise, tds_paise, paid_via, journal_id, created_by)
         VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11)",
        params![e.date, e.vendor_id, e.bill_ref.trim(), e.description.trim(), e.expense_account_id, amount, gst, tds, e.paid_via, journal, actor],
    )
    .map_err(|x| x.to_string())?;
    let id = tx.last_insert_rowid();
    db::audit(&tx, Some(actor), "expense_recorded", &format!("expense {id}: {}", fmt_inr(amount + gst)));
    tx.commit().map_err(|x| x.to_string())?;
    Ok(id)
}

#[derive(Serialize)]
pub struct ExpenseRow {
    pub id: i64,
    pub date: String,
    pub vendor: String,
    pub description: String,
    pub account: String,
    pub total_display: String,
    pub total_paise: i64,
    pub paid_via: String,
    pub settled: bool,
    pub voided: bool,
}

pub fn list_expenses(conn: &Connection) -> Result<Vec<ExpenseRow>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT e.id, e.date, COALESCE(v.name,''), e.description, a.code || ' ' || a.name, e.amount_paise + e.gst_paise, e.paid_via, e.settled_journal_id IS NOT NULL, e.voided_at IS NOT NULL
             FROM expenses e LEFT JOIN vendors v ON v.id = e.vendor_id JOIN accounts a ON a.id = e.expense_account_id ORDER BY e.date DESC, e.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| Ok(ExpenseRow { id: r.get(0)?, date: r.get(1)?, vendor: r.get(2)?, description: r.get(3)?, account: r.get(4)?, total_display: fmt_inr(r.get(5)?), total_paise: r.get(5)?, paid_via: r.get(6)?, settled: r.get::<_, i64>(7)? != 0, voided: r.get::<_, i64>(8)? != 0 }))
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

/// Pays a vendor bill that was booked as payable.
pub fn settle_expense(conn: &mut Connection, actor: i64, id: i64, date: &str, paid_via: &str) -> Result<(), String> {
    validate_date(date)?;
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let (via, amount, gst, tds, settled, voided): (String, i64, i64, i64, Option<i64>, Option<String>) = tx
        .query_row("SELECT paid_via, amount_paise, gst_paise, tds_paise, settled_journal_id, voided_at FROM expenses WHERE id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?)))
        .map_err(|_| "Expense not found.".to_string())?;
    if via != "payable" {
        return Err("This expense was already paid when it was recorded.".into());
    }
    if settled.is_some() || voided.is_some() {
        return Err("This expense is already settled or voided.".into());
    }
    let j = post(&tx, Some(actor), date, &format!("Payment of expense {id}"), "expense_payment", Some(id), vec![dr(acct(&tx, "2000")?, amount + gst - tds), cr(via_account(&tx, paid_via)?, amount + gst - tds)])?;
    tx.execute("UPDATE expenses SET settled_journal_id = ?1 WHERE id = ?2", params![j, id]).map_err(|e| e.to_string())?;
    db::audit(&tx, Some(actor), "expense_settled", &format!("expense {id}"));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(())
}

fn void_entry(conn: &mut Connection, actor: i64, table: &str, id: i64, reason: &str) -> Result<(), String> {
    if reason.trim().is_empty() {
        return Err("Give a reason.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let (journal, voided): (i64, Option<String>) = tx
        .query_row(&format!("SELECT journal_id, voided_at FROM {table} WHERE id = ?1"), params![id], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|_| "Entry not found.".to_string())?;
    if voided.is_some() {
        return Err("This entry is already voided.".into());
    }
    if table == "expenses" {
        let settled: Option<i64> = tx.query_row("SELECT settled_journal_id FROM expenses WHERE id = ?1", params![id], |r| r.get(0)).map_err(|e| e.to_string())?;
        if settled.is_some() {
            return Err("This expense has been paid. Void is not available; record a correcting entry.".into());
        }
    }
    reverse_journal(&tx, Some(actor), journal, reason)?;
    tx.execute(&format!("UPDATE {table} SET voided_at = datetime('now') WHERE id = ?1"), params![id]).map_err(|e| e.to_string())?;
    db::audit(&tx, Some(actor), "entry_voided", &format!("{table} {id}: {}", reason.trim()));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(())
}

pub fn void_expense(conn: &mut Connection, actor: i64, id: i64, reason: &str) -> Result<(), String> {
    void_entry(conn, actor, "expenses", id, reason)
}

// ---------- income (rent, interest, other) ----------

#[derive(Deserialize)]
pub struct IncomeInput {
    pub date: String,
    /// "rent", "interest" or "other".
    pub kind: String,
    pub property_id: Option<i64>,
    pub payer: String,
    pub description: String,
    pub amount: String,
    /// GST charged on the income (commercial rent). Needs a regular GST registration.
    pub gst: String,
    /// "intra" (CGST + SGST) or "inter" (IGST).
    pub gst_mode: String,
    /// Tax deducted at source by the payer.
    pub tds: String,
    pub received_via: String,
}

pub fn record_income(conn: &mut Connection, actor: i64, i: &IncomeInput) -> Result<i64, String> {
    validate_date(&i.date)?;
    let (amount, gst, tds) = (money(&i.amount, "Amount")?, money(&i.gst, "GST")?, money(&i.tds, "TDS")?);
    if amount == 0 {
        return Err("Enter the amount.".into());
    }
    if tds > amount {
        return Err("TDS cannot be more than the amount.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    if gst > 0 && scheme(&tx)? != "regular" {
        return Err("GST can only be charged with a regular GST registration. Remove the GST amount.".into());
    }
    let income_code = match i.kind.as_str() {
        "rent" => {
            let pid = i.property_id.ok_or("Choose the property.")?;
            let pk: String = tx.query_row("SELECT kind FROM properties WHERE id = ?1", params![pid], |r| r.get(0)).map_err(|_| "Property not found.".to_string())?;
            if pk == "commercial" { "4100" } else { "4110" }
        }
        "interest" => "4200",
        "other" => "4900",
        _ => return Err("Choose the type of income.".into()),
    };
    let (cgst, sgst, igst) = if gst == 0 { (0, 0, 0) } else if i.gst_mode == "inter" { (0, 0, gst) } else { (gst / 2, gst - gst / 2, 0) };
    let lines = vec![
        dr(via_account(&tx, &i.received_via)?, amount + gst - tds),
        dr(acct(&tx, "1210")?, tds),
        cr(acct(&tx, income_code)?, amount),
        cr(acct(&tx, "2100")?, cgst),
        cr(acct(&tx, "2110")?, sgst),
        cr(acct(&tx, "2120")?, igst),
    ];
    let income_account = acct(&tx, income_code)?;
    let journal = post(&tx, Some(actor), &i.date, &format!("{} income: {}", i.kind, i.payer.trim()), "income_pending", None, lines)?;
    tx.execute(
        "INSERT INTO income_entries (date, kind, property_id, payer, description, income_account_id, amount_paise, gst_paise, tds_paise, received_via, journal_id, created_by)
         VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12)",
        params![i.date, i.kind, i.property_id, i.payer.trim(), i.description.trim(), income_account, amount, gst, tds, i.received_via, journal, actor],
    )
    .map_err(|e| e.to_string())?;
    let id = tx.last_insert_rowid();
    db::audit(&tx, Some(actor), "income_recorded", &format!("income {id}: {}", fmt_inr(amount)));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(id)
}

#[derive(Serialize)]
pub struct IncomeRow {
    pub id: i64,
    pub date: String,
    pub kind: String,
    pub property: String,
    pub payer: String,
    pub amount_display: String,
    pub amount_paise: i64,
    pub tds_display: String,
    pub tds_paise: i64,
    pub voided: bool,
}

pub fn list_income(conn: &Connection) -> Result<Vec<IncomeRow>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT i.id, i.date, i.kind, COALESCE(p.name,''), i.payer, i.amount_paise, i.tds_paise, i.voided_at IS NOT NULL
             FROM income_entries i LEFT JOIN properties p ON p.id = i.property_id ORDER BY i.date DESC, i.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| Ok(IncomeRow { id: r.get(0)?, date: r.get(1)?, kind: r.get(2)?, property: r.get(3)?, payer: r.get(4)?, amount_display: fmt_inr(r.get(5)?), amount_paise: r.get(5)?, tds_display: fmt_inr(r.get(6)?), tds_paise: r.get(6)?, voided: r.get::<_, i64>(7)? != 0 }))
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn void_income(conn: &mut Connection, actor: i64, id: i64, reason: &str) -> Result<(), String> {
    void_entry(conn, actor, "income_entries", id, reason)
}

// ---------- payroll ----------

#[derive(Deserialize)]
pub struct PayrollInput {
    pub date: String,
    pub employee: String,
    pub period_label: String,
    pub gross: String,
    /// Income tax deducted from the salary (TDS on salary).
    pub tds: String,
    /// "cash", "bank", "bank:<id>" or "payable".
    pub paid_via: String,
    /// Provident fund deducted from the employee, and the employer's matching contribution.
    #[serde(default)]
    pub pf_employee: String,
    #[serde(default)]
    pub pf_employer: String,
    #[serde(default)]
    pub esi_employee: String,
    #[serde(default)]
    pub esi_employer: String,
    #[serde(default)]
    pub professional_tax: String,
}

pub fn record_payroll(conn: &mut Connection, actor: i64, p: &PayrollInput) -> Result<i64, String> {
    validate_date(&p.date)?;
    if p.employee.trim().is_empty() {
        return Err("Enter the employee's name.".into());
    }
    let (gross, tds) = (money(&p.gross, "Gross salary")?, money(&p.tds, "TDS")?);
    let (pf_ee, pf_er) = (money(&p.pf_employee, "Employee PF")?, money(&p.pf_employer, "Employer PF")?);
    let (esi_ee, esi_er) = (money(&p.esi_employee, "Employee ESI")?, money(&p.esi_employer, "Employer ESI")?);
    let pt = money(&p.professional_tax, "Professional tax")?;
    let deductions = tds + pf_ee + esi_ee + pt;
    if gross == 0 || deductions > gross {
        return Err("Enter the gross salary; the deductions cannot exceed it.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let payee = if p.paid_via == "payable" { acct(&tx, "2300")? } else { via_account(&tx, &p.paid_via)? };
    let lines = vec![
        dr(acct(&tx, "5000")?, gross),
        dr(acct(&tx, "5010")?, pf_er + esi_er),
        cr(acct(&tx, "2200")?, tds),
        cr(acct(&tx, "2310")?, pf_ee + pf_er),
        cr(acct(&tx, "2320")?, esi_ee + esi_er),
        cr(acct(&tx, "2330")?, pt),
        cr(payee, gross - deductions),
    ];
    let journal = post(&tx, Some(actor), &p.date, &format!("Salary: {} {}", p.employee.trim(), p.period_label.trim()), "payroll_pending", None, lines)?;
    tx.execute(
        "INSERT INTO payroll_entries (date, employee, period_label, gross_paise, tds_paise, paid_via, journal_id, created_by,
         pf_employee_paise, pf_employer_paise, esi_employee_paise, esi_employer_paise, pt_paise) VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13)",
        params![p.date, p.employee.trim(), p.period_label.trim(), gross, tds, p.paid_via, journal, actor, pf_ee, pf_er, esi_ee, esi_er, pt],
    )
    .map_err(|e| e.to_string())?;
    let id = tx.last_insert_rowid();
    db::audit(&tx, Some(actor), "payroll_recorded", &format!("payroll {id}"));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(id)
}

#[derive(Serialize)]
pub struct PayrollRow {
    pub id: i64,
    pub date: String,
    pub employee: String,
    pub period_label: String,
    pub gross_display: String,
    pub gross_paise: i64,
    pub tds_display: String,
    pub net_display: String,
    pub net_paise: i64,
    pub voided: bool,
}

pub fn list_payroll(conn: &Connection) -> Result<Vec<PayrollRow>, String> {
    let mut stmt = conn
        .prepare("SELECT id, date, employee, period_label, gross_paise, tds_paise + pf_employee_paise + esi_employee_paise + pt_paise, voided_at IS NOT NULL FROM payroll_entries ORDER BY date DESC, id DESC")
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| {
            let (g, t): (i64, i64) = (r.get(4)?, r.get(5)?);
            Ok(PayrollRow { id: r.get(0)?, date: r.get(1)?, employee: r.get(2)?, period_label: r.get(3)?, gross_display: fmt_inr(g), gross_paise: g, tds_display: fmt_inr(t), net_display: fmt_inr(g - t), net_paise: g - t, voided: r.get::<_, i64>(6)? != 0 })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn void_payroll(conn: &mut Connection, actor: i64, id: i64, reason: &str) -> Result<(), String> {
    void_entry(conn, actor, "payroll_entries", id, reason)
}

// ---------- statutory suggestions ----------

#[derive(Serialize)]
pub struct PayrollSuggestion {
    pub pf_employee: String,
    pub pf_employer: String,
    pub esi_employee: String,
    pub esi_employer: String,
}

/// Suggests PF and ESI from rates the caller supplies (they change; the defaults on screen must be checked).
/// PF applies to basic pay up to the wage ceiling; ESI applies only while gross pay is within the threshold.
pub fn suggest_statutory(basic: &str, gross: &str, pf_rate: &str, pf_ceiling: &str, esi_threshold: &str, esi_ee_rate: &str, esi_er_rate: &str) -> Result<PayrollSuggestion, String> {
    let (basic, gross) = (money(basic, "Basic pay")?, money(gross, "Gross pay")?);
    let pf_bp = parse_decimal(pf_rate, 2).map_err(|e| format!("PF rate. {e}"))?;
    let ceiling = money(pf_ceiling, "PF wage ceiling")?;
    let threshold = money(esi_threshold, "ESI threshold")?;
    let ee_bp = parse_decimal(esi_ee_rate, 2).map_err(|e| format!("ESI rate. {e}"))?;
    let er_bp = parse_decimal(esi_er_rate, 2).map_err(|e| format!("ESI rate. {e}"))?;
    let pf_wage = if ceiling > 0 { basic.min(ceiling) } else { basic };
    let pf = (pf_wage * pf_bp + 5_000) / 10_000;
    let (esi_ee, esi_er) = if threshold > 0 && gross > threshold { (0, 0) } else { ((gross * ee_bp + 5_000) / 10_000, (gross * er_bp + 5_000) / 10_000) };
    Ok(PayrollSuggestion { pf_employee: plain(pf), pf_employer: plain(pf), esi_employee: plain(esi_ee), esi_employer: plain(esi_er) })
}

// ---------- fixed assets ----------

#[derive(Deserialize)]
pub struct AssetInput {
    pub name: String,
    pub purchase_date: String,
    pub cost: String,
    /// Depreciation rate in percent per year on the written-down value, e.g. "15".
    pub rate_percent: String,
    pub paid_via: String,
}

pub fn add_asset(conn: &mut Connection, actor: i64, a: &AssetInput) -> Result<i64, String> {
    validate_date(&a.purchase_date)?;
    if a.name.trim().is_empty() {
        return Err("Name the asset.".into());
    }
    let (cost, bp) = (money(&a.cost, "Cost")?, parse_decimal(&a.rate_percent, 2).map_err(|e| format!("Rate. {e}"))?);
    if cost == 0 || bp > 10_000 {
        return Err("Enter the cost and a rate up to 100%.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let payee = if a.paid_via == "payable" { acct(&tx, "2000")? } else { via_account(&tx, &a.paid_via)? };
    let journal = post(&tx, Some(actor), &a.purchase_date, &format!("Purchase of {}", a.name.trim()), "asset_pending", None, vec![dr(acct(&tx, "1300")?, cost), cr(payee, cost)])?;
    tx.execute("INSERT INTO fixed_assets (name, purchase_date, cost_paise, dep_rate_bp, journal_id, created_by) VALUES (?1,?2,?3,?4,?5,?6)", params![a.name.trim(), a.purchase_date, cost, bp, journal, actor])
        .map_err(|e| e.to_string())?;
    let id = tx.last_insert_rowid();
    db::audit(&tx, Some(actor), "asset_added", &format!("asset {id}: {}", a.name.trim()));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(id)
}

#[derive(Serialize)]
pub struct AssetRow {
    pub id: i64,
    pub name: String,
    pub purchase_date: String,
    pub cost_display: String,
    pub cost_paise: i64,
    pub rate: String,
    pub accumulated_display: String,
    pub book_value_display: String,
    pub book_value_paise: i64,
}

pub fn list_assets(conn: &Connection) -> Result<Vec<AssetRow>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT a.id, a.name, a.purchase_date, a.cost_paise, a.dep_rate_bp, COALESCE((SELECT SUM(amount_paise) FROM asset_depreciation d WHERE d.asset_id = a.id), 0)
             FROM fixed_assets a ORDER BY a.purchase_date DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| {
            let (cost, bp, acc): (i64, i64, i64) = (r.get(3)?, r.get(4)?, r.get(5)?);
            Ok(AssetRow { id: r.get(0)?, name: r.get(1)?, purchase_date: r.get(2)?, cost_display: fmt_inr(cost), cost_paise: cost, rate: format!("{}%", plain(bp)), accumulated_display: fmt_inr(acc), book_value_display: fmt_inr(cost - acc), book_value_paise: cost - acc })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

/// Posts a year's depreciation (written-down value method, full rate in every year including the purchase year).
/// Each asset is depreciated once per financial year. Returns how many assets were depreciated.
pub fn post_depreciation(conn: &mut Connection, actor: i64, fy: &str) -> Result<usize, String> {
    let start_year: i32 = fy.get(..4).and_then(|y| y.parse().ok()).ok_or("Enter the financial year like 2026-27.")?;
    let (end_date, expect) = (format!("{}-03-31", start_year + 1), format!("{}-{:02}", start_year, (start_year + 1) % 100));
    if fy != expect {
        return Err("Enter the financial year like 2026-27.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let assets: Vec<(i64, String, i64, i64)> = {
        let mut stmt = tx.prepare("SELECT id, name, cost_paise, dep_rate_bp FROM fixed_assets WHERE active = 1 AND purchase_date <= ?1 ORDER BY id").map_err(|e| e.to_string())?;
        let rows = stmt.query_map(params![end_date], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?))).map_err(|e| e.to_string())?;
        rows.collect::<Result<_, _>>().map_err(|e| e.to_string())?
    };
    let mut done = 0;
    for (id, name, cost, bp) in assets {
        let already: i64 = tx.query_row("SELECT COUNT(*) FROM asset_depreciation WHERE asset_id = ?1 AND fy = ?2", params![id, fy], |r| r.get(0)).map_err(|e| e.to_string())?;
        if already > 0 {
            continue;
        }
        let accumulated: i64 = tx.query_row("SELECT COALESCE(SUM(amount_paise),0) FROM asset_depreciation WHERE asset_id = ?1", params![id], |r| r.get(0)).map_err(|e| e.to_string())?;
        let wdv = cost - accumulated;
        let dep = ((wdv * bp + 5_000) / 10_000).min(wdv);
        if dep <= 0 {
            continue;
        }
        let j = post(&tx, Some(actor), &end_date, &format!("Depreciation {fy}: {name}"), &format!("depreciation:{fy}"), Some(id), vec![dr(acct(&tx, "5700")?, dep), cr(acct(&tx, "1310")?, dep)])?;
        tx.execute("INSERT INTO asset_depreciation (asset_id, fy, amount_paise, journal_id) VALUES (?1,?2,?3,?4)", params![id, fy, dep, j]).map_err(|e| e.to_string())?;
        done += 1;
    }
    db::audit(&tx, Some(actor), "depreciation_posted", &format!("{fy}: {done} assets"));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(done)
}

// ---------- tax payments and manual journals ----------

/// Paying tax to the government: reduces the matching liability. `kind` is tds, cgst, sgst, igst, composition or income_tax.
pub fn tax_payment(conn: &mut Connection, actor: i64, kind: &str, date: &str, amount: &str, paid_via: &str, narration: &str) -> Result<i64, String> {
    validate_date(date)?;
    let amt = money(amount, "Amount")?;
    if amt == 0 {
        return Err("Enter the amount paid.".into());
    }
    let code = match kind {
        "tds" => "2200",
        "cgst" => "2100",
        "sgst" => "2110",
        "igst" => "2120",
        "composition" => "2130",
        "income_tax" => "2500",
        "pf" => "2310",
        "esi" => "2320",
        "professional_tax" => "2330",
        _ => return Err("Choose the tax being paid.".into()),
    };
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let label = if narration.trim().is_empty() { format!("{kind} payment") } else { narration.trim().to_string() };
    let j = post(&tx, Some(actor), date, &label, "tax_payment", None, vec![dr(acct(&tx, code)?, amt), cr(via_account(&tx, paid_via)?, amt)])?;
    db::audit(&tx, Some(actor), "tax_payment", &format!("{kind} {}", fmt_inr(amt)));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(j)
}

#[derive(Deserialize)]
pub struct JournalLineInput {
    pub account_id: i64,
    pub debit: String,
    pub credit: String,
    pub memo: String,
}

pub fn manual_journal(conn: &mut Connection, actor: i64, date: &str, narration: &str, lines: &[JournalLineInput]) -> Result<i64, String> {
    if narration.trim().is_empty() {
        return Err("Enter a narration.".into());
    }
    let mut out: Vec<Line> = Vec::new();
    for (i, l) in lines.iter().enumerate() {
        let (d, c) = (money(&l.debit, &format!("Line {} debit", i + 1))?, money(&l.credit, &format!("Line {} credit", i + 1))?);
        if d > 0 && c > 0 {
            return Err(format!("Line {} has both a debit and a credit.", i + 1));
        }
        out.push(Line { account_id: l.account_id, debit: d, credit: c, memo: l.memo.trim().to_string() });
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let j = post(&tx, Some(actor), date, narration.trim(), "manual", None, out)?;
    db::audit(&tx, Some(actor), "journal_posted", &format!("journal {j}"));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(j)
}

/// Reverses a journal that was posted by hand or by an accounting entry. Invoices, receipts and credit notes
/// must be corrected through their own screens so documents and ledger stay in step.
pub fn reverse_manual(conn: &mut Connection, actor: i64, journal_id: i64, reason: &str) -> Result<i64, String> {
    if reason.trim().is_empty() {
        return Err("Give a reason for the reversal.".into());
    }
    let source: String = conn.query_row("SELECT source_type FROM journals WHERE id = ?1", params![journal_id], |r| r.get(0)).map_err(|_| "Journal not found.".to_string())?;
    let blocked = ["invoice", "receipt", "credit_note", "reversal", "bank_txn"];
    if blocked.contains(&source.as_str()) {
        return Err("This journal came from a document. Reverse or cancel the document itself instead.".into());
    }
    if source.ends_with("_pending") || source == "expense_payment" || source.starts_with("depreciation") || source == "asset_pending" {
        return Err("Use the Void action on the entry (expense, income, salary) that created this journal.".into());
    }
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let r = reverse_journal(&tx, Some(actor), journal_id, reason)?;
    tx.commit().map_err(|e| e.to_string())?;
    Ok(r)
}

// ---------- bank statement lines posted straight to the ledger ----------

/// Posts a bank transaction to a ledger account (bank charges, interest, own transfers, refunds...) and takes it
/// out of the matching queue. `tds` is tax the bank deducted from interest.
pub fn classify_bank_txn(conn: &mut Connection, actor: i64, txn_id: i64, account_id: i64, tds: &str) -> Result<i64, String> {
    let tds = money(tds, "TDS")?;
    let tx = conn.transaction().map_err(|e| e.to_string())?;
    let (date, narration, debit, credit, status, bank_led): (String, String, i64, i64, String, Option<i64>) = tx
        .query_row(
            "SELECT t.txn_date, t.narration, t.debit_paise, t.credit_paise, t.status, a.ledger_account_id
             FROM bank_transactions t JOIN bank_batches b ON b.id = t.batch_id JOIN bank_accounts a ON a.id = b.account_id WHERE t.id = ?1",
            params![txn_id],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?)),
        )
        .map_err(|_| "Transaction not found.".to_string())?;
    if status != "unmatched" {
        return Err("Only unmatched transactions can be posted to the ledger.".into());
    }
    let bank_led = bank_led.ok_or("The bank account has no ledger account.")?;
    let (name, kind): (String, String) = tx.query_row("SELECT code || ' ' || name, type FROM accounts WHERE id = ?1", params![account_id], |r| Ok((r.get(0)?, r.get(1)?))).map_err(|_| "Choose an account.".to_string())?;
    if account_id == bank_led {
        return Err("Choose the account the money went to or came from, not the bank account itself.".into());
    }
    let lines = if credit > 0 {
        if tds > credit {
            return Err("TDS cannot exceed the credit.".into());
        }
        vec![dr(bank_led, credit), dr(acct(&tx, "1210")?, tds), cr(account_id, credit + tds)]
    } else {
        if tds > 0 {
            return Err("TDS applies to credits only.".into());
        }
        vec![dr(account_id, debit), cr(bank_led, debit)]
    };
    let _ = kind;
    let j = post(&tx, Some(actor), &date, &format!("Bank: {}", narration), "bank_txn", Some(txn_id), lines)?;
    tx.execute("UPDATE bank_transactions SET status = 'ignored', note = ?1 WHERE id = ?2", params![format!("Posted to {name}"), txn_id]).map_err(|e| e.to_string())?;
    db::audit(&tx, Some(actor), "bank_txn_posted", &format!("txn {txn_id} -> {name}"));
    tx.commit().map_err(|e| e.to_string())?;
    Ok(j)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ledger::{profit_and_loss, trial_balance};

    fn setup() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        db::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('o','x','owner')", []).unwrap();
        c
    }

    fn bal(c: &Connection, code: &str) -> i64 {
        let id = acct(c, code).unwrap();
        let (d, k): (i64, i64) = c.query_row("SELECT COALESCE(SUM(debit_paise),0), COALESCE(SUM(credit_paise),0) FROM journal_lines WHERE account_id = ?1", params![id], |r| Ok((r.get(0)?, r.get(1)?))).unwrap();
        d - k
    }

    fn expense(c: &Connection, amount: &str, gst: &str, tds: &str, via: &str) -> ExpenseInput {
        ExpenseInput { date: "2026-10-05".into(), vendor_id: None, bill_ref: "B1".into(), description: "Plotter paper".into(), expense_account_id: acct(c, "5400").unwrap(), amount: amount.into(), gst: gst.into(), tds: tds.into(), paid_via: via.into() }
    }

    fn rec(c: &mut Connection, amount: &str, gst: &str, tds: &str, via: &str) -> Result<i64, String> {
        let e = expense(c, amount, gst, tds, via);
        record_expense(c, 1, &e)
    }

    #[test]
    fn expense_input_credit_only_for_regular_registration() {
        let mut c = setup();
        c.execute("UPDATE company_profile SET gst_scheme = 'regular'", []).unwrap();
        rec(&mut c, "1000", "180", "", "cash").unwrap();
        assert_eq!((bal(&c, "5400"), bal(&c, "1220"), bal(&c, "1000")), (100_000, 18_000, -118_000));
        c.execute("UPDATE company_profile SET gst_scheme = 'composition'", []).unwrap();
        rec(&mut c, "1000", "180", "", "cash").unwrap();
        assert_eq!((bal(&c, "5400"), bal(&c, "1220")), (100_000 + 118_000, 18_000), "GST is cost when no credit can be claimed");
        assert!(trial_balance(&c, "2026-12-31").unwrap().balanced);
    }

    #[test]
    fn payable_expense_with_tds_settles_once_and_voids_only_when_unpaid() {
        let mut c = setup();
        let id = rec(&mut c, "10000", "", "1000", "payable").unwrap();
        assert_eq!((bal(&c, "5400"), bal(&c, "2200"), bal(&c, "2000")), (1_000_000, -100_000, -900_000));
        assert!(settle_expense(&mut c, 1, id, "2026-10-20", "payable").is_err());
        settle_expense(&mut c, 1, id, "2026-10-20", "cash").unwrap();
        assert_eq!((bal(&c, "2000"), bal(&c, "1000")), (0, -900_000));
        assert!(settle_expense(&mut c, 1, id, "2026-10-21", "cash").is_err());
        assert!(void_expense(&mut c, 1, id, "x").is_err(), "paid expenses cannot be voided");
        let id2 = rec(&mut c, "500", "", "", "cash").unwrap();
        assert!(void_expense(&mut c, 1, id2, " ").is_err());
        void_expense(&mut c, 1, id2, "Duplicate").unwrap();
        assert!(void_expense(&mut c, 1, id2, "again").is_err());
        assert!(list_expenses(&c).unwrap().iter().any(|e| e.id == id2 && e.voided));
        // validation
        assert!(rec(&mut c, "100", "", "200", "cash").unwrap_err().contains("TDS"));
        let mut bad = expense(&c, "100", "", "", "cash");
        bad.expense_account_id = acct(&c, "4000").unwrap();
        assert!(record_expense(&mut c, 1, &bad).unwrap_err().contains("expense account"));
    }

    #[test]
    fn rent_and_interest_income_with_tds_and_gst_rules() {
        let mut c = setup();
        c.execute("INSERT INTO properties (name, kind) VALUES ('Shop 3','commercial'), ('Flat 2','residential')", []).unwrap();
        let rent = |prop: i64, gst: &str, tds: &str| IncomeInput { date: "2026-10-05".into(), kind: "rent".into(), property_id: Some(prop), payer: "Tenant".into(), description: "".into(), amount: "50000".into(), gst: gst.into(), gst_mode: "intra".into(), tds: tds.into(), received_via: "bank".into() };
        assert!(record_income(&mut c, 1, &rent(1, "9000", "")).unwrap_err().contains("regular GST"));
        c.execute("UPDATE company_profile SET gst_scheme = 'regular'", []).unwrap();
        record_income(&mut c, 1, &rent(1, "9000", "5000")).unwrap();
        // Dr bank 54,000; Dr TDS recv 5,000; Cr rent 50,000; Cr CGST 4,500 + SGST 4,500
        assert_eq!((bal(&c, "1010"), bal(&c, "1210"), bal(&c, "4100")), (5_400_000, 500_000, -5_000_000));
        assert_eq!((bal(&c, "2100"), bal(&c, "2110")), (-450_000, -450_000));
        record_income(&mut c, 1, &rent(2, "", "")).unwrap();
        assert_eq!(bal(&c, "4110"), -5_000_000);
        let interest = IncomeInput { date: "2026-10-31".into(), kind: "interest".into(), property_id: None, payer: "HDFC".into(), description: "".into(), amount: "1000".into(), gst: "".into(), gst_mode: "intra".into(), tds: "100".into(), received_via: "cash".into() };
        record_income(&mut c, 1, &interest).unwrap();
        assert_eq!(bal(&c, "4200"), -100_000);
        let mut no_prop = rent(1, "", "");
        no_prop.property_id = None;
        assert!(record_income(&mut c, 1, &no_prop).is_err());
        assert_eq!(profit_and_loss(&c, "2026-04-01", "2027-03-31").unwrap().profit_paise, 5_000_000 + 5_000_000 + 100_000);
        assert!(trial_balance(&c, "2026-12-31").unwrap().balanced);
    }

    #[test]
    fn payroll_assets_depreciation_and_tax_payments() {
        let mut c = setup();
        let p = PayrollInput { date: "2026-10-31".into(), employee: "A. Draftsman".into(), period_label: "Oct 2026".into(), gross: "40000".into(), tds: "1500".into(), paid_via: "bank".into(), pf_employee: "".into(), pf_employer: "".into(), esi_employee: "".into(), esi_employer: "".into(), professional_tax: "".into() };
        let pid = record_payroll(&mut c, 1, &p).unwrap();
        assert_eq!((bal(&c, "5000"), bal(&c, "2200"), bal(&c, "1010")), (4_000_000, -150_000, -3_850_000));
        void_payroll(&mut c, 1, pid, "Wrong month").unwrap();
        assert_eq!(bal(&c, "5000"), 0);
        // asset: cost 1,00,000 at 15% WDV
        let a = AssetInput { name: "Plotter".into(), purchase_date: "2026-06-01".into(), cost: "100000".into(), rate_percent: "15".into(), paid_via: "bank".into() };
        let aid = add_asset(&mut c, 1, &a).unwrap();
        assert_eq!(post_depreciation(&mut c, 1, "2026-27").unwrap(), 1);
        assert_eq!(post_depreciation(&mut c, 1, "2026-27").unwrap(), 0, "once per year");
        assert_eq!(bal(&c, "5700"), 1_500_000);
        assert_eq!(post_depreciation(&mut c, 1, "2027-28").unwrap(), 1);
        assert_eq!(bal(&c, "5700"), 1_500_000 + 1_275_000, "second year is 15% of 85,000");
        let row = list_assets(&c).unwrap().into_iter().find(|r| r.id == aid).unwrap();
        assert_eq!(row.book_value_display, "₹72,250.00");
        assert!(post_depreciation(&mut c, 1, "26-27").is_err());
        // paying TDS to the government
        tax_payment(&mut c, 1, "tds", "2026-11-07", "1500", "bank", "").unwrap();
        assert_eq!(bal(&c, "2200"), 150_000, "the voided salary owes no TDS, so this payment leaves a debit balance (an overpayment)");
        assert!(tax_payment(&mut c, 1, "bogus", "2026-11-07", "5", "bank", "").is_err());
        assert!(trial_balance(&c, "2028-12-31").unwrap().balanced);
    }

    #[test]
    fn statutory_payroll_posts_to_its_own_liabilities_and_suggests_from_given_rates() {
        let mut c = setup();
        let p = PayrollInput {
            date: "2026-10-31".into(), employee: "B".into(), period_label: "Oct".into(), gross: "20000".into(), tds: "".into(), paid_via: "bank".into(),
            pf_employee: "1800".into(), pf_employer: "1800".into(), esi_employee: "150".into(), esi_employer: "650".into(), professional_tax: "200".into(),
        };
        let id = record_payroll(&mut c, 1, &p).unwrap();
        // expense = gross + employer contributions; liabilities hold the whole PF/ESI/PT; the bank pays gross less employee deductions
        assert_eq!((bal(&c, "5000"), bal(&c, "5010")), (2_000_000, 245_000));
        assert_eq!((bal(&c, "2310"), bal(&c, "2320"), bal(&c, "2330")), (-360_000, -80_000, -20_000));
        assert_eq!(bal(&c, "1010"), -(2_000_000 - 180_000 - 15_000 - 20_000));
        assert!(trial_balance(&c, "2026-12-31").unwrap().balanced);
        assert_eq!(list_payroll(&c).unwrap()[0].net_display, "₹17,850.00");
        // depositing PF clears the liability
        tax_payment(&mut c, 1, "pf", "2026-11-15", "3600", "bank", "ECR").unwrap();
        assert_eq!(bal(&c, "2310"), 0);
        // deductions larger than the gross are refused
        let mut bad = PayrollInput { gross: "1000".into(), ..p };
        bad.pf_employee = "2000".into();
        assert!(record_payroll(&mut c, 1, &bad).is_err());
        void_payroll(&mut c, 1, id, "Test").unwrap();
        assert_eq!((bal(&c, "5000"), bal(&c, "5010")), (0, 0));
        // suggestions: PF on basic capped at the ceiling; ESI only within the threshold
        let s = suggest_statutory("30000", "40000", "12", "15000", "21000", "0.75", "3.25").unwrap();
        assert_eq!((s.pf_employee.as_str(), s.esi_employee.as_str()), ("1800.00", "0.00"));
        let s2 = suggest_statutory("10000", "20000", "12", "15000", "21000", "0.75", "3.25").unwrap();
        assert_eq!((s2.pf_employee.as_str(), s2.esi_employee.as_str(), s2.esi_employer.as_str()), ("1200.00", "150.00", "650.00"));
        assert!(suggest_statutory("x", "1", "12", "0", "0", "1", "1").is_err());
    }

    #[test]
    fn manual_journals_validate_and_reverse_rules_protect_documents() {
        let mut c = setup();
        let (cash, cap, fees) = (acct(&c, "1000").unwrap(), acct(&c, "3000").unwrap(), acct(&c, "4000").unwrap());
        let line = |a: i64, d: &str, k: &str| JournalLineInput { account_id: a, debit: d.into(), credit: k.into(), memo: "".into() };
        assert!(manual_journal(&mut c, 1, "2026-04-01", "Opening", &[line(cash, "5000", ""), line(cap, "", "4000")]).is_err());
        assert!(manual_journal(&mut c, 1, "2026-04-01", "Opening", &[line(cash, "5000", "100"), line(cap, "", "4000")]).is_err());
        assert!(manual_journal(&mut c, 1, "2026-04-01", "  ", &[line(cash, "5000", ""), line(cap, "", "5000")]).is_err());
        let j = manual_journal(&mut c, 1, "2026-04-01", "Opening capital", &[line(cash, "5000", ""), line(cap, "", "5000")]).unwrap();
        reverse_manual(&mut c, 1, j, "Entered twice").unwrap();
        assert!(reverse_manual(&mut c, 1, j, "again").is_err());
        let rev: i64 = c.query_row("SELECT id FROM journals WHERE source_type = 'reversal'", [], |r| r.get(0)).unwrap();
        assert!(reverse_manual(&mut c, 1, rev, "undo the undo").unwrap_err().contains("document"));
        // a journal from an entry must be voided there
        rec(&mut c, "100", "", "", "cash").unwrap();
        let exp_journal: i64 = c.query_row("SELECT journal_id FROM expenses", [], |r| r.get(0)).unwrap();
        assert!(reverse_manual(&mut c, 1, exp_journal, "x").unwrap_err().contains("Void"));
        let _ = fees;
    }

    #[test]
    fn bank_lines_post_to_the_ledger_and_leave_the_matching_queue() {
        let mut c = setup();
        c.execute("INSERT INTO bank_accounts (name, is_primary, ledger_account_id) VALUES ('HDFC', 1, NULL)", []).unwrap();
        crate::ledger::ensure_bank_ledger(&c, 1, "HDFC").unwrap();
        c.execute("INSERT INTO bank_batches (filename, file_sha256, row_count, account_id) VALUES ('f','h',2,1)", []).unwrap();
        for (i, (narr, dbt, crd)) in [("Bank charges", 45_00, 0), ("Interest credited", 0, 900_00)].iter().enumerate() {
            c.execute("INSERT INTO bank_transactions (batch_id, row_no, txn_date, narration, debit_paise, credit_paise, raw_row, dedupe_key) VALUES (1,?1,'2026-10-31',?2,?3,?4,'r',?5)", params![i as i64 + 1, narr, dbt, crd, format!("k{i}")]).unwrap();
        }
        let (charges, interest) = (acct(&c, "5800").unwrap(), acct(&c, "4200").unwrap());
        classify_bank_txn(&mut c, 1, 1, charges, "").unwrap();
        classify_bank_txn(&mut c, 1, 2, interest, "100").unwrap();
        assert_eq!((bal(&c, "5800"), bal(&c, "4200"), bal(&c, "1210")), (4_500, -100_000, 10_000));
        let bank = c.query_row("SELECT ledger_account_id FROM bank_accounts", [], |r| r.get::<_, i64>(0)).unwrap();
        let bank_bal: i64 = c.query_row("SELECT COALESCE(SUM(debit_paise - credit_paise),0) FROM journal_lines WHERE account_id = ?1", params![bank], |r| r.get(0)).unwrap();
        assert_eq!(bank_bal, 90_000 - 4_500);
        assert!(classify_bank_txn(&mut c, 1, 1, charges, "").is_err(), "no double posting");
        let st: String = c.query_row("SELECT status FROM bank_transactions WHERE id = 1", [], |r| r.get(0)).unwrap();
        assert_eq!(st, "ignored");
        assert!(trial_balance(&c, "2026-12-31").unwrap().balanced);
    }
}
