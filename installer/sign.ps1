#Requires -Version 5.1
<#
.SYNOPSIS
  Authenticode-sign O-Studio Setup.exe when signing material exists.

.DESCRIPTION
  Same contract as the OSAS launcher installer\sign.ps1 — never invents a production cert.
    1. WINDOWS_CERTIFICATE (+ optional WINDOWS_CERTIFICATE_PASSWORD)
    2. Azure Trusted Signing env vars + Microsoft.Trusted.Signing.Client dlib
    3. SIGN_SELF_SIGNED=1 → %LOCALAPPDATA%\O-Studio\dev-codesign\ (SmartScreen still warns)
  Otherwise warns and exits 0 (unsigned).

.EXAMPLE
  powershell -File installer\sign.ps1 installer\O-Studio-Setup-0.1.0.exe
#>
param(
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]] $Files
)
$ErrorActionPreference = "Stop"

function Write-WarnMsg([string] $Message) {
    if ($env:GITHUB_ACTIONS) { Write-Output "::warning::$Message" } else { Write-Warning $Message }
}

function Find-SignTool {
    if ($env:SIGNTOOL) { return $env:SIGNTOOL }
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path $kits)) { throw "signtool.exe not found (install the Windows SDK)" }
    $found = Get-ChildItem -Path $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) { throw "signtool.exe not found (install the Windows SDK)" }
    return $found.FullName
}

function Invoke-SignTool([string[]] $Arguments) {
    & $script:SignTool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "signtool $($Arguments[0]) failed with exit code $LASTEXITCODE" }
}

function Assert-HasSignature([string[]] $Paths) {
    foreach ($path in $Paths) {
        $sig = Get-AuthenticodeSignature -FilePath $path
        if (-not $sig.SignerCertificate) {
            throw "no Authenticode signature on $path (status $($sig.Status))"
        }
    }
}

