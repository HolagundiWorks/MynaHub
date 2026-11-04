# One Setup for the suite (O-Suite)

`build-single.ps1` builds `out\O-Suite-Setup-<version>.exe`: a **single unified package** for installing and removing **O-Studio**, **O-CoreOp**, and **O-Draft** (optional O-Form / O-Dynamo when `-FormSetup` / `-DynamoSetup` are passed). Per user, no administrator rights.

## Install flow (interactive)

1. Run `O-Suite-Setup-<version>.exe`.
2. On **Setup mode**, choose **Install or add applications**.
3. Pick **Everything**, **O-Studio only**, or **Choose the apps** and select checkboxes (O-Studio, O-CoreOp, O-Draft).
4. If O-CoreOp or O-Draft is selected without O-Studio, **O-Studio is added automatically** (suite hub and launcher).
5. Optional task: download the shared Olmo 3 7B assistant model (4.5 GB, SHA-256 checked).
6. Progress is shown while each app’s installer runs (or O-Draft is copied from the portable layout).
7. The **Installation finished** page lists what was installed.

## Remove flow (same package)

1. Run the **same** `O-Suite-Setup-<version>.exe`.
2. On **Setup mode**, choose **Remove applications** (enabled when at least one suite app is detected on disk).
3. Select one or more installed applications (or tick every listed app to remove the whole suite).
4. Confirm on the **Confirm removal** page.
5. Progress runs each app’s uninstaller (O-Draft folder and `.osp` association are removed when O-Draft is selected).
6. The **Removal complete** page summarizes what was removed. **Company files (`.osas`), projects (`.osp`), and the assistant model are not deleted.**

## Windows Settings uninstall

**Apps and features → O-Suite** removes **all components that were installed through this package** (respecting Inno component state). For **partial** removal, use the Setup wizard **Remove applications** mode above.

## Silent command line

Install (no model download):

```text
O-Suite-Setup-<v>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /COMPONENTS="studio,coreop,draft" /TASKS="" /LOG="%TEMP%\suite-install.log"
```

Remove (all detected apps, or restrict with `/REMOVE=`):

```text
O-Suite-Setup-<v>.exe /VERYSILENT /MODE=uninstall /REMOVE="studio,coreop,draft"
```

`/REMOVE=` accepts `studio`, `coreop`, `draft`, or `all`. Omit `/REMOVE` to remove every detected suite app.

## Build inputs

**Release build (recommended):** `build-all.ps1` rebuilds O-Studio, O-CoreOp, and O-Draft from the sibling repos, then compiles O-Suite and writes `out/O-Suite-Setup-<version>.manifest.json` (git commits + SHA-256). Only **committed** O-Draft changes on `main` are included; local uncommitted edits are not.

```powershell
powershell -ExecutionPolicy Bypass -File installer\suite\build-all.ps1 -Version 1.3.1
```

**Wrap-only:** `build-single.ps1` packages whatever Setup.exe / zip files are already newest on disk (often stale). Partial recovery: `resume-build.ps1` after step 1 succeeds.

Implementation: `O-Suite.iss` + `O-Suite-Wizard.iss` (Inno Setup 6).

## Verified

- 2026-10-29: silent install of all three; uninstall via O-Suite entry; reinstall.
- 2026-11-04: **`O-Suite-Setup-1.3.0.exe`** used stale wrap-only inputs in places; O-CoreOp nested install incomplete. Use **1.3.1+** from `build-all.ps1`.
