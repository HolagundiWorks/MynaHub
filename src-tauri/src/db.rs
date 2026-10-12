use rusqlite::Connection;
use std::path::Path;

const MIGRATIONS: &[&str] = &[
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
];

pub fn open(path: &Path) -> Result<Connection, String> {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    let mut conn = Connection::open(path).map_err(|e| e.to_string())?;
    conn.pragma_update(None, "foreign_keys", "ON").map_err(|e| e.to_string())?;
    conn.pragma_update(None, "journal_mode", "WAL").map_err(|e| e.to_string())?;
    conn.busy_timeout(std::time::Duration::from_secs(5)).map_err(|e| e.to_string())?;
    migrate(&mut conn)?;
    Ok(conn)
}

/// Applies pending migrations, each in its own transaction, tracked by `user_version`.
/// The schema version this build expects.
pub fn latest_version() -> usize {
    MIGRATIONS.len()
}

/// Test helper: builds a database at schema version `n` (as an older release would have left it).
#[cfg(test)]
pub fn apply_first(conn: &mut Connection, n: usize) -> Result<(), String> {
    for (i, sql) in MIGRATIONS.iter().take(n).enumerate() {
        conn.execute_batch(sql).map_err(|e| e.to_string())?;
        conn.pragma_update(None, "user_version", (i + 1) as i64).map_err(|e| e.to_string())?;
    }
    Ok(())
}

pub fn migrate(conn: &mut Connection) -> Result<(), String> {
    let current: usize = conn
        .pragma_query_value(None, "user_version", |r| r.get(0))
        .map_err(|e| e.to_string())?;
    for (i, sql) in MIGRATIONS.iter().enumerate().skip(current) {
        let tx = conn.transaction().map_err(|e| e.to_string())?;
        tx.execute_batch(sql).map_err(|e| format!("migration {} failed: {e}", i + 1))?;
        tx.pragma_update(None, "user_version", (i + 1) as i64).map_err(|e| e.to_string())?;
        tx.commit().map_err(|e| e.to_string())?;
    }
    crate::builtin_templates::seed(conn)?;
    crate::builtin_html::seed(conn)?;
    Ok(())
}

pub fn audit(conn: &Connection, user_id: Option<i64>, event: &str, detail: &str) {
    let _ = conn.execute(
        "INSERT INTO audit_log (user_id, event, detail) VALUES (?1, ?2, ?3)",
        rusqlite::params![user_id, event, detail],
    );
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn migrations_apply_and_audit_is_append_only() {
        let mut conn = Connection::open_in_memory().unwrap();
        migrate(&mut conn).unwrap();
        audit(&conn, None, "test", "");
        assert!(conn.execute("DELETE FROM audit_log", []).is_err());
        assert!(conn.execute("UPDATE audit_log SET event='x'", []).is_err());
        migrate(&mut conn).unwrap(); // idempotent
    }

    /// An office that already used release 6 (before bank accounts, ledger, documents...) must upgrade cleanly with its data.
    #[test]
    fn upgrades_an_existing_workspace_and_keeps_its_data() {
        let mut conn = Connection::open_in_memory().unwrap();
        conn.pragma_update(None, "foreign_keys", "ON").unwrap();
        for (i, sql) in MIGRATIONS.iter().take(6).enumerate() {
            conn.execute_batch(sql).unwrap();
            conn.pragma_update(None, "user_version", (i + 1) as i64).unwrap();
        }
        conn.execute_batch(
            "INSERT INTO users (username, password_hash, role) VALUES ('o','x','owner');
             INSERT INTO clients (name) VALUES ('Acme');
             INSERT INTO projects (client_id, name) VALUES (1,'Villa');
             INSERT INTO invoices (project_id, kind, status, number, issue_date, gst_mode, total_paise) VALUES (1,'tax_invoice','draft',NULL,'2026-10-01','none',1000);
             INSERT INTO bank_batches (filename, file_sha256, row_count) VALUES ('old.csv','abc',1);
             INSERT INTO bank_transactions (batch_id,row_no,txn_date,credit_paise,raw_row,dedupe_key) VALUES (1,1,'2026-10-02',500,'r','k');",
        )
        .unwrap();
        migrate(&mut conn).unwrap();
        let v: i64 = conn.pragma_query_value(None, "user_version", |r| r.get(0)).unwrap();
        assert_eq!(v as usize, MIGRATIONS.len());
        // old statement rows were attributed to a default account that has a ledger account
        let (acct, led): (String, Option<i64>) = conn.query_row("SELECT a.name, a.ledger_account_id FROM bank_batches b JOIN bank_accounts a ON a.id = b.account_id", [], |r| Ok((r.get(0)?, r.get(1)?))).unwrap();
        assert_eq!(acct, "Main account");
        assert!(led.is_some());
        let n: i64 = conn.query_row("SELECT COUNT(*) FROM invoices", [], |r| r.get(0)).unwrap();
        assert_eq!(n, 1);
        let bal: i64 = conn.query_row("SELECT total_paise - received_paise - tds_paise - credited_paise FROM invoice_balances", [], |r| r.get(0)).unwrap();
        assert_eq!(bal, 1000);
    }
}
