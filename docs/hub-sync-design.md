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
