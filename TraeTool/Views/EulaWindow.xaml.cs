using System.Windows;

namespace TraeTool.Views;

/// <summary>首启用户协议/免责声明（同意返回 true；拒绝/关闭返回 false）。</summary>
public partial class EulaWindow : Window
{
    private bool _accepted;

    private EulaWindow()
    {
        InitializeComponent();
    }

    /// <summary>模态展示；true = 同意继续。</summary>
    public static bool ShowAccepted()
    {
        var w = new EulaWindow();
        w.ShowDialog();
        return w._accepted;
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        _accepted = true;
        Close();
    }

    private void OnDeclineClick(object sender, RoutedEventArgs e) => Close();
}
