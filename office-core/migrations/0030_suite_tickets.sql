-- One-time sign-in tickets: how the suite app (OSAS) signs the apps it launches in as the same person without passing a password.
-- Only a hash of the ticket is kept. A ticket lives for two minutes and works once.
CREATE TABLE suite_tickets (
  id INTEGER PRIMARY KEY,
  user_id INTEGER NOT NULL REFERENCES users(id),
  secret_hash TEXT NOT NULL UNIQUE,
  app TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  used_at INTEGER
);
CREATE INDEX idx_suite_tickets_expires ON suite_tickets(expires_at);
