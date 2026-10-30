# Office Core

The shared company file for the architecture office apps: O-Studio (the office app), O-CoreOp (quantities, bar bending, estimation and billing) and O-Draft (CAD).

Office Core holds the office's clients, projects, staff and library and the company file that holds them.

The crate owns:
- **Sign-in and the running workspace.** `workspace` holds the open company file and the signed-in session, with `require` (the check every command makes first), login, lock and unlock. `auth` has the roles and permissions and password handling.
- **The company file itself.** `company` creates, opens, locks and closes a company file, keeps the recent-files list, converts the first release's data, and can protect a file with a password and recovery code (`vault`). Documents are stored inside the file (`store`).
- **A C interface for native shells.** `host` has the session registry, the result type and the common operations (`company.open`, `company.create`, `auth.login` and so on). An app exports its own functions and passes its operations to it.
- **The file format.** `migrations/` and `schema::migrate`. An app passes a `prepare` function that brings a newly opened file to the schema it needs (the core, plus its own track). A company file is one SQLite file versioned by `user_version`; migrations only append. The engine links SQLite through `rusqlite` with the **`bundled`** feature, so the installer does not need a separate SQLite runtime on the machine.
- **The audit log.** `audit` writes to a table that the schema makes append-only.
- **The shared records.** `clients` (clients, representatives, projects, project brief) and `office` (staff, leave, library), with the checks in `check`.

An app adds its own session, permissions and commands on top, as O-Studio does in `src-tauri/src/clients.rs` and `office.rs`. Apps never write each other's tables. They share these records and refer to each other's records by id.

Test it on its own with `cargo test` in this folder.

## Schema tracks
The `core` chain (the migrations in this crate) holds the shared records and the original O-Studio tables, and is versioned by `user_version`. Every other app adds its own tables through a **track** (`track::Track`, applied with `track::migrate`). Each track has its own list of migrations and its own version, kept in the `schema_tracks` table, so each app can ship migrations independently and open the same file in any order.

Rules, checked on every migration and rolled back if broken:
- A track creates only objects named `<track>_...` (for example `aqc_schedules`, and its indexes and triggers).
- A track never alters or drops anything that already exists, so one app cannot change another app's tables or the shared records.
- A track may refer to shared tables by foreign key, for example `project_id INTEGER REFERENCES projects(id)`.
- A file written by a newer build of an app is refused with a message to update the app.
- Track names are lower-case letters and digits, start with a letter, and are not `core`.

```rust
const O-CoreOp: Track = Track { name: "aqc", migrations: &[include_str!("../migrations/0001_schedules.sql")] };
office_core::migrate(&mut conn)?;          // the core chain
office_core::track::migrate(&mut conn, &O-CoreOp)?; // this app's tables
```

## Not moved yet
O-Studio-specific tables (billing, the books, bank, templates, documents) are still in the core chain, because every existing company file has them there in that order. New O-Studio tables should go on an `aos` track rather than the core chain.

## Using it from an app
```rust
let state = AppState::new(company::placeholder(), app_dir, my_app::prepare);
company::create_company(&state, path, "Studio", "owner", password, password)?;   // or company::open_company
workspace::login(&state, "owner", password)?;
let user = workspace::require(&state, Permission::Edit)?;                      // first line of every command
```
Only one app can have a company file open at a time; the file is locked while it is open.
