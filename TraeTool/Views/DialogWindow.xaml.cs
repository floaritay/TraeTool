using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TraeTool.Services;
using Application = System.Windows.Application;      // WinForms 隐式 using 冲突坑
using IconsData = TraeTool.Resources.Icons;          // FrameworkElement.Resources 实例属性遮蔽

namespace TraeTool.Views;

/// <summary>对话框类型（决定图标与主按钮配色）。</summary>
public enum DialogKind { Info, Warn, Danger }

/// <summary>
/// 统一样式对话框（G5）：替代原生 MessageBox，支持图标、后果小字、主/次按钮。
/// </summary>
public partial class DialogWindow : Window
{
    private bool _primaryClicked;

    private DialogWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 显示对话框（模态）。返回 true = 点了主按钮。
    /// small：主按钮下的后果重申小字（10.4d）；secondary 为空则只有主按钮。
    /// </summary>
    public static bool Show(
        string title,
        string message,
        DialogKind kind = DialogKind.Info,
        string? small = null,
        string primary = "确定",
        string? secondary = null,
        bool dangerPrimary = false,
        Window? owner = null)
    {
        bool result = false;
        void Run()
        {
            try
            {
                RunCore();
            }
            catch (Exception ex)
            {
                AppLog.Error("dialog", $"对话框异常（{title}）: " + ex);
                throw;
            }
        }

        void RunCore()
        {
            var w = new DialogWindow
            {
                Owner = owner ?? Application.Current?.MainWindow,
            };
            w.TitleText.Text = title;
            w.MessageText.Text = message;
            if (!string.IsNullOrEmpty(small))
            {
                w.SmallText.Text = small;
                w.SmallText.Visibility = Visibility.Visible;
            }

            switch (kind)
            {
                case DialogKind.Warn:
                    w.IconCircle.Background = Get("WarnSoftBrush");
                    w.IconPath.Data = IconsData.Alert;
                    w.IconPath.Stroke = Get("WarnBrush");
                    break;
                case DialogKind.Danger:
                    w.IconCircle.Background = Get("DangerSoftBrush");
                    w.IconPath.Data = IconsData.Alert;
                    w.IconPath.Stroke = Get("DangerBrush");
                    break;
                default:
                    w.IconCircle.Background = Get("PrimarySoftBrush");
                    w.IconPath.Data = IconsData.Info;
                    w.IconPath.Stroke = Get("PrimaryBrush");
                    break;
            }

            w.PrimaryButton.Content = primary;
            if (dangerPrimary)
                w.PrimaryButton.Style = (Style)Application.Current.FindResource("DangerButton")!;
            if (secondary != null)
            {
                w.SecondaryButton.Content = secondary;
                w.SecondaryButton.Visibility = Visibility.Visible;
            }

            w.ShowDialog();
            result = w._primaryClicked;
        }

        var app = Application.Current;
        if (app == null || app.Dispatcher.CheckAccess()) Run();
        else app.Dispatcher.Invoke(Run);
        return result;
    }

    private static SolidColorBrush Get(string key) =>
        (SolidColorBrush)Application.Current.FindResource(key);

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        _primaryClicked = true;
        Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PrimaryButton.IsEnabled)
        {
            _primaryClicked = true;
            Close();
        }
        else if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
