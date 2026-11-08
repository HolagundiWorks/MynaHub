//! Schema tracks: how an app adds its own tables to a shared company file.
//!
//! The shared records and the original Kivo tables are the `core` chain, versioned by `user_version`. Every other app (Falcon, and
//! later Kivo additions) has its own track with its own list of migrations and its own version, kept in `schema_tracks`. Two apps
//! can therefore ship migrations independently and open the same file in any order.
//!
//! A track may only create objects named `<track>_...` and may not alter anything that already exists. It can still refer to the
//! shared tables, for example a foreign key to `projects(id)`. This keeps one app from changing another app's data or shape.

use rusqlite::Connection;
use std::collections::BTreeMap;

/// An app's own migrations. `name` is lower-case letters and digits, starts with a letter, and is not `core`.
pub struct Track {
    pub name: &'static str,
    pub migrations: &'static [&'static str],
}

fn valid_name(name: &str) -> bool {
    name != "core" && name.len() <= 16 && name.chars().next().is_some_and(|c| c.is_ascii_lowercase()) && name.chars().all(|c| c.is_ascii_lowercase() || c.is_ascii_digit())
}

fn ensure_table(conn: &Connection) -> Result<(), String> {
    conn.execute_batch("CREATE TABLE IF NOT EXISTS schema_tracks (track TEXT PRIMARY KEY, version INTEGER NOT NULL CHECK (version >= 0));")
        .map_err(|e| e.to_string())
}

/// The version of a track recorded in this file; 0 when the app has never opened it.
pub fn version(conn: &Connection, name: &str) -> Result<usize, String> {
    ensure_table(conn)?;
    let found: Option<i64> = conn
        .query_row("SELECT version FROM schema_tracks WHERE track = ?1", [name], |r| r.get(0))
        .map(Some)
        .or_else(|e| if matches!(e, rusqlite::Error::QueryReturnedNoRows) { Ok(None) } else { Err(e) })
        .map_err(|e| e.to_string())?;
    Ok(found.unwrap_or(0) as usize)
}

/// Everything in the file that a migration could add or change: name -> definition.
fn objects(conn: &Connection) -> Result<BTreeMap<String, String>, String> {
    let mut stmt = conn
        .prepare("SELECT name, COALESCE(sql, '') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'")
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?)))
        .map_err(|e| e.to_string())?
        .collect::<Result<BTreeMap<_, _>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

/// Applies the track's pending migrations, each in its own transaction. Refuses a file made by a newer build of the app.
pub fn migrate(conn: &mut Connection, track: &Track) -> Result<(), String> {
    migrate_slice(conn, track.name, track.migrations)
}

