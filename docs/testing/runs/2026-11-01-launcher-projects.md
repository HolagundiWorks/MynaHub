# Test run: launcher project selection, installer 1.0.9, 2026-11-01

Agent: Claude (UI Automation against the installed launcher, test company `TestCo-2026-10-31`, owner account)
Build under test: O-Suite-Setup-1.0.9.exe (MynaHub `feat/project-selection-wire`, MynaCAD `feat/project-selection-wire`, MynaOps `feat/project-selection-step3` at 397da22)

| Case | Result | Observed |
|---|---|---|
| LNC-PRJ-01 list | PASS | One item per project, shown `code  name  (client)`; archived hidden |
| LNC-PRJ-02 search | PASS | `villa` narrows to one; `zzzz` gives none; clearing restores |
| LNC-PRJ-03 load | PASS | `selected-project.json` holds company, project id, code, name, `.osp` path, user; the list, search, Load and New go inactive and Close project active; `launcher-status.txt` shows `Project: ... (loaded)` |
| LNC-PRJ-04 folder | PASS | A project without a folder gets the folder and `osas.project.json` when created; the `.osp` itself is made by the app on first open |
| LNC-PRJ-05 launch | PASS | MynaCAD started by Myna on the loaded project (ODraft.WinUi.exe, BUG-0020 fixed) and created the `.osp`, `active-project.json` names it; MynaOps started on the loaded project after BUG-0021 was fixed. Process environment not read directly |
| LNC-PRJ-06 no project | PASS | `--launch MynaCAD` with nothing loaded starts nothing and leaves the request pending; it ran when the project was loaded |
| LNC-PRJ-07 switch while open | PASS | Close project refused with both apps open, naming them; project stays loaded |
| LNC-PRJ-08 close | PASS | Refused while an app is open; after closing the apps it works, the file is gone and the list is active again |
| LNC-PRJ-09 reload | PASS | The same project loads again |
| LNC-PRJ-10 archived | NOT RUN | Needs an archive step in MynaHub |
| LNC-PRJ-11 new project | PASS | Created for an existing client, folder made, loaded at once |
| LNC-PRJ-12 new client | PASS | Client and project created, project loaded |
| LNC-PRJ-13 bad input | PASS | Empty name, duplicate name for the same client and duplicate code each refused; nothing created, load unchanged |
| LNC-PRJ-14 auditor | NOT RUN | The auditor sign-in could not be driven (Sign in did not invoke); needs a manual run |
| LNC-PRJ-15 other company | NOT RUN | Needs a second company file |
| LNC-PRJ-16 tray line | PARTIAL | The status line is in `launcher-status.txt` and the window; the tray menu text was not read (no UIA for the tray) |

Bugs: BUG-0021 (S1, fixed), BUG-0022, 0023, 0024, 0025, 0026 (see BUG-LOG).
