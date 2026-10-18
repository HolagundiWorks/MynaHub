-- One bank credit can be split across several invoices, so the old one-receipt-per-reference rule goes;
-- double use is prevented per bank transaction instead.
DROP INDEX idx_receipts_reference;

CREATE TABLE bank_batches (
  id INTEGER PRIMARY KEY,
  filename TEXT NOT NULL,
  file_sha256 TEXT NOT NULL UNIQUE,
  row_count INTEGER NOT NULL,
  imported_by INTEGER REFERENCES users(id),
  imported_at TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Original statement data is preserved as imported; only status / matches change.
CREATE TABLE bank_transactions (
  id INTEGER PRIMARY KEY,
  batch_id INTEGER NOT NULL REFERENCES bank_batches(id),
  row_no INTEGER NOT NULL,
  txn_date TEXT NOT NULL,
  narration TEXT NOT NULL DEFAULT '',
  reference TEXT NOT NULL DEFAULT '',
  debit_paise INTEGER NOT NULL DEFAULT 0 CHECK (debit_paise >= 0),
  credit_paise INTEGER NOT NULL DEFAULT 0 CHECK (credit_paise >= 0),
  balance_paise INTEGER,
  raw_row TEXT NOT NULL,
  dedupe_key TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'unmatched' CHECK (status IN ('unmatched','partial','matched','ignored','duplicate')),
  note TEXT NOT NULL DEFAULT '',
  CHECK ((debit_paise > 0) <> (credit_paise > 0))
);
CREATE INDEX idx_bank_txn_status ON bank_transactions(status);
CREATE INDEX idx_bank_txn_dedupe ON bank_transactions(dedupe_key);

CREATE TABLE bank_txn_receipts (
  txn_id INTEGER NOT NULL REFERENCES bank_transactions(id),
  receipt_id INTEGER NOT NULL UNIQUE REFERENCES receipts(id),
  matched_by INTEGER REFERENCES users(id),
  matched_at TEXT NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (txn_id, receipt_id)
);

CREATE TABLE bank_rejected_rows (
  id INTEGER PRIMARY KEY,
  batch_id INTEGER NOT NULL REFERENCES bank_batches(id),
  row_no INTEGER NOT NULL,
  raw_row TEXT NOT NULL,
  reason TEXT NOT NULL
);

CREATE TRIGGER bank_txn_original_data BEFORE UPDATE ON bank_transactions
WHEN NEW.txn_date <> OLD.txn_date OR NEW.narration <> OLD.narration OR NEW.reference <> OLD.reference
  OR NEW.debit_paise <> OLD.debit_paise OR NEW.credit_paise <> OLD.credit_paise OR NEW.raw_row <> OLD.raw_row
BEGIN SELECT RAISE(ABORT, 'imported bank data cannot be modified'); END;
CREATE TRIGGER bank_txn_no_delete BEFORE DELETE ON bank_transactions
BEGIN SELECT RAISE(ABORT, 'bank transactions cannot be deleted'); END;
