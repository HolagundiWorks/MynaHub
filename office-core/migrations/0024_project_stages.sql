-- Project stages are the phase marker and the intended payment schedule.
-- An issued agreement keeps the milestones it was issued with.
ALTER TABLE projects ADD COLUMN floors INTEGER NOT NULL DEFAULT 1;

CREATE TABLE project_stages (
  id INTEGER PRIMARY KEY,
  project_id INTEGER NOT NULL REFERENCES projects(id),
  position INTEGER NOT NULL,
  name TEXT NOT NULL,
  percent_bp INTEGER NOT NULL CHECK (percent_bp > 0 AND percent_bp <= 10000),
  marked_on TEXT NOT NULL DEFAULT '',
  UNIQUE (project_id, position)
);
CREATE INDEX idx_project_stages_project ON project_stages(project_id, position);
