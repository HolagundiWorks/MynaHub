# OSAS, the Open Source Architecture Suit

A small launcher for the three apps of the suite:

| App | For |
|---|---|
| **O-Studio** | Office and project management |
| **O-CoreOp** | Quantities, estimates and billing |
| **O-Draft** | Drafting |

OSAS shows which of them are installed on this computer, with their versions, and opens them. It starts the apps and changes nothing in them. O-Studio and O-CoreOp can have the same company file open at the same time.

## Company: one company file, one sign-in
The **Company** section at the top is where the suite's company file is chosen. Choose the `.osas` file (earlier `.adms` files open too), sign in once with your own user name and password, and OSAS:

- **creates** a new company (**Create a company…**: its name, the owner's user name and password, where to keep the file, and the recovery code shown once), or **chooses** an existing file. This is the only place a company is chosen or created: O-Studio and O-CoreOp have no file picker of their own;
- shows the company's name and who is signed in, and **Company details…** edits the name, address, contact, PAN, GSTIN, registration, bank details, letterhead style and accent that every app prints on its documents (saving needs the settings permission);
- starts each app on that company file, and signs it in as the same person with a **one-time ticket** (32 random bytes, good for two minutes and one use), so you do not type the password again and it is never passed on or stored;
- shows the company's **Users** (below the buttons) and manages them: add a user, change a role, set a new password for someone who forgot theirs, stop or allow sign-in. These people are the same in every app, and each change asks for your own password again. The last active owner can never be removed. O-Studio no longer has its own People section;
- remembers only the file's path and your user name, in `%LOCALAPPDATA%OSASsuite.json`.

If you are not signed in in OSAS, the app is still started on the file and asks for the sign-in itself. If the company file is protected (encrypted), OSAS asks for the file password too, but an app started on it asks for it again. The design is in the O-Studio repository, `docs/suite-hub.md`.

OSAS needs `office.dll`, the company-file engine, beside it. It is built from `office-ffi` in the O-Studio repository; the installer finds it there, or give `-OfficeDllDir <folder>`.

## Company file monitor
Below the app cards, OSAS watches the company file that O-Studio and O-CoreOp share. Choose the `.osas` file (earlier `.adms` files open too) once (it is remembered in `%LOCALAPPDATA%OSAScompany.txt`); it looks again every two seconds.

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

## ShilpiDB
The suite's spatial store for drawing geometry (shapes as boxes with data, found by place). The **ShilpiDB** section shows whether the server is running, its web gateway, and how many records it holds, and starts or stops a server for the chosen company. The data file is kept beside the company file, named `<company>.shilpi.vdb`. OSAS starts it only on `127.0.0.1`, stops only a server it started itself, and asks for the record count only, never the records. It looks for `shilpid.exe` and `shilpi-http.exe` beside OSAS, in `%LOCALAPPDATA%\Programs\shilpidb`, in the folder set as `shilpi_dir` in `suite.json`, and on the `PATH`. Host and ports (`shilpi_host`, `shilpi_port`, `shilpi_http_port`) are set in `suite.json` too. Clients, projects and money stay in the company file; ShilpiDB records point at them by ProjectRef.

## AI assistant
The **AI assistant** section prepares the connection for an assistant such as Claude Desktop or Claude Code. The assistant is `office-mcp`, started by that tool, not a chat window in OSAS. It signs in as one company user, so it sees only what that user's role allows, and it can use only fixed read-only tools (clients, projects, library, staff, proposals, meetings, TDS check). OSAS shows the tools and gives a settings block to paste. The password in it is a placeholder: OSAS never holds or writes a password. Tools that change records (preview, approval, audit) and tools for O-Draft and ShilpiDB are not built.

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
| O-Studio | `O-Studio.exe` (older installs: `Adms.Desktop.exe`) | `HKCU\Software\Human Centric Works\OStudio` |
| O-CoreOp | `O-CoreOp.exe` (older installs: `AQCCore.exe`) | Inno Setup entry `{A8F3C2E1-…}_is1` |
| O-Draft | `o-draft.exe` (older installs: `aad.exe`) | nothing known yet |

## Projects
| Folder | |
|---|---|
| `Osas.Core` | Finding the apps and the saved paths, and the company file monitor (`Monitor/`). No user interface, so it is tested on its own |
| `Osas.Core.Tests` | Thirty-six tests: the monitor, ShilpiDB and the assistant settings, and the company details, tickets and settings against the real engine (set `OFFICE_DLL_DIR` to the folder with `office.dll`); one asks Windows which program holds a file with a pretend computer: `dotnet test Osas.Core.Tests` |
| `Osas.Launcher` | The WinUI 3 window. Uses the shared design in `hcw-ui` (check it out beside this repository) |

```powershell
dotnet build Osas.Launcher -o Osas.Launcher\bin\Next
```

The window was checked with UI Automation: every button has a name, and an app that is not installed has a disabled Open button and a Find it button. It has not been tried with a screen reader.

## Not built yet
An installer that puts the launcher and the apps in place, a check for newer versions, opening a company file straight from the launcher, and a way to tell when an app is already running.

## Licence
The launcher is Human Centric Works software. It uses `hcw-ui`, which is MIT licensed; the marks in that repository have a separate notice (`hcw-ui/brand/LICENSE.md`). The licence of this repository has not been chosen.
