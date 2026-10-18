# HolagundiWorks suite: architecture and integration map

Status: written 2026-10-18 from a read of the repositories and the code in this one. Every statement marked **verified** was read in a repository or run. Everything marked **proposed** is a recommendation for the owner to decide, not something that exists.

## 1. The products today (verified)

| Product | Repository | What it is now | Stack | Licence |
|---|---|---|---|---|
| **AOS**, Architecture Office Suite | `HolagundiWorks/AOS-Architecture-Office-Suite` | Offline office app: clients, projects, letters, invoices, accounts, bank import, tax, staff, library, proposals, meetings, attendance, site records, permits, 26AS check. One `.adms` company file | Rust engine, WinUI 3 shell over a C interface | Not stated |
| **AAD**, Accelerated Architecture Drafting | `HolagundiWorks/AAD` | 2D CAD in Rust: drafting, DXF and DWG, layouts, plotting, architecture tools, take-off commands, an offline local-model agent (Qwen3), an MCP server, a JSON control channel, an AORMS sign-in, a `.vdb` store | Rust, wgpu, WinUI 3 shell (`aad-host.dll`) | MIT or Apache-2.0 |
| **AQC**, Accelerated Quantity and Costing | `HolagundiWorks/AQC` | Quantity take-off, BOQ, rate books, DSR estimates, bar bending schedules, purchase orders, running-account billing, scheduling, correspondence, stores and HR, an opt-in AI assistant, a CAD bridge importer, an AORMS connection | C# WinUI 3, C++ `bbs_engine.dll` | AGPL-3.0, or commercial |
| **ShilpiDB** | `HolagundiWorks/shilpidb` | A spatial vector store: a binary codec, a grid index over bounding boxes, a TCP server (`shilpid`), a shell, an HTTP gateway and a WinUI browser. The roadmap says no entity types beyond a bounding box, no metadata, no importers and no embeddings yet | Rust, dependency-free | Not checked |
| **AORMS** | `HolagundiWorks/aorms` | The web portal. It already works as the hub of the suite: the sign-in, project list, project seed, rate books and drawing files that AAD and AQC use (contract `2026-10-aqc-1`) | Next.js, Supabase | Not checked |

The product descriptions in the suite plan you shared are targets. Two differ from what exists: ShilpiDB is not yet a library of approved details and specifications (it stores bounding boxes), and AOS is not the owner of project identity today (AORMS is, for AAD and AQC).

## 2. How the pieces connect today (verified)

```
                AORMS portal (cloud): one login, projects, seeds, drawings, rate books
                 |            |                       |
        AAD (CAD)       AQC (quantities)        AOS (office, offline)
           |   \__ hcw-aqc-bridge JSON file or portal version kind cad_bridge __/
           |
       .vdb store (embeds ShilpiDB codec and grid; can talk to shilpid)
```

- **Sign-in:** one AORMS login. The refresh token is shared in one Windows credential (`AORMS/Connect`); each app keeps its own session (`aad`, `aqc`, and now `aos`). AOS joined in this work: the `aorms-connect` crate and the Office > AORMS portal page. The server change that lets AOS hold a session is on branch `claude/aos-suite-session` in the aorms repo and is not yet applied.
- **CAD to quantities:** AAD writes a `hcw-aqc-bridge` v1 JSON file (the AQC repository documents the contract in `docs/bridge/`), and AQC imports it. Through the portal this travels as a version of kind `cad_bridge`. AAD's `aqcexport` writer is described as the last unbuilt port item in the AORMS notes, and I did not check whether it has been finished since.
- **Project details:** AAD and AQC pull title, parties and floors from the portal. AOS now pulls a project's client, name and site address.
- **AI:** AAD has its own agent (`crates/agent`, a fixed tool registry with confirmation rules, audit log and an offline model) and an MCP server. AQC has an opt-in assistant. AOS now has `office-agent`, a read-only MCP server over the office records. Each is separate.

## 3. What was built in this repository for the suite (verified, with tests)

| Piece | Purpose | Tests |
|---|---|---|
| `office-core/` | The company file, sign-in, roles, schema tracks, shared records (clients, projects, briefs, staff, leave, library, proposals, meetings, attendance, 26AS) | 40 |
| `office-ffi/` and `native/Office.Interop` | A C interface and .NET wrapper so another native app can use the same file. Contract in `docs/office-abi.md` | 3, plus a console smoke test |
| `aorms-connect/` | The AORMS shared sign-in and project pull | 9, against a stand-in server |
| `office-agent/` | Governed read-only tools and an MCP server (`office-mcp`) | 8, including the real process |
| Schema tracks (`office-core::track`) | Lets an app add its own tables to the shared file without touching the others' | 4 |

## 4. Recommendations (proposed)

### 4.1 One project identity

Today three identifiers exist for a project: AORMS `projectOfficeId` (used by AAD and AQC), the AOS `projects.id` inside one company file, and AQC's own project file. Proposed:

- `ProjectRef = { company_uuid, project_id, aorms_project_office_id? }`. The first two are the AOS company file's own id and the project's id in it; the third is filled when the project has been pulled from or linked to the portal (AOS stores it in `aorms_links` already).
- When a project is linked to AORMS, `aorms_project_office_id` is the id AAD and AQC use. When it is not (an offline practice), the pair `company_uuid + project_id` is the id, and the bridge file carries it.
- **Decision for the owner:** is AORMS or the AOS company file the system of record for a project? The plan you shared says AOS; the built suite sign-in says AORMS. Both can hold if the rule is "AORMS when linked, AOS when not", but that has to be chosen.

### 4.2 Contracts, not shared databases

