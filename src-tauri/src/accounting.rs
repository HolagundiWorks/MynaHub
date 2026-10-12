//! Tauri commands for the ledger, its reports and the accounting entries.
//! Viewing needs the Finance permission; posting needs Post; closing periods and reversing journals ask for the password again.

use crate::admin::reauth;
use crate::auth::Permission;
use crate::commands::{require, AppState, INTERNAL};
use crate::entries::{self, AssetInput, ExpenseInput, IncomeInput, JournalLineInput, PayrollInput, Property, Vendor};
use crate::ledger;
use tauri::State;

macro_rules! conn {
    ($state:expr) => {
        $state.db.lock().map_err(|_| INTERNAL)?
    };
}

// ----- chart of accounts and periods

#[tauri::command]
pub fn account_list(state: State<AppState>, as_of: String) -> Result<Vec<ledger::AccountRow>, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    ledger::list_accounts(&c, &as_of)
}

#[tauri::command]
pub fn account_create(state: State<AppState>, code: String, name: String, kind: String) -> Result<i64, String> {
    let uid = require(&state, Permission::Post)?;
    let c = conn!(state);
    ledger::create_account(&c, uid, &code, &name, &kind)
}

#[tauri::command]
pub fn account_set_active(state: State<AppState>, id: i64, active: bool) -> Result<(), String> {
    let uid = require(&state, Permission::Post)?;
    let c = conn!(state);
    ledger::set_account_active(&c, uid, id, active)
}

#[tauri::command]
pub fn period_list(state: State<AppState>) -> Result<Vec<ledger::PeriodRow>, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    ledger::list_periods(&c)
}

#[tauri::command]
pub fn period_set(state: State<AppState>, fy: String, closed: bool, password: String) -> Result<(), String> {
    let uid = require(&state, Permission::ClosePeriod)?;
    reauth(&state, &password)?;
    let c = conn!(state);
    ledger::set_period_status(&c, uid, &fy, closed)
}

#[tauri::command]
pub fn ledger_backfill(state: State<AppState>) -> Result<(i64, i64, i64), String> {
    let uid = require(&state, Permission::Post)?;
    let c = conn!(state);
    ledger::backfill(&c, uid)
}

// ----- journals

#[tauri::command]
pub fn journal_post(state: State<AppState>, date: String, narration: String, lines: Vec<JournalLineInput>) -> Result<i64, String> {
    let uid = require(&state, Permission::Post)?;
    let mut c = conn!(state);
    entries::manual_journal(&mut c, uid, &date, &narration, &lines)
}

#[tauri::command]
pub fn journal_reverse(state: State<AppState>, id: i64, reason: String, password: String) -> Result<i64, String> {
    let uid = require(&state, Permission::Post)?;
    reauth(&state, &password)?;
    let mut c = conn!(state);
    entries::reverse_manual(&mut c, uid, id, &reason)
}

#[tauri::command]
pub fn journal_list(state: State<AppState>, from: String, to: String, limit: i64) -> Result<Vec<ledger::JournalRow>, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    ledger::journal_list(&c, &from, &to, limit)
}

#[tauri::command]
pub fn journal_lines(state: State<AppState>, id: i64) -> Result<Vec<ledger::JournalLineRow>, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    ledger::journal_lines(&c, id)
}

// ----- reports

#[tauri::command]
pub fn report_trial_balance(state: State<AppState>, as_of: String) -> Result<ledger::TrialBalance, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    ledger::trial_balance(&c, &as_of)
}

#[tauri::command]
pub fn report_ledger(state: State<AppState>, account_id: i64, from: String, to: String) -> Result<ledger::AccountLedger, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    ledger::account_ledger(&c, account_id, &from, &to)
}

#[tauri::command]
pub fn report_pnl(state: State<AppState>, from: String, to: String) -> Result<ledger::ProfitAndLoss, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    ledger::profit_and_loss(&c, &from, &to)
}

#[tauri::command]
pub fn report_balance_sheet(state: State<AppState>, as_of: String) -> Result<ledger::BalanceSheet, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    ledger::balance_sheet(&c, &as_of)
}

#[tauri::command]
pub fn report_tax_summary(state: State<AppState>, from: String, to: String) -> Result<ledger::TaxSummary, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    ledger::tax_summary(&c, &from, &to)
}

#[tauri::command]
pub fn report_ar_check(state: State<AppState>, as_of: String) -> Result<ledger::ArCheck, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    ledger::ar_check(&c, &as_of)
}

#[tauri::command]
pub fn report_tax_estimate(state: State<AppState>, from: String, to: String, rate_percent: String) -> Result<ledger::TaxEstimate, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    ledger::income_tax_estimate(&c, &from, &to, &rate_percent)
}

#[tauri::command]
pub fn report_tax_slabs(state: State<AppState>, from: String, to: String, slabs: Vec<ledger::Slab>, cess_percent: String) -> Result<ledger::TaxEstimate, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    ledger::income_tax_by_slabs(&c, &from, &to, &slabs, &cess_percent)
}

