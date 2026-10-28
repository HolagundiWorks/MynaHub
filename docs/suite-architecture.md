# HolagundiWorks suite: architecture and integration map

Status: written 2026-10-18 from a read of the repositories and the code in this one. Every statement marked **verified** was read in a repository or run. Everything marked **proposed** is a recommendation for the owner to decide, not something that exists.

## Names

The suite is **OSAS, the Open Source Architecture Suit**. Some code identifiers keep older spellings because files and servers already use them (the `aos`, `aqc` and `aad` portal session names, the `AOSENC1` file marker, the `AQCCore.exe` program name, the `aad-*` crates). Those are identifiers, not names.

| App | For | Accent | Repository |
|---|---|---|---|
| **O-Studio** | Office and project management | Orange | `HolagundiWorks/O-STUDIO` |
| **O-CoreOp** | Quantities, estimates, billing | Blue | `HolagundiWorks/O-COREOP` |
| **O-Draft** | Drafting | Black | `HolagundiWorks/O-DRAFT` |

The shared design is the public `HolagundiWorks/O-UI` library (assembly `Hcw.Ui`, MIT licensed; the marks in its brand folder have their own notice), and the folder has the marks for all three apps in their colours. O-Studio and O-CoreOp use the same responsive layout: a pane that collapses below 900 px, and pages that wrap down to 520 px. The accessibility audit is `docs/wcag-audit.md`.

## 1. The products today (verified)

| Product | Repository | What it is now | Stack | Licence |
|---|---|---|---|---|
| **O-Studio** | `HolagundiWorks/O-STUDIO` | Offline office app: clients, projects, letters, invoices, accounts, bank import, tax, staff, library, proposals, meetings, attendance, site records, permits, 26AS check. One `.adms` company file | Rust engine (cdylib) + WinUI 3 shell over a C interface | AGPL-3.0, or commercial |
| **O-Draft** | `HolagundiWorks/O-DRAFT` | 2D CAD in Rust: drafting, DXF and DWG, layouts, plotting, architecture tools, take-off commands, an offline local-model agent (Qwen3), an MCP server, a JSON control channel, a `.vdb` store | Rust, wgpu, WinUI 3 shell (`aad-host.dll`) | MIT or Apache-2.0 |
| **O-CoreOp** | `HolagundiWorks/O-COREOP` | Quantity take-off, BOQ, rate books, DSR estimates, bar bending schedules, purchase orders, running-account billing, scheduling, correspondence, stores and HR, an opt-in AI assistant, a CAD bridge importer | C# WinUI 3, C++ `bbs_engine.dll` | AGPL-3.0, or commercial |
| **ShilpiDB** | `HolagundiWorks/shilpidb` | A spatial vector store: a binary codec, a grid index over bounding boxes, a TCP server (`shilpid`), a shell, an HTTP gateway and a WinUI browser. The roadmap says no entity types beyond a bounding box, no metadata, no importers and no embeddings yet | Rust, dependency-free | Not checked |

OSS suite apps do **not** ship a portal connector. A hosted practice portal (if any) will live in separate forks later — not in these open-source trees.

The product descriptions in the suite plan you shared are targets. Two differ from what exists: ShilpiDB is not yet a library of approved details and specifications (it stores bounding boxes), and O-Studio keeps project identity in the company file (`company_uuid` + `project_id`).

## 2. How the pieces connect today (verified)

```
        O-Draft (CAD)  ---- hcw-aqc-bridge JSON / ShilpiDB ----  O-CoreOp (quantities)
           |                                                      |
           \____________ offline company file (office.dll) _______/
                                    |
                              O-Studio (office)
           |
       .vdb store (embeds ShilpiDB codec and grid; can talk to shilpid)
```

- **Sign-in:** Each app signs in to the local company file (OSAS ticket / `office.dll`). There is no shared portal credential store in the OSS trees.
- **CAD to quantities:** O-Draft writes a `hcw-aqc-bridge` v1 JSON file (the O-CoreOp repository documents the contract in `docs/bridge/`), and O-CoreOp imports it. Take-off can also go through a shared ShilpiDB server.
- **Project details:** O-Studio keeps clients, projects and site address in the company file; Draft/CoreOp can consume company-file `ProjectRef` / details offline.
- **AI:** O-Draft has its own agent (`crates/agent`, a fixed tool registry with confirmation rules, audit log and an offline model) and an MCP server. O-CoreOp has an opt-in assistant. O-Studio now has `office-agent`, a read-only MCP server over the office records. Each is separate.

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

O-CoreOp stamps `project_ref` and estimate `artifact_ref` on company-file documents; O-Studio fee proposals read `project_estimate_summary` and may store that ArtifactRef. The CAD bridge file should still gain a `ProjectRef` for offline import.

### 4.2 Contracts, not shared databases

Keep each app's data its own. Share through a small versioned contract package. Start with the ones that already exist and name the gaps:

| Contract | State |
|---|---|
| Company-file auth / tickets | Exists via `office.dll` / OSAS launcher; shared by suite apps offline |
| CAD to quantities (`hcw-aqc-bridge` v1) | Exists, in the O-CoreOp repo |
| Office records (`docs/office-abi.md`) | Exists, in this repo |
| `ProjectRef` and `ArtifactRef` (a drawing, an estimate or a document with revision, checksum and the revisions it was made from) | **Written** (`docs/contracts/refs.md`) in `office-core`; produced by CoreOp estimates / Draft take-off when company-linked; consumed by Studio fee proposals |
| Tool schema (below) | Partly exists: O-Draft's registry, `office-agent`. No common definition |

