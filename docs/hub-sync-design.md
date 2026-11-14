# MynaHub: Neon login and desktop / Android sync (design, Stage 0)

Status: **proposal, 2026-11-15. Nothing here is built.** Written from a read of this repository, the ArchiMan repository (MynaHub Mobile, branch `mynahub-mobile-rebrand`, tip `a8d5a23`) and read-only Neon MCP calls. No Neon resource was created or changed. Section 12 lists the decisions the owner must make before Stage 1.

Fixed by the owner (2026-11-11): MynaHub keeps every login and its own company database (`.osas`); MynaHub is the only app that syncs to Neon; MynaOps and MynaCAD never hold cloud keys and are independent of MynaHub; MynaHub has no AI and no launcher. This design adds no link between MynaHub and the other two apps.

## 1. What was read

| Area | Finding |
|---|---|
| Company file (`office-core/migrations`, 37 migrations + `aos_0001`) | SQLite. **Every table uses `INTEGER PRIMARY KEY` ids local to one file.** Only `company_file.company_uuid` is global. Some tables have `updated_at TEXT` (clients, projects, tasks, subcontracts, templates); many do not (invoices, receipts, journals, expenses). **No revision counter, no tombstones**: clients/projects are archived, never deleted; `audit_log`, `stored_files`, issued documents are append-only by trigger. |
| Users / roles | `users` (username, Argon2 `password_hash`, `recovery_hash`, role in owner / principal / accounts / document_editor / auditor, lockout counters). Roles and permissions: `office-core/src/auth.rs`. Sign-in is local to the open `.osas` (`auth.login` op through `office-ffi`); there is no network identity. |
| Existing Neon code | `native/Adms.Cloud` pushes whole `.osp` snapshots (`osp_project`, `osp_project_revision`, `osp_markup`, S3 `uploads`) with a service connection string from `%LOCALAPPDATA%\OSAS\neon.env`; **off by default** since 2026-11-08 (`ProjectFilePolicy`). `neon.ts` declares `auth: true`, buckets `uploads` and `assets`, a `hello` function and Realtime; `cloud/src/auth-smoke.ts` already exercises Neon Auth (Managed Better Auth, email + password). The sync model below is **new**; the `.osp` push stays as is. |
| Android (MynaHub Mobile) | Kotlin, Compose, Hilt, **Room schema 33**, app id `com.holagundi.archiman` kept, `INTERNET` permission already present. 60+ entities. Ids are `Long autoGenerate`; times are epoch millis `Long`; most entities have `createdAt`, few have `updatedAt`; no sync columns, no network stack (no OkHttp/Ktor/WorkManager in the app module). Local users (`local_users`) have **no password** (username, role, active), the phone has no password-based login today. The README says "the phone is the system of record". |
| Overlap with the company file | Clients, projects (code, status, site, dates), tasks, invoices, receipts, fee proposals, enquiries, permits, time entries, letters, ledger accounts / journals / periods (slice 1 just added), company profile, local users, finance records. Phone-only: drawings, site visits and issues, measurements and the Measurement Book, RCC members, rate book, schedules, Wi-Fi portal, Siteman. Desktop-only: payroll, bank import, 26AS, templates, boards, Falcon CSV import, approvals. |
| Neon (MCP, read) | The connected account has one organization (`org-floral-sun-23006249`, plan free, created 2026-11-14) and **zero projects**. The org and project named in `CLOUD-NEON.md` (`org-soft-meadow-34349055`, `delicate-butterfly-16013319`) are **not visible** through this connection. So either that project belongs to another account, or it was removed. Decision D1. |

Not verified (not run): any Neon behaviour of Auth, RLS, Data API or Object Storage on the free plan; the contents of an existing Neon project; anything on the phone at run time.

## 2. Principles

1. **One company, one cloud database "tenant"**, owned by MynaHub. A phone is another *device of the same company*, not a separate product.
2. **Local first on both sides.** Each device works fully offline; sync is background catch-up. Sign-in works offline.
3. **Neon holds shared state and a change log; it is not the source of truth for any single entity** (section 5). The company file stays authoritative for the practice ledger and issued documents.
4. **Additive only.** Local schemas change only by new columns/tables (company file: new numbered migration; Room: new migration + schema JSON). Neon: additive migrations as files.
5. **No cloud keys on a client.** Desktop and phone talk to Neon through user tokens and a thin server surface, never a database owner connection string (section 8).
6. **MynaOps and MynaCAD are untouched.** Project data still reaches the company file only through the CSV import.

