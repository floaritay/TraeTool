# E2E test: real mouse click on the custom titlebar MINIMIZE button.
# Verifies WindowChrome caption hit-testing (IsHitTestVisibleInChrome fix).
# Restores the window afterwards. ASCII-only.
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win32Tb {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
[Win32Tb]::SetProcessDPIAware() | Out-Null

$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'TraeTool\bin\Release\net8.0-windows\TraeTool.exe'
$p = Get-Process TraeTool -ErrorAction SilentlyContinue
if ($p) { Stop-Process -Id $p.Id -Force; Start-Sleep -Milliseconds 800 }
Start-Process $exe
Start-Sleep -Seconds 5

$p = Get-Process TraeTool -ErrorAction Stop
$h = $p.MainWindowHandle
$r = New-Object Win32Tb+RECT
[Win32Tb]::GetWindowRect($h, [ref]$r) | Out-Null
Write-Output ("window rect: {0},{1} - {2},{3}" -f $r.L, $r.T, $r.R, $r.B)

# Titlebar 38 logical @150% = 57 physical. Buttons from right edge:
# close 46 logical(69 phys), max 44(66), min 44(66). Min center = R - 69 - 66 - 33.
$cx = $r.R - 168
$cy = $r.T + 28
Write-Output "click min at $cx,$cy"
[Win32Tb]::SetCursorPos($cx, $cy) | Out-Null
Start-Sleep -Milliseconds 200
[Win32Tb]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)  # LEFTDOWN
Start-Sleep -Milliseconds 80
[Win32Tb]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)  # LEFTUP
Start-Sleep -Seconds 2

$r2 = New-Object Win32Tb+RECT
[Win32Tb]::GetWindowRect($h, [ref]$r2) | Out-Null
Write-Output ("after click: {0},{1} - {2},{3}" -f $r2.L, $r2.T, $r2.R, $r2.B)
if ($r2.R -lt -10000 -or ($r2.B - $r2.T) -lt 100) {
  Write-Output "PASS: window minimized -> titlebar buttons work"
  [Win32Tb]::ShowWindow($h, 9) | Out-Null   # SW_RESTORE
} else {
  Write-Output "FAIL: window did NOT minimize -> caption still swallows clicks"
}
