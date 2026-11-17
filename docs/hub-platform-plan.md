# MynaHub platform: implementation and remaining work

Updated 2026-11-17. The company owns its Neon database and hosts the Fastify/TypeScript company API with Drizzle and self-managed Better Auth. Mobile connects to that API; database/storage credentials never belong on the phone. Windows desktop still uses its company file and legacy connector. MynaCAD/MynaOps files are outside this integration.

## Implemented

| Area | Current behavior |
|---|---|
| BYON | init/check/apply CLI; separate schema owner/runtime role; migration/hash/grant checks; optional private attachment storage checks |
| Identity | Bootstrap owner, invitation acceptance, sign-in, MFA, device sessions and revocation |
| Authorization | Eleven templates, scoped custom roles/delegation, project assignments, project/business-table RLS and protected identity columns |
| Projects | Live pages, online creation/editing and confirmed retained deletion; version conflicts and immutable retries |
| Tasks | Live reads, online creation/editing/status, up to twenty eligible assignees, confirmed retained deletion |
| Clients | Scoped live contacts, organization-scoped creation/editing/unused-client deletion, versioned project link management |
| Site records | Shared instructions/inspections/snags, date validation, live pages, scoped creation/editing and confirmed deletion |
| Attachments | Private branch storage, authenticated bounded upload/download, size/hash checks, live metadata and confirmed retained removal |
| Mobile | Encrypted credential-only persistence; other company state in RAM, cleared on disconnect/background/leaving; isolated binary previews |
| Cleanup | Backup/restore, offline company snapshots/drafts/queues and obsolete transfer/manual-sync entry points removed; current docs/branding consolidated |

One authenticated long poll carries selected pages, current roles and permissions. Checks run approximately every second, with a twenty-second wait. Each page has at most twenty-five records. This uses the existing API/Postgres driver, not a separate Neon Realtime service. Local Mobile practice records remain separate; no automatic data migration or upload occurs.

## Remaining

1. Complete core/access/auth metadata row-security hardening.
2. Native Windows desktop company-API integration.
3. Public HTTPS hosting and deployment, production load checks and storage retention/orphan reconciliation.
4. Signed release installation and store rollout.

Neon Functions is selected; its adapter and bundle are prepared, with publication deferred at the owner's request. The custom domain remains pending. See [deployment preparation](neon-functions.md). Interactive picker/upload/text-view verification passed after owner unlock and the callback fix. Deletion correctly required a fresh session; the exact fictional file was removed and became unreadable. Isolated-renderer checks also passed. Backups/offline company storage are an explicit exclusion and must not be restored from old plans. Desktop company-file backup policy is separate.

## Verification and environments

191 API tests, API lint/types/build, 210 Android unit tests and app/test APK builds passed through private attachments. Physical-device checks covered live access changes, credential-only storage, project/client/task/site mutations, conflicts/retries, retired backup routes, private TXT/PNG/PDF uploads/viewing/deletion and isolated memory-only rendering. Runtime dependency audit reported no known vulnerabilities.

Migrations 0008–0015 were checked on the expiring demo-derived Neon verification branch before application to demo. Protected runtime-role checks covered grants, scope, concurrent writes, immutable receipts and retained deletion. Demo uses sixteen applied migrations; original demo projects/clients/tasks remain intact. Production and the real-owner development branch were not migrated.

See [current implementation](current-implementation.md), [BYON setup](byo-neon.md), [authorization](project-authorization.md), [project lifecycle](project-lifecycle.md), [tasks](task-mutations.md), [clients](client-contacts.md), [site records](site-records.md), [attachments](project-attachments.md), [live reads](mobile-live-projects.md) and [branding](BRANDING.md).
