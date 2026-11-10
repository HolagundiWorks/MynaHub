# Roadmap: MynaHub and MynaOps

Written 2026-10-19. It covers the two apps that exist today, MynaHub (office and projects) and MynaOps (quantities, estimates and billing). MynaCAD is separate; its hand-off is in the OSAS repository (`docs/handoff-o-draft-rebrand.md`).

Each item says how we know it is open. **Checked** means it was looked at in the code or by running the app. **Not tested** means nobody has tried it.

## Where things stand

| | MynaHub | MynaOps |
|---|---|---|
| Repository | `HolagundiWorks/O-STUDIO`, `master` | `HolagundiWorks/O-COREOP`, `main` |
| Window | WinUI 3, `native/Adms.Desktop` | WinUI 3, `BBSDesktop/BBSApp` |
| Engine | Rust, `src-tauri` and `office-core`, one `.osas` company file (earlier `.adms` files open) | C# and its own project file |
| Shared design | `O-UI` (`O.Ui`), orange | `O-UI` (`O.Ui`), blue |
| Tests | 159 engine tests, 40 office-core, UI by hand | Unit tests for import and sign-in; UI by hand |
| CI | Yes (`.github/workflows/ci.yml`: rust crates + WinUI build) | Yes (`.github/workflows/ci.yml`) |
| Accessibility | WCAG 2.2 AA audit done, see `wcag-audit.md` | Same |

## Phase 1: finish what is half done (now)

| # | Item | App | Evidence |
|---|---|---|---|
| 1.1 | ~~Merge the MynaOps shell into `main`~~ Done 2026-10-19 (merge commit `a0aa37d`; AORMS Connect that landed with that merge was later removed from OSS apps) | CoreOp | Checked |
| 1.2 | ~~Add continuous integration~~ Done for MynaHub (`rust`, `windows-app`). MynaOps has a workflow too | Both | Checked: workflows exist; keep them green |
| 1.3 | Narrator and NVDA pass over sign-in, create a client, save a record, read a table. Confirms audit item O5 | Both | Not tested; automation cannot do this |
| 1.4 | ~~Check every page at 520 px (O3)~~ Done 2026-10-19: all 31 pages, no loss of content. Cosmetic follow-up: narrow table columns break words (the header row misalignment is fixed) | Studio | Checked |
| 1.5 | Field-level errors (O6): done for the record pages (including errors the engine returns, matched by field label), transmittals, time book and letters. The password boxes are marked too. Still open: engine errors on the pages that are not record pages | Studio | Checked |
| 1.6 | Test at 200% text size, 400% zoom and Windows high contrast | Both | Not tested |
| 1.7 | Run the installers end to end on a clean machine (MynaHub NSIS, MynaOps Inno) | Both | Checked: both scripts exist and were built; no clean-machine install was done |
| 1.8 | ~~Choose a licence for MynaHub and for the OSAS repository~~ Done: both dual-licensed AGPL-3.0 or commercial (mirror CoreOp `LICENSING.md`) | Studio + OSAS | Checked |

## Phase 2: join the apps up

