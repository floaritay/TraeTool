using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TraeTool.Models;

using TraeTool.Services.Gateway;
namespace TraeTool.Services;

/// <summary>
/// config.json 存取。敏感字段（Account.Token / Session / DeviceId）以 DPAPI 加密落盘，
/// 内存中保持明文（各调用方无感）。
///
/// ⚠️ 已知陷阱：每个新增密文字段必须**同时**加进
/// <see cref="Seal"/>（Save 路径）与 <see cref="UnsealSecrets"/>（Load 路径），
/// 漏加任何一侧都会导致该字段「改完重启就失效」。
/// 本实现把密文字段集中在 <see cref="SecretFields"/> 一处登记，两侧复用同一张表防漏。
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>密文字段访问器（属性名 → get/set）。新增密文属性只需在此追加一行。</summary>
    private static readonly (string Name, Func<Account, string?> Get, Action<Account, string?> Set)[] SecretFields =
    [
        ("Token", a => a.Token, (a, v) => a.Token = v),
        ("Session", a => a.Session, (a, v) => a.Session = v),
        ("DeviceId", a => a.DeviceId, (a, v) => a.DeviceId = v),
    ];

    private static string BackupPath => Paths.ConfigFile + ".bak";

    /// <summary>
    /// 落盘串行化：Save 会被 UI 线程（设置变更）、签到批次、网关请求（记录请求过的模型名）、
    /// 模型同步（续期写回 Token）并发调用，同一路径不加锁会出现丢写与半截 .bak。
    /// </summary>
    private static readonly object SaveGate = new();

    public static AppSettings Load()
    {
        // 主文件损坏（如写盘中被杀导致截断）时回退到上一次 .bak——
        // 直接回退默认值会让下一次 Save 把空配置写盘
        foreach (var path in new[] { Paths.ConfigFile, BackupPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (cfg == null) continue;
                UnsealSecrets(cfg);
                Normalize(cfg);
                if (path != Paths.ConfigFile)
                    AppLog.Error("config", "主配置损坏，已从 .bak 恢复");
                return cfg;
            }
            catch (Exception ex)
            {
                AppLog.Error("config", $"读取 {Path.GetFileName(path)} 失败: {ex.Message}");
            }
        }

        var fresh = new AppSettings();
        Normalize(fresh);
        return fresh;
    }

    /// <summary>
    /// 写「加密副本」而非原对象（内存明文不受影响，磁盘无明文）。
    /// 步骤：留 .bak → 写临时文件 → 原子替换主文件；三步同锁，
    /// 避免并发时把另一个线程写到一半的主文件拷成 .bak，也避免半截主文件留在盘上。
    /// </summary>
    public static void Save(AppSettings cfg)
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            var json = JsonSerializer.Serialize(Seal(cfg), JsonOpts);
            var tmp = Paths.ConfigFile + ".tmp";
            lock (SaveGate)
            {
                // 覆盖前留一份 .bak（Load 可在主文件损坏时回退）
                if (File.Exists(Paths.ConfigFile))
                    File.Copy(Paths.ConfigFile, BackupPath, overwrite: true);
                File.WriteAllText(tmp, json);
                File.Move(tmp, Paths.ConfigFile, overwrite: true);
            }
        }
        catch { /* 保存失败（含 DPAPI 异常）保留磁盘旧文件，下次启动仍可读 */ }
    }

    /// <summary>生成敏感字段已 DPAPI 加密的深拷贝。</summary>
    private static AppSettings Seal(AppSettings cfg)
    {
        var copy = new AppSettings
        {
            Version = cfg.Version,
            ActiveAccountId = cfg.ActiveAccountId,
            AutoCheckinEnabled = cfg.AutoCheckinEnabled,
            AutoCheckinTime = cfg.AutoCheckinTime,
            CheckinIntervalSeconds = cfg.CheckinIntervalSeconds,
            CheckinFailSuspendThreshold = cfg.CheckinFailSuspendThreshold,
            StartWithWindows = cfg.StartWithWindows,
            CloseToTray = cfg.CloseToTray,
            DeviceIdMode = cfg.DeviceIdMode,
            // 新增非密文字段也必须登记在此拷贝里，否则永远不落盘（见类注释）
            ClientExePath = cfg.ClientExePath,
            LastAutoRunDate = cfg.LastAutoRunDate,
            ClientActiveAccountId = cfg.ClientActiveAccountId,
            Theme = cfg.Theme,
            EulaAccepted = cfg.EulaAccepted,
            UpdateFeedUrl = cfg.UpdateFeedUrl,
            ProxyMode = cfg.ProxyMode,
            ProxyUrl = cfg.ProxyUrl,
            GatewayEnabled = cfg.GatewayEnabled,
            GatewayPort = cfg.GatewayPort,
            GatewayToken = cfg.GatewayToken,
            GatewayChatEnabled = cfg.GatewayChatEnabled,
            GatewayManualModels = cfg.GatewayManualModels,
            GatewaySeenModels = cfg.GatewaySeenModels,
            ModelOverrides = (cfg.ModelOverrides ?? new List<ModelOverride>()).Select(o => new ModelOverride
            {
                Id = o.Id,
                Display = o.Display,
                Vendor = o.Vendor,
                Rate = o.Rate,
                Efforts = o.Efforts,
                Context = o.Context,
                LongContext = o.LongContext,
                Image = o.Image,
                Hidden = o.Hidden,
            }).ToList(),
            Accounts = cfg.Accounts.Select(a => new Account
            {
                Id = a.Id,
                Name = a.Name,
                Group = a.Group,
                AccountUid = a.AccountUid,
                TokenUpdatedAt = a.TokenUpdatedAt,
                LastCheckinDate = a.LastCheckinDate,
                FailStreak = a.FailStreak,
                SuspendedUntil = a.SuspendedUntil,
                RemainingCredits = a.RemainingCredits,
                Enabled = a.Enabled,
                IsMember = a.IsMember,
                ScreenName = a.ScreenName,
                MobileMasked = a.MobileMasked,
                AvatarUrl = a.AvatarUrl,
                StudentVerified = a.StudentVerified,
                Token = null,
                Session = null,
                DeviceId = "",
            }).ToList(),
        };

        for (int i = 0; i < cfg.Accounts.Count; i++)
        {
            var src = cfg.Accounts[i];
            var dst = copy.Accounts[i];
            foreach (var (_, get, set) in SecretFields)
            {
                var plain = get(src);
                if (string.IsNullOrEmpty(plain)) { set(dst, plain); continue; }
                // 加密失败保留明文（可自愈的降级，好过写 null 丢数据）；
                // DPAPI 仅在换 Windows 用户/重装系统时才会失败，届时解密也会失败并触发重登。
                set(dst, ProtectOrNull(plain) ?? plain);
            }
        }
        return copy;
    }

    private static string? ProtectOrNull(string plain)
    {
        try { return SecretProtector.Protect(plain); }
        catch { return null; }
    }

    /// <summary>加载后解密敏感字段回内存。解密失败（换机/换用户）时保留原值——
    /// 原值若本身就是旧版明文则平滑迁移，若是他人密文则等价于损坏，后续换新会覆盖。</summary>
    private static void UnsealSecrets(AppSettings cfg)
    {
        foreach (var acc in cfg.Accounts)
        {
            foreach (var (_, get, set) in SecretFields)
            {
                var enc = get(acc);
                if (string.IsNullOrEmpty(enc)) continue;
                set(acc, SecretProtector.Unprotect(enc) ?? enc);
            }
        }
    }

    /// <summary>常规化：激活账号回退、间隔限幅、设备号模式非法值回退、模型覆盖项迁移。</summary>
    private static void Normalize(AppSettings cfg)
    {
        MigrateManualModels(cfg);
        if (cfg.Accounts.Count > 0 &&
            (string.IsNullOrEmpty(cfg.ActiveAccountId) ||
             cfg.Accounts.All(a => a.Id != cfg.ActiveAccountId)))
        {
            cfg.ActiveAccountId = cfg.Accounts[0].Id;
        }
        if (cfg.CheckinIntervalSeconds < 1) cfg.CheckinIntervalSeconds = 1;
        if (cfg.CheckinIntervalSeconds > 60) cfg.CheckinIntervalSeconds = 60;
        if (cfg.CheckinFailSuspendThreshold < 1) cfg.CheckinFailSuspendThreshold = AccountHealth.DefaultSuspendThreshold;
        if (cfg.CheckinFailSuspendThreshold > 10) cfg.CheckinFailSuspendThreshold = 10;
        if (cfg.DeviceIdMode != AppSettings.DeviceIdModes.FromTraeClient)
            cfg.DeviceIdMode = AppSettings.DeviceIdModes.Random;
        // 代理模式非法值回退默认；端口限幅到 1024~65535（保留系统端口段）
        if (cfg.ProxyMode is not (AppSettings.ProxyModes.None or AppSettings.ProxyModes.Manual))
            cfg.ProxyMode = AppSettings.ProxyModes.System;
        if (cfg.ProxyMode == AppSettings.ProxyModes.Manual && string.IsNullOrWhiteSpace(cfg.ProxyUrl))
            cfg.ProxyMode = AppSettings.ProxyModes.System;
        if (cfg.GatewayPort < 1024) cfg.GatewayPort = AppSettings.DefaultGatewayPort;
        if (cfg.GatewayPort > 65535) cfg.GatewayPort = 65535;
    }

    /// <summary>
    /// 模型列表第 1 层的形态迁移与清洗：旧版的模型名清单（<see cref="AppSettings.GatewayManualModels"/>）
    /// 转成覆盖项后清空原字段；id 为空的覆盖项（编辑中的残渣）丢弃。
    /// </summary>
    private static void MigrateManualModels(AppSettings cfg)
    {
        cfg.ModelOverrides ??= new List<ModelOverride>();

        var legacy = ModelCatalog.ParseManual(cfg.GatewayManualModels);
        foreach (var name in legacy)
            if (!cfg.ModelOverrides.Any(o => string.Equals(o.Id, name, StringComparison.OrdinalIgnoreCase)))
                cfg.ModelOverrides.Add(new ModelOverride { Id = name });
        if (legacy.Count > 0) cfg.GatewayManualModels = null;

        cfg.ModelOverrides.RemoveAll(o => string.IsNullOrWhiteSpace(o.Id));
    }
}
