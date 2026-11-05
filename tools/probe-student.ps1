# Probe the real student_verification endpoint: decrypt config token/session
# (DPAPI CurrentUser -> raw), renew JWT via GetUserToken, then POST and print
# the RAW response to learn the exact JSON envelope. ASCII-only.
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Web.Extensions

$cfgPath = Join-Path $env:APPDATA 'TraeTool\config.json'
$json = [IO.File]::ReadAllText($cfgPath)
# crude field extraction (top-level accounts array, first entry)
function Pick([string]$name, [string]$src) {
  $m = [regex]::Match($src, '"' + $name + '"\s*:\s*"([^"]*)"')
  if ($m.Success) { return $m.Groups[1].Value }
  return $null
}
$tok = Pick 'Token' $json
$sess = Pick 'Session' $json
if (-not $tok -or -not $sess) { Write-Output 'missing token/session'; exit 1 }

function Unseal([string]$b64) {
  try {
    $b = [Convert]::FromBase64String($b64)
    $plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
      $b, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [Text.Encoding]::UTF8.GetString($plain)
  } catch { return $null }
}
$token = Unseal $tok
$session = Unseal $sess
Write-Output ("sealed? token={0} session={1}" -f ($token -ne $null), ($session -ne $null))
if (-not $token) { Write-Output 'unprotect failed (or legacy plaintext)'; $token = $tok }
if (-not $session) { $session = $sess }

# 1) renew JWT (Cookie only -> Result.Token)
$renewBody = '{}'
$renew = & curl.exe -s -X POST 'https://api.trae.cn/cloudide/api/v3/common/GetUserToken' `
  -H 'Content-Type: application/json' -H 'Referer: https://www.trae.cn/' -H 'Origin: https://www.trae.cn' `
  -H ('Cookie: X-Cloudide-Session=' + $session) -d $renewBody --max-time 15
$rm = [regex]::Match($renew, '"Token"\s*:\s*"([^"]+)"')
if ($rm.Success) { $token = $rm.Groups[1].Value; Write-Output 'renewed JWT ok' }
else { Write-Output ('renew failed: ' + $renew.Substring(0, [Math]::Min(220, $renew.Length))) }

# 2) student verification - print RAW
$stu = & curl.exe -s -X POST 'https://api.trae.cn/trae/api/v3/student_verification/status' `
  -H 'Content-Type: application/json' `
  -H ('Authorization: Cloud-IDE-JWT ' + $token) `
  -H ('Cookie: X-Cloudide-Session=' + $session) `
  -H 'Referer: https://www.trae.cn/' -H 'Origin: https://www.trae.cn' `
  -d '{}' --max-time 15
Write-Output '--- student_verification raw ---'
Write-Output $stu
