using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TraeTool.Services;

namespace TraeTool.Views;

/// <summary>
/// WebView2 登录窗（单进程内嵌，参考原版 TraeCheckin 形态）。
/// 每账号独立 CoreWebView2Environment（UserDataFolder = 该账号 profile 目录），
/// 登录态互相隔离；1.5s 轮询读 localStorage['Cloud-IDE-Token']（JWT）与
/// Cookie X-Cloudide-Session，凑齐后关闭窗口并通过 <see cref="Result"/> 交回。
/// </summary>
public partial class LoginWindow : Window
{
    /// <summary>登录结果；用户取消/失败为 null。</summary>
    public LoginResult? Result { get; private set; }

    private readonly string _accountId;
    private readonly string? _initialToken;
    private readonly DispatcherTimer _pollTimer;
    private bool _obtained;

    public sealed record LoginResult(string Token, string? Session);

    public LoginWindow(string accountId, string? initialToken)
    {
        InitializeComponent();
        _accountId = accountId;
        _initialToken = initialToken;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _pollTimer.Tick += async (_, _) => await TryReadTokenAsync();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataDir = Paths.WebViewProfileDir(_accountId);
            Directory.CreateDirectory(userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await WebView.EnsureCoreWebView2Async(env);
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess) _ = TryReadTokenAsync();
            };
            WebView.CoreWebView2.Navigate("https://www.trae.cn/dashboard#usage");
            _pollTimer.Start();
            StatusText.Text = "请完成登录…";
        }
        catch (Exception ex)
        {
            AppLog.Error("login", "WebView2 初始化失败: " + ex.Message);
            StatusText.Text = "初始化浏览器失败：" + ex.Message;
            // WPF/WinForms 双命名空间同时生效（csproj UseWindowsForms），MessageBox/MessageBoxImage 需全限定
            System.Windows.MessageBox.Show(this, "初始化浏览器失败：" + ex.Message, "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Result = null;
            Close();
        }
    }

    private async Task TryReadTokenAsync()
    {
        if (_obtained || WebView.CoreWebView2 == null) return;
        try
        {
            var raw = await WebView.CoreWebView2.ExecuteScriptAsync(
                "(function(){try{var t=localStorage.getItem('Cloud-IDE-Token');return t?t:'';}catch(e){return '';}})()");
            var token = raw.Trim('"');

            // 仅接受真正重新登录产生的新 token（长度门槛 + 与初始 token 不同），
            // 避免把 localStorage 中残留的失效旧 token 当成登录成功。
            if (string.IsNullOrEmpty(token) || token.Length < 40 ||
                string.Equals(token, _initialToken, StringComparison.Ordinal))
            {
                return;
            }

            string? session = null;
            try
            {
                var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.trae.cn/");
                foreach (var c in cookies)
                {
                    if (c.Name == "X-Cloudide-Session") { session = c.Value; break; }
                }
            }
            catch (Exception ex)
            {
                AppLog.Info("login", "读取 Session Cookie 失败（不影响 Token）: " + ex.Message);
            }

            _obtained = true;
            _pollTimer.Stop();
            Result = new LoginResult(token, session);
            AppLog.Info("login", $"获取 Token（len={token.Length}，session={(session == null ? "无" : "有")}）");
            Close();
        }
        catch
        {
            // 页面尚未就绪，等下个轮询周期
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer.Stop();
        base.OnClosed(e);
    }
}
