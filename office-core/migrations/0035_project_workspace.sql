-- Suite project folder: path to the on-disk workspace (odraft + oform + shilpi).
ALTER TABLE projects ADD COLUMN workspace_path TEXT NOT NULL DEFAULT '';
