# O-Studio packaging and signing

## Installer

NSIS per-user Setup.exe (no admin):

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

Output: `installer/O-Studio-Setup-<version>.exe` → `%LOCALAPPDATA%\Programs\O-Studio\` (`O-Studio.exe` at the root; `O-Studio.Launcher.exe` under `launcher\` with its own WinUI `resources.pri` so the two exes do not overwrite each other).

### SQLite (no separate install)

The company file (`.osas`) and the Rust engine use SQLite, but nothing asks the user to install SQLite on Windows.

| Layer | How it is bundled |
|---|---|
| Rust engine (`architect_office_desktop_lib.dll`) | `rusqlite` with the **`bundled`** feature — SQLite is linked into the DLL (`office-core`, `src-tauri`, `office-ffi`, `office-agent`). |
| WinUI monitor (`Osas.Core`) | `SQLitePCLRaw.bundle_e_sqlite3` — publishes **`e_sqlite3.dll`** next to `O-Studio.exe` for read-only company-file health checks. |

`build.ps1` calls `installer/sign.ps1` after NSIS. Without signing secrets the Setup
stays **unsigned** (exit 0). Configure:

| Mode | Variables |
|---|---|
| PFX | `WINDOWS_CERTIFICATE`, optional `WINDOWS_CERTIFICATE_PASSWORD` |
| Azure Trusted Signing | `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `AZURE_SIGNING_ACCOUNT`, `AZURE_CERT_PROFILE`, plus endpoint **or** `AZURE_SIGNING_REGION` |
| Dev only | `SIGN_SELF_SIGNED=1` |

Never commit production PFX files. With Azure secrets and no local NuGet cache,
`sign.ps1` downloads `Microsoft.Trusted.Signing.Client` automatically.

### Local unsigned vs signed smoke

```powershell
powershell -File installer\build.ps1
Get-AuthenticodeSignature installer\O-Studio-Setup-0.1.0.exe   # NotSigned

$env:SIGN_SELF_SIGNED = '1'
powershell -File installer\sign.ps1 installer\O-Studio-Setup-0.1.0.exe
# Do not ship self-signed builds
```

## Suite installer (O-Suite)

One **O-Suite-Setup-<version>.exe** installs or removes **O-Studio**, **O-CoreOp**, and **O-Draft** from a single wizard (component checkboxes, install and remove modes, shared assistant model task). Build order and silent flags: [installer/suite/README.md](installer/suite/README.md) and [docs/testing/TEST-ENVIRONMENT.md](testing/TEST-ENVIRONMENT.md).

```powershell
powershell -ExecutionPolicy Bypass -File installer\suite\build-single.ps1 -Version 1.3.0
```

Output: `installer/suite/out/O-Suite-Setup-<version>.exe` (per user, no administrator). O-Studio’s own NSIS Setup is embedded and run silently; O-CoreOp’s Inno Setup likewise; O-Draft is copied from the portable zip (the per-machine MSI is not used).

## Suite glue (legacy)

`installer/install-suite.ps1 -ArtifactsDir …` (in this repo; migrated from the OSAS hub)
picks up `O-Studio-Setup-*.exe` and optional sibling Setup.exe files. Sibling apps still
need `office.dll` from this repo’s `office-ffi` release build beside their install. O-Studio itself needs no `office.dll`: `architect_office_desktop_lib.dll` carries the same `office_*` interface, so the app ships one engine library.

**OSAS.exe is deprecated.** O-Studio is the suite shell (company choose/create, admin
password, users, launcher cards, approvals). Do not ship a separate OSAS Setup as the
company chooser. Older installs that still have OSAS.exe may write the same
`%LOCALAPPDATA%\OSAS\suite.json`; Studio is the preferred writer.

## Update channel (check-only)

Shared schema: `installer/channel/osas-channel.schema.json` (also migrated here).  
Package id: **`o-studio`**.

Set `OSAS_UPDATE_CHANNEL_URL`. Help → **Check for updates…** compares versions and
does not download installers yet.

## CI

- App CI: `.github/workflows/ci.yml` (Rust + WinUI build).
- Packaging: `.github/workflows/packaging-windows.yml` (`workflow_dispatch` or tag `v*`)
  installs NSIS, runs `installer/build.ps1`, maps shared signing secrets, uploads
  signed Setup or `…-unsigned.exe`.
- Jobs do not fail solely because secrets are missing.
- Ops enrollment (Azure Trusted Signing vs EV): Project doc `suite-installer-signing.md`.
