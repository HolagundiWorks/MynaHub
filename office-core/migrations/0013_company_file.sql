-- Company file: every document the company owns lives inside the database, so one .adms file is the whole company.
CREATE TABLE IF NOT EXISTS stored_files (
  key TEXT PRIMARY KEY,
  sha256 TEXT NOT NULL,
  bytes INTEGER NOT NULL CHECK (bytes >= 0),
  data BLOB NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE TRIGGER IF NOT EXISTS stored_files_immutable BEFORE UPDATE ON stored_files
BEGIN SELECT RAISE(ABORT, 'stored files are immutable'); END;
CREATE TRIGGER IF NOT EXISTS stored_files_no_delete BEFORE DELETE ON stored_files
BEGIN SELECT RAISE(ABORT, 'stored files cannot be deleted'); END;

-- Identity and format metadata. The path is never the identity; the uuid is.
CREATE TABLE IF NOT EXISTS company_file (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  format TEXT NOT NULL,
  company_uuid TEXT NOT NULL,
  display_name TEXT NOT NULL DEFAULT '',
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  last_opened_at TEXT
);
