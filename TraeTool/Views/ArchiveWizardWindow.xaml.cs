using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TraeTool.Services;
using Application = System.Windows.Application;   // 与 WinForms Application 冲突，需限定
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace TraeTool.Views;

/// <summary>
/// 建档向导窗口（G4）：五步指示器（每步带小字说明）+ 分步正文 + 警告/勾选/进度 + 主/次按钮。
/// 业务流程由 <see cref="WizardController"/> 驱动（ArchiveFlow 持有），本窗口只负责展示与用户动作回传。
/// </summary>
public partial class ArchiveWizardWindow : Window
{
    private static readonly (string Name, string Desc)[] StepDefs =
    [
        ("确认", "了解建档影响"),
        ("打开客户端", "在客户端登录该账号"),
        ("关闭客户端", "释放文件占用"),
        ("交叉比对", "比指纹防建错档"),
        ("备份建档", "生成登录态档案"),
    ];

    private readonly Border[] _stepBg = new Border[5];
    private readonly TextBlock[] _stepNum = new TextBlock[5];
    private readonly TextBlock[] _stepName = new TextBlock[5];
    private readonly TextBlock[] _stepDesc = new TextBlock[5];

    private int _currentStep;
    private bool _requireCheck;
    private bool _primaryAllowed;
    private string _primaryLabel = "下一步";

    internal WizardController Controller { get; }

    public ArchiveWizardWindow()
    {
        InitializeComponent();
        BuildSteps();
        Controller = new WizardController(this);
        Closed += (_, _) => Controller.NotifyClosed();
        SetStep(0);
    }

