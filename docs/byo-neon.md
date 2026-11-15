# Bring your own Neon (setting up cloud sync for your practice)

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
