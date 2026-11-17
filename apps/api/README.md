# Myna API

The company-owned backend of the Myna platform (see `docs/hub-platform-plan.md`). It sits in front of the company's own Neon PostgreSQL; MynaHub and MynaHub Mobile talk to it, never to the database. HolagundiWorks runs no part of it.

**Status: authentication, project authorization, live Mobile projects/tasks and online project/task edits built.**
Authentication, bootstrap/invitations, sessions and administrator MFA are joined
by custom role management, system templates, scoped project reads and project
assignments, with transaction-local project RLS. Authorized online edits add
expected-version checks and immutable retry receipts. See
[project-authorization.md](../../docs/project-authorization.md) for the endpoint
matrix, role scopes and verification, and [project edits](../../docs/project-edits.md).
Project creation and confirmed deletion are implemented with version checks,
immutable receipts and tombstones. See [project lifecycle](../../docs/project-lifecycle.md).
Metadata-table RLS, other live entity APIs, desktop
integration and files remain pending; this is not full Phase 3 completion.

## Run it

For a new company-owned Neon deployment, follow [the BYON setup guide](../../docs/byo-neon.md).
The `setup` CLI creates a local configuration with a random signing secret (`init`),
validates the two database roles and migration history without writes (`check`),
or applies shipped migrations and grants read-only readiness access (`apply`).
It does not provision cloud resources or switch the desktop/legacy business-sync transports to this API.

Mobile account integration discovers `/v1/mobile/connection` before sending
credentials. It returns API version 1, minimum app version code 8, the actual
Better Auth base path and implemented capabilities (identity, invitations,
sessions, two-factor, project reads and online edits). The public contract artifact is
`contracts/mobile-connection-v1.json`; its test must match the shipped metadata.
The Android client consumes this contract. Project reads are assignment-scoped;
Mobile subscribes to `GET /v1/mobile/projects/live` for automatic authorized
project updates and selected-project task pages. Task creation/editing requires
tasks.write and current project access, with idempotent retry and version-conflict
review. See [task mutations](../../docs/task-mutations.md) and
[live projects](../../docs/mobile-live-projects.md). Mobile
backup/restore and offline company storage are removed by the owner's decision.
Other business APIs and desktop integration remain pending.

Needs Node 22 or newer and a PostgreSQL database (Neon).

```bash
npm ci
cp .env.example .env        # fill in; never commit .env
npm run build
npm run migrate             # with DIRECT_DATABASE_URL (the schema owner)
npm start                   # with DATABASE_URL (the runtime role)
```

These commands expect variables injected by the host. For a local `.env`, use
`node --env-file=.env dist/server.js` and `node --env-file=.env dist/bootstrap-cli.js`.
`npm run setup` loads `.env` itself (existing process variables take precedence).

| Variable                                | Needed                | Meaning                                                                |
| --------------------------------------- | --------------------- | ---------------------------------------------------------------------- |
| `PUBLIC_API_URL`                        | yes                   | The address clients use. Must be `https://` when `NODE_ENV=production` |
| `DATABASE_URL`                          | yes                   | Runtime connection, role `myna_runtime`                                |
| `DIRECT_DATABASE_URL`                   | for `npm run migrate` | Schema owner connection; keep it out of the server's environment       |
| `PORT`, `HOST`, `LOG_LEVEL`, `NODE_ENV` | no                    | Defaults 3000, 0.0.0.0, info, production                               |

The server refuses to start on a missing or invalid value and names the variable, never its value. It also refuses an environment containing `DIRECT_DATABASE_URL` and verifies that the connected runtime role has no ownership, elevated privileges or inherited roles before listening. Keep migration settings in the operator environment; inject only runtime settings into the server.

### Two database roles (guidelines section 7.5)

Create the runtime role **before** migrating, so the migration can grant it data access (it only runs the grants if the role exists):

```sql
CREATE ROLE myna_runtime LOGIN PASSWORD '<a long random password>';
```

The runtime role can read and write data tables, can only insert and read `audit.events`, and owns nothing. Create it before migrating so all shipped grants apply. If it already exists without those grants, review both `drizzle/0001_guards.sql` and `drizzle/0004_phase2_guards.sql` with the schema owner. `npm run setup -- apply` additionally grants `USAGE` on `drizzle` and `SELECT` on its migration journal, which `/health/ready` needs. It never grants writes to that journal.

## First start: create the company and its administrator

No default account exists and public sign-up is off. After migrating:

```bash
npm run bootstrap:token            # prints a one-time setup token ONCE (valid 60 minutes, single use)
```

Then, from the person who will own the company:

```bash
curl -X POST https://api.company.example/v1/bootstrap -H "content-type: application/json" -d '{
  "setupToken": "<the token>", "organizationName": "Sample Architects", "slug": "sample-architects",
  "adminName": "Asha Owner", "adminEmail": "owner@company.example", "adminPassword": "<12 or more characters>" }'
```

This creates the organisation, eleven system role templates and the Owner, and closes bootstrap for good (a second attempt, even with a fresh token, is refused). The Owner then signs in (`POST /api/auth/sign-in/email`), **turns on two-factor** (`POST /api/auth/two-factor/enable`, then `/two-factor/verify-totp`; verifying issues a new session token, so keep the one in the response), and can invite people: `POST /v1/invitations` returns a single-use token **once**; the person sends it to `POST /v1/invitations/accept` with their name and a password. Invitations last 7 days and can be revoked. `POST /v1/members/{id}/suspend` ends that person's sessions immediately.

### Limits of this phase (read before relying on it)

- **No email is sent.** The invitation token is shown to the administrator, who must pass it on. Email delivery and email verification arrive with the setup wizard (Phase 7); until then possession of the token is the only proof of identity.
- **Rate limits** are configured and a live oversized sign-in batch was refused with 429. Full rate-limit/proxy behavior remains unverified; the store is in memory, so each container counts separately.
- **One company per deployment**; every request resolves the single organisation.
- Roles can be assigned at invitation and through the protected role-management endpoints; owner management screens are still pending.
- Automated tests use PGlite. Manual checks also passed setup/migrations, authentication, scoped project reads and runtime RLS on the real Neon demo branch; see the authorization guide. The CI workflow and Docker deployment have not run here.

## Checks

```bash
npm run format && npm run lint && npm run typecheck && npm test
```

`npm test` applies the real SQL migrations to PostgreSQL compiled to WebAssembly (PGlite) and tests the API with injected requests. CI additionally migrates a real Postgres with the built CLI and starts the server (`.github/workflows/api.yml`).

## Changing the schema

Edit `src/db/schema/*.ts`, run `npm run db:generate`, review the SQL in `drizzle/`, and add a test. Migrations are additive; never edit an applied one. Hand-written SQL goes in a `--custom` migration (`npx drizzle-kit generate --custom --name=...`).

Company site records now support scoped live instruction/inspection/snag pages, online creation/editing and confirmed deletion. See [site records](../../docs/site-records.md). Private project attachments are implemented and demo verified; production rollout remains pending. See [attachments](../../docs/project-attachments.md).

## Neon Functions hosting

The Fetch entry and `npm run build:function` prepare this API for Neon Functions. Publication is deferred at the owner's request; see [deployment preparation](../../docs/neon-functions.md) for branch binding, private environment generation and remaining production checks.
