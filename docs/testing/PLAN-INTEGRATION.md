# Test plan: the apps working together

Run after each app's own plan has no open S1 bug. Case format and rules: [README.md](README.md). Terms and links: [../SUITE.md](../SUITE.md). Use the test company and a test project throughout.

Links under test:

| Link | Mechanism |
|---|---|
| Launcher or O-Studio to apps | one-time ticket, `OSTUDIO_COMPANY`, `OSTUDIO_TICKET`, `OSTUDIO_USER` |
| Company file | `office.dll` (C interface), read-only details in O-CoreOp and O-Draft |
| O-Draft to O-CoreOp | the same `.osp`; O-CoreOp polls `meta.revision` every 2 s |
| Which `.osp` | active-project pointer file (path in `Office.Shared.SuiteWire`) |
| AI | The launcher hosts Olmo 3 7B on `127.0.0.1:8742`; O-Studio, O-CoreOp and O-Draft are clients |
| O-CoreOp to O-Studio | summaries and draft shares mirrored into the company file; approvals inbox |
| O-Studio to cloud | Neon (Postgres, object storage, realtime), O-Studio only |

## I1 Install, upgrade, uninstall

| ID | Scenario | Steps | Expected | How to check |
|---|---|---|---|---|
| INT-INS-01 | Fresh install | Clean profile or VM; run the suite Setup with all components | Exit 0; three app folders; Start menu entries; launcher in `...\O-Studio\launcher`; `.osp` registered | folders, registry `HKCU\Software\Classes\.osp` |
| INT-INS-02 | Component choice | Install only O-Studio, then add O-Draft | Only chosen apps installed; adding works | folders |
| INT-INS-03 | Upgrade over an older suite | Install 1.0.x over 1.0.y | Apps replaced; company files, projects, settings, model kept | file hashes |
| INT-INS-04 | Uninstall | Apps and features, "O-Suite" | Apps removed; company files, projects, `%LOCALAPPDATA%\HumanCentricWorks\models` stay; file association removed | folders, registry |
| INT-INS-05 | Running apps during install | Leave an app open while installing | A clear result (retry/close message), no corrupt install | log |
| INT-INS-06 | Silent install log | `/LOG` | Log ends with exit 0 and lists three app installers | log |
| INT-INS-07 | Model task | Install with the model task on a machine without the model | 4.26 GB file downloaded, SHA-256 `0de0bc63...c5c4` verified, placed in `%LOCALAPPDATA%\HumanCentricWorks\models` | hash |

## I2 Launcher, company file and tickets

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-LNC-01 | Sign-in once | Sign in in the launcher; start O-Studio, O-CoreOp and O-Draft in turn | None asks for a password; each shows the same company and user |
| INT-LNC-02 | Role carries over | Sign in as Auditor; start each app | Each app shows read-only behaviour for that role |
| INT-LNC-03 | Company details | Edit the firm profile in O-Studio | O-CoreOp and O-Draft show the new details after refresh/reopen, read-only |
| INT-LNC-04 | App started alone | Start O-CoreOp and O-Draft directly (no environment, no `OSTUDIO_STANDALONE`) | Each starts or wakes the launcher with a request and exits; after sign-in and Lock the launcher starts it on the project. With no launcher installed the app runs without a company |
| INT-LNC-05 | Company file in use by two apps | O-Studio and O-CoreOp open the same company | No corruption; both read; writes from O-Studio visible in O-CoreOp |
| INT-LNC-06 | Lock and sign out | Lock or sign out in the launcher while apps run | Document the behaviour of running apps (they keep their ticket session). A missing re-check is logged as a finding, not assumed a bug |
| INT-LNC-07 | Hand-off | Launcher, **O-Studio** | The launcher releases the file; O-Studio opens it signed in; closing O-Studio leaves the launcher usable |
| INT-LNC-08 | Single launcher | Start O-Studio's **Suite launcherâ€¦** and the tray launcher together | One launcher process |
| INT-LNC-09 | Status line truth | With AI host up/down, company open/closed, `neon.env` present/absent | `launcher-status.txt` follows within 20 s |

