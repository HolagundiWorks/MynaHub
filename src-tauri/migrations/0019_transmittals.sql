-- A transmittal is a project record of what was sent. It is not a journal and it does not replace an issued letter.
CREATE TABLE transmittals (
  id INTEGER PRIMARY KEY,
  project_id INTEGER NOT NULL REFERENCES projects(id),
  sent_date TEXT NOT NULL,
  recipient TEXT NOT NULL,
  purpose TEXT NOT NULL DEFAULT '',
  medium TEXT NOT NULL DEFAULT '',
  reference TEXT NOT NULL DEFAULT '',
  acknowledged INTEGER NOT NULL DEFAULT 0 CHECK (acknowledged IN (0, 1)),
  cancelled INTEGER NOT NULL DEFAULT 0 CHECK (cancelled IN (0, 1)),
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_transmittals_project ON transmittals(project_id, sent_date);

CREATE TABLE transmittal_items (
  id INTEGER PRIMARY KEY,
  transmittal_id INTEGER NOT NULL REFERENCES transmittals(id),
  position INTEGER NOT NULL,
  drawing_number TEXT NOT NULL DEFAULT '',
  title TEXT NOT NULL,
  revision TEXT NOT NULL DEFAULT '',
  copies INTEGER NOT NULL DEFAULT 1 CHECK (copies > 0 AND copies <= 999),
  UNIQUE (transmittal_id, position)
);
CREATE INDEX idx_transmittal_items ON transmittal_items(transmittal_id, position);
