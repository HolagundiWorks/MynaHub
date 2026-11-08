# Session broker and database service (contract v1)

Status: **proposal v1, 2026-11-08.** Part of [WORKFLOW-KIVO-HUB.md](../WORKFLOW-KIVO-HUB.md). Two parties build against this file at the same time: the **Kivo broker** (server of section 1) and the **launcher database service** (server of section 2, client of section 1). Change it only by editing this file in the same commit as the code.

Rules for both: no password, Neon key or company-file key ever crosses either interface or is written to disk by a client. A session token lives in memory only.

## 1. Session broker (served by Kivo)

Transport: Windows **named pipe** `\\.\pipe\osas-session-broker`, created by Kivo with an ACL that allows **only the current user** (no network access, `PipeOptions.CurrentUserOnly`). Message mode, UTF-8, **one JSON object per message**, one request then one response, then the client closes.

Every request has `"v":1` and `"op"`. Every response has `"v":1` and `"ok"`. On `ok:false`: `"error"` (code below) and `"message"` (English, for display).

| op | Request fields | Success result | Notes |
|---|---|---|---|
| `status` | none | `signed_in` (bool), `user`, `company_id`, `company_name`, `expires_utc` | Never shows UI. Safe to poll. |
| `ask_session` | `app` (`tren`, `falcon`, `myna`, `dbservice`), `purpose` (`db_connect`, `save_to_office`, `import`, `sync`), optional `project_id`, optional `interactive` (default true) | `token`, `expires_utc`, `user`, `company_id`, `roles` (array of strings) | If Kivo has a session: returns a token at once. If not and `interactive` is true: Kivo comes to the front with its sign-in, waits for the user, then answers (the call may take minutes; the client uses a 10 minute timeout). If `interactive` is false and signed out: `signed_out`. |
| `validate` | `token` | `valid` (bool), `user`, `company_id`, `roles`, `expires_utc` | Used by the database service for every connection. Constant-time token comparison. |
| `renew` | `token` | new `expires_utc` | Sliding window; refused after the absolute limit. |
| `end_session` | `token` (optional: omit to end all) | none | Kivo sign-out calls this for all tokens and tells the database service to drop its connections. |

Error codes: `signed_out`, `cancelled` (user closed the sign-in), `denied` (role does not allow the purpose), `bad_token`, `expired`, `kivo_unavailable` (client-side: pipe not there), `bad_request`.

Tokens: 32 random bytes, base64url, opaque. One token per (app, purpose, project). **Idle lifetime 8 hours, absolute 24 hours.** Kivo keeps the table in memory; restart of Kivo ends all sessions (clients ask again).

If the pipe does not exist, the client starts Kivo with `O-Studio.exe --broker` (starts hidden at the sign-in or the tray, creates the pipe, no main window until needed) and retries for up to 60 s.

## 2. Database service (served by the launcher, Myna)

Transport: **loopback HTTP** `http://127.0.0.1:8743` (the AI Core uses 8742). Every request except `/v1/health` carries `Authorization: Bearer <session token>`; the service calls broker `validate` (cache a positive answer for 30 s). JSON bodies. Errors: `{"ok":false,"error":"<code>","message":"..."}` with HTTP 401 for `signed_out`/`bad_token`/`expired`, 403 `denied`, 404, 409 `revision_conflict`.

| Method and path | Purpose |
|---|---|
| `GET /v1/health` (no token) | `{"ok":true,"state":"ready"|"signed_out"|"offline"|"starting","sync":"idle"|"pushing"|"pulling"|"offline","pending":<count>}` |
| `POST /v1/projects/connect` | Body `{project_id, name, osp_path, revision}`. Registers the `.osp`, returns `{connected:true, remote_revision, action:"none"|"push"|"pull"|"conflict"}`. |
| `GET /v1/projects/{id}` | `{local_revision, remote_revision, last_sync_utc, state, pending}` |
| `POST /v1/projects/{id}/push` | Body `{revision, sha256}`. Sends the file revision (the `.osp` is read by the service from the registered path). Returns `{remote_revision}`; `409` if the remote moved. |
| `POST /v1/projects/{id}/pull` | Fetches the newest remote revision to a sidecar next to the `.osp` (`<name>.osp.incoming`) and returns `{revision, sha256}`; the app decides to adopt it (never overwritten silently). |
| `GET /v1/projects/{id}/events` | Server-sent events: `revision` (`{remote_revision}`), `state`. Replaces polling. |

State when signed out: the service keeps running and keeps the local database; sync is paused; `/health` says `signed_out`; connect calls answer 401.

Without a session token the apps still open and save the local `.osp` (see C in the workflow note).

## 3. Process facts

- The service runs inside the launcher process (as the AI Core does) and starts with it. It never opens a window.
- The launcher UI shows `AI: ready/starting/stopped` and `Database: connected/offline/signed out` from `/health`.
- Environment for children started by the launcher: `OSAS_DB_URL=http://127.0.0.1:8743` (set next to the existing AI variables in `SuiteWire`).

## 4. Tests that must exist

- Broker: status signed out and in; `ask_session` non-interactive signed out gives `signed_out`; `validate` of a wrong token is `valid:false`; the pipe refuses another user (ACL test); end_session invalidates.
- Database service: `/health` without a token; 401 without or with a bad token; a fake broker answers `validate`; connect, push, pull against a local test store; `409` when the remote moved.
