using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TraeTool.Services;
using TraeTool.ViewModels;
using UserControl = System.Windows.Controls.UserControl; // 与 WinForms UserControl 冲突，需限定

namespace TraeTool.Views;

/// <summary>
/// 主窗口：自绘标题栏 + 左栏六页导航 + 关窗收托盘（CloseToTray 开启且非主动退出时）。
/// 六页同时驻留，切换 Visibility 保留页面状态。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>启动页（--page=checkin/accounts/switch/usage/proxy/gateway/settings，默认 dashboard）；截图/自动化用。</summary>
    public static string StartupPage = "dashboard";

    private readonly DashboardPage _dashboardPage = new();
    private readonly CheckinPage _checkinPage = new();
    private readonly AccountsPage _accountsPage = new();
    private readonly SwitchPage _switchPage = new();
    private readonly UsagePage _usagePage = new();
    private readonly ProxyPage _proxyPage = new();
    private readonly GatewayPage _gatewayPage = new();
    private readonly SettingsPage _settingsPage = new();

    private readonly UserControl[] _pages;

    public MainWindow()
    {
        InitializeComponent();
        _pages = [_dashboardPage, _checkinPage, _accountsPage, _switchPage, _usagePage,
                  _proxyPage, _gatewayPage, _settingsPage];
        // 默认页由 StartupPage 决定（其余折叠）
        var defaultPage = StartupPage switch
        {
            "checkin" => (UserControl)_checkinPage,
            "accounts" => _accountsPage,
            "switch" => _switchPage,
            "usage" => _usagePage,
            "proxy" => _proxyPage,
            "gateway" => _gatewayPage,
            "settings" => _settingsPage,
            _ => _dashboardPage,
        };
        foreach (var p in _pages)
            p.Visibility = p == defaultPage ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in _pages)
            PageHost.Children.Add(p);

        // Loaded 后再勾选导航（Checked 处理器有 IsLoaded 门闩，ctor 阶段触发会被忽略）
        Loaded += (_, _) =>
        {
            switch (StartupPage)
            {
                case "checkin": NavCheckin.IsChecked = true; break;
                case "accounts": NavAccounts.IsChecked = true; break;
                case "switch": NavSwitch.IsChecked = true; break;
                case "usage": NavUsage.IsChecked = true; break;
                case "proxy": NavProxy.IsChecked = true; break;
                case "gateway": NavGateway.IsChecked = true; break;
                case "settings": NavSettings.IsChecked = true; break;
            }
        };

        // DataContext 由 App 在构造后注入，用事件而非虚方法（WPF 无 OnDataContextChanged 虚方法）
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                _dashboardPage.DataContext = vm.Dashboard;
                _checkinPage.DataContext = vm.Checkin;
                _accountsPage.DataContext = vm.Accounts;
                _switchPage.DataContext = vm.Switch;
                _usagePage.DataContext = vm.Usage;
                _proxyPage.DataContext = vm.Settings;
                _gatewayPage.DataContext = vm.Settings;
                _settingsPage.DataContext = vm.Settings;
            }
        };
    }

    /// <summary>六页互斥显示。</summary>
    private void ShowOnly(UserControl page)
    {
        foreach (var p in _pages)
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavDashboard(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppLog.Info("nav", "切到仪表盘页");
        ShowOnly(_dashboardPage);
        (DataContext as MainViewModel)?.Dashboard.OnNavEnter();
    }

    private void OnNavAccounts(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppLog.Info("nav", "切到账号管理页");
        ShowOnly(_accountsPage);
    }

    private void OnNavCheckin(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppLog.Info("nav", "切到每日签到页");
        ShowOnly(_checkinPage);
        (DataContext as MainViewModel)?.Checkin.OnAccountsChanged();
    }

    private void OnNavSwitch(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppLog.Info("nav", "切到客户端切换页");
        ShowOnly(_switchPage);
        (DataContext as MainViewModel)?.Switch.OnNavEnter();
    }

    private void OnNavUsage(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppLog.Info("nav", "切到用量统计页");
        ShowOnly(_usagePage);
        (DataContext as MainViewModel)?.Usage.OnNavEnter();
    }

    private void OnNavProxy(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppLog.Info("nav", "切到出站代理页");
        ShowOnly(_proxyPage);
    }

    private void OnNavGateway(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppLog.Info("nav", "切到本机网关页");
        ShowOnly(_gatewayPage);
        (DataContext as MainViewModel)?.Settings.EnsureModelListSynced();
    }

    private void OnNavSettings(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppLog.Info("nav", "切到设置页");
        ShowOnly(_settingsPage);
        (DataContext as MainViewModel)?.Settings.EnsureModelListSynced();
    }

    // ---- 标题栏窗口按钮 ----

    private void OnMinClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        var vm = DataContext as MainViewModel;
        vm?.OnShown();
        vm?.EnsureDashboardLoaded();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // CloseToTray 开启：用户点 X 只是收托盘，后台继续自动签到；托盘「退出」/更新安装走 ForceExit 才真退。
        if (AppServices.Config.CloseToTray && !AppServices.ForceExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        // 真正退出前若切换在执行：确认，取消则留在前台（ForceExit 路径不重复询问）
        if (AppServices.SwitchBusy && !AppServices.ForceExit &&
            !DialogWindow.Show("退出程序",
                "客户端切换正在执行，现在退出可能造成数据损坏。仍要退出？",
                DialogKind.Warn, primary: "仍要退出", secondary: "取消", dangerPrimary: true))
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
