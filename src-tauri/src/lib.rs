mod accounting;
mod admin;
mod auth;
mod bank;
mod billing;
mod clients;
mod commands;
mod db;
mod documents;
mod entries;
mod ledger;
mod pdf;
#[cfg(test)]
mod qa_tests;
mod receipts;
mod registers;

use commands::AppState;
use tauri::Manager;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .setup(|app| {
            // OS-managed application-data directory; layout per guidelines section 3.2.
            let root = app.path().app_data_dir()?;
            let conn = db::open(&root.join("database").join("office.sqlite"))?;
            app.manage(AppState::new(conn, root.join("files")));
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
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
