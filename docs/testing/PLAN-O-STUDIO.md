# Test plan: O-Studio

O-Studio is the office app and suite hub: one company file for a practice. Source: `O-STUDIO\native\Adms.Desktop` (WinUI), engine in `office-core`, `src-tauri`. Help pages: `docs/help/*.md`. Case format: [README.md](README.md). `[N]` normal, `[B]` boundary or bad input, `[R]` repeat or undo.

Sign-in is in the launcher. O-Studio's own login page is only a prompt to use the launcher; set-up (first admin), unlock and recovery stay in O-Studio.

## S1 Smoke

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| STU-SMK-01 | Start | Start `O-Studio.exe` | Window "O-Studio" opens with the welcome panel (create, open, recent) | UIA |
| STU-SMK-02 | Start with ticket | Launcher, **O-Studio** | Opens on the company, signed in, workspace visible | UIA |
| STU-SMK-03 | Close | Close the window | Process ends; company file released | process |
| STU-SMK-04 | Engine | Welcome panel | No "engine could not be loaded" message | UIA |

## S2 Company file

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| STU-CMP-01 | No company creation | Look in O-Studio (File menu, welcome panel) | There is no **New company** and no **Create a new company**; the welcome panel and **File → Choose or create a company…** open the launcher. Creating a company is LNC-WIN-03 in the launcher plan | UIA |
| STU-CMP-02 | No company picker | Look in O-Studio | There is no **Open company** and no file picker for companies; O-Studio opens on the company the launcher chose, signed in | UIA |
| STU-CMP-03 | No recent list | Start O-Studio with no company open | The welcome panel shows only **Open the launcher** (and **Open the company last chosen** when the suite has one); no recent list | UIA |
| STU-CMP-04 | Missing recent file `[B]` | Delete a recent file outside the app | Row shown with **Open** disabled | UIA |
| STU-CMP-05 | Last chosen | **Open the company last chosen** | Opens the company in `suite.json` | UIA |
| STU-CMP-06 | Earlier file format | Open an `.adms` company | Opens (earlier files still open) | UIA |
| STU-CMP-07 | Convert earlier data | When earlier-version data exists, the **Convert** banner | Creates a company file; the original is untouched | file hashes |
| STU-CMP-08 | Not a company file `[B]` | Open a renamed text file | Refused with a message; the app stays usable | UIA |
| STU-CMP-09 | File lock | Open the same company in a second O-Studio, and in O-CoreOp | Defined behaviour: O-Studio and O-CoreOp may have it open together; two O-Studio windows on one file do not corrupt it. Record the actual behaviour | both apps |
| STU-CMP-10 | Protected file | Create a protected (encrypted) company; reopen | File starts with `AOSENC1`; asks for the file password; wrong one refused; recovery code works; losing both is documented as unrecoverable | file header, UIA |
| STU-CMP-11 | Backup before upgrade | Open a company made by an older schema | A copy is kept before the schema upgrade | folder listing |
| STU-CMP-12 | Switch company | In the launcher choose another company; O-Studio, **Choose another company** | The launcher opens; once another company is chosen no data from the first remains on screen | UIA |

## S3 Session, users and roles

