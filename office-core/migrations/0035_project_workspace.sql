-- Suite project folder: path to the on-disk workspace (O-Draft, O-Form, project.osp).
ALTER TABLE projects ADD COLUMN workspace_path TEXT NOT NULL DEFAULT '';
