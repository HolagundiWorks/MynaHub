ALTER TABLE company_profile ADD COLUMN architect_name TEXT NOT NULL DEFAULT '';

-- Templates hold wording with {{fields}}; each publish is a new immutable version.
CREATE TABLE templates (
  id INTEGER PRIMARY KEY,
  kind TEXT NOT NULL CHECK (kind IN ('letter','agreement')),
  name TEXT NOT NULL UNIQUE COLLATE NOCASE,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE TABLE template_versions (
  id INTEGER PRIMARY KEY,
  template_id INTEGER NOT NULL REFERENCES templates(id),
  version INTEGER NOT NULL,
  body TEXT NOT NULL,
  published_by INTEGER REFERENCES users(id),
  published_at TEXT NOT NULL DEFAULT (datetime('now')),
  UNIQUE (template_id, version)
);
CREATE TRIGGER template_versions_immutable BEFORE UPDATE ON template_versions
BEGIN SELECT RAISE(ABORT, 'published template versions are immutable'); END;
CREATE TRIGGER template_versions_no_delete BEFORE DELETE ON template_versions
BEGIN SELECT RAISE(ABORT, 'published template versions are immutable'); END;

CREATE TABLE documents (
  id INTEGER PRIMARY KEY,
  kind TEXT NOT NULL CHECK (kind IN ('letter_out','letter_in','agreement')),
  number TEXT UNIQUE,
  project_id INTEGER REFERENCES projects(id),
  party_name TEXT NOT NULL DEFAULT '',
  party_address TEXT NOT NULL DEFAULT '',
  subject TEXT NOT NULL DEFAULT '',
  reference TEXT NOT NULL DEFAULT '',
  doc_date TEXT NOT NULL,
  body TEXT NOT NULL DEFAULT '',
  template_version_id INTEGER REFERENCES template_versions(id),
  fee_paise INTEGER NOT NULL DEFAULT 0 CHECK (fee_paise >= 0),
  status TEXT NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','issued','signed','cancelled')),
  revision_of INTEGER REFERENCES documents(id),
  snapshot_json TEXT,
  pdf_key TEXT,
  pdf_sha256 TEXT,
  issued_at TEXT,
  created_by INTEGER REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_documents_project ON documents(project_id);
CREATE INDEX idx_documents_kind ON documents(kind, status);

-- Once issued, the content can never change; only the status moves on.
CREATE TRIGGER documents_content_frozen BEFORE UPDATE ON documents
WHEN OLD.status <> 'draft' AND (
  NEW.kind <> OLD.kind OR NEW.number IS NOT OLD.number OR NEW.project_id IS NOT OLD.project_id OR NEW.party_name <> OLD.party_name
  OR NEW.party_address <> OLD.party_address OR NEW.subject <> OLD.subject OR NEW.reference <> OLD.reference OR NEW.doc_date <> OLD.doc_date
  OR NEW.body <> OLD.body OR NEW.fee_paise <> OLD.fee_paise OR NEW.snapshot_json IS NOT OLD.snapshot_json
  OR NEW.pdf_key IS NOT OLD.pdf_key OR NEW.pdf_sha256 IS NOT OLD.pdf_sha256 OR NEW.template_version_id IS NOT OLD.template_version_id)
BEGIN SELECT RAISE(ABORT, 'issued documents cannot be modified'); END;
CREATE TRIGGER documents_no_delete BEFORE DELETE ON documents
WHEN OLD.status <> 'draft'
BEGIN SELECT RAISE(ABORT, 'issued documents cannot be deleted'); END;

CREATE TABLE document_milestones (
  id INTEGER PRIMARY KEY,
  document_id INTEGER NOT NULL REFERENCES documents(id),
  position INTEGER NOT NULL,
  name TEXT NOT NULL,
  percent_bp INTEGER NOT NULL CHECK (percent_bp > 0 AND percent_bp <= 10000),
  amount_paise INTEGER NOT NULL CHECK (amount_paise >= 0),
  invoice_id INTEGER REFERENCES invoices(id)
);
CREATE INDEX idx_milestones_doc ON document_milestones(document_id);
CREATE TRIGGER milestones_frozen BEFORE UPDATE ON document_milestones
WHEN (SELECT status FROM documents WHERE id = OLD.document_id) <> 'draft'
  AND (NEW.name <> OLD.name OR NEW.percent_bp <> OLD.percent_bp OR NEW.amount_paise <> OLD.amount_paise OR NEW.position <> OLD.position
       OR NEW.document_id <> OLD.document_id OR (OLD.invoice_id IS NOT NULL AND NEW.invoice_id IS NOT OLD.invoice_id))
BEGIN SELECT RAISE(ABORT, 'issued agreement milestones cannot be modified'); END;
CREATE TRIGGER milestones_no_delete BEFORE DELETE ON document_milestones
WHEN (SELECT status FROM documents WHERE id = OLD.document_id) <> 'draft'
BEGIN SELECT RAISE(ABORT, 'issued agreement milestones cannot be deleted'); END;

CREATE TABLE document_attachments (
  id INTEGER PRIMARY KEY,
  document_id INTEGER NOT NULL REFERENCES documents(id),
  kind TEXT NOT NULL CHECK (kind IN ('attachment','signed_copy')),
  filename TEXT NOT NULL,
  file_key TEXT NOT NULL UNIQUE,
  sha256 TEXT NOT NULL,
  bytes INTEGER NOT NULL,
  uploaded_by INTEGER REFERENCES users(id),
  uploaded_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE TRIGGER attachments_immutable BEFORE UPDATE ON document_attachments
BEGIN SELECT RAISE(ABORT, 'attachments are immutable'); END;
CREATE TRIGGER attachments_no_delete BEFORE DELETE ON document_attachments
BEGIN SELECT RAISE(ABORT, 'attachments cannot be deleted'); END;

-- Starter wording. It is a scaffold: have it reviewed before use with clients.
INSERT INTO templates (kind, name) VALUES ('letter', 'Standard letter');
INSERT INTO template_versions (template_id, version, body) VALUES (
  (SELECT id FROM templates WHERE name = 'Standard letter'), 1,
'Dear Sir / Madam,

Sub: {{document.subject}}
{{document.reference}}

With reference to the above, we write regarding {{project.name}}.

Kindly acknowledge receipt of this letter.

Thanking you,');

INSERT INTO templates (kind, name) VALUES ('agreement', 'Architectural consultancy agreement');
INSERT INTO template_versions (template_id, version, body) VALUES (
  (SELECT id FROM templates WHERE name = 'Architectural consultancy agreement'), 1,
'This agreement is made on {{document.date}} between {{company.name}}, {{company.address}} (the "Architect"), and {{client.name}}, {{client.address}} (the "Client").

# 1. Project
The Client appoints the Architect to provide architectural consultancy services for the project "{{project.name}}" at {{project.site}}.

# 2. Scope of services
The Architect shall provide the services listed in the schedule agreed between the parties. Services not listed are excluded and, if requested, will be charged separately.

# 3. Deliverables
The Architect shall deliver the drawings and documents described in the agreed schedule at the stages set out below.

# 4. Professional fees
The professional fee for the services is {{agreement.fee}}, payable according to the milestone schedule below. Applicable taxes are charged in addition where the Architect is registered for them.

# 5. Payment
Invoices are payable within the period stated on the invoice. Fees paid after deduction of tax at source are credited once the Client provides the relevant certificate.

# 6. Responsibilities of the Client
The Client shall provide timely information, approvals and access to the site, and shall pay the fees as agreed.

# 7. Variations
Changes to the scope shall be recorded in writing and the fee adjusted by agreement.

# 8. Termination
Either party may terminate this agreement by written notice. Fees for services performed up to the date of termination remain payable.

# 9. Governing law
This agreement is governed by the laws of India.');
