# Contract: suite approvals (estimates and running bills)

**Status:** Implemented in `office-core` (2026-10-28); **product path aligned with CoreOp [STUDIO-DRAFT-SHARE](https://github.com/HolagundiWorks/MynaOPS/pull/24)** ([MynaOPS#24](https://github.com/HolagundiWorks/MynaOPS/pull/24)).  
**Product rule:** CoreOp generates → shares **draft** into the company file → Studio **approves** → **lock** → corrections via **revised version** only.

There are two ABIs. **Product CoreOp → Studio uses document kinds** (`studio_draft_*` / `doc_*`). The older `approval_*` / `suite_approvals` table remains for tools and tests that still call it; CoreOp does **not** write that table.

---

## A. Product path — CoreOp draft documents (canonical)

CoreOp shares via `office.dll` `doc_put` on the open project. Studio Practice → Approvals lists and locks those documents.

### Document kinds

| Kind | Meaning |
|---|---|
| `aqc.estimate.draft` | Latest estimate draft for the project (one document; re-share overwrites with optimistic revision) |
| `aqc.ra.draft.<bill_id>` | One draft per running-account bill (`bill_id` is CoreOp’s bill `Id`, hex without `.` or `/`; Guid `"N"` form is 32 chars) |

Kind strings may be up to **80** characters (`share::MAX_KIND_LEN`) so RA drafts fit.

### Envelope (body JSON)

CoreOp always writes `state: "draft"`. Studio owns later states.

| Field | Notes |
|---|---|
| `v` | Envelope schema version (`1`) |
| `state` | `draft` → Studio may set `pending_approval` / `approved_locked` / `superseded` |
| `artifact_kind` | `estimate` or `ra_bill` |
| `shared_at` / `shared_by` | UTC ISO-8601 and ticket username |
| `artifact_ref` | Optional; shape of `docs/contracts/refs.md` |
| `summary` | Inbox totals without parsing `body` |
| `body` | Full estimate or RA bill JSON |

### Studio operations (`office.dll` + Studio engine)

| Operation | Permission | Arguments | Result |
|---|---|---|---|
| `studio_draft_list` | View | optional `project_id`, `state` | DraftInfo rows (summary fields; no full body) |
| `studio_draft_get` | View | `project_id`, `kind` | Full draft + envelope + body |
| `studio_draft_approve` | Issue | `project_id`, `kind`, `expected_revision` | Sets envelope `state` to `approved_locked` via `doc_put` |
| `studio_draft_mark_pending` | Issue | same | Sets `pending_approval` |
| `studio_draft_mark_superseded` | Issue | same | Sets `superseded` on a locked share (revise cycle start) |

Raw `doc_list` / `doc_get` / `doc_put` remain available; Approvals UI uses `studio_draft_*`.

### Mapping vs generic `approval_*`

| Product step | CoreOp | Studio |
|---|---|---|
| Share draft | `doc_put` kind `aqc.estimate.draft` or `aqc.ra.draft.*`, envelope `state: draft` | — |
| List inbox | — | `studio_draft_list` (filter those kinds) |
| Show detail | — | `studio_draft_get` → `summary` + `body` |
| Approve / lock | — | `studio_draft_approve` → envelope `approved_locked` |
| Revise | New `doc_put` (same kind, new revision, `state: draft`) | Optionally `studio_draft_mark_superseded` first; never patch locked body |

**Not used by CoreOp product share:** `approval_submit` / `approval_list` / `approval_approve` / `approval_withdraw`.

### Approve / revise rules

- Only `draft` or `pending_approval` → `approved_locked`.
- Optimistic concurrency: `expected_revision` must match the loaded document.
- Locked body is immutable in Studio; CoreOp re-share creates the next draft revision.
- Studio UI is the product surface for approve (Issue permission).

---

## B. Parallel path — `suite_approvals` (`approval_*`)

Retained for ABI consumers and tests that submit rows into `suite_approvals`.

### States

| Status | Meaning |
|---|---|
| `draft` | Submitted into the company file |
| `approved_locked` | Studio approved; body and title immutable |
| `superseded` | Replaced when a later draft of the same project+kind was approved |

Kinds: `estimate` | `running_bill`.

### Operations

| Operation | Permission | Arguments | Result |
|---|---|---|---|
| `approval_submit` | Edit | `project_id`, `kind`, `title`, `body`, optional `supersedes_id` | Approval row (`draft`) |
| `approval_list` | View | optional `project_id`, `kind`, `status` | List without bodies |
| `approval_get` | View | `id` | Full row including `body` |
| `approval_approve` | Issue | `id` | Locked row; prior supersedes target → `superseded` |
| `approval_withdraw` | Edit | `id` | Deletes a `draft` only |

### Submit rules

- At most one `draft` per `(project_id, kind)` at a time.
- `supersedes_id` must name an `approved_locked` row of the same project and kind; the new draft gets `version = previous + 1`.
- Body max 16 MB. Checksum is SHA-256 of the UTF-8 body (lower-case hex).

---

## Related

- CoreOp contract: `docs/STUDIO-DRAFT-SHARE.md` (MynaOPS#24)
- `ArtifactRef` (`docs/contracts/refs.md`)
- Product ownership: company details and approvals in Studio only
