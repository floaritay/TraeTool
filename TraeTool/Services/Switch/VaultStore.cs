using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TraeTool.Services.Switch;

/// <summary>vault 档案中单个文件的清单项。</summary>
public sealed record VaultEntry(string Rel, long Len, string Sha256Hex);

/// <summary>vault 档案清单（meta.json）。Account 仅为展示名；目录按账号 Id 键控。</summary>
public sealed class VaultMeta
{
    public string Account { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public List<VaultEntry> Entries { get; set; } = new();
}

/// <summary>载体文件 IO 失败（含删除失败）。携带出错的相对路径；调用方必须据此回滚，禁止吞掉。</summary>
public sealed class VaultIoException : Exception
{
    public string Rel { get; }
    public VaultIoException(string rel, string message, Exception? inner = null)
        : base($"[{rel}] {message}", inner) => Rel = rel;
}

/// <summary>
/// 档案 vs live 的相似度报告。
/// 注意：usertag 每次登录会话都会轮换，**不能**做跨会话等值身份判定；
/// 身份只能靠「非噪声稳定文件的相似度」判定（≥75% 阈值）。
/// </summary>
public sealed record SimilarityReport(
    double Score,            // 0..1 = Matched/Compared（仅计非噪声文件）
    int Compared,
    int Matched,
    List<string> Drift,      // 与 live 不一致的文件（展示/日志）
    int IntegrityBad);       // 档案自身与 meta 不符数（损坏）

/// <summary>校验报告 = 完整性 + 相似度 + 登录态存在性（仅存在，不比值）。</summary>
public sealed record VerifyReport(
    bool IdentityKnown,      // live 存在登录态（usertag 可读）
    double Score,
    int Compared,
    int Matched,
    int IntegrityBad,
    List<string> Drift,      // 与 live 不一致的稳定文件
    string LiveIdentity);    // live usertag（仅展示：每次登录都不同）

/// <summary>
/// 登录态档案库：字节级备份/恢复/校验/回写，位于 %APPDATA%\TraeTool\vault\&lt;accountId&gt;\，
/// 内部按数据根相对路径镜像 + meta.json（SHA-256 清单）。
/// 硬约束：
///  - 恢复前删 live code.lock，且无条件删 live state.vscdb-wal/-shm（否则 WAL 回放上一账号）
///  - 目录条目先整删再整树复制；文件先删再拷（防半新半旧）；删除失败抛 VaultIoException 触发回滚
///  - 重建档保留原 CreatedUtc；切换成功后 storage.json 回写（refresh 轮换防 401）
/// </summary>
public sealed class VaultStore
{
    /// <summary>相似度判定阈值（低于此分视为「客户端不是此账号/登录态已变」）。</summary>
    public const double SimilarityPass = 0.75;

    /// <summary>
    /// 建档交叉比对拦截阈值：建档时 live 与**其他账号**已有档案相似度 ≥ 此值
    /// → 警告可能建错档（同会话约 91%、跨会话约 ≤80%，0.85 居中留余量）。
    /// </summary>
    public const double CrossWarnSimilarity = 0.85;

    private static readonly JsonSerializerOptions MetaJson = new() { WriteIndented = true };

    public static string VaultPath(string accountId) => Path.Combine(Paths.VaultDir, accountId);

    public static bool Exists(string accountId) => File.Exists(Path.Combine(VaultPath(accountId), "meta.json"));

    public static VaultMeta? TryReadMeta(string accountId)
    {
        try
        {
            var p = Path.Combine(VaultPath(accountId), "meta.json");
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<VaultMeta>(File.ReadAllText(p), MetaJson);
        }
        catch { return null; }
    }

    // ==================== 建档 ====================

