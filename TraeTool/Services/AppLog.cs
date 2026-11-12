using System.IO;
namespace TraeTool.Services;

/// <summary>
/// 极简文件日志：追加写 %APPDATA%\TraeTool\logs\traetool-yyyyMMdd.log，线程安全。
/// 单日文件超过 <see cref="MaxFileBytes"/> 后轮转为 .1（只留一代），避免长时间运行写爆磁盘。
/// 失败静默（日志永远不该让主流程崩溃）。
/// </summary>
public static class AppLog
{
    /// <summary>单日日志文件大小上限（超限轮转为 .1）。</summary>
    private const long MaxFileBytes = 8 * 1024 * 1024;

    private static readonly object Gate = new();

    public static void Info(string scope, string message)
        => Write(scope, "INFO", message);

    public static void Error(string scope, string message)
        => Write(scope, "ERROR", message);

    private static void Write(string scope, string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{scope}] {message}";
            var path = Path.Combine(Paths.LogsDir, $"traetool-{DateTime.Now:yyyyMMdd}.log");
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.LogsDir);
                RotateIfTooLarge(path);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { /* 日志失败忽略 */ }
    }

    /// <summary>超过上限就把当前文件挪到 .1（覆盖上一代），日志本身不因限额而中断。</summary>
    private static void RotateIfTooLarge(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length < MaxFileBytes) return;
            var rolled = path + ".1";
            if (File.Exists(rolled)) File.Delete(rolled);
            File.Move(path, rolled);
        }
        catch { /* 轮转失败则继续追加原文件 */ }
    }
}
