<#
.SYNOPSIS
  Installs OSAS (the suite launcher) for the current user, or removes it.
.DESCRIPTION
  Publishes the launcher self-contained (no .NET or Windows App SDK install needed), copies it to
  %LOCALAPPDATA%\Programs\OSAS, adds a Start menu shortcut, and registers it in Settings > Apps so it can be uninstalled.
  Nothing outside the current user's folders is changed and no administrator rights are needed.
  Run from the repository root:   powershell -ExecutionPolicy Bypass -File installer\install.ps1
  Remove it:                      powershell -ExecutionPolicy Bypass -File installer\install.ps1 -Uninstall
  The sibling folder hcw-ui (the shared design) must be checked out beside this repository.
#>
param([switch]$Uninstall, [string]$Version = "0.2.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA "Programs\OSAS"
$shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "OSAS.lnk"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OSAS"

if ($Uninstall) {
    Get-Process OSAS -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target\*" } | Stop-Process -Force
    if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
    if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse -Force }
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    "OSAS was removed. Your saved choices in $env:LOCALAPPDATA\OSAS are kept."
    return
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("osas-publish-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
try {
    Write-Host "1/3 Publishing"
    dotnet publish (Join-Path $root "Osas.Launcher\Osas.Launcher.csproj") -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:Version=$Version -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    if (-not (Test-Path (Join-Path $stage "OSAS.exe"))) { throw "OSAS.exe was not produced." }

    Write-Host "2/3 Installing to $target"
    Get-Process OSAS -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target\*" } | Stop-Process -Force
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target | Out-Null
    Copy-Item (Join-Path $stage "*") $target -Recurse -Force
    Copy-Item $PSCommandPath (Join-Path $target "install.ps1") -Force

    Write-Host "3/3 Shortcut and Settings > Apps entry"
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = Join-Path $target "OSAS.exe"
    $link.WorkingDirectory = $target
    $link.Description = "OSAS, the Open Source Architecture Suit"
    $link.Save()
    New-Item -Path $uninstallKey -Force | Out-Null
    $size = [int]((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    $entries = @{
        DisplayName = "OSAS"; DisplayVersion = $Version; Publisher = "Human Centric Works"; InstallLocation = $target
        DisplayIcon = (Join-Path $target "OSAS.exe"); EstimatedSize = $size; NoModify = 1; NoRepair = 1
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $target 'install.ps1')`" -Uninstall"
    }
    foreach ($name in $entries.Keys) {
        $value = $entries[$name]
        $type = if ($value -is [int]) { "DWord" } else { "String" }
        New-ItemProperty -Path $uninstallKey -Name $name -Value $value -PropertyType $type -Force | Out-Null
    }
    "Installed OSAS $Version to $target"
}
finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
}
