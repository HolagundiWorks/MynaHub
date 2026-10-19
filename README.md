# OSAS, the Open Source Architecture Suit

A small launcher for the three apps of the suite:

| App | For |
|---|---|
| **O-Studio** | Office and project management |
| **O-CoreOp** | Quantities, estimates and billing |
| **O-Draft** | Drafting |

OSAS shows which of them are installed on this computer, with their versions, and opens them. It starts the apps and changes nothing in them. O-Studio and O-CoreOp can use the same company file, but only one of them at a time.

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
| `Osas.Core` | Finding the apps and the saved paths. No user interface, so it is tested on its own |
| `Osas.Core.Tests` | Eight tests with a pretend computer: `dotnet test Osas.Core.Tests` |
| `Osas.Launcher` | The WinUI 3 window. Uses the shared design in `hcw-ui` (check it out beside this repository) |

```powershell
dotnet build Osas.Launcher -o Osas.Launcher\bin\Next
```

The window was checked with UI Automation: every button has a name, and an app that is not installed has a disabled Open button and a Find it button. It has not been tried with a screen reader.

## Not built yet
An installer that puts the launcher and the apps in place, a check for newer versions, opening a company file straight from the launcher, and a way to tell when an app is already running.

## Licence
The launcher is Human Centric Works software. It uses `hcw-ui`, which is MIT licensed; the marks in that repository have a separate notice (`hcw-ui/brand/LICENSE.md`). The licence of this repository has not been chosen.
