# OSAS handoff notes (historical)

Written 2026-10-19. OSAS is the suite app (WinUI 3, this repository). It is the one place that chooses and creates the company file, signs in, manages users, shows the file monitor, opens the apps, and hosts the AI assistant. `README.md` says what exists; this says what does not. Nothing here has been tested with a screen reader.

**Update 2026-10-30:** Legacy spatial-server UI and connectors were removed. Project data lives in `.osp` files; see [SUITE.md](../SUITE.md) and [ODP-PROJECT-FILE-AND-SYNC.md](../ODP-PROJECT-FILE-AND-SYNC.md).

## Built (2026-10-19 through 2026-10-20)

- Company section: choose/create company, sign-in, users, company details, one-time tickets to sibling apps.
- Company file monitor: size, WAL, table counts, backups, who has the file open.
- AI assistant: local Tulu in O-Studio; governed read-only tools via `office-mcp`.

## Still open

| # | Item |
|---|---|
| 1 | Installer that stages all suite apps (see `installer/suite/`) |
| 2 | Neon cloud sync connector in O-Studio (see `docs/ODP-PROJECT-FILE-AND-SYNC.md`) |
| 3 | Write tools with preview and approval; O-Draft tools; streaming assistant replies |

## Moved into O-Studio

OSAS is no longer a separate program. Its library is `native/Osas.Core` and its screens are the **Suite** page of O-Studio (`native/Adms.Desktop/Suite/`).
