# Online project creation and deletion

Implemented 2026-11-16 in the Myna API and MynaHub Mobile API Dev.

## Creation

POST /v1/projects requires organization-scoped projects.create and projects.read.
Default creators are Owner, Administrator and Principal. Assigned-scope creation
roles are refused. Input contains stable id and operationId UUIDs, name (trimmed,
1–240 characters), code (trimmed, at most 80 characters) and project status.
Company, creator, timestamps and revision are set by the backend.

HTTP 200 returns outcome=created, operationId and the revision-1 project. Reusing
the same operation and values returns the original result without duplicate
rows or audit events. Changed values under a used operation are refused.
Project IDs cannot be reused; nonempty project codes remain reserved even after
deletion so older records keep their identity.

## Deletion

DELETE /v1/projects/:id requires projects.delete and projects.read within that
project's current scope, plus a fresh session. Administrator MFA remains enforced
by the existing authentication guards. Default deletion is Owner/Administrator
only; custom deletion roles may use assigned scope.

Input contains operationId, expectedVersion and confirmation exactly DELETE.
A stale revision returns HTTP 409 VERSION_CONFLICT with the current project.
Mobile requires explicit review, a new operation and retyping DELETE before
trying the newer revision. Successful deletion returns outcome=deleted, id,
revision and deletedAt. An identical deletion retry remains valid after the
project disappears, provided current authority still permits that action.

Deletion sets a tombstone and increments the revision. Projects and their tasks
disappear from live/API views; rows, assignments, receipts and audit history stay
in the database. This does not delete existing local practice projects. There is
no restore or physical-purge API in this implementation.

## Database and safety

Migration 0010 adds immutable actor/company-scoped lifecycle receipts and narrow
security-definer functions. Those functions verify active membership, actor,
company and role scope. Direct runtime INSERT/DELETE on projects and UPDATE of
the deletion marker remain denied. Function execution is not granted to PUBLIC.
Normal project RLS continues to hide tombstones; a separate permission helper
supports authorized deletion receipt replay without exposing deleted rows.
The mutation, receipt and audit event commit in one transaction.

Mobile drafts, typed confirmation and operation UUIDs are memory-only. Leaving,
backgrounding, connection failure or loss of permission clears the forms. A
request interrupted by connectivity loss may already have committed; reconnect
to check current company state.

## Verification

148 API tests passed, including concurrent creation, idempotent deletion after
hiding, stale revision conflicts, revoked assignment, tenant isolation, fresh
sessions, immutable receipts and raw-runtime protection. The isolated Neon branch
verified actual runtime concurrency, tombstone visibility and receipt replay
before migration 0010 was applied to demo only.

185 Android unit tests and both debug APK builds passed. The physical device
verified Owner MFA, create/retry, a stale deletion conflict, confirmed removal,
deletion retry, hidden project/task views and credential-only persistence.
The newly created test project was tombstoned; original demo projects were not
deleted. Its child task remains as retained history. Production and the real-owner
development branch were not migrated.
The creation and inline deletion forms were visually checked on the phone.
The deletion action stayed disabled with an empty confirmation, and that
inspection was canceled without deleting an original demo project.
