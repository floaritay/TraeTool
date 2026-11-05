# Phase-1 last path test: Session -> GetUserToken -> status probe (read-only).
# PS 5.1 Invoke-RestMethod cannot set the restricted Cookie header (it was
# silently dropped -> server said "Token invalid"), so this uses curl.exe instead.
# Decrypts account[0] Session/DeviceId via DPAPI; mutates NOTHING on disk.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$cfg = Get-Content "$env:APPDATA\TraeTool\config.json" -Raw | ConvertFrom-Json
if ($cfg.Accounts.Count -lt 1) { Write-Output 'no accounts'; exit 1 }
$acc = $cfg.Accounts[0]

function Unseal([string]$b64) {
    $bytes = [Convert]::FromBase64String($b64)
    $plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
        $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [System.Text.Encoding]::UTF8.GetString($plain)
}

if ([string]::IsNullOrEmpty($acc.Session)) { Write-Output 'no Session'; exit 2 }
$session = Unseal $acc.Session
$deviceId = Unseal $acc.DeviceId
Write-Output ("account={0} sessionLen={1} deviceTail={2}" -f $acc.ScreenName, $session.Length, $deviceId.Substring($deviceId.Length - 4))

# Step 1: renew JWT via Session cookie (curl.exe handles Cookie header natively)
$renewJson = & curl.exe -s -X POST 'https://api.trae.cn/cloudide/api/v3/common/GetUserToken' `
    -H "Cookie: X-Cloudide-Session=$session" `
    -H 'Referer: https://www.trae.cn/' `
    -H 'Origin: https://www.trae.cn' `
    -H 'User-Agent: TraeTool/1.0' `
    --max-time 30
$renew = $renewJson | ConvertFrom-Json
$jwt = $renew.Result.Token
if ([string]::IsNullOrEmpty($jwt)) {
    Write-Output ('RENEW FAIL: ' + (($renewJson | Out-String).Trim().Substring(0, [Math]::Min(300, ($renewJson | Out-String).Trim().Length))))
    exit 3
}
Write-Output ("RENEW OK: new jwt len={0}" -f $jwt.Length)

# Step 2: probe status with the new JWT (what EnsureValidTokenAsync does next)
$stJson = & curl.exe -s -X POST 'https://api.trae.cn/trae/api/v2/ug/checkin_credits/status' `
    -H "Authorization: Cloud-IDE-JWT $jwt" `
    -H 'X-User-Region: cn' `
    -H "x-device-id: $deviceId" `
    -H 'User-Agent: TraeTool/1.0' `
    -H 'Content-Type: application/json' `
    -d '{}' --max-time 30
$st = $stJson | ConvertFrom-Json
Write-Output ("PROBE: code={0} checked_in={1} message={2}" -f $st.code, $st.checked_in, $st.message)
if ($st.code -eq 0) { Write-Output 'ALL PASS'; exit 0 }
Write-Output ('PROBE FAIL: ' + $stJson); exit 4
