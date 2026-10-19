# Hand-off: what is left in OSAS

Written 2026-10-19. OSAS is the suite app (WinUI 3, this repository). It is the one place that chooses and creates the company file, signs in, manages users, shows the file monitor, opens the apps, and now prepares ShilpiDB and the AI assistant. `README.md` says what exists; this says what does not. Nothing here has been tested with a screen reader.

## Built and checked
- Company: choose, create (name, owner, password, location, one-time recovery code), details, sign in/out, users (add, role, password, stop sign-in; each asks for the signer's password). Checked against the real engine by tests and by driving the window with UI Automation.
- One-time ticket: an app opened from its card starts signed in as the same person.
- Company file monitor.
- ShilpiDB section: status, record count, start/stop of a local server. **Looked at in the window; Start was never run against a real `shilpid`** (none is installed on the development machine).
- AI assistant section: tool list and a paste-in MCP settings block. The assistant itself was never connected.
- Tests: 36 in `Osas.Core.Tests` (engine tests need `OFFICE_DLL_DIR`). Installed at `%LOCALAPPDATA%\Programs\OSAS`.

## Open
| # | Item |
|---|---|
| 1 | ~~`install.ps1` published without `office.dll`~~ it now stops unless the engine is found (`-OfficeDllDir`) or `-WithoutEngine` is passed. |
| 2 | ShilpiDB: install `shilpid` and `shilpi-http` with OSAS (or beside it), then run Start against them. Confirm `/stats` against the current ShilpiDB `main` (it was 5 commits ahead of the local copy when checked). Link records to the company by `ProjectRef`. |
| 3 | AI assistant: built in OSAS 2026-10-19 (model choice, own API key, consent, read-only governed tools as the signed-in person). Not yet run against the live service. Still to do: write tools with preview and approval, O-Draft and ShilpiDB tools, streaming, ship `office-mcp.exe` with OSAS. |
| 4 | Known limits (see O-STUDIO `docs/suite-hub.md`): a protected (encrypted) file is single-program; signing out in OSAS does not sign out apps already open; an idle lock makes an app ask for sign-in; switching company does not move open apps. |
| 5 | O-Draft is not integrated (`handoff-o-draft-rebrand.md`, section 11). Its card finds `o-draft.exe`, but no O-Draft is installed here. |
| 6 | Accessibility: UI Automation only. Narrator/NVDA, 200% text, high contrast not done. |
| 7 | An installer that puts the three apps in place, an update check, and a way to see that an app is already running. |
| 8 | The apps are now `O-Studio.exe` and `O-CoreOp.exe`; OSAS still finds `Adms.Desktop.exe`, `AQCCore.exe` and `aad.exe`. Settings folders `ADMS` and `AQCCore` are copied on first run and left in place; delete them when no earlier version is needed. |
