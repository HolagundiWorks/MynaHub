# O-Studio

O-Studio is the office app of **OSAS, the Open Source Architecture Suit**. The suite is O-Studio (office and project management, orange), O-CoreOp (quantities, estimates and billing, blue) and O-Draft (drafting, black). Some code identifiers keep older spellings, such as the `aos` portal session name and the `AOSENC1` file marker, because files and servers already use them.

Offline Windows desktop app for an architectural practice. Clients, projects, letters, invoices, and the accounts live in one company file. Developed by Human Centric Works.

**License:** [AGPL-3.0](LICENSE) (Community) or [Commercial](LICENSING.md) · see also [NOTICE](NOTICE)

Help for daily work is in [docs/help](docs/help/Home.md).

## Company file

Each practice is one `.adms` file. Copying or backing up that file moves the company with it. O-Studio checks the file before opening it, locks it while it is open, and keeps a copy before a schema upgrade. An ordinary company file is a SQLite database and is not encrypted. The engine can also protect a file at rest: a protected file starts with `AOSENC1` and holds a random file key wrapped once with the owner's password and once with the recovery code, and backups of a protected file are sealed the same way. Losing both the password and the recovery code means the file cannot be opened.

Sign-in uses the admin password. The password needs 12 or more characters. A recovery code is shown once when the company is created, and a new code is shown once whenever the password is reset.

## In the app

- **Practice** — Pulse, Clients, and Projects. A project records its Council of Architecture consultancy type and floors, which choose its standard stages and payment schedule. Time book, transmittal log, and project reports and presentations are records only; they never post a journal. See [Project records](docs/help/Project-records.md).
- **Documents** — Letters and Templates. Built-in templates stay as shipped. Copy one, then edit the copy. Word templates are saved as `.docx`.
- **Billing** — Invoices and receipts, and bank reconciliation. **Books** — expenses, income, salaries, assets, and journals. **Reports and tax** — financial reports, tax, and the chart of accounts and financial years. Each client and project has its own ledger, and an uncollected bill can be written off to a bad-debts account.
- **Office** — Firm profile, staff, leave, attendance, the office library, security and backup. Under **Practice** there are also enquiries, site records, permits, fee proposals, and meetings and decisions. Under **Reports and tax** there is a 26AS check against the TDS booked on receipts. See [Proposals, meetings and 26AS](docs/help/Proposals-meetings-and-26AS.md) and [Enquiries, site records and permits](docs/help/Enquiries-site-and-permits.md).

Shortcuts: Ctrl+N new record, Ctrl+S save, Ctrl+P print, Ctrl+F search, Ctrl+K go to a page.

## The suite

O-Studio is one of several apps that share an office's records. The pieces in this repository:

| Folder | What it is |
|---|---|
| `office-core/` | The company file, sign-in, schema tracks and the shared records. Not ShilpiDB, which is the separate drawing store. |
| `office-ffi/`, `native/Office.Interop/` | A C interface and .NET wrapper so another native app can use the same file. Contract: [docs/office-abi.md](docs/office-abi.md) |
| `office-agent/` | Governed read-only tools and an MCP server for an AI assistant |

The architecture and integration map for O-Studio, O-Draft, O-CoreOp, ShilpiDB and AORMS is [docs/suite-architecture.md](docs/suite-architecture.md). Each folder is its own Cargo crate; run `cargo test` inside it.

## Develop

The production window is the WinUI 3 app in `native/Adms.Desktop`. The Rust engine in `src-tauri` owns the company file, accounting, documents, and PDF output. WinUI loads `architect_office_desktop_lib.dll` over the C ABI — there is no web or Tauri window.

Build the engine first. The WinUI project copies the DLL from `src-tauri/target/debug` when it exists (or from `target/release` with `-p:EngineProfile=release`), and without it the app starts with no engine.

```bash
cargo build --manifest-path src-tauri/Cargo.toml
dotnet build native/Adms.Desktop/Adms.Desktop.csproj -c Debug -p:Platform=x64 -p:WindowsPackageType=None
```

The logos come from the sibling `O-UI` library (`brand/` there), in orange for O-Studio. Build output (`target/`, `bin/`, `obj/`, `installer/stage`) is ignored by git and can be deleted at any time.

Tests:

```bash
cargo test --manifest-path src-tauri/Cargo.toml   # engine, accounting, company file, templates, PDF
cargo test --manifest-path office-core/Cargo.toml
```

The installer is built with `installer/build.ps1` (release engine, self-contained publish, NSIS) and is named `O-Studio-Setup-<version>.exe`. It stays unsigned unless signing secrets are set (`installer/sign.ps1`). Help → **Check for updates…** is a channel stub. Packaging notes: [docs/packaging.md](docs/packaging.md). What is open and planned is in [docs/roadmap.md](docs/roadmap.md).

## Licensing

O-Studio is **dual-licensed**:

- **Community Edition** — [GNU AGPL v3](LICENSE). Free for individuals, hobbyists,
  research, and internal business use. Note the AGPL's network clause: hosting a modified
  version as a service (SaaS) requires releasing your source under the AGPL.
- **Commercial License** — required to **resell**, **embed in a proprietary product**,
  **offer as SaaS** without AGPL compliance, **remove the branding**, or **ship
  closed-source modifications**.

See [LICENSING.md](LICENSING.md). Commercial enquiries: **office@hcworks.in** · <https://hcworks.in>

Share contracts and documents with other suite apps — do not copy source across the
AGPL boundary into permissive repos (O-Draft, ShilpiDB, O-UI).

## Before live use

Have the firm's accountant review tax figures and the firm's counsel review agreement wording. The installer is not signed.
