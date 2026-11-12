using TraeTool.Services;
using TraeTool.ViewModels;

namespace TraeTool;

/// <summary>
/// 应用入口。注意：csproj 同时启用了 WPF 与 WinForms（托盘用），两个命名空间都带
/// Application/MessageBox 等同名类型，此处必须写全限定 System.Windows.Application。
/// 无 DI 容器：AppServices.Init + 手工构造 MainViewModel。
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
        HookGlobalExceptionHandlers();

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
        bool updateFailed = false;
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
            else if (string.Equals(arg, "--update-failed", StringComparison.OrdinalIgnoreCase))
                updateFailed = true;   // 安装脚本覆盖失败时回传（见 UpdateService.ApplyAndRestart）
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

        if (updateFailed)
            Dispatcher.BeginInvoke(() => Views.DialogWindow.Show(
                "更新未完成",
                "上次自动更新在覆盖程序文件时失败，当前仍是旧版本（通常是安装目录需要管理员权限）。\n\n" +
                "可改用便携版目录，或右键以管理员身份启动后再检查更新。",
                Views.DialogKind.Warn, primary: "知道了"));

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
                    if (r.UpToDate || r.Update == null) { AppLog.Error("update", "selftest: 更新源没有高于本程序的版本（自检源版本需高于当前运行版本）"); return; }
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
                    $"selftest 结果: ok={p.Ok} token={(p.Token != null)} sessionLen={p.Session?.Length ?? 0} device={(p.DeviceId != null)} source={p.Source}");
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
            // 切换执行中直接退出会损坏档案：先确认，取消则维持运行
            if (AppServices.SwitchBusy &&
                !Views.DialogWindow.Show("退出程序",
                    "客户端切换正在执行，现在退出可能造成数据损坏。仍要退出？",
                    Views.DialogKind.Warn, primary: "仍要退出", secondary: "取消", dangerPrimary: true))
                return;
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

    /// <summary>
    /// 全局异常兜底：托盘常驻下，UI 线程异常若不接管会直接结束进程且只留托盘图标消失。
    /// 全部记录到日志；界面异常标记为已处理以保持进程存活。
    /// </summary>
    private void HookGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("app", "未处理的界面异常: " + args.Exception);
            TryNotify("程序遇到错误", "已记录到日志；若反复出现请重启程序");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("app", "未处理的进程异常: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("app", "未观察的后台任务异常: " + args.Exception);
            args.SetObserved();
        };
    }

    private static void TryNotify(string title, string text)
    {
        try { MainWindowHost.ShowBalloon?.Invoke(title, text); }
        catch { /* 通知失败不影响主流程 */ }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _mainViewModel?.AutoCheckin.Stop();
        AppServices.Gateway.Stop();
        if (_servicesInitialized) AppServices.SaveConfig(); // 未初始化绝不落盘（防空配置覆盖）
        _tray?.Dispose();
        AppLog.Info("app", "退出");
        base.OnExit(e);
    }
}
