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

    /// <summary>连续签到失败达此次数即在当日暂停该账号（次日自动恢复）的默认阈值。</summary>
    public const int DefaultFailSuspendThreshold = 3;

    /// <summary>连续签到失败达到该次数后当日暂停该账号（次日自动恢复）。</summary>
    public int CheckinFailSuspendThreshold { get; set; } = DefaultFailSuspendThreshold;

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

    /// <summary>出站代理模式：仅约束本工具自己的网络请求与 WebView2 登录页，见 <see cref="ProxyModes"/>。</summary>
    public string ProxyMode { get; set; } = ProxyModes.System;

    /// <summary>手动代理地址（ProxyMode=manual 时生效，仅支持 http://），如 http://127.0.0.1:7890。</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>本机管理网关开关（只监听 127.0.0.1，提供状态查询与远程签到触发）。</summary>
    public bool GatewayEnabled { get; set; }

    /// <summary>本机管理网关默认端口（127.0.0.1 上的 HTTP）。</summary>
    public const int DefaultGatewayPort = 8766;

    /// <summary>本机管理网关监听端口（127.0.0.1 上的 HTTP）。</summary>
    public int GatewayPort { get; set; } = DefaultGatewayPort;

    /// <summary>本机管理网关访问令牌；首次开启时生成，随 config.json 保存。</summary>
    public string? GatewayToken { get; set; }

    /// <summary>启用网关的 OpenAI 兼容转发（/v1/*，在账号池间取号调上游）；需同时开启网关。</summary>
    public bool GatewayChatEnabled { get; set; }

    /// <summary>
    /// 模型列表第 1 层：人工维护的覆盖项。与目录中的 id 同名即覆盖该条目的字段，
    /// 不同名即自建条目；「删除」记成 <see cref="ModelOverride.Hidden"/>，可恢复。
    /// </summary>
    public List<ModelOverride> ModelOverrides { get; set; } = new();

    /// <summary>逗号/换行分隔的模型名清单（迁移用字段）：读取时并入 <see cref="ModelOverrides"/> 后置空。</summary>
    public string? GatewayManualModels { get; set; }

    /// <summary>模型列表第 4 层：本网关收到过的请求模型名（自动累积，换行分隔）。</summary>
    public string? GatewaySeenModels { get; set; }

    public static class DeviceIdModes
    {
        public const string Random = "random";
        public const string FromTraeClient = "client";
    }

    public static class ProxyModes
    {
        /// <summary>不使用代理，直连。</summary>
        public const string None = "none";

        /// <summary>跟随系统代理（系统未设代理时即直连，默认）。</summary>
        public const string System = "system";

        /// <summary>使用 <see cref="AppSettings.ProxyUrl"/> 指定的代理。</summary>
        public const string Manual = "manual";
    }
}

/// <summary>
/// 模型列表第 1 层的一条覆盖：字段为 null 表示不改，由官网同步 / 内置层补齐。
/// 与目录中的 id 同名即覆盖该条目，不同名即自建条目。
/// </summary>
public class ModelOverride
{
    public string Id { get; set; } = "";

    public string? Display { get; set; }

    public string? Vendor { get; set; }

    public string? Rate { get; set; }

    public string? Efforts { get; set; }

    public string? Context { get; set; }

    public string? LongContext { get; set; }

    public bool? Image { get; set; }

    /// <summary>「删除」= 隐藏：不出现在列表与 /v1/models，官网同步也带不回来，可一键恢复。</summary>
    public bool Hidden { get; set; }
}
