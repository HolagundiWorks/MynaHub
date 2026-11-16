# Mobile live project API

Implemented 2026-11-16. The owner requires live Mobile data, with no Mobile local
backup, offline company snapshot or persisted draft. Project writes remain the
versioned PATCH protocol in [project edits](project-edits.md).

GET /v1/mobile/projects/live accepts optional since (64 lowercase hexadecimal
characters) and after (UUID page boundary). It returns revision, projects,
nextCursor, roles and permissions. A page has at most 25 authorized summaries.
Optional taskProjectId selects a project on this authorized page; taskAfter
pages its tasks. The response taskPage is null without a visible selection,
otherwise {projectId,tasks,nextCursor}, bounded to 25 rows. Task fields are id,
projectId, title, description, nullable ISO dueAt, status, priority and version.
The revision includes the selected task page and both task query parameters.
Task changes, tombstones and access removal therefore update the same live
request. Company tasks inherit projects.read and assignment scope.
The revision is a server fingerprint, not a change-feed cursor or credential.

Without a matching revision the current page returns immediately. A matching
revision waits up to 20 seconds for change; session/membership/permissions and
RLS-scoped rows are checked approximately every second. Before publishing a
page the API checks authorization again. With no projects.read permission, an
active member receives an empty project list and their own current permissions;
restoring permission updates the live page. Suspension/revocation ends access.

Every project query uses a fresh transaction-local scope with myna_runtime.
No database connection is held open as a transaction for the long-poll wait.
Disconnect/shutdown abort waits and releases live-connection accounting.
Limits are four held requests per user and 100 per process, plus a route rate
limit. These are safeguards, not a production load-test result.

This uses the existing authenticated API and PostgreSQL driver. Migration 0008
adds studio.project_tasks with a composite same-company project foreign key,
bounded fields, status/priority checks and SELECT-only runtime RLS. Runtime
INSERT/UPDATE/DELETE/TRUNCATE are denied. There is no task mutation API yet.
No new Neon service, public sealed SQL query or client database key is
introduced. Responses use no-store. The capabilities projects.live and tasks.live are published
in the connection contract; the new Mobile connection requires it.

Mobile retains only its encrypted login credential. All company rows, identity,
permissions and draft values stay in memory and clear on disconnect/leaving.
Online edits still use current server authorization and optimistic concurrency.
Previous offline implementations were removed at the owner's explicit request.

132 API tests passed, plus lint, type checking and build. 176 Android JVM tests
and debug/test APK builds passed. Live Neon/physical-device tests verified
another session's edits, restoration, assignment removal/restoration and revoked
session refusal. The device checked credential-only persisted fields and retired
backup endpoints. The menu and automatic project update were visually verified.
An actual API outage cleared the visible company data; restarting the API
restored live data automatically. Original demo details and access were restored.

Migration 0008 was verified on an expiring demo-derived Neon branch before
applying to the demo branch. Two fictional DEMO-01 tasks were added. Production
and the real-owner development branch were not migrated.
The physical API Dev build read both demo tasks and checked credential-only
persistence. A Neon title/status change appeared automatically on the open
task screen; original task details were restored afterward.

Current coverage is project pages, read-only selected-project tasks and role/permission changes. Other business
entities and native desktop API integration remain pending. Complete metadata
RLS, public HTTPS deployment, release installation and production scale have
not been verified. Existing local practice data is preserved.
