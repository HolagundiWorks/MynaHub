# Builds OSAS-Setup-<version>.exe: the launcher published self-contained, then NSIS.
# Run from the repository root:  powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-OfficeDllDir <folder with office.dll>]
# hcw-ui (the shared design) must be checked out beside this repository; office.dll comes from the O-Studio repository (office-ffi, release).
param([string]$Version = "0.3.0", [string]$OfficeDllDir = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $PSScriptRoot "stage"
$makensis = @("$env:LOCALAPPDATA\tauri\NSIS\makensis.exe", "${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $makensis) { $cmd = Get-Command makensis -ErrorAction SilentlyContinue; if ($cmd) { $makensis = $cmd.Source } }
if (-not $makensis) { throw "makensis.exe was not found. Install NSIS." }

Write-Host "1/2 Publishing"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
$publish = @("publish", (Join-Path $root "Osas.Launcher\Osas.Launcher.csproj"), "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:Platform=x64", "-p:Version=$Version")
if ($OfficeDllDir) { $publish += "-p:OfficeDllDir=$OfficeDllDir" }
$publish += @("-o", $stage)
dotnet @publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if (-not (Test-Path (Join-Path $stage "office.dll"))) { throw "office.dll was not copied beside OSAS. Build office-ffi in the O-Studio repository in release mode, or pass -OfficeDllDir." }

Write-Host "2/2 Installer"
$out = Join-Path $PSScriptRoot "OSAS-Setup-$Version.exe"
& $makensis "/DVERSION=$Version" "/DSOURCE=$stage" "/DOUT=$out" (Join-Path $PSScriptRoot "osas.nsi")
if ($LASTEXITCODE -ne 0) { throw "makensis failed" }
Write-Host "Built $out"
Get-FileHash $out -Algorithm SHA256 | Format-List Hash