### 4.3 One AI platform, built on MCP

MCP is already how O-Draft (`aad-cli mcp`) and O-Studio (`office-mcp`) expose tools, and O-CoreOp's CLAUDE notes already treat the bridge as a file contract. Rather than a new runtime, propose:

1. Every app exposes its tools as an MCP server with typed input schemas, and refuses unknown arguments. O-Studio does this. O-Draft does. O-CoreOp does not yet.
2. A thin suite host (the "workspace" in the plan) is an MCP client that connects to the apps' servers, holds the active `ProjectRef`, applies the approval policy, and writes one audit trail. This is new work.
3. Tool names carry the app's namespace: `cad.*`, `aqc.*`, `aos.*`, `shilpi.*`. O-Studio's tools are currently `clients.search`, `projects.list`, and so on, and would be renamed to `aos.*` when the host exists.
4. Writes are proposals first. A tool that changes data returns a preview and needs an explicit approval step; only reads exist today.
5. Local and hosted models are interchangeable behind the host. Offline mode means no project content leaves the machine, and the host enforces it.

### 4.4 ShilpiDB's role

ShilpiDB is a spatial store, and `aad-vdb` already embeds it. The reusable-details library in the plan would be a second layer on top (drawing blocks with metadata, specifications, search). Proposed order: first define what a "detail" record is (geometry reference, metadata, approval state), then add it to ShilpiDB, then add a `shilpi.search_details` tool. Until then, `office-core`'s library (specifications, standards, lessons) holds the written side.

## 5. Repository by repository (proposed)

| Repo | Add | Interface | Acceptance test |
|---|---|---|---|
| O-Studio (this) | Rename tools to `aos.*`; project export `ProjectRef`; draft-estimate document from an O-CoreOp result | `office-ffi`, `office-mcp`, a new document-draft tool | An estimate document is created from a supplied BOQ and names its sources |
| O-Draft | Finish `aqcexport` if not done; add `ProjectRef` to the bridge file; a tool to list measurable elements | The bridge file, `aad-cli mcp` | A sample drawing exports a file O-CoreOp imports with the same project id |
| O-CoreOp | An MCP server around its calculation engine (`aqc.calculate_boq`, `aqc.compare_estimates`); record drawing revision and rate schedule in each estimate | MCP over stdio; Office C interface for the shared records | The same take-off gives the same BOQ twice, and the estimate names its inputs |
| ShilpiDB | The detail record and its search | `shilpid` TCP and the HTTP gateway | A detail is stored and found by a described phrase |
| Suite host (new) | The MCP client, project selector, approval and audit | MCP | See section 7 |

Licence boundary: O-Studio and O-CoreOp are AGPL-3.0 or commercial; O-Draft is MIT or Apache-2.0; O-UI is MIT (marks reserved). Share contracts and documents, not source, across the AGPL↔permissive line.

## 6. Phases (proposed, with exit criteria)

1. **Contracts.** `ProjectRef`, `ArtifactRef`, tool schema and revision rules written down and agreed. Exit: O-Studio, O-Draft and O-CoreOp each produce and read a `ProjectRef` without changing ownership of any data.
2. **Tool servers.** O-CoreOp gets an MCP server; O-Studio and O-Draft tools are renamed and given the same descriptor shape. Exit: one MCP client lists the tools of all three.
3. **Suite host and the first workflow.** The host connects, resolves the active project, and runs drawing to estimate: O-Draft supplies measurements, O-CoreOp calculates, O-Studio drafts the document. Unsupported geometry and missing measurements are reported, never guessed. Exit: a real project completes the workflow with traceable inputs and a human approval.
4. **Knowledge.** ShilpiDB details and document search with source references.
5. **Product.** Installer coordination, shared settings, cross-version tests.

## 7. The first milestone, and where it stands

| Step | State |
|---|---|
| Create or open a project in O-Studio | Works |
| Share the project with O-Draft / O-CoreOp via `ProjectRef` | Company-file `ProjectRef` wired for CoreOp saves and Draft take-off; Draft project-picker UI still open |
| Open its drawing in O-Draft | Company-file link via `companyfile` / OSAS ticket; interactive project picker UI still open |
| Resolve the same project in O-CoreOp | Company projects dialog + OSAS ticket; `.bbsproj` file remains for offline-only work |
| Search approved details in ShilpiDB | Not possible: ShilpiDB has no details store yet |
| Ask an assistant to inspect the drawing and propose a take-off | O-Draft's agent can inspect a drawing; take-off tools are reserved but not in its registry |
| Calculate quantities in O-CoreOp and draft the estimate in O-Studio | Estimate totals surface on fee proposals / invoices via `project_estimate_summary`; full draft-estimate document tool still open |
| Refuse unauthorised writes and keep an audit trail | Done for O-Studio reads: refusals and calls are audited. No write tools exist |

## 8. Decisions needed from the owner

1. ~~Portal vs company-file SoR~~ — company file (2026-10-28).
2. The bridge file must carry `ProjectRef` for offline sharing; does O-Studio also need a dedicated export?
3. ~~What licence does O-Studio carry?~~ **Settled:** O-Studio is AGPL-3.0 or commercial (same dual path as O-CoreOp; see `LICENSING.md`). Shared design `O-UI` / `Hcw.Ui` remains MIT; marks reserved.
4. Is the suite host a separate small app, or part of O-Studio?
5. What is a ShilpiDB "detail", precisely?