## 3. Login

### 3.1 Two layers, kept separate

| Layer | What | Where it lives |
|---|---|---|
| **Company sign-in (exists)** | Opens the `.osas`, decides role and permissions (`auth.rs`). Argon2 password, lockout, idle lock. Unchanged. | Local company file |
| **Cloud identity (new)** | Proves *who this person is to Neon* so sync can be authorised and isolated. Neon Auth (Managed Better Auth) account, JWT access token, refresh token. | Neon; tokens on the device |

The cloud identity never replaces the local password. Reason: offline sign-in must keep working, the file may be encrypted at rest with its own password (`AOSENC1`), and the five roles are enforced in the engine.

### 3.2 Providers

Proposed: **email + password** (already smoke-tested in `cloud/src/auth-smoke.ts`) plus **Google** as optional second provider (Decision D3). No magic link or phone in the first release.

### 3.3 Desktop (WinUI)

- A **Cloud account** panel under Suite > Cloud sync: Sign in / Sign out / status. Sign-in is a call to the Neon Auth HTTPS API from C# (`Adms.Cloud`); **no browser web view is needed for email + password**. Google (if chosen) uses the system browser and a loopback redirect on `127.0.0.1`.
- Token storage: refresh token encrypted with **DPAPI (`ProtectedData`, CurrentUser scope)** in `%LOCALAPPDATA%\O-Studio\cloud-auth.bin` (an identifier-spelled path, never committed). Access tokens are memory-only.
- **Linking to the local user.** A new local table `cloud_identity(user_id, neon_user_id, email, linked_at)` ties one cloud account to one local `users.id`. The link is made while signed in locally, by re-entering the cloud password once. The role used for sync permissions is the **local role**, mirrored to Neon (3.5).
- **Offline:** local sign-in works with no network. If the cloud token cannot refresh, the app shows "Not signed in to the cloud, changes are queued" and keeps working. Sign-out of the cloud never locks the company file.
- Neon connection settings today are a full `DATABASE_URL` in `neon.env`. In the new model the desktop needs only the **Auth base URL, the sync API base URL and project public identifiers**, no database password (3.6). The old keys stay for the existing `.osp` push only.

### 3.4 Android

- Sign in screen on first run of cloud sync only; the app remains usable without it (today it is local-only). The phone currently has no passworded login; add a cloud account step plus the existing device lock (biometric / PIN, `BiometricPrompt`) for reopening the app offline.
- Tokens in **EncryptedSharedPreferences or a Keystore-wrapped DataStore** (AES-GCM key in Android Keystore, non-exportable). Never in Room, never in backups (`allowBackup` for that file off; the app already has its own encrypted backup).
- HTTP via OkHttp (new dependency); sync via WorkManager (new dependency), foreground service type `dataSync` is already declared.
- Config (`NEON_AUTH_URL`, `SYNC_API_URL`) from `local.properties` into `BuildConfig` locally and from CI secrets in CI; both are public endpoints, not credentials, but kept out of git so a fork does not point at this tenant.

### 3.5 Roles in the cloud

Neon Auth users carry an application role claim (the five existing roles plus `member` for a phone-only user). Source of truth for roles: **MynaHub desktop (owner)**. The owner invites people (email), assigns role; roles are written to a Neon `company_member` table and into the user's token claims. The phone only enforces what the claim says; the server enforces it for real (RLS, section 6).

### 3.6 What clients may talk to

Options (Decision D2):

- **A (recommended): Neon Data API (PostgREST-style) with RLS**, authenticated by the Neon Auth JWT. Clients hold only a user token; every read/write is checked by Postgres RLS. Needs the Data API enabled on the branch, plus a small set of SQL functions (`sync_pull`, `sync_push`) for atomic batches.
- **B: a Neon Function (HTTPS, already declared in `neon.ts`) as the sync API**, holding the DB connection server-side, validating the JWT itself. More code to write and secure, easier to keep the SQL private.
- **C: direct Postgres from devices.** Rejected: puts a database credential on every phone.

