-- Several bank accounts (current, savings, UPI settlement...). Statements and transactions belong to one.
CREATE TABLE bank_accounts (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL UNIQUE COLLATE NOCASE,
  bank_name TEXT NOT NULL DEFAULT '',
  account_holder TEXT NOT NULL DEFAULT '',
  account_number TEXT NOT NULL DEFAULT '',
  ifsc TEXT NOT NULL DEFAULT '',
  is_primary INTEGER NOT NULL DEFAULT 0,
  active INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

ALTER TABLE bank_batches ADD COLUMN account_id INTEGER REFERENCES bank_accounts(id);

-- Anything imported before this migration is attributed to a default account.
INSERT INTO bank_accounts (name, is_primary) SELECT 'Main account', 1 WHERE EXISTS (SELECT 1 FROM bank_batches);
UPDATE bank_batches SET account_id = (SELECT id FROM bank_accounts ORDER BY id LIMIT 1) WHERE account_id IS NULL;
