# Test plan: the apps working together

Run after each app's own plan has no open S1 bug. Case format and rules: [README.md](README.md). Terms and links: [../SUITE.md](../SUITE.md). Use the test company and a test project throughout.

Links under test:

| Link | Mechanism |
|---|---|
| Launcher or MynaHub to apps | one-time ticket, `OSTUDIO_COMPANY`, `OSTUDIO_TICKET`, `OSTUDIO_USER` |
| Company file | `office.dll` (C interface), read-only details in MynaOps and MynaCAD |
| MynaCAD to MynaOps | the same `.osp`; MynaOps polls `meta.revision` every 2 s |
| Which `.osp` | active-project pointer file (path in `Office.Shared.SuiteWire`) |
| AI | Myna hosts Olmo 3 7B on `127.0.0.1:8742`; MynaHub, MynaOps and MynaCAD are clients |
| MynaOps to MynaHub | summaries and draft shares mirrored into the company file; approvals inbox |
| MynaHub to cloud | Neon (Postgres, object storage, realtime), MynaHub only |

## I1 Install, upgrade, uninstall

| ID | Scenario | Steps | Expected | How to check |
|---|---|---|---|---|
| INT-INS-01 | Fresh install of each Setup | Clean profile or VM; run `Myna-Setup` and `MynaOpsCAD-Setup` (in either order) | Each exits 0 and installs only its own apps: Myna-Setup gives MynaHub and the Myna launcher (`O-Studio.Launcher.exe` beside `O-Studio.exe`); MynaOpsCAD-Setup gives MynaOps and MynaCAD; Start menu entries; `.osp` opens in MynaCAD (double-click) and has **Open in MynaOps** | folders, registry `HKCU\Software\Classes\.osp` |
| INT-INS-02 | Apps work with only one Setup | Install only MynaOpsCAD-Setup; then only Myna-Setup | MynaOps and MynaCAD start and share the project file without MynaHub; MynaHub starts without them | UIA, process list |
| INT-INS-03 | Upgrade | Install 1.7.1 over an earlier build of the same Setup, and over the separate 1.7.0 MynaOps and MynaCAD Setups | Apps replaced; company files, projects, settings, model kept; old shortcuts and Start menu folders (Kivo, Tren, Falcon, MynaOps, MynaCAD) removed | file hashes, Start menu |
| INT-INS-04 | Uninstall | Settings > Apps: remove Myna, then "MynaOps and MynaCAD" | MynaOps and MynaCAD go together; company files, projects, `%LOCALAPPDATA%\HumanCentricWorks\models` stay; the `.osp` association goes with them | folders, registry |
| INT-INS-05 | Running apps during install | Leave an app open while installing | A clear result (retry/close message), no corrupt install | log |
| INT-INS-06 | Silent install log | `/LOG` | Each Setup log ends with exit 0 | log |
| INT-INS-07 | Model task | Install with the model task on a machine without the model | 4.26 GB file downloaded, SHA-256 `0de0bc63...c5c4` verified, placed in `%LOCALAPPDATA%\HumanCentricWorks\models` | hash |

## I2 Launcher, company file and tickets

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-LNC-01 | Sign-in once | Sign in in Myna; start MynaHub, MynaOps and MynaCAD in turn | None asks for a password; each shows the same company and user |
| INT-LNC-02 | Role carries over | Sign in as Auditor; start each app | Each app shows read-only behaviour for that role |
| INT-LNC-03 | Company details | Edit the firm profile in MynaHub | MynaOps and MynaCAD show the new details after refresh/reopen, read-only |
| INT-LNC-04 | App started alone | Start MynaOps and MynaCAD directly (no environment, no `OSTUDIO_STANDALONE`) | Each starts or wakes Myna with a request and exits; after sign-in and Lock Myna starts it on the project. With no launcher installed the app runs without a company |
| INT-LNC-05 | Company file in use by two apps | MynaHub and MynaOps open the same company | No corruption; both read; writes from MynaHub visible in MynaOps |
| INT-LNC-06 | Lock and sign out | Lock or sign out in Myna while apps run | Document the behaviour of running apps (they keep their ticket session). A missing re-check is logged as a finding, not assumed a bug |
| INT-LNC-07 | Hand-off | Launcher, **MynaHub** | Myna releases the file; MynaHub opens it signed in; closing MynaHub leaves Myna usable |
| INT-LNC-08 | Single launcher | Start MynaHub's **Suite launcherâ€¦** and the tray launcher together | One launcher process |
| INT-LNC-09 | Status line truth | With AI host up/down, company open/closed, `neon.env` present/absent | `launcher-status.txt` follows within 20 s |

