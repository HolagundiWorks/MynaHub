# One project file across devices: `.osp`, Neon sync, tablet viewer

Status: **step 1 built 2026-10-30** (the file and its two libraries, below); steps 2 to 4 are proposals. Decisions taken with the owner: the tablet and phone app views and marks up the synced file first; the file is a new SQLite-based container (`.osp`).

## Goals

1. **One original.** A project is one file. The same file, byte for byte in meaning, is what O-Draft, O-CoreOp and the viewer open on every device.
2. **O-Draft opens it as a drawing; O-CoreOp opens it as a take-off** (and as the project: estimate, bills, tasks, quantities, purchase orders).
3. **Continuation across devices.** Start on the desktop, carry on on a tablet or phone, come back, with nothing copied by hand and no silent overwrite.
4. **Offline first** on the desktop. The network only moves revisions around.

## The file: `.osp` (SQLite, one file beside the drawing)

SQLite gives transactions, one writer at a time with readers alongside, crash safety and a query language, and is readable from Rust (`rusqlite`), .NET (`Microsoft.Data.Sqlite`, already in O-CoreOp) and the web (sql.js / WASM). It replaces both the `.takeoff.vdb` and the O-CoreOp `.bbsproj`.

| Table | Holds |
|---|---|
| `meta` | `format` = `osas-project`, `version`, project id (UUID), name, created, `revision` (integer, +1 on every committed change), `base_revision` last synced |
| `drawing` | the drawing as the user's file: `name`, `ext` (dwg or dxf), `bytes` (blob), `sha256`. One row per drawing; several allowed. O-Draft reads it with its existing reader and saves it back as a new row version. |
| `drawing_state` | O-Draft's own state that is not in the DWG (levels, sheet sets, phases) as JSON |
| `takeoff_header`, `takeoff_row` | header per drawing (slot, state, source, levels) and one row per wall, opening, column, lintel, room, slab, board, circuit (`kind`, `n`, `json`). Same JSON as the bridge row, so the importer is unchanged. |
| `project_doc` | the O-CoreOp project JSON (chunked as now, or normalised later) |
| `asset` | annotated PDFs, images, attachments: `id`, `name`, `mime`, `sha256`, `bytes` |
| `markup` | viewer markups and comments: `id`, `drawing`, `author`, `created`, `kind`, geometry JSON, text, `resolved` |
| `history` | restore points (replaces the `.history.vdb`) |
| `change` | append-only log of what changed in each revision (table, key, kind) so sync can send deltas later |

Rules: SQLite WAL mode; a writer takes `BEGIN IMMEDIATE`; every commit increments `meta.revision` and writes `change` rows. Unknown tables and columns are kept (a newer app's data survives an older app's save).

**Who uses it**
- O-Draft: open = read the newest `drawing` row (and, if the file came from a DWG, import it into an `.osp` on first save). Save = write a `drawing` row and `takeoff_*` rows in one transaction. Take-off recording writes `takeoff_*` directly in the same SQLite file.
- O-CoreOp: open the `.osp`; the Take-off page reads `takeoff_*`; Save writes `project_doc`.
- Both can have the file open at once: SQLite WAL lets one write while the other reads, and writers queue. O-CoreOp watches `meta.revision` (cheap) instead of polling a server.
- Migration: `.vdb` take-off and `.bbsproj` **import into a new `.osp`** — specified here; **not shipped** in apps yet (greenfield `.osp` only). See O-CoreOp `docs/MIGRATION-OSP.md`.

## Sync with Neon

The file stays the working copy on each device. **Neon** (Lakebase Postgres + Object Storage) moves revisions.

| Part | Use |
|---|---|
| **Object Storage** | the `.osp` of every project (snapshot at a revision) and large assets |
| **Lakebase Postgres** `project`, `project_revision` | project id, owner, name, latest revision, who pushed it, sha256, size |
| **Lakebase Postgres** `markup` (mirror) | markups and comments, so the viewer can write them without owning the file |
| **Revision signal** | Studio learns "project X is now at revision N" by polling Postgres or `LISTEN`/`NOTIFY` (exact transport TBD in the connector) |
| **Auth** | Neon Auth (Managed Better Auth) and/or the existing company sign-in for Studio; viewer uses the same Neon project. Authorization enforced in O-Studio and API handlers, not in the desktop apps |

