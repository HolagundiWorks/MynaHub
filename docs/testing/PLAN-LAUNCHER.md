# Test plan: the O-Studio launcher

The launcher (`O-Studio.Launcher.exe`, in the `launcher` folder beside O-Studio) is a tray app. It holds the company sign-in, shows the status of the AI agent, the database and cloud sync, and starts the other apps with a one-time ticket. Source: `O-STUDIO\native\Adms.LoginWidget`.

Case format and rules: [README.md](README.md). `[N]` normal, `[B]` boundary or bad input, `[R]` repeat or undo.

## L1 Smoke

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| LNC-SMK-01 | Start | Start `launcher\O-Studio.Launcher.exe` | The process stays running for 30 s; a tray icon appears (it may be in the hidden-icons area); no window opens by itself | `Get-Process O-Studio.Launcher`; UIA tray overflow item named like "O-Studio suite" |
| LNC-SMK-02 | No crash events | After LNC-SMK-01 read the Application event log | No `Application Error` for `O-Studio.Launcher.exe` | `Get-WinEvent` |
| LNC-SMK-03 | Exit | Tray menu, **Exit** | The process ends; the tray icon disappears | process list |

## L2 Single session

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| LNC-ONE-01 | Second start `[N]` | Start the launcher while one is running | Still exactly one `O-Studio.Launcher` process; the second process exits with code 0; the **Company login** window opens in the first | process count, exit code, window title |
| LNC-ONE-02 | Rapid starts `[B]` | Start it 5 times within 2 s | One process remains; no error dialog | process count |
| LNC-ONE-03 | After exit `[R]` | Exit, then start again | A new launcher starts normally | process count |
| LNC-ONE-04 | After a crash `[B]` | Kill the process (`Stop-Process -Force`), start again | Starts normally (no stale lock) | process count |
| LNC-ONE-05 | Start from O-Studio | O-Studio, **Suite launcher…** while the launcher runs | No second launcher; the sign-in window comes forward | process count |
| LNC-ONE-06 | Two Windows users | (only on a machine with two accounts) each starts a launcher | Each user has their own; they do not block each other | process list per session |

## L3 Tray icon and menu

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| LNC-TRY-01 | Tooltip, no company | No company remembered (`suite.json` absent) | Tooltip "O-Studio suite launcher" | UIA name of the tray item |
| LNC-TRY-02 | Tooltip, company remembered | Company remembered, not signed in | "O-Studio suite — company remembered, not signed in" | UIA name |
| LNC-TRY-03 | Tooltip, signed in | Sign in (L5) | "<Company> — <user> (O-Studio suite)" | UIA name |
| LNC-TRY-04 | Menu opens | Click the icon (left or right) | A menu with, in order: status line; **AI agent:**; **Database:**; **Cloud sync:**; **Refresh status**; separator; **Company sign-in…**; one item per suite app (O-Studio, O-CoreOp, O-Draft, and O-Form/O-Dynamo only if installed); separator; **Exit** | screenshot of the menu region; UIA `#32768` |
| LNC-TRY-05 | Apps greyed when signed out | Open the menu while not signed in | App items are disabled; Company sign-in and Refresh are enabled | screenshot |
| LNC-TRY-06 | Apps enabled when signed in | Open the menu while signed in | App items are enabled | screenshot |
| LNC-TRY-07 | Long text `[B]` | Company with a 90-character name | Menu lines are shortened with an ellipsis, not cut mid-menu or overflowing | screenshot |
| LNC-TRY-08 | Escape | Open the menu, press Esc | Menu closes; nothing is started | screenshot, process list |
| LNC-TRY-09 | Explorer restart `[B]` | Restart `explorer.exe` | Known limit to record: does the icon return? Write the observed result | tray UIA |

## L4 Status lines and the status file

