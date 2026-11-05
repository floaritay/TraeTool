using System.IO;
namespace TraeTool.Services;

/// <summary>
/// 数据目录布局。启动时调用 <see cref="EnsureCreated"/> 一次性建齐。
/// </summary>
public static class Paths
{
    /// <summary>%APPDATA%\TraeTool</summary>
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraeTool");

    public static string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary>SQLite 等结构化数据。</summary>
    public static string DataDir => Path.Combine(Root, "data");

    public static string LogsDir => Path.Combine(Root, "logs");

    /// <summary>按账号 Id/Uid 键控的客户端登录态 vault。</summary>
    public static string VaultDir => Path.Combine(Root, "vault");

    /// <summary>切换状态与回滚快照。</summary>
    public static string SwitchDir => Path.Combine(Root, "switch");

    public static string DatabaseFile => Path.Combine(DataDir, "checkin.db");

    /// <summary>%LOCALAPPDATA%\TraeTool\WebView 共享根；每账号在其下有独立子目录。</summary>
    public static string WebViewRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TraeTool", "WebView");

    /// <summary>某账号的 WebView2 用户数据目录（账号间登录态隔离）。目录名取 Id 前 12 位足够唯一且路径短。</summary>
    public static string WebViewProfileDir(string accountId)
        => Path.Combine(WebViewRoot, "account_" + (accountId.Length > 12 ? accountId[..12] : accountId));

    /// <summary>确保全部数据目录存在（幂等）。首启时调用。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(VaultDir);
        Directory.CreateDirectory(SwitchDir);
        Directory.CreateDirectory(WebViewRoot);
    }

    /// <summary>删除某账号的 WebView profile 目录（删账号时调用），失败不抛。</summary>
    public static void TryDeleteProfile(string accountId)
    {
        try
        {
            var dir = WebViewProfileDir(accountId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* 目录被占用等场景忽略 */ }
    }
}
