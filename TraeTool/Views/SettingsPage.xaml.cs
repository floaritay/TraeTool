using System.Diagnostics;
using System.IO;
using System.Windows;
using TraeTool.Services;

namespace TraeTool.Views;

/// <summary>设置页（P6）：行为开关绑定 AccountsPageViewModel（改动即落盘）+ 外观/Token/更新/日志 + 关于。</summary>
public partial class SettingsPage : System.Windows.Controls.UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadLog();
    }

    /// <summary>读今天日志的最后 150 行（纯展示，异常兜底）。</summary>
    private void LoadLog()
    {
        try
        {
            var path = Path.Combine(Paths.LogsDir, $"traetool-{DateTime.Now:yyyyMMdd}.log");
            if (!File.Exists(path))
            {
                LogTextBlock.Text = "今天还没有日志：" + path;
                return;
            }
            var lines = File.ReadAllLines(path);
            var tail = lines.Skip(Math.Max(0, lines.Length - 150)).ToList();
            LogTextBlock.Text = string.Join(Environment.NewLine, tail);
            LogScroll.ScrollToEnd();
            LogMetaText.Text = $"尾 {tail.Count} 行 · 已刷新 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            LogTextBlock.Text = "读取日志失败：" + ex.Message;
            LogMetaText.Text = "读取失败";
        }
    }

    private void OnLogRefreshClick(object sender, RoutedEventArgs e) => LoadLog();

    private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Paths.LogsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.LogsDir}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogTextBlock.Text = "打开日志文件夹失败：" + ex.Message;
        }
    }
}
