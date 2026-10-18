ALTER TABLE company_profile ADD COLUMN bank_details TEXT NOT NULL DEFAULT '';

-- One counter per (kind, financial year). Allocated inside the issue transaction.
CREATE TABLE number_series (
  kind TEXT NOT NULL,
  fy TEXT NOT NULL,
  prefix TEXT NOT NULL,
  last_seq INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (kind, fy)
);

-- Money is stored in paise (integer). Quantities in thousandths.
CREATE TABLE invoices (
  id INTEGER PRIMARY KEY,
  project_id INTEGER NOT NULL REFERENCES projects(id),
  kind TEXT NOT NULL CHECK (kind IN ('tax_invoice','proforma')),
  status TEXT NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','issued','cancelled')),
  number TEXT UNIQUE,
  issue_date TEXT NOT NULL,
  due_date TEXT NOT NULL DEFAULT '',
  gst_mode TEXT NOT NULL CHECK (gst_mode IN ('none','intra','inter')),
  gst_rate_bp INTEGER NOT NULL DEFAULT 0,
  notes TEXT NOT NULL DEFAULT '',
  subtotal_paise INTEGER NOT NULL DEFAULT 0,
  tax_paise INTEGER NOT NULL DEFAULT 0,
  total_paise INTEGER NOT NULL DEFAULT 0,
  snapshot_json TEXT,
  pdf_key TEXT,
  pdf_sha256 TEXT,
  issued_at TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_invoices_project ON invoices(project_id);

CREATE TABLE invoice_lines (
  id INTEGER PRIMARY KEY,
  invoice_id INTEGER NOT NULL REFERENCES invoices(id) ON DELETE CASCADE,
  position INTEGER NOT NULL,
  description TEXT NOT NULL,
  qty_milli INTEGER NOT NULL,
  rate_paise INTEGER NOT NULL,
  amount_paise INTEGER NOT NULL
);
CREATE INDEX idx_invoice_lines_invoice ON invoice_lines(invoice_id);

-- Issued invoices are immutable.
CREATE TRIGGER invoices_issued_no_update BEFORE UPDATE ON invoices
WHEN OLD.status = 'issued' AND NOT (NEW.status = 'cancelled' AND NEW.number IS OLD.number AND NEW.total_paise = OLD.total_paise AND NEW.snapshot_json IS OLD.snapshot_json)
BEGIN SELECT RAISE(ABORT, 'issued invoices cannot be modified'); END;
CREATE TRIGGER invoices_issued_no_delete BEFORE DELETE ON invoices
WHEN OLD.status <> 'draft'
BEGIN SELECT RAISE(ABORT, 'issued invoices cannot be deleted'); END;
CREATE TRIGGER invoice_lines_issued_no_change BEFORE UPDATE ON invoice_lines
WHEN (SELECT status FROM invoices WHERE id = OLD.invoice_id) <> 'draft'
BEGIN SELECT RAISE(ABORT, 'issued invoice lines cannot be modified'); END;
CREATE TRIGGER invoice_lines_issued_no_delete BEFORE DELETE ON invoice_lines
WHEN (SELECT status FROM invoices WHERE id = OLD.invoice_id) <> 'draft'
BEGIN SELECT RAISE(ABORT, 'issued invoice lines cannot be deleted'); END;
