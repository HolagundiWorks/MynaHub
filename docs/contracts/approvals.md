# Contract: suite approvals (estimates and running bills)

**Status:** Implemented in `office-core` (2026-10-28)  
**Product rule:** CoreOp generates → shares **draft** into Studio → Studio **approves** → **lock** → corrections via **revised version** only.

## States

| Status | Meaning |
|---|---|
| `draft` | Shared into the company file; editable only by withdraw + resubmit, not by patching the body |
| `approved_locked` | Studio approved; body and title are immutable |
| `superseded` | Replaced when a later draft of the same project+kind was approved |

Kinds: `estimate` | `running_bill`.

## Operations (office interface and Studio engine)

| Operation | Permission | Arguments | Result |
|---|---|---|---|
| `approval_submit` | Edit | `project_id`, `kind`, `title`, `body`, optional `supersedes_id` | Approval row (`draft`) |
| `approval_list` | View | optional `project_id`, `kind`, `status` | List without bodies |
| `approval_get` | View | `id` | Full row including `body` |
| `approval_approve` | Issue | `id` | Locked row; if it had `supersedes_id`, that row becomes `superseded` |
| `approval_withdraw` | Edit | `id` | Deletes a `draft` only |

### Submit rules

- At most one `draft` per `(project_id, kind)` at a time.
- `supersedes_id` must name an `approved_locked` row of the same project and kind; the new draft gets `version = previous + 1`.
- Body max 16 MB. Checksum is SHA-256 of the UTF-8 body (lower-case hex).

### Approve rules

- Only `draft` → `approved_locked`.
- Studio UI is the product surface for approve; CoreOp must not call `approval_approve` in its product path (ABI allows Issue-role callers for tests/tools).

## CoreOp share (ABI sketch)

After CoreOp calculates an estimate or RA bill for a company-file project:

```json
{
  "project_id": 7,
  "kind": "estimate",
  "title": "Tower — DSR estimate",
  "body": "{ … CoreOp JSON payload … }",
  "supersedes_id": null
}
```

Call `approval_submit` on `office.dll`. To revise a locked Studio approval, set `supersedes_id` to that id.

Studio lists drafts under **Practice → Approvals**, shows the body, and calls `approval_approve`.

## Related

- Product: company details and approvals ownership  
- `ArtifactRef` (`docs/contracts/refs.md`) names content checksums; an approval row stores its own `checksum_sha256` of `body`
