# Studio absorbs the OSAS hub

**Status:** Decision recorded 2026-10-28 (Phase 1).  
**Audience:** Studio, CoreOp, Draft, Form.

## Decision (implemented in this repo)

| Choice | Value |
|---|---|
| Surviving repository | This one (`HolagundiWorks/O-STUDIO` / `holagundiworks/o-studio`) |
| User-facing shell | **O-Studio** WinUI — company choose/create, admin password, users, firm profile, suite launcher cards |
| Source of truth | Company file via `office-core` / `office-ffi` (`office.dll`) |
| Suite brand | **OSAS** remains the suite name; env vars and `suite.json` keep the `OSAS_*` contract |
| Secondary OSAS.exe | Deprecated as the place to choose the company; keep ticket redeem for older launchers |

## Implemented vs proposed

| Piece | State |
|---|---|
| Studio New / Open company (+ recent list) | **Implemented** (Phase 1) |
| Studio writes `%LOCALAPPDATA%\OSAS\suite.json` (path + username, never password) | **Implemented** (Phase 1) |
| People: create / list / reset password / stop sign-in | **Implemented** (Phase 1; `user_set_password` on Studio bridge) |
| Office → Suite apps cards (`auth.ticket` + `OSAS_COMPANY` / `OSAS_TICKET`) | **Implemented** scaffold (Phase 1); exe discovery best-effort |
| Suite `install-suite.ps1` + channel schema in this repo | **Implemented** (migrated from documented OSAS hub) |
| Firm profile edit in Studio | **Implemented** (already) |
| Ticket redeem when launched with env | **Implemented** (already) |
| AI assistant WinUI (model/key/consent) | **Proposed** — engine ops exist; OSAS UI not importable yet |
| Bill/estimate approve-lock in Studio | **Proposed** (later phase) |
| Line-by-line OSAS WinUI import | **Blocked** — OSAS repo still 404 to this cloud agent |

## How siblings keep reading company

CoreOp, Draft and Form continue to open with `OSAS_COMPANY` + one-time `OSAS_TICKET`, then call `document_context` (and related read ops). They must not present an admin-password gate. See [suite-hub.md](suite-hub.md) and [office-abi.md](office-abi.md).

## Related

- Product brief (project store): `studio-osas-merge.md` / `studio-osas-merge-plan.md`
- Ownership rules: company details and approvals live in Studio only
