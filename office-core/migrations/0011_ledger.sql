-- Double-entry general ledger. Posted journals are immutable; corrections are reversals.
CREATE TABLE accounts (
  id INTEGER PRIMARY KEY,
  code TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  type TEXT NOT NULL CHECK (type IN ('asset','liability','equity','income','expense')),
  is_system INTEGER NOT NULL DEFAULT 0,
  active INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

INSERT INTO accounts (code, name, type, is_system) VALUES
  ('1000', 'Cash in hand', 'asset', 1),
  ('1010', 'Bank (unassigned)', 'asset', 1),
  ('1200', 'Accounts receivable', 'asset', 1),
  ('1210', 'TDS receivable (deducted by clients)', 'asset', 1),
  ('1220', 'GST input credit', 'asset', 1),
  ('1300', 'Fixed assets (at cost)', 'asset', 1),
  ('1310', 'Accumulated depreciation', 'asset', 1),
  ('2000', 'Accounts payable', 'liability', 1),
  ('2100', 'GST output - CGST', 'liability', 1),
  ('2110', 'GST output - SGST', 'liability', 1),
  ('2120', 'GST output - IGST', 'liability', 1),
  ('2130', 'GST composition tax payable', 'liability', 1),
  ('2200', 'TDS payable', 'liability', 1),
  ('2300', 'Salaries payable', 'liability', 1),
  ('2400', 'Advances from clients', 'liability', 1),
  ('2500', 'Income tax payable', 'liability', 1),
  ('3000', 'Owner capital', 'equity', 1),
  ('3100', 'Retained earnings', 'equity', 1),
  ('4000', 'Professional fees', 'income', 1),
  ('4100', 'Rental income - commercial', 'income', 1),
  ('4110', 'Rental income - residential', 'income', 1),
  ('4200', 'Bank interest income', 'income', 1),
  ('4900', 'Other income', 'income', 1),
  ('5000', 'Salaries and wages', 'expense', 1),
  ('5100', 'Office rent', 'expense', 1),
  ('5200', 'Consultant and professional charges', 'expense', 1),
  ('5300', 'Travel and conveyance', 'expense', 1),
  ('5400', 'Printing and stationery', 'expense', 1),
  ('5500', 'Utilities and internet', 'expense', 1),
  ('5600', 'Software and subscriptions', 'expense', 1),
  ('5700', 'Depreciation', 'expense', 1),
  ('5800', 'Bank charges', 'expense', 1),
  ('5900', 'Income tax expense', 'expense', 1),
  ('5950', 'GST composition tax expense', 'expense', 1),
  ('5990', 'Other expenses', 'expense', 1);

ALTER TABLE bank_accounts ADD COLUMN ledger_account_id INTEGER REFERENCES accounts(id);
-- Each existing bank account gets its own ledger account.
INSERT INTO accounts (code, name, type) SELECT 'BANK-' || id, 'Bank - ' || name, 'asset' FROM bank_accounts;
UPDATE bank_accounts SET ledger_account_id = (SELECT a.id FROM accounts a WHERE a.code = 'BANK-' || bank_accounts.id);

ALTER TABLE receipts ADD COLUMN bank_account_id INTEGER REFERENCES bank_accounts(id);

CREATE TABLE accounting_periods (
  id INTEGER PRIMARY KEY,
  fy TEXT NOT NULL UNIQUE,
  start_date TEXT NOT NULL,
  end_date TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open','closed')),
  closed_at TEXT,
  closed_by INTEGER REFERENCES users(id)
);

CREATE TABLE journals (
  id INTEGER PRIMARY KEY,
  number TEXT NOT NULL UNIQUE,
  date TEXT NOT NULL,
  narration TEXT NOT NULL DEFAULT '',
  source_type TEXT NOT NULL,
  source_id INTEGER,
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_journals_date ON journals(date);
-- A business event can be posted only once (manual journals have no source id).
CREATE UNIQUE INDEX idx_journals_source ON journals(source_type, source_id) WHERE source_id IS NOT NULL;

CREATE TABLE journal_lines (
  id INTEGER PRIMARY KEY,
  journal_id INTEGER NOT NULL REFERENCES journals(id),
  account_id INTEGER NOT NULL REFERENCES accounts(id),
  debit_paise INTEGER NOT NULL DEFAULT 0 CHECK (debit_paise >= 0),
  credit_paise INTEGER NOT NULL DEFAULT 0 CHECK (credit_paise >= 0),
  memo TEXT NOT NULL DEFAULT '',
  CHECK ((debit_paise > 0) <> (credit_paise > 0))
);
CREATE INDEX idx_journal_lines_journal ON journal_lines(journal_id);
CREATE INDEX idx_journal_lines_account ON journal_lines(account_id);

CREATE TRIGGER journals_immutable BEFORE UPDATE ON journals
BEGIN SELECT RAISE(ABORT, 'posted journals cannot be modified'); END;
CREATE TRIGGER journals_no_delete BEFORE DELETE ON journals
BEGIN SELECT RAISE(ABORT, 'posted journals cannot be deleted'); END;
CREATE TRIGGER journal_lines_immutable BEFORE UPDATE ON journal_lines
BEGIN SELECT RAISE(ABORT, 'posted journal lines cannot be modified'); END;
CREATE TRIGGER journal_lines_no_delete BEFORE DELETE ON journal_lines
BEGIN SELECT RAISE(ABORT, 'posted journal lines cannot be deleted'); END;
-- Closed periods reject postings.
CREATE TRIGGER journals_closed_period BEFORE INSERT ON journals
WHEN EXISTS (SELECT 1 FROM accounting_periods p WHERE p.status = 'closed' AND NEW.date BETWEEN p.start_date AND p.end_date)
BEGIN SELECT RAISE(ABORT, 'the accounting period is closed'); END;
-- Accounts are never deleted (only deactivated).
CREATE TRIGGER accounts_system_protected BEFORE DELETE ON accounts BEGIN SELECT RAISE(ABORT, 'accounts cannot be deleted'); END;

CREATE TABLE vendors (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL UNIQUE COLLATE NOCASE,
  pan TEXT NOT NULL DEFAULT '',
  active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE properties (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL UNIQUE COLLATE NOCASE,
  kind TEXT NOT NULL CHECK (kind IN ('commercial','residential')),
  address TEXT NOT NULL DEFAULT '',
  active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE expenses (
  id INTEGER PRIMARY KEY,
  date TEXT NOT NULL,
  vendor_id INTEGER REFERENCES vendors(id),
  bill_ref TEXT NOT NULL DEFAULT '',
  description TEXT NOT NULL,
  expense_account_id INTEGER NOT NULL REFERENCES accounts(id),
  amount_paise INTEGER NOT NULL CHECK (amount_paise > 0),
  gst_paise INTEGER NOT NULL DEFAULT 0 CHECK (gst_paise >= 0),
  tds_paise INTEGER NOT NULL DEFAULT 0 CHECK (tds_paise >= 0),
  paid_via TEXT NOT NULL,
  journal_id INTEGER NOT NULL REFERENCES journals(id),
  settled_journal_id INTEGER REFERENCES journals(id),
  voided_at TEXT,
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE income_entries (
  id INTEGER PRIMARY KEY,
  date TEXT NOT NULL,
  kind TEXT NOT NULL CHECK (kind IN ('rent','interest','other')),
  property_id INTEGER REFERENCES properties(id),
  payer TEXT NOT NULL DEFAULT '',
  description TEXT NOT NULL DEFAULT '',
  income_account_id INTEGER NOT NULL REFERENCES accounts(id),
  amount_paise INTEGER NOT NULL CHECK (amount_paise > 0),
  gst_paise INTEGER NOT NULL DEFAULT 0 CHECK (gst_paise >= 0),
  tds_paise INTEGER NOT NULL DEFAULT 0 CHECK (tds_paise >= 0),
  received_via TEXT NOT NULL,
  journal_id INTEGER NOT NULL REFERENCES journals(id),
  voided_at TEXT,
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE payroll_entries (
  id INTEGER PRIMARY KEY,
  date TEXT NOT NULL,
  employee TEXT NOT NULL,
  period_label TEXT NOT NULL DEFAULT '',
  gross_paise INTEGER NOT NULL CHECK (gross_paise > 0),
  tds_paise INTEGER NOT NULL DEFAULT 0 CHECK (tds_paise >= 0),
  paid_via TEXT NOT NULL,
  journal_id INTEGER NOT NULL REFERENCES journals(id),
  voided_at TEXT,
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE fixed_assets (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  purchase_date TEXT NOT NULL,
  cost_paise INTEGER NOT NULL CHECK (cost_paise > 0),
  dep_rate_bp INTEGER NOT NULL CHECK (dep_rate_bp >= 0 AND dep_rate_bp <= 10000),
  journal_id INTEGER NOT NULL REFERENCES journals(id),
  active INTEGER NOT NULL DEFAULT 1,
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE asset_depreciation (
  asset_id INTEGER NOT NULL REFERENCES fixed_assets(id),
  fy TEXT NOT NULL,
  amount_paise INTEGER NOT NULL,
  journal_id INTEGER NOT NULL REFERENCES journals(id),
  PRIMARY KEY (asset_id, fy)
);