    private void BuildSteps()
    {
        double width = 468.0 / StepDefs.Length;
        for (int i = 0; i < StepDefs.Length; i++)
        {
            var num = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0x9D, 0xAB)),
            };
            var dot = new Border
            {
                Width = 21,
                Height = 21,
                CornerRadius = new CornerRadius(10.5),
                Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF3, 0xEF)),
                Child = num,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var name = new TextBlock
            {
                Text = StepDefs[i].Name,
                Style = (Style)FindResource("StepName"),
            };
            var desc = new TextBlock
            {
                Text = StepDefs[i].Desc,
                Style = (Style)FindResource("StepDesc"),
            };
            var cell = new StackPanel { Width = width };
            cell.Children.Add(dot);
            cell.Children.Add(name);
            cell.Children.Add(desc);

            _stepBg[i] = dot;
            _stepNum[i] = num;
            _stepName[i] = name;
            _stepDesc[i] = desc;
            StepsRow.Children.Add(cell);
        }
    }

    // ==================== Controller 侧调用（已封送 UI 线程） ====================

    internal void SetStep(int current)
    {
        _currentStep = current;
        for (int i = 0; i < 5; i++)
        {
            bool done = i < current;
            bool cur = i == current;
            if (done)
            {
                _stepBg[i].Background = (SolidColorBrush)FindResource("SuccessBrush");
                _stepNum[i].Text = "✓";
                _stepNum[i].Foreground = Brushes.White;
            }
            else if (cur)
            {
                _stepBg[i].Background = (SolidColorBrush)FindResource("PrimaryBrush");
                _stepNum[i].Text = (i + 1).ToString();
                _stepNum[i].Foreground = Brushes.White;
            }
            else
            {
                _stepBg[i].Background = (SolidColorBrush)FindResource("BorderBrush");
                _stepNum[i].Text = (i + 1).ToString();
                _stepNum[i].Foreground = (SolidColorBrush)FindResource("MuteBrush");
            }

            _stepName[i].Foreground = (SolidColorBrush)FindResource(cur ? "PrimaryStrongBrush" : "MuteBrush");
            _stepName[i].FontWeight = cur ? FontWeights.SemiBold : FontWeights.Normal;
            _stepDesc[i].Foreground = (SolidColorBrush)FindResource("MuteBrush");
        }
    }

    internal void SetBody(string title, string desc, string[]? bullets, string? warn, bool requireCheck,
        string? checkText = null)
    {
        BodyTitle.Text = title;
        BodyDesc.Text = desc;
        BodyDesc.Visibility = string.IsNullOrEmpty(desc) ? Visibility.Collapsed : Visibility.Visible;

        BulletsList.Items.Clear();
        if (bullets is { Length: > 0 })
            foreach (var b in bullets) BulletsList.Items.Add(b);
        BulletsList.Visibility = bullets is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        WarnBox.Visibility = warn == null ? Visibility.Collapsed : Visibility.Visible;
        WarnText.Text = warn ?? "";

        _requireCheck = requireCheck;
        ConfirmCheck.Visibility = requireCheck ? Visibility.Visible : Visibility.Collapsed;
        ConfirmCheck.IsChecked = false; // 每次换正文都要求重新勾选（防止旧勾选直接放行新警告）
        if (requireCheck) ConfirmText.Text = checkText ?? "我已确认无误，继续建档";
        RefreshPrimaryEnabled();
    }

    internal void SetStatus(string? text, bool running)
    {
        StatusText.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        StatusDot.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = text ?? "";
        StatusDot.Fill = (SolidColorBrush)FindResource(running ? "PrimaryBrush" : "SubBrush");
    }

    internal void SetPrimary(string label, bool enabled)
    {
        _primaryLabel = label;
        _primaryAllowed = enabled;
        PrimaryButton.Visibility = string.IsNullOrEmpty(label) ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = string.IsNullOrEmpty(label) ? "下一步" : label;
        RefreshPrimaryEnabled();
    }

    internal void RefreshPrimaryEnabled()
    {
        PrimaryButton.IsEnabled = _primaryAllowed && (!_requireCheck || ConfirmCheck.IsChecked == true);
    }

    internal void AddProgress(string line)
    {
        ProgressBox.Visibility = Visibility.Visible;
        ProgressLines.Children.Add(new TextBlock
        {
            Text = $"{DateTime.Now:HH:mm:ss}  {line}",
            FontFamily = (FontFamily)FindResource("FontMono"),
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x76, 0x84)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 1),
        });
        ProgressScroll.ScrollToEnd();
    }

    // ==================== 用户动作 ====================

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => Controller.OnPrimary();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Controller.OnSecondary();

    private void OnCheckChanged(object sender, RoutedEventArgs e)
    {
        AppLog.Info("wizard", $"确认勾选={(ConfirmCheck.IsChecked == true)}，requireCheck={_requireCheck}");
        RefreshPrimaryEnabled();
    }

    /// <summary>静态驱动入口：ShowDialog + Loaded 后运行 driver，driver 结束自动关窗。</summary>
    public static void Run(Window? owner, string subtitle, Func<WizardController, Task> driver)
    {
        void Start()
        {
            try
            {
                AppLog.Info("wizard", $"向导打开：{subtitle}");
                var w = new ArchiveWizardWindow();
                if (owner != null && owner.IsVisible) w.Owner = owner;
                else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                w.SubtitleText.Text = subtitle;
                w.Loaded += async (_, _) =>
                {
                    try
                    {
                        await driver(w.Controller);
                        AppLog.Info("wizard", "向导流程结束（driver 正常返回）");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("wizard", "向导流程异常: " + ex);
                        w.Controller.SetBody("出错了", ex.Message, null, null, false);
                        w.Controller.SetStatus(null, false);
                        w.Controller.SetPrimary("关闭", true);
                        await w.Controller.WaitPrimaryAsync();
                    }
                    finally
                    {
                        if (w.IsVisible) w.Close();
                    }
                };
                w.ShowDialog();
            }
            catch (Exception ex)
            {
                // 构造/XAML 级失败（历史事故：步骤样式挂在子元素上、窗口级 FindResource 抛
                // ResourceReferenceKeyNotFoundException，又被 RelayCommand 静默吞掉 → 点建档无反应）
                AppLog.Error("wizard", "向导启动失败: " + ex);
                throw;
            }
        }

        var app = Application.Current;
        if (app == null || app.Dispatcher.CheckAccess()) Start();
        else app.Dispatcher.Invoke(Start);
    }

    /// <summary>
    /// 预览模式（--wizard-preview）：自动演示五步全流程，无任何真实操作。
    /// 用于 UI 自检/截图，不点任何按钮也能走完。
    /// </summary>
    public static void RunPreview(Window? owner)
    {
        Run(owner, "预览模式（自动演示，无实际操作）", async c =>
        {
            AppLog.Info("wizard", "预览 step0 确认");
            c.Step(0);
            c.SetBody("启用「客户端切换」需要在客户端里登录一次",
                "这是向导第一步的演示正文，用于检查排版与小字。",
                ["须先在 Trae 客户端手动登录该账号一次", "建档会关闭并重新打开客户端", "建档时自动与已有档案交叉比对防错档"]);
            c.SetStatus(null);
            c.SetPrimary("开始建档");
            await Task.Delay(1500);

            AppLog.Info("wizard", "预览 step1 打开客户端");
            c.Step(1);
            c.SetBody("请在 Trae 客户端中登录「演示账号」",
                "登录完成后点击「我已登录」继续——接下来会自动关闭客户端并备份登录态。");
            c.SetStatus("等待你在客户端完成登录…", running: true);
            c.SetPrimary("我已登录");
            await Task.Delay(1500);

            AppLog.Info("wizard", "预览 step2 关闭客户端");
            c.Step(2);
            c.SetBody("关闭客户端", "建档需要登录态文件未被占用，正在关闭…");
            c.SetStatus("正在关闭客户端…", running: true);
            c.SetPrimary("");
            c.SetProgressLine("关闭客户端（WM_CLOSE → 必要时强杀）");
            await Task.Delay(1500);

            AppLog.Info("wizard", "预览 step3 交叉比对（含警告分支）");
            c.Step(3);
            c.SetBody("交叉比对", "比对登录态文件指纹与已有档案，避免重复建档或串号。");
            c.SetStatus("正在比对…", running: true);
            await Task.Delay(1200);
            c.SetProgressLine("vs 账号A：相似度 92%");
            c.SetBody("可能建错了档",
                "即将保存的客户端登录态与账号「账号A」的已有档案相似度 92%——客户端里登录的很可能不是本账号！",
                warn: "请回到客户端确认登录的确实是本账号后再勾选继续。",
                requireCheck: true,
                checkText: "我已确认客户端登录的是本账号，仍然建档");
            c.SetStatus("等待确认");
            c.SetPrimary("确认建档"); // requireCheck 场景由勾选放行，enabled 必须为 true
            await Task.Delay(2600);

            AppLog.Info("wizard", "预览 step4 备份建档");
            c.Step(4);
            c.SetBody("备份建档", "正在把「演示账号」的客户端登录态保存为档案…");
            c.SetStatus("正在备份…", running: true);
            c.SetPrimary("");
            c.SetProgressLine("备份 134 个文件…");
            await Task.Delay(1500);
            c.SetBody("建档完成", "已保存 134 个文件。现在可以在「客户端切换」页把客户端切换到此账号。");
            c.SetProgressLine("建档完成（134 个文件）");
            c.SetStatus("完成");
            c.SetPrimary("完成");
            await Task.Delay(1800);
        });
    }
}

