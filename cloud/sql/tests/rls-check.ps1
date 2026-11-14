# Isolation, role and conflict checks for cloud/sql/0001_hub_sync.sql, through the real Neon Data API with real Neon Auth tokens.
# Run against a DEV branch only. Creates three disposable cloud accounts and one test company; deletes nothing.
# Needs NEON_AUTH_BASE_URL and NEON_DATA_API_URL (both public endpoints). Prints PASS/FAIL lines only, never tokens or passwords.
param([string]$AuthUrl = $env:NEON_AUTH_BASE_URL, [string]$ApiUrl = $env:NEON_DATA_API_URL)
$ErrorActionPreference = 'Stop'
if (-not $AuthUrl -or -not $ApiUrl) { throw 'Set NEON_AUTH_BASE_URL and NEON_DATA_API_URL.' }
$origin = @{ Origin = 'http://localhost:3000' }
$script:fail = 0
$script:last = $null
function Check($name, $ok) { if ($ok) { "PASS $name" } else { "FAIL $name (http $($script:last.Code) $($script:last.Err))"; $script:fail++ } }

function New-User($tag) {
  $email = "mynahub-rls-$tag+$([guid]::NewGuid().ToString('N').Substring(0,8))@mailinator.com"
  $pw = 'Rls!' + [guid]::NewGuid().ToString('N')
  $body = @{ email = $email; password = $pw; name = "rls $tag" } | ConvertTo-Json
  $null = Invoke-WebRequest -Method Post -Uri "$AuthUrl/sign-up/email" -Headers $origin -ContentType 'application/json' -Body $body -UseBasicParsing
  # A web session keeps every cookie the sign-in sets (reading Set-Cookie by hand loses all but one in Windows PowerShell 5.1).
  $null = Invoke-WebRequest -Method Post -Uri "$AuthUrl/sign-in/email" -Headers $origin -ContentType 'application/json' -Body $body -UseBasicParsing -SessionVariable ws
  $g = Invoke-WebRequest -Uri "$AuthUrl/get-session" -Headers $origin -WebSession $ws -UseBasicParsing
  [pscustomobject]@{ Email = $email; Jwt = [string]$g.Headers['set-auth-jwt'] }
}

# Returns @{ Code; Body } and never throws on HTTP errors.
function Call($user, $method, $path, $body, $schema = 'hub') {
  $h = @{ 'Accept-Profile' = $schema; 'Content-Profile' = $schema; Prefer = 'return=representation' }
  if ($user) { $h.Authorization = "Bearer $($user.Jwt)" }
  try {
    $p = @{ Method = $method; Uri = "$ApiUrl/$path"; Headers = $h; UseBasicParsing = $true }
    if ($null -ne $body) { $p.Body = ($body | ConvertTo-Json -Depth 8 -Compress); $p.ContentType = 'application/json' }
    $r = Invoke-WebRequest @p
    $script:last = @{ Code = [int]$r.StatusCode; Body = $(if ($r.Content) { $r.Content | ConvertFrom-Json } else { $null }); Err = '' }
  } catch {
    $code = 0; $err = $_.Exception.Message
    if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode; try { $err = (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() } catch {} }
    $script:last = @{ Code = $code; Body = $null; Err = $err }
  }
  $script:last
}

$a = New-User 'a'; $b = New-User 'b'; $c = New-User 'c'
$co = 'rls-test-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$id1 = [guid]::NewGuid().ToString(); $id2 = [guid]::NewGuid().ToString()
function Rec($sid, $base, $ms, $name, $kind = 'client') { @{ kind = $kind; sync_id = $sid; base_rev = $base; updated_ms = $ms; deleted = $false; body = @{ name = $name } } }

$r = Call $a Post 'rpc/create_company' @{ p_id = $co; p_name = 'RLS test' }
Check 'owner creates a company' ($r.Code -in 200, 204)
$r = Call $b Post 'rpc/create_company' @{ p_id = $co; p_name = 'steal' }
Check 'another user cannot take an existing company id' ($r.Code -ge 400)

$r = Call $a Post 'rpc/push_records' @{ p_company = $co; p_records = @((Rec $id1 0 1000 'Client one')) }
Check 'owner pushes a new record (created)' ($r.Code -eq 200 -and $r.Body[0].status -eq 'created' -and $r.Body[0].rev -eq 1)

