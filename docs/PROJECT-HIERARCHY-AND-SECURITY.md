# Project hierarchy and security (enterprise model)

**Canonical copy:** Kivo `docs/PROJECT-HIERARCHY-AND-SECURITY.md`. Keep identical copies in Falcon, Tren, and O-UI when this changes. Updated 2026-10-31.

This document ties together **where project data lives**, **who may change it**, and **which controls enforce that** in the Kivo suite.

## 1. Hierarchy (systems of record)

```
O-Studio (hub)
├── Company file (.osas)          ← practice identity, RBAC, audit, billing records
│   ├── Clients, projects, brief, stages, workspace paths
│   ├── project_documents         ← cross-app mirrors & approval drafts (not full BOQ)
│   └── Letters, invoices, books, users, approvals
│
└── Per project
    └── Project file (.osp)       ← drawing, take-off, full estimate / BOQ / RA (SQLite)
        ├── drawing, drawing_state     (O-Draft writes)
        ├── takeoff_*                  (O-Draft writes)
        ├── project_doc                (O-CoreOp writes — authoritative quantities & money)
        └── meta.revision              (conflict detection)

Neon (optional)                   ← O-Studio only: .osp snapshots + Postgres revision index
```

**Rules**

| Layer | System of record | Never store here |
|-------|------------------|------------------|
| Company file | Clients, projects, firm profile, practice billing, **`aqc.project` summary**, approval **drafts** | Full take-off rows, DWG bytes, complete BOQ JSON |
| `.osp` | Drawing, take-off, **`project_doc`** (full CoreOp state) | Company users, invoice ledger, Neon credentials |
| `ProjectRef` | Identity only (`company_uuid` + `project_id`) | Payload data |

## 2. Data classification

