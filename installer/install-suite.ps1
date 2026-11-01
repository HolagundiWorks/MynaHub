#Requires -Version 5.1
<#
.SYNOPSIS
  Suite glue installer: installs O-Studio (and sibling Setup.exe files when present).

.DESCRIPTION
  Migrated into O-Studio as the surviving OSAS hub (Phase 1 merge). Looks in
  -ArtifactsDir for O-Studio-Setup-*.exe (and optional O-CoreOp / O-Draft setups)
  and runs each silently for the current user. Does not create a separate hub app (OSAS.exe is retired, see docs/osas-deprecation.md).

.PARAMETER ArtifactsDir
  Folder containing suite Setup.exe files (default: this installer folder).
#>
[CmdletBinding()]
param(
    [string]$ArtifactsDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ArtifactsDir)) {
    throw "ArtifactsDir not found: $ArtifactsDir"
}

$patterns = @(
    'O-Studio-Setup-*.exe',
    'O-CoreOp-Setup-*.exe',
    'O-Draft-Setup-*.exe',
    'O-Form-Setup-*.exe'
)

$found = @()
foreach ($pattern in $patterns) {
    $found += Get-ChildItem -LiteralPath $ArtifactsDir -Filter $pattern -File -ErrorAction SilentlyContinue
}

if ($found.Count -eq 0) {
    Write-Warning "No suite Setup.exe files matched in $ArtifactsDir"
    exit 0
}

foreach ($setup in $found | Sort-Object Name -Unique) {
    Write-Host "Installing $($setup.Name)…"
    # NSIS per-user installers typically accept /S for silent.
    $proc = Start-Process -FilePath $setup.FullName -ArgumentList '/S' -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        throw "Installer failed ($($proc.ExitCode)): $($setup.Name)"
    }
}

Write-Host "Suite install finished. Open O-Studio to choose the company file and launch sibling apps."
exit 0
