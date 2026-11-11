# HolagundiWorks suite: architecture and integration map

> **Update 2026-10-30:** projects live in a `.osp` project file shared by MynaCAD and MynaOps; MynaHub will hold the cloud sync. The current names, roles, toolbars and links are in [SUITE.md](SUITE.md); where this map disagrees, SUITE.md is newer.

Status: written 2026-10-18 from a read of the repositories and the code in this one. Every statement marked **verified** was read in a repository or run. Everything marked **proposed** is a recommendation for the owner to decide, not something that exists.

## Names

The suite is **OSAS, the Open Source Architecture Suit**. Some code identifiers keep older spellings because files and servers already use them (the `aos`, `aqc` and `aad` portal session names, the `AOSENC1` file marker, the `AQCCore.exe` program name, the `aad-*` crates). Those are identifiers, not names.

| App | For | Accent | Repository |
|---|---|---|---|
| **MynaHub** | Office and project management | Orange | `HolagundiWorks/O-STUDIO` |
| **MynaOps** | Quantities, estimates, billing | Blue | `HolagundiWorks/O-COREOP` |
| **MynaCAD** | Drafting | Black | `HolagundiWorks/O-DRAFT` |

The shared design is the public `HolagundiWorks/O-UI` library (assembly `O.Ui`, MIT licensed; the marks in its brand folder have their own notice), and the folder has the marks for all three apps in their colours. MynaHub and MynaOps use the same responsive layout: a pane that collapses below 900 px, and pages that wrap down to 520 px. The accessibility audit is `docs/wcag-audit.md`.

## 1. The products today (verified)

| Product | Repository | What it is now | Stack | Licence |
|---|---|---|---|---|
| **MynaHub** | `HolagundiWorks/O-STUDIO` | Offline office app: clients, projects, letters, invoices, accounts, bank import, tax, staff, library, proposals, meetings, attendance, site records, permits, 26AS check. One `.adms` company file | Rust engine (cdylib) + WinUI 3 shell over a C interface | AGPL-3.0, or commercial |
| **MynaCAD** | `HolagundiWorks/O-DRAFT` | 2D CAD in Rust: drafting, DXF and DWG, layouts, plotting, architecture tools, take-off into `.osp`, an offline local-model agent (Qwen3), an MCP server, a JSON control channel | Rust, wgpu, WinUI 3 shell (`aad-host.dll`) | MIT or Apache-2.0 |
| **MynaOps** | `HolagundiWorks/O-COREOP` | Quantity take-off, BOQ, rate books, DSR estimates, bar bending schedules, purchase orders, running-account billing, scheduling, correspondence, stores and HR, an opt-in AI assistant, a CAD bridge importer | C# WinUI 3, C++ `bbs_engine.dll` | AGPL-3.0, or commercial |
| **MynaUI** | `HolagundiWorks/O-UI` | Shared WinUI themes, marks, SuiteShell chrome (`O.Ui`) | .NET 8 class library | MIT (marks reserved) |

OSS suite apps do **not** ship a portal connector. A hosted practice portal (if any) will live in separate forks later — not in these open-source trees.

The product descriptions in the suite plan you shared are targets. MynaHub keeps project identity in the company file (`company_uuid` + `project_id`) while working data lives in each project's `.osp`.

## 2. How the pieces connect today (verified)

```
        MynaCAD (CAD)  ---- hcw-aqc-bridge JSON / .osp take-off ----  MynaOps (quantities)
           |                                                      |
           \____________ offline company file (office.dll) _______/
                                    |
                              MynaHub (office)
```

- **Sign-in:** Each app signs in to the local company file (OSAS ticket / `office.dll`). There is no shared portal credential store in the OSS trees.
- **CAD to quantities:** MynaCAD writes take-off rows into the shared `.osp` (or a `hcw-aqc-bridge` v1 JSON file for the offline path; the MynaOps repository documents the contract in `docs/bridge/`), and MynaOps imports it.
- **Project details:** MynaHub keeps clients, projects and site address in the company file; Draft/CoreOp can consume company-file `ProjectRef` / details offline.
- **AI:** MynaCAD has its own agent (`crates/agent`, a fixed tool registry with confirmation rules, audit log and an offline model) and an MCP server. MynaOps has an opt-in assistant. MynaHub now has `office-agent`, a read-only MCP server over the office records. Each is separate.

## 3. What was built in this repository for the suite (verified, with tests)

| Piece | Purpose | Tests |
|---|---|---|
| `office-core/` | The company file, sign-in, roles, schema tracks, shared records (clients, projects, briefs, staff, leave, library, proposals, meetings, attendance, 26AS) | 40 |
| `office-ffi/` and `native/Office.Interop` | A C interface and .NET wrapper so another native app can use the same file. Contract in `docs/office-abi.md` | 3, plus a console smoke test |
| `office-agent/` | Governed read-only tools and an MCP server (`office-mcp`) | 8, including the real process |
| Schema tracks (`office-core::track`) | Lets an app add its own tables to the shared file without touching the others' | 4 |

## 4. Recommendations (proposed)

### 4.1 One project identity

**Decision (2026-10-28):** the company file is the system of record. `ProjectRef = { v, company_uuid, project_id }`. Portal connectors are out of the suite apps.

MynaOps stamps `project_ref` and estimate `artifact_ref` on company-file documents; MynaHub fee proposals read `project_estimate_summary` (and may store that ArtifactRef) and surface RA totals via `project_ra_summary`. Offline bridge import parses `source.project_ref` when Draft writes it.

### 4.2 Contracts, not shared databases

Keep each app's data its own. Share through a small versioned contract package. Start with the ones that already exist and name the gaps:

| Contract | State |
|---|---|
| Company-file auth | In MynaHub only (`office.dll` engine); tickets and the launcher are retired (2026-11-08 and 2026-11-11) |
| CAD to quantities (`hcw-aqc-bridge` v1) | Exists, in the MynaOps repo |
| Office records (`docs/office-abi.md`) | Exists, in this repo |
| `ProjectRef` and `ArtifactRef` (a drawing, an estimate or a document with revision, checksum and the revisions it was made from) | **Written** (`docs/contracts/refs.md`) in `office-core`; produced by CoreOp estimates / Draft take-off when company-linked; consumed by Studio fee proposals |
| Tool schema (below) | Partly exists: MynaCAD's registry, `office-agent`. No common definition |

### 4.3 One AI platform, built on MCP

MCP is already how MynaCAD (`aad-cli mcp`) and MynaHub (`office-mcp`) expose tools, and MynaOps's CLAUDE notes already treat the bridge as a file contract. Rather than a new runtime, propose:

1. Every app exposes its tools as an MCP server with typed input schemas, and refuses unknown arguments. MynaHub does this. MynaCAD does. MynaOps does not yet.
2. A thin suite host (the "workspace" in the plan) is an MCP client that connects to the apps' servers, holds the active `ProjectRef`, applies the approval policy, and writes one audit trail. This is new work.
3. Tool names carry the app's namespace: `cad.*`, `aqc.*`, `aos.*`. MynaHub's tools are currently `clients.search`, `projects.list`, and so on, and would be renamed to `aos.*` when the host exists.
4. Writes are proposals first. A tool that changes data returns a preview and needs an explicit approval step; only reads exist today.
5. Local and hosted models are interchangeable behind the host. Offline mode means no project content leaves the machine, and the host enforces it.

### 4.4 Reusable details (proposed)

A library of approved drawing details and specifications is not built yet. Proposed order: define what a detail record is (geometry reference, metadata, approval state), store it in the project file or company library, then expose search through MCP. Until then, `office-core`'s library (specifications, standards, lessons) holds the written side.

## 5. Repository by repository (proposed)

| Repo | Add | Interface | Acceptance test |
|---|---|---|---|
| MynaHub (this) | Rename tools to `aos.*`; project export `ProjectRef`; draft-estimate document from a MynaOps result | `office-ffi`, `office-mcp`, a new document-draft tool | An estimate document is created from a supplied BOQ and names its sources |
| MynaCAD | Finish `aqcexport` if not done; add `ProjectRef` to the bridge file; a tool to list measurable elements | The bridge file, `aad-cli mcp` | A sample drawing exports a file MynaOps imports with the same project id |
| MynaOps | An MCP server around its calculation engine (`aqc.calculate_boq`, `aqc.compare_estimates`); record drawing revision and rate schedule in each estimate | MCP over stdio; Office C interface for the shared records | The same take-off gives the same BOQ twice, and the estimate names its inputs |
| Suite host (new) | The MCP client, project selector, approval and audit | MCP | See section 7 |

Licence boundary: MynaHub and MynaOps are AGPL-3.0 or commercial; MynaCAD is MIT or Apache-2.0; MynaUI is MIT (marks reserved). Share contracts and documents, not source, across the AGPL↔permissive line.

## 6. Phases (proposed, with exit criteria)

1. **Contracts.** `ProjectRef`, `ArtifactRef`, tool schema and revision rules written down and agreed. Exit: MynaHub, MynaCAD and MynaOps each produce and read a `ProjectRef` without changing ownership of any data.
2. **Tool servers.** MynaOps gets an MCP server; MynaHub and MynaCAD tools are renamed and given the same descriptor shape. Exit: one MCP client lists the tools of all three.
3. **Suite host and the first workflow.** The host connects, resolves the active project, and runs drawing to estimate: MynaCAD supplies measurements, MynaOps calculates, MynaHub drafts the document. Unsupported geometry and missing measurements are reported, never guessed. Exit: a real project completes the workflow with traceable inputs and a human approval.
4. **Knowledge.** Approved details and document search with source references.
5. **Product.** Installer coordination, shared settings, cross-version tests.

## 7. The first milestone, and where it stands

| Step | State |
|---|---|
| Create or open a project in MynaHub | Works |
| Share the project with MynaCAD / MynaOps via `ProjectRef` | Company-file `ProjectRef` wired for CoreOp saves and Draft take-off; Draft project-picker UI still open |
| Open its drawing in MynaCAD | Company-file link via `companyfile` / OSAS ticket; interactive project picker UI still open |
| Resolve the same project in MynaOps | Company projects dialog + OSAS ticket; `.osp` project file |
| Search approved details | Not built yet |
| Ask an assistant to inspect the drawing and propose a take-off | MynaCAD's agent can inspect a drawing; take-off tools are reserved but not in its registry |
| Calculate quantities in MynaOps and draft the estimate in MynaHub | Estimate + RA totals surface on fee proposals / invoices via `project_estimate_summary` / `project_ra_summary`; full draft-estimate document tool and Windows GUI E2E still open |
| Refuse unauthorised writes and keep an audit trail | Done for MynaHub reads: refusals and calls are audited. No write tools exist |

## 8. Decisions needed from the owner

1. ~~Portal vs company-file SoR~~ — company file (2026-10-28).
2. The bridge file must carry `ProjectRef` for offline sharing; does MynaHub also need a dedicated export?
3. ~~What licence does MynaHub carry?~~ **Settled:** MynaHub is AGPL-3.0 or commercial (same dual path as MynaOps; see `LICENSING.md`). Shared design `MynaUI` / `O.Ui` remains MIT; marks reserved.
4. Is the suite host a separate small app, or part of MynaHub?
5. What is a reusable "detail" record, precisely?
