-- A document an app keeps for a project in the shared company file, with a revision so two apps cannot overwrite each other.
-- O-CoreOp keeps a project's quantities and estimates here (kind 'aqc.project'). The last revisions are kept in the history table.
CREATE TABLE project_documents (
  project_id INTEGER NOT NULL REFERENCES projects(id),
  kind TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK (revision >= 1),
  body TEXT NOT NULL,
  checksum_sha256 TEXT NOT NULL,
  updated_by TEXT NOT NULL DEFAULT '',
  updated_at TEXT NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (project_id, kind)
);
CREATE TABLE project_document_history (
  project_id INTEGER NOT NULL,
  kind TEXT NOT NULL,
  revision INTEGER NOT NULL,
  body TEXT NOT NULL,
  checksum_sha256 TEXT NOT NULL,
  updated_by TEXT NOT NULL DEFAULT '',
  updated_at TEXT NOT NULL,
  PRIMARY KEY (project_id, kind, revision)
);
