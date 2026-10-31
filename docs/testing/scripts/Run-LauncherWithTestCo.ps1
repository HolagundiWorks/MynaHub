# Launcher smoke with seeded TestCo (docs/testing/TEST-ENVIRONMENT.md).
param(
    [string]$TestRoot = "D:\Work Development\TestRuns\2026-10-31"
)

$launcherDir = "D:\Work Development\Repos\O-STUDIO\native\Adms.LoginWidget\bin\Release\net8.0-windows10.0.19041.0\win-x64"
$exe = Join-Path $launcherDir "O-Studio.Launcher.exe"
$company = Get-ChildItem -Path $TestRoot -Filter "TestCo-*.osas" | Select-Object -First 1
if (-not $company) { throw "Run: dotnet run --project native/Adms.TestSeed -c Release" }

Write-Host "Company:" $company.Name
$suite = Get-Content "$env:LOCALAPPDATA\O-Studio\suite.json" -Raw | ConvertFrom-Json
Write-Host "suite.json:" $suite.company_path $suite.username

Get-Process "O-Studio.Launcher" -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 1
$p = Start-Process $exe -WorkingDirectory $launcherDir -PassThru
Start-Sleep 12
Write-Host "Launcher alive:" (-not $p.HasExited)
Write-Host "--- launcher-status.txt ---"
Get-Content "$env:LOCALAPPDATA\O-Studio\launcher-status.txt"
Get-Process "O-Studio.Launcher" -EA SilentlyContinue | Stop-Process -Force
