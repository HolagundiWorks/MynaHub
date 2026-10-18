//! The company file: one SQLite file whose schema is versioned by `user_version`. Migrations only ever append.

use rusqlite::Connection;
use std::path::Path;

pub const MIGRATIONS: &[&str] = &[
    include_str!("../migrations/0001_init.sql"),
    include_str!("../migrations/0002_clients_projects.sql"),
    include_str!("../migrations/0003_invoices.sql"),
    include_str!("../migrations/0004_gst_scheme_receipts.sql"),
    include_str!("../migrations/0005_proforma_link.sql"),
    include_str!("../migrations/0006_bank.sql"),
    include_str!("../migrations/0007_bank_accounts.sql"),
    include_str!("../migrations/0008_billing_completion.sql"),
    include_str!("../migrations/0009_cancelled_frozen.sql"),
    include_str!("../migrations/0010_documents.sql"),
    include_str!("../migrations/0011_ledger.sql"),
    include_str!("../migrations/0012_payroll_statutory.sql"),
    include_str!("../migrations/0013_company_file.sql"),
    include_str!("../migrations/0014_template_library.sql"),
    include_str!("../migrations/0015_template_studio.sql"),
    include_str!("../migrations/0016_consultancy_type.sql"),
    include_str!("../migrations/0017_logo_and_time.sql"),
    include_str!("../migrations/0018_logo_shapes.sql"),
    include_str!("../migrations/0019_transmittals.sql"),
    include_str!("../migrations/0020_document_accent.sql"),
    include_str!("../migrations/0021_onboarding_brief.sql"),
    include_str!("../migrations/0022_boards.sql"),
    include_str!("../migrations/0023_party_ledger.sql"),
    include_str!("../migrations/0024_project_stages.sql"),
    include_str!("../migrations/0025_fieldwork.sql"),
    include_str!("../migrations/0026_team_library.sql"),
    include_str!("../migrations/0027_aorms_links.sql"),
    include_str!("../migrations/0028_proposals_meetings_attendance_26as.sql"),
];

/// The schema version this build expects.
pub fn latest_version() -> usize {
    MIGRATIONS.len()
}

/// Builds a database at schema version `n` (as an older release would have left it).
pub fn apply_first(conn: &mut Connection, n: usize) -> Result<(), String> {
    for (i, sql) in MIGRATIONS.iter().take(n).enumerate() {
        conn.execute_batch(sql).map_err(|e| e.to_string())?;
        conn.pragma_update(None, "user_version", (i + 1) as i64).map_err(|e| e.to_string())?;
    }
    Ok(())
}

/// Applies pending migrations, each in its own transaction, tracked by `user_version`.
pub fn migrate(conn: &mut Connection) -> Result<(), String> {
    let current: usize = conn.pragma_query_value(None, "user_version", |r| r.get(0)).map_err(|e| e.to_string())?;
    for (i, sql) in MIGRATIONS.iter().enumerate().skip(current) {
        let tx = conn.transaction().map_err(|e| e.to_string())?;
        tx.execute_batch(sql).map_err(|e| format!("migration {} failed: {e}", i + 1))?;
        tx.pragma_update(None, "user_version", (i + 1) as i64).map_err(|e| e.to_string())?;
        tx.commit().map_err(|e| e.to_string())?;
    }
    Ok(())
}

/// Opens (creating if needed) a company file with the settings every app uses, then brings it up to date with `prepare`.
pub fn open(path: &Path, prepare: fn(&mut Connection) -> Result<(), String>) -> Result<Connection, String> {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    let mut conn = Connection::open(path).map_err(|e| e.to_string())?;
    conn.pragma_update(None, "foreign_keys", "ON").map_err(|e| e.to_string())?;
    conn.pragma_update(None, "journal_mode", "WAL").map_err(|e| e.to_string())?;
    conn.busy_timeout(std::time::Duration::from_secs(5)).map_err(|e| e.to_string())?;
    prepare(&mut conn)?;
    Ok(conn)
}

/// Appends to the audit log, which the schema makes append-only.
pub fn audit(conn: &Connection, user_id: Option<i64>, event: &str, detail: &str) {
    let _ = conn.execute("INSERT INTO audit_log (user_id, event, detail) VALUES (?1, ?2, ?3)", rusqlite::params![user_id, event, detail]);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn migrations_apply_once_and_the_audit_log_is_append_only() {
        let mut conn = Connection::open_in_memory().unwrap();
        migrate(&mut conn).unwrap();
        audit(&conn, None, "test", "");
        assert!(conn.execute("DELETE FROM audit_log", []).is_err());
        assert!(conn.execute("UPDATE audit_log SET event='x'", []).is_err());
        migrate(&mut conn).unwrap();
        let v: i64 = conn.pragma_query_value(None, "user_version", |r| r.get(0)).unwrap();
        assert_eq!(v as usize, latest_version());
    }
}
