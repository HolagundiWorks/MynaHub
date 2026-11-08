//! Office Core: the shared company file and records used by the architecture office apps (Kivo, Falcon).
//!
//! The crate owns the file format (the migrations), the audit log and the records every app needs: clients, projects and their
//! briefs, staff, leave and the office library. An app adds its own commands, sessions and permissions on top. Apps never write
//! each other's tables; they share these records and refer to one another by id.

pub mod approvals;
pub mod studio_drafts;
pub mod auth;
pub mod check;
pub mod clients;
pub mod company;
pub mod context;
pub mod falcon_csv;
pub mod falcon_import;
pub mod hash;
pub mod host;
pub mod office;
pub mod practice;
pub mod profile;
pub mod project_codes;
pub mod project_workspace;
pub mod refs;
pub mod share;
pub mod schema;
pub mod store;
pub mod subcontracts;
pub mod suite;
pub mod tasks;
pub mod tds26as;
pub mod track;
pub mod users;
pub mod vault;
pub mod workspace;

pub use schema::{audit, latest_version, migrate};
