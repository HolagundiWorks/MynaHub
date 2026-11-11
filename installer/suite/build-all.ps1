#Requires -Version 5.1
<#
.SYNOPSIS
  Builds the two Myna suite installers from current source: MynaHub-Setup (MynaHub) and MynaOpsCAD-Setup (MynaOps + MynaCAD together).

.DESCRIPTION
  Two installers, each complete on its own (per user, no administrator):

    MynaHub-Setup-<Version>.exe     MynaHub (NSIS, built by installer\build.ps1; removes the Myna launcher of a 1.7.x install)
    MynaOpsCAD-Setup-<Version>.exe  MynaOps and MynaCAD together (Inno Setup: O-COREOP\BBSDesktop\installer\O-CoreOp.iss in its combined mode,
                                    with MynaCAD's portable layout from MynaCAD\packaging\windows\package.ps1)

  MynaOps and MynaCAD depend on each other and share the project file, so they are one installer and are removed together; MynaOps also hosts
  the AI agent for both. MynaHub is a separate installer. The Myna launcher was removed on 2026-11-11. There is no installer for MynaOps or
  MynaCAD alone, and no combined suite installer.

  Expected sibling repos (names are fixed):
    O-STUDIO     (MynaHub; this script)
    O-COREOP     (MynaOps; the combined Inno script)
    MynaCAD (MynaCAD; the working tree is packaged: commit or stash edits you do not want in the build)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\suite\build-all.ps1 -Version 1.7.1
#>
[CmdletBinding()]
param(
    [string] $Version = "1.7.0"
)

$ErrorActionPreference = "Stop"
$suiteDir = $PSScriptRoot
$installerDir = Split-Path $suiteDir -Parent
$studioRepo = Split-Path $installerDir -Parent
$reposRoot = Split-Path $studioRepo -Parent
$coreOpRepo = Join-Path $reposRoot "O-COREOP"
$draftRepo = Join-Path $reposRoot "MynaCAD"
$outDir = Join-Path $suiteDir "out"

function Import-VcVars64 {
    $vcvars = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $vcvars) { throw "vcvars64.bat not found (install VS 2022 Build Tools with C++)." }
    cmd /c "`"$vcvars`" && set" | ForEach-Object {
        if ($_ -match '^(?<k>[^=]+?)=(?<v>.*)$') { Set-Item -Path "env:$($matches.k)" -Value $matches.v }
    }
}

function Get-RepoHead([string] $Path) {
    if (-not (Test-Path (Join-Path $Path ".git"))) { return "no-git" }
    Push-Location $Path
    try {
        $b = (git rev-parse --abbrev-ref HEAD 2>$null)
        $h = (git rev-parse --short HEAD 2>$null)
        $dirty = if ((git status --porcelain 2>$null)) { "+dirty" } else { "" }
        return "$b@$h$dirty"
    } finally { Pop-Location }
}

function Assert-Repo([string] $Path, [string] $Name) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "$Name repo not found at $Path" }
}

function Find-Iscc {
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found." }
    return $iscc
}

Assert-Repo $studioRepo "O-STUDIO"
Assert-Repo $coreOpRepo "O-COREOP"
Assert-Repo $draftRepo "MynaCAD"

$env:Path = "$env:LOCALAPPDATA\tauri\NSIS;$env:Path"
Import-VcVars64
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$manifest = [ordered]@{
    built_utc  = (Get-Date).ToUniversalTime().ToString("o")
    version    = $Version
    mynahub    = Get-RepoHead $studioRepo
    mynaops    = Get-RepoHead $coreOpRepo
    mynacad    = Get-RepoHead $draftRepo
    installers = [ordered]@{}
}

function Publish-Installer([string] $Source, [string] $Name, [string] $Key) {
    $target = Join-Path $outDir $Name
    Copy-Item -LiteralPath $Source -Destination $target -Force
    $manifest.installers[$Key] = @{ file = $Name; sha256 = (Get-FileHash $target -Algorithm SHA256).Hash }
    Write-Host "  -> $target" -ForegroundColor Green
}

Write-Host "=== 1/2 MynaHub Setup ===" -ForegroundColor Cyan
Push-Location $studioRepo
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $installerDir "build.ps1")
if ($LASTEXITCODE -ne 0) { throw "MynaHub Setup build failed (exit $LASTEXITCODE)" }
Pop-Location
$hubSetup = Get-ChildItem (Join-Path $installerDir "O-Studio-Setup-*.exe") | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $hubSetup) { throw "O-Studio-Setup-*.exe not found after the MynaHub build" }
# The staged NSIS output keeps its internal name (O-Studio-Setup); the published file is MynaHub-Setup.
Publish-Installer $hubSetup.FullName "MynaHub-Setup-$Version.exe" "mynahub"

Write-Host "=== 2/2 MynaOps + MynaCAD Setup ===" -ForegroundColor Cyan
# MynaCAD's portable layout first: it goes inside the combined Setup
if ($env:AAD_FONTS_DIR -and -not (Test-Path -LiteralPath $env:AAD_FONTS_DIR)) { Remove-Item Env:AAD_FONTS_DIR -ErrorAction SilentlyContinue }
Push-Location (Join-Path $draftRepo "packaging\windows")
& powershell -NoProfile -ExecutionPolicy Bypass -File package.ps1 -Arch x64
if ($LASTEXITCODE -ne 0) { throw "MynaCAD package build failed (exit $LASTEXITCODE)" }
Pop-Location
$draftZip = Get-ChildItem (Join-Path $draftRepo "dist\release\o-draft-*-windows-x64-portable.zip") | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $draftZip) { throw "the MynaCAD portable zip was not produced" }
$stage = Join-Path $suiteDir "stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$app = Join-Path $stage "app"
New-Item -ItemType Directory -Path $app -Force | Out-Null
Expand-Archive -LiteralPath $draftZip.FullName -DestinationPath $app -Force
# the zip may hold one top-level folder; flatten it so the exe sits in the root
$top = Get-ChildItem $app
if ($top.Count -eq 1 -and $top[0].PSIsContainer) { Get-ChildItem $top[0].FullName | Move-Item -Destination $app; Remove-Item $top[0].FullName -Recurse }
if (-not (Test-Path (Join-Path $app "ODraft.WinUi.exe"))) { throw "ODraft.WinUi.exe is not in $($draftZip.FullName)" }
Push-Location (Join-Path $coreOpRepo "BBSDesktop\installer")
& powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1 -CadStage $stage
if ($LASTEXITCODE -ne 0) { throw "MynaOps + MynaCAD Setup build failed (exit $LASTEXITCODE); not publishing a stale installer" }
Pop-Location
$opsSetup = Get-ChildItem (Join-Path $coreOpRepo "BBSDesktop\artifacts\installer\MynaOpsCAD-Setup-*.exe") | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $opsSetup) { throw "MynaOpsCAD-Setup-*.exe not found after the build" }
Publish-Installer $opsSetup.FullName "MynaOpsCAD-Setup-$Version.exe" "mynaopscad"
Remove-Item $stage -Recurse -Force

$manifestPath = Join-Path $outDir "Myna-installers-$Version.manifest.json"
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "Manifest: $manifestPath" -ForegroundColor Green
Get-ChildItem $outDir -Filter "*$Version*" | ForEach-Object { Write-Host ("  {0}  {1:N0} MB" -f $_.Name, ($_.Length / 1MB)) -ForegroundColor Green }
