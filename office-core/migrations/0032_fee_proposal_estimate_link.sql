-- Link a fee proposal to the O-CoreOp estimate saved on the company-file project (ArtifactRef JSON + total at link time).
ALTER TABLE fee_proposals ADD COLUMN estimate_artifact_json TEXT NOT NULL DEFAULT '';
ALTER TABLE fee_proposals ADD COLUMN estimate_total_paise INTEGER NOT NULL DEFAULT 0 CHECK (estimate_total_paise >= 0);
