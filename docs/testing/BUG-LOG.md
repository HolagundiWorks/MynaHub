# Bug log

One row per bug. Process: [BUG-PROCESS.md](BUG-PROCESS.md). Severity: [README.md](README.md). Newest at the bottom. S1 and S2 also have a file in `bugs/`.

| Id | Severity | App | Case | Summary | Status | Found | Fix |
|---|---|---|---|---|---|---|---|
| BUG-0001 | S3 | O-Studio (`Osas.Core.Tests`) | ENV-03 | `SuiteCompanyTests.The_environment_an_app_is_started_with_names_the_company_and_the_ticket` fails: the test expects one environment variable when no ticket is given, but the suite shell now also sets the `OSAS_AI_*` / AI variables | verified fixed (59/59 pass on 2026-11-01) | 2026-10-30 | 2026-11-01 run |
| BUG-0002 | S3 | Launcher | LNC-TRY-04 | The tray menu with the AI agent, database and cloud sync lines was added and compiles, but its rendered text has not been inspected by a test (the status file is checked instead) | open — status file verified; menu pixels not captured | 2026-10-31 | |
| BUG-0003 | S3 | O-Studio repo | ENV-04 | `installer/stage-test/*` (about 100 built Windows App SDK files) is tracked in git (commit `7d8e5d9`); build output should not be tracked | open | 2026-10-31 | |
| BUG-0004 | S3 | O-Studio repo | ENV-04 | The earlier public history of the OSAS repository contains installers (about 320 MB, `installer/suite-dist`); only a history rewrite removes them and that needs the owner's decision | open (owner decision) | 2026-10-30 | |
| BUG-0005 | S4 | O-Studio build | ENV-04 | The launcher and O-Studio each carry the full Windows App SDK payload, so the O-Studio Setup is large (61 MB compressed); publishing them as one app would halve it | open | 2026-10-31 | |

Items 0002 to 0005 were found while building the installers and are listed so the agent confirms or closes them in its first run. Items still to be found by the test program are added below.

| BUG-0006 | S2 | Launcher | LNC-WIN-03 | Sign-in window has no **Create a new company** flow; plan expects creating `TestCo-<date>.osas` from the launcher | fixed in code, to be verified in a UI run | 2026-11-01 | launcher commit |
| BUG-0007 | S3 | Launcher | LNC-WIN-01 | Welcome panel is Open + Follow only; plan lists create, open, and recent list on the welcome panel | fixed in code, to be verified in a UI run | 2026-11-01 | launcher commit |
| BUG-0008 | S3 | Launcher / docs | LNC-WIN-13 | Plan cites `%LOCALAPPDATA%\OSAS\suite.json`; app writes `%LOCALAPPDATA%\O-Studio\suite.json` per SUITE.md | fixed in code, to be verified in a UI run | 2026-11-01 | launcher commit |
| BUG-0009 | S3 | Launcher | LNC-WIN-15 | No user password recovery (“Forgot password?”) on the sign-in panel; only file-unlock recovery and admin setup recovery code | fixed in code, to be verified in a UI run | 2026-11-01 | launcher commit |
