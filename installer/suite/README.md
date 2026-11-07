# One Setup for the suite (O-Suite)

`build-single.ps1` builds `out\O-Suite-Setup-<version>.exe`: a **single unified package** for installing and removing **Kivo**, **Falcon**, and **Tren** (optional Finch / O-Dynamo when `-FormSetup` / `-DynamoSetup` are passed). Per user, no administrator rights.

## Install flow (interactive)

1. Run `O-Suite-Setup-<version>.exe`.
2. On **Setup mode**, choose **Install or add applications**.
3. Pick **Everything**, **Kivo only**, or **Choose the apps** and select checkboxes (Kivo, Falcon, Tren).
4. If Falcon or Tren is selected without Kivo, **Kivo is added automatically** (suite hub and launcher).
5. Optional task: download the shared Olmo 3 7B assistant model (4.5 GB, SHA-256 checked).
6. Progress is shown while each app’s installer runs (or Tren is copied from the portable layout).
7. The **Installation finished** page lists what was installed.

## Remove flow (same package)

1. Run the **same** `O-Suite-Setup-<version>.exe`.
2. On **Setup mode**, choose **Remove applications** (enabled when at least one suite app is detected on disk).
3. Select one or more installed applications (or tick every listed app to remove the whole suite).
4. If **Kivo** is selected while **Falcon** or **Tren** stays installed, Setup asks what to do, because those apps use Kivo (Myna, sign-in and assistant host): **Yes** removes them as well, **No** keeps them (they run without Myna until Kivo is installed again), **Cancel** returns to the selection. A silent setup keeps them and logs it.
5. Confirm on the **Confirm removal** page, which lists the applications about to be removed.
6. Progress runs each app’s uninstaller. Removing Tren deletes its folder and its "Open in Tren" verb; the `.osp` file type is deleted only when Falcon is not installed, so Falcon's own verb and association stay.

Double-clicking a project file (`.osp`) starts Tren with that path. The right-click verb **Open in Falcon** starts Falcon with that path. Neither command starts Myna.
7. The **Removal complete** page summarizes what was removed. **Company files (`.osas`), projects (`.osp`), and the assistant model are not deleted.**

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

**Release build (recommended):** `build-all.ps1` rebuilds Kivo, Falcon, and Tren from the sibling working trees, then compiles O-Suite and writes `out/O-Suite-Setup-<version>.manifest.json` (git commits + SHA-256). Uncommitted edits in those trees are packaged; the manifest marks that revision `+dirty`.

```powershell
powershell -ExecutionPolicy Bypass -File installer\suite\build-all.ps1 -Version 1.3.1
```

**Wrap-only:** `build-single.ps1` packages whatever Setup.exe / zip files are already newest on disk (often stale). Partial recovery: `resume-build.ps1` after step 1 succeeds.

Implementation: `O-Suite.iss` + `O-Suite-Wizard.iss` (Inno Setup 6).

## Verified

- 2026-10-29: silent install of all three; uninstall via O-Suite entry; reinstall.
- 2026-11-04: **`O-Suite-Setup-1.3.0.exe`** used stale wrap-only inputs in places; Falcon nested install incomplete. Use **1.3.1+** from `build-all.ps1`.