The file is `%LOCALAPPDATA%\O-Studio\launcher-status.txt`: four lines (time, AI agent, Database, Cloud sync).

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| LNC-STA-01 | File written | Start the launcher | The file exists within 10 s with the four lines | file read |
| LNC-STA-02 | AI agent, host down | Nothing on port 8742 | "AI agent: not running (port 8742 is taken by another program)" when another program holds the port; with the launcher running and nothing else on the port the line is "ready" | file |
| LNC-STA-03 | AI agent, host up | Start the launcher (it starts the host); wait 20 s or choose Refresh status | "AI agent: ready (Olmo 3 7B on port 8742)" | file; `curl http://127.0.0.1:8742/v1/health` |
| LNC-STA-04 | AI agent, model missing | Temporarily rename the model file (restore afterwards!) and start the host | "model or runtime not installed" | file |
| LNC-STA-05 | AI agent, token mismatch | Put a wrong token in `OSTUDIO_AI_TOKEN` for a client while the host runs (restore afterwards) | "token mismatch: close the suite apps and restart the launcher" | file |
| LNC-STA-06 | Database, none | No remembered company | "no company file chosen" | file |
| LNC-STA-07 | Database, closed | A remembered existing company, not open | "<file> (<size>), closed" | file |
| LNC-STA-08 | Database, missing file `[B]` | Remembered path whose file was renamed | "<file> not found" | file |
| LNC-STA-09 | Database, open signed out / signed in / locked | Open the company in the sign-in window; sign in; lock | "open, sign-in needed", then "open, signed in as <user>", then "open, session locked" | file |
| LNC-STA-10 | Size shown | Company file of about 1 MB and one under 1 KB | `MB` with one decimal; at least `1 KB` | file |
| LNC-STA-11 | Cloud, no credentials | No `neon.env` | "not set up (no neon.env)" | file |
| LNC-STA-12 | Cloud, credentials | `neon.env` with a `DATABASE_URL=` value | "credentials found; starts at sign-in" before sign-in; after sign-in "running (n projects syncing)" (n = projects switched on in O-Studio > Cloud sync); "could not connect (...); retrying every 30 s" when Neon cannot be reached; after Sign out "stopped (signed out); starts again at the next sign-in" | file |
| LNC-STA-13 | Cloud, empty value `[B]` | `DATABASE_URL=` with nothing after it | "neon.env has no database address" | file |
| LNC-STA-14 | Refresh status | Change something, choose **Refresh status** | The file's first line (time) advances at once | file |
| LNC-STA-15 | Auto refresh | Leave it for 60 s | The time line advances at least twice | file |
| LNC-STA-16 | Unreadable folder `[B]` | Make the folder read-only | The launcher keeps running; no dialog | process |
| LNC-STA-17 | No secrets | Read the file and the menu | No password, ticket, token or connection string appears anywhere | text search |

## L5 Company sign-in window

Opened by **Company sign-in…**, by a second launch, or by an error.

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| LNC-WIN-01 | Opens | Choose Company sign-in… | A window "Company login", about 420x640, shows the welcome panel (create, open, recent) and, at the bottom, the three status lines | UIA tree, screenshot |
| LNC-WIN-02 | Status lines live | Change state (L4) with the window open | The three lines update without reopening | UIA text |
| LNC-WIN-03 | Create a company | Create a company with name, owner, password of 12+ characters | The company opens; the recovery code is shown once; setup completes | UIA, `suite.json` |
| LNC-WIN-04 | Password too short `[B]` | 11 characters | Refused with a clear message; nothing created | UIA |
| LNC-WIN-05 | Open an existing company | Pick a `.osas` file | Opens and asks for sign-in | UIA |
| LNC-WIN-06 | Wrong file `[B]` | Pick a `.txt` renamed `.osas` | Refused with a message; the launcher is still running | UIA |
| LNC-WIN-07 | Sign in | Correct user and password | "Signed in" panel with the company and user | UIA |
| LNC-WIN-08 | Wrong password `[B]` | Wrong password 3 times | Refused each time with a message; no lock-out crash; password field cleared | UIA |
| LNC-WIN-09 | Protected file | Company encrypted at rest | Asks for the file password or recovery code first | UIA |
| LNC-WIN-10 | Lock / Unlock | Lock, then unlock | The session locks (tray tooltip changes), unlock with the password restores it | UIA, tooltip |
| LNC-WIN-11 | Sign out | Sign out | Back to sign-in; tray app items disabled again | menu screenshot |
| LNC-WIN-12 | Close company | Close company | Company closed; the file lock is released (another app can open it) | open the file in O-Studio |
| LNC-WIN-13 | Writes `suite.json` | Sign in | `%LOCALAPPDATA%\OSAS\suite.json` has the company path and user name, no password | file read |
| LNC-WIN-14 | Close the window | Close with the X | The launcher keeps running in the tray | process |
| LNC-WIN-15 | Forgotten password | Look for a recovery route | Record where it is (O-Studio has one: "Forgot password? Use recovery code"). A missing route in the launcher is not a bug if documented | doc |