/// <summary>
/// 向导控制器：业务驱动方（ArchiveFlow）逐步推进 UI 并等待用户动作。
/// 所有方法线程安全（自动封送 UI 线程）；窗口已关闭时操作为空效。
/// </summary>
public sealed class WizardController
{
    private readonly ArchiveWizardWindow _w;
    private TaskCompletionSource<bool>? _tcs;
    private volatile bool _closed;
    private volatile bool _cancelRequested;
    private volatile bool _pendingPrimaryClick; // 主按钮早于 WaitPrimaryAsync 被点的竞态兜底

    internal WizardController(ArchiveWizardWindow w) => _w = w;

    private void Invoke(Action a)
    {
        if (_closed) return;
        var d = _w.Dispatcher;
        if (d.CheckAccess())
        {
            if (!_closed) a();
        }
        else d.Invoke(() => { if (!_closed) a(); });
    }

    /// <summary>推进到第 index 步（0 起）。</summary>
    public void Step(int index) => Invoke(() => _w.SetStep(index));

    /// <summary>设置本步正文；warn 显示橙色警告框；requireCheck 打开确认勾选（主按钮由其放行）。</summary>
    public void SetBody(string title, string desc, string[]? bullets = null, string? warn = null,
        bool requireCheck = false, string? checkText = null)
        => Invoke(() => _w.SetBody(title, desc, bullets, warn, requireCheck, checkText));

    /// <summary>底部状态行（text = null 隐藏；running = 蓝点）。</summary>
    public void SetStatus(string? text, bool running = false)
        => Invoke(() => _w.SetStatus(text, running));

    /// <summary>设置主按钮（label 空 = 隐藏）。
    /// 最终可用 = enabled && (!requireCheck || 勾选已确认)——
    /// requireCheck 场景必须传 enabled=true，由勾选做闸门；传 false 会钉死按钮。</summary>
    public void SetPrimary(string label, bool enabled = true)
        => Invoke(() =>
        {
            _pendingPrimaryClick = false; // 新的按钮语义出现 → 作废旧的提前点击
            _w.SetPrimary(label, enabled);
        });

    /// <summary>向进度日志追加一行。</summary>
    public void SetProgressLine(string line) => Invoke(() => _w.AddProgress(line));

    /// <summary>用户是否已请求取消（点 X / 取消按钮）。长步骤间隙应检查。</summary>
    public bool IsCancelRequested => _cancelRequested;

    /// <summary>等待用户点主按钮；取消/关窗返回 false。</summary>
    public Task<bool> WaitPrimaryAsync()
    {
        if (_closed || _cancelRequested) return Task.FromResult(false);
        if (_pendingPrimaryClick)
        {
            _pendingPrimaryClick = false;
            return Task.FromResult(true);
        }
        _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _tcs.Task;
    }

    internal void OnPrimary()
    {
        var tcs = Interlocked.Exchange(ref _tcs, null);
        if (tcs != null) tcs.TrySetResult(true);
        else _pendingPrimaryClick = true;
    }

    internal void OnSecondary()
    {
        _cancelRequested = true;
        var tcs = Interlocked.Exchange(ref _tcs, null);
        tcs?.TrySetResult(false);
    }

    internal void NotifyClosed()
    {
        _closed = true;
        _cancelRequested = true;
        var tcs = Interlocked.Exchange(ref _tcs, null);
        tcs?.TrySetResult(false);
    }
}