## I3 O-Draft to O-CoreOp through one `.osp`

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-OSP-01 | Record and read | Lock `house` in the launcher. O-Draft (started from the launcher): draw walls, Take-off, Save, Record. O-CoreOp (started from the launcher): Take-off page | Both open the same `.osp`; drawing listed with the right counts; read, accept, quantities equal O-Draft's numbers |
| INT-OSP-02 | Live notice | Keep O-CoreOp's Take-off page open; change a wall in O-Draft and Record | O-CoreOp shows "O-Draft updated the take-off" within about 2 s and the list refreshes |
| INT-OSP-03 | Alternating saves | Alternate: O-CoreOp saves the project record; O-Draft records again; repeat 20 times | Revision rises by exactly the number of saves; the project record and the take-off both intact |
| INT-OSP-04 | Simultaneous writers | Script 4 writers (O-Draft recording, O-CoreOp saving, two harness writers) | No lost update, no "database is locked" message beyond the 5 s wait, one revision per commit |
| INT-OSP-05 | Same project in both apps | Lock a project, start O-Draft and O-CoreOp from the launcher | Both open the locked `.osp`; `active-project.json` names it; closing O-Draft deletes the pointer; O-CoreOp is unaffected |
| INT-OSP-06 | Drawing preserved | After INT-OSP-03 | The drawing bytes in the `.osp` are identical to the originals unless O-Draft changed them (sha256 in `inspect`) |
| INT-OSP-07 | Copy a project | Copy the project folder in Explorer; add the copy to a project in O-Studio (attach the `.osp`); lock it | Drawing, take-off and project all present in the copy |
| INT-OSP-08 | Take-off removed | Delete a measured wall in O-Draft, record | O-CoreOp's list shows one record less; re-import removes nothing already imported unless the importer says so (record the documented behaviour) |
| INT-OSP-09 | Annotated PDF | Record with annotation | O-CoreOp fetches the PDF, stores it, report includes it |
| INT-OSP-10 | Wrong versions `[B]` | Open an `.osp` with an unknown `format` or version 2 | Both apps refuse with a clear message |
| INT-OSP-11 | `.osp` over a network or synced folder `[B]` | Put the file in OneDrive/Drive folder and use both apps | Record the observed behaviour (WAL files, locks); data-loss is S1 |
| INT-OSP-12 | Legacy absence | Look for `.vdb` or `shilpid` anywhere in the apps | None: no menu, command, file type or process |

## I4 Assistant (AI Core)

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-AI-01 | Host starts | Start the launcher (no sign-in needed) | `127.0.0.1:8742/v1/health` ready; launcher status "ready" |
| INT-AI-02 | Clients | Ask from O-CoreOp and O-Draft | Both answer through the host; one model process |
| INT-AI-03 | Host stops | Exit the launcher | Clients show the "host down" message; launcher shows "not running" |
| INT-AI-04 | Token | Start with no token file | A token is created; a client without it is refused (401) |
| INT-AI-05 | Local only | `netstat` | Listens on loopback only |
| INT-AI-06 | Concurrency | Two questions at once | Both answered or the second waits; no crash |
| INT-AI-07 | Model missing | Move the model | "not installed" in the launcher, a clear message in clients |

## I5 O-CoreOp to O-Studio

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-CMP-01 | Project summary mirror | Link an O-CoreOp project to a Studio project; save | O-Studio project dashboard shows the summary |
| INT-CMP-02 | Approvals | Share an estimate draft and an RA bill draft from O-CoreOp | Appear in O-Studio Approvals with kind and project; approve one, reject one; O-CoreOp shows each decision |
| INT-CMP-03 | Unlinked project | Save a project not linked | Nothing written to the company file |
| INT-CMP-04 | Read-only company in O-CoreOp | Try to change company data in O-CoreOp | No way to; the company file stays byte-identical except through O-Studio |
| INT-CMP-05 | Role limits | Share as a user without the right | Refused with a message |

