#Requires -Version 5.1
# Continues suite build after MynaHub + office-ffi (needs MSVC from vcvars64 and NSIS on PATH).
$ErrorActionPreference = 'Stop'
$env:Path = "$env:LOCALAPPDATA\tauri\NSIS;$env:Path"
$vcvars = @(
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $vcvars) { throw 'vcvars64.bat not found. Install VS 2022 Build Tools with C++ workload.' }
cmd /c "`"$vcvars`" && set" | ForEach-Object {
    if ($_ -match '^(?<k>[^=]+?)=(?<v>.*)$') {
        Set-Item -Path "env:$($matches.k)" -Value $matches.v
    }
}
$studio = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$coreop = Join-Path (Split-Path $studio -Parent) 'O-COREOP'
$draft = Join-Path (Split-Path $studio -Parent) 'O-DRAFT-main'
$officeDll = if ($env:CARGO_TARGET_DIR) {
    Join-Path $env:CARGO_TARGET_DIR 'release'
} else {
    Join-Path $studio 'office-ffi\target\release'
}
$ver = '1.3.0'
& (Join-Path $coreop 'BBSDesktop\installer\build-installer.ps1')
$savedTarget = $env:CARGO_TARGET_DIR
Remove-Item Env:CARGO_TARGET_DIR -ErrorAction SilentlyContinue
try {
    & (Join-Path $draft 'packaging\windows\package.ps1') -Arch x64
} finally {
    if ($savedTarget) { $env:CARGO_TARGET_DIR = $savedTarget }
}
Set-Location $PSScriptRoot
& (Join-Path $PSScriptRoot 'build-single.ps1') -Version $ver
Write-Host "Built $(Join-Path $PSScriptRoot "out\Myna-Setup-$ver.exe")"