Roles: Owner, Principal, Accounts, Document editor, Auditor.

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| STU-USR-01 | Add user | Office, security: add a user per role | Created; shown in the list | UIA |
| STU-USR-02 | Password rules `[B]` | 11 characters; empty; same as user name | Refused with a clear message | UIA |
| STU-USR-03 | Change role | Change a user's role | Takes effect at the next sign-in; asks the owner's password again | UIA |
| STU-USR-04 | Stop and allow sign-in | Stop a user, try to sign in, allow again | Refused while stopped; works after | launcher |
| STU-USR-05 | Reset password | Reset a user's password | Old password refused; new works | launcher |
| STU-USR-06 | Last owner `[B]` | Try to remove or demote the only active owner | Refused | UIA |
| STU-USR-07 | Owner permissions | As Owner | Settings, users, backup, post, close period, issue, audit: all available | UIA |
| STU-USR-08 | Principal permissions | As Principal | View and issue only; no settings, users, backup, post | UIA |
| STU-USR-09 | Accounts permissions | As Accounts | View, issue, post; no settings or users | UIA |
| STU-USR-10 | Document editor permissions | As Document editor | View and edit documents; no issue, post, settings | UIA |
| STU-USR-11 | Auditor permissions | As Auditor | Read-only everywhere; can view the audit trail; every edit control disabled or refused | UIA |
| STU-USR-12 | Lock | **Lock** | Workspace hidden; unlock needs the password; wrong password refused | UIA |
| STU-USR-13 | Sign out | **Sign out** | Back to the launcher prompt | UIA |
| STU-USR-14 | Recovery | Login prompt, **Forgot password? Use recovery code**: user, code, new password | Password reset; a new recovery code shown once; the old code stops working | UIA |
| STU-USR-15 | Recovery, wrong code `[B]` | Wrong code | Refused; nothing changed | UIA |
| STU-USR-16 | No login page | Start without a ticket and a company open | A prompt to use the launcher, a button **Open the launcher**, **Choose another company** (opens the launcher), recovery link; no username or password boxes; the launcher opens once | UIA |

## S4 Practice

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| STU-PRA-01 | Pulse | Open Pulse | Summary of clients, projects, tasks without errors; counts match the lists | UIA |
| STU-PRA-02 | Clients: add, edit, delete | CRUD one client | Saved; list updates; delete asks to confirm; a client with projects cannot vanish silently | UIA |
| STU-PRA-03 | Clients, bad input `[B]` | Empty name; 300-character name; characters `<>&"'` | Refused or stored and displayed intact | UIA |
| STU-PRA-04 | Projects: add | Add a project: Council of Architecture consultancy type, floors | Standard stages and the payment schedule follow the type and floors | UIA |
| STU-PRA-05 | Projects: edit and archive | Change floors, archive | Stages update with a warning where work exists; archived hidden by default | UIA |
| STU-PRA-06 | Project path / folder | Open a project's path view | Shows the project's folder contract (`docs/contracts/project-folder.md`) | UIA |
| STU-PRA-07 | Time book | Add, edit, delete a time entry | Saved; totals correct; never posts a journal | UIA, ledger unchanged |
| STU-PRA-08 | Transmittal log | Add a transmittal; print | Logged with number; never posts a journal | UIA |
| STU-PRA-09 | Project reports and presentations | Create one of each | Document produced from the project data; records only, no journal | UIA, output file |
| STU-PRA-10 | Enquiries | Add, convert to a project | Stored; conversion creates a project with the enquiry's details | UIA |
| STU-PRA-11 | Site records | Add a site visit record | Stored with date, project, notes | UIA |
| STU-PRA-12 | Permits | Add and track a permit | Status changes saved | UIA |
| STU-PRA-13 | Fee proposals | Create, issue, accept | Numbering continues; accepted proposal links to a project | UIA |
| STU-PRA-14 | Meetings and decisions | Add a meeting with decisions | Saved; decisions listed | UIA |
| STU-PRA-15 | Tasks board and widget | Add, move and finish a task; open the tasks widget | Board and widget agree; widget needs sign-in | UIA |
| STU-PRA-16 | Project dashboard (O-CoreOp summary) | Open a project that has a mirrored summary from O-CoreOp | Summary figures shown; absent summary shows an empty state, not an error | UIA |

## S5 Documents

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| STU-DOC-01 | Letters | New letter from a built-in template, fill fields, save, print/export | Numbered; fields merged; output matches the screen | UIA, PDF |
| STU-DOC-02 | Built-in templates read-only | Edit a built-in template | Not changed; a copy is made to edit | UIA |
| STU-DOC-03 | Template copy | Copy, edit, save | Saved as `.docx`; opens in Word/LibreOffice | file |
| STU-DOC-04 | Word editing | Bold, italic, list, table, image, page break | Round-trips through save and reopen | UIA, `.docx` |
| STU-DOC-05 | Letterhead | Firm profile letterhead style and accent | Printed documents use them | PDF |
| STU-DOC-06 | Large letter `[B]` | 50 pages | Opens and prints without hanging | timing |
| STU-DOC-07 | Document editor role | As Document editor | Can edit letters; cannot issue invoices | UIA |

