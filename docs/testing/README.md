# Suite test program

**Audience:** the Cursor agent that runs the suite's tests, and the people who read its reports.
**Scope:** every feature of MynaHub, MynaOps, MynaCAD and Myna, one by one; then the apps working together; then bug reports, fixes and re-tests.
**Canonical copy:** MynaHub repository, `docs/testing/`. MynaCAD and MynaOps keep a short pointer (`docs/TEST-PROGRAM.md`).
**Terms:** names, files and links are in [../SUITE.md](../SUITE.md). Use the names in that file in every report.

## 1. What the agent must produce

For one test run the agent produces, and commits to the repository it tested:

| Output | Where | Format |
|---|---|---|
| Run report | `docs/testing/runs/<yyyy-mm-dd>-<app>.md` | [RESULTS-TEMPLATE.md](RESULTS-TEMPLATE.md) |
| Bug reports | one row in [BUG-LOG.md](BUG-LOG.md) and, for S1 and S2, one file `docs/testing/bugs/BUG-<id>.md` | [BUG-PROCESS.md](BUG-PROCESS.md) |
| Fixes | branch `fix/BUG-<id>-<short-name>` with the code change, a regression test and a doc update | [BUG-PROCESS.md](BUG-PROCESS.md) |
| Evidence | `docs/testing/evidence/<run>/` | screenshots (PNG), log excerpts, command output (text). No installers, no databases, no user data |

A test is not "passed" because nothing crashed. It is passed when the **Expected** column was observed and the observation is written in the run report.

## 2. Order of work

1. **Prepare** the machine and the build: [TEST-ENVIRONMENT.md](TEST-ENVIRONMENT.md).
2. **Smoke test** each app (the first section of each plan): it installs, starts, shows its main window, closes. Stop and report if any smoke test fails (S1).
3. **Feature tests, app by app**, in this order, because later apps need earlier ones:
   1. [PLAN-O-STUDIO.md](PLAN-O-STUDIO.md) (company sign-in lives here; the launcher plan, [PLAN-LAUNCHER.md](PLAN-LAUNCHER.md), is retired with the launcher)
   2. [PLAN-O-DRAFT.md](PLAN-O-DRAFT.md)
   3. [PLAN-O-COREOP.md](PLAN-O-COREOP.md)
4. **Connected-app tests:** [PLAN-INTEGRATION.md](PLAN-INTEGRATION.md).
5. **Bugs:** log each one as found (do not batch). After a plan section is finished, fix its S1/S2 bugs, re-run the failed cases and the regression cases, then continue.
6. **Report:** fill the run report, update the status column of every case, list what was not run and why.

## 2a. When a plan and the documentation disagree

The feature's own documentation (help pages, `docs/status.md`, README) says what the feature should do; the plan's **Expected** column was written from it and from the code layout. If the app does what the documentation says and the plan says otherwise, correct the plan and note it in the run report. If the app and the documentation disagree, log a bug (S3, or S2 if money or data is involved) and say which one looks right. Some rows describe behaviour that was inferred rather than seen: where the plan says "record the observed behaviour", write what happened and let the owner decide.

## 3. Case format

Every plan lists cases as rows: `ID | Feature | Steps | Expected | How to check`.

- **ID** `<APP>-<AREA>-<NN>` and never reused. A case that is dropped is marked *retired*, not deleted.
- **Steps** are what a person would do. The agent may use the automation in "How to check" instead of the mouse.
- **Expected** is a specific, observable result: a value, a file, a visible control, a message. If a row says only "works", treat it as incomplete and write the real expectation into the run report.
- **Result values:** `PASS`, `FAIL` (a bug is logged), `BLOCKED` (something earlier failed; name it), `NOT RUN` (say why), `N/A` (feature not built; point to the doc that says so).
- Each feature has a **normal**, a **boundary or bad-input** and a **repeat/undo** case where it makes sense. The plans mark them `[N]`, `[B]`, `[R]`.

## 4. Rules the agent follows

1. **Tests use test data only.** Make a new test company (`TestCo-<date>.osas`), test users and test projects. Never open, change or sign in to the user's real company file (`HCW.osas`) or real projects. Generate test passwords (12 or more characters) and record them only in the gitignored `docs/testing/.local/credentials.txt`, never in a report, commit or chat.
2. **Do not touch the user's personal files** (anything outside the repositories, `%LOCALAPPDATA%\O-Studio`, `%LOCALAPPDATA%\OSAS` and the test folder). Never delete a real company file, a real project file or the shared model in `%LOCALAPPDATA%\HumanCentricWorks\models`.
3. **Cloud sync tests use a test Neon branch only** (see PLAN-INTEGRATION). Never point tests at the production branch. Keys stay in `%LOCALAPPDATA%\OSAS\neon.env`; they are never copied into a report or commit.
4. **Do not change the code under test while testing it.** Log the bug first; fix it on a branch afterwards ([BUG-PROCESS.md](BUG-PROCESS.md)).
5. **Git hygiene:** use `git add <paths>` (never `git add -A` on installer or build folders), check `git diff --cached --name-only` for `.exe`, `.dll`, `.zip`, `.msi`, `.osp`, `.osas` before committing, never force-push, never push to `main`/`master` directly: push the fix branch and open a pull request.
6. **Report honestly.** A case that could not be run is `NOT RUN`, not `PASS`. If a fix could not be verified, say so.
7. **Windows quirks:** see the project memory notes (heredocs, PowerShell aliases, cp1252 sources, UTF-8 BOM on `.ps1`). Write scripts with a file tool, then run them.

## 5. Severity

| Level | Meaning | Examples | Target |
|---|---|---|---|
| **S1 Blocker** | Cannot install or start, data loss or corruption, security hole, wrong money or quantity result | app crashes on launch; `.osp` loses the drawing on save; invoice total wrong; password accepted when wrong | fix before anything else |
| **S2 Major** | A feature does not work, or works wrongly, with no workaround | Take-off page shows no drawings; Save As fails; menu item does nothing | fix in this cycle |
| **S3 Minor** | Works, but wrong in a smaller way, or a workaround exists | wrong label, misaligned control, unclear message | fix when convenient |
| **S4 Cosmetic** | Spelling, spacing, icon | typo, 2 px offset | batch |

## 6. Coverage map

| App | Plan | Areas |
|---|---|---|
| (Myna launcher, removed 2026-11-11) | [PLAN-LAUNCHER.md](PLAN-LAUNCHER.md) | retired; kept as history |
| MynaHub | [PLAN-O-STUDIO.md](PLAN-O-STUDIO.md) | company file, users and roles, practice register, documents, billing, books, reports and tax, office, tasks, approvals, suite and cloud sync pages, help, shell |
| MynaCAD | [PLAN-O-DRAFT.md](PLAN-O-DRAFT.md) | files, drawing, modify, dimensions, layers, layouts, architecture tools, levels and sheet sets, take-off, electrical, smart blocks, PDF, history and revisions, project file, colour dialog, scripting and automation, AI tools, shell |
| MynaOps | [PLAN-O-COREOP.md](PLAN-O-COREOP.md) | project settings, levels, RCC, civil BOQ, openings, take-off page, CAD bridge, item links, rate book, estimate, PO and reports, personas, schedule, contracts, accounts, stores, HR, assistant, project file, shell |
| Connected | [PLAN-INTEGRATION.md](PLAN-INTEGRATION.md) | launcher removal on upgrade, AI agent hosted by MynaOps (window and headless), project database manager, `.osp` live sync, cloud sync, approvals share, install, upgrade and uninstall |
