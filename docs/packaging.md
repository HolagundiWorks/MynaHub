# O-Studio packaging and signing

## Installer

NSIS per-user Setup.exe (no admin):

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

Output: `installer/O-Studio-Setup-<version>.exe` → `%LOCALAPPDATA%\Programs\O-Studio\`.

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

## Suite glue

OSAS `installer/install-suite.ps1 -ArtifactsDir …` picks up `O-Studio-Setup-*.exe`.
The launcher also needs `office.dll` from this repo’s `office-ffi` release build.

## Update channel (check-only)

Shared schema: OSAS `installer/channel/osas-channel.schema.json`.  
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
