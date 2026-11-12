using System.IO;
using System.IO.Compression;
using System.Text.Json;
using TraeTool.Models;

namespace TraeTool.Services;

/// <summary>
/// 备份导出/导入。
/// 导出 = config.json（DPAPI 密文原样）+ checkin.db（VACUUM INTO 快照）+ usage_*.jsonl → zip；
///        给出可选口令时整体加密封装（BackupCrypto，AES-256-GCM + PBKDF2），跨机器可用该口令恢复。
/// 导入 = 校验 → 危险确认 → 暂存到 %APPDATA%\TraeTool\import_staging\ →
///        下次启动在 Database.Open/Config.Load 之前覆盖落位（避免运行中替换被锁文件）。
/// </summary>
public static class BackupService
{
    /// <summary>
    /// 导出到指定路径；password 非空时加密封装。
    /// 成功返回该路径，失败 error 带原因。
    /// </summary>
    public static string? ExportTo(string zipPath, out string? error, string? password = null)
    {
        error = null;
        var tmpZip = Path.Combine(Path.GetTempPath(),
            "traetool_export_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            if (!File.Exists(Paths.ConfigFile))
            {
                error = "还没有可导出的配置";
                return null;
            }

            using (var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
            {
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
            }

            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (!string.IsNullOrEmpty(password))
            {
                File.WriteAllBytes(zipPath, BackupCrypto.Encrypt(File.ReadAllBytes(tmpZip), password));
                AppLog.Info("backup", "导出加密备份: " + zipPath);
            }
            else
            {
                File.Copy(tmpZip, zipPath, overwrite: true);
                AppLog.Info("backup", "导出备份（未加密，仅本机同账户可还原）: " + zipPath);
            }
            return zipPath;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Error("backup", "导出失败: " + ex);
            return null;
        }
        finally
        {
            FileSystemUtil.TryDeleteFile(tmpZip);
        }
    }

    /// <summary>该备份文件是否为口令加密封装。</summary>
    public static bool IsEncryptedBackup(string path) => BackupCrypto.IsEncryptedFile(path);

    /// <summary>
    /// 校验导入包并暂存（尚未覆盖任何现有数据）。加密包必须提供正确口令。
    /// 校验点：可解开容器（若加密）且内含可反序列化的 config.json。
    /// </summary>
    public static bool StageImport(string zipPath, out string? error, string? password = null)
    {
        error = null;
        var staging = Path.Combine(Paths.Root, "import_staging");
        string? decryptedZip = null;
        try
        {
            var source = zipPath;
            if (BackupCrypto.IsEncryptedFile(zipPath))
            {
                if (string.IsNullOrEmpty(password))
                {
                    error = "该备份已加密，请填写导出时设置的口令";
                    return false;
                }
                try
                {
                    decryptedZip = Path.Combine(Path.GetTempPath(),
                        "traetool_import_" + Guid.NewGuid().ToString("N") + ".zip");
                    File.WriteAllBytes(decryptedZip, BackupCrypto.Decrypt(File.ReadAllBytes(zipPath), password));
                }
                catch (Exception ex)
                {
                    AppLog.Error("backup", "备份解密失败: " + ex.Message);
                    error = "口令错误或备份损坏，无法解密";
                    return false;
                }
                source = decryptedZip;
            }

            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            var tmp = Path.Combine(Path.GetTempPath(),
                "traetool_import_" + Guid.NewGuid().ToString("N"));
            try
            {
                ZipFile.ExtractToDirectory(source, tmp);

                var cfgFile = Path.Combine(tmp, "config.json");
                if (!File.Exists(cfgFile))
                {
                    error = "备份包里没有 config.json";
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
                    return false;
                }

                Directory.CreateDirectory(staging);
                // 平移进 staging（保留相对结构：config.json / data/...）；白名单外的条目丢弃
                int skipped = 0;
                foreach (var entry in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(tmp, entry);
                    if (!IsImportAllowed(rel)) { skipped++; continue; }
                    var dest = Path.Combine(staging, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(entry, dest, overwrite: true);
                }
                if (skipped > 0)
                    AppLog.Info("backup", $"导入包里有 {skipped} 个非备份条目已忽略");
                AppLog.Info("backup", $"导入包已校验并暂存（{cfg.Accounts.Count} 个账号），等待重启应用");
                return true;
            }
            finally
            {
                FileSystemUtil.TryDeleteDir(tmp);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Error("backup", "暂存导入包失败: " + ex);
            FileSystemUtil.TryDeleteDir(staging);
            return false;
        }
        finally
        {
            if (decryptedZip != null) FileSystemUtil.TryDeleteFile(decryptedZip);
        }
    }

    /// <summary>
    /// 启动期应用暂存（必须在 Database.Open 与 ConfigStore.Load 之前调用）：
    /// 把 import_staging 下的文件逐个移入应用目录——移走即标记已应用，中断后重跑只处理剩余文件；
    /// config.json 最后覆盖，覆盖前旧配置留 .bak，其余文件留 .preimport。全部成功后删除 staging。
    /// </summary>
    public static void ApplyImportStaging()
    {
        var staging = Path.Combine(Paths.Root, "import_staging");
        if (!Directory.Exists(staging)) return;
        try
        {
            // config.json 最后处理：数据文件全部就位后才替换配置（.bak 只在首次应用时生成）
            var files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories)
                .OrderBy(f => string.Equals(Path.GetRelativePath(staging, f), "config.json",
                    StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ToList();
            foreach (var src in files)
            {
                var rel = Path.GetRelativePath(staging, src);
                if (!IsImportAllowed(rel))
                {
                    AppLog.Error("backup", "跳过非备份条目: " + rel);
                    continue;
                }
                var dest = Path.Combine(Paths.Root, rel);
                if (File.Exists(dest))
                {
                    if (string.Equals(rel, "config.json", StringComparison.OrdinalIgnoreCase))
                        File.Copy(dest, Paths.ConfigFile + ".bak", overwrite: true);
                    else
                        File.Copy(dest, dest + ".preimport", overwrite: true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(src, dest, overwrite: true);
            }
            Directory.Delete(staging, recursive: true);
            AppLog.Info("backup", "导入备份已应用：配置与数据已覆盖（重启完成）");
        }
        catch (Exception ex)
        {
            AppLog.Error("backup", "应用导入暂存失败: " + ex);
        }
    }

    /// <summary>
    /// 导入包允许落位的条目，与 <see cref="ExportTo"/> 写出的内容一致：
    /// <c>config.json</c>、<c>data/checkin.db</c>、<c>data/usage_*.jsonl</c>。
    /// 备份包是外部输入，没有白名单就等于允许它往数据目录写任意文件。
    /// </summary>
    private static bool IsImportAllowed(string rel)
    {
        if (string.Equals(rel, "config.json", StringComparison.OrdinalIgnoreCase)) return true;
        var norm = rel.Replace('\\', '/');
        if (!norm.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) return false;
        if (norm.IndexOf('/', "data/".Length) >= 0) return false;   // 不允许 data/ 下再套目录
        var name = norm["data/".Length..];
        return string.Equals(name, "checkin.db", StringComparison.OrdinalIgnoreCase)
               || (name.StartsWith("usage_", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));
    }
}