**Protocol (optimistic, like the company file):** a device pushes `base_revision = N` with its snapshot; the server accepts only if the latest is still N (else it answers with the newer revision and the device merges). Merge unit: `takeoff_row` and `markup` by key (last writer per row, shown to the user), `project_doc` and `drawing` by whole-file choice (keep mine / take theirs / keep both as a copy). The `change` log lets later versions send deltas instead of whole files.

**Offline:** nothing blocks; changes queue as a revision; the next connection pushes or asks for a merge.

**Privacy:** drawings and billing then live in hosted Postgres and object storage. Use a Neon project in a region the practice accepts; creating a cloud project costs money and needs the owner's confirmation before anything is uploaded.

## Tablet and phone viewer

- A web app (installable, works on iOS, Android, tablets). It signs in, lists projects, downloads a project snapshot, and draws it.
- **Drawing:** the Rust renderer (`aad-render`, with `aad-doc` and `aad-io` for DWG/DXF) built to WASM, drawing to WebGPU or a 2D canvas. The device draws the same display list the desktop does.
- **Phase 1:** open, pan, zoom, layers on/off, measure, jump to a take-off item, read the take-off and the estimate totals.
- **Phase 2:** markups and comments (pen, text, cloud, photo), stored in `markup` and synced; they show on the desktop as a panel and can be turned into take-off notes.
- **Not in the first phases:** editing the drawing geometry, and driving the desktop remotely. Remote control would need a relay for O-Draft's control channel (today loopback only, no authentication) and is a security project of its own.

## Build order

1. **`.osp` library** (Rust crate `aad-odp` and a .NET reader in `Office.Shared`), with import from `.vdb` and `.bbsproj`, tests, and a spec. No UI change.
2. **O-Draft and O-CoreOp on `.osp`:** open, save, take-off recording, live refresh by revision; remove the server and pointer file.
3. **Sync service (step 3 — in progress):** Neon schema (Postgres), Object Storage layout, push/pull in **O-Studio** WinUI (**Suite → Cloud sync**), Postgres `LISTEN` for near-real-time pull. Merge UI and viewer still open.
4. **Viewer phase 1** (WASM render, read-only), then **phase 2** (markups).

Each step ships on its own; the file works without sync, and sync works without the viewer.

## Open questions

- Neon region and who owns the project and can open it (company or per person)?
- DWG fidelity: the drawing is stored as the user's own DWG/DXF bytes, so nothing is lost, but the viewer needs the DWG reader in WASM (large). If that is too heavy, O-Draft can also store a pre-built display list per drawing for the viewer.
- Do markups need to appear inside the DWG, or are they a separate layer shown over it?

## Step 1: built

