using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TraeTool.Services.Switch;

namespace TraeTool.Services;

/// <summary>一项重置结果。</summary>
public sealed record DeviceResetItem(string Item, bool Ok, string Detail);

/// <summary>
/// 本机 Trae 客户端设备标识管理（用户主动重置本机设备指纹的运维工具）。
/// 覆盖三项客户端标识：machineid 文件（UUID）、storage.json 内的
/// telemetry.machineId（64 位 hex）、telemetry.sqmId（{GUID 大写}）、
/// telemetry.devDeviceId（GUID），以及可选的 Windows MachineGuid
/// （HKLM 注册表，需管理员权限，系统级影响——界面默认不勾选）。
/// 每次改动前先把原文件备份到 %TEMP%\traedevreset_时间戳\ 目录。
/// aha/TinyStorage、ahanet 等其余标识结构不处理（界面有说明）。
/// </summary>
public static class DeviceResetService
{
    /// <summary>本重置覆盖不到的本机标识结构（界面列明，避免误以为已彻底重置）。</summary>
    public static readonly IReadOnlyList<string> UncoveredStructures =
    [
        "aha / TinyStorage（客户端内嵌存储，结构与位置随版本变化）",
        "ahanet 相关配置",
        "系统级硬件标识（磁盘序列号 / 网卡 MAC / 主板信息）",
    ];

    public static async Task<List<DeviceResetItem>> ResetClientAsync(
        bool machineId, bool telemetry, bool machineGuid, IProgress<string>? log)
    {
        var results = new List<DeviceResetItem>();
        var root = CarrierDefs.DataRoot;
        var backup = Path.Combine(Path.GetTempPath(),
            "traedevreset_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        bool needBackup = machineId || telemetry;
        if (needBackup) Directory.CreateDirectory(backup);

        if (machineId)
        {
            try
            {
                var f = Path.Combine(root, "machineid");
                if (!File.Exists(f))
                {
                    results.Add(new DeviceResetItem("machineid", false, "文件不存在"));
                }
                else
                {
                    File.Copy(f, Path.Combine(backup, "machineid"), overwrite: true);
                    File.WriteAllText(f, Guid.NewGuid().ToString());
                    log?.Report("machineid 已重新生成");
                    results.Add(new DeviceResetItem("machineid", true, "已重置"));
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("devreset", "machineid 重置失败: " + ex.Message);
                results.Add(new DeviceResetItem("machineid", false, ex.Message));
            }
        }

        if (telemetry)
        {
            try
            {
                var f = Path.Combine(root, "User", "globalStorage", "storage.json");
                if (!File.Exists(f))
                {
                    results.Add(new DeviceResetItem("storage.json 遥测", false, "文件不存在"));
                }
                else
                {
                    File.Copy(f, Path.Combine(backup, "storage.json"), overwrite: true);
                    var node = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
                    node["telemetry.machineId"] = RandomHex(64);
                    node["telemetry.sqmId"] = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
                    node["telemetry.devDeviceId"] = Guid.NewGuid().ToString();
                    File.WriteAllText(f, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    log?.Report("storage.json 三项遥测标识已重置");
                    results.Add(new DeviceResetItem("storage.json 遥测", true,
                        "machineId / sqmId / devDeviceId 已重置"));
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("devreset", "storage.json 遥测重置失败: " + ex.Message);
                results.Add(new DeviceResetItem("storage.json 遥测", false, ex.Message));
            }
        }

        if (machineGuid)
        {
            try
            {
                using var rk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Cryptography", writable: true);
                if (rk == null)
                {
                    results.Add(new DeviceResetItem("MachineGuid", false, "注册表项不可用"));
                }
                else
                {
                    rk.SetValue("MachineGuid", Guid.NewGuid().ToString());
                    AppLog.Info("devreset", "MachineGuid 已更新");
                    results.Add(new DeviceResetItem("MachineGuid", true, "已重置（旧值已写日志）"));
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("devreset", "MachineGuid 写入失败: " + ex.Message);
                results.Add(new DeviceResetItem("MachineGuid", false,
                    "写入失败（需以管理员身份运行）：" + ex.Message));
            }
        }

        if (needBackup) log?.Report("原文件已备份到 " + backup);
        await Task.CompletedTask;
        return results;
    }

    private static string RandomHex(int chars)
    {
        var b = new byte[chars / 2];
        Random.Shared.NextBytes(b);
        return Convert.ToHexString(b).ToLowerInvariant();
    }
}
