using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TraeTool.ViewModels;

namespace TraeTool.Views;

public partial class DashboardPage : System.Windows.Controls.UserControl
{
    public DashboardPage()
    {
        InitializeComponent();
    }

    /// <summary>账号行点击 → SelectAccount（CommandParameter 传该行 Id）。</summary>
    private void OnAccountRowClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DashAccountRow row &&
            DataContext is DashboardViewModel vm)
        {
            vm.SelectAccountCommand.Execute(row.Id);
        }
    }

    /// <summary>趋势图画布尺寸变化 → VM 按实际宽度重算折线（原固定 520 已废弃）。</summary>
    private void OnTrendSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm && e.NewSize.Width > 60)
            vm.SetChartSize(e.NewSize.Width, e.NewSize.Height);
    }
}

/// <summary>true → Collapsed，false → Visible（HasTrend 反转显示用）。</summary>
public sealed class InvertBoolToVisConverter : System.Windows.Data.IValueConverter
{
    public static readonly InvertBoolToVisConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
