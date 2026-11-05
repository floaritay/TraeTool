using System.Diagnostics;
using System.IO;

namespace TraeTool.Services.Switch;

/// <summary>
/// Trae 桌面客户端「登录态载体」定义：
/// 固定 15 项白名单 + 缓存/日志类排除目录 + 数据根/可执行文件探测。
/// 白名单必须固定——动态目录扫描会漏掉 state.vscdb 侧车文件导致切换失效。
/// </summary>
public static class CarrierDefs
{
    /// <summary>一条载体（文件或整体目录）。</summary>
    public readonly record struct CarrierEntry(string Rel, bool IsDir);

    /// <summary>
    /// 固定 15 项白名单（相对数据根）。目录条目整体备份/恢复。
    /// </summary>
    public static IReadOnlyList<CarrierEntry> Whitelist { get; } = new CarrierEntry[]
    {
        new(@"User\globalStorage\storage.json", false),   // 登录态键 + 设备遥测（切换后必须回写）
        new(@"User\globalStorage\state.vscdb", false),    // SQLite：登录令牌真源
        new(@"User\globalStorage\state.vscdb-wal", false), // WAL 侧车（必须与主库成对处理）
        new(@"User\globalStorage\state.vscdb-shm", false),
        new(@"User\globalStorage\state.vscdb.backup", false),
        new("machineid", false),
        new("aha", true),                                  // 设备授权数据
        new("Preferences", false),
        new("Local State", false),
        new(@"Local Storage\leveldb", true),               // 网页侧登录/偏好 KV（含滚动日志，整树替换）
        new(@"Local Storage\config.db", false),
        new(@"Network", true),                             // cookies
        new(@"Partitions\trae-webview", true),
        new(@"Partitions\icube-web-crawler-shared-session-v1.0", true),
        new(@"Session Storage", true),
    };

    /// <summary>
    /// 复制/哈希时按目录名（任意深度）排除——缓存/日志成千上万文件且与登录态无关，
    /// 纳入会让建档卡顿数十秒、回滚白拷几十 MB。
    /// </summary>
    public static IReadOnlySet<string> ExcludedDirNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "CachedData", "CachedConfigurations", "CachedProfilesData",
        "Code Cache", "GPUCache", "Crashpad", "DawnGraphiteCache", "DawnWebGPUCache",
        "ShaderCache", "Dictionaries", "blob_storage", "Service Worker",
        "Shared Dictionary", "logs", "Backups",
    };

    public static bool IsExcludedDir(string dirName) => ExcludedDirNames.Contains(dirName);

    /// <summary>客户端数据根：%APPDATA%\Trae CN。</summary>
    public static string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Trae CN");

    /// <summary>客户端进程名（不含 .exe）。</summary>
    public const string ProcessName = "Trae CN";

    public static string Abs(string rel) => Path.Combine(DataRoot, rel);

    /// <summary>
    /// 探测客户端 exe，顺序：① 手动 override（文件存在）② 运行中 "Trae CN" 进程主模块
    /// （最可靠——本机安装路径不受安装器版本影响）③ 候选路径清单（含本机
    /// <c>D:\Trae\Trae CN</c> 等布局）④ null。
    /// </summary>
    public static string? TryDetectExe(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var fromProcess = TryGetRunningExe();
        if (fromProcess != null) return fromProcess;

        foreach (var exe in CandidateExePaths())
        {
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>从运行中的客户端进程读 exe 路径（失败返回 null）。</summary>
    public static string? TryGetRunningExe()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(ProcessName))
            {
                using (p)
                {
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                    }
                    catch { /* 无权限/已退出：试下一个实例 */ }
                }
            }
        }
        catch { /* 进程枚举失败 */ }
        return null;
    }

    /// <summary>候选 exe 路径（按优先级）。</summary>
    private static IEnumerable<string> CandidateExePaths()
    {
        var drives = new[] { "D:", "C:", "E:" };
        // 常见布局：<drive>\Trae\Trae CN\Trae CN.exe
        foreach (var d in drives)
            yield return Path.Combine(d + @"\", "Trae", "Trae CN", "Trae CN.exe");
        foreach (var d in drives)
            yield return Path.Combine(d + @"\", "Trae CN", "Trae CN.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Trae CN", "Trae CN.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Trae CN", "Trae CN.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Trae CN", "Trae CN.exe");
    }
}
