using Microsoft.Win32;

namespace TraeTool.Services;

/// <summary>开机自启动：HKCU\Software\Microsoft\Windows\CurrentVersion\Run 注册表键。</summary>
public static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TraeTool";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? throw new InvalidOperationException("无法打开注册表 Run 键");
            if (enabled)
            {
                var exe = Environment.ProcessPath
                          ?? throw new InvalidOperationException("无法定位可执行文件路径");
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("autostart", $"设置自启失败(enabled={enabled}): {ex.Message}");
            throw;
        }
    }
}
