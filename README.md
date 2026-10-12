# Architect Office (desktop)

Local-first, offline desktop app for an architectural consultancy: documents, billing and accounting.
Built from `native_desktop_development_guidelines_with_login.md` (Tauri 2 + React + TypeScript + Vite + Carbon + Rust + SQLite).

## What is in it

- **Security:** owner setup (no default credentials), Argon2id login with lockout, configurable auto-lock, roles and permissions checked in Rust, password change and recovery code, users, audit log, verified backup and restore.
- **Clients and projects**, archived rather than deleted.
- **Billing:** invoices, pro forma invoices (convertible), credit notes, cancellation, receipts with TDS, receipt reversal, GST per the company scheme (none / composition 6% / regular 18%), UPI QR, immutable issued PDFs with SHA-256.
- **Bank:** several accounts, CSV import with column mapping and duplicate detection, suggested matching (never automatic), split and partial payments, manual entry and manual linking, unmatch, posting bank charges and interest to the ledger.
- **Documents:** outward and inward letter registers, consultancy agreements with milestones (billable into invoices), versioned templates with a field catalogue, revisions, signed-copy storage, attachments.
- **Accounting:** double-entry ledger with automatic postings, chart of accounts, manual journals and reversals, financial years with close/reopen, expenses and vendors, rent / interest / other income, salaries, fixed assets and depreciation, tax payments, trial balance, profit and loss, balance sheet, account ledger, GST and TDS summary, receivable check, income-tax estimate.

## Interface

Built on the pure IBM Carbon Design System (official `white` theme, UI Shell, DataTable, Pagination, Modal, OverflowMenu, Tag; no custom colours). Every register uses one shared Carbon DataTable with server-side paging, allow-listed sorting, search, removable filter tags, column chooser, table-size preference, CSV export of all filtered rows, and distinct loading / empty / error states. Ctrl+K opens go-to. Documented Carbon exceptions: `.ao-num` (end-aligned tabular figures in numeric cells) and layout-only classes using Carbon spacing tokens. Preferences are remembered on this computer. Carbon compliance has been checked by source audit and component tests, not by a formal design review.

`npm run dev` opens the interface in a browser with sample data (development only, never in the real app).

## Not done

Encryption of the database at rest, installer signing and an installer test, a drag-and-drop letterhead designer (templates are wording only; layout is fixed), a statutory income-tax computation (an estimate is provided), server-side pagination for very large registers, desktop-to-Android sync. Tax rules and the starter agreement wording must be reviewed by the firm's accountant and counsel before live use.

## Develop

```bash
npm install
npm run tauri dev
```

Tests: `cd src-tauri && cargo test`
