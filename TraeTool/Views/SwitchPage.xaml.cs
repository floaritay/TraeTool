using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TraeTool.Views;

public partial class SwitchPage : System.Windows.Controls.UserControl
{
    public SwitchPage()
    {
        InitializeComponent();
    }
}

/// <summary>档案状态文字色：已备份 → 绿；无备份 → 灰。</summary>
public sealed class ArchiveStateColorConverter : IValueConverter
{
    public static readonly ArchiveStateColorConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s == "已备份"
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
