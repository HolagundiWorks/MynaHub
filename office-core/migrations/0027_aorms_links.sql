-- A project pulled from the AORMS portal remembers which portal project it came from, so a later pull updates it instead of adding another.
CREATE TABLE aorms_links (
  project_id INTEGER PRIMARY KEY REFERENCES projects(id),
  project_office_id TEXT NOT NULL UNIQUE,
  pulled_at TEXT NOT NULL DEFAULT (datetime('now'))
);
