# ADMS: Architects Documents Management System (desktop)

Local-first, offline desktop app for an architectural consultancy: documents, billing and accounting.
Built from `native_desktop_development_guidelines_with_login.md` (Tauri 2 + React + TypeScript + Vite + Carbon + Rust + SQLite).

## What is in it

- **Company file:** the app starts at a company chooser (create a new company, open an existing one, recent companies). Each company is one `.adms` file (files saved by earlier builds as `.aorms` are the same format and still open) (a SQLite database) holding its records, issued PDFs, attachments and audit history, so moving or backing up that one file moves the company. Files are validated by content (not name) before opening, are never overwritten, are locked while open (a second window or program is refused), and a copy is taken before any schema upgrade. Close or switch company from the settings panel; all company state is cleared. Data saved by the first release can be converted from the start screen. **The extension is not encryption: files are not encrypted at rest.**
- **Security:** owner setup (no default credentials), Argon2id login with lockout, configurable auto-lock, roles and permissions checked in Rust, password change and recovery code, users, audit log, verified backup and restore.
- **Clients and projects**, archived rather than deleted.
- **Billing:** invoices, pro forma invoices (convertible), credit notes, cancellation, receipts with TDS, receipt reversal, GST per the company scheme (none / composition 6% / regular 18%), UPI QR, immutable issued PDFs with SHA-256.
- **Bank:** several accounts, CSV import with column mapping and duplicate detection, suggested matching (never automatic), split and partial payments, manual entry and manual linking, unmatch, posting bank charges and interest to the ledger.
- **Documents:** outward and inward letter registers, consultancy agreements with milestones (billable into invoices), revisions, signed-copy storage, attachments. A library of 26 built-in **practice templates** (consultancy, billing, project administration: proposal, appointment letter, detailed agreement, scope and fee annexures, variation, minutes, transmittal, site visit note, status report, handover register, closure, no-dues, fee reminder, RFI and more) is added to every company file. Templates are practice wording oriented to Indian architectural practice, **not official Council of Architecture forms**; review them before use. Built-ins are never edited: make a copy, edit and publish versions of the copy, archive or restore. Text in `[[double brackets]]` is a prompt that must be replaced before a document can be issued. The Template Manager previews a template with sample data and lists missing company details, and editing a template of your own opens a **design canvas**: a block editor (headings, paragraphs, bullet lists, field insertion, reorder, delete, or the raw text view) beside a live PDF preview rendered by the real engine. HTML templates (a separate family) are designed in the **Template Studio**: a CodeMirror code editor for HTML and CSS with validation markers, an approved-field picker and autocomplete, a live preview from the real PDF renderer (A4 or `@page` paper, sample data profiles), drafts, immutable versions, restore and copy-to-edit; see `docs/template-studio.md`. The wording canvas is deliberately constrained (fonts, sizes and page layout come from the fixed type system), not a free-form drawing tool. **Typography:** documents are set in bundled IBM Plex (Sans for text, SemiBold/Medium faces for headings and labels, Serif for formal agreement titles, Mono for document numbers, Devanagari as the Hindi/Marathi fallback), embedded in every PDF so nothing depends on installed fonts. Named styles and the print type scale (in points) are in `src-tauri/src/typography.rs`; fonts and licence are recorded in `src-tauri/assets/fonts/plex/README.md`. Sample PDFs are in `docs/sample-pdfs`. Classic or minimal letterhead style is chosen in the company profile and frozen into each issued document.
- **Accounting:** double-entry ledger with automatic postings, chart of accounts, manual journals and reversals, financial years with close/reopen, expenses and vendors, rent / interest / other income, salaries, fixed assets and depreciation, tax payments, trial balance, profit and loss, balance sheet, account ledger, GST and TDS summary, receivable check, income-tax estimate.

## Interface

Built on the pure IBM Carbon Design System (official `white` theme, UI Shell, DataTable, Pagination, Modal, OverflowMenu, Tag; no custom colours). Every register uses one shared Carbon DataTable with server-side paging, allow-listed sorting, search, removable filter tags, column chooser, table-size preference, CSV export of all filtered rows, and distinct loading / empty / error states. Ctrl+K opens go-to. Documented Carbon exceptions: `.ao-num` (end-aligned tabular figures in numeric cells) and layout-only classes using Carbon spacing tokens. Preferences are remembered on this computer. Carbon compliance has been checked by source audit and component tests, not by a formal design review.

`npm run dev` opens the interface in a browser with sample data (development only, never in the real app).

## Not done

Encryption of the company file at rest (key management not yet decided), installer signing and an installer test, a layout/page editor for templates (templates are wording only; layout and type are fixed), conditional and repeatable template blocks, a statement-of-account document, continuation sheets, DOCX output, Devanagari fallback is tested only for font embedding (not for shaping of conjuncts), and visual regression tests of the PDFs, a statutory income-tax computation (an estimate is provided), desktop-to-Android sync. Tax rules and the starter agreement wording must be reviewed by the firm's accountant and counsel before live use.

## Develop

```bash
npm install
npm run tauri dev
```

Tests: `cd src-tauri && cargo test`
