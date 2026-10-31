-- Lock project codes when using automatic numbering (Firm settings).
ALTER TABLE company_profile ADD COLUMN project_code_locked TEXT NOT NULL DEFAULT 'on'
  CHECK (project_code_locked IN ('on', 'off'));
