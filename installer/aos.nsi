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

Name "${APP_NAME}"
OutFile "${OUT}"
InstallDir "$LOCALAPPDATA\Programs\${APP_DIR}"
InstallDirRegKey HKCU "Software\${PUBLISHER}\${APP_ID}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${PUBLISHER}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "FileDescription" "${APP_NAME} setup"
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

Section "Install"
  SetOutPath "$INSTDIR"
  ; the program was called Adms.Desktop before; remove those files so an upgrade leaves one program behind
  Delete "$INSTDIR\Adms.Desktop.*"
  ; remove a broken copy of the launcher from the install root (BUG-0019); keep launcher\ as the real payload (BUG-0033)
  Delete "$INSTDIR\O-Studio.Launcher.*"
  ; force WinUI PRI refresh on upgrade (BUG-0033: a shared-folder publish left the wrong resources.pri at the root)
  Delete "$INSTDIR\resources.pri"
  Delete "$INSTDIR\launcher\resources.pri"
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

  ; shortcuts carry the application icon, so the Start menu, the desktop and the taskbar all show it
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\Assets\app.ico" 0
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\Assets\app.ico" 0
  ; Myna: hosts the assistant and the database service, and chooses the project. Company sign-in is in MynaHub.
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\Myna.lnk" "$INSTDIR\launcher\O-Studio.Launcher.exe" "" "$INSTDIR\launcher\O-Studio.Launcher.exe" 0
  CreateShortcut "$DESKTOP\Myna.lnk" "$INSTDIR\launcher\O-Studio.Launcher.exe" "" "$INSTDIR\launcher\O-Studio.Launcher.exe" 0

  ; the entry in Settings > Apps
  WriteRegStr HKCU "Software\${PUBLISHER}\${APP_ID}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
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
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Myna.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$DESKTOP\Myna.lnk"
  ; the install folder is fixed and holds only the program, never company files; it is removed only if it is the app's own folder
  IfFileExists "$INSTDIR\${EXE}" 0 +2
    RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "Software\${PUBLISHER}\${APP_ID}"
SectionEnd
