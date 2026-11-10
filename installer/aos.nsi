; Installer for MynaHub (the WinUI app and its Rust engine).
; Build with installer\build.ps1, or:  makensis /DSOURCE=<publish folder> installer\aos.nsi
;
; Installs for the current user only (no administrator rights), into a fixed folder, so the uninstaller can remove exactly what
; was installed and nothing else. Company files (.osas, and earlier .adms) are never touched: they live wherever the user saved them.
Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"
!insertmacro GetSize

!define APP_NAME "MynaHub"
; The Setup, the Start menu folder and the Apps entry are called MynaHub. The Myna launcher was removed on 2026-11-11: this Setup only installs
; MynaHub, and on an upgrade it removes the launcher an earlier Myna Setup installed (see RemoveOldLauncher below).
!define SETUP_NAME "MynaHub"
!define START_DIR "MynaHub"
; The install folder, the registry keys and the uninstall id keep the earlier name so an upgrade finds the old install.
!define APP_DIR "O-Studio"
!define APP_ID "OStudio"
!define PUBLISHER "Human Centric Works"
!define EXE "O-Studio.exe"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"

!ifndef VERSION
  !define VERSION "0.1.0"
!endif
!ifndef SOURCE
  !error "Pass /DSOURCE=<folder produced by dotnet publish>"
!endif
!ifndef OUT
  !define OUT "O-Studio-Setup-${VERSION}.exe"
!endif

Name "${SETUP_NAME}"
OutFile "${OUT}"
InstallDir "$LOCALAPPDATA\Programs\${APP_DIR}"
InstallDirRegKey HKCU "Software\${PUBLISHER}\${APP_ID}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${PUBLISHER}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${SETUP_NAME}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "FileDescription" "${SETUP_NAME} setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 ${PUBLISHER}"

!define MUI_ICON "${SOURCE}\Assets\app.ico"
!define MUI_UNICON "${SOURCE}\Assets\app.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${APP_NAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

; The Myna launcher (O-Studio.Launcher.exe in launcher\, Setups up to 1.7.x) no longer exists. Stop a running copy (it held the AI model and would
; keep its folder locked), then remove its folder, its shortcuts, the Start menu folder Myna when nothing else is left in it, any autostart entry
; and the two state files only it used. The AI agent is now hosted by MynaOps (MynaOpsCAD-Setup).
!macro RemoveOldLauncher
  nsExec::Exec 'taskkill /F /T /IM O-Studio.Launcher.exe'
  Pop $0
  Sleep 500
  RMDir /r "$INSTDIR\launcher"
  Delete "$INSTDIR\O-Studio.Launcher.*"
  Delete "$SMPROGRAMS\Myna\Myna.lnk"
  Delete "$SMPROGRAMS\Myna\MynaHub.lnk"
  Delete "$SMPROGRAMS\Myna\Uninstall Myna.lnk"
  ; only removed when empty: anything the user put there stays
  RMDir "$SMPROGRAMS\Myna"
  Delete "$DESKTOP\Myna.lnk"
  Delete "$SMSTARTUP\Myna.lnk"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Myna"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "O-Studio.Launcher"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "O-Suite Launcher"
  Delete "$LOCALAPPDATA\O-Studio\launch-request.json"
  Delete "$LOCALAPPDATA\O-Studio\launcher-status.txt"
!macroend

Section "Install"
  SetOutPath "$INSTDIR"
  !insertmacro RemoveOldLauncher
  ; the program was called Adms.Desktop before; remove those files so an upgrade leaves one program behind
  Delete "$INSTDIR\Adms.Desktop.*"
  ; force WinUI PRI refresh on upgrade (BUG-0033: a shared-folder publish left the wrong resources.pri at the root)
  Delete "$INSTDIR\resources.pri"
  File /r "${SOURCE}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; earlier Setups used the old names for the Start menu folder and the launcher shortcuts
  Delete "$SMPROGRAMS\O-Studio\O-Studio.lnk"
  Delete "$SMPROGRAMS\O-Studio\Uninstall O-Studio.lnk"
  Delete "$SMPROGRAMS\O-Studio\O-Suite Launcher.lnk"
  RMDir "$SMPROGRAMS\O-Studio"
  Delete "$DESKTOP\O-Studio.lnk"
  Delete "$DESKTOP\O-Suite Launcher.lnk"
  ; Shortcuts made under the name Kivo (before the Myna names)
  Delete "$SMPROGRAMS\Kivo\Kivo.lnk"
  Delete "$SMPROGRAMS\Kivo\Uninstall Kivo.lnk"
  Delete "$SMPROGRAMS\Kivo\Myna.lnk"
  RMDir "$SMPROGRAMS\Kivo"
  Delete "$DESKTOP\Kivo.lnk"
  Delete "$SMPROGRAMS\MynaHub\MynaHub.lnk"
  Delete "$SMPROGRAMS\MynaHub\Uninstall MynaHub.lnk"
  Delete "$SMPROGRAMS\MynaHub\Myna.lnk"
  RMDir "$SMPROGRAMS\MynaHub"

  ; shortcuts carry the application icon, so the Start menu, the desktop and the taskbar all show it
  CreateDirectory "$SMPROGRAMS\${START_DIR}"
  CreateShortcut "$SMPROGRAMS\${START_DIR}\${APP_NAME}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\Assets\app.ico" 0
  CreateShortcut "$SMPROGRAMS\${START_DIR}\Uninstall ${SETUP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\Assets\app.ico" 0

  ; the entry in Settings > Apps
  WriteRegStr HKCU "Software\${PUBLISHER}\${APP_ID}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${SETUP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Assets\app.ico"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\${START_DIR}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${START_DIR}\Uninstall ${SETUP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${START_DIR}"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  ; an install that was never upgraded may still have the launcher of a Myna Setup (1.7.x)
  !insertmacro RemoveOldLauncher
  ; the install folder is fixed and holds only the program, never company files; it is removed only if it is the app's own folder
  IfFileExists "$INSTDIR\${EXE}" 0 +2
    RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "Software\${PUBLISHER}\${APP_ID}"
SectionEnd
