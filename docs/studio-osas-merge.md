# Studio absorbs the OSAS hub

**Status:** Studio-side Phases 1, 3, 4 implemented (2026-10-28).  
**Audience:** Studio, CoreOp, Draft, Form.

## Decision (implemented in this repo)

| Choice | Value |
|---|---|
| Surviving repository | This one (`HolagundiWorks/MynaHub` / `HolagundiWorks/MynaHub`) |
| User-facing shell | **MynaHub** WinUI — company, admin password, users, firm, suite cards, approvals, assistant |
| Source of truth | Company file via `office-core` / `office-ffi` (`office.dll`) |
| Suite brand | User-facing hub is **MynaHub**; wire env vars keep the `OSAS_*` contract |
| Secondary OSAS.exe | **Retired** — see [osas-deprecation.md](osas-deprecation.md) |

## Implemented vs proposed

| Piece | State |
|---|---|
| Studio New / Open company (+ recent list) | **Moved to Myna** (2026-11-03): Studio has no company chooser or creator; its File menu and welcome panel open Myna |
| Studio writes `%LOCALAPPDATA%\OSAS\suite.json` (path + username) | **Implemented** |
| People: create / list / reset password / stop sign-in | **Implemented** |
| Office → Suite apps cards | **Implemented** (scaffold) |
| Suite `install-suite.ps1` + channel schema | **Implemented** |
| Approvals inbox + CoreOp draft kinds (`studio_draft_*`) | **Implemented** — [contracts/approvals.md](contracts/approvals.md); consumes MynaOPS#24 `aqc.estimate.draft` / `aqc.ra.draft.*` |
| Parallel `approval_*` / `suite_approvals` ABI | **Implemented** (tools/tests; not CoreOp product share) |
| Assistant local tools + consent prefs | **Implemented** |
| Firm profile edit in Studio | **Implemented** |
| Ticket redeem when launched with env | **Implemented** |
| Full cloud Anthropic chat UI | **Proposed** (consent prefs exist; chat loop thin) |
| Line-by-line OSAS WinUI import | **Blocked** — OSAS repo still 404 to cloud agent |
| CoreOp product share via `doc_put` draft kinds | **Sibling** — [MynaOPS#24](https://github.com/HolagundiWorks/MynaOPS/pull/24) |

## How siblings keep reading company

CoreOp, Draft and Form continue to open with `OSAS_COMPANY` + one-time `OSAS_TICKET`, then call `document_context`. They must not present an admin-password gate. CoreOp shares bill/estimate drafts with `doc_put` kinds `aqc.estimate.draft` and `aqc.ra.draft.<bill_id>`; Studio alone calls `studio_draft_approve` (Issue) in the product path.

## Related

- Deprecation: [osas-deprecation.md](osas-deprecation.md)  
- Approvals: [contracts/approvals.md](contracts/approvals.md)  
- Project store plan: `studio-osas-merge-plan.md`
