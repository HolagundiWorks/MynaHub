# OSAS.exe deprecation

**Status:** Implemented policy (2026-10-28)  
**Surviving product:** O-Studio (`HolagundiWorks/O-STUDIO`)

## What changed

The separate **OSAS** suite hub executable is **deprecated**. O-Studio is the user-facing shell for:

- Choosing and creating the company file
- Admin password and users
- Firm (company) details
- Suite launcher cards (`OSAS_COMPANY` + `OSAS_TICKET`)
- Approvals inbox for CoreOp estimates / running bills
- Assistant (governed local tools; cloud chat opt-in)

The **OSAS** name remains the suite brand. Environment variables and `%LOCALAPPDATA%\OSAS\suite.json` are unchanged so CoreOp, Draft and Form keep working.

## Installers

Use `installer/install-suite.ps1` in this repository. Do not publish a new OSAS Setup as the company chooser.

## If the OSAS GitHub repository becomes writable

Add a short README at the top that points contributors to `HolagundiWorks/O-STUDIO` and states that OSAS.exe is no longer the hub. Archive the repository when ready.