/// The same as `migrate`, for an app that supplies its migrations at run time (through the C interface).
pub fn migrate_slice(conn: &mut Connection, name: &str, migrations: &[&str]) -> Result<(), String> {
    if !valid_name(name) {
        return Err(format!("'{name}' is not a usable track name."));
    }
    let current = version(conn, name)?;
    if current > migrations.len() {
        return Err(format!("This company file was made by a newer version of the {name} app. Update the app to open it."));
    }
    let prefix = format!("{name}_");
    for (index, sql) in migrations.iter().enumerate().skip(current) {
        let tx = conn.transaction().map_err(|e| e.to_string())?;
        let before = objects(&tx)?;
        tx.execute_batch(sql).map_err(|e| format!("{} migration {} failed: {e}", name, index + 1))?;
        for (name, definition) in objects(&tx)? {
            match before.get(&name) {
                None if !name.starts_with(&prefix) => {
                    return Err(format!("{} migration {} created '{name}'; a track may only create objects named {prefix}...", name, index + 1));
                }
                Some(old) if *old != definition => {
                    return Err(format!("{} migration {} changed '{name}'; a track may not change tables it does not own.", name, index + 1));
                }
                _ => {}
            }
        }
        if objects(&tx)?.len() < before.len() {
            return Err(format!("{} migration {} removed something it does not own.", name, index + 1));
        }
        tx.execute(
            "INSERT INTO schema_tracks (track, version) VALUES (?1, ?2) ON CONFLICT(track) DO UPDATE SET version = excluded.version",
            rusqlite::params![name, (index + 1) as i64],
        )
        .map_err(|e| e.to_string())?;
        tx.commit().map_err(|e| e.to_string())?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn core() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::schema::migrate(&mut c).unwrap();
        c
    }

    const AQC_1: &str = "CREATE TABLE aqc_schedules (id INTEGER PRIMARY KEY, name TEXT NOT NULL); CREATE INDEX aqc_schedules_name ON aqc_schedules(name);";
    const AQC_2: &str = "CREATE TABLE aqc_items (id INTEGER PRIMARY KEY, project_id INTEGER NOT NULL REFERENCES projects(id), title TEXT NOT NULL);";
    const AQC_TRACK: Track = Track { name: "aqc", migrations: &[AQC_1, AQC_2] };
    const AAD: Track = Track { name: "aad", migrations: &["CREATE TABLE aad_drawings (id INTEGER PRIMARY KEY, project_id INTEGER REFERENCES projects(id));"] };

    #[test]
    fn a_track_applies_once_and_resumes_where_it_stopped() {
        let mut c = core();
        assert_eq!(version(&c, "aqc").unwrap(), 0);
        let first = Track { name: "aqc", migrations: &[AQC_1] };
        migrate(&mut c, &first).unwrap();
        assert_eq!(version(&c, "aqc").unwrap(), 1);
        migrate(&mut c, &AQC_TRACK).unwrap();
        migrate(&mut c, &AQC_TRACK).unwrap();
        assert_eq!(version(&c, "aqc").unwrap(), 2);
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1, 'Villa')", []).unwrap();
        c.execute("INSERT INTO aqc_items (project_id, title) VALUES (1, 'Structure')", []).unwrap();
        assert!(c.execute("INSERT INTO aqc_items (project_id, title) VALUES (99, 'x')", []).is_err(), "a track table can reference projects");
    }

    #[test]
    fn two_apps_keep_separate_versions_and_open_in_any_order() {
        let mut c = core();
        migrate(&mut c, &AAD).unwrap();
        migrate(&mut c, &AQC_TRACK).unwrap();
        assert_eq!((version(&c, "aad").unwrap(), version(&c, "aqc").unwrap()), (1, 2));
        let user_version: i64 = c.pragma_query_value(None, "user_version", |r| r.get(0)).unwrap();
        assert_eq!(user_version as usize, crate::schema::latest_version(), "the core chain is untouched");
    }

    #[test]
    fn a_track_cannot_create_or_change_what_it_does_not_own() {
        let mut c = core();
        let stray = Track { name: "aqc", migrations: &["CREATE TABLE schedules (id INTEGER PRIMARY KEY);"] };
        assert!(migrate(&mut c, &stray).unwrap_err().contains("may only create"));
        let alter = Track { name: "aqc", migrations: &["ALTER TABLE clients ADD COLUMN aqc_note TEXT;"] };
        assert!(migrate(&mut c, &alter).unwrap_err().contains("may not change"));
        let drop = Track { name: "aqc", migrations: &["DROP TABLE time_entries;"] };
        assert!(migrate(&mut c, &drop).is_err());
        assert_eq!(version(&c, "aqc").unwrap(), 0, "a refused migration leaves no trace");
        let tables: i64 = c.query_row("SELECT COUNT(*) FROM sqlite_master WHERE name = 'rate_books' OR name = 'time_entries' AND 0", [], |r| r.get(0)).unwrap();
        assert_eq!(tables, 0);
        let still_there: i64 = c.query_row("SELECT COUNT(*) FROM sqlite_master WHERE name = 'time_entries'", [], |r| r.get(0)).unwrap();
        assert_eq!(still_there, 1);
    }

    #[test]
    fn track_names_are_checked_and_a_newer_file_is_refused() {
        let mut c = core();
        for name in ["core", "", "Aqc", "9aqc", "aqc-x"] {
            assert!(migrate(&mut c, &Track { name, migrations: &[] }).is_err(), "{name}");
        }
        migrate(&mut c, &AQC_TRACK).unwrap();
        let older = Track { name: "aqc", migrations: &[AQC_1] };
        assert!(migrate(&mut c, &older).unwrap_err().contains("newer version"));
    }
}