## L6 Launching apps

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| LNC-APP-01 | Start O-CoreOp | Signed in, menu, **O-CoreOp** | O-CoreOp starts on the same company, signed in as the same user, with no password prompt | process, window title |
| LNC-APP-02 | Start O-Draft | Menu, **O-Draft** | O-Draft starts and shows company details read-only | process, UI |
| LNC-APP-03 | Start O-Studio (hand-off) | Menu, **O-Studio** | The launcher releases the company, O-Studio opens it signed in as the same user; the launcher shows it closed | process, status file |
| LNC-APP-04 | Ticket is single-use | Copy the ticket from a started app's environment, try to use it again (test harness) | Second use refused | `auth.login_ticket` call |
| LNC-APP-05 | Ticket expires | Wait 3 minutes, use an issued ticket | Refused (tickets last about 2 minutes) | harness |
| LNC-APP-06 | App not installed `[B]` | Rename an app's folder, choose it | Message that the app is not installed; the launcher keeps running | UIA |
| LNC-APP-07 | Signed out `[B]` | Try to start an app signed out | Not possible (item greyed) | menu |
| LNC-APP-08 | Environment | Inspect the started app's environment | `OSTUDIO_COMPANY` (path), `OSTUDIO_TICKET` (cleared by the app after use), `OSTUDIO_USER`; no password | process environment |

## L6 Project selection (design: [../PROJECT-SELECTION-DESIGN.md](../PROJECT-SELECTION-DESIGN.md))

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| LNC-PRJ-01 | List `[N]` | Sign in; look at the Project section | Drop-down lists the company's projects as `code  name  (client)`; archived ones are absent | UIA |
| LNC-PRJ-02 | Search `[N]` | Type part of a code, name or client in the filter | The list narrows; clearing the filter restores it | UIA |
| LNC-PRJ-03 | Load `[N]` | Pick a project, Load project | State line says Loaded with code and name; the list, filter, Load project and New project… are inactive and Close project is active; `selected-project.json` has the project id, `.osp` path, company and user; status file `Project:` line shows it | file read |
| LNC-PRJ-04 | Folder made on load `[N]` | Load a project that has no folder yet | The project folder with `osas.project.json` exists; the selection names `project.osp` inside it | file system |
| LNC-PRJ-05 | Launch on the project `[N]` | Load, then Tray > O-CoreOp and O-Draft | Each starts with `OSTUDIO_PROJECT_ID` and `OSTUDIO_PROJECT_OSP` set (read from the process environment) | process env |
| LNC-PRJ-06 | Launch without a project `[B]` | Close the project, Tray > O-Draft | Refused with "Load a project first" | message |
| LNC-PRJ-07 | Switch refused while an app is open `[B]` | Load A, start O-Draft, try Close project | Refused: "O-Draft still has A open"; A stays loaded and the list stays inactive | message, file |
| LNC-PRJ-08 | Close refused while an app is open `[B]` | O-Draft running, Close project | Refused naming O-Draft; after closing the app, Close project works, `selected-project.json` is gone and the list becomes active again | file |
| LNC-PRJ-09 | Closed project re-opens `[R]` | Close project, pick the same project, Load project | Loads again; the list is inactive while it is loaded | UIA |
| LNC-PRJ-10 | Archived project `[B]` | Archive a project in O-Studio, refresh the list | Not listed; a stale lock on it is refused by `project_launch_info` (launchable false) | engine call |
| LNC-PRJ-11 | New project `[N]` | New project…, existing client, name, accept the generated code | Project and its folder exist in the company file; it is loaded at once | O-Studio project list |
| LNC-PRJ-12 | New project, new client `[N]` | New project…, New client…, enter the client name | Client and project created, project loaded | O-Studio |
| LNC-PRJ-13 | New project, bad input `[B]` | No name; no client; a duplicate name for the same client; a duplicate code | Each refused with a clear message; nothing created | message |
| LNC-PRJ-14 | New project as Auditor `[B]` | Sign in as Auditor, New project… | Refused (Edit permission); no records created | message, audit log |
| LNC-PRJ-15 | Other company `[B]` | Load in company A, sign in to company B | B sees no loaded project; A's project returns when A is opened again | file |
| LNC-PRJ-16 | Tray status line `[N]` | Open the tray menu | A disabled line `Project: code name (loaded)` or `none loaded` | UIA |