Keep each app's data its own. Share through a small versioned contract package. Start with the ones that already exist and name the gaps:

| Contract | State |
|---|---|
| Sign-in and session (`2026-10-aqc-1`) | Exists, in the aorms repo |
| CAD to quantities (`hcw-aqc-bridge` v1) | Exists, in the AQC repo |
| Office records (`docs/office-abi.md`) | Exists, in this repo |
| `ProjectRef` and `ArtifactRef` (a drawing, an estimate or a document with revision, checksum and the revisions it was made from) | **Missing.** Needed before an estimate can name the drawing revision and rate schedule it used |
| Tool schema (below) | Partly exists: AAD's registry, `office-agent`. No common definition |

### 4.3 One AI platform, built on MCP

MCP is already how AAD (`aad-cli mcp`) and AOS (`office-mcp`) expose tools, and AQC's CLAUDE notes already treat the bridge as a file contract. Rather than a new runtime, propose:

1. Every app exposes its tools as an MCP server with typed input schemas, and refuses unknown arguments. AOS does this. AAD does. AQC does not yet.
2. A thin suite host (the "workspace" in the plan) is an MCP client that connects to the apps' servers, holds the active `ProjectRef`, applies the approval policy, and writes one audit trail. This is new work.
3. Tool names carry the app's namespace: `cad.*`, `aqc.*`, `aos.*`, `shilpi.*`. AOS's tools are currently `clients.search`, `projects.list`, and so on, and would be renamed to `aos.*` when the host exists.
4. Writes are proposals first. A tool that changes data returns a preview and needs an explicit approval step; only reads exist today.
5. Local and hosted models are interchangeable behind the host. Offline mode means no project content leaves the machine, and the host enforces it.

### 4.4 ShilpiDB's role

ShilpiDB is a spatial store, and `aad-vdb` already embeds it. The reusable-details library in the plan would be a second layer on top (drawing blocks with metadata, specifications, search). Proposed order: first define what a "detail" record is (geometry reference, metadata, approval state), then add it to ShilpiDB, then add a `shilpi.search_details` tool. Until then, `office-core`'s library (specifications, standards, lessons) holds the written side.

## 5. Repository by repository (proposed)

| Repo | Add | Interface | Acceptance test |
|---|---|---|---|
| AOS (this) | Rename tools to `aos.*`; project export `ProjectRef`; draft-estimate document from an AQC result | `office-ffi`, `office-mcp`, a new document-draft tool | An estimate document is created from a supplied BOQ and names its sources |
| AAD | Finish `aqcexport` if not done; add `ProjectRef` to the bridge file; a tool to list measurable elements | The bridge file, `aad-cli mcp` | A sample drawing exports a file AQC imports with the same project id |
| AQC | An MCP server around its calculation engine (`aqc.calculate_boq`, `aqc.compare_estimates`); record drawing revision and rate schedule in each estimate | MCP over stdio; Office C interface for the shared records | The same take-off gives the same BOQ twice, and the estimate names its inputs |
| ShilpiDB | The detail record and its search | `shilpid` TCP and the HTTP gateway | A detail is stored and found by a described phrase |
| AORMS | Apply migration 0113 so AOS can hold a session; decide entitlement | Existing REST | AOS signs in against the real portal |
| Suite host (new) | The MCP client, project selector, approval and audit | MCP | See section 7 |

Licence boundary: AQC is AGPL-3.0, AAD is MIT or Apache-2.0, and AOS has no licence file. Share contracts and documents, not source, between AQC and the others. Decide AOS's licence before others build against it.

## 6. Phases (proposed, with exit criteria)

1. **Contracts.** `ProjectRef`, `ArtifactRef`, tool schema and revision rules written down and agreed. Exit: AOS, AAD and AQC each produce and read a `ProjectRef` without changing ownership of any data.
2. **Tool servers.** AQC gets an MCP server; AOS and AAD tools are renamed and given the same descriptor shape. Exit: one MCP client lists the tools of all three.
3. **Suite host and the first workflow.** The host connects, resolves the active project, and runs drawing to estimate: AAD supplies measurements, AQC calculates, AOS drafts the document. Unsupported geometry and missing measurements are reported, never guessed. Exit: a real project completes the workflow with traceable inputs and a human approval.
4. **Knowledge.** ShilpiDB details and document search with source references.
5. **Product.** Installer coordination, shared settings, cross-version tests.

## 7. The first milestone, and where it stands

| Step | State |
|---|---|
| Create or open a project in AOS | Works |
| Link it to the portal project | Works in AOS; the portal change is committed on a branch and not applied |
| Open its drawing in AAD | AAD pulls the project from the portal; AOS-only projects are not reachable from AAD yet |
| Resolve the same project in AQC | Through the portal only |
| Search approved details in ShilpiDB | Not possible: ShilpiDB has no details store yet |
| Ask an assistant to inspect the drawing and propose a take-off | AAD's agent can inspect a drawing; take-off tools are reserved but not in its registry |
| Calculate quantities in AQC and draft the estimate in AOS | No AQC tool server, and no AOS draft-estimate tool |
| Refuse unauthorised writes and keep an audit trail | Done for AOS reads: refusals and calls are audited. No write tools exist |

## 8. Decisions needed from the owner

1. Which system of record for a project: AORMS when linked and AOS otherwise, or one of them always?
2. Should AOS be able to run with no AORMS at all and still share projects with AAD and AQC? If yes, the bridge file must carry `ProjectRef`, and AOS needs an export.
3. What licence does AOS carry?
4. Is the suite host a separate small app, or part of AOS?
5. What is a ShilpiDB "detail", precisely?