Both A and B keep devices free of connection strings. Whether the Data API and Functions are usable on the owner's plan/region is not verified (D1).

## 4. Sync model

### 4.1 What syncs, wave by wave

| Wave | Entities | Notes |
|---|---|---|
| 1 | company profile (read only to phone), clients, projects, project stages, tasks | Overlap is clean; most useful on a phone. |
| 2 | enquiries, site records / site visits, time entries, permits, fee proposals, project briefs | |
| 3 | invoices, receipts, expenses | Money: desktop-authoritative (5.2). |
| 4 | documents/drawings (files in object storage), measurements, ledger | Larger; ledger only after the double-entry model on the phone and the desktop are reconciled (Decision D6). |
| never | users and password hashes, recovery codes, `suite_tickets`, audit log (desktop keeps its own), `neon.env`, `.osp` project files (stay local unless the opt-in is set), payroll, bank, 26AS | Hashes must never leave the file. |

### 4.2 Identity: global ids

Local integer ids collide across devices. Each synced row gets a **UUID (v7, time-ordered)** column `sync_id`, added by an additive migration (company file migration `0038_sync_columns.sql`; Room migration 33 to 34). Foreign keys in Neon use `sync_id`. Local integer ids remain local. Existing rows get their `sync_id` at first sync (generated, stored).

### 4.3 Change tracking

Per synced table add: `sync_id TEXT UNIQUE`, `rev INTEGER NOT NULL DEFAULT 0` (bumped on every local write by trigger), `updated_ms INTEGER`, `deleted INTEGER DEFAULT 0` (**tombstone**), and a table `sync_state(table, last_pulled_seq, last_pushed_rev)` plus `sync_outbox(table, sync_id, rev)`. Triggers on INSERT/UPDATE write the outbox, so no application code has to remember. Deletion on tables that today cannot be deleted (clients, projects) maps to `archived`; for tables that can be deleted, a tombstone row is sent and the local row is removed after acknowledgement.

Neon side: one table per entity with the same columns plus `company_id`, `seq BIGINT` from a per-company sequence assigned on write, and `updated_by`. Devices pull `WHERE company_id = me AND seq > last_pulled_seq ORDER BY seq`. Realtime/LISTEN is an optimisation; polling (every 30 s foreground, WorkManager periodic in background) is the baseline.

### 4.4 Conflict rule

- Row level, **optimistic**: a push names `base_rev` (the cloud `rev` the device last saw). If it equals the cloud `rev`, accept and increment. Otherwise it is a conflict.
- Default resolution: **field-wise last-writer-wins by `updated_ms` for ordinary entities** (clients, tasks, site records), with server clock as tiebreaker; the loser's version is kept in a `sync_conflict` row so nothing is silently lost.
- **Authoritative entities** (5.2) never merge: the non-owner's edit is rejected and returned as "needs review".
- Tombstone vs edit: the edit wins if its `updated_ms` is later than the delete (resurrects the row), otherwise the delete wins. Documented, tested.
- Issued/locked records (issued invoices, `approved_locked` approvals) are immutable in the cloud as they are locally.

### 4.5 Initial sync