## I3 MynaCAD to MynaOps through one `.osp`

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-OSP-01 | Record and read | Lock `house` in Myna. MynaCAD (started from Myna): draw walls, Take-off, Save, Record. MynaOps (started from Myna): Take-off page | Both open the same `.osp`; drawing listed with the right counts; read, accept, quantities equal MynaCAD's numbers |
| INT-OSP-02 | Live notice | Keep MynaOps's Take-off page open; change a wall in MynaCAD and Record | MynaOps shows "MynaCAD updated the take-off" within about 2 s and the list refreshes |
| INT-OSP-03 | Alternating saves | Alternate: MynaOps saves the project record; MynaCAD records again; repeat 20 times | Revision rises by exactly the number of saves; the project record and the take-off both intact |
| INT-OSP-04 | Simultaneous writers | Script 4 writers (MynaCAD recording, MynaOps saving, two harness writers) | No lost update, no "database is locked" message beyond the 5 s wait, one revision per commit |
| INT-OSP-05 | Same project in both apps | Lock a project, start MynaCAD and MynaOps from Myna | Both open the locked `.osp`; `active-project.json` names it; closing MynaCAD deletes the pointer; MynaOps is unaffected |
| INT-OSP-06 | Drawing preserved | After INT-OSP-03 | The drawing bytes in the `.osp` are identical to the originals unless MynaCAD changed them (sha256 in `inspect`) |
| INT-OSP-07 | Copy a project | Copy the project folder in Explorer; add the copy to a project in MynaHub (attach the `.osp`); lock it | Drawing, take-off and project all present in the copy |
| INT-OSP-08 | Take-off removed | Delete a measured wall in MynaCAD, record | MynaOps's list shows one record less; re-import removes nothing already imported unless the importer says so (record the documented behaviour) |
| INT-OSP-09 | Annotated PDF | Record with annotation | MynaOps fetches the PDF, stores it, report includes it |
| INT-OSP-10 | Wrong versions `[B]` | Open an `.osp` with an unknown `format` or version 2 | Both apps refuse with a clear message |
| INT-OSP-11 | `.osp` over a network or synced folder `[B]` | Put the file in OneDrive/Drive folder and use both apps | Record the observed behaviour (WAL files, locks); data-loss is S1 |
| INT-OSP-12 | Legacy absence | Look for `.vdb` or `shilpid` anywhere in the apps | None: no menu, command, file type or process |

## I4 Assistant (AI Core)

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-AI-01 | Host starts | Start Myna (no sign-in needed) | `127.0.0.1:8742/v1/health` ready; launcher status "ready" |
| INT-AI-02 | Clients | Ask from MynaOps and MynaCAD | Both answer through the host; one model process |
| INT-AI-03 | Host stops | Exit Myna | Clients show the "host down" message; launcher shows "not running" |
| INT-AI-04 | Token | Start with no token file | A token is created; a client without it is refused (401) |
| INT-AI-05 | Local only | `netstat` | Listens on loopback only |
| INT-AI-06 | Concurrency | Two questions at once | Both answered or the second waits; no crash |
| INT-AI-07 | Model missing | Move the model | "not installed" in Myna, a clear message in clients |

## I5 MynaOps to MynaHub

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-CMP-01 | Project summary mirror | Link a MynaOps project to a Studio project; save | MynaHub project dashboard shows the summary |
| INT-CMP-02 | Approvals | Share an estimate draft and an RA bill draft from MynaOps | Appear in MynaHub Approvals with kind and project; approve one, reject one; MynaOps shows each decision |
| INT-CMP-03 | Unlinked project | Save a project not linked | Nothing written to the company file |
| INT-CMP-04 | Read-only company in MynaOps | Try to change company data in MynaOps | No way to; the company file stays byte-identical except through MynaHub |
| INT-CMP-05 | Role limits | Share as a user without the right | Refused with a message |

