# Bring your own Neon (company-owned API setup)

Status: **API setup CLI implemented; desktop/mobile API cutover is pending.**
This is the setup path for the approved [platform plan](hub-platform-plan.md):
each practice owns its Neon database and hosts its Myna API container. Self-managed
Better Auth runs inside that API. Clients will use the company's API address and
their own sessions. Neon-managed Auth and the Data API are not required for this path.

The API currently implements authentication. Authorization, business endpoints,
API sync and client cutover remain separate phases. Setting up this backend does
not enable the current clients to sync through it. Keep the existing dev `hub.*`
schema and accounts until cutover is verified; this tool never drops them.

## Set up the backend

1. Create your own Neon project and a development branch. Use a dedicated database
   for this API. Copy the schema owner's **direct** PostgreSQL connection URL into
   your password manager. No Neon management API key is needed by the setup CLI.
2. In the branch's SQL Editor, create the runtime role with a generated password:

   ```sql
   CREATE ROLE myna_runtime LOGIN PASSWORD '<a new random password>';
   ```

   Create it through SQL, rather than as an administrator through the console.
   It must have no role memberships, administrative privileges or object ownership.
   Copy a connection URL for this role and the same endpoint/database; a pooled
   runtime URL is supported. Require TLS (`sslmode=require` or `verify-full`) in
   both URLs. The owner URL must not use the `-pooler` host.
   See Neon's [role privileges](https://neon.com/docs/manage/roles) and
   [connection pooling](https://neon.com/docs/connect/connection-pooling).
3. From `apps/api`, install the locked dependencies and build:

   ```sh
   npm ci
   npm run build
   npm run setup -- init
   ```

   `init` creates `.env` with a new 48-byte random signing secret and placeholders
   for the URLs. It refuses to overwrite an existing file. Set `PUBLIC_API_URL`
   to your future HTTPS API address, `DATABASE_URL` to the runtime URL and
   `DIRECT_DATABASE_URL` to the schema-owner URL. Edit these privately; never
   paste them into chat, logs or source control. Keep `REQUIRE_ADMIN_MFA=true`.
   Restrict file access to the operator's OS account (Unix creation mode is 0600;
   on Windows check the directory/file ACL). Existing process variables override
   `.env`, so clear stale settings before setup.
4. Back up an existing database before applying migrations, then run:

   ```sh
   npm run setup -- apply
   npm run setup -- check
   ```

   `apply` first validates the connection targets and runtime role. It rejects
   changed or unknown applied migration hashes before running the shipped
   migrations. It grants the runtime role read-only access to the migration
   journal for readiness, then checks table access and account state. Repeating
   it preserves existing company accounts and the signing secret. It does not
   create roles, rotate passwords, mint tokens, reset data or provision Neon.
   `check` runs only catalog/data reads and never applies migrations or grants.
   Both commands return exit code 2 on failure and suppress private database
   error details. If a migration fails, inspect privately; earlier migrations
   may already be applied. There is no automatic rollback or reset.
5. Deploy the built API behind HTTPS using the [API README](../apps/api/README.md)
   and Dockerfile. Inject runtime settings only: **omit `DIRECT_DATABASE_URL`**
   from the running server. Keep the signing secret stable in your host's secret
   store. To run setup in the container, inject both URLs into a separate,
   short-lived job and run `node dist/setup-cli.js apply` (or `check`).
6. Generate a first-admin token in a private operator terminal:

   ```sh
   node --env-file=.env dist/bootstrap-cli.js
   ```

   The token is intentionally shown once. Submit it with company/admin details
   to `POST /v1/bootstrap` over HTTPS, following the API README. Then sign in,
   enable and verify two-factor authentication, and invite employees. Keep the
   replacement session token returned after two-factor verification. Re-running
   setup reports an existing company; it does not reopen bootstrap.
7. Verify `/health/ready`, first-admin sign-in, MFA and invitation acceptance on
   your deployment. Do not configure the released desktop/phone to use this API
   yet: their transports still use the legacy endpoints. The planned runtime
   company-address setting is described in the Android repo's
   `docs/platform-migration.md`.

If a role check fails, review privileges privately with the schema owner. On
older PostgreSQL databases, `PUBLIC` may have `CREATE` on the public schema;
remove it only after checking who else uses that dedicated database. Setup
deliberately refuses such a role and never revokes another application's access.
If a schema was migrated before creating `myna_runtime`, review the shipped
grants described in the API README; re-running migrations will not replay them.

## Verification and limits

