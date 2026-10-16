# AOS — Architecture Office Suite

Offline Windows desktop app for an architectural practice. Clients, projects, letters, invoices, and the accounts live in one company file. Developed by Human Centric Works.

Help for daily work is in [docs/help](docs/help/Home.md).

## Company file

Each practice is one `.adms` file. Copying or backing up that file moves the company with it. AOS checks the file before opening it, locks it while it is open, and keeps a copy before a schema upgrade. The file is a SQLite database and is not encrypted.

Sign-in uses the admin password. The password needs 12 or more characters. A recovery code is shown once when the company is created, and a new code is shown once whenever the password is reset.

## In the app

- **Practice** — Pulse, Clients, and Projects. A project records its Council of Architecture consultancy type.
- **Documents** — Letters and Templates. Built-in templates stay as shipped. Copy one, then edit the copy. Word templates are saved as `.docx`.
- **Accounts** — Invoices, reports, journals, expenses, income, salaries, assets, tax, bank reconciliation, and the chart of accounts.
- **Office** — Firm profile, recent activity, audit, and backup.

Shortcuts: Ctrl+N new record, Ctrl+S save, Ctrl+P print, Ctrl+F search, Ctrl+K go to a page.

## Develop

The production window is the WinUI 3 app in `native/Adms.Desktop`. The Rust engine in `src-tauri` owns the company file, accounting, documents, and PDF output.

```bash
dotnet build native/Adms.Desktop/Adms.Desktop.csproj
cargo test --manifest-path src-tauri/Cargo.toml
```

`npm run tauri dev` opens the earlier Carbon interface, which uses the same engine.

## Before live use

Have the firm's accountant review tax figures and the firm's counsel review agreement wording. The installer is not signed.
