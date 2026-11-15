# Myna API

The company-owned backend of the Myna platform (see `docs/hub-platform-plan.md`). It sits in front of the company's own Neon PostgreSQL; MynaHub and MynaHub Mobile talk to it, never to the database. HolagundiWorks runs no part of it.

**Status: Phase 1 (backend foundation).** What exists: configuration loader, `/health` and `/health/ready`, request ids and one error format, OpenAPI 3.1 at `/openapi.json`, the first schema (organisations, memberships, roles, permissions, projects, audit events) as versioned migrations, a Dockerfile and a CI workflow. **Not built yet:** authentication (Phase 2), authorization and row level security (Phase 3), sync (Phases 4 to 6), files, any business endpoint.

## Run it

Needs Node 22 or newer and a PostgreSQL database (Neon).

```bash
npm ci
cp .env.example .env        # fill in; never commit .env
npm run build
npm run migrate             # with DIRECT_DATABASE_URL (the schema owner)
npm start                   # with DATABASE_URL (the runtime role)
```

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

The runtime role can read and write data tables, can only insert and read `audit.events`, and owns nothing. If you create it after migrating, run the grants at the end of `drizzle/0001_guards.sql` by hand.

## Checks

```bash
npm run format && npm run lint && npm run typecheck && npm test
```

`npm test` applies the real SQL migrations to PostgreSQL compiled to WebAssembly (PGlite) and tests the API with injected requests. CI additionally migrates a real Postgres with the built CLI and starts the server (`.github/workflows/api.yml`).

## Changing the schema

Edit `src/db/schema/*.ts`, run `npm run db:generate`, review the SQL in `drizzle/`, and add a test. Migrations are additive; never edit an applied one. Hand-written SQL goes in a `--custom` migration (`npx drizzle-kit generate --custom --name=...`).
