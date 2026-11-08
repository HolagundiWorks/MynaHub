; One Setup for the whole suite: Kivo, Falcon, Tren (and Finch / O-Dynamo when their payloads are given to build-single.ps1).
; Per-user, no administrator. Each app keeps its own folder, Start menu entry and Settings > Apps entry exactly as when installed on its own:
; Falcon and Kivo are installed by their own Setup run silently, Tren is copied from its portable layout (the MSI is per-machine).
; Build with build-single.ps1, which stages the payloads and calls ISCC with the defines below.

#ifndef SuiteVersion
  #define SuiteVersion "1.0.0"
#endif
#ifndef Stage
  #define Stage "stage"
#endif
#define MyAppName "Kivo Suite"
#define MyPublisher "Human Centric Works"
#define ModelFile "Olmo-3-7B-Instruct-Q4_K_M.gguf"
#define ModelSha256 "0de0bc63fae8a73a48e167be48f149c9544bd4af61d223cd3f331c6ea664c5c4"
#define ModelUrl "https://huggingface.co/unsloth/Olmo-3-7B-Instruct-GGUF/resolve/a2a9ae53f5a95087616b3f45ca96e9d2631344ee/" + ModelFile

[Setup]
AppId={{5D0E7A64-3B1C-4F6E-9A55-0C4B6E1D2F87}
AppName={#MyAppName}
AppVersion={#SuiteVersion}
AppPublisher={#MyPublisher}
DefaultDirName={localappdata}\Programs\O-Suite
DefaultGroupName=Kivo Suite
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#Stage}\..\out
OutputBaseFilename=Kivo-Suite-Setup-{#SuiteVersion}
Compression=lzma2/fast
SolidCompression=no
WizardStyle=modern
LicenseFile={#Stage}\LICENSE
UninstallDisplayName={#MyAppName} (choose apps to remove in Setup, or use Apps and features for full removal)
VersionInfoVersion={#SuiteVersion}.0
VersionInfoCompany={#MyPublisher}
VersionInfoProductName={#MyAppName}
CloseApplications=no
ChangesAssociations=yes
DiskSpanning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Everything"
Name: "compact"; Description: "Kivo only"
Name: "custom"; Description: "Choose the apps"; Flags: iscustom

[Components]
Name: "studio"; Description: "Kivo: office and project management (the suite hub)"; Types: full compact custom; Flags: checkablealone
Name: "coreop"; Description: "Falcon: quantities, estimates and billing"; Types: full custom; Flags: checkablealone
Name: "draft"; Description: "Tren: drafting and take-off"; Types: full custom; Flags: checkablealone
#ifdef HasForm
Name: "form"; Description: "Finch: 3D modelling"; Types: full custom; Flags: checkablealone
#endif
#ifdef HasDynamo
Name: "dynamo"; Description: "O-Dynamo: visual programming"; Types: full custom; Flags: checkablealone
#endif

[Tasks]
Name: "model"; Description: "Download the assistant model (Olmo 3 7B, 4.5 GB; runs on this computer only, Falcon and Tren share it)"; GroupDescription: "Assistant:"; Components: coreop draft

[Files]
; sub-installers: extracted to the temp folder, run in [Run], deleted afterwards
Source: "{#Stage}\O-Studio-Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Components: studio
Source: "{#Stage}\O-CoreOp-Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Components: coreop
#ifdef HasForm
Source: "{#Stage}\O-Form-Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Components: form
#endif
#ifdef HasDynamo
Source: "{#Stage}\O-Dynamo-Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Components: dynamo
#endif
; Tren: the portable layout
Source: "{#Stage}\draft\*"; DestDir: "{localappdata}\Programs\O-Draft"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: draft

[InstallDelete]
Type: files; Name: "{userprograms}\O-Draft.lnk"; Components: draft

[Icons]
Name: "{userprograms}\Tren"; Filename: "{localappdata}\Programs\O-Draft\ODraft.WinUi.exe"; WorkingDir: "{localappdata}\Programs\O-Draft"; Components: draft

[Registry]
; The Kivo Suite project file (.osp): double-click opens the file in Tren. Falcon's own Setup adds "Open in Falcon".
; Neither verb starts Myna.
Root: HKCU; Subkey: "Software\Classes\.osp"; ValueType: string; ValueName: ""; ValueData: "OSAS.Project"; Flags: uninsdeletevalue; Components: draft
Root: HKCU; Subkey: "Software\Classes\OSAS.Project"; ValueType: string; ValueName: ""; ValueData: "Kivo Suite project"; Components: draft
Root: HKCU; Subkey: "Software\Classes\OSAS.Project\shell\open"; ValueType: string; ValueName: ""; ValueData: "Open in Tren"; Flags: uninsdeletekey; Components: draft
Root: HKCU; Subkey: "Software\Classes\OSAS.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{localappdata}\Programs\O-Draft\ODraft.WinUi.exe"" ""%1"""; Components: draft

[Run]
Filename: "{tmp}\O-Studio-Setup.exe"; Parameters: "/S"; StatusMsg: "Installing Kivo..."; Flags: waituntilterminated; Components: studio
Filename: "{tmp}\O-CoreOp-Setup.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""model"""; StatusMsg: "Installing Falcon (assistant model via Kivo Suite task)..."; Flags: waituntilterminated; Components: coreop; Tasks: model
Filename: "{tmp}\O-CoreOp-Setup.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""!model"""; StatusMsg: "Installing Falcon..."; Flags: waituntilterminated; Components: coreop; Tasks: not model
#ifdef HasForm
Filename: "{tmp}\O-Form-Setup.exe"; Parameters: "/S"; StatusMsg: "Installing Finch..."; Flags: waituntilterminated; Components: form
#endif
#ifdef HasDynamo
Filename: "{tmp}\O-Dynamo-Setup.exe"; Parameters: "/S"; StatusMsg: "Installing O-Dynamo..."; Flags: waituntilterminated; Components: dynamo
#endif

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Programs\O-Draft"; Components: draft

[UninstallRun]
; each app removes itself the way it was installed; the model in %LocalAppData%\HumanCentricWorks and the projects are left alone
Filename: "{localappdata}\Programs\O-CoreOp\unins000.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"; Flags: waituntilterminated skipifdoesntexist; RunOnceId: "RemoveCoreOp"; Components: coreop
Filename: "{localappdata}\Programs\O-Studio\Uninstall.exe"; Parameters: "/S"; Flags: waituntilterminated skipifdoesntexist; RunOnceId: "RemoveStudio"; Components: studio

[Code]
#include "O-Suite-Wizard.iss"