## S6 Billing, books, reports and tax

Money results are S1 when wrong.

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| STU-BIL-01 | Invoice: create | Client, project, lines, GST/TDS as set | Total, tax and net equal a hand calculation (write the calculation in the report) | UIA, PDF |
| STU-BIL-02 | Invoice: issue and number | Issue | Number allocated once; locked from edit; journal posted | ledger |
| STU-BIL-03 | Receipt | Record a receipt against an invoice | Outstanding falls by the amount; TDS on receipt booked | ledger |
| STU-BIL-04 | Part payment and overpayment `[B]` | Part receipt; receipt above the invoice | Part reduces balance; overpayment refused or booked as advance (record which) | ledger |
| STU-BIL-05 | Write off | Write off an uncollected bill | Goes to bad debts; client ledger clears | ledger |
| STU-BIL-06 | Void/credit | Void or credit-note an issued invoice | Reverses the journal; numbering gap handled as documented | ledger |
| STU-BIL-07 | Bank statement import (CSV) | Map columns of the sample CSV; import | 20 rows imported; duplicate import is detected and skipped | UIA, row count |
| STU-BIL-08 | Bank statement import (Excel) | Same with `.xlsx` | Same result | UIA |
| STU-BIL-09 | Bank reconciliation | Match imported rows to receipts and payments | Matched items cleared; unmatched listed; difference shown | UIA |
| STU-BKS-01 | Expenses | Add, edit, delete | Journal posted for each; deleting reverses | ledger |
| STU-BKS-02 | Income | Add other income | Posted | ledger |
| STU-BKS-03 | Salaries | Run a month | Per-employee amounts, deductions; journal posted once; running twice refused or warned | ledger |
| STU-BKS-04 | Assets | Add an asset and depreciation | Schedule correct to hand calculation | UIA |
| STU-BKS-05 | Journals | Manual journal: balanced; unbalanced `[B]` | Balanced posts; unbalanced refused | UIA |
| STU-BKS-06 | Per-client and per-project ledger | Open both | Totals equal the sum of their entries | UIA |
| STU-REP-01 | Financial reports | Trial balance, P&L, balance sheet | Debits equal credits; balance sheet balances; figures equal the ledgers | UIA, PDF |
| STU-REP-02 | Tax | GST, TDS summaries | Agree with the entries by period | UIA |
| STU-REP-03 | Chart of accounts and financial years | Add an account; close a year | Closed period refuses new entries; only Owner can close | UIA |
| STU-REP-04 | 26AS check | Load a 26AS file; compare | TDS booked on receipts matched, differences listed | UIA |
| STU-REP-05 | Print / PDF of each report | Ctrl+P | Matches the screen; page breaks sensible | PDF |

## S7 Office

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| STU-OFF-01 | Firm profile | Name, address, PAN, GSTIN, registration, bank, letterhead, accent | Saved; appears on documents; also seen read-only in O-CoreOp and O-Draft | UIA, other apps |
| STU-OFF-02 | Staff | Add, edit, leave | Saved | UIA |
| STU-OFF-03 | Leave | Apply and approve leave | Balance changes | UIA |
| STU-OFF-04 | Attendance | Mark attendance | Saved per day; monthly summary | UIA |
| STU-OFF-05 | Office library | Add a document | Stored and openable | UIA |
| STU-OFF-06 | Security | Admin password change; recovery code regenerate | Old credentials refused | launcher |
| STU-OFF-07 | Backup | **Backup** | A copy of the company file is made; opens as a company; protected files give a sealed backup | open the copy |
| STU-OFF-08 | Activity/audit | Perform changes; open the audit trail as Auditor | Each change recorded with user and time; cannot be edited | UIA |

