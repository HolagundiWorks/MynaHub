# Versioned online project edits

Implemented 2026-11-15 in the company API and Hub Mobile. This is an explicit
online edit. Mobile uses live API updates without a local backup or offline queue.
Tasks, files, project creation/deletion and desktop API integration remain
pending. Existing local practice records are not uploaded or changed.

## Request and response

`PATCH /v1/projects/{id}` requires projects.read and projects.write for that
project. Owner, Administrator and Principal have company-wide scope; Project
Manager has assigned-project scope. Other system roles remain read-only.
Custom-role delegation cannot widen the caller's authority and requires
projects.read alongside projects.write.

The strict JSON request includes:

```json
{
  "operationId": "11111111-1111-4111-8111-111111111111",
  "expectedVersion": 1,
  "name": "Hillside Villa",
  "code": "DEMO-01",
  "status": "on_hold"
}
```

Name is trimmed and limited to 1–240 characters; code to 80. Status is active,
on_hold, completed or archived. A full editable-field snapshot is required;
unknown fields such as organizationId, createdBy, version or deletedAt are
refused. Expected version is a positive PostgreSQL integer below its maximum.

A successful 200 returns outcome=applied, operationId and the project summary
with its version incremented once. A stale expectedVersion returns 409 with the
standard error envelope (VERSION_CONFLICT) and a conflict containing operationId,
expectedVersion, submitted name/code/status and the current project summary.
It writes no project changes. Duplicate codes return PROJECT_CODE_IN_USE.

## Retries and authorization

Keep the same operation UUID and payload when retrying an uncertain request.
Receipts are scoped to company, authenticated user and operation UUID, across
sessions. A canonical request hash binds the operation to its project, expected
version and field values. A changed request under a used UUID returns
OPERATION_ID_REUSED. Current project read/write authority is checked before
receipt lookup, so losing an assignment prevents replay from revealing data.

Both successes and version conflicts receive immutable receipts. Retrying
returns the original result, even if later edits have advanced the project;
reload for the latest state. A reviewed new edit needs a new operation UUID
and expected version. Project-row locks serialize competing edits and an
operation advisory lock serializes retries. Update, receipt and audit event
commit together; a code collision or other failure rolls the transaction back.
Retries produce no additional update audit event. Audit details contain the
operation UUID and revision numbers, not edited field values.

Migration 0007 adds the receipt table, policies, immutable-receipt triggers and
projects.write grants for the four editing system templates. Existing custom
roles do not acquire write authority automatically. Runtime can update only
name, code, status, version and updated_at; it cannot alter project identity,
company, creator, creation time or tombstone. Receipt reads/inserts require
matching transaction-local company/user and current project authority; runtime
has no receipt UPDATE/DELETE/TRUNCATE grants. Existing metadata-table RLS gaps
remain described in [project authorization](project-authorization.md).

Receipts currently have no expiry or cleanup job. They are a bounded protocol
foundation, not the general operation-receipt/change-feed service. A holder of
the trusted API runtime credential is still trusted to set transaction context;
this is not an interface for arbitrary SQL from untrusted clients.

## Mobile behavior

Practice > Cloud sync > Load company projects offers Edit company project when
effective permissions include projects.write. Once sent, a draft is frozen for
unchanged retries. A conflict shows both versions and offers review of the draft
on the returned revision or use of company values. Either choice returns to
review; another explicit Save is required. Further concurrent edits can conflict
again. Load company projects checks the latest company state.

Company rows, permissions and unsaved field changes stay in memory. The owner
removed offline snapshots, saved drafts and Mobile backup/restore on 2026-11-16.
Only the encrypted login credential persists. Live updates replace authorized
pages automatically; disconnecting/leaving clears displayed company data.
Interrupted writes may already have committed, so reconnect to inspect the live
server state. No background write queue or local practice upload is added.

## Verification

124 API tests passed, plus lint, type checking, build and formatting. Tests cover
atomic replay, stale conflicts, operation reuse, competing edits, read-only and
assignment boundaries, foreign companies, input validation, duplicate codes,
runtime column grants, actor-scoped receipts, receipt immutability and setup
grant validation. Existing populated Phase 2 upgrades still preserve identities
and projects.

Only the Neon demo branch was migrated. Live runtime/API checks passed edits,
retry, structured conflict, operation reuse refusal, assignment/read-only scope
and receipt context isolation; setup apply/check passed. Original demo project
details were restored, retaining revisions/audit history.

The preceding online-edit milestone passed its API/mobile checks. The physical
Android device passed save/retry/conflict/restoration against the real API.
The editor and a deliberately stale draft's conflict screen were visually
checked; both submitted and newer company values were visible. The original
details were restored and reloaded afterward. Private test input was removed.
The subsequent offline-recovery implementation was tested and then retired at
the owner's request. Current live-only verification and limits are recorded in
[live projects](mobile-live-projects.md). Public HTTPS, release installation and
production rollout remain unverified.