#[tauri::command]
pub fn payroll_suggest(
    state: State<AppState>, basic: String, gross: String, pf_rate: String, pf_ceiling: String, esi_threshold: String, esi_ee_rate: String, esi_er_rate: String,
) -> Result<entries::PayrollSuggestion, String> {
    require(&state, Permission::Finance)?;
    entries::suggest_statutory(&basic, &gross, &pf_rate, &pf_ceiling, &esi_threshold, &esi_ee_rate, &esi_er_rate)
}

// ----- vendors and properties

#[tauri::command]
pub fn vendor_list(state: State<AppState>) -> Result<Vec<Vendor>, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    entries::list_vendors(&c)
}

#[tauri::command]
pub fn vendor_save(state: State<AppState>, vendor: Vendor) -> Result<i64, String> {
    require(&state, Permission::Post)?;
    let c = conn!(state);
    entries::save_vendor(&c, &vendor)
}

#[tauri::command]
pub fn property_list(state: State<AppState>) -> Result<Vec<Property>, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    entries::list_properties(&c)
}

#[tauri::command]
pub fn property_save(state: State<AppState>, property: Property) -> Result<i64, String> {
    require(&state, Permission::Post)?;
    let c = conn!(state);
    entries::save_property(&c, &property)
}

// ----- entries

#[tauri::command]
pub fn expense_list(state: State<AppState>) -> Result<Vec<entries::ExpenseRow>, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    entries::list_expenses(&c)
}

#[tauri::command]
pub fn expense_record(state: State<AppState>, expense: ExpenseInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Post)?;
    let mut c = conn!(state);
    entries::record_expense(&mut c, uid, &expense)
}

#[tauri::command]
pub fn expense_settle(state: State<AppState>, id: i64, date: String, paid_via: String) -> Result<(), String> {
    let uid = require(&state, Permission::Post)?;
    let mut c = conn!(state);
    entries::settle_expense(&mut c, uid, id, &date, &paid_via)
}

#[tauri::command]
pub fn expense_void(state: State<AppState>, id: i64, reason: String) -> Result<(), String> {
    let uid = require(&state, Permission::Post)?;
    let mut c = conn!(state);
    entries::void_expense(&mut c, uid, id, &reason)
}

#[tauri::command]
pub fn income_list(state: State<AppState>) -> Result<Vec<entries::IncomeRow>, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    entries::list_income(&c)
}

#[tauri::command]
pub fn income_record(state: State<AppState>, income: IncomeInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Post)?;
    let mut c = conn!(state);
    entries::record_income(&mut c, uid, &income)
}

#[tauri::command]
pub fn income_void(state: State<AppState>, id: i64, reason: String) -> Result<(), String> {
    let uid = require(&state, Permission::Post)?;
    let mut c = conn!(state);
    entries::void_income(&mut c, uid, id, &reason)
}

#[tauri::command]
pub fn payroll_list(state: State<AppState>) -> Result<Vec<entries::PayrollRow>, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    entries::list_payroll(&c)
}

#[tauri::command]
pub fn payroll_record(state: State<AppState>, payroll: PayrollInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Post)?;
    let mut c = conn!(state);
    entries::record_payroll(&mut c, uid, &payroll)
}

#[tauri::command]
pub fn payroll_void(state: State<AppState>, id: i64, reason: String) -> Result<(), String> {
    let uid = require(&state, Permission::Post)?;
    let mut c = conn!(state);
    entries::void_payroll(&mut c, uid, id, &reason)
}

#[tauri::command]
pub fn asset_list(state: State<AppState>) -> Result<Vec<entries::AssetRow>, String> {
    require(&state, Permission::Finance)?;
    let c = conn!(state);
    entries::list_assets(&c)
}

#[tauri::command]
pub fn asset_add(state: State<AppState>, asset: AssetInput) -> Result<i64, String> {
    let uid = require(&state, Permission::Post)?;
    let mut c = conn!(state);
    entries::add_asset(&mut c, uid, &asset)
}

#[tauri::command]
pub fn asset_depreciate(state: State<AppState>, fy: String) -> Result<usize, String> {
    let uid = require(&state, Permission::Post)?;
    let mut c = conn!(state);
    entries::post_depreciation(&mut c, uid, &fy)
}

#[tauri::command]
pub fn tax_payment(state: State<AppState>, kind: String, date: String, amount: String, paid_via: String, narration: String) -> Result<i64, String> {
    let uid = require(&state, Permission::Post)?;
    let mut c = conn!(state);
    entries::tax_payment(&mut c, uid, &kind, &date, &amount, &paid_via, &narration)
}

#[tauri::command]
pub fn bank_classify(state: State<AppState>, txn_id: i64, account_id: i64, tds: String) -> Result<i64, String> {
    let uid = require(&state, Permission::Post)?;
    let mut c = conn!(state);
    entries::classify_bank_txn(&mut c, uid, txn_id, account_id, &tds)
}
