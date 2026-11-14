-- Cloud sync, wave 1 (docs/hub-sync-design.md section 4): clients, projects, project stages and tasks.
-- Additive only: three new tables and triggers. No existing table or column changes. Nothing is recorded until sync is enabled.
CREATE TABLE sync_map (
  kind TEXT NOT NULL,                 -- client | project | project_stage | task
  local_id INTEGER NOT NULL,
  sync_id TEXT NOT NULL UNIQUE,       -- the global id (UUID) the cloud and the phone use
  cloud_rev INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (kind, local_id)
);

-- Rows changed here and not yet acknowledged by the cloud. v grows with every change so an acknowledgement never clears a newer edit.
CREATE TABLE sync_dirty (
  kind TEXT NOT NULL,
  local_id INTEGER NOT NULL,
  deleted INTEGER NOT NULL DEFAULT 0,
  v INTEGER NOT NULL,
  changed_ms INTEGER NOT NULL,
  PRIMARY KEY (kind, local_id)
);

CREATE TABLE sync_state (
  key TEXT PRIMARY KEY,               -- enabled, applying, cloud_company, last_pulled_seq
  value TEXT NOT NULL
);

-- clients
CREATE TRIGGER sync_clients_ai AFTER INSERT ON clients
WHEN (SELECT value FROM sync_state WHERE key = 'enabled') = '1' AND IFNULL((SELECT value FROM sync_state WHERE key = 'applying'), '0') = '0'
BEGIN
  INSERT INTO sync_dirty (kind, local_id, deleted, v, changed_ms) VALUES ('client', NEW.id, 0, (SELECT IFNULL(MAX(v), 0) + 1 FROM sync_dirty), CAST(strftime('%s', 'now') AS INTEGER) * 1000)
  ON CONFLICT (kind, local_id) DO UPDATE SET deleted = 0, v = excluded.v, changed_ms = excluded.changed_ms;
END;
CREATE TRIGGER sync_clients_au AFTER UPDATE ON clients
WHEN (SELECT value FROM sync_state WHERE key = 'enabled') = '1' AND IFNULL((SELECT value FROM sync_state WHERE key = 'applying'), '0') = '0'
BEGIN
  INSERT INTO sync_dirty (kind, local_id, deleted, v, changed_ms) VALUES ('client', NEW.id, 0, (SELECT IFNULL(MAX(v), 0) + 1 FROM sync_dirty), CAST(strftime('%s', 'now') AS INTEGER) * 1000)
  ON CONFLICT (kind, local_id) DO UPDATE SET deleted = 0, v = excluded.v, changed_ms = excluded.changed_ms;
END;
CREATE TRIGGER sync_clients_ad AFTER DELETE ON clients
WHEN (SELECT value FROM sync_state WHERE key = 'enabled') = '1' AND IFNULL((SELECT value FROM sync_state WHERE key = 'applying'), '0') = '0'
BEGIN
  INSERT INTO sync_dirty (kind, local_id, deleted, v, changed_ms) VALUES ('client', OLD.id, 1, (SELECT IFNULL(MAX(v), 0) + 1 FROM sync_dirty), CAST(strftime('%s', 'now') AS INTEGER) * 1000)
  ON CONFLICT (kind, local_id) DO UPDATE SET deleted = 1, v = excluded.v, changed_ms = excluded.changed_ms;
END;

-- projects
CREATE TRIGGER sync_projects_ai AFTER INSERT ON projects
WHEN (SELECT value FROM sync_state WHERE key = 'enabled') = '1' AND IFNULL((SELECT value FROM sync_state WHERE key = 'applying'), '0') = '0'
BEGIN
  INSERT INTO sync_dirty (kind, local_id, deleted, v, changed_ms) VALUES ('project', NEW.id, 0, (SELECT IFNULL(MAX(v), 0) + 1 FROM sync_dirty), CAST(strftime('%s', 'now') AS INTEGER) * 1000)
  ON CONFLICT (kind, local_id) DO UPDATE SET deleted = 0, v = excluded.v, changed_ms = excluded.changed_ms;
END;
CREATE TRIGGER sync_projects_au AFTER UPDATE ON projects
WHEN (SELECT value FROM sync_state WHERE key = 'enabled') = '1' AND IFNULL((SELECT value FROM sync_state WHERE key = 'applying'), '0') = '0'
BEGIN
  INSERT INTO sync_dirty (kind, local_id, deleted, v, changed_ms) VALUES ('project', NEW.id, 0, (SELECT IFNULL(MAX(v), 0) + 1 FROM sync_dirty), CAST(strftime('%s', 'now') AS INTEGER) * 1000)
  ON CONFLICT (kind, local_id) DO UPDATE SET deleted = 0, v = excluded.v, changed_ms = excluded.changed_ms;