| # | Item | App | Evidence |
|---|---|---|---|
| 2.1 | ~~MynaHub AORMS portal sign-in and project pull~~ Removed; MynaHub uses the company file only | Studio | Checked: connector crate and portal page removed |
| 2.2 | ~~MynaOps reads the shared company file~~ Built 2026-10-19 (see `docs/shared-company-file.md`): both apps open the file at once, projects and clients exist once, MynaOps keeps a project's quantities as a revisioned document in the file. Live sync polls every two seconds. Still to do: normalise the document into tables | Both | Checked: office-core / office-ffi / Office.Shared.Tests; screens not fully click-tested |
| 2.3 | `ProjectRef` / `ArtifactRef` in `office-core`; MynaOps stamps `project_ref` and estimate `artifact_ref` on company-file saves; MynaCAD stamps take-off `project_ref` when linked | Both | Checked: office-core refs tests; CoreOp ArtifactRefs unit tests; Draft take-off tests |
| 2.8 | **Studio as the hub** (merge 2026-10-28): Studio holds company details, admin password, users and launcher cards; siblings follow with a one-time ticket (`docs/suite-hub.md`, `docs/studio-osas-merge.md`). Phase 1 restores create/open/People/cards in MynaHub; OSAS.exe chooser deprecated | Studio + siblings | Engine tickets checked; Studio Phase 1 UI landed |
| 2.4 | Estimates ↔ fee proposals: `project_estimate_summary` + Fee proposals “Use MynaOps estimate as cost”; `project_ra_summary` shows MynaOps running-bill (RA) totals beside fee/invoices. Still to do: E2E click-through, optional invoice ArtifactRef column | Both | Checked: office-core estimate/RA summary + proposal link tests |
| 2.5 | An MCP tool server for MynaOps, with the same descriptor shape as MynaHub's | CoreOp | Checked: only MynaHub has one (`office-agent`) |
| 2.6 | Write tools for the agent, with approval and an audit trail | Studio | Checked: every tool is read-only today |
| 2.7 | Myna installers (since 2026-11-10 three: Myna-Setup for MynaHub and the launcher, MynaOps-Setup, MynaCAD-Setup; before that one combined Myna Suite Setup); update check stub; launcher opens `.osp` via O-Studio.Launcher | MynaHub | **Checked 2026-11-04:** `installer/suite/O-Suite.iss` + `O-Suite-Wizard.iss`, `build-single.ps1`; signed single Setup and hosted channel still open (3.1) |

## Phase 3: depth

| # | Item | App |
|---|---|---|
| 3.1 | Code signing for the installers, and an update path | Both | `installer/sign.ps1` hooks exist; need a real Authenticode / Azure Trusted Signing cert and a hosted channel URL |
| 3.2 | Automated UI tests with UI Automation for the main flows, run in CI | Both |
| 3.3 | Check MynaOps quantities and purchase-order output against a real project, and add the cases it exposes | CoreOp |
| 3.4 | MynaHub: reports and presentations are records only; a printable pack per project | Studio |
| 3.5 | Reusable details store and search with sources (suite phase 4) | Suite |

## Code and documentation debt

| # | Item | State |
|---|---|---|
| D1 | ~~Retire the Carbon + Tauri web shell~~ Done: React `src/`, npm/Vite/Carbon packages, `carbon-ui` CI, and Tauri window packaging removed. `src-tauri` is a library/cdylib for WinUI; folder name kept so engine paths stay stable | Done |
| D2 | `README.md` says no installer has been built and tells you to build the debug engine | Fixed in this change |
| D3 | The old amber and ink logo set (`brand/`, `tools/brand`) | Removed in this change |
| D4 | Sample PDFs from the first template library (`docs/sample-pdfs`) | Removed in this change; they were not referenced |
| D5 | The engine's bundle icons were the old amber mark | Replaced with the MynaHub mark in this change |
| D6 | Old names remain in code on purpose: `aos` and `aqc` session names, `AOSENC1`, `AQCCore.exe`, the crate names. Changing them needs a migration | Keep; list them in `suite-architecture.md` |
| D7 | Build output was lying about (about 10 GB across the repositories) | Cleaned in this change; `.gitignore` already covers it |

## Order of work

1. Phase 1.1 / 1.2 / 1.8 / D1 / most of Phase 2 SoR wiring are done. Keep CI green.
2. **Next trust gates:** Windows GUI E2E (estimate → fee → invoice; Draft take-off → CoreOp); Authenticode / Azure Trusted Signing certs + hosted update channel; counsel review of dual AGPL+commercial; Draft interactive project-picker UI.
3. 1.3 to 1.6 together as one accessibility pass, then close the audit.
4. Remaining Phase 2 nice-to-haves: MCP on CoreOp (2.5), write tools (2.6), open-company-from-launcher.
