mod accounting;
mod admin;
mod auth;
mod bank;
mod boards;
mod billing;
mod bridge;
mod builtin_html;
mod builtin_templates;
mod clients;
mod commands;
mod company_file;
mod db;
mod docx;
mod documents;
mod entries;
mod ffi;
mod grammar;
mod ledger;
mod pdf;
mod practice;
#[cfg(test)]
mod qa_tests;
mod receipts;
mod registers;
mod store;
mod sheets;
mod stages;
mod studio;
mod template_engine;
mod timebook;
mod transmittals;
mod typography;
mod vault;

use commands::AppState;
use tauri::Manager;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            // No company is open at start; the user creates or opens a company file first.
            let root = app.path().app_data_dir()?;
            app.manage(AppState::new(company_file::placeholder(), root));
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            company_file::company_active,
            company_file::company_validate,
            company_file::company_recent_list,
            company_file::company_recent_remove,
            company_file::company_open,
            company_file::company_create,
            company_file::company_close,
            company_file::company_legacy_info,
            company_file::company_convert_legacy,
            commands::auth_status,
            commands::auth_setup,
            commands::auth_login,
            commands::auth_unlock,
            commands::auth_lock,
            commands::auth_logout,
            commands::company_get_profile,
            commands::company_save_profile,
            clients::client_list,
            clients::client_save,
            clients::client_set_archived,
            clients::project_list,
            clients::project_save,
            clients::project_set_archived,
            clients::project_brief_get,
            clients::project_brief_save,
            stages::project_stages,
            stages::project_stages_apply,
            stages::project_stage_mark,
            stages::project_stage_unmark,
            billing::invoice_calculate,
            billing::invoice_list,
            billing::invoice_get,
            billing::invoice_save_draft,
            billing::invoice_issue,
            billing::invoice_pdf,
            billing::invoice_convert_proforma,
            billing::invoice_cancel,
            billing::credit_note_issue,
            billing::credit_note_list,
            billing::credit_note_pdf_data,
            bank::bank_unmatch,
            documents::doc_fields,
            documents::doc_list,
            documents::doc_get,
            documents::doc_save_draft,
            documents::doc_issue,
            documents::doc_register_inward,
            documents::doc_pdf,
            documents::doc_revise,
            documents::doc_mark_signed,
            documents::doc_cancel,
            documents::doc_bill_milestone,
            documents::doc_attach,
            documents::doc_attachments,
            documents::doc_attachment_data,
            documents::template_list,
            documents::template_versions_list,
            documents::template_create,
            documents::template_publish,
            documents::template_duplicate,
            documents::template_set_archived,
            documents::template_preview,
            studio::studio_fields,
            studio::studio_open,
            studio::studio_validate,
            studio::studio_preview,
            studio::studio_save_draft,
            studio::studio_publish,
            studio::studio_restore,
            studio::studio_discard_draft,
            studio::studio_create,
            accounting::account_list,
            accounting::account_create,
            accounting::account_set_active,
            accounting::period_list,
            accounting::period_set,
            accounting::ledger_backfill,
            accounting::journal_post,
            accounting::journal_reverse,
            accounting::journal_list,
            accounting::journal_lines,
            accounting::report_trial_balance,
            accounting::report_ledger,
            accounting::report_pnl,
            accounting::report_balance_sheet,
            accounting::report_tax_summary,
            accounting::report_ar_check,
            accounting::report_tax_estimate,
            accounting::report_tax_slabs,
            practice::client_statement_report,
            practice::party_ledger_report,
            practice::project_fee_position,
            practice::project_report,
            registers::bank_txn_page,
            registers::journal_page,
            registers::invoice_page,
            registers::doc_page,
            registers::audit_page,
            accounting::payroll_suggest,
            accounting::vendor_list,
            accounting::vendor_save,
            accounting::property_list,
            accounting::property_save,
            accounting::expense_list,
            accounting::expense_record,
            accounting::expense_settle,
            accounting::expense_void,
            accounting::income_list,
            accounting::income_record,
            accounting::income_void,
            accounting::payroll_list,
            accounting::payroll_record,
            accounting::payroll_void,
            accounting::asset_list,
            accounting::asset_add,
            accounting::asset_depreciate,
            accounting::tax_payment,
            accounting::bank_classify,
            receipts::receipt_balance,
            receipts::receipt_list,
            receipts::receipt_suggest_tds,
            receipts::receipt_record,
            receipts::invoice_writeoff,
            receipts::invoice_writeoff_reverse,
            receipts::invoice_writeoff_list,
            receipts::receipt_reverse,
            receipts::receipt_pdf,
            bank::bank_csv_preview,
            bank::bank_import_commit,
            bank::bank_txn_list,
            bank::bank_summary,
            bank::bank_rejected_list,
            bank::bank_txn_suggest,
            bank::bank_reconcile,
            bank::bank_txn_ignore,
            bank::bank_account_list,
            bank::bank_account_save,
            bank::bank_manual_entry,
            bank::bank_unlinked_receipts,
            bank::bank_link_receipts,
            admin::auth_change_password,
            admin::auth_recover,
            admin::security_get,
            admin::security_set_idle_minutes,
            admin::user_list,
            admin::user_create,
            admin::user_update,
            admin::audit_list,
            admin::backup_create,
            admin::backup_list,
            admin::backup_restore,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
