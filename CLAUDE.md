# MynaHub (repo O-STUDIO): rules for coding agents

MynaHub ("the studio", exe `O-Studio.exe`) is the company hub of the Myna suite: company sign-in, users and roles, practice records, estimates, bills, approvals, the company database (`.osas`, SQLite via the Rust `office-core` engine) and the only cloud sync (Neon). Read [docs/SUITE.md](docs/SUITE.md) (canonical names, roles, architecture; implemented vs planned), [README.md](README.md) and [docs/packaging.md](docs/packaging.md) first.

## Invariants
- **No launcher, no AI in MynaHub** (removed 2026-11-11). MynaOps hosts the AI agent; MynaOps and MynaCAD share a local `.osp` and are independent of MynaHub: do not add a sign-in broker, tickets, a database service or an app-to-app link. Project data reaches the company database only through MynaOps's CSV export, imported here.
- **Names.** User-visible text uses MynaHub / MynaOps / MynaCAD / MynaMod / Myna (family). Identifiers keep the old spellings on purpose: install folder `O-Studio`, `O-Studio.exe`, `OSTUDIO_*` env vars, `%LOCALAPPDATA%\O-Studio` and `\OSAS`, ticket ids, SQL migrations. `docs/SUITE.md` is the authority; the same copy lives in O-COREOP and MynaCAD: change all three.
- **Secrets.** Neon credentials live in `%LOCALAPPDATA%\OSAS\neon.env`; never commit or print them. MynaCAD and MynaOps never hold cloud keys.
- **Say only what you ran.** Documentation separates implemented from planned; update affected docs in the same change.

## Layout
`native/Adms.Desktop` (MynaHub WinUI app), `native/Osas.Core` (+ `Osas.Core.Tests`), `native/Adms.Cloud`, `native/Adms.Smoke` and `Adms.TestSeed` (checks and seed data), `office-core` + `office-ffi` + `src-tauri` (Rust engine, built as `architect_office_desktop_lib.dll`), `installer/` (`aos.nsi` hub Setup, `build.ps1`, `suite/build-all.ps1` builds BOTH installers: `MynaHub-Setup` and `MynaOpsCAD-Setup`), `docs/`.

## Build and test
- `dotnet test native/Osas.Core.Tests -p:Platform=x64`; `dotnet build native/Adms.Desktop/Adms.Desktop.csproj -c Release -p:Platform=x64`; Rust: `cargo test` in `office-core`. The smoke run needs the engine DLL (`cargo build --release` in `src-tauri`, `Adms.Smoke -p:EngineDll=...`). `dotnet build native/Adms.sln` fails on a PRI duplicate on unchanged main: build projects, not the solution.
- Installers: `installer/suite/build-all.ps1 -Version X` packages the WORKING TREES of O-STUDIO, O-COREOP and MynaCAD: keep them clean and on main while it runs (details in the project memory). `installer/suite/README.md` has the silent command lines.
- WinUI projects reference `..\..\..\o-ui` (the sibling `o-ui` checkout): after a change there, build all three apps.

## Git
`main` and `master` must stay identical (push both: `git push origin HEAD:main HEAD:master`). Never `git add -A` or add whole `installer/` or `out/` folders; check `git diff --cached --name-only` for exe/dll/zip/msi first. Two old stashes are kept on purpose. Parallel agents: separate worktree and `CARGO_TARGET_DIR`, edit only your files.
