# Online company task creation and editing

Implemented 2026-11-16 in the Myna API and MynaHub Mobile API Dev.

## API and permissions

POST /v1/projects/:projectId/tasks creates a task. PATCH
/v1/projects/:projectId/tasks/:taskId updates its editable fields. Both require
tasks.write plus current project read access in the caller's scope. Default
writers are Owner, Administrator, Principal and Project Manager; the manager
remains assignment-scoped. Other system roles retain task read access only.
Custom write roles must include projects.read and obey existing delegation rules.

Create input: operationId and id UUIDs, title (1–240 trimmed characters),
description (up to 10,000 characters), nullable ISO dueAt, status and priority.
Edit input replaces id with expectedVersion (1–2147483646). Valid statuses are
open, in_progress, completed and cancelled; priorities are low, normal, high
and urgent. Identity, organization, project and deletion fields cannot be edited.

Successful create/edit returns HTTP 200 with outcome=applied, operationId and
the task. New tasks start at revision 1; edits increment once. A stale revision
returns HTTP 409 VERSION_CONFLICT with the submitted fields and current task.
Conflict review requires a new operation UUID and current expected version.
Current assignment and membership authority are rechecked before receipt replay.

An identical operation retry returns its original result; changed values under
the same operation UUID are refused. Task, immutable actor-scoped receipt and
audit event commit together. Create also retains the same task UUID on retry.
The live task page reflects committed changes automatically.

## Database

Migration 0009 adds immutable task_mutation_receipts, grants tasks.write to
existing writer templates and adds task INSERT/UPDATE RLS. Insert requires the
current company/project, revision 1 and no tombstone. Runtime UPDATE privileges
cover only title, description, due time, status, priority, revision and update
time. Task identity, company, project and deletion columns remain protected;
runtime DELETE/TRUNCATE remain denied. Receipts have actor/project RLS and
same-company/project/task foreign-key integrity.

## Mobile

View company tasks exposes New/Edit controls to authorized writers. Title,
description, due date, status and priority can be edited online. Conflicts show
the company values beside the retained draft and offer explicit review/rebase.
Drafts and retry UUIDs stay in memory only; connection loss, backgrounding,
leaving or losing project/write access clears them. There is no offline queue
or automatic write replay. A network-interrupted save may already have committed.

The date field accepts YYYY-MM-DD. A new/changed date uses midnight UTC; leaving
an existing date unchanged preserves its stored time. Clear it for no due date.
The Mobile handshake requires tasks.edit. No local practice tasks are uploaded.

## Verification and limits

140 API tests passed, including create/retry, version conflicts, assignment
removal, read-only roles, validation, RLS and protected-column checks.
The isolated Neon branch verified concurrent creation with the restricted
runtime role, edit/retry/conflict, immutable receipts and protected identity
fields before migration 0009 was applied to the demo branch only.
180 Android unit tests and app/test APK builds passed.
The physical device verified create/retry/edit/conflict and restored the edited
demo task's original details. Three fictional DEMO-01 tasks are now available
for manual testing; existing local practice tasks were untouched.
The editor was visually checked and an unsaved task editor cleared after
background/resume on the physical device.

Task deletion, assignee management, native desktop API integration and
production rollout remain pending.

## Assignees and deletion

Task assignee management and confirmed deletion are implemented online. GET /v1/projects/:projectId/task-assignees returns at most 25 eligible active company members (membership UUID and display name only). PUT /v1/projects/:projectId/tasks/:taskId/assignees replaces up to 20 distinct membership IDs. Eligibility requires current project-read access through the candidate's own role scope and project assignment. Only active eligible assignees appear in task views; retained links become inactive when membership or project access is removed.

DELETE /v1/projects/:projectId/tasks/:taskId requires the expected task revision, a stable operation UUID, literal DELETE confirmation and a fresh login. It retains the task tombstone, assignments and audit history. All mutations share the task revision, so editing, assigning and deleting can conflict. Retrying a task creation after deletion cannot restore it.

Permissions tasks.assign and tasks.delete are separate from tasks.write. Owner, Administrator, Principal and Project Manager receive them by default, within their role scope. Assigned custom roles require projects.read. Current authority is rechecked before receipt replay. Assignment/deletion receipts are immutable and actor/company scoped; raw runtime assignment writes and task tombstone updates remain denied.

Mobile shows assigned names and online assignee/deletion forms. Eligible members are an opt-in page within the same live poll, with independent pagination. Forms, selections and confirmation clear on backgrounding, disconnect, leaving or scope loss. Conflicts require review and a new operation; deletion confirmation is cleared when reviewed.

Migration 0013 passed isolated Neon restricted-role concurrent assignment/retry, edit/delete conflicts, tombstone replay, immutable receipt and raw-grant checks before demo application. The physical-device taskActions workflow also passed without changing original demo tasks. Production and real-owner development were not migrated.
