using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TraeTool.Views;

public partial class CheckinPage : System.Windows.Controls.UserControl
{
    public CheckinPage()
    {
        InitializeComponent();
    }
}

/// <summary>0 → Visible（"暂无记录"），非 0 → Collapsed。</summary>
public sealed class ZeroToVisConverter : IValueConverter
{
    public static readonly ZeroToVisConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
