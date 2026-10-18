ALTER TABLE company_profile ADD COLUMN logo_key TEXT NOT NULL DEFAULT '';

-- Hours against a project. These lines are a record of time; they do not post a journal.
CREATE TABLE time_entries (
  id INTEGER PRIMARY KEY,
  project_id INTEGER NOT NULL REFERENCES projects(id),
  person TEXT NOT NULL,
  work_date TEXT NOT NULL,
  minutes INTEGER NOT NULL CHECK (minutes > 0 AND minutes <= 1440),
  note TEXT NOT NULL DEFAULT '',
  cancelled INTEGER NOT NULL DEFAULT 0 CHECK (cancelled IN (0, 1)),
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_time_entries_project ON time_entries(project_id, work_date);