- **Rust:** crate `aad-odp` in O-Draft (`crates/odp`): create, open, one-revision edits, reads, the change log, import of a `.vdb` (take-off headers and rows, annotated PDFs as assets, O-CoreOp's project record as the project document) and of a `.bbsproj`. 14 tests; example `pack` (`cargo run -p aad-odp --example pack -- out.osp Name --drawing f.dwg --vdb f.vdb --bbsproj f.bbsproj`).
- **.NET:** `Office.Shared.OdpFile` in O-CoreOp: open, create, read meta, drawing, take-off, project and assets, write the project or a drawing, `ChangesSince`. 6 tests.
- **Same file both ways:** the .NET tests read `Fixtures/small.osp` written by Rust (22 KB DXF + a take-off + the project); the Rust tests read `tests/fixtures/from_dotnet.osp` written by .NET and then write to it.
- Real data: `DEMO.dwg` (1.3 MB) and the imported take-off pack into one 1.4 MB `.osp`.
- The apps do not use it yet (step 2).

## Step 2: built (O-Draft and O-CoreOp use `.osp`)

- **O-Draft opens and saves `.osp`.** File > Open and Save as list "Project (drawing and take-off)". Opening reads the drawing stored in the file (DWG or DXF bytes) with the existing reader; Save writes the drawing back **in place, as one revision, and not at all when the bytes are unchanged**, so a save that changed nothing does not make anyone reload. The file is never replaced as a whole, so another program can have it open. (Engine: `ContainerHooks` in `crates/engine/src/cmd/file.rs`; the host installs them with `aad-odp`.)
- **Take-off recording goes into the project file.** With no `path` or `server` given, `takeoff.record` (and the automatic record after a save) writes into the `.osp` the drawing came from (`OdpSink`: the recorder's steps are unchanged; only what differs is written, as one revision). `takeoff.restore` reads from it.
- **O-CoreOp opens `.osp`.** The project document is stored in it; the Take-off page reads the take-off (headers, rows, the annotated PDF) from it with the importer and the preview unchanged.
- **Live, with no server:** O-CoreOp polls `meta.revision` every two seconds; a revision it did not write itself raises "O-Draft updated the take-off". The active-project pointer names the `.osp`.
- **Checked end to end (2026-10-30):** `DEMO.dwg` plus its take-off packed into `work.osp`; O-Draft opened it (title `work.osp`), O-CoreOp opened the same file and showed the project "house", O-Draft recorded the take-off into it, and O-CoreOp showed "O-Draft updated the take-off" within the poll interval. After a save the change log read: drawing put (r1), take-off headers and rows (r2), project (r2), a new header (r3), drawing put (r4).

### Not done

- O-Draft stores the drawing as DXF when a project is saved as a new `.osp` from a DWG (DWG stays DWG when the file already holds one). "Save as project" from a DWG keeping the DWG bytes is not done.
- Two writers at the same instant were not stress-tested (one waits up to five seconds for the other).
- The `.osp` is not in the installers' file associations yet.
- Sync (step 3) and the viewer (step 4).

## Decision (2026-10-30): the Neon connector is in O-Studio

Sync is O-Studio's job, not O-Draft's or O-CoreOp's. O-Draft and O-CoreOp only ever open and save the local `.osp` and never talk to Neon; O-Studio (the hub that already owns the company, sign-in and projects) runs the connector. This keeps the drawing and billing apps offline-first and free of keys, and gives the suite one place for sign-in, team access and the sync log.

**Update 2026-10-30:** cloud backend is **Neon** (replacing the earlier Supabase plan). Same roles: Postgres metadata, object storage for snapshots, Studio-only client.

What that means for step 3:

| Part | In O-Studio |
|---|---|
| Sign-in | Neon Auth and/or the company sign-in Studio already uses; the session and API keys stay in Studio. Nothing from it is handed to O-Draft or O-CoreOp. |
| Which projects sync | Studio lists the projects it knows (company file project id, folder, `.osp` path). Sync is per project, switched on in Studio. |
| Push | Studio watches each synced `.osp`'s `meta.revision` (cheap, read-only open). When it rises it uploads the new snapshot to Object Storage and records it in `project_revision`, refusing if the server is already ahead (then it offers the merge). |
| Pull | Studio watches Postgres for new revisions of projects it syncs and downloads snapshots from Object Storage. It writes into the `.osp` the same way the apps do (one revision, `BEGIN IMMEDIATE`); O-Draft and O-CoreOp see the new revision through the polling they already do (O-CoreOp shows "O-Draft updated the take-off" style messages; O-Draft reloads when asked). |
| Merge | A conflict is shown in Studio (keep mine, take theirs, keep both as a copy); take-off rows and markups merge by key. |
| Viewer | The tablet and phone viewer signs in to the same Neon project and reads what Studio pushed; markups it makes go to Postgres and Studio pulls them into the file. |
| Offline | Studio queues pushes until it is online; the apps do not notice. |

Not in the apps: no Neon client, no keys, no sync code. The `.osp` file format and the revision and change-log tables (done in steps 1 and 2) are what Studio builds on. O-Studio is a Rust-and-.NET app; the connector can read and write the file with `aad-odp` (Rust) or `OdpFile` (.NET), whichever Studio's side already uses.

**Cursor:** enable the **neon-postgres** plugin in each suite repo (`.cursor/settings.json`) so agents can use the Neon MCP server and skills while designing the sync schema.
