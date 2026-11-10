# The Myna suite installers

Two installers, each complete on its own (per user, no administrator). **MynaOps and MynaCAD depend on each other** (MynaCAD records the take-off into the project file, MynaOps reads it and writes the estimate back), so they are **one installer** and are removed together. MynaHub is the other installer. The Myna launcher was removed on 2026-11-11; MynaOps hosts the AI agent for MynaOps and MynaCAD. There is no installer for MynaOps or MynaCAD alone, and no combined suite installer (retired 2026-11-10).

| Installer | Installs | Install folders | Start menu |
|---|---|---|---|
| `MynaHub-Setup-<version>.exe` | **MynaHub** | `%LOCALAPPDATA%\Programs\O-Studio` | folder **MynaHub**: MynaHub, Uninstall MynaHub |
| `MynaOpsCAD-Setup-<version>.exe` | **MynaOps** (quantities, estimates, billing; hosts the AI agent) and **MynaCAD** (drafting); the llama runtime; optional task: the assistant model | `%LOCALAPPDATA%\Programs\O-CoreOp` and `...\O-Draft` | folder **MynaOps and MynaCAD**: MynaOps, MynaCAD |

The install folders keep their old names on purpose (identifiers), so an upgrade replaces an earlier install in place. Each Setup removes the shortcuts and Start menu folders that earlier Setups made under older names (Kivo, Tren, Falcon, and the separate MynaOps and MynaCAD folders of 1.7.0). **Upgrade from 1.7.x:** `MynaHub-Setup` stops and removes the Myna launcher (`launcher\` folder, `Myna.lnk` in the Start menu and on the desktop, the Start menu folder **Myna** when it is empty afterwards, Run values, `launch-request.json`, `launcher-status.txt`); `MynaOpsCAD-Setup` stops a MynaOps that runs without a window as the AI host (`O-CoreOp.exe --ai-host`) before replacing files, and so does its uninstaller.

## File types

Double-clicking a project file (`.osp`) opens it in MynaCAD; the right-click verb **Open in MynaOps** opens it in MynaOps. Neither starts the other app. Both are registered by `MynaOpsCAD-Setup` and removed with it.

## Removing

Settings > Apps lists **MynaHub** (the same entry that read "Myna (MynaHub and the Myna launcher)" before) and **MynaOps and MynaCAD**. Removing the second takes both apps. Company files (`.osas`), projects (`.osp`) and the assistant model are never deleted.

## Silent command lines

```text
MynaHub-Setup-<v>.exe /S
MynaOpsCAD-Setup-<v>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="" /LOG="%TEMP%\opscad-install.log"
```

Use `/TASKS="model"` with `MynaOpsCAD-Setup` to download the assistant model (Olmo 3 7B, 4.5 GB, SHA-256 checked). From Git Bash set `MSYS_NO_PATHCONV=1` or the slashes are rewritten.

## Building

`build-all.ps1` rebuilds both from the sibling working trees (`O-STUDIO`, `O-COREOP`, `O-DRAFT-main`; commit or stash edits you do not want packaged) and writes the two Setups plus `Myna-installers-<version>.manifest.json` (git commits and SHA-256) to `out/`.

```powershell
powershell -ExecutionPolicy Bypass -File installer\suite\build-all.ps1 -Version 1.7.1
```

Needs the MSVC build tools (vcvars64), NSIS (the tauri copy under `%LOCALAPPDATA%\tauri\NSIS` is used), and Inno Setup 6. MynaHub's script is `installer/aos.nsi`; the combined MynaOps and MynaCAD script is `O-COREOP/BBSDesktop/installer/O-CoreOp.iss` (`#ifdef WithCad`: `build-installer.ps1 -CadStage <folder with app\ODraft.WinUi.exe>`), around the portable layout from `O-DRAFT-main/packaging/windows/package.ps1`.
