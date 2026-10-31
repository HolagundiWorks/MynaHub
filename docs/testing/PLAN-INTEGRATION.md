# Test plan: the apps working together

Run after each app's own plan has no open S1 bug. Case format and rules: [README.md](README.md). Terms and links: [../SUITE.md](../SUITE.md). Use the test company and a test project throughout.

Links under test:

| Link | Mechanism |
|---|---|
| Launcher or O-Studio to apps | one-time ticket, `OSTUDIO_COMPANY`, `OSTUDIO_TICKET`, `OSTUDIO_USER` |
| Company file | `office.dll` (C interface), read-only details in O-CoreOp and O-Draft |
| O-Draft to O-CoreOp | the same `.osp`; O-CoreOp polls `meta.revision` every 2 s |
| Which `.osp` | active-project pointer file (path in `Office.Shared.SuiteWire`) |
| AI | O-Studio hosts Olmo 3 7B on `127.0.0.1:8742`; O-CoreOp and O-Draft are clients |
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
| INT-LNC-04 | No company chosen | Start O-CoreOp and O-Draft directly with no `suite.json` | They work without company details; message says to use the launcher |
| INT-LNC-05 | Company file in use by two apps | O-Studio and O-CoreOp open the same company | No corruption; both read; writes from O-Studio visible in O-CoreOp |
| INT-LNC-06 | Lock and sign out | Lock or sign out in the launcher while apps run | Document the behaviour of running apps (they keep their ticket session). A missing re-check is logged as a finding, not assumed a bug |
| INT-LNC-07 | Hand-off | Launcher, **O-Studio** | The launcher releases the file; O-Studio opens it signed in; closing O-Studio leaves the launcher usable |
| INT-LNC-08 | Single launcher | Start O-Studio's **Suite launcher…** and the tray launcher together | One launcher process |
| INT-LNC-09 | Status line truth | With AI host up/down, company open/closed, `neon.env` present/absent | `launcher-status.txt` follows within 20 s |

## I3 O-Draft to O-CoreOp through one `.osp`

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-OSP-01 | Record and read | O-Draft: draw walls, Take-off, save as `house.osp`, Record. O-CoreOp: open `house.osp`, Take-off page | Drawing listed with the right counts; read, accept, quantities equal O-Draft's numbers |
| INT-OSP-02 | Live notice | Keep O-CoreOp's Take-off page open; change a wall in O-Draft and Record | O-CoreOp shows "O-Draft updated the take-off" within about 2 s and the list refreshes |
| INT-OSP-03 | Alternating saves | Alternate: O-CoreOp saves the project record; O-Draft records again; repeat 20 times | Revision rises by exactly the number of saves; the project record and the take-off both intact |
| INT-OSP-04 | Simultaneous writers | Script 4 writers (O-Draft recording, O-CoreOp saving, two harness writers) | No lost update, no "database is locked" message beyond the 5 s wait, one revision per commit |
| INT-OSP-05 | Active project pointer | Open `house.osp` in O-Draft; in O-CoreOp use "open O-Draft's project" | O-CoreOp opens the same file; closing it in O-Draft clears the pointer |
| INT-OSP-06 | Drawing preserved | After INT-OSP-03 | The drawing bytes in the `.osp` are identical to the originals unless O-Draft changed them (sha256 in `inspect`) |
| INT-OSP-07 | Save As | O-CoreOp: Save As `copy.osp`; open in O-Draft | Drawing, take-off and project all present |
| INT-OSP-08 | Take-off removed | Delete a measured wall in O-Draft, record | O-CoreOp's list shows one record less; re-import removes nothing already imported unless the importer says so (record the documented behaviour) |
| INT-OSP-09 | Annotated PDF | Record with annotation | O-CoreOp fetches the PDF, stores it, report includes it |
| INT-OSP-10 | Wrong versions `[B]` | Open an `.osp` with an unknown `format` or version 2 | Both apps refuse with a clear message |
| INT-OSP-11 | `.osp` over a network or synced folder `[B]` | Put the file in OneDrive/Drive folder and use both apps | Record the observed behaviour (WAL files, locks); data-loss is S1 |
| INT-OSP-12 | Legacy absence | Look for `.vdb` or `shilpid` anywhere in the apps | None: no menu, command, file type or process |

## I4 Assistant (AI Core)

| ID | Scenario | Steps | Expected |
|---|---|---|---|
| INT-AI-01 | Host starts | Open O-Studio signed in | `127.0.0.1:8742/v1/health` ready; launcher status "ready" |
| INT-AI-02 | Clients | Ask from O-CoreOp and O-Draft | Both answer through the host; one model process |
| INT-AI-03 | Host stops | Close O-Studio | Clients show the "host down" message; launcher shows "not running" |
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
| INT-CLD-01 | Connection | O-Studio, Cloud sync, Test connection with the test keys | Success; status in the launcher "credentials found" |
| INT-CLD-02 | Push | Enable sync for a project; save in O-Draft | The cloud revision equals the local `meta.revision` |
| INT-CLD-03 | Pull | Edit the cloud copy; pull | The local `.osp` gets the change; O-CoreOp shows its notice |
| INT-CLD-04 | Conflict | Edit both | Conflict surfaced; nothing silently lost |
| INT-CLD-05 | Offline | Disable network, edit, reconnect | Edits queue and sync; no corruption |
| INT-CLD-06 | Keys | Search `.osp`, logs, reports, evidence for the connection string | Absent |
| INT-CLD-07 | Draft/CoreOp never hold keys | Inspect their folders and environment | No Neon values |

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
