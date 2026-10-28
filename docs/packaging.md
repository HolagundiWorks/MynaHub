# O-Studio packaging and signing

## Installer

NSIS per-user Setup.exe (no admin):

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

Output: `installer/O-Studio-Setup-<version>.exe` → `%LOCALAPPDATA%\Programs\O-Studio\`.

`build.ps1` calls `installer/sign.ps1` after NSIS. Without signing secrets the Setup
stays **unsigned** (exit 0). Configure `WINDOWS_CERTIFICATE`, Azure Trusted Signing,
or `SIGN_SELF_SIGNED=1` (dev only). Never commit production PFX files.

## Suite glue

OSAS `installer/install-suite.ps1 -ArtifactsDir …` picks up `O-Studio-Setup-*.exe`.
The launcher also needs `office.dll` from this repo’s `office-ffi` release build.

## Update channel (check-only)

Shared schema: OSAS `installer/channel/osas-channel.schema.json`.  
Package id: **`o-studio`**.

Set `OSAS_UPDATE_CHANNEL_URL`. Help → **Check for updates…** compares versions and
does not download installers yet.

## CI notes

- Windows runner for NSIS + `dotnet publish` of the WinUI app.
- Upload `-unsigned` artifacts when secrets are absent; do not fail the job for that.
- Ops checklist: Project doc `suite-installer-signing.md`.
