//! O-Studio company-file engine (library / cdylib for the WinUI host).
//!
//! The production window is WinUI 3 (`native/Adms.Desktop`), which loads
//! `architect_office_desktop_lib` and calls through the C ABI in `ffi`.

use office_core::{auth, store, vault};

mod accounting;
mod approvals_cmd;
mod studio_drafts_cmd;
mod desk;
mod admin;
mod bank;
mod boards;
mod billing;
mod bridge;
mod builtin_html;
mod builtin_templates;
mod clientfile;
mod clients;
mod commands;
mod company_file;
mod db;
mod docx;
mod documents;
mod entries;
mod ffi;

// The shared company-file interface (`office_*`, from office-ffi) is linked in, so this one library is the whole engine.
pub use shared_office::ffi as office_interface;
mod grammar;
mod ledger;
mod pdf;
mod practice;
#[cfg(test)]
mod qa_tests;
mod receipts;
mod registers;
mod taxabstract;
mod sheets;
mod stages;
mod studio;
mod template_engine;
mod timebook;
mod transmittals;
mod fieldwork;
mod office;
mod typography;
mod workflow;
