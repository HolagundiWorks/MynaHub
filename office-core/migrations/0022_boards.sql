-- Project reports and presentations submitted from the practice. They are records of what was
-- shown, not journals and not issued letters.
CREATE TABLE boards (
  id INTEGER PRIMARY KEY,
  project_id INTEGER NOT NULL REFERENCES projects(id),
  kind TEXT NOT NULL CHECK (kind IN ('report', 'presentation')),
  title TEXT NOT NULL,
  body TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'submitted')),
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at TEXT NOT NULL DEFAULT (datetime('now')),
  submitted_at TEXT
);
CREATE INDEX idx_boards_project ON boards(project_id, id);

CREATE TABLE board_slides (
  id INTEGER PRIMARY KEY,
  board_id INTEGER NOT NULL REFERENCES boards(id) ON DELETE CASCADE,
  position INTEGER NOT NULL,
  title TEXT NOT NULL DEFAULT '',
  body TEXT NOT NULL DEFAULT '',
  UNIQUE (board_id, position)
);

CREATE TABLE board_marks (
  id INTEGER PRIMARY KEY,
  slide_id INTEGER NOT NULL REFERENCES board_slides(id) ON DELETE CASCADE,
  kind TEXT NOT NULL CHECK (kind IN ('line', 'arrow', 'pointer')),
  x1 REAL NOT NULL,
  y1 REAL NOT NULL,
  x2 REAL NOT NULL,
  y2 REAL NOT NULL,
  label TEXT NOT NULL DEFAULT ''
);

CREATE TABLE board_figures (
  id INTEGER PRIMARY KEY,
  board_id INTEGER NOT NULL REFERENCES boards(id) ON DELETE CASCADE,
  slide_id INTEGER REFERENCES board_slides(id) ON DELETE CASCADE,
  file_key TEXT NOT NULL,
  x REAL NOT NULL DEFAULT 0,
  y REAL NOT NULL DEFAULT 0,
  w REAL NOT NULL DEFAULT 0.32,
  h REAL NOT NULL DEFAULT 0.32,
  caption TEXT NOT NULL DEFAULT ''
);

CREATE TABLE board_annexures (
  id INTEGER PRIMARY KEY,
  board_id INTEGER NOT NULL REFERENCES boards(id) ON DELETE CASCADE,
  filename TEXT NOT NULL,
  file_key TEXT NOT NULL,
  bytes INTEGER NOT NULL
);

CREATE TABLE materials (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  category TEXT NOT NULL DEFAULT '',
  file_key TEXT NOT NULL,
  bytes INTEGER NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
