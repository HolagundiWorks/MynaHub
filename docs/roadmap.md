# Roadmap: O-Studio and O-CoreOp

Written 2026-10-19. It covers the two apps that exist today, O-Studio (office and projects) and O-CoreOp (quantities, estimates and billing). O-Draft is separate; its hand-off is in the OSAS repository (`docs/handoff-o-draft-rebrand.md`).

Each item says how we know it is open. **Checked** means it was looked at in the code or by running the app. **Not tested** means nobody has tried it.

## Where things stand

| | O-Studio | O-CoreOp |
|---|---|---|
| Repository | `HolagundiWorks/O-STUDIO`, `master` | `HolagundiWorks/O-COREOP`, `main` |
| Window | WinUI 3, `native/Adms.Desktop` | WinUI 3, `BBSDesktop/BBSApp` |
| Engine | Rust, `src-tauri` and `office-core`, one `.adms` company file | C# and its own project file |
| Shared design | `hcw-ui`, orange | `hcw-ui`, blue |
| Tests | 159 engine tests, 40 office-core, UI by hand | Unit tests for import and sign-in; UI by hand |
| CI | None | None |
| Accessibility | WCAG 2.2 AA audit done, see `wcag-audit.md` | Same |

## Phase 1: finish what is half done (now)

| # | Item | App | Evidence |
|---|---|---|---|
| 1.1 | ~~Merge the O-CoreOp shell into `main`~~ Done 2026-10-19 (merge commit `a0aa37d`, with the AORMS sign-in and Online projects branch) | CoreOp | Checked |
| 1.2 | Add continuous integration: build, `cargo test`, `dotnet test`, the static QA test | Both | Checked: neither repository has a workflow |
| 1.3 | Narrator and NVDA pass over sign-in, create a client, save a record, read a table. Confirms audit item O5 | Both | Not tested; automation cannot do this |
| 1.4 | ~~Check every page at 520 px (O3)~~ Done 2026-10-19: all 31 pages, no loss of content. Cosmetic follow-up: narrow table columns break words (the header row misalignment is fixed) | Studio | Checked |
| 1.5 | Field-level errors (O6): done for the record pages (including errors the engine returns, matched by field label), transmittals, time book and letters. The password boxes are marked too. Still open: engine errors on the pages that are not record pages | Studio | Checked |
| 1.6 | Test at 200% text size, 400% zoom and Windows high contrast | Both | Not tested |
| 1.7 | Run the installers end to end on a clean machine (O-Studio NSIS, O-CoreOp Inno) | Both | Checked: both scripts exist and were built; no clean-machine install was done |
| 1.8 | Choose a licence for O-Studio and for the OSAS repository | Studio | Open decision, see the end of `suite-architecture.md` |

## Phase 2: join the apps up

| # | Item | App | Evidence |
|---|---|---|---|
| 2.1 | Apply the AORMS migration `0113_aqc_sessions_aos_app.sql` and test the portal sign-in and project pull against the real portal | Studio, CoreOp | Checked: the change is on branch `claude/aos-suite-session` of the portal repository and is not applied; the portal client has only been tested against a fake |
| 2.2 | ~~O-CoreOp reads the shared company file~~ Built 2026-10-19 (see `docs/shared-company-file.md`): both apps open the file at once, projects and clients exist once, O-CoreOp keeps a project's quantities as a revisioned document in the file. Branch `feat/shared-company-file` in the O-CoreOp repository, not merged. Live sync built too: each app polls every two seconds and updates what it shows (2026-10-19). Still to do: normalise the document into tables, `ProjectRef` on O-CoreOp outputs | Both | Checked: 5 tests in `office-core`, 1 in `office-ffi`, 5 against the real engine in `Office.Shared.Tests`; the app builds and starts; opening a file through the screens has not been clicked through |
| 2.3 | `ProjectRef` and `ArtifactRef`: **written and implemented in `office-core`** (`docs/contracts/refs.md`, with a JSON schema and three operations on the office interface). Still to do: O-CoreOp and O-Draft producing and reading them, and apps computing checksums | Both | Checked: 6 tests in `office-core`, 1 in `office-ffi` |
| 2.4 | Estimates and running bills: O-CoreOp calculates, O-Studio shows the result against the fee proposal | Both | Checked: O-Studio has fee proposals but no link to quantities |
| 2.5 | An MCP tool server for O-CoreOp, with the same descriptor shape as O-Studio's | CoreOp | Checked: only O-Studio has one (`office-agent`) |
| 2.6 | Write tools for the agent, with approval and an audit trail | Studio | Checked: every tool is read-only today |
| 2.7 | The OSAS launcher: an installer for the suite, update check, open a company file from the launcher | OSAS | Listed as not built in its README |

## Phase 3: depth

| # | Item | App |
|---|---|---|
| 3.1 | Code signing for the installers, and an update path | Both |
| 3.2 | Automated UI tests with UI Automation for the main flows, run in CI | Both |
| 3.3 | Check O-CoreOp quantities and purchase-order output against a real project, and add the cases it exposes | CoreOp |
| 3.4 | O-Studio: reports and presentations are records only; a printable pack per project | Studio |
| 3.5 | ShilpiDB details store and search with sources (suite phase 4) | Suite |

## Code and documentation debt

| # | Item | State |
|---|---|---|
| D1 | The earlier Carbon interface (`src/`, `index.html`, `vite.config.ts`, `package.json`, Tauri's web window) duplicates the WinUI app. Retire it, keep the Rust engine, and make `src-tauri` a library only | **Decision needed.** The engine and its tests do not depend on it, but `tauri.conf.json`, the Vitest tests and the NSIS bundle script do |
| D2 | `README.md` says no installer has been built and tells you to build the debug engine | Fixed in this change |
| D3 | The old amber and ink logo set (`brand/`, `tools/brand`) | Removed in this change |
| D4 | Sample PDFs from the first template library (`docs/sample-pdfs`) | Removed in this change; they were not referenced |
| D5 | The engine's bundle icons were the old amber mark | Replaced with the O-Studio mark in this change |
| D6 | Old names remain in code on purpose: `aos` and `aqc` session names, `AOSENC1`, `AQCCore.exe`, the crate names. Changing them needs a migration | Keep; list them in `suite-architecture.md` |
| D7 | Build output was lying about (about 10 GB across the repositories) | Cleaned in this change; `.gitignore` already covers it |

## Order of work

1. Phase 1.1 to 1.2 first: merge and CI, so everything after is checked on each push.
2. 1.3 to 1.6 together, as one accessibility pass, then close the audit.
3. D1 once the owner decides.
4. Phase 2 in the order 2.1, 2.3, 2.2, 2.4. The rest of phase 2 needs 2.3.