function Ensure-SelfSignedPfx {
    $dir = Join-Path $env:LOCALAPPDATA "O-Studio\dev-codesign"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $pfxPath = Join-Path $dir "o-studio-dev.pfx"
    $pwdFile = Join-Path $dir "pfx.password"
    if (-not (Test-Path -LiteralPath $pfxPath) -or -not (Test-Path -LiteralPath $pwdFile)) {
        Write-Output "Creating self-signed code-signing certificate at $pfxPath"
        $cert = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject "CN=O-Studio Development (self-signed)" `
            -FriendlyName "O-Studio development code signing" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -KeyExportPolicy Exportable `
            -KeySpec Signature `
            -KeyLength 2048 `
            -HashAlgorithm SHA256 `
            -NotAfter (Get-Date).AddYears(5)
        $plain = [guid]::NewGuid().ToString("N")
        $secure = ConvertTo-SecureString -String $plain -Force -AsPlainText
        Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure | Out-Null
        Set-Content -LiteralPath $pwdFile -Value $plain -NoNewline
    }
    return @{ Path = $pfxPath; Password = (Get-Content -LiteralPath $pwdFile -Raw).Trim() }
}

function Invoke-SignWithPfx([string] $Pfx, [string] $Password, [string[]] $Paths, [string] $Label) {
    Write-Output "Signing with $Label : $($Paths -join ', ')"
    $args = @("sign", "/fd", "SHA256", "/td", "SHA256", "/tr", "http://timestamp.digicert.com", "/f", $Pfx)
    if ($Password) { $args += @("/p", $Password) }
    $args += $Paths
    Invoke-SignTool $args
    Assert-HasSignature $Paths
}

foreach ($f in $Files) {
    if (-not (Test-Path -LiteralPath $f)) { throw "file not found: $f" }
}

$script:SignTool = $null
try { $script:SignTool = Find-SignTool } catch { Write-WarnMsg $_.Exception.Message }

if ($env:WINDOWS_CERTIFICATE) {
    if (-not $script:SignTool) { throw "signtool required for WINDOWS_CERTIFICATE" }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("studio-sign-" + [guid]::NewGuid().ToString("N") + ".pfx")
    try {
        [IO.File]::WriteAllBytes($tmp, [Convert]::FromBase64String($env:WINDOWS_CERTIFICATE))
        Invoke-SignWithPfx $tmp $env:WINDOWS_CERTIFICATE_PASSWORD $Files "WINDOWS_CERTIFICATE (signtool)"
    } finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
    return
}

$azureReady = $env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID -and $env:AZURE_CLIENT_SECRET -and `
    $env:AZURE_SIGNING_ACCOUNT -and $env:AZURE_CERT_PROFILE
if ($azureReady) {
    if (-not $script:SignTool) { throw "signtool required for Azure Trusted Signing" }
    $endpoint = $env:AZURE_SIGNING_ENDPOINT
    if (-not $endpoint) {
        $region = $env:AZURE_SIGNING_REGION
        if (-not $region) { throw "set AZURE_SIGNING_ENDPOINT or AZURE_SIGNING_REGION" }
        $endpoint = "https://$region.codesigning.azure.net"
    }
    $dlib = $env:AZURE_SIGNING_DLIB
    $dlibTmp = $null
    if (-not $dlib) {
        $pkg = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.trusted.signing.client" -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | Select-Object -First 1
        if ($pkg) {
            $cand = Join-Path $pkg.FullName "bin\x64\Azure.CodeSigning.Dlib.dll"
            if (Test-Path $cand) { $dlib = $cand }
        }
    }
    if (-not $dlib -or -not (Test-Path $dlib)) {
        $dlibTmp = Join-Path ([IO.Path]::GetTempPath()) ("studio-tsclient-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $dlibTmp | Out-Null
        $zip = Join-Path $dlibTmp "trusted-signing.zip"
        Write-Output "Downloading Microsoft.Trusted.Signing.Client from NuGet…"
        Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Microsoft.Trusted.Signing.Client" -OutFile $zip
        Expand-Archive -Path $zip -DestinationPath (Join-Path $dlibTmp "pkg") -Force
        $found = Get-ChildItem -Path (Join-Path $dlibTmp "pkg") -Recurse -Filter Azure.CodeSigning.Dlib.dll |
            Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
        if (-not $found) { throw "Azure.CodeSigning.Dlib.dll not found in Microsoft.Trusted.Signing.Client" }
        $dlib = $found.FullName
    }
    $meta = Join-Path ([IO.Path]::GetTempPath()) ("studio-azsign-" + [guid]::NewGuid().ToString("N") + ".json")
    try {
        @{
            Endpoint               = $endpoint
            CodeSigningAccountName = $env:AZURE_SIGNING_ACCOUNT
            CertificateProfileName = $env:AZURE_CERT_PROFILE
        } | ConvertTo-Json | Set-Content -LiteralPath $meta -Encoding utf8
        $signArgs = @("sign", "/fd", "SHA256", "/td", "SHA256", "/tr", "http://timestamp.digicert.com", "/dlib", $dlib, "/dmdf", $meta) + $Files
        Invoke-SignTool $signArgs
        Assert-HasSignature $Files
    } finally {
        Remove-Item -LiteralPath $meta -Force -ErrorAction SilentlyContinue
        if ($dlibTmp) { Remove-Item -Recurse -Force $dlibTmp -ErrorAction SilentlyContinue }
    }
    return
}

if ($env:SIGN_SELF_SIGNED -eq "1") {
    if (-not $script:SignTool) { throw "signtool required for SIGN_SELF_SIGNED" }
    $pfx = Ensure-SelfSignedPfx
    Invoke-SignWithPfx $pfx.Path $pfx.Password $Files "SIGN_SELF_SIGNED (dev only)"
    Write-WarnMsg "Signed with a local self-signed certificate. SmartScreen will still warn; do not ship this to customers."
    return
}

Write-WarnMsg "No signing material (WINDOWS_CERTIFICATE / Azure Trusted Signing / SIGN_SELF_SIGNED=1). Leaving unsigned: $($Files -join ', ')"
exit 0
