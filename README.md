# OSAS, the Open Source Architecture Suit

A small launcher for the three apps of the suite:

| App | For |
|---|---|
| **O-Studio** | Office and project management |
| **O-CoreOp** | Quantities, estimates and billing |
| **O-Draft** | Drafting |

OSAS shows which of them are installed on this computer, with their versions, and opens them. It starts the apps and changes nothing in them. O-Studio and O-CoreOp can have the same company file open at the same time.

## Company file monitor
Below the app cards, OSAS watches the company file that O-Studio and O-CoreOp share. Choose the `.adms` file once (it is remembered in `%LOCALAPPDATA%OSAScompany.txt`); it looks again every two seconds.

| Shown | What it is |
|---|---|
| Size on disk | The file plus its write-ahead log |
| Free inside the file | Pages SQLite has freed but not given back; compacting returns them |
| Project documents, Stored files | Room taken by O-CoreOp's saved projects, and by letters, drawings and other stored files |
| Records | Counts of clients, projects, invoices and audit entries |
| Writes by other programs | Looks in the last minute that saw another program write (several commits between two looks show as one) |
| File format | Schema version, journal mode, page count and size |
| Backups | Files in the `<name> backups` folder beside the file, their size and the newest |
| Drive | Free and total space where the file is |
| Open in | Which apps have the file open, with each one's processor use and memory (asked of Windows, so it is the real list) |
| Space used by table | The biggest tables, where the database engine reports it |

It opens the file read-only, never signs in, and shows sizes and counts only: no names, amounts or documents. A protected (encrypted) file can only be measured from outside, so for those it shows the size and who has it open.

## Install
```powershell
powershell -ExecutionPolicy Bypass -File installerinstall.ps1            # install for the current user
powershell -ExecutionPolicy Bypass -File installerinstall.ps1 -Uninstall # remove it
```
It publishes self-contained (no .NET or Windows App SDK needed on the computer), installs to `%LOCALAPPDATA%ProgramsOSAS`, adds a Start menu shortcut and an entry in Settings > Apps, and needs no administrator rights. Your saved choices in `%LOCALAPPDATA%OSAS` are kept when you uninstall. `hcw-ui` must be checked out beside this repository.

## How an app is found
In this order, for each app: a program you chose with **Find it…** (kept in `%LOCALAPPDATA%\OSAS\paths.json`); the folder its installer registered; the usual install folder (`%LOCALAPPDATA%\Programs\O-Studio`, `O-CoreOp`, `O-Draft`, and the earlier names); and the `PATH`. A chosen path must be an absolute path to an `.exe` that exists.

| App | Program | Registered by |
|---|---|---|
| O-Studio | `Adms.Desktop.exe` | `HKCU\Software\Human Centric Works\OStudio` |
| O-CoreOp | `AQCCore.exe` | Inno Setup entry `{A8F3C2E1-…}_is1` |
| O-Draft | `o-draft.exe` (older installs: `aad.exe`) | nothing known yet |

## Projects
| Folder | |
|---|---|
| `Osas.Core` | Finding the apps and the saved paths, and the company file monitor (`Monitor/`). No user interface, so it is tested on its own |
| `Osas.Core.Tests` | Twenty-two tests, including one that asks Windows which program holds a file with a pretend computer: `dotnet test Osas.Core.Tests` |
| `Osas.Launcher` | The WinUI 3 window. Uses the shared design in `hcw-ui` (check it out beside this repository) |

```powershell
dotnet build Osas.Launcher -o Osas.Launcher\bin\Next
```

The window was checked with UI Automation: every button has a name, and an app that is not installed has a disabled Open button and a Find it button. It has not been tried with a screen reader.

## Not built yet
An installer that puts the launcher and the apps in place, a check for newer versions, opening a company file straight from the launcher, and a way to tell when an app is already running.

## Licence
The launcher is Human Centric Works software. It uses `hcw-ui`, which is MIT licensed; the marks in that repository have a separate notice (`hcw-ui/brand/LICENSE.md`). The licence of this repository has not been chosen.
