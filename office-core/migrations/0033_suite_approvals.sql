-- Suite approvals: CoreOp (and peers) share estimates / running bills as drafts;
-- Studio alone approves and locks. Corrections are a new version that supersedes the locked one.
CREATE TABLE suite_approvals (
  id INTEGER PRIMARY KEY,
  project_id INTEGER NOT NULL REFERENCES projects(id),
  kind TEXT NOT NULL CHECK (kind IN ('estimate', 'running_bill')),
  title TEXT NOT NULL,
  status TEXT NOT NULL CHECK (status IN ('draft', 'approved_locked', 'superseded')),
  version INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
  body TEXT NOT NULL,
  checksum_sha256 TEXT NOT NULL,
  supersedes_id INTEGER REFERENCES suite_approvals(id),
  submitted_by TEXT NOT NULL,
  submitted_at TEXT NOT NULL DEFAULT (datetime('now')),
  decided_by TEXT,
  decided_at TEXT,
  UNIQUE (project_id, kind, version)
);
CREATE INDEX idx_suite_approvals_status ON suite_approvals(status);
CREATE INDEX idx_suite_approvals_project ON suite_approvals(project_id, kind);
