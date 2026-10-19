# The office C interface (version 1)

`office.dll` lets a native app, such as O-CoreOp, share the office company file with O-Studio. It is built from `office-ffi/` with `cargo build --release` there. `native/Office.Interop` is a small .NET wrapper and `native/Office.Smoke` shows it in use; an app that prefers its own wrapper only needs the contract below.

This is not ShilpiDB. ShilpiDB is the separate store for drawing data. This interface is for the office's records: clients, projects, staff, leave and the library.

Several apps can have the same company file open at the same time (see [shared-company-file.md](shared-company-file.md)). A session holds a shared claim on the file while it is open, and destroying the session releases it. Protecting a file or upgrading its format needs the file to itself and is refused while another app has it open; a protected (encrypted) file can be open in one app at a time.

## Functions

| Function | Does |
|---|---|
| `uint32 office_api_version()` | Returns `1`. |
| `OfficeResult office_session_create(const char* app_dir)` | Starts a session with no company open. `app_dir` holds only the recent-files list and temporary working copies. Returns `{"handle": n}`. |
| `OfficeResult office_session_destroy(uint64 handle)` | Closes the company file, releases its lock and ends the session. |
| `OfficeResult office_call(uint64 handle, const char* op, const char* args_json)` | Runs an operation. `args_json` is a JSON object, or null for none. |
| `void office_result_free(OfficeResult)` | Releases a result. Call it for every result, including failures. |

`OfficeResult` is `{ int32 status; uint8* data; size_t data_len; char* error; }`. Status is 0 ok, 1 a domain error (the message in `error` is meant to be shown to the user), 2 a bad request, 3 an invalid handle, 4 an internal error. On success `data` is UTF-8 JSON. Strings passed in are borrowed for the call.

## Operations

Opening and signing in: `company.validate` {path}, `company.open` {path, secret, unlock}, `company.create` {path, name, username, password, confirm} (returns the one-time recovery code), `company.close`, `auth.status`, `auth.login` {username, password}, `auth.unlock` {password}, `auth.lock`, `auth.logout`.

Every other operation needs a signed-in, unlocked session. Reading needs View. Adding or changing needs Edit (not the read-only auditor role).

| Operation | Arguments | Returns |
|---|---|---|
| `client_list` | include_archived | clients |
| `client_save` | client | id |
| `client_set_archived` | id, archived | null |
| `project_list` | client_id (or null), include_archived | projects, with `client_name` |
| `project_save` | project | id |
| `project_set_archived` | id, archived | null |
| `project_brief_get` / `project_brief_save` | project_id [, brief] | `{project_id, brief}` / null |
| `project_ref` | project_id | `ProjectRef` (see [contracts/refs.md](contracts/refs.md)) |
| `project_resolve` | reference | the project id the `ProjectRef` names in this file |
| `artifact_ref_check` | reference | null when the `ArtifactRef` is well formed |
| `company_profile_get` / `company_profile_save` | / profile | the company details / null. Saving needs the settings permission and keeps the file's display name in step |
| `auth.ticket` | app | `{ticket, expires_in, username}`; a one-time sign-in for another app (see [suite-hub.md](suite-hub.md)) |
| `auth.login_ticket` | ticket | signs this session in as the person the ticket was issued to |
| `watch` | [project_id] | `{version, schema_ok, revision}`; the version goes up when another app writes. Needs a session but no permission, and does not count as activity (an idle session still locks) |
| `data_version` | | `{version, schema_ok}`; the version goes up when another app writes |
| `doc_get` / `doc_list` / `doc_revision` | project_id, kind [, revision] | a project's document, its documents without bodies, or one revision |
| `doc_put` | project_id, kind, expected_revision, body | `{revision, checksum_sha256}`, or a refusal if the revision is stale |
| `staff_list` / `staff_save` | / input | staff with leave days this year / id |
| `leave_list` / `leave_save` | / input | leave / id |
| `library_list` / `library_save` | kind (or null), search (or null) / input | entries / id |
| `track.apply` | name, migrations (list of SQL) | the track's version |
| `track.version` | name | the track's version |

Record shapes are those of `office-core` (`clients.rs`, `office.rs`). A malformed request is refused with status 2 or a message, never a crash.

## Adding an app's own tables

An app keeps its data in the same file on its own **schema track**, applied with `track.apply` when it opens the file. It sends its full list of migrations every time; migrations already applied are skipped.

- Every object the track creates must be named `<track>_...` (for example `aqc_schedules`, and its indexes and triggers).
- A track may refer to the shared tables by foreign key, for example `project_id INTEGER REFERENCES projects(id)`.
- It may not alter or drop anything that already exists. A migration that tries is rolled back and the call fails, leaving the file as it was.
- Track names are lower-case letters and digits, start with a letter, and are not `core`.
- A list shorter than what the file already holds is refused with a message to update the app.

## Licensing

O-CoreOp is AGPL-3.0. Do not copy source between O-CoreOp and this repository. The contract in this file is what the two share.
