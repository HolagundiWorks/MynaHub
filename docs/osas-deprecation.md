# OSAS.exe deprecation

**Status:** Implemented policy (2026-10-28)  
**Surviving product:** O-Studio (`HolagundiWorks/O-STUDIO`)

## What changed

The **OSAS.exe** launcher is **deprecated**. **O-Studio** is the user-facing shell for:

- Choosing and creating the company file
- Admin password and users
- Firm (company) details
- Suite launcher cards (sibling apps via ticket)
- Approvals inbox for CoreOp estimates / running bills
- Assistant (governed local tools; cloud chat opt-in)

User-facing docs and UI must say **O-Studio**, not OSAS or a separate launcher. Environment variables and `%LOCALAPPDATA%\OSAS\suite.json` stay unchanged on the wire so CoreOp, Draft and Form keep working.

## Installers

Use `installer/install-suite.ps1` in this repository. Do not publish a new OSAS Setup as the company chooser.

## If the OSAS GitHub repository becomes writable

Add a short README at the top that points contributors to `HolagundiWorks/O-STUDIO` and states that OSAS.exe is no longer the hub. Archive the repository when ready.
