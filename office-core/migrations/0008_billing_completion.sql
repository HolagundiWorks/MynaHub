-- Reversals: receipts stay immutable; a reversal marks one as no longer effective.
CREATE TABLE receipt_reversals (
  receipt_id INTEGER PRIMARY KEY REFERENCES receipts(id),
  reason TEXT NOT NULL,
  reversed_by INTEGER REFERENCES users(id),
  reversed_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE TRIGGER receipt_reversals_immutable BEFORE UPDATE ON receipt_reversals
BEGIN SELECT RAISE(ABORT, 'reversals are immutable'); END;
CREATE TRIGGER receipt_reversals_no_delete BEFORE DELETE ON receipt_reversals
BEGIN SELECT RAISE(ABORT, 'reversals are immutable'); END;

CREATE VIEW effective_receipts AS
  SELECT * FROM receipts WHERE id NOT IN (SELECT receipt_id FROM receipt_reversals);

-- 'allocation' receipts were created by a bank match; 'link' receipts existed before and were only tied to it.
ALTER TABLE bank_txn_receipts ADD COLUMN origin TEXT NOT NULL DEFAULT 'allocation';

ALTER TABLE company_profile ADD COLUMN upi_id TEXT NOT NULL DEFAULT '';
ALTER TABLE invoices ADD COLUMN cancel_reason TEXT NOT NULL DEFAULT '';
ALTER TABLE invoices ADD COLUMN cancelled_at TEXT;

CREATE TABLE credit_notes (
  id INTEGER PRIMARY KEY,
  number TEXT NOT NULL UNIQUE,
  invoice_id INTEGER NOT NULL REFERENCES invoices(id),
  issue_date TEXT NOT NULL,
  reason TEXT NOT NULL,
  gst_mode TEXT NOT NULL,
  gst_rate_bp INTEGER NOT NULL DEFAULT 0,
  subtotal_paise INTEGER NOT NULL,
  tax_paise INTEGER NOT NULL,
  total_paise INTEGER NOT NULL CHECK (total_paise > 0),
  snapshot_json TEXT NOT NULL,
  pdf_key TEXT NOT NULL,
  pdf_sha256 TEXT NOT NULL,
  issued_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_credit_notes_invoice ON credit_notes(invoice_id);
CREATE TRIGGER credit_notes_immutable BEFORE UPDATE ON credit_notes
BEGIN SELECT RAISE(ABORT, 'credit notes are immutable'); END;
CREATE TRIGGER credit_notes_no_delete BEFORE DELETE ON credit_notes
BEGIN SELECT RAISE(ABORT, 'credit notes are immutable'); END;

CREATE TABLE receipt_pdfs (
  receipt_id INTEGER PRIMARY KEY REFERENCES receipts(id),
  pdf_key TEXT NOT NULL,
  pdf_sha256 TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE VIEW invoice_balances AS
  SELECT i.id AS invoice_id, i.total_paise,
    COALESCE((SELECT SUM(amount_paise) FROM effective_receipts r WHERE r.invoice_id = i.id), 0) AS received_paise,
    COALESCE((SELECT SUM(tds_paise) FROM effective_receipts r WHERE r.invoice_id = i.id), 0) AS tds_paise,
    COALESCE((SELECT SUM(total_paise) FROM credit_notes c WHERE c.invoice_id = i.id), 0) AS credited_paise
  FROM invoices i;
