# Test environment

## 1. Machine

| Item | Requirement |
|---|---|
| OS | Windows 11, 1920x1080 or larger, one user session |
| Visual Studio | Build Tools 2022 **with the "Universal Windows Platform build tools" workload** (provides `Microsoft.Build.AppxPackage.dll`). Without it the WinUI apps do not build |
| .NET | .NET 8 SDK (the 9 SDK also works) |
| Rust | stable toolchain, MSVC target (`x86_64-pc-windows-msvc`); `wasm32-unknown-unknown` target for the Draft web check |
| Other tools | CMake, Ninja, Inno Setup 6, NSIS (a copy ships in `%LOCALAPPDATA%\tauri\NSIS`), `wix` (dotnet tool) |
| Disk | 60 GB free for a full cargo build |

Environment variables for builds on this machine:

```
AppxMSBuildToolsPath = C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\
PATH += C:\Users\<you>\AppData\Local\tauri\NSIS
AAD_FONTS_DIR = (empty for packaging)
```

Run build scripts inside `vcvars64.bat` (MSVC environment).

## 2. Build and install

Build order and commands (full detail in each repository's packaging doc):

| Step | Command | Output |
|---|---|---|
| 1 | `O-STUDIO\installer\build.ps1` | `O-STUDIO\installer\O-Studio-Setup-0.1.0.exe` (`O-Studio.exe` and `O-Studio.Launcher.exe` in one folder) |
| 2 | `O-COREOP\BBSDesktop\installer\build-installer.ps1 -OfficeDllDir O-STUDIO\office-ffi\target\release` (build `office-ffi` first: `cargo build --release`) | `O-COREOP\BBSDesktop\artifacts\installer\O-CoreOp-Setup-<v>.exe` |
| 3 | `O-DRAFT-main\packaging\windows\package.ps1 -Arch x64` | `O-DRAFT-main\dist\release\o-draft-<v>-windows-x64.msi` and `...-portable.zip` |
| 4 | `O-STUDIO\installer\suite\build-single.ps1 -Version <v>` | `O-STUDIO\installer\suite\out\O-Suite-Setup-<v>.exe` |

Install for testing with the suite installer, per user, no model download (the model is shared and already present):

```
O-Suite-Setup-<v>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /COMPONENTS="studio,coreop,draft" /TASKS="" /LOG="%TEMP%\suite-install.log"
```

Installed locations (per user): `%LOCALAPPDATA%\Programs\O-Studio` (`O-Studio.Launcher.exe` beside `O-Studio.exe`), `...\O-CoreOp`, `...\O-Draft`, `...\O-Suite` (uninstaller).

Record in every run report: the installer file name and SHA-256, the commit of each repository, the Windows build number.

## 3. Test data

| Data | How to get it | Used by |
|---|---|---|
| Test company `TestCo-<date>.osas` | run `dotnet run --project native/Adms.TestSeed -c Release` (creates company under `D:\Work Development\TestRuns\<date>\`, role users, demo client/project, copies `small.osp`; passwords in `docs/testing/.local/credentials.txt`) | all |
| Re-link Documents `001-1` + DEMO drawing to TC-01 without re-seeding | `dotnet run --project native/Adms.TestSeed -c Release -- "D:\Work Development\TestRuns\<date>" --wire-only` | all |
| Test users, one per role | create in O-Studio (Owner, Principal, Accounts, Document editor, Auditor) | role tests |
| Small DXF/DWG | `Adms.TestSeed` copies `OneDrive\Documents\OSAS TEST\DEMO.dwg` (and `D1.dwg`) into `<test folder>\cad\`, and into `projects\tc-01-1\draft\`; also copies `drawing.osp` and `projects\001-1\` into `<test folder>\from-documents\`. Paths are listed in `docs/testing/.local/credentials.txt`. Plus `O-DRAFT-main\crates\engine\tests\fixtures` and `docs/qa` files | Draft |
| `small.osp` | `O-COREOP\BBSDesktop\Office.Shared.Tests\Fixtures\small.osp` (copy; never test on the fixture itself) | CoreOp, Draft |
| Empty `.osp` | `cargo run -p aad-odp --example pack -- test.osp Test --drawing demo.dxf` | Draft, CoreOp |
| Bank statement CSV / XLSX | write a 20-row sample (date, narration, debit, credit, balance) into the test folder | Studio bank import |
| Word template | any `.docx` the test creates from a built-in template | Studio |
| Neon test branch | a Neon branch named `test-<date>` created from the console; its connection values go in `%LOCALAPPDATA%\OSAS\neon.env` for the run and are restored afterwards | cloud sync |

Test folder: `D:\Work Development\TestRuns\<yyyy-mm-dd>\` (outside every repository). Reports and evidence go into the repository; data does not.

## 4. Tools the agent can use

| Need | Tool |
|---|---|
| Drive and read WinUI windows | UI Automation (PowerShell `System.Windows.Automation`), or the repository's UIA scripts: `O-COREOP\BBSDesktop\tests\e2e\*.ps1` (`run-e2e.ps1`, `uia-smoke.ps1`, `uia-osp-takeoff.ps1`, `e2e-draft-coreop-takeoff.ps1`). Prefer UIA element names to screen coordinates. Verify what is under a click point before clicking |
| Drive O-Draft without the mouse | `aad-cli` (`aad-cli run --script file.scr`), the JSON control channel on `127.0.0.1`, or the MCP server ([control-protocol.md](../../../O-DRAFT-main/docs/control-protocol.md)); every command in the menus is also a command |
| Read the launcher's state | `%LOCALAPPDATA%\O-Studio\launcher-status.txt` (AI agent, database, cloud sync; rewritten at least every 20 s) and `%LOCALAPPDATA%\OSAS\suite.json` |
| Read the project file | `cargo run -p aad-odp --example inspect -- file.osp` |
| Unit and integration suites (run first, they are the baseline) | `cargo test --workspace` (O-Draft); `dotnet test AQC.CadImport.Tests` and `dotnet test Office.Shared.Tests` (O-CoreOp); `cargo test` in each O-Studio crate (`office-core`, `office-ffi`, `office-agent`) and `dotnet test native\Osas.Core.Tests` |
| Screenshots | `System.Drawing` `CopyFromScreen` of the window rectangle only |
| Crashes | Windows Application event log (`Application Error`, `.NET Runtime`): copy the faulting module and exception code into the bug |

## 5. Baseline before any manual test

Record, and fix or log, any failure here first:

| ID | Check | Expected |
|---|---|---|
| ENV-01 | `cargo test --workspace` in O-Draft | 0 failed (last known: 1652 passed) |
| ENV-02 | `dotnet test` AQC.CadImport.Tests, Office.Shared.Tests | 0 failed (last known: 88 and 91) |
| ENV-03 | `dotnet test native\Osas.Core.Tests` in O-Studio | note any failures; `SuiteCompanyTests.The_environment_an_app_is_started_with_names_the_company_and_the_ticket` is a known failure (BUG-0001) |
| ENV-04 | all four installers build and the suite installer installs silently | exit code 0, three app folders exist |
| ENV-05 | uninstall through "O-Suite" in Apps and features | the apps are removed; the model, company files and projects stay |
