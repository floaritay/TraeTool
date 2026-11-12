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

    /// <summary>
    /// 清理配置里已不存在的账号的 WebView2 用户数据目录——该目录持久化着登录令牌，
    /// 仅在「删除账号」时清理会让登出/导出的账号凭据长期留在磁盘上。
    /// 账号列表为空时不做任何删除：配置读取失败时不能把全部登录态误删。
    /// </summary>
    public static void CleanupOrphanProfiles(IEnumerable<string> accountIds)
    {
        try
        {
            var ids = accountIds.Where(id => !string.IsNullOrEmpty(id)).ToList();
            if (ids.Count == 0) return;
            if (!Directory.Exists(WebViewRoot)) return;

            var keep = ids.Select(id => "account_" + (id.Length > 12 ? id[..12] : id))
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.GetDirectories(WebViewRoot))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith("account_", StringComparison.OrdinalIgnoreCase)) continue;
                if (keep.Contains(name)) continue;
                try { Directory.Delete(dir, recursive: true); } catch { /* 被占用则下次启动再试 */ }
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }

    /// <summary>切换回滚快照目录（切换前建立的 live 备份；成功路径会立即删除）。</summary>
    public static string SwitchSnapshotDir(string name) => Path.Combine(SwitchDir, name);

    /// <summary>档案暂存目录（建档整备完成后整体换入正式目录，失败即清理）。</summary>
    public static string VaultStagingDir(string accountId)
        => Path.Combine(VaultDir, accountId + ".staging_" + Guid.NewGuid().ToString("N"));

    /// <summary>档案上一代备份目录（建档换入时保留一代）。</summary>
    public static string VaultBackupDir(string accountId) => Path.Combine(VaultDir, accountId + ".bak");

    /// <summary>
    /// 清理进程崩溃残留的临时目录：过期的切换回滚快照、档案暂存目录。
    /// 二者都含客户端登录态副本，不长期留存；保留窗口内的人工回滚快照不受影响。
    /// </summary>
    public static void CleanupStaleTempDirs()
    {
        SweepStaleDirs(SwitchDir, "rollback_", TimeSpan.FromDays(7));
        SweepStaleDirs(VaultDir, ".staging_", TimeSpan.FromHours(2));
    }

    /// <summary>清理保留窗口外的日志文件（含轮转出的 .1）。</summary>
    public static void CleanupOldLogs(TimeSpan? olderThan = null)
    {
        try
        {
            if (!Directory.Exists(LogsDir)) return;
            var cutoff = DateTime.UtcNow - (olderThan ?? TimeSpan.FromDays(14));
            foreach (var file in Directory.GetFiles(LogsDir, "traetool-*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch { /* 被占用则下次启动再试 */ }
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }

    private static void SweepStaleDirs(string root, string nameMarker, TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (!Path.GetFileName(dir).Contains(nameMarker, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (Directory.GetCreationTimeUtc(dir) < cutoff)
                        Directory.Delete(dir, recursive: true);
                }
                catch { /* 被占用则下次启动再试 */ }
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }
}
