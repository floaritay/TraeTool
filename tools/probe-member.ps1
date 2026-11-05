# Read-only probe: dump entitlements + GetUserInfo JSON to temp files for field inspection.
# ASCII-only script (CN-locale PS 5.1 encoding pitfall).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$cfg = Get-Content "$env:APPDATA\TraeTool\config.json" -Raw | ConvertFrom-Json
$acc = $cfg.Accounts[0]

function Unseal([string]$b64) {
    $bytes = [Convert]::FromBase64String($b64)
    [System.Text.Encoding]::UTF8.GetString(
        [System.Security.Cryptography.ProtectedData]::Unprotect(
            $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser))
}

$jwt = Unseal $acc.Token
$session = Unseal $acc.Session
$deviceId = Unseal $acc.DeviceId
$outDir = Join-Path $env:TEMP 'traietool_probe'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# 1) entitlement list (full)
& curl.exe -s -X POST 'https://api.trae.cn/trae/api/v2/pay/user_current_entitlement_list' `
    -H "Authorization: Cloud-IDE-JWT $jwt" `
    -H 'X-User-Region: cn' `
    -H "x-device-id: $deviceId" `
    -H 'User-Agent: TraeTool/1.0' `
    -H 'Content-Type: application/json' `
    -d '{}' --max-time 30 -o "$outDir\entitlements.json"

# 2) GetUserInfo (full)
& curl.exe -s -X POST 'https://api.trae.cn/cloudide/api/v3/trae/GetUserInfo' `
    -H "Authorization: Cloud-IDE-JWT $jwt" `
    -H "Cookie: X-Cloudide-Session=$session" `
    -H 'Referer: https://www.trae.cn/' `
    -H 'Origin: https://www.trae.cn' `
    -H 'Content-Type: application/json' `
    -d '{}' --max-time 30 -o "$outDir\userinfo.json"

Write-Output ("dumped to " + $outDir)
Get-ChildItem $outDir | ForEach-Object { Write-Output ($_.Name + ' ' + $_.Length + ' bytes') }
