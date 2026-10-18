use rusqlite::Connection;
#[cfg(test)]
use std::path::Path;

pub use office_core::schema::{audit, latest_version};
#[cfg(test)]
pub use office_core::schema::{apply_first, MIGRATIONS};

#[cfg(test)]
pub fn open(path: &Path) -> Result<Connection, String> {
    office_core::schema::open(path, migrate)
}

/// Brings the company file to the current schema, then adds the template library this app ships with.
pub fn migrate(conn: &mut Connection) -> Result<(), String> {
    office_core::schema::migrate(conn)?;
    crate::builtin_templates::seed(conn)?;
    crate::builtin_html::seed(conn)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

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
        let library: i64 = conn.query_row("SELECT COUNT(*) FROM templates WHERE built_in = 1", [], |r| r.get(0)).unwrap();
        assert!(library >= 24, "the template library arrives with the upgrade");
        let n: i64 = conn.query_row("SELECT COUNT(*) FROM invoices", [], |r| r.get(0)).unwrap();
        assert_eq!(n, 1);
        let bal: i64 = conn.query_row("SELECT total_paise - received_paise - tds_paise - credited_paise - written_off_paise FROM invoice_balances", [], |r| r.get(0)).unwrap();
        assert_eq!(bal, 1000);
    }
}
