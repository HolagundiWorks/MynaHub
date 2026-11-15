# Checks YOUR OWN Neon project is ready for MynaHub company sync. Read-only: it signs in with a cloud account you created and reads
# only what that account may read. It creates nothing and never prints tokens or passwords.
#   NEON_AUTH_BASE_URL and NEON_DATA_API_URL (public addresses from the Neon console), then
#   .\check-neon.ps1 -Email you@example.com -Password (Read-Host -AsSecureString)   (or set MYNA_CHECK_EMAIL / MYNA_CHECK_PASSWORD)
param(
  [string]$AuthUrl = $env:NEON_AUTH_BASE_URL,
  [string]$ApiUrl = $env:NEON_DATA_API_URL,
  [string]$Email = $env:MYNA_CHECK_EMAIL,
  $Password = $env:MYNA_CHECK_PASSWORD
)
$ErrorActionPreference = 'Stop'
$fail = 0
function Result($ok, $name, $hint) { if ($ok) { "PASS $name" } else { "FAIL $name  -> $hint"; $script:fail++ } }
if (-not $AuthUrl -or -not $ApiUrl) { throw 'Set NEON_AUTH_BASE_URL and NEON_DATA_API_URL first (see docs/byo-neon.md).' }
if ($Password -is [securestring]) { $Password = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)) }
$AuthUrl = $AuthUrl.TrimEnd('/'); $ApiUrl = $ApiUrl.TrimEnd('/')
$origin = @{ Origin = 'http://localhost:3000' }

function Http($method, $url, $headers, $body, $session) {
  try {
    $p = @{ Method = $method; Uri = $url; Headers = $headers; UseBasicParsing = $true }
    if ($null -ne $body) { $p.Body = $body; $p.ContentType = 'application/json' }
    if ($session) { $p.WebSession = $session }
    $r = Invoke-WebRequest @p
    @{ Code = [int]$r.StatusCode; Text = $r.Content; Headers = $r.Headers }
  } catch {
    $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    @{ Code = $code; Text = ''; Headers = @{} }
  }
}

$jwks = Http Get "$AuthUrl/.well-known/jwks.json" @{} $null $null
Result ($jwks.Code -eq 200 -and $jwks.Text -match '"keys"') 'Neon Auth answers (its signing keys are published)' 'Enable Neon Auth (Managed Better Auth) on the branch and copy its Auth URL.'

$anon = Http Get "$ApiUrl/record?limit=1" @{ 'Accept-Profile' = 'hub' } $null $null
Result ($anon.Code -ne 200 -or $anon.Text.Trim() -in '[]', '') 'Company data is not readable without signing in' 'The hub tables must have row level security on and no grants for the anonymous role (cloud/sql/0001_hub_sync.sql).'

if ($Email -and $Password) {
  $body = (@{ email = $Email; password = $Password } | ConvertTo-Json)
  try {
    $null = Invoke-WebRequest -Method Post -Uri "$AuthUrl/sign-in/email" -Headers $origin -ContentType 'application/json' -Body $body -UseBasicParsing -SessionVariable ws
    $g = Invoke-WebRequest -Uri "$AuthUrl/get-session" -Headers $origin -WebSession $ws -UseBasicParsing
    $jwt = [string]$g.Headers['set-auth-jwt']
    Result ($jwt.Split('.').Count -eq 3) 'Sign-in works and returns an access token' 'Check the email and password of the cloud account, and that localhost is allowed or the origin is trusted in Neon Auth.'
    if ($jwt) {
      $h = @{ Authorization = "Bearer $jwt"; 'Accept-Profile' = 'hub' }
      $c = Http Get "$ApiUrl/company?select=id,name&limit=5" $h $null $null
      Result ($c.Code -eq 200) 'The hub schema is exposed and readable for a signed-in account' 'Enable the Data API, expose the hub schema, then apply cloud/sql/0001_hub_sync.sql (docs/byo-neon.md, order matters).'
      if ($c.Code -eq 200) { "INFO  this account belongs to $(@($c.Text | ConvertFrom-Json).Count) company/companies" }
      $w = Http Post "$ApiUrl/record" ($h + @{ 'Content-Profile' = 'hub' }) '{"company_id":"x","kind":"client","sync_id":"00000000-0000-0000-0000-000000000000","seq":1,"updated_ms":1,"updated_by":"x"}' $null
      Result ($w.Code -ge 400) 'Records cannot be written directly (only through the push function)' 'Revoke insert/update on hub tables from the authenticated role (cloud/sql/0001_hub_sync.sql does).'
    }
  } catch { Result $false 'Sign-in works and returns an access token' 'The sign-in was refused (wrong email or password, or sign-up-only accounts not created yet).' }
} else {
  'INFO  no -Email/-Password given: the signed-in checks were skipped.'
}
"$fail check(s) failed. Not checked here: that public sign-up is switched off (do it in the Neon console, see docs/byo-neon.md)."
exit $fail
