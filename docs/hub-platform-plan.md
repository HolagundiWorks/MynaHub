# Myna platform: implementation plan (against the Developer Guidelines v1.0)

Status: **plan for the owner's decision, 2026-11-15. No code was changed for this plan.** The guidelines (`DEVELOPER_GUIDELINES.md`, §19.1) ask for an inspection and a plan before architectural change; this is that.

**What was inspected.** MynaHub at `e5b582f` (office-core engine, `native/Adms.Cloud`, `cloud/`, `cloud/sql`, the desktop Cloud sync page) and the Android app in the ArchiMan repository on `mynahub-mobile-sync` (`bfead81`, `6eb82dd`; the product is named **MynaHub Mobile**, the guidelines' "Myno Mobile" appears nowhere). MynaOps and MynaCAD were not inspected; they stay out of the cloud (SUITE.md). The Neon project `mynahub` (dev branch only) was read and changed through Neon's tools. Nothing below was run against a design that does not yet exist.

## 1. The short version

The guidelines describe a **company-owned Myna API** (Node, Fastify, self-managed Better Auth, Drizzle) in front of the company's Neon database, with invitation-only identity, RBAC, an authorization engine, files and sync. What exists today is a different shape that already proves the sync idea: **clients talk straight to the Neon Data API with Neon-managed Auth, and Postgres row level security is the only gate.** Guideline §2.2 and §10.4 forbid exactly that ("clients must not bypass the API"). So the plan is: **keep the client-side sync engines (they are reusable), replace the server side with the Myna API, and close the gaps in §3.** It is a large build (a new backend, hosting and a new auth setup), and it raises the bar for a small studio from "paste two web addresses" to "deploy one container". That cost is the guidelines' own choice (§14); this plan accepts it only if you confirm decision D1.

## 2. What exists, against the guidelines

| Guideline | Today | State |
|---|---|---|
| §1.1 no developer-run service | None. The two Neon addresses are the user's own (docs/byo-neon.md) | Met |
| §2.2 / §10.4 API is the only path to data | Clients use the Neon Data API plus RLS directly | **Conflict** |
| §3 stack (Node, TypeScript, Fastify, Drizzle, OpenAPI, Vitest) | None of it in the repo; `cloud/` has TypeScript smoke scripts only | Not started |
| §5 onboarding (wizard, bootstrap token, first admin) | A manual checklist and `cloud/setup/check-neon.ps1` | Partial |
| §6.1 self-managed Better Auth | **Neon-managed** Better Auth (accounts live in `neon_auth`) | **Conflict** |
| §6.2 invitation-only | Public sign-up switched off in the console; no invitation flow; accounts made by hand | Partial |
| §6.4 per-device sessions, view/revoke, MFA | Each sign-in is its own server session; no screens, no MFA | Not started |
| §6.5 secure token storage | DPAPI on desktop (built, tested with a stand-in), Android Keystore (built, **not run on a device**) | Partial |
| §7 schemas, UUID ids, organisation scope, versions, soft delete, migrations | Wave 1 only, as one generic `hub.record` table with a JSON body; UUID `sync_id`; revision and tombstone per row; one migration file | **Weaker than §7.4** |
| §8 RBAC (custom roles, `resource.action` permissions, resource policies) | Five fixed roles in the Rust engine; `hub.company_member.role` mirrors a subset | Not started |
| §8.6 RLS as defence in depth | RLS on every `hub` table, no direct write grants, 22 checks pass on dev | Met (but it is the *only* gate) |
| §9 versioned REST and OpenAPI | None | Not started |
| §12 sync (operation ids, expected version, structured conflicts, opaque cursors, receipts) | Push/pull by record: `base_rev` as expected version, numeric `seq` cursor, tombstones; conflicts settle **last writer wins** with the loser kept in a table | **Partial; §12.5 conflicts with LWW** |
| §13 files in private object storage with signed URLs | None | Not started |
| §15.3 audit events | Desktop keeps a local audit log; the cloud has none | Not started |
| §16 / §17 security tests in CI | RLS and end-to-end scripts run by hand; no CI for the cloud side | Not started |
| §10.2 clients hold no database credentials | Desktop and phone hold none. But `native/Adms.Cloud` still has the old direct-Postgres and S3-key path (off by default, `.osp` push) | Partial |

## 3. Conflicts that need your decision

1. **Data path (D1).** The guidelines require a backend. Today's build is a Data API client. Recommendation: build the Myna API; keep the Neon RLS as the second wall (§8.6). The existing `hub.push_records` rules become the first version of the sync service.
2. **Desktop is local-first (D2).** MynaHub keeps its company in a local `.osas` file and must sign in offline (CLAUDE.md invariants; your 2026-11-11 decision). Guideline §10.4 says Hub requests business data through the API. Both cannot hold. Recommendation: the desktop stays local-first and **syncs** through the API (as built, new transport); it does not become online-only.
3. **Source of truth (D2 again).** §12.1 says the cloud database is authoritative and the phone is a cache. Today the desktop file is authoritative for money, numbering and the ledger (decision D6) and either side may edit field data. Recommendation: cloud is authoritative for **shared field data** (projects, tasks, site updates); the desktop file stays authoritative for **finance** and stays out of the cloud until a separate decision.
4. **Auth (D3).** Moving to self-managed Better Auth means new tables in the company's database and a new sign-in address; the existing dev accounts do not carry over (acceptable: dev only). It buys the invitation flow, MFA, session screens and the bootstrap token the guidelines want, and removes the "no way to create an account" problem found in docs/byo-neon.md.
5. **Roles (D4).** The five roles do not equal the guidelines' seven. Recommendation: define the permission catalogue (`resource.action`) once in a shared package; keep the five roles as **system role templates** on the cloud side (Owner, Principal, Accounts, Document editor, Auditor) alongside the guidelines' defaults and custom roles; the local `.osas` roles are untouched.
6. **Conflicts (D7).** §12.5 forbids silent overwrite. Today's LWW keeps the loser but still decides alone. Recommendation: replace it with **structured conflict responses** (version mismatch returns both versions), resolved by the client for plain fields; tasks/projects stay simple first; money never merges.
7. **Mobile scope (D6).** The guidelines want a small companion (assigned projects, tasks, site updates, photos, documents, notifications, selected approvals). The Android app is a full offline practice app (measurements, finance, ledger, portal). Recommendation: leave the existing local features as they are, and make **only** the guideline's list cloud-synced; do not add finance or admin to the cloud.
8. **Release 1 mobile mode (D9).** §12.6 says online-first for release 1. The phone already has an offline outbox. Recommendation: keep it (it works and is tested) but ship release 1 with the queue limited to the guideline's list and no background sync.
9. **Where the code lives (D5).** The guidelines sketch a `myna-platform` monorepo. Recommendation: start `apps/api` and `packages/api-contracts` **inside the MynaHub repository** (it already has `cloud/` and the public history), publish the OpenAPI contract as the shared artifact the Android repo consumes, and extract to its own repository only if it grows.
10. **Name (D8).** Confirm the app name stays **MynaHub Mobile** (SUITE.md and the app), not "Myno Mobile".

## 4. Target shape (what gets built)

```text
Company-owned: Neon Postgres  +  Myna API (one Docker container, TypeScript/Fastify)  +  object storage
MynaHub (desktop, local .osas)  --sync-->  Myna API  <--sync--  MynaHub Mobile
```
- **Clients hold one thing**: the company's API address (and their own session). No Neon addresses, no keys. This also removes today's "phone has the addresses baked in at build time" problem: the phone is configured at run time with the API address.
- **Myna API** owns Better Auth (self-managed, in the company's database), the permission engine, organisation/members/invitations/roles, projects/tasks/site updates, documents (signed URLs), sync, audit. Runtime database role with RLS on; a separate migration role.
- **Reused as is:** the desktop change tracking and pull-apply (`office-core/src/sync.rs`, migration 0038), the Android `SyncStore`/`SyncEngine`, the DPAPI and Keystore session storage, the demo and check tooling. Only the transport changes (Data API calls become `/v1` calls) and the push gets an operation id.
- **Retired:** the Data API path and `neon_auth` for new companies (after cutover), and the old direct-Postgres and S3-key path in `native/Adms.Cloud` (it conflicts with §10.2 and the `.osp` project files already stay local).

## 5. Phases

Each phase ends with something that runs and is tested; nothing moves to the next until the acceptance line is met. Test notes follow §16; CI must work without any company's production credentials (§17), so integration tests use a local Postgres container, not a hosted Neon branch.

| Phase | Scope | Reuses | New | Acceptance |
|---|---|---|---|---|
| **0 Decisions** | D1 to D10 below | | | Owner's answers recorded in this file |
| **1 Backend foundation** | `apps/api` (Fastify, TypeScript, Zod), Drizzle schema for `core` (organizations, memberships, roles, permissions, role_permissions, membership_roles), `sync`, `audit`; versioned migrations; `/health`; Dockerfile (non-root, health check); two database roles; CI (lint, types, unit, integration against a Postgres container) | `cloud/` TypeScript tooling | Everything server-side | A container started with a Neon connection string reaches `/health` and migrates a fresh database; migration test on an existing database |
| **2 Authentication** | Better Auth (self-managed) in the API; one-time bootstrap token (hashed, expiring, single use, audited); first administrator; invitations (hashed single-use tokens); sessions list/revoke; MFA for administrators; public sign-up off after bootstrap | Desktop and Android session storage | Auth service and invitation flow | Security tests: sign-up blocked, expired or reused invitation fails, suspended account loses access, revoked session fails |
| **3 Authorization** | Permission catalogue package; roles incl. the five system templates and custom roles; `authorize()` middleware with resource checks; RLS context set per transaction (`SET LOCAL`), runtime role without `BYPASSRLS` | RLS checks from `cloud/sql/tests` | Engine and middleware | Cross-organisation and role-escalation tests pass; a request without a permission is refused on every endpoint |
| **4 MynaHub integration** | Desktop "Company connection" (API address, no Neon fields), sign-in, Members and Roles pages for the owner, sync through `/v1/sync` with operation ids and structured conflicts; local company sign-in stays and works offline | `sync.rs`, `HubSyncEngine`, DPAPI store | `/v1` client, members UI | Desktop syncs a demo company through the API; offline sign-in verified; conflict case returns both versions |
| **5 MynaHub Mobile integration** | Run-time company connection (paste or scan the pairing text), login, per-device session, assigned projects and tasks, site updates, photos (signed upload), notifications later | `SyncStore`, `SyncEngine`, Keystore store | `/v1` client, screens, pairing | **Run on a real device**; end-to-end desktop to API to phone to desktop on two accounts, with the member role |
| **6 Synchronisation hardening** | Operation receipts and idempotency, opaque cursors, authorised change feed per organisation, deletion events, conflict resolution UI, local data cleanup on revocation | | | Mobile changes sync with no unauthorised access and no silent loss (tests for retry, conflict, revoked session, permission change) |
| **7 Production hardening** | Backups and restore test, monitoring, upgrade and compatibility docs, security review, setup wizard, file upload validation and retention, reference deployment guide | `docs/byo-neon.md`, check script | Wizard, deployment docs | A company installs, backs up, restores and upgrades with only its own infrastructure |

**Cutover note.** The dev Neon project keeps its current `hub.*` schema and Data API until Phase 4 works; nothing there is deleted without your say. There is no production data and no production deployment to migrate.

## 6. First week, if you approve D1 to D3

1. Create `apps/api` and `packages/api-contracts`; Fastify skeleton, `/health`, Zod config loader that refuses to start without its secrets.
2. Drizzle schema and first migration for organisations, memberships, roles, permissions, audit; migration test on a Postgres container; CI workflow.
3. Dockerfile and a documented run against a throwaway Neon branch.
4. Write the permission catalogue and the five role templates as data with unit tests.

## 7. Risks

- **Hosting burden.** A studio must run a container with a domain and HTTPS. Mitigation: a reference deployment guide for one or two hosts, chosen by you; a single container, no extra services.
- **Scope.** Phases 2 and 3 are the bulk of the work and are security-sensitive; they need the integration tests before any client is switched.
- **Two auth layers on desktop** (local company sign-in plus cloud session) stay by design (offline sign-in); the plan keeps them separate.
- **Android device.** Nothing in the Android cloud path has run on a device yet; Phase 5 needs one.
- **Better Auth version.** §19.4 requires matching documentation to the exact version; pick and pin the version when Phase 2 starts and read that version's docs first.

## 8. Decisions needed

| # | Decision | Recommendation |
|---|---|---|
| D1 | Build the Myna API and stop using the Neon Data API from clients? | Yes |
| D2 | Desktop stays local-first and syncs through the API; cloud is authoritative for shared field data, desktop file for finance | Yes |
| D3 | Self-managed Better Auth in the API (replaces Neon-managed Auth; dev accounts are discarded) | Yes |
| D4 | Permission catalogue plus the five roles as system templates plus custom roles | Yes |
| D5 | API lives in this repository first (`apps/api`, `packages/api-contracts`) | Yes |
| D6 | Mobile cloud scope: assigned projects, tasks, site updates, photos, documents, notifications; finance and admin stay out | Yes |
| D7 | Structured conflicts replace last-writer-wins; money never merges | Yes |
| D8 | App name stays MynaHub Mobile | Yes |
| D9 | Mobile keeps its offline queue but release 1 limits it to the cloud scope above | Yes |
| D10 | Which reference host(s) to document for the container (for example one managed container host) | Your choice |

## 9. Decisions taken (2026-11-15) and Phase 1 status

The owner approved the recommendations D1 to D9. **D10:** the reference host is chosen in Phase 7; until then the container is described generically (any host that runs a Docker image, with HTTPS in front).

**Phase 1 (backend foundation), built in `apps/api`** (see its README). What was run:
- `npm run format`, `npm run lint`, `npm run typecheck`, `npm test` (28 tests: configuration, health and request handling, and the SQL migrations on a real PostgreSQL engine compiled to WebAssembly, PGlite), `npm run build`, `npm audit --omit=dev` (0 vulnerabilities): all passed.
- The built server, started as a real process: missing configuration exits with code 2 and names the variables only; `/health` answers; `/health/ready` returns 503 `DATABASE_UNAVAILABLE` for an unreachable database without leaking the password; `/openapi.json` is OpenAPI 3.1 and lists the routes.

**What the schema has:** `core.organizations`, `core.memberships`, `access.roles`, `access.permissions`, `access.role_permissions`, `access.membership_roles`, `studio.projects` (UUID, organisation scope, `version`, `deleted_at`, status and version constraints), `audit.events` (append-only: update, delete and truncate are refused by triggers). Two migrations, additive. The runtime role `myna_runtime` is granted data access only if it exists when migrating.

**Not run, so not claimed:** the Dockerfile and the container health check (no Docker on this machine); the CI workflow `.github/workflows/api.yml` (it has never executed); the node-postgres connection path and the migrate command against a real Postgres server (the tests use PGlite; CI is meant to cover the real server); anything against a real Neon project. The base image is pinned by tag, not by digest.

**Deviations from the plan, on purpose:**
- `packages/api-contracts` is not a separate package yet: the contract is the Zod schemas plus `/openapi.json` inside `apps/api`. It becomes a package when the Android and desktop clients first consume it (Phase 4 and 5).
- The `sync` schema and its tables (operation receipts, change feed) are added with the sync work (Phase 4 to 6), not now; migrations are additive so nothing is lost by waiting.
- Row level security is not enabled yet: it needs the per-transaction organisation context designed with the authorization engine (Phase 3). Until then the API has no business endpoints, so nothing is exposed.
- Node 24 is installed here; the container and CI target Node 22 (the current LTS line named in the guidelines). The package accepts Node 22 or newer. Dependency versions are whatever npm resolved today (for example Zod 4, TypeScript 6, Vitest 5) and are pinned by the lockfile.

**Next: Phase 2 (authentication).** Before any code, pin the Better Auth version and read that version's documentation (guidelines section 19.4); then the bootstrap token, first administrator, invitations, sessions and MFA, each with the security tests listed in the plan.

## 10. Phase 2 (authentication) status, 2026-11-15

**Built in `apps/api`** (Better Auth pinned to 1.7.25; its documentation for that version was read first, as section 19.4 asks): self-managed Better Auth in the `auth` schema with public sign-up disabled, bearer tokens for mobile and two-factor; a one-time bootstrap (setup token stored as a hash, single use, short expiry, advisory-locked, audited, closed for good once a company exists); employee invitations (hashed single-use tokens, expiring, revocable, one live token per person, only an Owner may invite an Owner); sessions (list, revoke one, revoke others, sign out; no cookie cache, so revocation is immediate); member suspension and reactivation (ends all sessions at once; cannot suspend yourself; only an Owner can suspend an Owner; the last Owner is protected; needs a fresh sign-in); a membership check on every protected request; administrator two-factor enforced on administrative endpoints; `GET /v1/me` and `/v1/me/permissions`; audit events for each of these. Permission keys so far: `organization.read`, `organization.members.manage`, `audit.read`; the seven default roles of the guidelines.

**Run:** `npm run format`, `lint`, `typecheck`, `test` (69 tests: 12 bootstrap, 11 invitations, 10 members and sessions, 4 two-factor with real TOTP codes computed in the test, a schema-drift test against the library's own table definitions, plus Phase 1), `build`, and `npm audit --omit=dev` (0 vulnerabilities after an override of a legacy esbuild inside `drizzle-kit`): all passed. The built server was started as a real process: it refuses to start without `BETTER_AUTH_SECRET`, `/openapi.json` lists the new routes, `/v1/me` without a session is 401, an auth route with the database down answers a generic 500, and neither the database password nor the secret reached the logs.

**Findings worth knowing:**
- Verifying a two-factor code **rotates the session**: the old token stops working and the new one arrives in the `set-auth-token` header. Clients (the mobile app in particular) must replace their stored token.
- A sign-in that still needs the second factor returns a `set-auth-token` header too, but that token is **not** a session (tested: it is refused everywhere). Do not store it.
- The environment refused my attempt to download and run the separate `auth` generator CLI (an unlisted package). I did not work around it: the Drizzle tables were written from the pinned library's own table definitions, and a test fails if they ever drift.
- `memberships.auth_user_id` is text while Better Auth user ids are UUIDs: joins cast explicitly (a real bug caught by a test).

**Not done / not verified:** email delivery and verification (Phase 7); the rate limits (configured, never tested); the node-postgres path, `npm run migrate` and `npm run bootstrap:token` against a real PostgreSQL server (no Postgres or Docker here; CI is meant to cover it and has not run); the Docker image; a role-change endpoint and the full permission catalogue (Phase 3); invitation acceptance by someone who already has an account (refused for now).

**Next: Phase 3 (authorization).** Permission catalogue package, the five desktop role templates alongside the guidelines' seven, custom roles, resource-level policy checks, and the per-transaction organisation context for row level security, each with cross-organisation and escalation tests.

## BYON setup CLI (2026-11-15)

`apps/api` now includes `npm run setup -- init|check|apply`; see
[byo-neon.md](byo-neon.md). It creates a new local environment with a random
signing secret without overwriting existing settings, validates matching
owner/runtime connection targets and least privilege, applies the shipped
migrations on explicit `apply`, grants read-only readiness metadata access, and
reports whether first-admin bootstrap is still needed. Applied migration hashes
must match this build; setup does not reset data or reopen bootstrap.

This is operator tooling, not completion of Phase 7. Cloud provisioning, a
graphical wizard, hosting, email and client API cutover remain pending. The
existing dev Data API path is retained. Verification and limits are recorded in
the BYON guide.
