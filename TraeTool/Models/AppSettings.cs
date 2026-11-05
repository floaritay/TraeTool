namespace TraeTool.Models;

/// <summary>
/// 本地配置（%APPDATA%\TraeTool\config.json）。
/// 密文字段（Token/Session/DeviceId）落盘前必须经 DPAPI 加密，
/// 序列化走 ConfigStore 的克隆路径、反序列化走解密路径——见 ConfigStore 注释。
/// </summary>
public class AppSettings
{
    public const string CurrentVersion = "1";

    /// <summary>配置版本，用于未来迁移判断。</summary>
    public string Version { get; set; } = CurrentVersion;

    /// <summary>全部账号（多账号模型主存储）。</summary>
    public List<Account> Accounts { get; set; } = new();

    /// <summary>当前激活（正在查看）的账号 Id；为空时取 Accounts[0]。</summary>
    public string? ActiveAccountId { get; set; }

    public bool AutoCheckinEnabled { get; set; } = true;

    /// <summary>自动签到时间，"HH:mm"。默认 09:00。</summary>
    public string AutoCheckinTime { get; set; } = "09:00";

    /// <summary>批量签到的账号基础间隔秒数（执行时附加 ±2s 随机抖动）。</summary>
    public int CheckinIntervalSeconds { get; set; } = 5;

    /// <summary>开机自启动（HKCU Run 键）。</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>关闭窗口时最小化到托盘而非退出。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>设备号来源：随机生成 / 从本机 Trae 客户端读取真实设备号。</summary>
    public string DeviceIdMode { get; set; } = DeviceIdModes.Random;

    /// <summary>Trae 客户端 exe 手动指定路径；空 = 自动探测（运行进程优先 → 候选清单）。</summary>
    public string? ClientExePath { get; set; }

    /// <summary>自动签到最近一次触发日期（yyyy-MM-dd）——持久化后每天只自动跑一次，重启不重复。</summary>
    public string? LastAutoRunDate { get; set; }

    /// <summary>程序「记住」的当前客户端登录账号 Id（切换成功/建档成功时写入）；用于行徽标在相似度无法判定时的裁决。</summary>
    public string? ClientActiveAccountId { get; set; }

    /// <summary>界面主题：light / dark（ThemeService 应用）。</summary>
    public string Theme { get; set; } = "light";

    /// <summary>首启用户协议/免责声明已同意（EulaWindow）。</summary>
    public bool EulaAccepted { get; set; }

    /// <summary>更新源 URL（GitHub Releases API 或 version.json；空 = 未配置更新源）。</summary>
    public string? UpdateFeedUrl { get; set; }

    public static class DeviceIdModes
    {
        public const string Random = "random";
        public const string FromTraeClient = "client";
    }
}
