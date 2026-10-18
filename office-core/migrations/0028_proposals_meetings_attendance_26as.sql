-- Fee proposals, client meetings and decisions, staff attendance, and the TDS entries a client's deductor reported (Form 26AS).
-- Proposals and meetings are records; none of these post to the books.
CREATE TABLE fee_proposals (
  id INTEGER PRIMARY KEY,
  number TEXT NOT NULL DEFAULT '',
  client_id INTEGER NOT NULL REFERENCES clients(id),
  project_id INTEGER REFERENCES projects(id),
  title TEXT NOT NULL,
  basis TEXT NOT NULL CHECK (basis IN ('percent', 'per_area', 'lump_sum')),
  cost_paise INTEGER NOT NULL DEFAULT 0 CHECK (cost_paise >= 0),
  rate_bp INTEGER NOT NULL DEFAULT 0 CHECK (rate_bp BETWEEN 0 AND 10000),
  area_milli INTEGER NOT NULL DEFAULT 0 CHECK (area_milli >= 0),
  area_rate_paise INTEGER NOT NULL DEFAULT 0 CHECK (area_rate_paise >= 0),
  fee_paise INTEGER NOT NULL CHECK (fee_paise > 0),
  gst_bp INTEGER NOT NULL DEFAULT 0 CHECK (gst_bp BETWEEN 0 AND 2800),
  status TEXT NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'sent', 'accepted', 'declined')),
  issued_date TEXT NOT NULL,
  valid_until TEXT NOT NULL DEFAULT '',
  notes TEXT NOT NULL DEFAULT '',
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_fee_proposals_client ON fee_proposals(client_id, status);

CREATE TABLE meetings (
  id INTEGER PRIMARY KEY,
  meeting_date TEXT NOT NULL,
  title TEXT NOT NULL,
  client_id INTEGER REFERENCES clients(id),
  project_id INTEGER REFERENCES projects(id),
  attendees TEXT NOT NULL DEFAULT '',
  notes TEXT NOT NULL DEFAULT '',
  decisions TEXT NOT NULL DEFAULT '',
  follow_up_date TEXT NOT NULL DEFAULT '',
  follow_up_done INTEGER NOT NULL DEFAULT 0 CHECK (follow_up_done IN (0, 1)),
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_meetings_date ON meetings(meeting_date);

CREATE TABLE attendance (
  staff_id INTEGER NOT NULL REFERENCES staff(id),
  day TEXT NOT NULL,
  status TEXT NOT NULL CHECK (status IN ('present', 'half', 'absent', 'leave', 'holiday')),
  note TEXT NOT NULL DEFAULT '',
  marked_by INTEGER REFERENCES users(id),
  PRIMARY KEY (staff_id, day)
);

CREATE TABLE tds_26as (
  id INTEGER PRIMARY KEY,
  deductor TEXT NOT NULL,
  tan TEXT NOT NULL,
  section TEXT NOT NULL DEFAULT '',
  txn_date TEXT NOT NULL,
  amount_paise INTEGER NOT NULL CHECK (amount_paise >= 0),
  tds_paise INTEGER NOT NULL CHECK (tds_paise > 0),
  imported_by INTEGER REFERENCES users(id),
  imported_at TEXT NOT NULL DEFAULT (datetime('now')),
  UNIQUE (tan, txn_date, amount_paise, tds_paise)
);
CREATE INDEX idx_tds_26as_date ON tds_26as(txn_date);
