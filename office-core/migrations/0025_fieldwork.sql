-- Site records, statutory permits and enquiries. All are plain project or practice records; none posts to the books.
CREATE TABLE site_records (
  id INTEGER PRIMARY KEY,
  project_id INTEGER NOT NULL REFERENCES projects(id),
  kind TEXT NOT NULL CHECK (kind IN ('instruction', 'inspection', 'snag')),
  record_date TEXT NOT NULL,
  title TEXT NOT NULL,
  details TEXT NOT NULL DEFAULT '',
  location TEXT NOT NULL DEFAULT '',
  assigned_to TEXT NOT NULL DEFAULT '',
  due_date TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'resolved', 'closed')),
  resolved_date TEXT NOT NULL DEFAULT '',
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_site_records_project ON site_records(project_id, kind, status);

CREATE TABLE permits (
  id INTEGER PRIMARY KEY,
  project_id INTEGER NOT NULL REFERENCES projects(id),
  authority TEXT NOT NULL,
  kind TEXT NOT NULL,
  reference TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL DEFAULT 'planned' CHECK (status IN ('planned', 'applied', 'queried', 'approved', 'rejected')),
  applied_date TEXT NOT NULL DEFAULT '',
  decision_date TEXT NOT NULL DEFAULT '',
  valid_until TEXT NOT NULL DEFAULT '',
  notes TEXT NOT NULL DEFAULT '',
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_permits_project ON permits(project_id, status);

CREATE TABLE enquiries (
  id INTEGER PRIMARY KEY,
  received_date TEXT NOT NULL,
  name TEXT NOT NULL,
  contact TEXT NOT NULL DEFAULT '',
  source TEXT NOT NULL DEFAULT '',
  brief TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL DEFAULT 'new' CHECK (status IN ('new', 'meeting', 'proposal', 'won', 'lost')),
  follow_up TEXT NOT NULL DEFAULT '',
  notes TEXT NOT NULL DEFAULT '',
  client_id INTEGER REFERENCES clients(id),
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_enquiries_status ON enquiries(status, follow_up);