$r = Call $b Get "record?company_id=eq.$co"
Check 'non-member reads no records' ($r.Code -eq 200 -and @($r.Body).Count -eq 0)
$r = Call $b Post 'rpc/push_records' @{ p_company = $co; p_records = @((Rec $id2 0 2000 'Intruder')) }
Check 'non-member cannot push' ($r.Code -ge 400)
$r = Call $b Post 'record' @{ company_id = $co; kind = 'client'; sync_id = $id2; seq = 9; updated_ms = 1; updated_by = 'x' }
Check 'non-member cannot write the table directly' ($r.Code -ge 400)
$r = Call $a Post 'record' @{ company_id = $co; kind = 'client'; sync_id = $id2; seq = 9; updated_ms = 1; updated_by = 'x' }
Check 'even the owner cannot write the table directly' ($r.Code -ge 400)
$r = Call $null Get "record?company_id=eq.$co"
Check 'no token reads nothing' ($r.Code -ge 400 -or @($r.Body).Count -eq 0)
$r = Call $b Get "company_member?company_id=eq.$co"
Check 'non-member sees no member list' ($r.Code -eq 200 -and @($r.Body).Count -eq 0)

$r = Call $b Post 'rpc/set_member' @{ p_company = $co; p_email = $b.Email; p_role = 'owner' }
Check 'non-member cannot make themselves owner' ($r.Code -ge 400)
$r = Call $a Post 'rpc/set_member' @{ p_company = $co; p_email = $b.Email; p_role = 'auditor' }
Check 'owner adds B as auditor' ($r.Code -in 200, 204)
$r = Call $b Get "record?company_id=eq.$co&order=seq"
Check 'auditor reads the records' ($r.Code -eq 200 -and @($r.Body).Count -eq 1)
$r = Call $b Post 'rpc/push_records' @{ p_company = $co; p_records = @((Rec $id1 1 3000 'Auditor edit')) }
Check 'auditor push is forbidden' ($r.Code -eq 200 -and $r.Body[0].status -eq 'forbidden')
$r = Call $b Get "conflict?company_id=eq.$co"
Check 'auditor cannot read conflicts' ($r.Code -eq 200 -and @($r.Body).Count -eq 0)

$r = Call $a Post 'rpc/set_member' @{ p_company = $co; p_email = $b.Email; p_role = 'member' }
$r = Call $b Post 'rpc/push_records' @{ p_company = $co; p_records = @((Rec $id1 1 3000 'B edit')) }
Check 'member push with the current rev updates' ($r.Code -eq 200 -and $r.Body[0].status -eq 'updated' -and $r.Body[0].rev -eq 2)
$r = Call $a Post 'rpc/push_records' @{ p_company = $co; p_records = @((Rec $id1 1 4000 'A newer stale-base')) }
Check 'stale base but newer edit wins (merged, rev 3)' ($r.Code -eq 200 -and $r.Body[0].status -eq 'merged' -and $r.Body[0].rev -eq 3)
$r = Call $b Post 'rpc/push_records' @{ p_company = $co; p_records = @((Rec $id1 2 3500 'B older stale-base')) }
Check 'stale base and older edit is rejected_older' ($r.Code -eq 200 -and $r.Body[0].status -eq 'rejected_older' -and $r.Body[0].rev -eq 3)
$r = Call $a Get "conflict?company_id=eq.$co"
Check 'both losers are kept in conflict' ($r.Code -eq 200 -and @($r.Body).Count -eq 2)
$r = Call $a Get "record?company_id=eq.$co&seq=gt.0&order=seq"
Check 'pull by seq returns the record at its latest content' ($r.Code -eq 200 -and @($r.Body).Count -eq 1 -and $r.Body[0].body.name -eq 'A newer stale-base')

$r = Call $b Post 'rpc/push_records' @{ p_company = $co; p_records = @((Rec $id2 0 5000 'Wrong kind' 'invoice')) }
Check 'a kind outside wave 1 is forbidden' ($r.Code -eq 200 -and $r.Body[0].status -eq 'forbidden')
$r = Call $c Get "company?id=eq.$co"
Check 'unrelated user does not see the company' ($r.Code -eq 200 -and @($r.Body).Count -eq 0)
$r = Call $a Post 'rpc/remove_member' @{ p_company = $co; p_user = 'nobody' }
$me = (Call $a Get "company_member?company_id=eq.$co&role=eq.owner").Body
$r = Call $a Post 'rpc/remove_member' @{ p_company = $co; p_user = $me[0].user_id }
Check 'the last owner cannot be removed' ($r.Code -ge 400)

"$script:fail failure(s)"
exit $script:fail
