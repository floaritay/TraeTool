using System.IO;
namespace TraeTool.Services;

/// <summary>
/// 极简文件日志：追加写 %APPDATA%\TraeTool\logs\traetool-yyyyMMdd.log，线程安全。
/// 一期用它观察 API 请求结果与批次执行过程；失败静默（日志永远不该让主流程崩溃）。
/// </summary>
public static class AppLog
{
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
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { /* 日志失败忽略 */ }
    }
}
