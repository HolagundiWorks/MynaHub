# Workflow: MynaHub is the office, the launcher is the engine room

Status update 2026-11-08 (later): **MynaCAD and MynaOps are independent of MynaHub.** They share the local `.osp` as their database and no longer sign in to MynaHub or call Myna's database service (decision 2 below, "each app asks MynaHub on demand", is withdrawn). MynaHub receives project data only through MynaOps's exported CSV. The broker, the database service, the sign-in ticket and the locked project were then removed from MynaHub, Myna, MynaCAD and MynaOps (same day).

Status: **first slices built and shipped in suite 1.5.0 (2026-11-08)**: the MynaHub session broker and company sign-in, the slimmed Myna (AI Core and database service only), MynaOps's CSV export and MynaHub's CSV import. MynaCAD and MynaOps now connect the open `.osp` to the database service (built after 1.5.5, next suite build). **Still to build:** approvals and revisions stored fully in MynaHub, and pulling a newer shared revision into an open file. Open questions: approval roles, locking measured-to-date, whether `item_code` is MynaOps's library code or MynaHub's price list, one company or several per PC.

## The workflow in one line

Draw in **MynaCAD**, measure in **MynaOps**, both on **one `.osp` file** that is connected to the suite database. **MynaOps exports a CSV**; **MynaHub imports it** for estimates and running bills. **Approvals and revisions live in MynaHub.**

## Who does what

| App | Owns | Does not own any more |
|---|---|---|
| **Myna (launcher)** | Hosts the **AI Core** (Olmo, loopback 8742) and the **database service**: the local shared project database (project files are not synced). Tray icon, start and stop of the suite apps. | Company login (removed from the launcher) |
| **MynaHub** | **Company login** and the session. Estimates, running bills, **approvals, revisions**, clients, accounts. Cloud sync settings. CSV import. | |
| **MynaCAD** | The drawing and its take-off rows, written into the `.osp`. | Sign-in, approvals, revisions |
| **MynaOps** | Quantities, rates and the estimate sheet from the take-off. **CSV export.** | Sign-in, approvals, revisions |

## Decisions (owner, 2026-11-08)

1. **Database (changed 2026-11-08):** a **local database on this PC**. **Project files stay on this PC (decided 2026-11-08).** MynaCAD and MynaOps share the local `.osp`; nothing in it is pushed to Neon. Only MynaHub is synced, and project data reaches the company database only through the CSV that MynaOps exports and MynaHub imports. Pushing the project file itself is off; `OSAS_PUSH_PROJECT_FILES=1` turns it back on for a machine that should do it (`Osas.Core.ProjectFilePolicy`). Earlier design: a local database that syncs to Neon. This is the design in [ODP-PROJECT-FILE-AND-SYNC.md](ODP-PROJECT-FILE-AND-SYNC.md) and [CLOUD-NEON.md](CLOUD-NEON.md), with the launcher as the host.
2. **Sign-in:** **each app asks MynaHub on demand.** When MynaCAD or MynaOps first needs the database it asks MynaHub for a session; MynaHub opens, the user signs in once, and the session is handed back. MynaCAD and MynaOps never store company credentials or Neon keys (already the rule in CLOUD-NEON.md).
3. **CSV contents:** estimate lines, measured quantities for running bills, and the plumbing and electrical take-off rows.

## What already exists (build on it)

- `.osp` (SQLite, `takeoff_header` / `takeoff_row` kinds 1 to 13, `project_doc`, `change`, `meta.revision`), live sync between MynaCAD and MynaOps.
- MynaHub: approvals (`approvals_cmd.rs`), accounting and ledger, documents, a revision sidecar written by MynaCAD (`revision.rs`).
- MynaOps: an Estimate page with CSV, purchase orders, Take-off page reading kinds 3 to 13.
- Launcher: Myna starts Cloud sync after sign-in and hosts the AI Core.

## What is new

### A. Session broker (MynaHub)
MynaHub exposes a small local endpoint (named pipe, current user only). `ask_session(app, purpose)` returns a short-lived token, opening MynaHub's sign-in if there is no session. The launcher database service accepts that token; MynaCAD and MynaOps present it to connect.

### B. Launcher database service (Myna)
The launcher owns the database process and sync. It no longer shows a login; it waits for a session from MynaHub (A) and then starts sync as it does today. Tray menu shows "AI: ready", "Database: connected / offline / signed out".

### C. DB connect in MynaCAD and MynaOps
The `.osp` stays the working copy. "Connect" means: register the project id with the database service, push the file's revisions, and pull others' revisions (the `change` log and `meta.revision` already carry this). If there is no session the file still opens and edits; the app shows "Not connected" and asks MynaHub when the user chooses Connect or Save to office.

### D. MynaOps CSV export, MynaHub import
One file, UTF-8, header row, one `row_type` column so the three contents travel together.

| row_type | Columns |
|---|---|
| `estimate` | project_id, drawing_revision, package, item_code, description, unit, quantity, rate, amount |
| `measured` | project_id, drawing_revision, item_code, unit, measured_to_date, this_period, source (`tren:<kind>` or `falcon`) |
| `takeoff` | project_id, drawing_revision, kind (`plumbing`, `plumbing_fixtures`, `electrical` ...), tag, size, quantity, unit, level |

MynaHub import: `estimate` rows create or update an estimate version; `measured` rows feed a **running bill** (this period against the previous bill and the estimate); `takeoff` rows attach as the priced basis. Import is idempotent on (project_id, drawing_revision, row_type, item_code or tag) and reports rows added, changed and refused.

### E. Approvals and revisions in MynaHub
Estimates and bills have a revision number and an approval state in MynaHub. MynaOps and MynaCAD show the state read-only (from the database) and stop keeping their own revision or approval records. The `.osp` revision stays a technical counter; the business revision is MynaHub's.

## Build order and who can work at once

1. **Contract first (small):** session-broker protocol (A) and the CSV schema (D). Everything else depends on these two.
2. In parallel after that: **B** launcher (remove login, host DB service), **A** MynaHub broker plus sign-in screen, **D** MynaOps export and MynaHub import.
3. Then **C** MynaCAD and MynaOps connect (needs A and B).
4. Then **E** approvals and revisions in MynaHub, and remove the old fields from MynaOps.

## Open points for the owner

- Which approval roles exist for an estimate and a bill (one approver, or a chain)?
- Does a running bill lock the `measured_to_date` it used, so a later re-measure makes a new bill rather than changing the old one? (Recommended: yes.)
- Is `item_code` the MynaOps item library code, or MynaHub's own price list code?
- One company per PC, or several? It decides whether the database service keeps one local database or one per company file.

### Defaults in force until the owner answers (2026-11-08)

- A running bill **locks** the `measured_to_date` it used; a re-measure makes a new bill.
- **One company per PC**: MynaHub keeps one open company, and the database service keeps one local database.
- **One approver** for an estimate and a bill.
