-- Template library: families, built-in originals and company-owned copies. Wording versions stay immutable.
ALTER TABLE templates ADD COLUMN template_key TEXT;
ALTER TABLE templates ADD COLUMN family TEXT NOT NULL DEFAULT 'administration' CHECK (family IN ('consultancy','billing','administration'));
ALTER TABLE templates ADD COLUMN layout TEXT NOT NULL DEFAULT 'letter' CHECK (layout IN ('letter','record'));
ALTER TABLE templates ADD COLUMN description TEXT NOT NULL DEFAULT '';
ALTER TABLE templates ADD COLUMN built_in INTEGER NOT NULL DEFAULT 0 CHECK (built_in IN (0,1));
ALTER TABLE templates ADD COLUMN archived INTEGER NOT NULL DEFAULT 0 CHECK (archived IN (0,1));
ALTER TABLE templates ADD COLUMN copied_from INTEGER REFERENCES templates(id);
CREATE UNIQUE INDEX idx_templates_key ON templates(template_key) WHERE template_key IS NOT NULL;

-- The two starter templates from the first release become built-ins.
UPDATE templates SET built_in = 1, template_key = 'standard-letter', family = 'administration',
  description = 'A plain outward letter.' WHERE name = 'Standard letter';
UPDATE templates SET built_in = 1, template_key = 'consultancy-agreement-basic', family = 'consultancy',
  description = 'A short consultancy agreement with a milestone fee schedule.' WHERE name = 'Architectural consultancy agreement';

-- Letterhead: how the firm name and contact details are set at the top of issued documents.
ALTER TABLE company_profile ADD COLUMN letterhead_style TEXT NOT NULL DEFAULT 'classic' CHECK (letterhead_style IN ('classic','minimal'));
