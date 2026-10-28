# Contract: `ProjectRef` and `ArtifactRef` (version 1)

Written 2026-10-19. This is phase 1 of the suite plan in `docs/suite-architecture.md`. The code is `office-core/src/refs.rs`; the schema is `refs.schema.json` beside this file.

An app that follows this contract can say "this estimate was made from revision 3 of that drawing, for this project" in a way another app can check. No app has to share its data to do it.

## What a reference is, and is not
- A reference **names** something. It carries no project data and gives no right to read any.
- A reference is **versioned**. Field `v` is `1`. A reader refuses a version it does not know.
- Apps never write each other's tables. They exchange references and ask each other for the thing named.

## ProjectRef

```json
{ "v": 1, "company_uuid": "…", "project_id": 7 }
```

| Field | Meaning |
|---|---|
| `company_uuid` | The id of the O-Studio company file the project lives in (`company_file.company_uuid`). |
| `project_id` | The project's id in that file. |

**Finding a project again.** A reader matches when `company_uuid` equals this file's and `project_id` exists here. If that fails, the project is not here and the reader says so. It does not guess from the project's name.

An offline project is identified by the company file it was made in (`company_uuid` + `project_id`).

## ArtifactRef

```json
{
  "v": 1,
  "project": { "v": 1, "company_uuid": "…", "project_id": 7 },
  "kind": "estimate",
  "id": "EST-0004",
  "revision": 2,
  "checksum_sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
  "made_from": [
    { "kind": "drawing", "id": "GF-PLAN", "revision": 3, "checksum_sha256": "…" }
  ]
}
```

| Field | Rule |
|---|---|
| `kind` | One of `drawing`, `boq`, `estimate`, `rate_schedule`, `document`. A new kind means a new contract version. |
| `id` | The maker's own id, 1 to 200 characters. Nobody else interprets it. |
| `revision` | 1 or more, and goes up by one each time the maker saves a new revision. |
| `checksum_sha256` | The SHA-256 of the artifact's content at that revision, as 64 **lower-case** hex digits. Upper case is refused so two apps cannot disagree on spelling. |
| `made_from` | The revisions this one was calculated or drafted from, at most 100. Empty for a source such as a drawing. |

**Rules a reader checks** (`check_artifact`):
- the version, the kind, the id, the revision and the checksum are valid, for the artifact and for each input;
- an artifact is never made from itself, nor from a later revision of itself (an earlier revision of itself is allowed).

A reader does **not** check that the artifact exists or that the checksum is true. That is for the app that holds the content: it recomputes the checksum and compares.

## Who produces what

| App | Produces | Reads |
|---|---|---|
| O-Studio | `ProjectRef` for any project; `ArtifactRef` for a fee proposal or document it issues | `ProjectRef` and `ArtifactRef` from other apps, to attach an estimate to a project |
| O-CoreOp | `ArtifactRef` for a BOQ, estimate or rate schedule, naming the drawing revision it used in `made_from` | `ProjectRef` to open or create the project it belongs to |
| O-Draft | `ArtifactRef` for a drawing, and a `ProjectRef` in the bridge file | `ProjectRef`, to say which project a drawing is for |

## Operations (office interface, `docs/office-abi.md`)

| Operation | Needs | Does |
|---|---|---|
| `project_ref` `{ project_id }` | View | Returns the `ProjectRef` of a project in the open company file |
| `project_resolve` `{ reference }` | View | Returns the project id the reference names here, or an error |
| `artifact_ref_check` `{ reference }` | View | Returns `null` when the reference is well formed, or an error naming the problem |

## System of record

The **company file** (`company_uuid` + `project_id`) is the suite system of record for project identity. Portal / AORMS ids are not used in the desktop suite.

## Operations added for fee linking

| Operation | Needs | Does |
|---|---|---|
| `project_estimate_summary` `{ project_id }` | View | Reads the O-CoreOp `aqc.project` document's `last_estimate` (grand total in paise/rupees, rate book, optional `artifact_ref` and `project_ref`) |

## What is not done yet
- O-Draft interactive project picker UI for `companyfile` (engine already stamps `project_ref` on take-off when linked).
- Bridge file (`hcw-aqc-bridge`): O-CoreOp parses `source.project_ref` when present (Import captions Mine/Other/missing); keep ensuring Draft export always writes it when company-linked. ShilpiDB take-off path already carries the ref.
- Fee proposals can use the O-CoreOp estimate as cost; Studio also surfaces RA (`project_ra_summary`) beside fee/invoices. Fee invoices do not yet store an `ArtifactRef` column of their own.
- End-to-end click-through of calculate → company-file save → fee proposal → invoice on a real project (Windows GUI / human).
