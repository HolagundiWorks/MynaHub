-- GST registration scheme declared once in the company profile:
--   none        unregistered / no GST charged
--   composition 6% of turnover (3% CGST + 3% SGST), paid by the firm, not charged on the bill
--   regular     18% (9% CGST + 9% SGST intra-state, or 18% IGST inter-state)
ALTER TABLE company_profile ADD COLUMN gst_scheme TEXT NOT NULL DEFAULT 'none'
  CHECK (gst_scheme IN ('none','composition','regular'));

-- A receipt settles part of an invoice. `tds_paise` is tax the client deducted and paid
-- to the government on the firm's behalf; it settles the invoice but is not cash received.
CREATE TABLE receipts (
  id INTEGER PRIMARY KEY,
  number TEXT NOT NULL UNIQUE,
  invoice_id INTEGER NOT NULL REFERENCES invoices(id),
  received_date TEXT NOT NULL,
  amount_paise INTEGER NOT NULL CHECK (amount_paise >= 0),
  tds_paise INTEGER NOT NULL DEFAULT 0 CHECK (tds_paise >= 0),
  tds_section TEXT NOT NULL DEFAULT '',
  mode TEXT NOT NULL CHECK (mode IN ('bank','upi','cheque','cash')),
  reference TEXT NOT NULL DEFAULT '',
  notes TEXT NOT NULL DEFAULT '',
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  CHECK (amount_paise + tds_paise > 0)
);
CREATE INDEX idx_receipts_invoice ON receipts(invoice_id);
-- A bank reference (UTR) can only be recorded once.
CREATE UNIQUE INDEX idx_receipts_reference ON receipts(reference) WHERE reference <> '';

CREATE TRIGGER receipts_no_update BEFORE UPDATE ON receipts
BEGIN SELECT RAISE(ABORT, 'receipts are immutable'); END;
CREATE TRIGGER receipts_no_delete BEFORE DELETE ON receipts
BEGIN SELECT RAISE(ABORT, 'receipts are immutable'); END;