1. Desktop signs in, owner creates the company in Neon (one row in `company`, id = `company_file.company_uuid`; **the uuid is already the stable identity**).
2. First push: desktop assigns `sync_id`s and pushes wave 1 in batches (500 rows) inside resumable pages; progress in `sync_state`.
3. Phone signs in, is added as member, pulls everything for waves it is allowed, in batches.
4. **A phone that already holds local data** (today's ArchiMan users) must choose: *adopt* the company (local rows are matched by `projectCode` / client name and offered for merge, else uploaded as new, with a preview) or *stay local*. Never silent merge (Decision D5).
5. A second desktop joining an existing company starts from an empty or older `.osas` with the same `company_uuid` and pulls.

### 4.6 Large files

Drawings, receipts PDFs, photos, `stored_files` blobs: bytes go to **Neon Object Storage** (S3-compatible) under `company/<company_id>/<sha256>`; the row in Postgres holds key, sha256, size. Content-addressed so duplicates and retries are harmless. Devices obtain **short-lived presigned URLs** from the sync API (5.3); they never hold the S3 access key (today's `Adms.Cloud` uses the key directly on the desktop; that stays for the `.osp` path only). Limit and resumable upload for files above 8 MB. Wave 4.

## 5. Ownership (system of record), proposed

| Entity | Owner | Why |
|---|---|---|
| Company profile, users/roles, number series, accounting periods, ledger, issued invoices, receipts, approvals, estimates / running bills | **Desktop (MynaHub)** | The company file is the system of record (`shared-company-file.md`, 2026-10-28); only MynaHub certifies, issues and approves; the phone can *draft* and read. |
| Clients, projects (basic fields), tasks, enquiries, site visits/issues, daily reports, meetings, time entries, measurements, site photos | **Either (shared, field-wise LWW)** | Created in the field on the phone as often as at the desk. |
| Drawings, letters, documents | **Shared by file revision**; a new revision wins, older stay as history | Mirrors `project_document_history`. |
| Phone-only (RCC members, rate book, Wi-Fi portal state, Siteman) | Phone | Not synced. |
| Payroll, bank, 26AS, templates | Desktop | Not synced. |

Phone-drafted money: a phone may create an invoice **draft** with a client-side temporary number; the desktop assigns the real number from `number_series` on acceptance (avoids two devices issuing `INV-2026-07`). The phone's `INV-YYYY-NN` numbering is then superseded.

## 6. Isolation

- Every Neon synced row has `company_id`. **Row Level Security** on every table: `USING (company_id = auth.company_id() AND is_member)`; writes additionally check the role claim (`accounts` may write money, `auditor` read only, mirroring `Role::allows`).
- One Neon **project** for the product, one **company per tenant row**; multi-company in one project is the default. Strong isolation (a project per customer) is a later option, not needed for one practice.
- Per-user: the five roles above; `document_editor` and `auditor` see no ledger tables (matches `Permission::Finance`).
- Storage: object keys prefixed by company; presign function checks membership; bucket private.
- No table is readable by the anonymous role. RLS is tested with two fake companies and each role (Stage 2 tests, run against a dev branch).

## 7. Neon resources needed (none created)

| Resource | Proposal |
|---|---|
| Organization | Existing `org-floral-sun-23006249` (read via MCP, free plan), or the org in `CLOUD-NEON.md` (D1) |
| Project | One, `mynahub` (region near India, e.g. `aws-ap-southeast-1` as the doc suggests) |
| Branches | `production` (default, no experiments), `dev` (all Stage 1 to 3 work, from `production`; auto-expire policy from `neon.ts` removed for `dev` so it persists), short-lived `test-<n>` for RLS tests |
| Database | `neondb` default; schema `hub` for sync tables |
| Roles | Neon Auth's own roles; `authenticated` (Data API/RLS), a `migrator` role used only from this PC for migrations; no role shared with clients |
| Neon Auth | Enabled on `dev` first; email + password; trusted domains/redirects for the desktop loopback; Google optional |
| Buckets | Reuse `uploads` (private); add `company-files` for synced documents (separate from `.osp` snapshots) |
| Data API / Function | Per D2 |
| Realtime | Optional, later |

Cost on the free plan is unchecked (D1). A "dev" branch is not production data; the existing `neon.ts` TTL rule would delete it, hence the change above.

## 8. Security

- **Secrets:** desktop `%LOCALAPPDATA%\OSAS\neon.env` only (owner connection strings, S3 keys, realtime secret; unchanged, not read by any new code). The new desktop login needs public endpoints only. Android: `local.properties` / CI secrets, only public URLs; **no secret ever enters a client**. Nothing in git, logs, commits or replies. The repo is public: no connection strings, tenant data or extra Neon ids beyond what is needed (this doc names the org id that the Neon console already shows the owner; remove on request).
- Tokens: DPAPI (desktop), Keystore (Android); short access token, rotating refresh token, sign-out revokes.
- Transport TLS only; certificate pinning not planned.
- Data on devices: the `.osas` may be encrypted by the file password; the phone database is not encrypted today (backups are). Cloud data is encrypted at rest by Neon only, **not end-to-end**: the owner should accept that Neon can read synced rows (D7). Option: client-side encryption of sensitive fields later.
- Audit: pushes/pulls logged locally (no row content); desktop audit log gets "cloud sign-in", "sync started/stopped", never credentials.
- Rate limits and lockouts are Neon Auth's; local lockout (`lockout_secs`) stays.
- Public repo: tests must use fake tenants and disposable accounts only.

## 9. Failure modes

| Situation | Behaviour |
|---|---|
| No network | Both apps work; outbox grows; status "queued N changes". |
| Token expired and refresh fails | Cloud status "sign in again"; local sign-in unaffected; no data lost. |
| Neon scale-to-zero cold start | First request slow (retry with backoff). |
| Push conflict | 4.4: LWW with kept loser, or "needs review" for authoritative records. |
| Interrupted initial sync / upload | Resumable by `sync_state`, idempotent on `sync_id` + `sha256`. |
| Clock skew (phones) | Server assigns `seq`; `updated_ms` only a tiebreaker; skew above 5 min flagged. |
| Schema version mismatch (old phone, new desktop) | Server exposes `min_client_version`; old client is told to update and keeps working read-only/queued. |
| Role removed or user disabled | Server rejects; the device keeps local data, the status shows "access removed"; owner can wipe cloud membership but cannot wipe a device remotely (not planned). |
| Company file restored from backup | Rev/seq mismatch detected (`sync_state` ahead of cloud): requires an explicit "re-sync as new device" choice, never a silent push that would delete newer cloud rows. |
| Neon outage / free-plan limit reached | Same as no network; no data loss locally. |
| Duplicate company_uuid (copied file) | Refused with an explanation; owner can reissue the uuid. |
| Cloud data deleted by mistake | Never delete Neon data/branches in this work; use branch point-in-time restore and snapshots (owner enables, D8). |

## 10. Staged plan (after the owner answers)

| Stage | Scope | Done when |
|---|---|---|
| 1 Desktop login | Neon Auth panel in Suite > Cloud sync, DPAPI token store, `cloud_identity` migration, offline unchanged, Auth smoke test against dev | `dotnet test` for the new C# code and an Auth sign-in/out against `dev`, by hand, recorded. |
| 2 Sync engine + schema | company file migration `0038` (`sync_id`/`rev`/tombstone/outbox), Neon `hub` schema as files in `cloud/sql/` applied to `dev` only, RLS tests with two companies and every role, push/pull in `Adms.Cloud`, conflict tests | Rust and C# tests green; two `.osas` copies converge through `dev`. |
| 3 Android | new branch from `mynahub-mobile-rebrand`; Room migration 33 to 34 with sync columns, OkHttp + WorkManager, login, wave 1 | `gradlew :app:testDebugUnitTest` and `:app:assembleDebug`; Room schema JSON added. |
| 4 End to end | desktop to Neon to phone and back for waves 1 to 2, then 3 and 4 as ready; docs and wiki say exactly what was run by hand | Manual log in docs; known gaps listed. |

Each stage is committed on its own, nothing is pushed or merged without the owner, `main` stays installable.

## 11. Cost and effort note

Waves 3 and 4 (money, ledger, files) are the largest and riskiest; wave 1 gives the owner a working phone-to-desktop loop first. Android and desktop use different money types (phone `Double` amount, desktop integer paise): conversion rules must be fixed before wave 3 (D6).

## 12. Decisions the owner must make

1. **D1 Neon account and project.** The connected account (`org-floral-sun-23006249`) has no project, and the org/project in `CLOUD-NEON.md` is not visible. Which account is the product's? OK to create a new project `mynahub` in region `aws-ap-southeast-1` (free plan limits unchecked)? Or relink the old one?
2. **D2 Client access path:** Neon Data API with RLS (recommended), a Neon Function as sync API, or something else.
3. **D3 Sign-in providers:** email + password only, or add Google; may phones and desktops use the same cloud account; who may invite members (owner only, recommended).
4. **D4 Role mapping:** keep the five desktop roles in the cloud; is a phone-only `member` role (no finance) wanted?
5. **D5 Existing phone data:** adopt-and-merge preview (recommended) or phone data stays local and the phone starts fresh when joining.
6. **D6 Ownership table (section 5) and money:** confirm desktop-authoritative money and numbering; confirm ledger waits for wave 4; confirm paise vs `Double` conversion (store paise in Neon).
7. **D7 Privacy:** accept plain (not end-to-end) encryption of synced rows at Neon; whether personal data (PAN, GSTIN, client contacts) may sync.
8. **D8 Safety:** enable snapshots/backups on the project; keep `production` empty until Stage 4 passes.
9. **D9 Android repo:** a new branch (name proposed `mynahub-mobile-sync`) cut from `mynahub-mobile-rebrand` tip `a8d5a23`; keep the app id `com.holagundi.archiman` and package names.
10. **D10 Scope of wave 1:** confirm clients, projects, stages and tasks as the first loop.

## 13. Decisions taken and Stage 1 status (2026-11-15)

The owner accepted all recommendations D1 to D10. Created in Neon (organization `org-floral-sun-23006249`): project `mynahub` (`falling-sky-24757585`, `aws-ap-southeast-1`, Postgres 18), default branch `production` (empty), branch `dev` (`br-weathered-hill-aonxt3ks`) with Neon Auth (Managed Better Auth, email + password) enabled. No bucket, role, Data API or snapshot yet.

**Stage 1 (desktop login), what exists and what was run:**
- `native/Adms.Cloud/CloudAuthClient.cs` (email + password sign-in, access token from the `set-auth-jwt` header, sign-out; https only) and `CloudSessionStore.cs` (session cookies sealed with DPAPI, per company, in `%LOCALAPPDATA%\O-Studio\cloud-auth.json`).
- Suite > Cloud sync has a Cloud account section (sign in, sign out). The address comes from `NEON_AUTH_BASE_URL` in `neon.env` (a public endpoint; now kept when `neon.env` is merged).
- Deviation from section 3.3: the link between the cloud account and the local user is stored in `cloud-auth.json` (field `LocalUser`), not in a `cloud_identity` company-file table. The table arrives with Stage 2's migration if still wanted. Offline: nothing in company sign-in reads this file.
- Run: `dotnet test native/Osas.Core.Tests -p:Platform=x64` filtered to `CloudAuthTests` (8 tests, fake HTTP, fake sealing, plus one live test against `dev` with a disposable account) and the desktop Release build. **Not run:** the WinUI panel by hand (no screenshot or click-through), the DPAPI sealing on its own (tests use a stand-in), the Google provider (not configured; shared Google provider is listed by Neon but unused).
- **Open finding:** `dev` Auth currently allows sign-up by anyone who knows the endpoint, without email verification. Stage 2 must make membership (not just a valid token) the gate through RLS, and sign-up should be switched off or invite-only before production (owner-only invites, D3).

## 14. Stage 2 status (2026-11-15): sync engine and schema, wave 1

Wave 1 = clients, projects, project stages, tasks. Built and run:

- **Neon (dev branch only):** `cloud/sql/0001_hub_sync.sql` applied to `dev` (schema `hub`: `company`, `company_member`, `company_seq`, `record`, `conflict`; functions `create_company`, `set_member`, `remove_member`, `push_records`, `role_of`; RLS on every table; no direct write grants). Data API enabled on `dev` with `hub` as the only exposed schema. `production` is untouched and empty.
- **Deviations from sections 4 and 6:** one generic `hub.record` table with a JSON `body` per row (not one table per entity), so a new field never needs a schema change on three clients; the local mapping is a `sync_map` table, not new columns on the company tables. Membership and roles live in `hub.company_member` (never in Neon Auth user fields, which users can edit).
- **Company file:** migration `0038_sync_wave1.sql` (additive: `sync_map`, `sync_dirty`, `sync_state`, triggers that record changes only once sync is enabled) and `office-core/src/sync.rs` (collect, ack, apply-pull). PAN and GSTIN of clients and a project's workspace path are **not** synced. Operations `sync_status/enable/disable/collect/ack/apply` in `src-tauri/src/syncops.rs` (link and unlink need the owner; the rest need Edit).
- **C#:** `native/Adms.Cloud/HubSyncEngine.cs` (`HubDataApi`, `HubSyncEngine`: push first, then pull) and, in Suite > Cloud sync, **Link this company to the cloud** and **Sync company now**. There is no background loop yet: syncing is by button.
- **Conflict rule as built:** the push names the cloud revision it last saw; if stale, the later `updated_ms` wins and the loser is kept in `hub.conflict`. A pull never overwrites a row with unsent local edits (push first). A record whose parent has not arrived is deferred and holds the cursor.

**Run (what is verified):**
- `cargo test` in `office-core`: 106 passed (9 are the new sync tests).
- `dotnet test native/Osas.Core.Tests -p:Platform=x64`: 54 passed (fake HTTP and a scripted engine for the orchestration).
- `cloud/sql/tests/rls-check.ps1` against `dev` with three disposable accounts: 22 of 22 passed (non-member sees and writes nothing; no token reads nothing; no direct table writes, even for the owner; auditor cannot push or read conflicts; stale-base conflicts settle by later edit and both losers are kept; last owner cannot be removed).
- `native/Adms.SyncCheck` against `dev` with the release engine DLL: two real company files (A and B) shared one cloud company; A's existing rows reached the empty B; an edit on B reached A; a delete on A removed the task on B; a conflicting client edit settled on the later one on both; a no-op pass pushed nothing; the PAN stayed on A. 9 of 9 passed.
- Desktop Release build succeeded.

**Not run / known gaps:** the two new buttons were never clicked in the running app; no background or periodic sync, no push on edit; invoices, receipts, ledger, files and the other waves are not synced; `hub.company_member` role names beyond owner/auditor/member were not exercised by the RLS script; Android is Stage 3. Observed once and not reproduced: a brand-new account's very first Data API call answered "not signed in"; the same call succeeded on repeat. If it recurs, the client should retry once after sign-up.
**Dev data created by the checks** (disposable, never deleted by this work): about ten test cloud accounts and the test companies `rls-test-*` and `synccheck-*` on `dev`.

## 15. Stage 3 and 4 status (2026-11-15): Android and end to end

**Stage 3 (ArchiMan repository, branch `mynahub-mobile-sync` cut from `a8d5a23`, commits `bfead81` and the retry commit after it; nothing pushed).** MynaHub Mobile gets Practice > Cloud sync: cloud sign-in (Neon Auth, session sealed with the Android Keystore), join a company, Sync now. The phone syncs **clients, projects and project tasks** with the same record bodies as the desktop (`app/src/main/java/com/holagundi/archiman/sync/`, `docs/cloud-sync.md` in that repository). The Room schema version did not change: the tracking tables are created on every database open. Differences on the phone: no project stages (the phone models stages as fee items), tasks without a project are skipped, priority and position and client private notes stay on the phone, the phone has no client archive. Joining shows the counts to be uploaded and merges same-named records instead of duplicating them (D5).

**Run (Stage 3):** `gradlew :app:testDebugUnitTest` 138 tests, 0 failures (28 new sync tests; the SQL, triggers and conflict logic run on real SQLite, table definitions from the exported Room schema) and `gradlew :app:assembleDebug` succeeded. **Not run:** the app on a device or emulator (no screenshot or click-through), the Keystore sealing, the HTTPS transport on Android, the Compose screen.

**Run (Stage 4, by hand, against the dev branch with two different disposable accounts):** step 1, `native/Adms.SyncCheck -- e2e1 <dir>`: the owner's real company file sent 4 records to a new cloud company and added the phone account as `member` (PASS, PASS). Step 2, the Android sync code (`LiveCloudE2ETest`, real HTTPS, real SQLite via JDBC, not a phone) signed in as the member, saw the company, received the 4 records, edited a task, added a task and changed a client phone, and sent 3 changes. Step 3, `-- e2e3 <dir>`: the desktop synced again and got all three changes (4 of 4 checks PASS). So desktop to Neon to phone code and back is proven for wave 1, across two accounts and the member role.

**Fixed on the way:** a token was sometimes accepted before the database knew whose it was ("not signed in" on a first call, seen twice). Both clients now retry such a request up to three times with a fresh token (tests added on both sides).

**Neon on dev now:** sign-up is switched off (owner-created accounts only). New members need an account the owner creates (Neon console or the MCP user tool) before **set_member** can add them; there is no in-app invite yet. The Data API schema is `hub` only.

**Still open:** a background or automatic sync on either side; the other waves (invoices, receipts, ledger, files, stages); an in-app way to add members; an Android device run; production (`production` branch has no schema, Auth settings or trusted origins yet: the app sends `http://localhost:3000` as its origin, which only works where localhost is allowed, as on dev); email verification is off, so `set_member` by email trusts that the invited address belongs to the person (turn verification on before production).
