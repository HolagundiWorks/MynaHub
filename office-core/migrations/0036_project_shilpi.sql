-- The ShilpiDB data file a project's estimate, billing, tasks, quantities and purchase orders are read from.
ALTER TABLE projects ADD COLUMN shilpi_path TEXT NOT NULL DEFAULT '';
