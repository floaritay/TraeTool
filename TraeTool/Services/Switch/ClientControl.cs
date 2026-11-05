using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TraeTool.Services.Switch;

/// <summary>
/// Trae 客户端进程生命周期：优雅关闭（WM_CLOSE）→ 强杀进程树 → 拉起。
/// 直接强杀会让 SQLite 停在未 checkpoint 的 WAL 上，所以必须先给优雅关闭窗口期；
/// 强杀后仍存活必须让调用方中止——带文件锁硬做备份/恢复会损坏数据。
/// </summary>
public static class ClientControl
{
    private static readonly TimeSpan GracefulWait = TimeSpan.FromSeconds(11);
    private static readonly TimeSpan ForceWait = TimeSpan.FromSeconds(8);

    /// <summary>解析 exe：手动 override → 运行进程 → 候选清单（见 CarrierDefs）。</summary>
    public static string? ResolveExe(string? overridePath) => CarrierDefs.TryDetectExe(overridePath);

    public static bool IsRunning()
    {
        try { return Process.GetProcessesByName(CarrierDefs.ProcessName).Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// 关闭全部客户端进程。返回 false = 超时仍存活（调用方必须中止操作）。
    /// 顺序：对每个主窗口发 WM_CLOSE 等优雅期 → Kill(entireProcessTree) 等强杀期。
    /// </summary>
    public static async Task<bool> KillAllAsync(CancellationToken ct = default)
    {
        var procs = GetProcs();
        if (procs.Length == 0) return true;

        AppLog.Info("switch", $"关闭客户端：{procs.Length} 个进程，先优雅关闭（≤{GracefulWait.TotalSeconds:0}s）");
        foreach (var p in procs)
        {
            try { TryPostWmClose(p); } catch { /* 下面按存活情况统一处理 */ }
        }
        if (await WaitForExitAsync(procs, GracefulWait, ct)) return true;

        procs = GetProcs();
        if (procs.Length == 0) return true;
        AppLog.Info("switch", "优雅关闭超时，强杀进程树");
        foreach (var p in procs)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* 可能已退出 */ }
        }
        var exited = await WaitForExitAsync(procs, ForceWait, ct);

        if (!exited)
            AppLog.Error("switch", "强杀后仍有客户端进程存活，中止操作");
        foreach (var p in procs) p.Dispose();
        return exited;
    }

    /// <summary>拉起客户端（工作目录设为 exe 所在目录）。</summary>
    public static void Launch(string exePath)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("客户端可执行文件不存在", exePath);
        Process.Start(new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
        });
        AppLog.Info("switch", "已拉起客户端: " + exePath);
    }

    // ---------- 内部 ----------

    private static Process[] GetProcs()
    {
        try { return Process.GetProcessesByName(CarrierDefs.ProcessName); }
        catch { return Array.Empty<Process>(); }
    }

    private static async Task<bool> WaitForExitAsync(Process[] procs, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            bool anyAlive = false;
            foreach (var p in procs)
            {
                try
                {
                    if (!p.HasExited) { anyAlive = true; break; }
                }
                catch { /* 句柄失效视为已退出 */ }
            }
            if (!anyAlive) return true;
            await Task.Delay(300, ct);
        }
        // 最终确认
        foreach (var p in procs)
        {
            try { if (!p.HasExited) return false; } catch { }
        }
        return true;
    }

    private static void TryPostWmClose(Process p)
    {
        if (p.MainWindowHandle != IntPtr.Zero)
            PostMessage(p.MainWindowHandle, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    private const uint WmClose = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
