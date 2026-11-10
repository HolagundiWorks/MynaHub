# MynaOps CSV import in MynaHub

The file format is the MynaOps CSV contract v1 (kept with the suite workflow notes; the shared fixture is [falcon-csv.sample.csv](falcon-csv.sample.csv), used by the tests of both apps). This note records how MynaHub applies it.

Code: `office-core/src/falcon_csv.rs` (strict parser and validator), `office-core/src/falcon_import.rs` (preview and import), `src-tauri/src/falcon_import_cmd.rs` (bridge operations `falcon_csv_preview`, `falcon_csv_import`, `falcon_csv_map_list`, `falcon_csv_map_set`), `native/Adms.Desktop/FalconImport.cs` (the button on Practice, Approvals, and its dialogs).

## Where it lands

MynaHub's existing approvals model, `suite_approvals` (`docs/contracts/approvals.md`, section B): an estimate version is a row of kind `estimate`, a running bill a row of kind `running_bill`. Both start as `draft`; the import never approves. Approve with the existing Approve action.

Around it, on the `aos` schema track (`office-core/migrations/aos_0001_falcon_import.sql`): the MynaOps project to MynaHub project link, the item code mapping table, the imported rows (the idempotency ledger and the priced take-off basis) and the import log.

## Rules

- A file with any problem is refused whole, with a line number and reason for each problem (up to 200).
- Estimate and take-off rows form an estimate version for one drawing revision. Same drawing revision and nothing different: no change. Different, and the version is a draft: the draft is updated. Different, and the version is approved: the next draft version is created (supersedes the approved one only when approved). A new drawing revision: a new version; an unapproved MynaOps draft of an older revision becomes `superseded` and stays readable. A file for a drawing revision older than the newest imported one is refused if it differs.
- Measured rows form a draft running bill holding the lines that are new or changed since the last import for that drawing revision. A re-measure of a line already in an approved bill goes into a new draft bill; the approved bill keeps what it used. Measured quantities are compared with the approved estimate (else the estimate in the file, else the newest version) and an overrun is flagged, not refused. MynaOps's `previous` is compared with the last approved bill in MynaHub and a difference is flagged.
- Unmapped item codes are reported, imported as unmapped, and never guessed. The mapping (MynaOps code to MynaHub price-list item) is edited in the preview.
- Result: `{added, changed, unchanged, refused, unmapped}` plus `removed`, `noop`, `issues`, `flags`. Estimate lines that a newer file dropped from a draft are counted in `changed` and `removed`. Written to the activity log (`falcon_csv_import`, `falcon_csv_import_nochange`, `falcon_csv_refused`) with the file's SHA-256.
