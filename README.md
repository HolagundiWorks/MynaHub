# MynaHub

MynaHub is the office app and suite hub. The suite is MynaHub (office and company file, orange), MynaOps (quantities, estimates and billing, blue), MynaCAD (drafting, black) and Myna3D (3D form, planned); Myna is the name of the suite. MynaHub holds every login (company sign-in, users, roles) and the company file; it has no AI (the AI agent runs in MynaOps for MynaOps and MynaCAD). Some code identifiers keep older spellings, such as the `aos` / `aqc` session strings and the `AOSENC1` file marker, because files already use them.

> **The suite:** names, roles, file formats, toolbars of every app and how they link in real time are in [docs/SUITE.md](docs/SUITE.md).

Offline Windows desktop app for an architectural practice. Clients, projects, letters, invoices, and the accounts live in one company file. MynaHub also chooses that company file, holds the admin password, launches sibling apps with one-time tickets, and approves estimates and running bills shared from MynaOps. A separate **OSAS.exe** hub is deprecated. Developed by Human Centric Works.

**License:** [AGPL-3.0](LICENSE) (Community) or [Commercial](LICENSING.md) · see also [NOTICE](NOTICE)

Help for daily work is in [docs/help](docs/help/Home.md). Merge decision: [docs/studio-osas-merge.md](docs/studio-osas-merge.md). Approvals ABI: [docs/contracts/approvals.md](docs/contracts/approvals.md).

## Company file

Each practice is one `.adms` file. Copying or backing up that file moves the company with it. MynaHub checks the file before opening it, locks it while it is open, and keeps a copy before a schema upgrade. An ordinary company file is a SQLite database and is not encrypted. The engine can also protect a file at rest: a protected file starts with `AOSENC1` and holds a random file key wrapped once with the owner's password and once with the recovery code, and backups of a protected file are sealed the same way. Losing both the password and the recovery code means the file cannot be opened.

Sign-in uses the admin password. The password needs 12 or more characters. A recovery code is shown once when the company is created, and a new code is shown once whenever the password is reset.

## In the app

- **Practice** — Pulse, Clients, and Projects. A project records its Council of Architecture consultancy type and floors, which choose its standard stages and payment schedule. Time book, transmittal log, and project reports and presentations are records only; they never post a journal. See [Project records](docs/help/Project-records.md).
- **Documents** — Letters and Templates. Built-in templates stay as shipped. Copy one, then edit the copy. Word templates are saved as `.docx`.
- **Billing** — Invoices and receipts, and bank reconciliation. **Books** — expenses, income, salaries, assets, and journals. **Reports and tax** — financial reports, tax, and the chart of accounts and financial years. Each client and project has its own ledger, and an uncollected bill can be written off to a bad-debts account.
- **Office** — Firm profile, staff, leave, attendance, the office library, security and backup. Under **Practice** there are also enquiries, site records, permits, fee proposals, and meetings and decisions. Under **Reports and tax** there is a 26AS check against the TDS booked on receipts. See [Proposals, meetings and 26AS](docs/help/Proposals-meetings-and-26AS.md) and [Enquiries, site records and permits](docs/help/Enquiries-site-and-permits.md).

Shortcuts: Ctrl+N new record, Ctrl+S save, Ctrl+P print, Ctrl+F search, Ctrl+K go to a page.

## The Myna launcher was removed (2026-11-11)

