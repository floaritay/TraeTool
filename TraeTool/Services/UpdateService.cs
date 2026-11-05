using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using TraeTool.ViewModels;

namespace TraeTool.Services;

/// <summary>一次可用的更新信息。</summary>
public sealed record UpdateInfo(string Version, string? Notes, string PackageUrl);

/// <summary>检查结果：Update 非空=有新版；UpToDate=true=已最新；Error 非空=失败。</summary>
public sealed record UpdateCheck(UpdateInfo? Update, bool UpToDate, string? Error);

/// <summary>
/// 自动更新（更新源可配置）：
///  - GitHub Releases API（…/repos/owner/repo/releases/latest，取 tag_name + .zip 资产）
///  - 普通 version.json：{"version":"1.2.0","url":"https://…/TraeTool-1.2.0.zip","notes":"…"}
/// 安装 = 下载解压到暂存区 → 写一个等待本进程退出的 bat（xcopy 覆盖后重启）→ 退出。
/// </summary>
public static class UpdateService
{
    /// <summary>
    /// 内置默认更新源（留空 UpdateFeedUrl 时使用）。
    /// 指向本仓库 GitHub Releases；发布新版本时打 tag + 上传 zip 资产即可被客户端检测到。
    /// </summary>
    public const string DefaultFeedUrl = "https://api.github.com/repos/floaritay/TraeTool/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentVersionText
    {
        get
        {
            var v = CurrentVersion;
            return v.Revision > 0 ? $"v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"
                 : v.Build > 0 ? $"v{v.Major}.{v.Minor}.{v.Build}"
                 : $"v{v.Major}.{v.Minor}";
        }
    }

    public static async Task<UpdateCheck> CheckAsync(string? feedUrl)
    {
        // 用户未配置 → 用内置默认源（开源发布后 DefaultFeedUrl 生效，URL 永久免填）
        if (string.IsNullOrWhiteSpace(feedUrl)) feedUrl = DefaultFeedUrl;
        if (string.IsNullOrWhiteSpace(feedUrl))
            return new UpdateCheck(null, false, "未配置更新源（项目尚未内置默认源；可填写 GitHub Releases API 或 version.json 地址）");
        try
        {
            string version;
            string? notes = null;
            string? url;
            if (feedUrl.Contains("api.github.com/repos", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, feedUrl.Trim());
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                req.Headers.TryAddWithoutValidation("User-Agent", "TraeTool");
                using var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                    return new UpdateCheck(null, false, $"更新源返回 HTTP {(int)resp.StatusCode}");
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                version = (root.TryGetProperty("tag_name", out var t) ? t.GetString() : "")?.TrimStart('v', 'V') ?? "";
                if (root.TryGetProperty("body", out var b)) notes = b.GetString();
                url = null;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                            break;
                        }
                    }
                }
                if (string.IsNullOrEmpty(url))
                    return new UpdateCheck(null, false, "更新源里没有 .zip 安装包资产");
            }
            else
            {
                var json = await Http.GetStringAsync(feedUrl.Trim());
                using var doc = JsonDocument.Parse(json);
                version = doc.RootElement.GetProperty("version").GetString() ?? "";
                url = doc.RootElement.GetProperty("url").GetString();
                if (doc.RootElement.TryGetProperty("notes", out var n)) notes = n.GetString();
                if (string.IsNullOrEmpty(url))
                    return new UpdateCheck(null, false, "version.json 缺少 url 字段");
            }

            if (!Version.TryParse(version, out var ver))
                return new UpdateCheck(null, false, $"无法解析版本号：{version}");
            if (ver <= CurrentVersion)
                return new UpdateCheck(null, true, null);
            return new UpdateCheck(new UpdateInfo(version, notes, url!), false, null);
        }
        catch (Exception ex)
        {
            return new UpdateCheck(null, false, "检查更新失败：" + ex.Message);
        }
    }

    /// <summary>下载并解压到暂存区；返回 staging 目录，失败返回错误文本。</summary>
    public static async Task<(string? Staging, string? Error)> DownloadAndStageAsync(
        UpdateInfo info, IProgress<string>? progress = null)
    {
        try
        {
            var root = Path.Combine(Paths.Root, "update");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Directory.CreateDirectory(root);
            var zip = Path.Combine(root, "pkg.zip");

            progress?.Report("下载更新包…");
            var bytes = await Http.GetByteArrayAsync(info.PackageUrl);
            if (bytes.Length < 1024) return (null, "更新包异常（过小）");
            await File.WriteAllBytesAsync(zip, bytes);

            progress?.Report("解压中…");
            var staging = Path.Combine(root, "staging");
            ZipFile.ExtractToDirectory(zip, staging);

            // 常见 zip 外包一层文件夹 → 下钻
            var entries = Directory.GetFileSystemEntries(staging);
            if (entries.Length == 1 && Directory.Exists(entries[0])) staging = entries[0];

            if (!File.Exists(Path.Combine(staging, "TraeTool.exe")))
                return (null, "更新包中未找到 TraeTool.exe");
            return (staging, null);
        }
        catch (Exception ex)
        {
            return (null, "下载/解压失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 写等待脚本并重启安装：bat 等本进程退出 → xcopy 覆盖 → 启动新版本 → 自删。
    /// 必须经 AppServices.ForceExit 退出（CloseToTray 开着时 OnClosing 会拦截普通 Close）。
    /// </summary>
    public static void ApplyAndRestart(string stagingDir)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前 exe");
        var appDir = Path.GetDirectoryName(exe)!;
        var pid = Environment.ProcessId;
        var bat = Path.Combine(Path.GetTempPath(), $"traetool_update_{pid}.bat");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine(":wait");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
        sb.AppendLine("if not errorlevel 1 (ping -n 2 127.0.0.1 >nul & goto wait)");
        sb.AppendLine($"set \"STG={stagingDir}\"");
        sb.AppendLine($"set \"APP={appDir}\"");
        sb.AppendLine("xcopy /s /e /y \"%STG%\\*\" \"%APP%\\\" >nul");
        sb.AppendLine($"start \"\" \"{exe}\"");
        sb.AppendLine("endlocal");
        sb.AppendLine("del \"%~f0\"");
        // bat 必须纯 ASCII（避免 CN locale 下编码损坏）
        File.WriteAllText(bat, sb.ToString(), Encoding.ASCII);

        AppLog.Info("update", $"安装脚本已写入 {pid}→{appDir}，准备退出安装");
        ViewModels.AppServices.ForceExit = true;
        Process.Start(new ProcessStartInfo(bat) { UseShellExecute = true });
        System.Windows.Application.Current?.Shutdown();
    }

    /// <summary>
    /// 原地重启（无文件覆盖）：等待本进程退出 → 重新启动 → 自删脚本。
    /// 导入备份等“改完需重启生效”的场景用。
    /// </summary>
    public static void RestartApp()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前 exe");
        var pid = Environment.ProcessId;
        var bat = Path.Combine(Path.GetTempPath(), $"traetool_restart_{pid}.bat");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine(":wait");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
        sb.AppendLine("if not errorlevel 1 (ping -n 2 127.0.0.1 >nul & goto wait)");
        sb.AppendLine($"start \"\" \"{exe}\"");
        sb.AppendLine("endlocal");
        sb.AppendLine("del \"%~f0\"");
        File.WriteAllText(bat, sb.ToString(), Encoding.ASCII);

        AppLog.Info("update", "写入重启脚本 pid=" + pid);
        ViewModels.AppServices.ForceExit = true;
        Process.Start(new ProcessStartInfo(bat) { UseShellExecute = true });
        System.Windows.Application.Current?.Shutdown();
    }
}
