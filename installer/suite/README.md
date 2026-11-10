# The Myna installers

Three installers, each complete on its own (per user, no administrator). There is no combined suite installer any more (retired 2026-11-10): the apps are independent of each other.

| Installer | Installs | Install folder | Start menu |
|---|---|---|---|
| `Myna-Setup-<version>.exe` | **MynaHub** and the **Myna launcher** (AI Core host, tray icon) | `%LOCALAPPDATA%\Programs\O-Studio` | folder **Myna**: MynaHub, Myna |
| `MynaOps-Setup-<version>.exe` | **MynaOps** (quantities, estimates, billing); optional task: the assistant model | `%LOCALAPPDATA%\Programs\O-CoreOp` | folder **MynaOps** |
| `MynaCAD-Setup-<version>.exe` | **MynaCAD** (drafting) | `%LOCALAPPDATA%\Programs\O-Draft` | folder **MynaCAD** |

The install folders keep their old names on purpose (identifiers), so an upgrade replaces an earlier install in place. Each Setup removes the shortcuts earlier Setups made under the old names (Kivo, Tren, Falcon, O-Draft).

## File types

Double-clicking a project file (`.osp`) opens it in MynaCAD (registered by `MynaCAD-Setup`). `MynaOps-Setup` adds the right-click verb **Open in MynaOps**. Neither starts Myna. Removing MynaCAD removes the association it made.

## Removing

Settings > Apps lists **Myna (MynaHub and the Myna launcher)**, **MynaOps** and **MynaCAD**; each removes only itself. Company files (`.osas`), projects (`.osp`) and the assistant model are never deleted.

## Silent command lines

```text
Myna-Setup-<v>.exe /S
MynaOps-Setup-<v>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="" /LOG="%TEMP%\ops-install.log"
MynaCAD-Setup-<v>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="%TEMP%\cad-install.log"
```

Use `/TASKS="model"` with `MynaOps-Setup` to download the assistant model (Olmo 3 7B, 4.5 GB, SHA-256 checked). From Git Bash set `MSYS_NO_PATHCONV=1` or the slashes are rewritten.

## Building

`build-all.ps1` rebuilds all three from the sibling working trees (`O-STUDIO`, `O-COREOP`, `O-DRAFT-main`; commit or stash edits you do not want packaged) and writes the three Setups plus `Myna-installers-<version>.manifest.json` (git commits and SHA-256) to `out/`.

```powershell
powershell -ExecutionPolicy Bypass -File installer\suite\build-all.ps1 -Version 1.7.0
```

Needs the MSVC build tools (vcvars64), NSIS (the tauri copy under `%LOCALAPPDATA%\tauri\NSIS` is used), and Inno Setup 6. MynaCAD's script is `O-DRAFT-main/packaging/windows/MynaCAD.iss`; MynaOps's is `O-COREOP/BBSDesktop/installer/O-CoreOp.iss`; MynaHub's is `installer/aos.nsi`.
