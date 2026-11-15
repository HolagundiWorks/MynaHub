# Myna API

The company-owned backend of the Myna platform (see `docs/hub-platform-plan.md`). It sits in front of the company's own Neon PostgreSQL; MynaHub and MynaHub Mobile talk to it, never to the database. HolagundiWorks runs no part of it.

**Status: Phase 2 (authentication) built; see the limits below.** What exists: configuration loader, `/health` and `/health/ready`, request ids and one error format, OpenAPI 3.1 at `/openapi.json`, the schema (organisations, memberships, roles, permissions, projects, audit events, invitations, one-time setup tokens, and Better Auth's tables in the `auth` schema) as versioned migrations, a Dockerfile and a CI workflow, and the identity layer: self-managed Better Auth 1.7.25 (email and password, bearer tokens, two-factor), a one-time bootstrap, invitations, member suspension, sessions and a membership check on every protected request. **Not built yet:** the full permission catalogue and row level security (Phase 3), sync (Phases 4 to 6), files, any project or task endpoint.

## Run it

For a new company-owned Neon deployment, follow [the BYON setup guide](../../docs/byo-neon.md).
The `setup` CLI creates a local configuration with a random signing secret (`init`),
validates the two database roles and migration history without writes (`check`),
or applies shipped migrations and grants read-only readiness access (`apply`).
It does not provision cloud resources or switch the existing desktop/mobile clients to this API.

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

The server refuses to start on a missing or invalid value and names the variable, never its value.

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

This creates the organisation, the seven default roles and the Owner, and closes bootstrap for good (a second attempt, even with a fresh token, is refused). The Owner then signs in (`POST /api/auth/sign-in/email`), **turns on two-factor** (`POST /api/auth/two-factor/enable`, then `/two-factor/verify-totp`; verifying issues a new session token, so keep the one in the response), and can invite people: `POST /v1/invitations` returns a single-use token **once**; the person sends it to `POST /v1/invitations/accept` with their name and a password. Invitations last 7 days and can be revoked. `POST /v1/members/{id}/suspend` ends that person's sessions immediately.

### Limits of this phase (read before relying on it)

- **No email is sent.** The invitation token is shown to the administrator, who must pass it on. Email delivery and email verification arrive with the setup wizard (Phase 7); until then possession of the token is the only proof of identity.
- **Rate limits** (sign-in, two-factor, bootstrap, accept) are configured but switched off in the test environment, so they have **not been tested**; the store is in memory, so each container counts separately.
- **One company per deployment**; every request resolves the single organisation.
- Roles are assigned only at invitation; there is no screen or endpoint to change a role yet (Phase 3).
- Tested on PostgreSQL compiled to WebAssembly (PGlite). The node-postgres path, the migrate command and Better Auth over a real server are exercised only by the CI workflow, which has not run yet.

## Checks

```bash
npm run format && npm run lint && npm run typecheck && npm test
```

`npm test` applies the real SQL migrations to PostgreSQL compiled to WebAssembly (PGlite) and tests the API with injected requests. CI additionally migrates a real Postgres with the built CLI and starts the server (`.github/workflows/api.yml`).

## Changing the schema

Edit `src/db/schema/*.ts`, run `npm run db:generate`, review the SQL in `drizzle/`, and add a test. Migrations are additive; never edit an applied one. Hand-written SQL goes in a `--custom` migration (`npx drizzle-kit generate --custom --name=...`).
