-- Template Studio: HTML + CSS templates. Published versions stay immutable; work in progress lives in a separate draft.
ALTER TABLE templates ADD COLUMN format TEXT NOT NULL DEFAULT 'wording' CHECK (format IN ('wording','html'));
ALTER TABLE template_versions ADD COLUMN html_source TEXT;
ALTER TABLE template_versions ADD COLUMN css_source TEXT;
ALTER TABLE template_versions ADD COLUMN change_summary TEXT NOT NULL DEFAULT '';

CREATE TABLE template_drafts (
  template_id INTEGER PRIMARY KEY REFERENCES templates(id),
  html_source TEXT NOT NULL,
  css_source TEXT NOT NULL,
  based_on_version INTEGER,
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
