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
| Studio writes `%LOCALAPPDATA%\OSAS\suite.json` when a company is open | **Implemented** (Phase 1) |
| People (users) on Security page again | **Implemented** (Phase 1) |
| Office → Suite apps cards (`auth.ticket` + `OSAS_COMPANY` / `OSAS_TICKET`) | **Implemented** scaffold (Phase 1); exe discovery best-effort |
| Firm profile edit in Studio | **Implemented** (already) |
| Ticket redeem when launched with env | **Implemented** (already) |
| Bill/estimate approve-lock in Studio | **Proposed** (later phase) |
| Import leftover OSAS WinUI source | **Blocked** until that repo is available |

## How siblings keep reading company

CoreOp, Draft and Form continue to open with `OSAS_COMPANY` + one-time `OSAS_TICKET`, then call `document_context` (and related read ops). They must not present an admin-password gate. See [suite-hub.md](suite-hub.md) and [office-abi.md](office-abi.md).

## Related

- Product brief (project store): `studio-osas-merge.md` / `studio-osas-merge-plan.md`
- Ownership rules: company details and approvals live in Studio only