Setup tests use PGlite for real PostgreSQL catalog, grants and migration-hash
checks, plus configuration and exclusive-file-creation tests. Real PostgreSQL
CI now runs `setup apply` twice, `check`, readiness and the first-admin flow with
the restricted runtime role. That workflow has not been run in this session.
Run locally: 91 tests passed, including the CLI initialization/overwrite smoke
test; lint, type checking and build passed. Formatting was checked with
`npm run format -- --end-of-line auto` because this Windows checkout has CRLF
line endings; the unqualified formatting check rejects those existing endings.
The initial implementation did not exercise a live Neon project. The live
development setup below was verified subsequently. Docker deployment, a
graphical wizard, automated Neon project provisioning, reference hosting,
email delivery and desktop/mobile API cutover are still pending.

### Live development setup verification (2026-11-15)

Using the Neon MCP connection, created a fresh `mynahub` project (PostgreSQL 18,
AWS Singapore), an empty `production` branch and a `dev` branch. On `dev` only,
created `myna_runtime` through SQL, saved generated credentials in the ignored
local API environment, and ran `setup apply` twice followed by `setup check`.
All passed: migration hashes, runtime-role isolation, table permissions and
read-only access to readiness metadata. The owner uses the direct endpoint;
the runtime uses its pooled counterpart. Both use `sslmode=verify-full`.

Started the built API on loopback with the runtime credential and without the
schema-owner variable in its process environment. `/health` and `/health/ready`
returned 200 (database OK, migrations current); `/v1/me` without a session
returned 401; `/openapi.json` returned 200 with OpenAPI 3.1.0. Stopped that local
smoke-test server afterward. Confirmed through MCP that `production` still has
zero application tables and `dev` has the five API schemas plus the migration
journal. No Neon-managed Auth or Data API was enabled.

Subsequently, with the owner's confirmation, created the first development
company and Owner through the running API with a one-time bootstrap token.
Verified password sign-in and the authenticated Owner role, administrative
requests blocked with `MFA_REQUIRED` before enrollment, refusal to mint another
bootstrap token, and sign-out revoking the verification session. The generated
password was saved only in the ignored local `.env.owner.json`; no email was
sent. Personal account details and credentials are intentionally absent here.

Actual MFA enrollment and second-factor sign-in, invitations, Docker, public
HTTPS hosting and Android-device use have not been exercised against live Neon.
The account rejected an explicit custom suspend interval, so project creation
kept the account's default instead.

### Separate demo fixtures (2026-11-15)

Created a dedicated `demo` branch from empty production, not from the development
company. Its **Sample Architects (Demo)** company has one active account for each
of the seven current cloud role templates and four fictional project fixtures.
Accounts were created through bootstrap/invitation handlers backed by real Neon.
Verified each role's sign-in, identity and administrative permission boundary.
Owner and Administrator enrolled and verified TOTP; bootstrap is closed. Passwords,
TOTP setup URIs and backup codes are only in the ignored local
`apps/api/.env.demo-credentials.json`; branch settings are in `.env.demo`.

Sign-in limits stayed enabled: a large batch hit HTTP 429; remaining users passed
in a smaller batch. Injected API requests also reported a shared IP bucket when
no client IP was available. Trusted proxy/IP handling needs verification with the
chosen real host; this fixture run does not establish production rate limiting.
The four projects were seeded directly as database fixtures because business
project routes are not implemented yet.

Also ran the existing `Adms.DemoCompany` generator locally without legacy cloud
sync. It created a desktop `.osas` with five local role users, three clients, four
projects, nine tasks, stages and a fee proposal. Native-engine checks passed for
all five logins and project access, the fixture counts, and denial of accounting
periods to the document editor. The desktop window was not driven. The local and
cloud credentials are separate and the new API is not wired into desktop/mobile.
Artifacts and start instructions are under ignored
`docs/testing/.local/mynahub-demo/`; nothing confidential belongs in source control.

## Legacy dev sync setup (Neon-managed Auth and Data API)

The following checklist documents the existing dev sync proof. It is retained
for that path and must not be used to configure the new Myna API.

Status: **a guide plus two helpers, not a one-click installer.** Written 2026-11-15 from what was done by hand on the maintainer's Neon dev project. MynaHub is open source and has **no shared cloud**: every practice creates its own Neon project and owns it. Nothing in this repository points at anyone's project; the only things MynaHub needs from you are two **public** web addresses. No database password, API key or token is ever put in the app or committed.

Cloud sync is optional. Without it MynaHub works exactly as before, offline, on one PC.

## What you end up with

One Neon project of yours holding: Neon Auth (the cloud accounts), the Neon Data API (how MynaHub and MynaHub Mobile read and write), and a `hub` schema with row level security (who may see which company). Your people sign in with a cloud account; the **owner** of a company decides who is a member and with which role.

## Steps (the order matters)

