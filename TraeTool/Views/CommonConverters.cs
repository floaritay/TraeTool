using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TraeTool.Views;

/// <summary>null 或空串 → Visible（输入框占位提示文字）。</summary>
public sealed class NullOrEmptyToVisConverter : IValueConverter
{
    public static readonly NullOrEmptyToVisConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not string s || s.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>非 null → Visible（头像图加载成功后显示）。</summary>
public sealed class NotNullToVisConverter : IValueConverter
{
    public static readonly NotNullToVisConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null → Visible（无头像时显示首字兜底）。</summary>
public sealed class NullToVisConverter : IValueConverter
{
    public static readonly NullToVisConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>ImageSource → ImageBrush（椭圆头像填充用）。</summary>
public sealed class ImageToBrushConverter : IValueConverter
{
    public static readonly ImageToBrushConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ImageSource src)
        {
            var brush = new ImageBrush(src);
            brush.Freeze();
            return brush;
        }
        return System.Windows.Media.Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 头像色相（double 0~359）→ HSL(46%, 44%) 纯色刷（与 UI 原型 .avatar 同配比）。
/// </summary>
public sealed class HueToBrushConverter : IValueConverter
{
    public static readonly HueToBrushConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double hue = value is double d ? d : 210;
        var brush = new SolidColorBrush(HslToRgb((hue % 360 + 360) % 360, 0.46, 0.44));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static System.Windows.Media.Color HslToRgb(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hp = h / 60;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        double r1, g1, b1;
        if (hp < 1) { r1 = c; g1 = x; b1 = 0; }
        else if (hp < 2) { r1 = x; g1 = c; b1 = 0; }
        else if (hp < 3) { r1 = 0; g1 = c; b1 = x; }
        else if (hp < 4) { r1 = 0; g1 = x; b1 = c; }
        else if (hp < 5) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }
        double m = l - c / 2;
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round((r1 + m) * 255),
            (byte)Math.Round((g1 + m) * 255),
            (byte)Math.Round((b1 + m) * 255));
    }
}
