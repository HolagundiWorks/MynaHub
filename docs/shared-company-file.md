# The shared company file

Written 2026-10-19. O-Studio and O-CoreOp work in the same company file (`.adms`) at the same time. Clients and projects are the office's records and exist once. O-CoreOp keeps each project's quantities and estimates in the same file, as a document on that project.

This is roadmap item 2.2. It is built in `office-core` (`company.rs`, `share.rs`), exposed through the office interface (`office-ffi`, [office-abi.md](office-abi.md)), and used by O-CoreOp through `Office.Shared` and the File menu.

## What is shared

| What | Where it lives | Who writes it |
|---|---|---|
| Clients, projects, the project brief, staff, leave, library | The shared tables of the company file | O-Studio, and O-CoreOp through the office interface |
| A project's quantities, estimates and settings | `project_documents`, kind `aqc.project` (the same JSON a `.bbsproj` holds) | O-CoreOp |
| Invoices, letters, accounts | The company file | O-Studio |

O-CoreOp keeps the content of a project as one JSON document. The relational tables for quantities and estimates are a later step: they would let O-Studio query a BOQ line, and they need O-CoreOp's model (`.bbsproj` version 17, dozens of lists) normalised and migrated. The document store already gives one place for the data, one project identity, revisions and history.

## Two apps at the same time

- **Plain files open in shared mode.** SQLite write-ahead logging with ordinary locking and a five second wait for another app's write. Both apps read and write; SQLite orders the writes.
- **A shared claim.** Each open company holds a shared lock on its `.aoslock` sidecar file. It is how an app knows whether another app has the file open.
- **Taking the file to itself.** Protecting a file (encrypting it at rest) and upgrading its format change the file for every app, so they take the exclusive lock, which is refused while any other app has the company open. The message says to close it there first. An upgrade refused this way changes nothing, and the safety copy is only taken once the exclusive lock is held.
- **Protected (encrypted) files stay one app at a time.** They are opened through a decrypted working copy that only one app can hold. A second app gets "already open".

## Seeing the other app's changes

`data_version` returns a number that goes up whenever another connection has written to the file (SQLite's `PRAGMA data_version`), and `schema_ok`, which is false when another app has upgraded the file past what this build understands. O-CoreOp polls it every two seconds and, when the open project's document has moved on, says so.

## Not overwriting each other

A save names the revision it was based on (`expected_revision`, 0 for a document never saved). If another app has saved since, the save is refused with "This project was changed in another app since you opened it. Reload it before saving, so nothing is overwritten." and nothing is written. The check and the write happen in one immediate transaction.

The last ten revisions of each document are kept in `project_document_history`. Each revision has a SHA-256, which is what an `ArtifactRef` ([contracts/refs.md](contracts/refs.md)) names.

## Operations (office interface)

| Operation | Needs | Does |
|---|---|---|
| `data_version` | View | `{ version, schema_ok }` |
| `doc_get` `{ project_id, kind }` | View | The document, or null |
| `doc_list` `{ project_id }` | View | The project's documents without their bodies |
| `doc_revision` `{ project_id, kind, revision }` | View | A revision from the current one or the history |
| `doc_put` `{ project_id, kind, expected_revision, body }` | Edit | `{ revision, checksum_sha256 }`, or the conflict message |

A kind is lower-case letters, digits, dots and underscores, starting with a letter. O-CoreOp uses `aqc.project`. A document can be up to 64 MB.

## In O-CoreOp

- **File > Open company file…** asks for the user name and password (and the file password for a protected file).
- **File > Company projects…** lists the projects; Open loads the project's document, or starts a new project filled in from the office's record when it has none; New project creates the client and project in the company file.
- **File > Save to company file…** puts the project that is open (for example one opened from a `.bbsproj` file) into the company file, as a new project or over an existing one.
- **Save** writes to the company file for a project that lives there. A conflict offers to reload.
- The **Company** chip in the status strip shows which file is open.
- The engine is `office.dll`, built from `office-ffi` in this repository and copied beside the app; without it the menu items say it is missing.

## What is not done

- O-Studio does not yet watch `data_version` or show a project's O-CoreOp quantities. It reads the same file, so a refresh shows the new projects.
- The quantities are one document, not tables (above).
- The conflict dialog offers reload or keep-mine; there is no merge.
- O-CoreOp's other stores (rate books, parties, schedules) are inside the project document today.
- The `ProjectRef` of a project is available (`project_ref`), but O-CoreOp does not yet put it on its BOQ and estimate outputs.
