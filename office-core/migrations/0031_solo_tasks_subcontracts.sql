-- Solo / freelance practice: shared project summary, Kanban tasks, consultancy subcontracts.
-- Staff payroll stays in the file for older companies but the solo shell hides those pages.

ALTER TABLE projects ADD COLUMN summary TEXT NOT NULL DEFAULT '';

CREATE TABLE tasks (
  id INTEGER PRIMARY KEY,
  project_id INTEGER REFERENCES projects(id) ON DELETE SET NULL,
  title TEXT NOT NULL COLLATE NOCASE,
  notes TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL DEFAULT 'todo' CHECK (status IN ('todo','doing','done')),
  position INTEGER NOT NULL DEFAULT 0,
  due_date TEXT NOT NULL DEFAULT '',
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_tasks_board ON tasks(status, position, id);
CREATE INDEX idx_tasks_project ON tasks(project_id);

CREATE TABLE subcontracts (
  id INTEGER PRIMARY KEY,
  project_id INTEGER NOT NULL REFERENCES projects(id),
  consultant TEXT NOT NULL COLLATE NOCASE,
  work TEXT NOT NULL DEFAULT '',
  amount_paise INTEGER NOT NULL DEFAULT 0 CHECK (amount_paise >= 0),
  status TEXT NOT NULL DEFAULT 'quoted' CHECK (status IN ('quoted','agreed','paid','cancelled')),
  due_date TEXT NOT NULL DEFAULT '',
  paid_date TEXT NOT NULL DEFAULT '',
  notes TEXT NOT NULL DEFAULT '',
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_subcontracts_project ON subcontracts(project_id, status);
