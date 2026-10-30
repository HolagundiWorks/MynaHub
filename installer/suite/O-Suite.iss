; One Setup for the whole suite: O-Studio, O-CoreOp, O-Draft (and O-Form / O-Dynamo when their payloads are given to build-single.ps1).
; Per-user, no administrator. Each app keeps its own folder, Start menu entry and Settings > Apps entry exactly as when installed on its own:
; O-CoreOp and O-Studio are installed by their own Setup run silently, O-Draft is copied from its portable layout (the MSI is per-machine).
; Build with build-single.ps1, which stages the payloads and calls ISCC with the defines below.

#ifndef SuiteVersion
  #define SuiteVersion "1.0.0"
#endif
#ifndef Stage
  #define Stage "stage"
#endif
#define MyAppName "O-Suite"
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
DefaultGroupName=O-Suite
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#Stage}\..\out
OutputBaseFilename=O-Suite-Setup-{#SuiteVersion}
Compression=lzma2/fast
SolidCompression=no
WizardStyle=modern
LicenseFile={#Stage}\LICENSE
UninstallDisplayName={#MyAppName} (uninstalls the apps it installed)
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
Name: "compact"; Description: "O-Studio only"
Name: "custom"; Description: "Choose the apps"; Flags: iscustom

[Components]
Name: "studio"; Description: "O-Studio: office and project management (the suite hub)"; Types: full compact custom; Flags: checkablealone
Name: "coreop"; Description: "O-CoreOp: quantities, estimates and billing"; Types: full custom; Flags: checkablealone
Name: "draft"; Description: "O-Draft: drafting and take-off"; Types: full custom; Flags: checkablealone
#ifdef HasForm
Name: "form"; Description: "O-Form: 3D modelling"; Types: full custom; Flags: checkablealone
#endif
#ifdef HasDynamo
Name: "dynamo"; Description: "O-Dynamo: visual programming"; Types: full custom; Flags: checkablealone
#endif

[Tasks]
Name: "model"; Description: "Download the assistant model (Olmo 3 7B, 4.5 GB; runs on this computer only, O-CoreOp and O-Draft share it)"; GroupDescription: "Assistant:"; Components: coreop draft

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
; O-Draft: the portable layout
Source: "{#Stage}\draft\*"; DestDir: "{localappdata}\Programs\O-Draft"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: draft

[Icons]
Name: "{userprograms}\O-Draft"; Filename: "{localappdata}\Programs\O-Draft\ODraft.WinUi.exe"; WorkingDir: "{localappdata}\Programs\O-Draft"; Components: draft

[Registry]
; The O-Suite project file (.osp): double-click opens it in O-Draft; the right-click menu also has O-CoreOp (registered by its own Setup).
Root: HKCU; Subkey: "Software\Classes\.osp"; ValueType: string; ValueName: ""; ValueData: "OSAS.Project"; Flags: uninsdeletevalue; Components: draft
Root: HKCU; Subkey: "Software\Classes\OSAS.Project"; ValueType: string; ValueName: ""; ValueData: "O-Suite project"; Components: draft
Root: HKCU; Subkey: "Software\Classes\OSAS.Project\shell\open"; ValueType: string; ValueName: ""; ValueData: "Open in O-Draft"; Flags: uninsdeletekey; Components: draft
Root: HKCU; Subkey: "Software\Classes\OSAS.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{localappdata}\Programs\O-Draft\ODraft.WinUi.exe"" ""%1"""; Components: draft

[Run]
Filename: "{tmp}\O-Studio-Setup.exe"; Parameters: "/S"; StatusMsg: "Installing O-Studio..."; Flags: waituntilterminated; Components: studio
Filename: "{tmp}\O-CoreOp-Setup.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""model"""; StatusMsg: "Installing O-CoreOp (and downloading the assistant model)..."; Flags: waituntilterminated; Components: coreop; Tasks: model
Filename: "{tmp}\O-CoreOp-Setup.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"; StatusMsg: "Installing O-CoreOp..."; Flags: waituntilterminated; Components: coreop; Tasks: not model
#ifdef HasForm
Filename: "{tmp}\O-Form-Setup.exe"; Parameters: "/S"; StatusMsg: "Installing O-Form..."; Flags: waituntilterminated; Components: form
#endif
#ifdef HasDynamo
Filename: "{tmp}\O-Dynamo-Setup.exe"; Parameters: "/S"; StatusMsg: "Installing O-Dynamo..."; Flags: waituntilterminated; Components: dynamo
#endif

[UninstallRun]
; each app removes itself the way it was installed; the model in %LocalAppData%\HumanCentricWorks and the projects are left alone
Filename: "{localappdata}\Programs\O-CoreOp\unins000.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"; Flags: waituntilterminated skipifdoesntexist; RunOnceId: "RemoveCoreOp"
Filename: "{localappdata}\Programs\O-Studio\Uninstall.exe"; Parameters: "/S"; Flags: waituntilterminated skipifdoesntexist; RunOnceId: "RemoveStudio"

[Code]
var
  ModelPage: TDownloadWizardPage;

function ModelPath: String;
begin
  Result := ExpandConstant('{localappdata}\HumanCentricWorks\models\{#ModelFile}');
end;

procedure InitializeWizard;
begin
  ModelPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

{ The model is fetched here once, checked against its SHA-256 and put where both apps look. O-CoreOp's own Setup then finds it and skips its download. }
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsTaskSelected('model') and (not FileExists(ModelPath)) then
  begin
    ModelPage.Clear;
    ModelPage.Add('{#ModelUrl}', '{#ModelFile}', '{#ModelSha256}');
    ModelPage.Show;
    try
      try
        ModelPage.Download;
        ForceDirectories(ExtractFileDir(ModelPath));
        if not RenameFile(ExpandConstant('{tmp}\{#ModelFile}'), ModelPath) then
        begin
          CopyFile(ExpandConstant('{tmp}\{#ModelFile}'), ModelPath, False);
          DeleteFile(ExpandConstant('{tmp}\{#ModelFile}'));
        end;
      except
        if ModelPage.AbortedByUser then
          Log('Model download aborted by the user.')
        else
          SuppressibleMsgBox(AddPeriod(GetExceptionMessage) + #13#10 + 'The assistant will not work until {#ModelFile} is in ' + ExtractFileDir(ModelPath) + '.', mbError, MB_OK, IDOK);
      end;
    finally
      ModelPage.Hide;
    end;
  end;
end;