## I6 Cloud sync (test branch only)

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-CLD-01 | Connection | MynaHub, Cloud sync, Test connection with the test keys | Success; Myna status line reads "running" once signed in |
| INT-CLD-02 | Push | Enable sync for a project; save in MynaCAD | The cloud revision equals the local `meta.revision` |
| INT-CLD-03 | Pull | Edit the cloud copy; pull | The local `.osp` gets the change; MynaOps shows its notice |
| INT-CLD-04 | Conflict | Edit both | Conflict surfaced; nothing silently lost |
| INT-CLD-05 | Offline | Disable network, edit, reconnect | Edits queue and sync; no corruption |
| INT-CLD-06 | Keys | Search `.osp`, logs, reports, evidence for the connection string | Absent |
| INT-CLD-07 | Draft/CoreOp never hold keys | Inspect their folders and environment | No Neon values |
| INT-CLD-08 | Sync runs in Myna | Sign in to Myna with Neon set up (test branch); do not open MynaHub | `launcher-status.txt` shows "running"; a project switched on in cloud-sync.json is pushed within seconds of a save |
| INT-CLD-09 | Survives the window and MynaHub | Close the sign-in window, then start MynaHub from Myna | Sync keeps running (status stays "running"); MynaHub's Cloud sync page shows Myna's line and no toggle |
| INT-CLD-10 | Sign out stops it | Sign out in Myna | "stopped (signed out)"; no push after a save; signing in again restarts it |
| INT-CLD-11 | Switch on / off while running | In MynaHub, switch a project on, then off | Myna picks it up within about 5 s; the other projects' switches are not undone by Myna's own saves |
| INT-CLD-12 | Neon unreachable | Point `OSTUDIO_NEON_ENV_FILE` at an unreachable database | "could not connect ... retrying every 30 s"; Myna stays usable |
| INT-CLD-13 | Two companies on one PC | Switch project 1 on in company A; open company B (also has a project 1) | Company B's project 1 is off; switching it on or off does not change company A's; Myna syncs only the signed-in company's switched-on projects |

## I7 Failure and recovery

| ID | Scenario | Expected |
|---|---|---|
| INT-FLT-01 | Kill MynaCAD while recording | The `.osp` opens; no half record (the take-off header says "recording" or "complete" correctly); next record completes it |
| INT-FLT-02 | Kill MynaOps while saving | The `.osp` opens; last committed revision present |
| INT-FLT-03 | Disk full on save | A clear error; the previous file intact |
| INT-FLT-04 | Delete the `.osp` while open in both apps | Each reports an error on next save; no crash |
| INT-FLT-05 | Company file removed while Myna is signed in | Launcher shows "not found"; apps handle it |
| INT-FLT-06 | Locked files from antivirus | Retry or message, no crash |
| INT-FLT-07 | Clock change | Revisions are counters, not times: sync unaffected |

## I8 Choosing the project (launcher)

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-PRJ-01 | Create, lock, open | New project in Myna; start MynaCAD, then MynaOps | The folder exists; MynaCAD creates `project.osp` on first open; MynaOps opens the same file; both show the project name |
| INT-PRJ-02 | Switch refused | Lock A, start MynaCAD, try to lock B | Refused, naming MynaCAD; after closing MynaCAD the switch works |
| INT-PRJ-03 | Double-click a project file | Double-click `project.osp` of a known project | Launcher opens (or wakes), signs in, locks that project, starts MynaCAD on it. For a file outside every project: a message that names the file |
| INT-PRJ-04 | Right-click, Open in MynaOps | Right-click the `.osp` | The same, starting MynaOps |
| INT-PRJ-05 | Project folder missing | Rename the project folder, start an app | The app says the folder was not found and to choose the project again; nothing is created |
| INT-PRJ-06 | Archived while locked | Archive the locked project in MynaHub, try to lock or start | Refused; the list no longer shows it |
| INT-PRJ-07 | Second PC | Open the same company file on another PC | The project list is the same; the lock is local to each PC |
