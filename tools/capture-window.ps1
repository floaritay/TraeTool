# Capture a TraeTool window (PrintWindow), DPI-aware so the bitmap covers the
# full physical window (at 150% scaling the physical size is 1470x1020).
# Usage:
#   powershell -File capture-window.ps1 [page] [-Title <substring>] [-SleepSec N]
#     page: dashboard(default) | checkin | accounts | switch | usage | settings
#     -Title: capture the top-level window whose title contains it (e.g. wizard)
#     extra args are passed to TraeTool.exe (e.g. --wizard-preview)
param(
  [string]$Page = "dashboard",
  [string]$Title = "",
  [int]$SleepSec = 6,
  [string]$ExtraArgs = "",
  [switch]$Size860
)
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Win32Cap {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  public delegate bool EnumProc(IntPtr h, IntPtr lp);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static List<IntPtr> WindowsOf(uint pid) {
    var list = new List<IntPtr>();
    EnumWindows((h, lp) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list;
  }
  public static string TitleOf(IntPtr h) {
    var sb = new StringBuilder(512);
    GetWindowText(h, sb, 512);
    return sb.ToString();
  }
}
'@
[Win32Cap]::SetProcessDPIAware() | Out-Null

$root = Split-Path -Parent $PSScriptRoot   # repo root (script lives in tools\)
$exe = Join-Path $root 'TraeTool\bin\Release\net8.0-windows\TraeTool.exe'
$tag = if ($Title) { ($Title -replace '[^\w\-]', '_') } else { $Page }
$out = Join-Path $root "ui-window-$tag.png"

$p = Get-Process TraeTool -ErrorAction SilentlyContinue
if ($p) { Stop-Process -Id $p.Id -Force; Start-Sleep -Milliseconds 800 }

$argList = @()
if ($Page -and $Page -ne 'dashboard') { $argList += "--page=$Page" }
if ($ExtraArgs) { $argList += ($ExtraArgs -split ' ' | Where-Object { $_ }) }
if ($argList.Count -gt 0) { Start-Process $exe -ArgumentList $argList }
else { Start-Process $exe }
Start-Sleep -Seconds $SleepSec

$p = Get-Process TraeTool -ErrorAction Stop
$h = $p.MainWindowHandle
if ($Size860 -and $h -ne [IntPtr]::Zero) {
  # 860x560 logical @150% = 1290x840 physical (caller is DPI-aware)
  [Win32Cap]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 1290, 840, 0x0002 -bor 0x0004) | Out-Null
  Start-Sleep -Milliseconds 700
  $tag = "$Page-min860"
  $out = Join-Path $root "ui-window-$tag.png"
}
if ($Title) {
  $h = [IntPtr]::Zero
  foreach ($w in [Win32Cap]::WindowsOf($p.Id)) {
    if ([Win32Cap]::TitleOf($w) -like "*$Title*") { $h = $w; break }
  }
}
if ($h -eq [IntPtr]::Zero) { Write-Output 'no-window'; exit 1 }

$r = New-Object Win32Cap+RECT
[Win32Cap]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L
$ht = $r.B - $r.T

$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[Win32Cap]::PrintWindow($h, $hdc, 2) | Out-Null   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($out)
$bmp.Dispose()

if (-not $Title) { Stop-Process -Name TraeTool -Force }
Write-Output "captured ${w}x${ht} -> $out"
