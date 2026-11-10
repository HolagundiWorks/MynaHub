# The Myna installers

Two installers, each complete on its own (per user, no administrator). **MynaOps and MynaCAD depend on each other** (MynaCAD records the take-off into the project file, MynaOps reads it and writes the estimate back), so they are **one installer** and are removed together. MynaHub and the Myna launcher are the other installer. There is no installer for MynaOps or MynaCAD alone, and no combined suite installer (retired 2026-11-10).

| Installer | Installs | Install folders | Start menu |
|---|---|---|---|
| `Myna-Setup-<version>.exe` | **MynaHub** and the **Myna launcher** (AI Core host, tray icon) | `%LOCALAPPDATA%\Programs\O-Studio` | folder **Myna**: MynaHub, Myna |
| `MynaOpsCAD-Setup-<version>.exe` | **MynaOps** (quantities, estimates, billing) and **MynaCAD** (drafting); optional task: the assistant model | `%LOCALAPPDATA%\Programs\O-CoreOp` and `...\O-Draft` | folder **MynaOps and MynaCAD**: MynaOps, MynaCAD |

The install folders keep their old names on purpose (identifiers), so an upgrade replaces an earlier install in place. Each Setup removes the shortcuts and Start menu folders that earlier Setups made under older names (Kivo, Tren, Falcon, and the separate MynaOps and MynaCAD folders of 1.7.0).

## File types

Double-clicking a project file (`.osp`) opens it in MynaCAD; the right-click verb **Open in MynaOps** opens it in MynaOps. Neither starts Myna. Both are registered by `MynaOpsCAD-Setup` and removed with it.

## Removing

Settings > Apps lists **Myna (MynaHub and the Myna launcher)** and **MynaOps and MynaCAD**. Removing the second takes both apps. Company files (`.osas`), projects (`.osp`) and the assistant model are never deleted.

## Silent command lines

```text
Myna-Setup-<v>.exe /S
MynaOpsCAD-Setup-<v>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="" /LOG="%TEMP%\opscad-install.log"
```

Use `/TASKS="model"` with `MynaOpsCAD-Setup` to download the assistant model (Olmo 3 7B, 4.5 GB, SHA-256 checked). From Git Bash set `MSYS_NO_PATHCONV=1` or the slashes are rewritten.

## Building

`build-all.ps1` rebuilds both from the sibling working trees (`O-STUDIO`, `O-COREOP`, `O-DRAFT-main`; commit or stash edits you do not want packaged) and writes the two Setups plus `Myna-installers-<version>.manifest.json` (git commits and SHA-256) to `out/`.

```powershell
powershell -ExecutionPolicy Bypass -File installer\suite\build-all.ps1 -Version 1.7.1
```

Needs the MSVC build tools (vcvars64), NSIS (the tauri copy under `%LOCALAPPDATA%\tauri\NSIS` is used), and Inno Setup 6. MynaHub's script is `installer/aos.nsi`; the combined MynaOps and MynaCAD script is `O-COREOP/BBSDesktop/installer/O-CoreOp.iss` (`#ifdef WithCad`: `build-installer.ps1 -CadStage <folder with app\ODraft.WinUi.exe>`), around the portable layout from `O-DRAFT-main/packaging/windows/package.ps1`.