END;
CREATE TRIGGER sync_projects_ad AFTER DELETE ON projects
WHEN (SELECT value FROM sync_state WHERE key = 'enabled') = '1' AND IFNULL((SELECT value FROM sync_state WHERE key = 'applying'), '0') = '0'
BEGIN
  INSERT INTO sync_dirty (kind, local_id, deleted, v, changed_ms) VALUES ('project', OLD.id, 1, (SELECT IFNULL(MAX(v), 0) + 1 FROM sync_dirty), CAST(strftime('%s', 'now') AS INTEGER) * 1000)
  ON CONFLICT (kind, local_id) DO UPDATE SET deleted = 1, v = excluded.v, changed_ms = excluded.changed_ms;
END;

-- project stages
CREATE TRIGGER sync_stages_ai AFTER INSERT ON project_stages
WHEN (SELECT value FROM sync_state WHERE key = 'enabled') = '1' AND IFNULL((SELECT value FROM sync_state WHERE key = 'applying'), '0') = '0'
BEGIN
  INSERT INTO sync_dirty (kind, local_id, deleted, v, changed_ms) VALUES ('project_stage', NEW.id, 0, (SELECT IFNULL(MAX(v), 0) + 1 FROM sync_dirty), CAST(strftime('%s', 'now') AS INTEGER) * 1000)
  ON CONFLICT (kind, local_id) DO UPDATE SET deleted = 0, v = excluded.v, changed_ms = excluded.changed_ms;
END;
CREATE TRIGGER sync_stages_au AFTER UPDATE ON project_stages
WHEN (SELECT value FROM sync_state WHERE key = 'enabled') = '1' AND IFNULL((SELECT value FROM sync_state WHERE key = 'applying'), '0') = '0'
BEGIN
  INSERT INTO sync_dirty (kind, local_id, deleted, v, changed_ms) VALUES ('project_stage', NEW.id, 0, (SELECT IFNULL(MAX(v), 0) + 1 FROM sync_dirty), CAST(strftime('%s', 'now') AS INTEGER) * 1000)
  ON CONFLICT (kind, local_id) DO UPDATE SET deleted = 0, v = excluded.v, changed_ms = excluded.changed_ms;
END;
CREATE TRIGGER sync_stages_ad AFTER DELETE ON project_stages
WHEN (SELECT value FROM sync_state WHERE key = 'enabled') = '1' AND IFNULL((SELECT value FROM sync_state WHERE key = 'applying'), '0') = '0'
BEGIN
  INSERT INTO sync_dirty (kind, local_id, deleted, v, changed_ms) VALUES ('project_stage', OLD.id, 1, (SELECT IFNULL(MAX(v), 0) + 1 FROM sync_dirty), CAST(strftime('%s', 'now') AS INTEGER) * 1000)
  ON CONFLICT (kind, local_id) DO UPDATE SET deleted = 1, v = excluded.v, changed_ms = excluded.changed_ms;
END;

-- tasks
CREATE TRIGGER sync_tasks_ai AFTER INSERT ON tasks
WHEN (SELECT value FROM sync_state WHERE key = 'enabled') = '1' AND IFNULL((SELECT value FROM sync_state WHERE key = 'applying'), '0') = '0'
BEGIN
  INSERT INTO sync_dirty (kind, local_id, deleted, v, changed_ms) VALUES ('task', NEW.id, 0, (SELECT IFNULL(MAX(v), 0) + 1 FROM sync_dirty), CAST(strftime('%s', 'now') AS INTEGER) * 1000)
  ON CONFLICT (kind, local_id) DO UPDATE SET deleted = 0, v = excluded.v, changed_ms = excluded.changed_ms;
END;
CREATE TRIGGER sync_tasks_au AFTER UPDATE ON tasks
WHEN (SELECT value FROM sync_state WHERE key = 'enabled') = '1' AND IFNULL((SELECT value FROM sync_state WHERE key = 'applying'), '0') = '0'
BEGIN
  INSERT INTO sync_dirty (kind, local_id, deleted, v, changed_ms) VALUES ('task', NEW.id, 0, (SELECT IFNULL(MAX(v), 0) + 1 FROM sync_dirty), CAST(strftime('%s', 'now') AS INTEGER) * 1000)
  ON CONFLICT (kind, local_id) DO UPDATE SET deleted = 0, v = excluded.v, changed_ms = excluded.changed_ms;
END;
CREATE TRIGGER sync_tasks_ad AFTER DELETE ON tasks
WHEN (SELECT value FROM sync_state WHERE key = 'enabled') = '1' AND IFNULL((SELECT value FROM sync_state WHERE key = 'applying'), '0') = '0'
BEGIN
  INSERT INTO sync_dirty (kind, local_id, deleted, v, changed_ms) VALUES ('task', OLD.id, 1, (SELECT IFNULL(MAX(v), 0) + 1 FROM sync_dirty), CAST(strftime('%s', 'now') AS INTEGER) * 1000)
  ON CONFLICT (kind, local_id) DO UPDATE SET deleted = 1, v = excluded.v, changed_ms = excluded.changed_ms;
END;