| Class | Examples | Storage | Typical exposure |
|-------|----------|---------|------------------|
| **C1 – Public firm** | Company name on letterhead | Company profile | All signed-in roles |
| **C2 – Project commercial** | Estimate totals, RA nets, fee proposals | `.osp` + `aqc.project` summary | View+; write CoreOp / mirror on save |
| **C3 – Financial books** | Invoices, journals, bank | Company file | Finance, Accounts, Owner |
| **C4 – Credentials** | Password hashes, Neon env, suite tickets | Company file / `%LOCALAPPDATA%\O-Studio\` | Owner; **never** in Tren or Falcon |
| **C5 – Drawing IP** | DWG/DXF bytes | `.osp` | View via apps; sync via Kivo only |

## 3. Trust boundaries

| Boundary | Mechanism |
|----------|-----------|
| Desktop apps ↔ company file | `office.dll` / `office-ffi`: session, role, permission on every mutating call |
| Kivo → sibling apps | One-time **suite ticket** (120 s, single use, SHA-256 stored); `OSTUDIO_COMPANY` + `OSTUDIO_TICKET`; audited |
| Falcon ↔ Tren | Same `.osp`, `meta.revision`; no network |
| Desktop ↔ Neon | **Kivo only**; apps hold no cloud keys |
| Assistant (Olmo) | Myna hosts; read-only `agent_*` tools; bank/UPI omitted from `company.details` |

## 4. Role-based access (company file)

Roles are defined in `office-core` (`auth.rs`). Effective permissions:

| Role | View | Edit records | Issue (approve) | Finance | Post | Manage settings / users |
|------|------|--------------|-----------------|---------|------|-------------------------|
| Owner | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Principal | ✓ | ✓ | ✓ | ✓ | — | — |
| Accounts | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| Document editor | ✓ | ✓ | — | — | — | — |
| Auditor | ✓ | — | — | ✓ | — | — (audit log only) |

**Suite-linked Falcon:** clients/projects/profile are **read-only**; CoreOp may **Edit** `doc_put` for `aqc.project` summaries and estimate/RA **drafts** only. Create client/project is refused (`CompanyOfficePolicy`).

## 5. Separation of duties (approvals)

| Action | Who | Permission | Document kind |
|--------|-----|------------|---------------|
| Mirror BOQ / estimate / RA **abstracts** | Falcon | Edit | `aqc.project` (summary JSON only) |
| Share estimate / RA **draft** | Falcon | Edit | `aqc.estimate.draft`, `aqc.ra.draft.*` — envelope **`state: draft` only** |
| Mark pending / **approve & lock** / supersede | Kivo | Issue | `studio_draft_*` API — not raw `doc_put` |
| Change firm profile / users | Kivo | Manage settings / users + re-auth for user admin |

Engine enforcement (2026-10-31):

- `share::validate_body_for_put` — `aqc.project` allows only `project_ref`, `last_estimate`, `last_estimate_rate_book_version_id`, `accounts.bills`; rejects full project leak.
- `studio_drafts::validate_coreop_share_body` — rejects `approved_locked` / `pending_approval` via direct `doc_put`.
- `project_ref` in summary must resolve to the same company file and **same `project_id`**.

## 6. Integrity and concurrency

| Control | Purpose |
|---------|---------|
| Optimistic revision (`expected_revision`) | Prevents silent overwrite across Kivo ↔ Falcon |
| Document checksum (SHA-256) | `ArtifactRef` integrity; history keeps last 10 revisions |
| Append-only `audit_log` | Tamper-evident practice events |
| SQLite WAL + immediate transactions | Single writer ordering; 5 s busy timeout |
| `watch` / `data_version` | Live refresh without granting Edit on poll |
| `.osp` `meta.revision` | Tren / Falcon co-open detection |

## 7. Authentication and session

| Control | Detail |
|---------|--------|
| Password storage | Argon2 (`auth.rs`) |
| Password policy | Minimum 12 characters |
| Lockout | Exponential backoff from 5th failure (up to 15 min) |
| Idle lock | Default 5 minutes (`DEFAULT_IDLE`) |
| Protected company file | Encrypted at rest (`AOSENC1`); single-app decrypted working copy |
| Suite ticket | 32 random bytes; env var cleared after read; `suite_ticket_issued` / `login_ticket_ok` audited |

## 8. Audit events (representative)

| Event | When |
|-------|------|
| `project_created` / `project_updated` | Kivo project save |
| `document_saved` | `doc_put` (kind + revision in detail) |
| `studio_draft_approved` | Estimate/RA locked in Approvals |
| `company_profile_saved` | Firm settings |
| `suite_ticket_issued` / `login_ticket_ok` | Launch sibling app |
| `agent_tool_call` | Assistant read tool |

Auditors use **View audit** (`audit_list` / Office → Security).

## 9. Cloud (Neon) — defense in depth

- Credentials only under `%LOCALAPPDATA%\O-Studio\` (e.g. `neon.env`); never in `.osp` or company file body.
- Sync is revision-based; conflict dialog (cloud-ahead) before overwrite.
- Object storage holds `.osp` snapshots; Postgres holds revision metadata — see `docs/CLOUD-NEON.md`.

**Planned:** Neon Auth for viewer/tablet; row-level merge for simultaneous edits; IP allow lists per deployment.

## 10. App responsibilities (quick map)

| Data | Authoritative write | Read |
|------|---------------------|------|
| Firm, users, clients, project registry | Kivo | All apps (context) |
| Drawing & take-off | Tren → `.osp` | Falcon import |
| Quantities, estimate, RA (full) | Falcon → `.osp` `project_doc` | Falcon |
| Abstracts for Studio dashboard / fees | Falcon → company `aqc.project` | Kivo |
| Approval drafts | Falcon share → company drafts | Kivo Approvals |
| Practice invoices & tax | Kivo | Kivo |

## 11. Related documents

| Topic | Document |
|-------|----------|
| Suite names & links | `docs/SUITE.md` |
| Company file concurrency | `docs/shared-company-file.md` |
| ProjectRef / ArtifactRef | `docs/contracts/refs.md` |
| Draft approval ABI | `docs/contracts/approvals.md`, Falcon `docs/STUDIO-DRAFT-SHARE.md` |
| `.osp` format | Kivo `docs/ODP-PROJECT-FILE-AND-SYNC.md`, Falcon `docs/PROJECT-FILE.md` |
| Office ABI | `docs/office-abi.md` |
