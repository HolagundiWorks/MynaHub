//! A C interface to Office Core, for native apps that share the office company file (MynaOps, and others).
//!
//! The operations are the common ones from `office_core::host` (`company.open`, `company.create`, `auth.login` and so on) and the
//! shared records: clients, projects with their briefs, staff, leave and the library. `track.apply` lets an app add its own tables
//! to the file under the schema-track rules (see `office_core::track`). The contract is written out in `docs/office-abi.md`.

pub mod bridge;
pub mod ffi;