The tray launcher (`O-Studio.Launcher.exe`, shown as **Myna**) no longer exists. Company sign-in, users and the company file were already in MynaHub; MynaHub opens MynaOps and MynaCAD from its bottom bar by path, with nothing passed to them. The AI agent (Olmo 3 7B, on this computer) that the launcher hosted is now hosted by **MynaOps**, which MynaCAD starts without a window when it needs the agent. Installing `MynaHub-Setup` over a 1.7.x install removes the launcher, its `launcher\` folder and its Myna shortcuts. See [docs/SUITE.md](docs/SUITE.md).

## Install and remove

Two installers, each complete on its own (per user, no administrator): `MynaHub-Setup-<version>.exe` installs **MynaHub** (and removes the Myna launcher an earlier Myna Setup installed), and `MynaOpsCAD-Setup-<version>.exe` installs **MynaOps and MynaCAD** together. MynaOps and MynaCAD depend on each other and share the local project file, so they are one installer and are removed together; there is no installer for either alone, and no combined suite installer. Each Setup is also its own uninstaller (Settings > Apps); company files, projects and the assistant model are never deleted. Details and silent command lines: [installer/suite/README.md](installer/suite/README.md).

## The suite

MynaHub is one of several apps that share an office's records. The pieces in this repository:

| Folder | What it is |
|---|---|
| `office-core/` | The company file, sign-in, schema tracks and the shared records. |
| `office-ffi/`, `native/Office.Interop/` | A C interface and .NET wrapper so another native app can use the same file. Contract: [docs/office-abi.md](docs/office-abi.md) |
| `office-agent/` | Governed read-only tools and an MCP server for an external AI assistant (not used by any app; MynaHub has no AI) |

The architecture and integration map for MynaHub, MynaCAD, MynaOps and MynaUI is [docs/suite-architecture.md](docs/suite-architecture.md). Each folder is its own Cargo crate; run `cargo test` inside it.

## Develop

The production window is the WinUI 3 app in `native/Adms.Desktop`. The Rust engine in `src-tauri` owns the company file, accounting, documents, and PDF output. WinUI loads `architect_office_desktop_lib.dll` over the C ABI — there is no web or Tauri window.

Build the engine first. The WinUI project copies the DLL from `src-tauri/target/debug` when it exists (or from `target/release` with `-p:EngineProfile=release`), and without it the app starts with no engine.

```bash
cargo build --manifest-path src-tauri/Cargo.toml
dotnet build native/Adms.Desktop/Adms.Desktop.csproj -c Debug -p:Platform=x64 -p:WindowsPackageType=None
```

The logos come from the sibling `MynaUI` library (`brand/` there), in orange for MynaHub. Build output (`target/`, `bin/`, `obj/`, `installer/stage`) is ignored by git and can be deleted at any time.

Tests:

```bash
cargo test --manifest-path src-tauri/Cargo.toml   # engine, accounting, company file, templates, PDF
cargo test --manifest-path office-core/Cargo.toml
```

The installer is built with `installer/build.ps1` (release engine, self-contained publish, NSIS) and is named `O-Studio-Setup-<version>.exe` inside the build (see [docs/packaging.md](docs/packaging.md)). For both suite installers, build **`installer/suite/build-all.ps1`** → `MynaHub-Setup-<version>.exe` and `MynaOpsCAD-Setup-<version>.exe` ([installer/suite/README.md](installer/suite/README.md)). It stays unsigned unless signing secrets are set (`installer/sign.ps1`). Help → **Check for updates…** is a channel stub. What is open and planned is in [docs/roadmap.md](docs/roadmap.md).

### Cloud sync (Neon)

MynaHub will sync `.osp` project files through **Neon** (Postgres + Object Storage); only Studio holds cloud credentials. **Setup:** [docs/CLOUD-NEON.md](docs/CLOUD-NEON.md). **In the app:** Suite → **Cloud sync** (or `npm run verify` for a headless smoke test).

## Licensing

MynaHub is **open source** and **dual-licensed** (Developed by Human Centric Works):

- **Community Edition** — [GNU AGPL v3](LICENSE). Free and open source for individuals,
  hobbyists, research, and internal business use. Note the AGPL's network clause: hosting
  a modified version as a service (SaaS) requires releasing your source under the AGPL.
- **Commercial License** — optional **AGPL relief** (not a closed-source product) if you
  cannot comply with the AGPL — for example to **resell**, **embed in a proprietary
  product**, **offer as SaaS** without AGPL compliance, **remove the branding**, or
  **ship closed-source modifications**.

See [LICENSING.md](LICENSING.md). Support / commercial: **support@hcworks.in** · <https://hcworks.in>

Share contracts and documents with other suite apps — do not copy source across the
AGPL boundary into permissive repos (MynaCAD, MynaUI).

## Before live use

Have the firm's accountant review tax figures and the firm's counsel review agreement wording. The installer is not signed.
