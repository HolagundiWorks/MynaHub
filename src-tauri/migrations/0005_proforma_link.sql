-- A tax invoice raised from a pro forma points back to it.
ALTER TABLE invoices ADD COLUMN source_proforma_id INTEGER REFERENCES invoices(id);
CREATE UNIQUE INDEX idx_invoices_source_proforma ON invoices(source_proforma_id)
  WHERE source_proforma_id IS NOT NULL AND status <> 'cancelled';
