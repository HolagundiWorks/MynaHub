-- Client onboarding (billing identity and authorised representatives) and the project design brief.
ALTER TABLE clients ADD COLUMN billing_address TEXT NOT NULL DEFAULT '';
ALTER TABLE clients ADD COLUMN communication_preference TEXT NOT NULL DEFAULT '';

CREATE TABLE client_representatives (
  id INTEGER PRIMARY KEY,
  client_id INTEGER NOT NULL REFERENCES clients(id),
  name TEXT NOT NULL,
  designation TEXT NOT NULL DEFAULT '',
  phone TEXT NOT NULL DEFAULT '',
  position INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_client_reps ON client_representatives(client_id, position);

CREATE TABLE project_briefs (
  project_id INTEGER PRIMARY KEY REFERENCES projects(id),
  body TEXT NOT NULL DEFAULT '{}',
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
