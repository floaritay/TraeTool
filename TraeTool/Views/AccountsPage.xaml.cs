using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TraeTool.Views;

public partial class AccountsPage : System.Windows.Controls.UserControl
{
    public AccountsPage()
    {
        InitializeComponent();
    }
}

// —— 页面自带的轻量转换器（放在同文件便于维护）——

/// <summary>bool → Visibility（true 显示）。</summary>
public sealed class BoolToVisConverter : IValueConverter
{
    public static readonly BoolToVisConverter TrueInstance = new() { WhenTrue = Visibility.Visible };
    public static readonly BoolToVisConverter FalseInstance = new() { WhenTrue = Visibility.Collapsed };

    public Visibility WhenTrue { get; set; } = Visibility.Visible;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? WhenTrue : (WhenTrue == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → 取反的 bool（Busy 反转为 IsEnabled）。</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public static readonly InvertBoolConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>非空字符串 → Visible，空 → Collapsed。</summary>
public sealed class NonEmptyConverter : IValueConverter
{
    public static readonly NonEmptyConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