## S8 Suite hub, approvals and cloud sync pages

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| STU-SUI-01 | Suite page cards | Open Suite | Cards for O-Studio, O-CoreOp, O-Draft with version and installed state | UIA |
| STU-SUI-02 | Launch from card | Launch O-CoreOp from the card | Starts signed in (ticket), no password | process |
| STU-SUI-03 | Suite monitor | Open the monitor panel | Shows who has the company file open, size; no names or amounts of records | UIA |
| STU-SUI-04 | Approvals inbox | Share an estimate from O-CoreOp; open Approvals | Draft appears with kind and project; approve / reject; decision visible in O-CoreOp | both apps |
| STU-SUI-05 | Assistant bar | Ask a question about the practice | Answer from read-only tools only; a change request is refused or needs a preview and approval | UIA |
| STU-SUI-06 | Assistant, host down | Stop the AI host | A clear message, not a hang | UIA |
| STU-CLD-01 | Cloud sync page, no keys | Open Cloud sync with no `neon.env` | "not configured" message with the path to create it | UIA |
| STU-CLD-02 | Test connection | With the test branch keys, **Test connection** | Success message; with a wrong password a failure message that does not print the password | UIA |
| STU-CLD-03 | Enable sync for a project | Sign in through the launcher; in O-Studio enable a project with an `.osp` | The launcher (not O-Studio) pushes on save; revision shown; the page says background sync runs in the launcher and shows the launcher's Cloud sync line; there is no on/off toggle | UIA, Neon row |
| STU-CLD-04 | Pull | Change the cloud copy from a second machine/profile; pull | Local `.osp` revision increases; conflict handled as documented | file inspect |
| STU-CLD-05 | Conflict | Change both sides | The conflict is shown, nothing is silently overwritten | UIA |
| STU-CLD-06 | Offline | Disconnect the network | App keeps working; the launcher's sync retries and resumes later | UIA |
| STU-CLD-07 | Push now / Pull now without the launcher | Quit the launcher's sync (sign out in the launcher), then press Push now on a project | The push goes through a short-lived connection and reports the result | UIA |

## S9 Shell and general

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| STU-SHL-01 | Shortcuts | Ctrl+N, S, P, F, K | New record, save, print, search, go to page | UIA |
| STU-SHL-02 | Go to page | Ctrl+K, type a page name | Jumps to it | UIA |
| STU-SHL-03 | Search | Ctrl+F in lists | Filters; clearing restores | UIA |
| STU-SHL-04 | Theme | Light, dark, system, high contrast | All pages readable; no clipped text; contrast usable | screenshots |
| STU-SHL-05 | Window sizes | 1280x720, 900 px, 520 px wide | Pane collapses below 900 px; pages wrap down to 520 px; no horizontal scroll of the page | screenshots |
| STU-SHL-06 | Keyboard only | Tab through each page | Every control reachable; visible focus; accessible names | UIA |
| STU-SHL-07 | Bottom status bar and Ask bar | Observe | Same shell as O-Draft and O-CoreOp (shared bar) | screenshot |
| STU-SHL-08 | Window placement | Move, resize, close, reopen | Position remembered; never off-screen on a changed monitor setup | screenshots |
| STU-SHL-09 | Updates | **Check for updates** | A clear "not installed automatically" message; no crash | UIA |
| STU-SHL-10 | Help | Open each help page | Opens, matches current screens (note stale pages as S4 bugs) | UIA |
| STU-SHL-11 | Large data `[B]` | 5,000 clients/records | Lists scroll and search without freezing over 2 s | timing |
| STU-SHL-12 | Unsaved draft on close | Close with an unsaved draft | Asks to save | UIA |
| STU-SHL-13 | Engine errors | Provoke a validation error | A readable message at the top; the page state stays | UIA |
