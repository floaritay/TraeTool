using System.Drawing;
using Forms = System.Windows.Forms;

namespace TraeTool.Services;

/// <summary>
/// 系统托盘：打开主窗口 / 立即签到 / 退出。
/// 用 WinForms NotifyIcon（csproj 已启用 UseWindowsForms），与 WPF 混用需注意
/// 事件可能来自非 UI 线程，回调统一交给调用方切回 Dispatcher。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;

    public event Action? OpenRequested;
    public event Action? QuickCheckinRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("打开主界面", null, (_, _) => OpenRequested?.Invoke());
        _menu.Items.Add("立即签到", null, (_, _) => QuickCheckinRequested?.Invoke());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Text = "TraeTool 签到助手",
            Icon = LoadIcon(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void ShowBalloon(string title, string text)
    {
        try { _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info); }
        catch { /* 托盘提示失败忽略 */ }
    }

    private static Icon LoadIcon()
    {
        // 优先取 exe 内嵌的 app.ico（csproj ApplicationIcon），系统图标兜底
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ic = Icon.ExtractAssociatedIcon(exe);
                if (ic != null) return ic;
            }
        }
        catch { /* 落到兜底 */ }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
