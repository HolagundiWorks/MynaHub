// Pascal script for O-Suite.iss: unified install and remove flows in one Setup package.

var
  SuiteModePage: TWizardPage;
  SuiteModeInstall: Boolean;
  RemoveSelectPage: TWizardPage;
  RemoveConfirmPage: TWizardPage;
  RemoveSummaryPage: TWizardPage;
  RemoveList: TNewCheckListBox;
  RemoveStudio, RemoveCoreOp, RemoveDraft: Boolean;
  RemoveSummaryText: String;
  ModelPage: TDownloadWizardPage;
  RemoveSummaryMemo: TNewStaticText;

function Expand(const S: String): String;
begin
  Result := ExpandConstant(S);
end;

function FileExistsExpanded(const PathConst: String): Boolean;
begin
  Result := FileExists(Expand(PathConst));
end;

function IsStudioInstalled: Boolean;
begin
  Result := FileExistsExpanded('{localappdata}\Programs\O-Studio\O-Studio.exe');
end;

function IsCoreOpInstalled: Boolean;
begin
  Result := FileExistsExpanded('{localappdata}\Programs\O-CoreOp\unins000.exe');
end;

function IsDraftInstalled: Boolean;
begin
  Result := FileExistsExpanded('{localappdata}\Programs\O-Draft\ODraft.WinUi.exe');
end;

function AnySuiteAppInstalled: Boolean;
begin
  Result := IsStudioInstalled or IsCoreOpInstalled or IsDraftInstalled;
end;

function ParamValue(const Name: String): String;
begin
  Result := ExpandConstant('{param:' + Name + '|}');
end;

function IsSilentSetup: Boolean;
begin
  Result := WizardSilent;
end;

function ParseRemoveList(const List: String): Boolean;
var
  S: String;
begin
  Result := True;
  RemoveStudio := False;
  RemoveCoreOp := False;
  RemoveDraft := False;
  S := LowerCase(List);
  if S = '' then
    Exit;
  if (Pos('studio', S) > 0) or (Pos('all', S) > 0) or (Pos('full', S) > 0) then
    RemoveStudio := True;
  if (Pos('coreop', S) > 0) or (Pos('all', S) > 0) or (Pos('full', S) > 0) then
    RemoveCoreOp := True;
  if (Pos('draft', S) > 0) or (Pos('all', S) > 0) or (Pos('full', S) > 0) then
    RemoveDraft := True;
end;

procedure InitRemoveFlagsFromList;
begin
  RemoveStudio := RemoveList.Checked[0];
  RemoveCoreOp := RemoveList.Checked[1];
  RemoveDraft := RemoveList.Checked[2];
end;

function CountRemoveSelected: Integer;
begin
  Result := 0;
  if RemoveStudio then Inc(Result);
  if RemoveCoreOp then Inc(Result);
  if RemoveDraft then Inc(Result);
end;

procedure SetProgress(Pct: Integer; const Msg: String);
begin
  if Assigned(WizardForm) then
  begin
    WizardForm.StatusLabel.Caption := Msg;
    WizardForm.ProgressGauge.Position := Pct;
  end;
end;

function ExecAndWait(const Filename, Params, Status: String): Boolean;
var
  ResultCode: Integer;
