#Requires -Version 5.1
<#
.SYNOPSIS
  Builds O-Suite-Setup-<version>.exe, one Setup for every suite app, from the apps' finished installers and packages.

.DESCRIPTION
  Does not build the apps: use **`build-all.ps1`** when the suite must match current source. This script only wraps existing Setup.exe / zip files (whichever is newest on disk), which is often stale.

    O-Studio  : its NSIS Setup (run with /S)
    O-CoreOp  : its Inno Setup (run with /VERYSILENT; the model task is passed on)
    O-Draft   : the portable zip (ODraft.WinUi.exe, o-draft.exe, aad-cli.exe), unpacked into %LocalAppData%\Programs\O-Draft
    O-Form / O-Dynamo : only when -FormSetup / -DynamoSetup name a finished Setup; they get a checkbox then, and none otherwise

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\suite\build-single.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string] $Version = "1.0.0",
    [string] $StudioSetup = "",
    [string] $CoreOpSetup = "",
    [string] $DraftZip = "",
    [string] $ToolsDir = "",
    [string] $FormSetup = "",
    [string] $DynamoSetup = "",
    [string] $LicenseFile = ""
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$installer = Split-Path $here -Parent
$repos = Split-Path (Split-Path $installer -Parent) -Parent
$dist = Join-Path $installer "suite-dist"
$studioRepo = Split-Path $installer -Parent
if (-not $ToolsDir) { $ToolsDir = Join-Path $installer "suite-tools" }
if (-not $StudioSetup) { $StudioSetup = (Get-ChildItem @($installer, $dist) -Filter "O-Studio-Setup-*.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
if (-not $CoreOpSetup) {
    $fresh = Join-Path $repos "O-COREOP\BBSDesktop\artifacts\installer"
    $CoreOpSetup = (Get-ChildItem @($fresh, $dist) -Filter "O-CoreOp-Setup-*.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $DraftZip) { $DraftZip = (Get-ChildItem @((Join-Path $repos "O-DRAFT-main\dist\release"), $dist) -Filter "o-draft-*-windows-x64-portable.zip" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
if (-not $LicenseFile) { $LicenseFile = Join-Path $repos "O-COREOP\LICENSE" }

function Need([string] $Path, [string] $What) { if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { throw "$What not found ($Path). Build it first, or pass its path." } }
Need $StudioSetup "O-Studio Setup"; Need $CoreOpSetup "O-CoreOp Setup"; Need $DraftZip "O-Draft portable zip"; Need $LicenseFile "licence file"

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found." }

$stage = Join-Path $here "stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item $StudioSetup (Join-Path $stage "O-Studio-Setup.exe")
Copy-Item $CoreOpSetup (Join-Path $stage "O-CoreOp-Setup.exe")
Copy-Item $LicenseFile (Join-Path $stage "LICENSE")
$defines = @("/DSuiteVersion=$Version", "/DStage=$stage")
if ($FormSetup) { Need $FormSetup "O-Form Setup"; Copy-Item $FormSetup (Join-Path $stage "O-Form-Setup.exe"); $defines += "/DHasForm" }
if ($DynamoSetup) { Need $DynamoSetup "O-Dynamo Setup"; Copy-Item $DynamoSetup (Join-Path $stage "O-Dynamo-Setup.exe"); $defines += "/DHasDynamo" }

Write-Host "O-Draft : $DraftZip"
$draft = Join-Path $stage "draft"
Expand-Archive -LiteralPath $DraftZip -DestinationPath $draft -Force
# the zip may hold one top-level folder; flatten it so the exe sits in the root
$top = Get-ChildItem $draft
if ($top.Count -eq 1 -and $top[0].PSIsContainer) { Get-ChildItem $top[0].FullName | Move-Item -Destination $draft; Remove-Item $top[0].FullName -Recurse }
if (-not (Test-Path (Join-Path $draft "ODraft.WinUi.exe"))) { throw "ODraft.WinUi.exe is not in $DraftZip" }

Write-Host "Compiling ..." -ForegroundColor Yellow
& $iscc @defines (Join-Path $here "O-Suite.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
$out = Join-Path $here "out\O-Suite-Setup-$Version.exe"
Get-Item $out | Format-List FullName, Length, LastWriteTime
Get-FileHash $out -Algorithm SHA256 | Format-List Hash
