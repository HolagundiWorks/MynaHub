-- One optional ink for rules and labels on new documents. Blank keeps the sheet in black.
ALTER TABLE company_profile ADD COLUMN accent_hex TEXT NOT NULL DEFAULT '';
