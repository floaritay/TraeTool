using System.Windows;

namespace TraeTool.Services;

/// <summary>
/// 主题切换：整表替换 App 资源里 Themes/{Light|Dark}.xaml 的合并字典。
/// 所有主题敏感引用必须是 DynamicResource（StaticResource 只在首次解析时取值，换肤不生效）。
/// </summary>
public static class ThemeService
{
    private static ResourceDictionary? _current;

    public static void Apply(string? theme)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        var name = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        var dict = new ResourceDictionary
        {
            Source = new Uri($"Themes/{name}.xaml", UriKind.Relative),
        };

        var mds = app.Resources.MergedDictionaries;
        // 关键：WPF 合并字典**越靠后优先级越高**——必须移除旧主题字典后
        // 把新字典 Add 到末尾，否则会被 App.xaml 里预挂的 Light.xaml 压住。
        for (int i = mds.Count - 1; i >= 0; i--)
        {
            var src = mds[i].Source?.OriginalString ?? "";
            if (src.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase))
                mds.RemoveAt(i);
        }
        mds.Add(dict);
        _current = dict;
    }
}
