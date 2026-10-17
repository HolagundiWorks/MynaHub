-- Client tax identity, a write-off of an uncollected bill, and a party tag on new journal lines.
-- Accounts receivable stays the control account. Each client and project has its own ledger in the reports.

ALTER TABLE clients ADD COLUMN kind TEXT NOT NULL DEFAULT 'individual'
  CHECK (kind IN ('individual','company','architect_firm'));
ALTER TABLE clients ADD COLUMN city TEXT NOT NULL DEFAULT '';
ALTER TABLE clients ADD COLUMN state TEXT NOT NULL DEFAULT '';
ALTER TABLE clients ADD COLUMN legal_name TEXT NOT NULL DEFAULT '';
ALTER TABLE clients ADD COLUMN tan TEXT NOT NULL DEFAULT '';
ALTER TABLE clients ADD COLUMN gst_registration TEXT NOT NULL DEFAULT ''
  CHECK (gst_registration IN ('','regular','composition','unregistered','sez'));

ALTER TABLE journal_lines ADD COLUMN client_id INTEGER REFERENCES clients(id);
ALTER TABLE journal_lines ADD COLUMN project_id INTEGER REFERENCES projects(id);

INSERT INTO accounts (code, name, type, is_system) VALUES
  ('5960', 'Bad debts written off', 'expense', 1);

CREATE TABLE invoice_writeoffs (
  id INTEGER PRIMARY KEY,
  invoice_id INTEGER NOT NULL REFERENCES invoices(id),
  writeoff_date TEXT NOT NULL,
  amount_paise INTEGER NOT NULL CHECK (amount_paise > 0),
  reason TEXT NOT NULL,
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_writeoffs_invoice ON invoice_writeoffs(invoice_id);
CREATE TRIGGER invoice_writeoffs_immutable BEFORE UPDATE ON invoice_writeoffs
BEGIN SELECT RAISE(ABORT, 'write-offs are immutable'); END;
CREATE TRIGGER invoice_writeoffs_no_delete BEFORE DELETE ON invoice_writeoffs
BEGIN SELECT RAISE(ABORT, 'write-offs are immutable'); END;

CREATE TABLE writeoff_reversals (
  writeoff_id INTEGER PRIMARY KEY REFERENCES invoice_writeoffs(id),
  reason TEXT NOT NULL,
  reversed_by INTEGER REFERENCES users(id),
  reversed_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE TRIGGER writeoff_reversals_immutable BEFORE UPDATE ON writeoff_reversals
BEGIN SELECT RAISE(ABORT, 'reversals are immutable'); END;
CREATE TRIGGER writeoff_reversals_no_delete BEFORE DELETE ON writeoff_reversals
BEGIN SELECT RAISE(ABORT, 'reversals are immutable'); END;

DROP VIEW invoice_balances;
CREATE VIEW invoice_balances AS
  SELECT i.id AS invoice_id, i.total_paise,
    COALESCE((SELECT SUM(amount_paise) FROM effective_receipts r WHERE r.invoice_id = i.id), 0) AS received_paise,
    COALESCE((SELECT SUM(tds_paise) FROM effective_receipts r WHERE r.invoice_id = i.id), 0) AS tds_paise,
    COALESCE((SELECT SUM(total_paise) FROM credit_notes c WHERE c.invoice_id = i.id), 0) AS credited_paise,
    COALESCE((SELECT SUM(w.amount_paise) FROM invoice_writeoffs w WHERE w.invoice_id = i.id AND w.id NOT IN (SELECT writeoff_id FROM writeoff_reversals)), 0) AS written_off_paise
  FROM invoices i;