1. **Neon account and project.** Create a project at neon.com in a region near you. Keep the default branch (`production`). Optional but wise: create a second branch `dev` and try everything there first.
2. **Neon Auth.** On the branch, turn on Neon Auth (Managed Better Auth) with email and password. Copy its **Auth URL**.
3. **Neon Data API.** On the branch's database turn on the Data API with Neon Auth as the provider. **Do not** let it grant everything to signed-in users. Expose only the schema `hub` (add it in the Data API settings; it appears after step 4, then refresh). Copy its **Data API URL**.
4. **Apply the schema.** Run [`cloud/sql/0001_hub_sync.sql`](../cloud/sql/0001_hub_sync.sql) once on that database (Neon SQL Editor, or `psql`). It is additive and safe to run again. It must come after step 3 because it uses the Data API's `authenticated` role and `auth.user_id()`.
5. **Create the accounts.** Each person needs a cloud account (email and password) before the owner can add them. See "Open problems", the first one.
6. **Switch off public sign-up** in the Neon Auth settings once your accounts exist. Until then anyone who finds the Auth URL can create an account (they still see nothing, because company access is by membership, but do not leave it open).
7. **Tell MynaHub the two addresses.** Put them in `%LOCALAPPDATA%\O-Studio\neon.env`:
   ```
   NEON_AUTH_BASE_URL=<your Auth URL>
   NEON_DATA_API_URL=<your Data API URL>
   ```
   (Suite > Cloud sync > Load env file merges them for you.) Never commit `neon.env`.
8. **Check it.** `cloud/setup/check-neon.ps1` signs in with one of your accounts and verifies the setup without changing anything: Auth reachable, company data not readable without signing in, the `hub` schema exposed, records not directly writable. Run it before you trust the setup.
9. **Use it.** In MynaHub: Suite > Cloud sync > sign in to the cloud > Link this company to the cloud (company owner only) > Sync company now. On the phone (MynaHub Mobile): Practice > Cloud sync > sign in > join the company. The owner adds each person with a role through the `set_member` function (no screen for it yet, see below).

## Try it with a demo company first

`native/Adms.DemoCompany` creates a **fictional** sample company file with one demo user per role (owner, principal, accounts, document editor, auditor), four projects with stages and tasks, three clients and a proposal. Passwords are random per run and are written only to a `demo-credentials.txt` next to the file. With `--cloud` (and your cloud account in `MYNA_DEMO_CLOUD_EMAIL` / `MYNA_DEMO_CLOUD_PASSWORD`) it also links and syncs the demo company to your own Neon. See the tool's header for the command.

## Open problems (honest list)

1. **No way to create a cloud account from the apps.** Neither the desktop nor the phone has a "create account" screen; the maintainer created accounts by calling the Neon Auth sign-up address while sign-up was still open. Options: (a) an owner-only "Create cloud account" button used while sign-up is temporarily on; (b) Neon Auth's organization invitations (the plugin is on, invitation email is off); (c) the Neon console's user list. Which one is a decision for the project owner; until then, step 5 is manual.
2. **The phone has the addresses baked in at build time** (`local.properties` into `BuildConfig`). A shared APK cannot serve many practices. Proposed fix: a runtime "Cloud address" setting, filled by scanning a QR code or pasting a pairing text that the desktop shows. Not built.
3. **Adding a member has no screen.** `hub.set_member(company, email, role)` works (tested) but is called by hand. A Members page for the owner is the natural next step.
4. **Sign-in origin.** The apps send `http://localhost:3000` as their origin, which works because Neon Auth's "allow localhost" is on. Before relying on this for real data, decide the trusted-origin story (a custom scheme for the phone, a fixed loopback port for the desktop).
5. **Email.** Verification is off and Neon's shared sender is used. `set_member` by email therefore trusts that the invited address belongs to the person. Turn verification on (and use your own sender) before adding people you do not know.
6. **No one-command provisioning.** Creating the project, branches, Auth and Data API through the Neon API with your API key could be scripted, but it was not written because it cannot be tested without a key. The checklist above is what to follow.
7. **Neon plan limits** (storage, compute hours, scale-to-zero cold starts of a second or two) were not measured.

## What was verified, and how

On the maintainer's dev branch, in the same order as above (project, branch, Auth, Data API, schema file, sign-up off): schema applied; 22 isolation checks (`cloud/sql/tests/rls-check.ps1`); two company files plus a phone-side run syncing through it (`native/Adms.SyncCheck`). `check-neon.ps1` was run against that branch (all checks passed). The Neon console steps (1 to 3, 6) were done through Neon's own management tools, not by clicking the console, so button names there may differ from the words above. A brand-new third-party project was **not** set up from this guide end to end.

## Company API project authorization (2026-11-15)

The demo branch now includes migrations 0005 through 0007 for scoped project reads, assignments, role management and versioned online edits. See [project authorization](project-authorization.md) and [project edits](project-edits.md) for permissions, runtime RLS boundaries and verification. Production remains uninitialized; the development branch still requires these migrations before using the updated API. Mobile supports assigned summaries and authorized online edits; offline sync remains pending.
