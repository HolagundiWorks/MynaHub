-- O-Studio: automatic project codes and optional .osp pick on create.
ALTER TABLE company_profile ADD COLUMN project_code_prefix TEXT NOT NULL DEFAULT 'PROJ';
ALTER TABLE company_profile ADD COLUMN project_code_format TEXT NOT NULL DEFAULT 'prefix_year_number';
ALTER TABLE company_profile ADD COLUMN project_code_custom TEXT NOT NULL DEFAULT '';
ALTER TABLE company_profile ADD COLUMN project_osp_on_create TEXT NOT NULL DEFAULT 'automatic'
  CHECK (project_osp_on_create IN ('automatic', 'choose'));
