# Architecture

- **App:** `native/Adms.Desktop`, a WinUI 3 app (executable `O-Studio.exe`).
- **Engine:** Rust `office-core` (SQLite, the company file), `office-ffi` and `src-tauri`, built as `architect_office_desktop_lib.dll`. `office-agent` provides governed read-only tools that `office-ffi` links.
- **C# libraries:** `Osas.Core` (suite catalog and shared logic, with `Osas.Core.Tests`), `Adms.Cloud` (Neon sync), `Adms.Interop`.
- **Cloud:** `cloud/`, `neon.ts` and `package.json` hold the Neon sync setup. Credentials live in `%LOCALAPPDATA%\OSAS\neon.env` and are never committed.
- **Installers:** NSIS for MynaHub (`installer/aos.nsi`, `installer/build.ps1`) and, through `installer/suite/build-all.ps1`, the combined Inno Setup installer for MynaOps and MynaCAD.
- **Upgrade:** MynaHub-Setup removes the old Myna launcher of a 1.7.x install.
