# Kivo and Neon (cloud sync)

**Project files stay on this PC (decided 2026-11-08).** Tren and Falcon share the local `.osp`; nothing in it is pushed to Neon. Only Kivo is synced, and project data reaches the company database only through the CSV that Falcon exports and Kivo imports. Pushing the project file itself is off; `OSAS_PUSH_PROJECT_FILES=1` turns it back on for a machine that should do it (`Osas.Core.ProjectFilePolicy`).

The rest of this page describes the opt-in mode. Cloud sync for `.osp` project files was **Kivo only** — see [ODP-PROJECT-FILE-AND-SYNC.md](ODP-PROJECT-FILE-AND-SYNC.md). Tren and Falcon keep opening the local `.osp`; they never store Neon keys.

This document explains how to **create** a Neon project, **connect** it to the Kivo repository, and **smoke-test** the four services before any WinUI “Cloud sync” UI exists.

---

## What lives where

| Layer | Role |
| --- | --- |
| **Neon Console / CLI** | Org, project, branch, Postgres, Object Storage, Functions, Realtime |
| **Repo root** | `neon.ts` (what to deploy), `.neon` (linked org/project/branch), `cloud/` (sync schema + verify) |
| **`.env.local`** | Connection strings and secrets on **your machine only** (gitignored). Copy from [`.env.example`](../.env.example). |
| **Launcher + WinUI Kivo** | **Myna** runs the background sync (push every 2 s, Postgres `LISTEN` pull) from sign-in on. **Kivo, Suite → Cloud sync**: test connection, enable sync per project, push or pull now. Credentials: `%LocalAppData%\\OSAS\\neon.env` (copy from repo `.env.local`). |

**Testing credentials:** A shared test project/password is fine for experiments. Put values only in `.env.local` or a password manager — never commit them, paste them in issues, or check them into the company file.

---

## 1. Create a Neon project

Pick one path.

### A. Neon Console (browser)

