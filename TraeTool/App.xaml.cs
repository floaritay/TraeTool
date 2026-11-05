using TraeTool.Services;
using TraeTool.ViewModels;

namespace TraeTool;

/// <summary>
/// 应用入口。注意：csproj 同时启用了 WPF 与 WinForms（托盘用），两个命名空间都带
/// Application/MessageBox 等同名类型，此处必须写全限定 System.Windows.Application。
/// 无 DI 容器：AppServices.Init + 手工构造 MainViewModel（见计划）。
/// </summary>
public partial class App : System.Windows.Application
{
    private MainViewModel? _mainViewModel;
    private TrayIconService? _tray;
    /// <summary>AppServices.Init 成功后才允许落盘——否则提前退出（如第二实例/初始化失败）会把空配置写盘、清空账号！</summary>
    private bool _servicesInitialized;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private Thread? _showListener;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例互斥：第二个实例只负责唤醒第一个实例的窗口后立即退出
        _singleInstanceMutex = new Mutex(true, @"Local\TraeTool.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(@"Local\TraeTool.ShowEvent");
                ev.Set();
            }
            catch { /* 首个实例已退出的竞态：直接放行为新实例 */ }
            Shutdown();
            return;
        }

        // --page=xxx 指定启动页（截图/自动化）；--wizard-preview 向导演示；
        // --selftest-update=<feed> 自动更新端到端自检（检查→下载→脚本接管重启）
        bool wizardPreview = false;
        bool selftestImport = false;
        string? selftestUpdate = null;
        foreach (var arg in e.Args)
        {
            if (arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
                Views.MainWindow.StartupPage = arg["--page=".Length..].Trim().ToLowerInvariant();
            else if (string.Equals(arg, "--wizard-preview", StringComparison.OrdinalIgnoreCase))
                wizardPreview = true;
            else if (arg.StartsWith("--selftest-update=", StringComparison.OrdinalIgnoreCase))
                selftestUpdate = arg["--selftest-update=".Length..];
            else if (string.Equals(arg, "--selftest-import", StringComparison.OrdinalIgnoreCase))
                selftestImport = true;
        }

        try
        {
            AppServices.Init();
            _servicesInitialized = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("初始化数据目录/数据库失败：" + ex.Message, "TraeTool",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // 主题（light/dark）——在任何窗口创建前应用（晚于 Init 因为要读配置）
        ThemeService.Apply(AppServices.Config.Theme);

        // 首启用户协议：未同意 → 弹窗；拒绝则退出（不落盘任何新状态）
        if (!AppServices.Config.EulaAccepted)
        {
            if (!Views.EulaWindow.ShowAccepted())
            {
                Shutdown(1);
                return;
            }
            AppServices.Config.EulaAccepted = true;
            AppServices.SaveConfig();
        }

        _mainViewModel = new MainViewModel();
        MainWindowHost.ShowBalloon = (title, text) => _tray?.ShowBalloon(title, text);

        var window = new Views.MainWindow { DataContext = _mainViewModel };
        MainWindow = window;
        window.Show();

        if (wizardPreview)
            Dispatcher.BeginInvoke(() => Views.ArchiveWizardWindow.RunPreview(window));

        if (selftestUpdate != null)
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    AppLog.Info("update", $"selftest: 检查更新源 {selftestUpdate}");
                    var r = await UpdateService.CheckAsync(selftestUpdate);
                    if (r.Error != null) { AppLog.Error("update", "selftest 检查失败: " + r.Error); return; }
                    if (r.UpToDate || r.Update == null) { AppLog.Error("update", "selftest: 未发现更高版本（预期 1.0.1）"); return; }
                    AppLog.Info("update", $"selftest: 发现 v{r.Update.Version}，下载并解压…");
                    var (staging, err) = await UpdateService.DownloadAndStageAsync(r.Update);
                    if (err != null || staging == null) { AppLog.Error("update", "selftest 下载失败: " + err); return; }
                    AppLog.Info("update", $"selftest: 暂存完成 {staging}，写入安装脚本并退出重启");
                    UpdateService.ApplyAndRestart(staging); // 退出 → bat 覆盖 → 自动重启
                }
                catch (Exception ex)
                {
                    AppLog.Error("update", "selftest 异常: " + ex);
                }
            });

        if (selftestImport)
            Dispatcher.BeginInvoke(async () =>
            {
                AppLog.Info("import", "selftest: 开始只读探查本机客户端…");
                var p = await LocalClientImporter.ProbeAsync();
                AppLog.Info("import",
                    $"selftest 结果: ok={p.Ok} token={(p.Token != null)} sessionLen={p.Session?.Length ?? 0} device={p.DeviceId ?? "-"} source={p.Source}");
                AppLog.Info("import", "selftest msg: " + p.Message);
            });

        // 托盘常驻
        _tray = new TrayIconService();
        _tray.OpenRequested += () => System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            MainWindow?.Show();
            MainWindow?.Activate();
        });
        _tray.QuickCheckinRequested += () => System.Windows.Application.Current.Dispatcher.Invoke(async () =>
        {
            // 托盘快捷签到：复用一键签到（只签启用账号）
            if (AppServices.BatchBusy) return;
            await _mainViewModel.Checkin.CheckinCommand.ExecuteAsync(null);
        });
        _tray.ExitRequested += () => System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _mainViewModel.AutoCheckin.Stop();
            AppServices.ForceExit = true; // 否则 CloseToTray 时 OnClosing 会拦截 Shutdown
            AppServices.SaveConfig();
            Shutdown();
        });

        _mainViewModel.StartAutoCheckin();

        // 后台监听"第二个实例请求显示窗口"事件（托盘隐藏时被再次启动 → 恢复窗口）
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TraeTool.ShowEvent");
        _showListener = new Thread(() =>
        {
            while (true)
            {
                try { _showEvent.WaitOne(); }
                catch { break; }
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        MainWindow?.Show();
                        if (MainWindow != null) MainWindow.WindowState = System.Windows.WindowState.Normal;
                        MainWindow?.Activate();
                    });
                }
                catch { break; }
            }
        }) { IsBackground = true };
        _showListener.Start();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _mainViewModel?.AutoCheckin.Stop();
        if (_servicesInitialized) AppServices.SaveConfig(); // 未初始化绝不落盘（防空配置覆盖）
        _tray?.Dispose();
        AppLog.Info("app", "退出");
        base.OnExit(e);
    }
}