    /// <summary>
    /// 把 live 数据根的 15 项载体（过滤排除目录）备份到 vault\&lt;accountId&gt;\。
    /// 已有档案则刷新 Entries 但保留原 CreatedUtc。
    /// </summary>
    public Task<VaultMeta> BackupAsync(
        string accountId, string accountName, IProgress<string>? log = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var dir = VaultPath(accountId);
            Directory.CreateDirectory(dir);

            var entries = new List<VaultEntry>();
            int files = 0;
            long bytes = 0;
            foreach (var (rel, isDir) in CarrierDefs.Whitelist)
            {
                ct.ThrowIfCancellationRequested();
                var src = CarrierDefs.Abs(rel);
                if (isDir)
                {
                    if (!Directory.Exists(src)) continue;
                    foreach (var file in EnumerateFiltered(src))
                    {
                        ct.ThrowIfCancellationRequested();
                        var fileRel = Path.GetRelativePath(CarrierDefs.DataRoot, file);
                        var dst = Path.Combine(dir, fileRel);
                        CopyFileStrict(file, dst, rel: fileRel);
                        entries.Add(new VaultEntry(fileRel.Replace('\\', '/'),
                            new FileInfo(dst).Length, HashFile(dst)));
                        files++; bytes += entries[^1].Len;
                        log?.Report($"已备份 {fileRel}");
                    }
                }
                else
                {
                    if (!File.Exists(src)) continue;
                    CopyFileStrict(src, Path.Combine(dir, rel), rel);
                    var dst = Path.Combine(dir, rel);
                    entries.Add(new VaultEntry(rel.Replace('\\', '/'),
                        new FileInfo(dst).Length, HashFile(dst)));
                    files++; bytes += entries[^1].Len;
                    log?.Report($"已备份 {rel}");
                }
            }

            var old = TryReadMeta(accountId);
            var meta = new VaultMeta
            {
                Account = accountName,
                CreatedUtc = old?.CreatedUtc ?? DateTime.UtcNow, // 重建档保留原始时间
                Entries = entries,
            };
            File.WriteAllText(Path.Combine(dir, "meta.json"),
                JsonSerializer.Serialize(meta, MetaJson));
            AppLog.Info("switch", $"建档完成 {accountName}: {files} 文件 / {bytes / 1024.0:0} KB");
            return meta;
        }, ct);
    }

    // ==================== 校验 ====================

    /// <summary>
    /// 相似度比对：档案按 meta 重哈希（完整性）后，与 live 逐文件比对，
    /// **排除运行时噪声**（LevelDB 滚动日志/清单、storage.json 等每次运行都会变的文件——
    /// 实测排除后同账号吻合度 ~91%，而精确比对必报"不一致"）。
    /// 同步执行；UI 层放 Task.Run。
    /// </summary>
    public SimilarityReport ScoreAgainstLive(string accountId, IProgress<string>? log = null)
    {
        var meta = TryReadMeta(accountId);
        if (meta == null)
            throw new FileNotFoundException("该账号尚无档案", VaultPath(accountId));

        var dir = VaultPath(accountId);
        int integrityBad = 0, compared = 0, matched = 0;
        var drift = new List<string>();

        foreach (var e in meta.Entries)
        {
            var rel = e.Rel;
            var vaultFile = Path.Combine(dir, rel.Replace('/', '\\'));
            if (!File.Exists(vaultFile))
            {
                integrityBad++;
                log?.Report($"档案缺失: {rel}");
                continue;
            }
            var vh = HashFile(vaultFile);
            if (!string.Equals(vh, e.Sha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                integrityBad++;
                log?.Report($"档案被改动: {rel}");
                continue;
            }

            if (IsCompareNoise(rel)) continue; // 运行时噪声只做完整性、不做 live 一致性

            compared++;
            var liveFile = CarrierDefs.Abs(rel.Replace('/', '\\'));
            string? lh = null;
            try { if (File.Exists(liveFile)) lh = HashFile(liveFile); }
            catch { lh = null; }

            if (lh == null)
            {
                drift.Add(rel + "（live 缺失）");
                log?.Report($"live 缺失: {rel}");
            }
            else if (string.Equals(lh, e.Sha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                matched++;
            }
            else
            {
                drift.Add(rel);
                log?.Report($"与 live 不一致: {rel}");
            }
        }
        double score = compared > 0 ? (double)matched / compared : 0;
        return new SimilarityReport(score, compared, matched, drift, integrityBad);
    }

    /// <summary>
    /// 比对时排除的运行时/噪声文件（实测分类）：
    ///  - runtime 三件：storage.json / config.db / Network Persistent State（每次运行都写）
    ///  - WAL/SHM 侧车（必然随运行变化）
    ///  - LevelDB 滚动噪声：LOG / LOG.old / CURRENT / LOCK / MANIFEST-*
    /// 注意 .log 活动段（如 000639.log）不排除——它是内容文件，靠阈值吸收其漂移。
    /// </summary>
    public static bool IsCompareNoise(string rel)
    {
        var r = rel.Replace('\\', '/').ToLowerInvariant();
        if (r is "user/globalstorage/storage.json"
              or "local storage/config.db"
              or "network/network persistent state")
            return true;
        if (r.EndsWith("-wal", StringComparison.Ordinal) ||
            r.EndsWith("-shm", StringComparison.Ordinal))
            return true;
        var name = r[(r.LastIndexOf('/') + 1)..];
        return name is "log" or "log.old" or "current" or "lock"
               || name.StartsWith("manifest-", StringComparison.Ordinal);
    }

    /// <summary>校验 = 完整性 + 相似度 + 登录态存在性（usertag 只看有无，绝不比值）。</summary>
    public Task<VerifyReport> VerifyAsync(string accountId, IProgress<string>? log = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string? liveId = AuthProbe.TryGetIdentity(CarrierDefs.DataRoot);
            var s = ScoreAgainstLive(accountId, log);
            return new VerifyReport(liveId != null, s.Score, s.Compared, s.Matched, s.IntegrityBad, s.Drift, liveId ?? "");
        }, ct);
    }

    // ==================== 恢复（含回滚复用）====================

    /// <summary>恢复某账号档案到 live。</summary>
    public Task RestoreAsync(string accountId, IProgress<string>? log = null, CancellationToken ct = default)
        => Task.Run(() => RestoreCore(VaultPath(accountId), log, ct), ct);

    /// <summary>把回滚快照目录恢复到 live（Switcher 失败回滚用）。</summary>
    public static Task RestoreFromAsync(string snapshotRoot, IProgress<string>? log = null, CancellationToken ct = default)
        => Task.Run(() => RestoreCore(snapshotRoot, log, ct), ct);

    /// <summary>
    /// 恢复核心：前置清理 → 白名单逐条整删整拷。
    /// 前置：删 code.lock（双候选位置）；**无条件**删 live state.vscdb-wal/-shm——
    /// 客户端被强杀是常态，最新登录可能只在 WAL 里，不删就回放旧账号数据。
    /// 任何删除失败都抛 VaultIoException（禁止吞掉，调用方据已建的快照回滚）。
    /// </summary>
    private static void RestoreCore(string srcRoot, IProgress<string>? log, CancellationToken ct)
    {
        var live = CarrierDefs.DataRoot;

        // ① 单实例锁
        foreach (var lockPath in new[]
                 {
                     Path.Combine(live, "code.lock"),
                     Path.Combine(live, "User", "globalStorage", "code.lock"),
                 })
        {
            TryDeleteFileStrict(lockPath, log);
        }

        // ② WAL/SHM 侧车无条件清除（档案里若也带了会随后按条目写回——与档案同批，属同一快照，一致）
        foreach (var sidecar in new[]
                 {
                     Path.Combine(live, "User", "globalStorage", "state.vscdb-wal"),
                     Path.Combine(live, "User", "globalStorage", "state.vscdb-shm"),
                 })
        {
            TryDeleteFileStrict(sidecar, log);
        }

        // ③ 白名单逐条恢复
        foreach (var (rel, isDir) in CarrierDefs.Whitelist)
        {
            ct.ThrowIfCancellationRequested();
            var src = Path.Combine(srcRoot, rel);
            var dst = Path.Combine(live, rel);

            if (isDir)
            {
                if (!Directory.Exists(src)) continue; // 档案侧没有该条目 → 不动 live（快照同语义）
                log?.Report($"恢复目录 {rel}");
                DeletePathStrict(dst, rel, log);
                Directory.CreateDirectory(dst);
                foreach (var file in EnumerateFiltered(src))
                {
                    ct.ThrowIfCancellationRequested();
                    var fileRel = Path.GetRelativePath(src, file);
                    var target = Path.Combine(dst, fileRel);
                    CopyFileStrict(file, target, Path.Combine(rel, fileRel));
                }
            }
            else
            {
                if (!File.Exists(src)) continue;
                log?.Report($"恢复文件 {rel}");
                DeletePathStrict(dst, rel, log);
                CopyFileStrict(src, dst, rel);
            }
        }
        AppLog.Info("switch", "恢复完成: " + srcRoot);
    }

    // ==================== 回写 ====================

    /// <summary>
    /// 把 live storage.json 回写进档案并更新 meta 该条目（保留 CreatedUtc）。
    /// refresh token 每次切换被服务端轮换——档案不回写则同账号第二次切换必 401。
    /// </summary>
    public Task SyncBackStorageJsonAsync(string accountId)
    {
        return Task.Run(() =>
        {
            const string rel = @"User\globalStorage\storage.json";
            var liveFile = CarrierDefs.Abs(rel);
            if (!File.Exists(liveFile))
            {
                AppLog.Error("switch", "回写失败：live storage.json 不存在");
                return false;
            }

            var dir = VaultPath(accountId);
            var vaultFile = Path.Combine(dir, rel);
            CopyFileStrict(liveFile, vaultFile, rel);

            var meta = TryReadMeta(accountId);
            if (meta != null)
            {
                var entryRel = rel.Replace('\\', '/');
                var idx = meta.Entries.FindIndex(e => e.Rel == entryRel);
                var entry = new VaultEntry(entryRel,
                    new FileInfo(vaultFile).Length, HashFile(vaultFile));
                if (idx >= 0) meta.Entries[idx] = entry;
                else meta.Entries.Add(entry);
                File.WriteAllText(Path.Combine(dir, "meta.json"),
                    JsonSerializer.Serialize(meta, MetaJson));
            }
            AppLog.Info("switch", $"已回写 storage.json 到档案 {accountId[..Math.Min(8, accountId.Length)]}…");
            return true;
        });
    }

    // ==================== 快照（回滚区）====================

    /// <summary>
    /// 把 live 白名单载体（过滤排除目录、不写 meta）复制到快照目录。
    /// Switcher 在动 live 之前调用——这是所有失败路径的回滚依据。
    /// </summary>
    public static Task SnapshotLiveAsync(string destRoot, IProgress<string>? log = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            Directory.CreateDirectory(destRoot);
            foreach (var (rel, isDir) in CarrierDefs.Whitelist)
            {
                ct.ThrowIfCancellationRequested();
                var src = CarrierDefs.Abs(rel);
                if (isDir)
                {
                    if (!Directory.Exists(src)) continue;
                    var dst = Path.Combine(destRoot, rel);
                    Directory.CreateDirectory(dst);
                    foreach (var file in EnumerateFiltered(src))
                    {
                        var fileRel = Path.GetRelativePath(src, file);
                        CopyFileStrict(file, Path.Combine(dst, fileRel), Path.Combine(rel, fileRel));
                    }
                }
                else
                {
                    if (!File.Exists(src)) continue;
                    var target = Path.Combine(destRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    CopyFileStrict(src, target, rel);
                }
            }
            log?.Report("已建立回滚快照");
        }, ct);
    }

    // ==================== 内部工具 ====================

    /// <summary>递归枚举目录内文件，按目录名过滤排除集（任意深度）。</summary>
    private static IEnumerable<string> EnumerateFiltered(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!CarrierDefs.IsExcludedDir(Path.GetFileName(sub)))
                    stack.Push(sub);
            }
            foreach (var f in Directory.EnumerateFiles(dir))
                yield return f;
        }
    }

    /// <summary>拷贝（目标先删再写），失败抛 VaultIoException。</summary>
    private static void CopyFileStrict(string src, string dst, string rel)
    {
        try
        {
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(dst)) File.Delete(dst);
            File.Copy(src, dst, overwrite: false);
        }
        catch (Exception ex) when (ex is not VaultIoException)
        {
            throw new VaultIoException(rel, "复制失败: " + ex.Message, ex);
        }
    }

    /// <summary>删除文件/目录（不存在则跳过），失败抛 VaultIoException —— 绝不吞。</summary>
    private static void DeletePathStrict(string path, string rel, IProgress<string>? log)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                log?.Report($"已删除 {rel}");
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                log?.Report($"已删除目录 {rel}");
            }
        }
        catch (Exception ex)
        {
            throw new VaultIoException(rel, "删除失败: " + ex.Message, ex);
        }
    }

    private static void TryDeleteFileStrict(string path, IProgress<string>? log)
    {
        if (File.Exists(path))
            DeletePathStrict(path, Path.GetFileName(path), log);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