begin
  SetProgress(WizardForm.ProgressGauge.Position, Status);
  Result := Exec(Filename, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure RemoveDraftLayout;
var
  Dir: String;
begin
  Dir := Expand('{localappdata}\Programs\O-Draft');
  if DirExists(Dir) then
    DelTree(Dir, True, True, True);
  // The .osp association is shared: MynaOps adds its own "Open in MynaOps" verb to the same ProgId. While MynaOps stays
  // installed only MynaCAD's verb goes; the file type and the other app's verb are left alone.
  if IsCoreOpInstalled then
  begin
    if RegKeyExists(HKCU, 'Software\Classes\OSAS.Project\shell\open') then
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\OSAS.Project\shell\open');
  end
  else
  begin
    if RegKeyExists(HKCU, 'Software\Classes\.osp') then
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\.osp');
    if RegKeyExists(HKCU, 'Software\Classes\OSAS.Project') then
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\OSAS.Project');
  end;
  DeleteFile(Expand('{userprograms}\O-Draft.lnk'));
  DeleteFile(Expand('{userprograms}\Tren.lnk'));
  DeleteFile(Expand('{userprograms}\MynaCAD.lnk'));
end;

{ Names of the apps chosen for removal, one per line, for the confirmation. }
function RemoveSelectionText: String;
begin
  Result := '';
  if RemoveStudio then Result := Result + '  - MynaHub' + #13#10;
  if RemoveCoreOp then Result := Result + '  - MynaOps' + #13#10;
  if RemoveDraft then Result := Result + '  - MynaCAD' + #13#10;
end;

{ MynaOps and MynaCAD use MynaHub (the suite hub: company sign-in, approvals and revisions; it starts Myna, which hosts the assistant and the database service). Removing it while one of them stays installed
  needs a decision. Returns False to stay on the page. Silent setups keep the other apps and log it. }
function ResolveStudioDependents: Boolean;
var
  Stay: String;
  Answer: Integer;
begin
  Result := True;
  if not RemoveStudio then
    Exit;
  Stay := '';
  if IsCoreOpInstalled and (not RemoveCoreOp) then Stay := Stay + '  - MynaOps' + #13#10;
  if IsDraftInstalled and (not RemoveDraft) then Stay := Stay + '  - MynaCAD' + #13#10;
  if Stay = '' then
    Exit;
  if IsSilentSetup then
  begin
    Log('MynaHub is removed while these stay installed: ' + Stay);
    Exit;
  end;
  Answer := MsgBox('These applications use MynaHub (company sign-in, and Myna, which hosts the assistant and the database service) and are still installed:' + #13#10#13#10 + Stay + #13#10 +
    'Yes: remove them as well.' + #13#10 + 'No: keep them (they cannot sign in to a company or reach the shared database until MynaHub is installed again).' + #13#10 +
    'Cancel: go back and change the selection.', mbConfirmation, MB_YESNOCANCEL);
  if Answer = IDCANCEL then
  begin
    Result := False;
    Exit;
  end;
  if Answer = IDYES then
  begin
    if IsCoreOpInstalled then
    begin
      RemoveCoreOp := True;
      RemoveList.Checked[1] := True;
    end;
    if IsDraftInstalled then
    begin
      RemoveDraft := True;
      RemoveList.Checked[2] := True;
    end;
  end;
end;

function RunSelectedRemovals(var Summary: String): Boolean;
var
  Ok: Boolean;
  Lines: String;
begin
  Ok := True;
  Lines := '';
  if Assigned(WizardForm) then
    WizardForm.ProgressGauge.Style := npbstNormal;
  SetProgress(5, 'Preparing removal…');

  if RemoveStudio then
  begin
    if FileExistsExpanded('{localappdata}\Programs\O-Studio\Uninstall.exe') then
    begin
      if not ExecAndWait(Expand('{localappdata}\Programs\O-Studio\Uninstall.exe'), '/S', 'Removing MynaHub…') then
      begin
        Ok := False;
        Lines := Lines + '- MynaHub: uninstaller reported an error.' + #13#10;
      end
      else
        Lines := Lines + '- MynaHub removed.' + #13#10;
    end
    else
      Lines := Lines + '- MynaHub was not installed (skipped).' + #13#10;
    SetProgress(40, 'MynaHub step finished.');
  end;

  if RemoveCoreOp then
  begin
    if FileExistsExpanded('{localappdata}\Programs\O-CoreOp\unins000.exe') then
    begin
      if not ExecAndWait(Expand('{localappdata}\Programs\O-CoreOp\unins000.exe'), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', 'Removing MynaOps…') then
      begin
        Ok := False;
        Lines := Lines + '- MynaOps: uninstaller reported an error.' + #13#10;
      end
      else
        Lines := Lines + '- MynaOps removed.' + #13#10;
    end
    else
      Lines := Lines + '- MynaOps was not installed (skipped).' + #13#10;
    SetProgress(70, 'MynaOps step finished.');
  end;

  if RemoveDraft then
  begin
    SetProgress(80, 'Removing MynaCAD…');
    RemoveDraftLayout;
    Lines := Lines + '- MynaCAD removed.' + #13#10;
  end;

  Lines := Lines + #13#10 +
    'The assistant model in %LocalAppData%\HumanCentricWorks\models, company files (.osas), and project files (.osp) were not deleted.';
  Summary := Lines;
  SetProgress(100, 'Removal finished.');
  Result := Ok;
end;

procedure DeselectAllInstallComponents;
begin
  WizardSelectComponents('');
end;

procedure EnsureInstallDependencies;
var
  Names: String;
begin
  if (WizardIsComponentSelected('coreop') or WizardIsComponentSelected('draft')) and
     (not WizardIsComponentSelected('studio')) then
  begin
    Names := 'studio';
    if WizardIsComponentSelected('coreop') then
      Names := Names + ',coreop';
    if WizardIsComponentSelected('draft') then
      Names := Names + ',draft';
    WizardSelectComponents(Names);
  end;
end;

function ModelPath: String;
begin
  Result := Expand('{localappdata}\HumanCentricWorks\models\{#ModelFile}');
end;

procedure SuiteModePageClick(Sender: TObject);
begin
  SuiteModeInstall := (TNewRadioButton(Sender).Tag = 0);
end;

procedure CreateSuiteWizardPages;
var
  Top: Integer;
  RInstall, RRemove: TNewRadioButton;
  Lbl: TNewStaticText;
begin
  SuiteModePage := CreateCustomPage(wpWelcome, 'Setup mode', 'Install new applications or remove ones you no longer need.');
  Top := 0;
  RInstall := TNewRadioButton.Create(SuiteModePage);
  RInstall.Parent := SuiteModePage.Surface;
  RInstall.Caption := 'Install or add applications';
  RInstall.Top := Top;
  RInstall.Width := SuiteModePage.SurfaceWidth;
  RInstall.Tag := 0;
  RInstall.Checked := True;
  RInstall.OnClick := @SuiteModePageClick;
  Top := Top + RInstall.Height + ScaleY(8);

  RRemove := TNewRadioButton.Create(SuiteModePage);
  RRemove.Parent := SuiteModePage.Surface;
  RRemove.Caption := 'Remove applications';
  RRemove.Top := Top;
  RRemove.Width := SuiteModePage.SurfaceWidth;
  RRemove.Tag := 1;
  RRemove.OnClick := @SuiteModePageClick;
  if not AnySuiteAppInstalled then
    RRemove.Enabled := False;

  RemoveSelectPage := CreateCustomPage(SuiteModePage.ID, 'Remove applications', 'Select the applications to remove. Shared data (company files, projects, assistant model) is kept.');
  RemoveList := TNewCheckListBox.Create(RemoveSelectPage);
  RemoveList.Parent := RemoveSelectPage.Surface;
  RemoveList.Top := 0;
  RemoveList.Width := RemoveSelectPage.SurfaceWidth;
  RemoveList.Height := ScaleY(140);
  RemoveList.AddCheckBox('MynaHub (office and suite hub)', '', 0, IsStudioInstalled, True, True, False, nil);
  RemoveList.AddCheckBox('MynaOps (quantities and billing)', '', 0, IsCoreOpInstalled, True, True, False, nil);
  RemoveList.AddCheckBox('MynaCAD (drafting and take-off)', '', 0, IsDraftInstalled, True, True, False, nil);

  RemoveConfirmPage := CreateCustomPage(RemoveSelectPage.ID, 'Confirm removal', 'Review your choices before anything is deleted.');
  Lbl := TNewStaticText.Create(RemoveConfirmPage);
  Lbl.Parent := RemoveConfirmPage.Surface;
  Lbl.AutoSize := False;
  Lbl.WordWrap := True;
  Lbl.Width := RemoveConfirmPage.SurfaceWidth;
  Lbl.Height := ScaleY(120);
  Lbl.Caption :=
    'The selected applications will be uninstalled. ' +
    'The Olmo assistant model, company files, and project files are not removed. ' +
    'If you remove MynaHub while MynaOps or MynaCAD stay installed, those apps cannot sign in to a company or reach the shared database until you install MynaHub again.';

  RemoveSummaryPage := CreateCustomPage(RemoveConfirmPage.ID, 'Removal complete', 'Summary of what was removed.');
  RemoveSummaryMemo := TNewStaticText.Create(RemoveSummaryPage);
  RemoveSummaryMemo.Parent := RemoveSummaryPage.Surface;
  RemoveSummaryMemo.AutoSize := False;
  RemoveSummaryMemo.WordWrap := True;
  RemoveSummaryMemo.Top := 0;
  RemoveSummaryMemo.Width := RemoveSummaryPage.SurfaceWidth;
  RemoveSummaryMemo.Height := ScaleY(200);
  RemoveSummaryMemo.Caption := '';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if not SuiteModeInstall then
  begin
    if (PageID = wpSelectComponents) or (PageID = wpSelectTasks) or (PageID = wpReady) or (PageID = wpFinished) then
      Result := True;
  end
  else
  begin
    if (PageID = RemoveSelectPage.ID) or (PageID = RemoveConfirmPage.ID) or (PageID = RemoveSummaryPage.ID) then
      Result := True;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ModeParam: String;
begin
  Result := True;

  ModeParam := LowerCase(ParamValue('mode'));
  if (CurPageID = SuiteModePage.ID) and (not SuiteModeInstall) then
  begin
    DeselectAllInstallComponents;
  end;

  if CurPageID = wpSelectComponents then
    EnsureInstallDependencies;

  if (CurPageID = RemoveSelectPage.ID) and (not SuiteModeInstall) then
  begin
    InitRemoveFlagsFromList;
    if CountRemoveSelected = 0 then
    begin
      MsgBox('Select at least one application to remove, or switch back to Install mode.', mbError, MB_OK);
      Result := False;
    end
    else if not ResolveStudioDependents then
      Result := False;
  end;

  if (CurPageID = RemoveConfirmPage.ID) and (not SuiteModeInstall) then
  begin
    InitRemoveFlagsFromList;
    if MsgBox('Remove these applications now?' + #13#10#13#10 + RemoveSelectionText + #13#10 +
      'Other applications, company files, projects and the assistant model are kept.', mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
    if Assigned(WizardForm) then
      WizardForm.ProgressGauge.Style := npbstNormal;
    RunSelectedRemovals(RemoveSummaryText);
  end;

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
          SuppressibleMsgBox(AddPeriod(GetExceptionMessage) + #13#10 +
            'The assistant will not work until {#ModelFile} is in ' + ExtractFileDir(ModelPath) + '.', mbError, MB_OK, IDOK);
      end;
    finally
      ModelPage.Hide;
    end;
  end;

  if ModeParam = 'uninstall' then
    ; { silent remove handled in InitializeSetup }
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Memo: String;
begin
  if (CurPageID = RemoveSummaryPage.ID) and (not SuiteModeInstall) then
  begin
    Memo := RemoveSummaryText;
    if Memo = '' then
      Memo := 'No applications were removed.';
    RemoveSummaryMemo.Caption := Memo;
  end;

  if (CurPageID = wpFinished) and (not SuiteModeInstall) then
    WizardForm.FinishedLabel.Caption := RemoveSummaryText;

  if (CurPageID = wpFinished) and SuiteModeInstall then
  begin
    Memo := 'Installation finished.' + #13#10#13#10;
    if WizardIsComponentSelected('studio') then Memo := Memo + '- MynaHub' + #13#10;
    if WizardIsComponentSelected('coreop') then Memo := Memo + '- MynaOps' + #13#10;
    if WizardIsComponentSelected('draft') then Memo := Memo + '- MynaCAD' + #13#10;
#ifdef HasForm
    if WizardIsComponentSelected('form') then Memo := Memo + '- MynaMod' + #13#10;
#endif
#ifdef HasDynamo
    if WizardIsComponentSelected('dynamo') then Memo := Memo + '- O-Dynamo' + #13#10;
#endif
    Memo := Memo + #13#10 + 'Open MynaHub to sign in to your company, then start MynaOps and MynaCAD from it. Myna runs in the tray and hosts the assistant and the database service.';
    WizardForm.FinishedLabel.Caption := Memo;
  end;
end;

function InitializeSetup(): Boolean;
var
  ModeParam, RemoveParam: String;
begin
  ModeParam := LowerCase(ParamValue('mode'));
  RemoveParam := LowerCase(ParamValue('remove'));
  SuiteModeInstall := True;

  if (ModeParam = 'uninstall') or (ModeParam = 'remove') then
  begin
    SuiteModeInstall := False;
    if IsSilentSetup then
    begin
      if not ParseRemoveList(RemoveParam) then
      begin
        Result := False;
        Exit;
      end;
      if CountRemoveSelected = 0 then
      begin
        { default silent remove: everything installed }
        RemoveStudio := IsStudioInstalled;
        RemoveCoreOp := IsCoreOpInstalled;
        RemoveDraft := IsDraftInstalled;
      end;
      if not RunSelectedRemovals(RemoveSummaryText) then
        Log(RemoveSummaryText);
      Result := False;
      Exit;
    end;
  end;

  Result := True;
end;

procedure InitializeWizard;
begin
  ModelPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  CreateSuiteWizardPages;

  if LowerCase(ParamValue('mode')) = 'uninstall' then
  begin
    SuiteModeInstall := False;
    DeselectAllInstallComponents;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if UninstallSilent then
    Exit;
  if MsgBox(
    'This removes every suite application that was installed through Myna Setup (respecting the components originally chosen).' + #13#10#13#10 +
    'To remove only some applications, run Myna-Setup.exe again and choose "Remove applications".' + #13#10#13#10 +
    'Company files, projects, and the assistant model are not deleted.' + #13#10#13#10 +
    'Continue?',
    mbConfirmation, MB_YESNO) = IDNO then
    Result := False;
end;
