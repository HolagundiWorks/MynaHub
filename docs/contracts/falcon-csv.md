# MynaOps CSV export and MynaHub import (contract v1)

Status: **proposal v1, 2026-11-08.** Part of [WORKFLOW-KIVO-HUB.md](../WORKFLOW-KIVO-HUB.md). MynaOps writes this file; MynaHub reads it. Change it only together with both code paths.

## File

- Name: `<project name>-falcon-<yyyymmdd>-r<drawing revision>.csv`
- UTF-8 **with BOM** (opens correctly in Excel), RFC 4180: comma separator, `"` quoting, `""` for a quote, CRLF line ends.
- Numbers use `.` as the decimal separator, no thousands separator. Dates ISO 8601. Currency amounts have no symbol; the currency is the company's (a `currency` column on the `meta` row, below).
- **One header row, always the same 20 columns, in this order.** A column that does not apply to a row is empty. MynaHub reads by column name, not position, and ignores unknown extra columns.

```
schema,row_type,project_id,drawing_revision,package,item_code,description,unit,quantity,rate,amount,measured_to_date,this_period,previous,source,kind,tag,size,level,note
```

`schema` is always `osas-falcon-csv/1`. `project_id` is the `.osp` project UUID. `drawing_revision` is the `.osp` `meta.revision` at export.

## Row types

| `row_type` | Meaning | Columns used |
|---|---|---|
| `meta` | Exactly one, first data row. | `project_id`, `drawing_revision`, `description` (project name), `note` (`currency=INR;exported=<ISO time>;falcon=<version>`) |
| `estimate` | One line of the estimate. | `package`, `item_code`, `description`, `unit`, `quantity`, `rate`, `amount`, `source` |
| `measured` | Quantity measured so far for an item, for a running bill. | `item_code`, `description`, `unit`, `measured_to_date`, `this_period`, `previous`, `source` |
| `takeoff` | One take-off row from the drawing (priced basis). | `kind`, `tag`, `size`, `unit`, `quantity`, `level`, `source`, optional `item_code` |
| `total` | Exactly one, last. Check row. | `amount` (sum of `estimate` amounts), `quantity` (count of data rows) |

Rules:
- `amount = quantity * rate`, rounded to 2 places; MynaHub recomputes and refuses a line that differs by more than 0.01.
- `measured_to_date = previous + this_period` (within 0.0005); `previous` is the measured-to-date of the last bill MynaOps knows of (0 if none).
- `source` is `falcon` for lines typed or priced in MynaOps, `tren:<kind number>` for lines taken from the drawing (kinds 1 to 13, including 12 plumbing and 13 plumbing fixtures).
- `kind` for `takeoff` rows is the name: `wall`, `opening`, `column`, `lintel`, `room`, `slab`, `board`, `circuit`, `plumbing`, `plumbing_fixtures`, `electrical`, and so on, exactly as the `.osp` names them.
- `item_code` is **MynaOps's item library code** until the owner confirms MynaHub's price-list code scheme; MynaHub keeps a mapping table (`item_code` to MynaHub price-list item) and reports unmapped codes on import rather than guessing.

## MynaHub import behaviour

1. Read the whole file, validate the schema, the `meta` and `total` rows and every row; a bad file is refused **as a whole** with a list of line numbers and reasons (nothing partly imported).
2. Show a preview: rows by type, unmapped item codes, lines that differ from the current estimate, totals.
3. On confirm, in one transaction:
   - `estimate` rows create or update an **estimate version** for the project (a new revision number in MynaHub; the previous revision stays readable).
   - `measured` rows become a **draft running bill** for the period: each item's `this_period` against the approved estimate quantity; a bill that would exceed the estimate quantity is flagged, not refused.
   - `takeoff` rows are stored as the priced basis attached to that estimate version.
4. **Idempotent:** the import key is (`project_id`, `drawing_revision`, `row_type`, `item_code` or `tag`). Importing the same file twice changes nothing and says so; a newer `drawing_revision` makes a new estimate revision.
5. Import result: `{added, changed, unchanged, refused, unmapped:[...]}`, shown to the user and written to MynaHub's activity log with the file's SHA-256.
6. Approvals and revisions are MynaHub's: an import never approves anything, and it never edits an **approved** estimate in place (it creates the next revision in draft).

## Tests that must exist

- MynaOps: writes a file for a sample project (all row types, plumbing rows included); header exact; BOM present; round trip through a strict CSV reader.
- MynaHub: valid file imports; same file twice is a no-op; bad `amount`, wrong `schema`, missing `total`, duplicate item code are refused as a whole; unmapped codes are reported; an approved estimate gets a new draft revision, not an edit.
- A shared fixture: `docs/contracts/falcon-csv.sample.csv`, used by both test suites (MynaOps must produce it byte for byte from its sample project; MynaHub must import it).