1. Sign in at [console.neon.tech](https://console.neon.tech).
2. Create an **organization** (or use an existing one).
3. **New project** — choose a region with Object Storage and Realtime (for example `ap-southeast-1` on AWS).
4. Note **Organization ID**, **Project ID**, and the default branch name (often `production` or `main`).

Enable **Realtime** on that branch if the console offers it (Settings → Realtime). Object Storage and Postgres are on by default for new projects in supported regions.

### B. Neon CLI (same repo you develop in)

```powershell
npm i -g neon@latest    # or: npx neon@latest … for one-off commands
cd D:\Work Development\Repos\O-STUDIO
neon login              # completes in the browser; required before deploy
```

Create and link in one step (replace IDs from the console):

```powershell
neon init -y --agent cursor `
  --org-id org-soft-meadow-34349055 `
  --project-id delicate-butterfly-16013319 `
  --branch production `
  --services postgres `
  --services object-storage `
  --services functions `
  --services realtime
```

Important: repeat **`--services`** for each service. A single comma-separated value is treated as one invalid name.

Or create a **new** project instead of linking:

```powershell
neon init -y --agent cursor `
  --org-id <your-org-id> `
  --project-name o-studio-sync `
  --region-id aws-ap-southeast-1 `
  --branch production `
  --services postgres --services object-storage --services functions --services realtime
```

That writes:

- **`.neon`** — `{ orgId, projectId, branch }`
- **`neon.ts`** — buckets, functions, Realtime flag (edit before deploy if you change names)
- **`package.json`** / **`cloud/`** — if not already present

---

## 2. Declare infrastructure (`neon.ts`)

The repo’s [`neon.ts`](../neon.ts) tells Neon what to provision on **`neon deploy`**:

| Declaration | Purpose for Kivo |
| --- | --- |
| Postgres (always) | Sync metadata: `osp_project`, `osp_project_revision`, `osp_markup` ([`cloud/src/schema.ts`](../cloud/src/schema.ts)) |
| `buckets.uploads` | Private bucket for `.osp` snapshots (`S3_BUCKET=uploads`) |
| `functions.hello` | Deploy smoke test ([`hello.ts`](../hello.ts)) |
| `preview.realtime` | Live revision notifications (experimental; see troubleshooting) |

After editing `neon.ts`:

```powershell
neon deploy
```

Deploy applies changes to the linked branch and **pulls env vars into `.env.local`** (unless you use `--no-env-pull`).

---

## 3. Connect env vars on your PC

### From `neon deploy` (recommended)

```powershell
neon deploy
npm install
```

You should get (names may vary slightly by CLI version):

| Variable | Use in Kivo |
| --- | --- |
| `DATABASE_URL` / `DATABASE_URL_UNPOOLED` | Direct Postgres (migrations, Realtime DB name) |
| `DATABASE_URL_POOLED` | App traffic (pooler host) — preferred in `cloud/` verify |
| `NEON_BRANCH` | Branch name (`production`) |
| `AWS_*` + `S3_BUCKET` | Neon Object Storage (S3-compatible API) |
| `NEON_FUNCTION_*_BASE_URL` | HTTPS base URL for deployed functions |
| `NEON_REALTIME_URL`, `NEON_REALTIME_SECRET` | Server-side Realtime only — never ship to WinUI or sibling apps |

### Manual / test-only fill-in

Copy [`.env.example`](../.env.example) to **`.env.local`** and paste values from the Neon Console (**Connection details**, **Storage**, **Realtime**). For a **testing-only** shared project, that file stays on your machine; rotate secrets if they leak.

Optional: run without a file on disk:

```powershell
neon run npm start
```

The CLI injects the same vars for one command.

---

## 4. Prove the connection (before WinUI)

From the **O-STUDIO repository root**:

```powershell
npm run build
npm run start
```

Expected lines:

```text
[postgres] ok — 3 osp_* tables ready
[object-storage] ok — bucket "uploads" read/write
[functions] ok — hello responded
[realtime] ok — …   # or upstream_rejected — see below
```

This runs [`cloud/src/verify.ts`](../cloud/src/verify.ts): creates sync tables if missing, round-trips a file in **`uploads`**, calls the **hello** function, and tries a Realtime `select 1`.

---

## 5. How Kivo the **app** connects

Built in WinUI (**Suite → Cloud sync**). Architecture: [SUITE.md](SUITE.md) and [ODP-PROJECT-FILE-AND-SYNC.md](ODP-PROJECT-FILE-AND-SYNC.md).

1. **Only Kivo** holds Neon credentials (`%LocalAppData%\\O-Studio\\neon.env` or `OSTUDIO_NEON_ENV_FILE`).
2. **Suite → Cloud sync:** list company projects that have a `project.osp` path, turn sync on per project, **Push now** / **Pull now**, background push every 2s and pull on Postgres `NOTIFY osp_revision`.
3. **Test connection** checks Postgres schema and Object Storage (`S3_BUCKET`, default **`uploads`** from [`neon.ts`](../neon.ts)).
4. **Cloud ahead:** when push fails because another device uploaded first, Studio offers **Take cloud copy** or **Back up local, then pull** (sibling `.osp` backup file).
5. **Tren / Falcon** unchanged: local `.osp` only; they detect new revisions via existing polls.

Developers without WinUI can still run **`npm run verify`** (same services as the app connector uses).

---

## 6. Troubleshooting

| Symptom | What to do |
| --- | --- |
| `neon login` / browser timeout | Run `neon login` again, finish in the browser, retry `neon deploy`. |
| Missing env vars | `neon deploy` from repo root; confirm `.neon` points at the right project. |
| Storage 403 / wrong bucket | Set `S3_BUCKET` to the bucket name in `neon.ts` (`uploads`). Redeploy if you added buckets. |
| Realtime **`upstream_rejected`** | Env is often correct; Postgres/Storage/Functions still OK. Try `neon realtime subscribe "select 1 as ok" --branch production`. If CLI fails too, fix Realtime on the branch in Console or contact Neon support. |
| `NEON_REALTIME_URL` is `https://` | The verify script upgrades to `wss://` automatically. |

---

## 7. Cursor / agents

Enable the **neon-postgres** plugin in [`.cursor/settings.json`](../.cursor/settings.json). See [`.cursor/rules/neon-cloud-sync.mdc`](../.cursor/rules/neon-cloud-sync.mdc).

Official Neon agent skills: `neon skills -s neon --agent cursor -y` (run once per machine).

---

## Related docs

- [ODP-PROJECT-FILE-AND-SYNC.md](ODP-PROJECT-FILE-AND-SYNC.md) — `.osp` format and sync protocol
- [SUITE.md](SUITE.md) — suite roles and “machine → machine” link
- [packaging.md](packaging.md) — SQLite embedded in the installer (company file ≠ Neon)
