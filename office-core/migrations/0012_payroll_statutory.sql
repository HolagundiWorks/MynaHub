-- Statutory payroll deductions and contributions. Amounts are entered (or suggested from editable rates), never assumed.
ALTER TABLE payroll_entries ADD COLUMN pf_employee_paise INTEGER NOT NULL DEFAULT 0 CHECK (pf_employee_paise >= 0);
ALTER TABLE payroll_entries ADD COLUMN pf_employer_paise INTEGER NOT NULL DEFAULT 0 CHECK (pf_employer_paise >= 0);
ALTER TABLE payroll_entries ADD COLUMN esi_employee_paise INTEGER NOT NULL DEFAULT 0 CHECK (esi_employee_paise >= 0);
ALTER TABLE payroll_entries ADD COLUMN esi_employer_paise INTEGER NOT NULL DEFAULT 0 CHECK (esi_employer_paise >= 0);
ALTER TABLE payroll_entries ADD COLUMN pt_paise INTEGER NOT NULL DEFAULT 0 CHECK (pt_paise >= 0);

INSERT INTO accounts (code, name, type, is_system) VALUES
  ('2310', 'PF payable', 'liability', 1),
  ('2320', 'ESI payable', 'liability', 1),
  ('2330', 'Professional tax payable', 'liability', 1),
  ('5010', 'Employer PF and ESI contribution', 'expense', 1);