## I6 Cloud sync (test branch only)

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-CLD-01 | Connection | O-Studio, Cloud sync, Test connection with the test keys | Success; the launcher status line reads "running" once signed in |
| INT-CLD-02 | Push | Enable sync for a project; save in O-Draft | The cloud revision equals the local `meta.revision` |
| INT-CLD-03 | Pull | Edit the cloud copy; pull | The local `.osp` gets the change; O-CoreOp shows its notice |
| INT-CLD-04 | Conflict | Edit both | Conflict surfaced; nothing silently lost |
| INT-CLD-05 | Offline | Disable network, edit, reconnect | Edits queue and sync; no corruption |
| INT-CLD-06 | Keys | Search `.osp`, logs, reports, evidence for the connection string | Absent |
| INT-CLD-07 | Draft/CoreOp never hold keys | Inspect their folders and environment | No Neon values |
| INT-CLD-08 | Sync runs in the launcher | Sign in to the launcher with Neon set up (test branch); do not open O-Studio | `launcher-status.txt` shows "running"; a project switched on in cloud-sync.json is pushed within seconds of a save |
| INT-CLD-09 | Survives the window and O-Studio | Close the sign-in window, then start O-Studio from the launcher | Sync keeps running (status stays "running"); O-Studio's Cloud sync page shows the launcher's line and no toggle |
| INT-CLD-10 | Sign out stops it | Sign out in the launcher | "stopped (signed out)"; no push after a save; signing in again restarts it |
| INT-CLD-11 | Switch on / off while running | In O-Studio, switch a project on, then off | The launcher picks it up within about 5 s; the other projects' switches are not undone by the launcher's own saves |
| INT-CLD-12 | Neon unreachable | Point `OSTUDIO_NEON_ENV_FILE` at an unreachable database | "could not connect ... retrying every 30 s"; the launcher stays usable |
| INT-CLD-13 | Two companies on one PC | Switch project 1 on in company A; open company B (also has a project 1) | Company B's project 1 is off; switching it on or off does not change company A's; the launcher syncs only the signed-in company's switched-on projects |

## I7 Failure and recovery

| ID | Scenario | Expected |
|---|---|---|
| INT-FLT-01 | Kill O-Draft while recording | The `.osp` opens; no half record (the take-off header says "recording" or "complete" correctly); next record completes it |
| INT-FLT-02 | Kill O-CoreOp while saving | The `.osp` opens; last committed revision present |
| INT-FLT-03 | Disk full on save | A clear error; the previous file intact |
| INT-FLT-04 | Delete the `.osp` while open in both apps | Each reports an error on next save; no crash |
| INT-FLT-05 | Company file removed while the launcher is signed in | Launcher shows "not found"; apps handle it |
| INT-FLT-06 | Locked files from antivirus | Retry or message, no crash |
| INT-FLT-07 | Clock change | Revisions are counters, not times: sync unaffected |

## I8 Choosing the project (launcher)

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-PRJ-01 | Create, lock, open | New project in the launcher; start O-Draft, then O-CoreOp | The folder exists; O-Draft creates `project.osp` on first open; O-CoreOp opens the same file; both show the project name |
| INT-PRJ-02 | Switch refused | Lock A, start O-Draft, try to lock B | Refused, naming O-Draft; after closing O-Draft the switch works |
| INT-PRJ-03 | Double-click a project file | Double-click `project.osp` of a known project | Launcher opens (or wakes), signs in, locks that project, starts O-Draft on it. For a file outside every project: a message that names the file |
| INT-PRJ-04 | Right-click, Open in O-CoreOp | Right-click the `.osp` | The same, starting O-CoreOp |
| INT-PRJ-05 | Project folder missing | Rename the project folder, start an app | The app says the folder was not found and to choose the project again; nothing is created |
| INT-PRJ-06 | Archived while locked | Archive the locked project in O-Studio, try to lock or start | Refused; the list no longer shows it |
| INT-PRJ-07 | Second PC | Open the same company file on another PC | The project list is the same; the lock is local to each PC |
