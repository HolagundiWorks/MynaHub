# One Setup for the suite

`build-single.ps1` makes `out\O-Suite-Setup-<version>.exe` (about 210 MB, per user, no administrator). The wizard offers **O-Studio**, **O-CoreOp** and **O-Draft** as checkboxes, plus a task to download the assistant model (Olmo 3 7B, 4.5 GB, SHA-256 checked, shared by O-CoreOp and O-Draft).

It does not build the apps. Build them first (`..\build-suite.ps1`, `O-COREOP\BBSDesktop\installer\build-installer.ps1`, `O-DRAFT\packaging\windows\package.ps1`), then run this. It stages: O-Studio's NSIS Setup (run `/S`), O-CoreOp's Inno Setup (run `/VERYSILENT`, model task passed on), and O-Draft's portable zip unpacked into `%LocalAppData%\Programs\O-Draft`. O-Draft's MSI is per-machine, so it is not used.

O-Form and O-Dynamo get a checkbox only when `-FormSetup` / `-DynamoSetup` name a finished Setup; neither has a build here, so this Setup has three apps.

Each app keeps its own Start menu and Settings > Apps entry. The suite's own entry (O-Suite) uninstalls the apps it installed (their uninstallers are run); the model in `%LocalAppData%\HumanCentricWorks\models` and projects are left.

Checked 2026-10-29: silent install of all three (39 s), installed O-Draft started and served its file, uninstall removed all four entries, reinstall restored them. Not checked: the interactive wizard; the model download inside this Setup (the same code as O-CoreOp's, run there); unsigned (no signing material).
