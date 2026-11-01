# OSAS.exe retired

**Status:** Retired (2026-11-01). Deprecated 2026-10-28. **Surviving product:** O-Studio (`HolagundiWorks/O-STUDIO`).

The old **OSAS.exe** hub is gone. Its work is done by two apps:

- **The O-Studio launcher** (`O-Studio.Launcher.exe`, `native/Adms.LoginWidget`): sign-in, the project list (select, load, close), starting O-Draft, O-CoreOp and later O-Form with the selected project, the AI agent and the database (cloud sync) servers, and the tray icon.
- **O-Studio**: the company file, admin password, users, firm details, approvals and the Neon sync settings.

Removed on 2026-11-01: `installer/osas` (the OSAS Setup scripts), `docs/osas` (the hand-off notes) and the installed OSAS 0.3.0 entry on the development machine. Saved choices in `%LOCALAPPDATA%\OSAS` are not touched.

## What stays, on purpose

These keep the OSAS name because renaming them would break existing files and the other apps; they are not a product:

- the shared library **Osas.Core** (`native/Osas.Core`) and its tests,
- the company file extension **`.osas`**,
- the data folder `%LOCALAPPDATA%\OSAS` (`suite.json`, `active-project.json`) next to `%LOCALAPPDATA%\O-Studio`,
- `OSAS_*` environment variables read by older builds.

User text says **O-Studio**; never OSAS (see `SUITE.md`).

## The old repository

`OSAS-Open_Source_Architecture_Suit` is no longer built or released. It still holds uncommitted edits on branch `suite/single-installer`, so it was left as it is; add a README pointing to `HolagundiWorks/O-STUDIO` and archive it when those edits are dealt with.
