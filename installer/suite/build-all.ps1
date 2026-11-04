#Requires -Version 5.1
<#
.SYNOPSIS
  Builds fresh app installers, then O-Suite-Setup-<Version>.exe, with a manifest of git commits.

.DESCRIPTION
  Use this instead of build-single.ps1 alone when the suite must match current source.
  build-single.ps1 only wraps whatever Setup.exe / zip files already exist (often stale).

  Expected sibling repos (names are fixed):
    O-STUDIO   branch feat/launcher-services (hub + suite iss)
    O-COREOP   branch feat/project-selection-step3
    O-DRAFT-main on main (only committed work is packaged; uncommitted edits are not included)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\suite\build-all.ps1 -Version 1.3.1
#>
[CmdletBinding()]
param(
    [string] $Version = "1.3.1"
)

$ErrorActionPreference = "Stop"
$suiteDir = $PSScriptRoot
$installerDir = Split-Path $suiteDir -Parent
$studioRepo = Split-Path $installerDir -Parent
$reposRoot = Split-Path $studioRepo -Parent
$coreOpRepo = Join-Path $reposRoot "O-COREOP"
$draftRepo = Join-Path $reposRoot "O-DRAFT-main"
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

Assert-Repo $studioRepo "O-STUDIO"
Assert-Repo $coreOpRepo "O-COREOP"
Assert-Repo $draftRepo "O-DRAFT"

$env:Path = "$env:LOCALAPPDATA\tauri\NSIS;$env:Path"
Import-VcVars64

$manifest = [ordered]@{
    built_utc   = (Get-Date).ToUniversalTime().ToString("o")
    suite_version = $Version
    o_studio    = Get-RepoHead $studioRepo
    o_coreop    = Get-RepoHead $coreOpRepo
    o_draft     = Get-RepoHead $draftRepo
    payloads    = @{}
}

Write-Host "=== 1/5 O-Studio NSIS ===" -ForegroundColor Cyan
Push-Location $studioRepo
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $installerDir "build.ps1")
Pop-Location
$studioSetup = Get-ChildItem (Join-Path $installerDir "O-Studio-Setup-*.exe") | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$manifest.payloads.o_studio_setup = @{ path = $studioSetup.FullName; sha256 = (Get-FileHash $studioSetup.FullName -Algorithm SHA256).Hash }

Write-Host "=== 2/5 office-ffi (office.dll) ===" -ForegroundColor Cyan
$savedTarget = $env:CARGO_TARGET_DIR
if (-not $savedTarget) { $env:CARGO_TARGET_DIR = "C:\Temp\odraft-target" }
Push-Location (Join-Path $studioRepo "office-ffi")
cargo build --release
Pop-Location
$officeDir = Join-Path $env:CARGO_TARGET_DIR "release"
if (-not (Test-Path (Join-Path $officeDir "office.dll"))) { throw "office.dll missing in $officeDir" }

Write-Host "=== 3/5 O-CoreOp Setup ===" -ForegroundColor Cyan
Push-Location (Join-Path $coreOpRepo "BBSDesktop\installer")
& powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1 -OfficeDllDir $officeDir
Pop-Location
$coreOpSetup = Get-ChildItem (Join-Path $coreOpRepo "BBSDesktop\artifacts\installer\O-CoreOp-Setup-*.exe") | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$manifest.payloads.o_coreop_setup = @{ path = $coreOpSetup.FullName; sha256 = (Get-FileHash $coreOpSetup.FullName -Algorithm SHA256).Hash }

Write-Host "=== 4/5 O-Draft portable zip ===" -ForegroundColor Cyan
if ($savedTarget) { $env:CARGO_TARGET_DIR = $savedTarget } else { Remove-Item Env:CARGO_TARGET_DIR -ErrorAction SilentlyContinue }
if ($env:AAD_FONTS_DIR -and -not (Test-Path -LiteralPath $env:AAD_FONTS_DIR)) { Remove-Item Env:AAD_FONTS_DIR -ErrorAction SilentlyContinue }
Push-Location (Join-Path $draftRepo "packaging\windows")
& powershell -NoProfile -ExecutionPolicy Bypass -File package.ps1 -Arch x64
Pop-Location
$draftZip = Get-ChildItem (Join-Path $draftRepo "dist\release\o-draft-*-windows-x64-portable.zip") | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$manifest.payloads.o_draft_zip = @{ path = $draftZip.FullName; sha256 = (Get-FileHash $draftZip.FullName -Algorithm SHA256).Hash }

Write-Host "=== 5/5 O-Suite unified Setup ===" -ForegroundColor Cyan
& (Join-Path $suiteDir "build-single.ps1") -Version $Version `
    -StudioSetup $studioSetup.FullName `
    -CoreOpSetup $coreOpSetup.FullName `
    -DraftZip $draftZip.FullName

$suiteExe = Join-Path $outDir "O-Suite-Setup-$Version.exe"
$manifest.suite_sha256 = (Get-FileHash $suiteExe -Algorithm SHA256).Hash
$manifestPath = Join-Path $outDir "O-Suite-Setup-$Version.manifest.json"
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "Manifest: $manifestPath" -ForegroundColor Green
Write-Host "Setup:    $suiteExe" -ForegroundColor Green
