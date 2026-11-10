# One Setup for the suite (Myna Suite)

`build-single.ps1` builds `out\Myna-Setup-<version>.exe`: a **single unified package** for installing and removing **MynaHub**, **MynaOps**, and **MynaCAD** (optional MynaMod / O-Dynamo when `-FormSetup` / `-DynamoSetup` are passed). Per user, no administrator rights.

## Install flow (interactive)

1. Run `Myna-Setup-<version>.exe`.
2. On **Setup mode**, choose **Install or add applications**.
3. Pick **Everything**, **MynaHub only**, or **Choose the apps** and select checkboxes (MynaHub, MynaOps, MynaCAD).
4. If MynaOps or MynaCAD is selected without MynaHub, **MynaHub is added automatically** (suite hub and launcher).
5. Optional task: download the shared Olmo 3 7B assistant model (4.5 GB, SHA-256 checked).
6. Progress is shown while each app’s installer runs (or MynaCAD is copied from the portable layout).
7. The **Installation finished** page lists what was installed.

## Remove flow (same package)

1. Run the **same** `Myna-Setup-<version>.exe`.
2. On **Setup mode**, choose **Remove applications** (enabled when at least one suite app is detected on disk).
3. Select one or more installed applications (or tick every listed app to remove the whole suite).
4. If **MynaHub** is selected while **MynaOps** or **MynaCAD** stays installed, Setup asks what to do, because those apps use MynaHub (Myna, sign-in and assistant host): **Yes** removes them as well, **No** keeps them (they run without Myna until MynaHub is installed again), **Cancel** returns to the selection. A silent setup keeps them and logs it.
5. Confirm on the **Confirm removal** page, which lists the applications about to be removed.
6. Progress runs each app’s uninstaller. Removing MynaCAD deletes its folder and its "Open in MynaCAD" verb; the `.osp` file type is deleted only when MynaOps is not installed, so MynaOps's own verb and association stay.

Double-clicking a project file (`.osp`) starts MynaCAD with that path. The right-click verb **Open in MynaOps** starts MynaOps with that path. Neither command starts Myna.
7. The **Removal complete** page summarizes what was removed. **Company files (`.osas`), projects (`.osp`), and the assistant model are not deleted.**

## Windows Settings uninstall

**Apps and features → Myna Suite** removes **all components that were installed through this package** (respecting Inno component state). For **partial** removal, use the Setup wizard **Remove applications** mode above.

## Silent command line

Install (no model download):

```text
Myna-Setup-<v>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /COMPONENTS="studio,coreop,draft" /TASKS="" /LOG="%TEMP%\suite-install.log"
```

Remove (all detected apps, or restrict with `/REMOVE=`):

```text
Myna-Setup-<v>.exe /VERYSILENT /MODE=uninstall /REMOVE="studio,coreop,draft"
```

`/REMOVE=` accepts `studio`, `coreop`, `draft`, or `all`. Omit `/REMOVE` to remove every detected suite app.

## Build inputs

**Release build (recommended):** `build-all.ps1` rebuilds MynaHub, MynaOps, and MynaCAD from the sibling working trees, then compiles Myna Suite and writes `out/Myna-Setup-<version>.manifest.json` (git commits + SHA-256). Uncommitted edits in those trees are packaged; the manifest marks that revision `+dirty`.

```powershell
powershell -ExecutionPolicy Bypass -File installer\suite\build-all.ps1 -Version 1.3.1
```

**Wrap-only:** `build-single.ps1` packages whatever Setup.exe / zip files are already newest on disk (often stale). Partial recovery: `resume-build.ps1` after step 1 succeeds.

Implementation: `O-Suite.iss` + `O-Suite-Wizard.iss` (Inno Setup 6).

## Verified

- 2026-10-29: silent install of all three; uninstall via Myna Suite entry; reinstall.
- 2026-11-04: **`Myna-Setup-1.3.0.exe`** used stale wrap-only inputs in places; MynaOps nested install incomplete. Use **1.3.1+** from `build-all.ps1`.
