# MynaHub: current implementation

Updated 2026-11-17. The company API and Windows desktop are separate implementation surfaces. Desktop still uses its company file and legacy connector; native desktop API integration is pending. MynaCAD and MynaOps remain outside this work.

## Company API and Mobile

Implemented: BYON setup/check/apply; Better Auth sign-in, invitations, MFA and device sessions; eleven role templates, scoped custom roles and project assignments; live project/task/client/site/file pages; versioned project creation/editing/confirmed deletion; task creation/editing/assignees/confirmed deletion; client creation/editing/confirmed deletion and project linking; shared site instructions/inspections/snags; private project attachments with authenticated, hash-verified viewing.

Mobile keeps only encrypted login credentials and public connection settings. Identity, permissions, rows, drafts, file selections and previews stay in RAM and clear when disconnected, backgrounded or left. Backups, offline company snapshots and persistent write queues are removed. Existing local practice Room records remain separate and are not uploaded or deleted. Binary file previews run in an isolated Android process with RAM-backed input and bounded raw-pixel output.

## Remaining work

Complete metadata-table RLS, native desktop API integration, public HTTPS deployment, production load/retention/reconciliation checks, signed release installation and store rollout remain pending. Other local fieldwork modules (daily reports, detailed issue histories and permits) are not silently mapped into shared site records. Production and real-owner development were not migrated.

Verification through private attachments: 191 API tests, lint/types/build; 210 Android unit tests and app/test APK builds; isolated Neon runtime/grant checks before demo migrations; physical-phone company, task, client, site and private file workflows. Interactive owner-unlock, file selection, upload and text viewing passed after fixing callback ownership across AppLockGate. The exact fictional test file was removed with a fresh demo session. Demo uses migrations 0000–0015. Original demo projects/clients/tasks were preserved.

## Documentation map

- [Platform plan](hub-platform-plan.md): implementation matrix and remaining work.
- [Branding](BRANDING.md): names, palette, artwork and platform differences.
- [BYON setup](byo-neon.md) and [API README](../apps/api/README.md).
- [Authorization](project-authorization.md) and [project edits](project-edits.md).
- [Project lifecycle](project-lifecycle.md), [task mutations](task-mutations.md), [clients](client-contacts.md).
- [Site records](site-records.md), [private attachments](project-attachments.md), [live reads](mobile-live-projects.md).
- [Desktop packaging](packaging.md) and [maintenance record](maintenance-2026-11-16.md).

Legacy desktop connector identifiers remain for compatibility. Retained .gitkeep files preserve intentional empty directories. Credentials, signing material, company files and installed application records are not cleanup outputs.

The API server refuses migration-owner credentials in its environment and checks the connected runtime role before listening. A separate demo process passed both rejection and limited-role startup checks on 2026-11-17.

Neon Functions hosting is selected. Its Fetch adapter and deployment bundle are prepared and verified locally against demo; publication is deferred at the owner's request. See [deployment preparation](neon-functions.md). API checks now total 191 tests.
