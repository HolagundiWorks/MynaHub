# Live client contacts

Implemented 2026-11-16 for the company API and Mobile. Client creation, editing, confirmed deletion and project/client link management are implemented online. Existing desktop and local Mobile client records are not migrated or uploaded.

GET /v1/clients returns at most 25 contacts and a UUID cursor; GET /v1/clients/:id returns one currently authorized contact. The live project endpoint accepts clients=true and an optional clientAfter cursor. Its clientPage is null unless requested and authorized. Changes, assignment loss and permission loss use the same authenticated long poll as projects and tasks.

Contacts include name, kind, contact person, email, phone, postal and correspondence addresses, communication preference, notes and revision. Financial identifiers are excluded. Requests require clients.read. Organization scope sees company contacts; assigned scope requires an actual assignment to a nondeleted linked project plus projects.read on that project. An organization-wide project-read role does not broaden an assigned client-read role. Company and active membership boundaries apply to every read, including direct restricted-role SQL.

Clients retain SELECT-only raw runtime grants and row-level security; controlled SECURITY DEFINER functions implement mutations. Project/client foreign keys require the same organization. Runtime callers cannot change project client_id directly. New company projects have no client link until an authorized user explicitly links one.

Mobile's Show company clients is opt-in, paginated independently within the existing live connection. Accounts with organization-scoped clients.write can create and edit contacts; Owner and Administrator also receive clients.delete by default. Principal receives clients.write. Assigned client writes are rejected. Manage project client also requires current project read/write scope. Contacts stay in RAM and clear on backgrounding, leaving, disconnecting or losing permission. No contact cache, backup, persisted draft or offline queue is added.

Verification: 161 API tests, lint, type checks and build; 193 Android unit tests and app/test APK builds; preview and demo Neon migration/hash/grant checks and restricted-role Owner/Staff visibility. Physical-device verification is recorded in the Mobile device-check guide. Production schema was not migrated.

## Online mutations

POST /v1/clients, PATCH /v1/clients/:id and DELETE /v1/clients/:id use stable operation UUIDs; edits/deletion also require the expected revision. Deletion requires typing DELETE, a fresh login, and administrator MFA when configured. Contacts linked to any active project cannot be deleted: unlink first. Deletion retains a tombstone and audit history; it does not delete local practice records. Replaying a creation after deletion cannot restore the contact.

GET/PUT /v1/projects/:id/client reads or replaces the current link with a same-company active contact, or null to unlink. PUT checks the project's revision and increments it, so concurrent project edits and link changes conflict safely. Linking locks the contact before the project so deletion cannot race a new active link. Link changes immediately affect assigned contact visibility in live reads.

Mutation receipts and audit events commit with the write. Receipts are actor/company-scoped and immutable; retries require current permission, and changed values require a new operation. Conflicts require explicit review; deletion review clears the confirmation. Forms, confirmation and selected contacts remain in RAM and clear on connection/access loss or leaving.

Migration 0012 passed isolated Neon restricted-role concurrent create/edit, link/unlink, deletion refusal, deletion retry and receipt immutability checks before demo application. Production and the real-owner development branch were not migrated.

The physical-device clientMutations check passed create/edit/retry/conflict, project link/unlink, linked deletion refusal and confirmed deletion with credential-only persistence. Its fictional client and project were tombstoned after verification; original demo records were preserved.
