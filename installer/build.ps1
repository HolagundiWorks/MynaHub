# Builds the Windows installer for the WinUI app: the Rust engine in release mode, the app published self-contained, then NSIS.
# Run from the repository root:  powershell -ExecutionPolicy Bypass -File installer\build.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$version = "0.1.0"
$stage = Join-Path $PSScriptRoot "stage"
$makensis = @("$env:ProgramFiles (x86)\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $makensis) { $cmd = Get-Command makensis -ErrorAction SilentlyContinue; if ($cmd) { $makensis = $cmd.Source } }
if (-not $makensis) { throw "makensis.exe was not found. Install NSIS and ensure makensis.exe is on PATH." }

# The engine is built into src-tauri\target unless CARGO_TARGET_DIR names another folder (useful when the repository drive is short of space).
$engineDir = if ($env:CARGO_TARGET_DIR) { Join-Path $env:CARGO_TARGET_DIR "release" } else { Join-Path $root "src-tauri\target\release" }
Write-Host "1/3 Rust engine (release)"
cargo build --release --manifest-path src-tauri\Cargo.toml
if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }

Write-Host "2/3 Publishing the app"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
dotnet publish native\Adms.Desktop\Adms.Desktop.csproj -c Release -r win-x64 --self-contained true -p:EngineProfile=release "-p:EngineDll=$engineDir\architect_office_desktop_lib.dll" -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if (-not (Test-Path (Join-Path $stage "architect_office_desktop_lib.dll"))) { throw "The engine DLL was not copied beside the app." }

# Launcher is a full self-contained publish under launcher\ so its resources.pri does not overwrite O-Studio.exe's (BUG-0033 / 0xc000027b).
Write-Host "2b/3 Publishing launcher to launcher\ (isolated WinUI payload)"
$launcherDir = Join-Path $stage "launcher"
dotnet publish native\Adms.LoginWidget\Adms.LoginWidget.csproj -c Release -r win-x64 `
    --self-contained true `
    -o $launcherDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (launcher) failed" }
if (-not (Test-Path (Join-Path $launcherDir "O-Studio.Launcher.exe"))) { throw "O-Studio.Launcher.exe was not published." }

Write-Host "3/3 Installer"
$out = Join-Path $PSScriptRoot "O-Studio-Setup-$version.exe"
& $makensis "/DVERSION=$version" "/DSOURCE=$stage" "/DOUT=$out" (Join-Path $PSScriptRoot "aos.nsi")
if ($LASTEXITCODE -ne 0) { throw "makensis failed" }

# Optional Authenticode — no secrets means unsigned (exit 0). See docs/packaging.md.
$signScript = Join-Path $PSScriptRoot "sign.ps1"
if (Test-Path -LiteralPath $signScript) {
    Write-Host "4/4 Signing (if material present)"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript $out
    if ($LASTEXITCODE -ne 0) { throw "sign.ps1 failed ($LASTEXITCODE)" }
}

Write-Host "Built $out"
Get-FileHash $out -Algorithm SHA256 | Format-List Hash
