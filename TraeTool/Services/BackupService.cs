using System.IO;
using System.IO.Compression;
using System.Text.Json;
using TraeTool.Models;

namespace TraeTool.Services;

/// <summary>
/// 备份导出/导入。
/// 导出 = config.json（DPAPI 密文原样）+ checkin.db（VACUUM INTO 快照）+ usage_*.jsonl → zip。
/// 导入 = 校验 → 危险确认 → 暂存到 %APPDATA%\TraeTool\import_staging\ →
///        下次启动在 Database.Open/Config.Load 之前覆盖落位（避免运行中替换被锁文件）。
/// </summary>
public static class BackupService
{
    /// <summary>导出到指定 zip 路径；成功返回该路径，失败 error 带原因。</summary>
    public static string? ExportTo(string zipPath, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(Paths.ConfigFile))
            {
                error = "还没有可导出的配置";
                return null;
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);

            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(Paths.ConfigFile, "config.json");

            // 签到库在线快照（避免直接拷被 SQLite 持有的 WAL 不一致文件）
            var tmpDb = Path.Combine(Path.GetTempPath(),
                "traetool_export_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var conn = Database.CreateConnection())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "VACUUM INTO '" + tmpDb.Replace("'", "''") + "'";
                    cmd.ExecuteNonQuery();
                }
                zip.CreateEntryFromFile(tmpDb, "data/checkin.db");
            }
            finally
            {
                if (File.Exists(tmpDb)) File.Delete(tmpDb);
            }

            if (Directory.Exists(Paths.DataDir))
            {
                foreach (var f in Directory.GetFiles(Paths.DataDir, "usage_*.jsonl"))
                    zip.CreateEntryFromFile(f, "data/" + Path.GetFileName(f));
            }

            AppLog.Info("backup", "导出备份: " + zipPath);
            return zipPath;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Error("backup", "导出失败: " + ex);
            return null;
        }
    }

    /// <summary>
    /// 校验导入包并暂存（尚未覆盖任何现有数据）。返回 true=暂存成功。
    /// 校验点：zip 内有 config.json 且能反序列化为 AppSettings。
    /// </summary>
    public static bool StageImport(string zipPath, out string? error)
    {
        error = null;
        var staging = Path.Combine(Paths.Root, "import_staging");
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            var tmp = Path.Combine(Path.GetTempPath(),
                "traetool_import_" + Guid.NewGuid().ToString("N"));
            ZipFile.ExtractToDirectory(zipPath, tmp);

            var cfgFile = Path.Combine(tmp, "config.json");
            if (!File.Exists(cfgFile))
            {
                error = "备份包里没有 config.json";
                TryDelete(tmp);
                return false;
            }
            var cfg = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(cfgFile), new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
                });
            if (cfg == null || cfg.Accounts == null)
            {
                error = "config.json 无法解析（不是有效的 TraeTool 备份）";
                TryDelete(tmp);
                return false;
            }

            Directory.CreateDirectory(staging);
            // 平移进 staging（保留相对结构：config.json / data/...）
            foreach (var entry in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(tmp, entry);
                var dest = Path.Combine(staging, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(entry, dest, overwrite: true);
            }
            TryDelete(tmp);
            AppLog.Info("backup", $"导入包已校验并暂存（{cfg.Accounts.Count} 个账号），等待重启应用");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Error("backup", "暂存导入包失败: " + ex);
            TryDelete(staging);
            return false;
        }
    }

    /// <summary>
    /// 启动期应用暂存（必须在 Database.Open 与 ConfigStore.Load 之前调用）：
    /// 把 import_staging 下的文件覆盖到应用目录，旧配置留 .bak，旧库留 .preimport，然后删除 staging。
    /// </summary>
    public static void ApplyImportStaging()
    {
        var staging = Path.Combine(Paths.Root, "import_staging");
        if (!Directory.Exists(staging)) return;
        try
        {
            foreach (var src in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(staging, src);
                var dest = Path.Combine(Paths.Root, rel);
                if (File.Exists(dest))
                {
                    if (string.Equals(rel, "config.json", StringComparison.OrdinalIgnoreCase))
                        File.Copy(dest, Paths.ConfigFile + ".bak", overwrite: true);
                    else
                        File.Copy(dest, dest + ".preimport", overwrite: true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: true);
            }
            Directory.Delete(staging, recursive: true);
            AppLog.Info("backup", "导入备份已应用：配置与数据已覆盖（重启完成）");
        }
        catch (Exception ex)
        {
            AppLog.Error("backup", "应用导入暂存失败: " + ex);
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 清理失败无害 */ }
    }
}
