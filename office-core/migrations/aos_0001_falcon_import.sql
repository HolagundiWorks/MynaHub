-- MynaOps CSV import (track "aos"). Estimate and bill versions live in suite_approvals; these tables hold what the import needs
-- around them: which MynaOps project is which MynaHub project, the MynaOps item code mapping, the imported rows (the idempotency
-- ledger and the priced take-off basis) and the import log.
CREATE TABLE aos_falcon_link (
  falcon_project TEXT PRIMARY KEY,
  project_id INTEGER NOT NULL REFERENCES projects(id),
  linked_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE TABLE aos_falcon_itemmap (
  falcon_code TEXT PRIMARY KEY,
  kivo_item TEXT NOT NULL CHECK (length(kivo_item) > 0),
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE TABLE aos_falcon_version (
  approval_id INTEGER PRIMARY KEY REFERENCES suite_approvals(id) ON DELETE CASCADE,
  falcon_project TEXT NOT NULL,
  drawing_revision INTEGER NOT NULL
);
CREATE TABLE aos_falcon_bill (
  approval_id INTEGER PRIMARY KEY REFERENCES suite_approvals(id) ON DELETE CASCADE,
  falcon_project TEXT NOT NULL,
  drawing_revision INTEGER NOT NULL
);
CREATE TABLE aos_falcon_row (
  id INTEGER PRIMARY KEY,
  approval_id INTEGER NOT NULL REFERENCES suite_approvals(id) ON DELETE CASCADE,
  row_type TEXT NOT NULL CHECK (row_type IN ('estimate', 'measured', 'takeoff')),
  key TEXT NOT NULL,
  hash TEXT NOT NULL,
  payload TEXT NOT NULL,
  UNIQUE (approval_id, row_type, key)
);
CREATE INDEX aos_falcon_row_key ON aos_falcon_row(row_type, key);
CREATE TABLE aos_falcon_import (
  id INTEGER PRIMARY KEY,
  at TEXT NOT NULL DEFAULT (datetime('now')),
  user_id INTEGER REFERENCES users(id),
  project_id INTEGER REFERENCES projects(id),
  file_name TEXT NOT NULL DEFAULT '',
  file_sha256 TEXT NOT NULL,
  drawing_revision INTEGER,
  added INTEGER NOT NULL DEFAULT 0,
  changed INTEGER NOT NULL DEFAULT 0,
  unchanged INTEGER NOT NULL DEFAULT 0,
  refused INTEGER NOT NULL DEFAULT 0,
  unmapped TEXT NOT NULL DEFAULT ''
);
