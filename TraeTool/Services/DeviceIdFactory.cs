using System.IO;
using System.Text.Json;

namespace TraeTool.Services;

/// <summary>
/// x-device-id 生成与读取。
/// 硬约束（服务端风控）：
///   - 必须是纯数字（Aha 设备号格式），GUID/UUID 会触发 9074「参与用户太多」；
///   - 服务端按 device-id 每日去重 claim → 多账号共用同一设备号时只有第一个能签到，
///     所以每个账号必须持有互不重复的设备号。
/// </summary>
public static class DeviceIdFactory
{
    /// <summary>生成 16 位数字设备号（1e15 ~ 1e16-1）。</summary>
    public static string Generate()
        => Random.Shared.NextInt64(1_000_000_000_000_000L, 10_000_000_000_000_000L).ToString();

    /// <summary>生成与给定集合内现有设备号都不重复的新设备号。</summary>
    public static string GenerateUnique(IEnumerable<string?> used)
    {
        var set = new HashSet<string>(used.Where(d => !string.IsNullOrWhiteSpace(d))!, StringComparer.Ordinal);
        string id;
        do { id = Generate(); }
        while (set.Contains(id));
        return id;
    }

    /// <summary>
    /// 从本机 Trae 桌面客户端数据目录读取真实 Aha 设备号（storage.json 键
    /// <c>iCubeAuthInfo://icube-dc:&lt;digits&gt;</c>）。目录顺序：现役 Trae CN 优先。
    /// 读不到返回 null。
    /// </summary>
    public static string? TryReadFromTraeClient()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var dir in new[] { "Trae CN", "TRAE SOLO CN", "TRAE SOLO" })
        {
            var path = Path.Combine(appData, dir, "User", "globalStorage", "storage.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    const string prefix = "iCubeAuthInfo://icube-dc:";
                    if (!prop.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var id = prop.Name.Substring(prefix.Length);
                    if (id.Length >= 8 && id.All(char.IsDigit)) return id;
                }
            }
            catch { /* 文件损坏/占用则试下一个目录 */ }
        }
        return null;
    }
}
