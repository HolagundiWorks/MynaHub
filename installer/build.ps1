# Builds the Windows installer for the WinUI app: the Rust engine in release mode, the app published self-contained, then NSIS.
# Run from the repository root:  powershell -ExecutionPolicy Bypass -File installer\build.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$version = "0.1.0"
$stage = Join-Path $PSScriptRoot "stage"
$makensis = @("$env:LOCALAPPDATA\tauri\NSIS\makensis.exe", "$env:ProgramFiles (x86)\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $makensis) { $cmd = Get-Command makensis -ErrorAction SilentlyContinue; if ($cmd) { $makensis = $cmd.Source } }
if (-not $makensis) { throw "makensis.exe was not found. Install NSIS, or build once with 'npm run tauri build' so Tauri downloads it." }

Write-Host "1/3 Rust engine (release)"
cargo build --release --manifest-path src-tauri\Cargo.toml
if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }

Write-Host "2/3 Publishing the app"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
dotnet publish native\Adms.Desktop\Adms.Desktop.csproj -c Release -r win-x64 --self-contained true -p:EngineProfile=release -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if (-not (Test-Path (Join-Path $stage "architect_office_desktop_lib.dll"))) { throw "The engine DLL was not copied beside the app." }

Write-Host "3/3 Installer"
$out = Join-Path $PSScriptRoot "AOS-Setup-$version.exe"
& $makensis "/DVERSION=$version" "/DSOURCE=$stage" "/DOUT=$out" (Join-Path $PSScriptRoot "aos.nsi")
if ($LASTEXITCODE -ne 0) { throw "makensis failed" }
Write-Host "Built $out"
Get-FileHash $out -Algorithm SHA256 | Format-List Hash
