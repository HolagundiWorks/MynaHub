# Workflow: Kivo is the office, the launcher is the engine room

Status update 2026-11-08 (later): **Tren and Falcon are independent of Kivo.** They share the local `.osp` as their database and no longer sign in to Kivo or call Myna's database service (decision 2 below, "each app asks Kivo on demand", is withdrawn). Kivo receives project data only through Falcon's exported CSV. The broker and the database service remain in Kivo and Myna but have no client.

Status: **first slices built and shipped in suite 1.5.0 (2026-11-08)**: the Kivo session broker and company sign-in, the slimmed Myna (AI Core and database service only), Falcon's CSV export and Kivo's CSV import. Tren and Falcon now connect the open `.osp` to the database service (built after 1.5.5, next suite build). **Still to build:** approvals and revisions stored fully in Kivo, and pulling a newer shared revision into an open file. Open questions: approval roles, locking measured-to-date, whether `item_code` is Falcon's library code or Kivo's price list, one company or several per PC.

## The workflow in one line

Draw in **Tren**, measure in **Falcon**, both on **one `.osp` file** that is connected to the suite database. **Falcon exports a CSV**; **Kivo imports it** for estimates and running bills. **Approvals and revisions live in Kivo.**

## Who does what

| App | Owns | Does not own any more |
|---|---|---|
| **Myna (launcher)** | Hosts the **AI Core** (Olmo, loopback 8742) and the **database service**: the local shared project database (project files are not synced). Tray icon, start and stop of the suite apps. | Company login (removed from the launcher) |
| **Kivo** | **Company login** and the session. Estimates, running bills, **approvals, revisions**, clients, accounts. Cloud sync settings. CSV import. | |
| **Tren** | The drawing and its take-off rows, written into the `.osp`. | Sign-in, approvals, revisions |
| **Falcon** | Quantities, rates and the estimate sheet from the take-off. **CSV export.** | Sign-in, approvals, revisions |

## Decisions (owner, 2026-11-08)

1. **Database (changed 2026-11-08):** a **local database on this PC**. **Project files stay on this PC (decided 2026-11-08).** Tren and Falcon share the local `.osp`; nothing in it is pushed to Neon. Only Kivo is synced, and project data reaches the company database only through the CSV that Falcon exports and Kivo imports. Pushing the project file itself is off; `OSAS_PUSH_PROJECT_FILES=1` turns it back on for a machine that should do it (`Osas.Core.ProjectFilePolicy`). Earlier design: a local database that syncs to Neon. This is the design in [ODP-PROJECT-FILE-AND-SYNC.md](ODP-PROJECT-FILE-AND-SYNC.md) and [CLOUD-NEON.md](CLOUD-NEON.md), with the launcher as the host.
2. **Sign-in:** **each app asks Kivo on demand.** When Tren or Falcon first needs the database it asks Kivo for a session; Kivo opens, the user signs in once, and the session is handed back. Tren and Falcon never store company credentials or Neon keys (already the rule in CLOUD-NEON.md).
3. **CSV contents:** estimate lines, measured quantities for running bills, and the plumbing and electrical take-off rows.

## What already exists (build on it)

- `.osp` (SQLite, `takeoff_header` / `takeoff_row` kinds 1 to 13, `project_doc`, `change`, `meta.revision`), live sync between Tren and Falcon.
- Kivo: approvals (`approvals_cmd.rs`), accounting and ledger, documents, a revision sidecar written by Tren (`revision.rs`).
- Falcon: an Estimate page with CSV, purchase orders, Take-off page reading kinds 3 to 13.
- Launcher: Myna starts Cloud sync after sign-in and hosts the AI Core.

## What is new

### A. Session broker (Kivo)
Kivo exposes a small local endpoint (named pipe, current user only). `ask_session(app, purpose)` returns a short-lived token, opening Kivo's sign-in if there is no session. The launcher database service accepts that token; Tren and Falcon present it to connect.

### B. Launcher database service (Myna)
The launcher owns the database process and sync. It no longer shows a login; it waits for a session from Kivo (A) and then starts sync as it does today. Tray menu shows "AI: ready", "Database: connected / offline / signed out".

### C. DB connect in Tren and Falcon
The `.osp` stays the working copy. "Connect" means: register the project id with the database service, push the file's revisions, and pull others' revisions (the `change` log and `meta.revision` already carry this). If there is no session the file still opens and edits; the app shows "Not connected" and asks Kivo when the user chooses Connect or Save to office.

### D. Falcon CSV export, Kivo import
One file, UTF-8, header row, one `row_type` column so the three contents travel together.

| row_type | Columns |
|---|---|
| `estimate` | project_id, drawing_revision, package, item_code, description, unit, quantity, rate, amount |
| `measured` | project_id, drawing_revision, item_code, unit, measured_to_date, this_period, source (`tren:<kind>` or `falcon`) |
| `takeoff` | project_id, drawing_revision, kind (`plumbing`, `plumbing_fixtures`, `electrical` ...), tag, size, quantity, unit, level |

Kivo import: `estimate` rows create or update an estimate version; `measured` rows feed a **running bill** (this period against the previous bill and the estimate); `takeoff` rows attach as the priced basis. Import is idempotent on (project_id, drawing_revision, row_type, item_code or tag) and reports rows added, changed and refused.

### E. Approvals and revisions in Kivo
Estimates and bills have a revision number and an approval state in Kivo. Falcon and Tren show the state read-only (from the database) and stop keeping their own revision or approval records. The `.osp` revision stays a technical counter; the business revision is Kivo's.

## Build order and who can work at once

1. **Contract first (small):** session-broker protocol (A) and the CSV schema (D). Everything else depends on these two.
2. In parallel after that: **B** launcher (remove login, host DB service), **A** Kivo broker plus sign-in screen, **D** Falcon export and Kivo import.
3. Then **C** Tren and Falcon connect (needs A and B).
4. Then **E** approvals and revisions in Kivo, and remove the old fields from Falcon.

## Open points for the owner

- Which approval roles exist for an estimate and a bill (one approver, or a chain)?
- Does a running bill lock the `measured_to_date` it used, so a later re-measure makes a new bill rather than changing the old one? (Recommended: yes.)
- Is `item_code` the Falcon item library code, or Kivo's own price list code?
- One company per PC, or several? It decides whether the database service keeps one local database or one per company file.

### Defaults in force until the owner answers (2026-11-08)

- A running bill **locks** the `measured_to_date` it used; a re-measure makes a new bill.
- **One company per PC**: Kivo keeps one open company, and the database service keeps one local database.
- **One approver** for an estimate and a bill.
