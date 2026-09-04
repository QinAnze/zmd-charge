using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using EndfieldCharge.Animations;
using EndfieldCharge.Models;
using EndfieldCharge.Services;

namespace EndfieldCharge.Views;

/// <summary>
/// 工业模式风格的 HUD：三态动画——
/// 中心小胶囊（电标居中）→ 长条矩形（电标左移 + 模式标题）→
/// 左侧小胶囊（数字 + 徽章）→ 停留数秒 → 收尾关掉。
///
/// 两种显示模式：
///   · 弹出模式（默认）：播完就收起。
///   · 常驻模式：播完停在末态不收起，数据持续刷新，
///     只有调用 <see cref="StopResident"/>（托盘退出）才会消失。
///
/// 点击圆胶囊 → 打开性能面板（CPU/GPU/内存/硬盘/上传/下载），
/// 再点一下 → 回到电量显示（非常驻模式随即收起）。
/// </summary>
public partial class HudWindow : Window
{
    private static readonly TimeSpan DismissDuration = TimeSpan.FromMilliseconds(160);

    // 常驻模式的刷新 / 位置校正间隔
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RepositionInterval = TimeSpan.FromSeconds(5);

    // 性能面板的刷新间隔
    private static readonly TimeSpan PanelRefreshInterval = TimeSpan.FromMilliseconds(500);

    // 徽章圆环颜色：正常黄绿，告警红（低电量 / 高负载）
    private static readonly Color BadgeColorNormal = Color.Parse("#C6CA4C");
    private static readonly Color BadgeColorDanger = Color.Parse("#FF4D4F");

    private CancellationTokenSource? _cts;

    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _repositionTimer;
    private DispatcherTimer? _panelTimer;
    private Func<HudContent>? _contentProvider;
    private Func<SystemLoadSnapshot?>? _perfProvider;
    private bool _resident;
    private bool _panelOpen;
    private bool _panelBusy;   // 形变动画进行中（防止两条形变轨道打架）

    /// <summary>无参构造：XAML 运行时加载器需要（AVLN3001 / CI TreatWarningsAsErrors）。</summary>
    public HudWindow() : this(null)
    {
    }

    public HudWindow(Func<SystemLoadSnapshot?>? perfProvider)
    {
        InitializeComponent();

        _perfProvider = perfProvider;

        Cursor = new Cursor(StandardCursorType.Hand);

        // 点击圆胶囊 ⇄ 性能面板（弹出 / 常驻模式行为一致）
        PointerPressed += (_, _) => TogglePanel();

        ResetToInitial();
    }

    /// <summary>是否处于常驻模式。</summary>
    public bool IsResident => _resident;

    /// <summary>
    /// 简化版：拔电时只弹"内容圆胶囊"——无电标先出、无模式矩形、无波纹。
    /// 全圆胶囊直接带数值弹出 → 停留 → 整体缩小收回。
    /// </summary>
    public async Task ShowSimpleAsync(HudContent content)
    {
        ApplyContent(content);
        ResetPanel();   // 新一轮播放前把性能面板收干净

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ResetToInitial();
        SetSimpleCState();

        ShowPositioned();

        try
        {
            await Task.WhenAll(
                HudAnimations.SimplePillAppear().RunAsync(Pill, ct),
                HudAnimations.SimpleFadeIn().RunAsync(BoltIcon, ct),
                HudAnimations.SimpleFadeIn().RunAsync(NumHost, ct),
                HudAnimations.SimpleScaleOut().RunAsync(ScaleHost, ct));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ct.IsCancellationRequested && IsVisible)
            Hide();
    }

    /// <summary>填入内容并播放一次完整三态动画，播完自动收起。</summary>
    public Task ShowFullAsync(HudContent content) =>
        PlayAsync(content, resident: false);

    /// <summary>
    /// 常驻模式：播一次完整动画，然后停在末态（数字态）不收起，
    /// 内容按 <paramref name="contentProvider"/> 持续刷新，点击也不会关闭。
    /// </summary>
    public Task ShowResidentAsync(Func<HudContent> contentProvider)
    {
        _resident = true;
        _contentProvider = contentProvider;

        var content = contentProvider();
        return PlayAsync(content, resident: true);
    }

    /// <summary>退出常驻模式：停掉刷新定时器并隐藏窗口，回到"弹一下就走"的行为。</summary>
    public void StopResident()
    {
        _resident = false;
        _contentProvider = null;

        StopResidentTimers();
        ResetPanel();

        _cts?.Cancel();
        if (IsVisible)
            Hide();
    }

    /// <summary>常驻模式下按当前数据源刷新一次内容（电源事件等场景调用）。</summary>
    public void RefreshContent()
    {
        if (!_resident || _contentProvider is null)
            return;

        ApplyContent(_contentProvider());
    }

    private async Task PlayAsync(HudContent content, bool resident)
    {
        ApplyContent(content);
        ResetPanel();   // 新一轮播放前把性能面板收干净

        // 打断上一次播放
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        bool debugStatic = Array.Exists(Environment.GetCommandLineArgs(), a => a == "--debug-ring");

        ResetToInitial();

        ShowPositioned();

        if (debugStatic)
        {
            // 调试模式：直接呈现状态 C（数字态），保持 1.5s 后收起。
            ShowFullyExpandedStatic();
            try { await Task.Delay(1500, ct); }
            catch (OperationCanceledException) { return; }
            if (!ct.IsCancellationRequested && IsVisible)
                Hide();
            return;
        }

        var tracks = new List<Task>
        {
            HudAnimations.PillCorner().RunAsync(Pill, ct),
            HudAnimations.PillAppear().RunAsync(Pill, ct),
            HudAnimations.PillHeight().RunAsync(Pill, ct),
            HudAnimations.PillHeight().RunAsync(RippleHost, ct),
            HudAnimations.BoltIcon().RunAsync(BoltIcon, ct),
            HudAnimations.RippleHost().RunAsync(RippleHost, ct),
            HudAnimations.CircleForm().RunAsync(CircleForm, ct),
            HudAnimations.SquareForm().RunAsync(SquareForm, ct),
            HudAnimations.TitleHost().RunAsync(TitleHost, ct),
            HudAnimations.NumHost().RunAsync(NumHost, ct),
            HudAnimations.Ripple(0.0, 1.5, 0.50).RunAsync(RippleInner, ct),
            HudAnimations.Ripple(0.0, 2.0, 0.50).RunAsync(RippleMid, ct),
            HudAnimations.Ripple(0.0, 2.5, 0.60).RunAsync(RippleOuter, ct),
            HudAnimations.RippleRise().RunAsync(RippleInnerHost, ct),
            HudAnimations.RippleRise().RunAsync(RippleMidHost, ct),
            HudAnimations.RippleRise().RunAsync(RippleOuterHost, ct),
        };

        // 常驻模式不挂收尾缩放：否则动画末尾会把整体缩到 0，
        // 之后要停留在末态就得再"闪回来"。
        if (!resident)
            tracks.Add(HudAnimations.ScaleOut().RunAsync(ScaleHost, ct));

        try
        {
            await Task.WhenAll(tracks);
        }
        catch (OperationCanceledException)
        {
            return; // 被新的触发或手动收起打断，保留当前状态交由下一轮处理
        }

        if (ct.IsCancellationRequested)
            return;

        if (resident)
        {
            // 明确拨到末态（数值态），再开始持续刷新
            ShowFullyExpandedStatic();
            StartResidentTimers();
            return;
        }

        if (IsVisible)
            Hide();
    }

    // ---------------- 常驻模式的定时器 ----------------

    private void StartResidentTimers()
    {
        StopResidentTimers();

        _refreshTimer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Normal, (_, _) => RefreshContent());
        _refreshTimer.Start();

        // 显示器插拔 / 分辨率变化会把贴顶窗口挤走，定期校正一次位置
        _repositionTimer = new DispatcherTimer(RepositionInterval, DispatcherPriority.Normal, (_, _) => PositionTopCenter());
        _repositionTimer.Start();
    }

    private void StopResidentTimers()
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;

        _repositionTimer?.Stop();
        _repositionTimer = null;
    }

    /// <summary>提前收起（点击时）。常驻模式下不生效——常驻只认托盘退出。</summary>
    private async Task DismissAsync()
    {
        if (_resident)
            return;

        _cts?.Cancel();

        var fade = new Animation
        {
            Duration = DismissDuration,
            FillMode = FillMode.Forward,
            Easing = new QuadraticEaseOut(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(OpacityProperty, 1d) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(OpacityProperty, 0d) },
                },
            },
        };

        await fade.RunAsync(Root);
        Hide();
    }

    // ---------------- 性能面板（点击圆胶囊 ⇄ 形变为圆角矩形） ----------------

    private void TogglePanel()
    {
        if (_panelBusy)
            return;   // 形变进行中不响应，防止两条形变轨道打架

        if (_panelOpen)
            _ = ClosePanelAsync();
        else
            _ = OpenPanelAsync();
    }

    /// <summary>圆胶囊形变为圆角矩形（高 60→90、圆角 30→18），面板内容随后浮现。</summary>
    private async Task OpenPanelAsync()
    {
        _panelBusy = true;
        _panelOpen = true;

        // 打断正在播的动画 / 待收起流程，把胶囊拨到圆胶囊态，从它开始形变
        _cts?.Cancel();
        Root.Opacity = 1;
        ShowFullyExpandedStatic();

        // 电标换性能图标；电量内容渐隐让位
        SquareBoltIcon.IsVisible = false;
        SquarePerfIcon.IsVisible = true;

        PerfPanel.IsVisible = true;
        UpdatePerfPanel();
        StartPanelTimer();

        try
        {
            await Task.WhenAll(
                HudAnimations.PanelExpand().RunAsync(Pill),
                HudAnimations.PanelFadeIn().RunAsync(PerfPanel),
                HudAnimations.NumFadeOut().RunAsync(NumHost));
        }
        catch (OperationCanceledException)
        {
            // 被新一轮播放打断，状态交由后续流程接管
        }
        NumHost.Opacity = 0;   // FillMode.Forward 收尾，保证干净
        _panelBusy = false;
    }

    /// <summary>圆角矩形形变回圆胶囊，回到电量显示；非常驻模式随即收起。</summary>
    private async Task ClosePanelAsync()
    {
        _panelBusy = true;
        _panelOpen = false;
        StopPanelTimer();

        // 电标换回闪电；电量内容随收缩渐显
        SquareBoltIcon.IsVisible = true;
        SquarePerfIcon.IsVisible = false;

        try
        {
            await Task.WhenAll(
                HudAnimations.PanelContract().RunAsync(Pill),
                HudAnimations.PanelFadeOut().RunAsync(PerfPanel),
                HudAnimations.NumFadeIn().RunAsync(NumHost));
        }
        catch (OperationCanceledException)
        {
            // 被新一轮播放打断
        }

        PerfPanel.IsVisible = false;
        NumHost.Opacity = 1;   // FillMode.Forward 收尾
        _panelBusy = false;

        if (!_resident)
            await DismissAsync();
    }

    /// <summary>把面板与形变状态收干净（新一轮播放 / 退出常驻前调用）。</summary>
    private void ResetPanel()
    {
        _panelOpen = false;
        _panelBusy = false;
        StopPanelTimer();
        PerfPanel.IsVisible = false;
        PerfPanel.Opacity = 1;        // 抵消 FadeOut 的 FillMode.Forward 残留
        SquareBoltIcon.IsVisible = true;
        SquarePerfIcon.IsVisible = false;
    }

    private void StartPanelTimer()
    {
        StopPanelTimer();
        _panelTimer = new DispatcherTimer(
            PanelRefreshInterval, DispatcherPriority.Normal, (_, _) => UpdatePerfPanel());
        _panelTimer.Start();
    }

    private void StopPanelTimer()
    {
        _panelTimer?.Stop();
        _panelTimer = null;
    }

    private void UpdatePerfPanel()
    {
        SystemLoadSnapshot? s = _perfProvider?.Invoke();
        if (s is null)
            return;

        PerfCpu.Text = $"{s.CpuPercent:F0} %";
        PerfGpu.Text = s.GpuPercent is { } gpu ? $"{gpu:F0} %" : "--";
        PerfMem.Text = $"{s.MemoryPercent:F0} %";
        PerfDisk.Text = s.DiskMBs is { } mbs ? FormatDiskSpeed(mbs) : "--";
        PerfUp.Text = FormatSpeed(s.NetUpKBs);
        PerfDown.Text = FormatSpeed(s.NetDownKBs);
    }

    private static string FormatSpeed(double kbs) =>
        kbs >= 1024d ? $"{kbs / 1024d:F1} MB/s" : $"{kbs:F0} KB/s";

    private static string FormatDiskSpeed(double mbs) =>
        mbs >= 1024d ? $"{mbs / 1024d:F2} GB/s" : $"{mbs:F1} MB/s";

    // ---------------- 数据绑定（直接赋值，无 MVVM 开销） ----------------

    private const double RingDiameter = 46d;
    private const double RingThickness = 4.5d;

    /// <summary>
    /// 把一份 <see cref="HudContent"/> 贴到界面上。
    /// 数值已在工厂里格式化好，这里只负责排版与配色。
    /// </summary>
    private void ApplyContent(HudContent content)
    {
        // 模式标题（简化胶囊的 Title 为空，不显示）
        TitleText.Text = content.Title;
        CaptionText.Text = content.Caption;

        // 主数值 / 单位 / 右侧百分比
        ValueText.Text = content.Primary;
        UnitText.Text = content.Unit;
        PercentText.Text = content.RingPercent.ToString();

        // 圆环：笔记本 = 电量百分比；台式机 = 设备负载
        double fraction = Math.Clamp(content.RingPercent / 100d, 0d, 1d);
        BadgeArc.Data = BuildRingGeometry(fraction, RingDiameter, RingThickness);

        // 配色：告警时才变红，且整组一起换（环 + 内部图形 + 电极）
        var color = content.RingDanger ? BadgeColorDanger : BadgeColorNormal;
        var brush = new SolidColorBrush(color);

        BadgeArc.Stroke = brush;
        LaptopScreen.BorderBrush = brush;
        LaptopBase.Background = brush;
        BadgeElectrode.Background = brush;
        ChipBody.BorderBrush = brush;
        ChipDie.Background = brush;
        ChipPins.Foreground = brush;

        // 内部图形：电量模式用笔记本，功耗模式用芯片
        bool power = content.Mode == HudMode.PowerDraw;
        BadgeLaptop.IsVisible = !power;
        BadgeChip.IsVisible = power;
        BadgeElectrode.IsVisible = !power;   // 电极只属于笔记本图形
    }

    /// <summary>无电池数据时的占位内容（读不到任何信息时用）。</summary>
    public void ApplyUnavailable()
    {
        ApplyContent(HudContent.Empty);
    }

    /// <summary>
    /// 生成"电量进度圆环"几何：从 12 点方向起顺时针，弧长 = fraction × 360°。
    /// fraction=1 时收在 359.5°，避免整圆 ArcSegment 退化成不可见。
    /// </summary>
    private static Geometry BuildRingGeometry(double fraction, double diameter, double thickness)
    {
        double radius = (diameter - thickness) / 2d;
        var center = new Point(diameter / 2d, diameter / 2d);

        double sweep = 360d * Math.Clamp(fraction, 0d, 1d);
        if (sweep < 0.5d)
            sweep = 0.5d;      // 0% 也留一个圆点（Round 端点下可见）
        if (sweep > 359.5d)
            sweep = 359.5d;

        const double startAngle = -90d; // 12 点方向
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + sweep);

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments = new PathSegments
        {
            new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                RotationAngle = 0d,
                IsLargeArc = sweep > 180d,
                SweepDirection = SweepDirection.Clockwise,
            },
        };

        return new PathGeometry { Figures = new PathFigures { figure } };
    }

    /// <summary>角度以度为单位，0° 指向 +X（3 点方向），顺时针增大。</summary>
    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180d;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }

    // ---------------- 动画复位 ----------------

    /// <summary>
    /// 把所有可视元素复位到"动画开始前"的状态。
    /// 每轮播放前调用，保证重复触发时状态干净。
    /// </summary>
    private void ResetToInitial()
    {
        Root.Opacity = 1;

        // 收尾缩放容器复位
        ScaleHost.RenderTransform = new ScaleTransform(1d, 1d);

        // 胶囊：560×60，全圆角（状态 A），待 PillAppear 弹出
        Pill.Width = 560;
        Pill.Height = 60;
        Pill.CornerRadius = new CornerRadius(30d);
        Pill.Opacity = 0;
        Pill.RenderTransform = new ScaleTransform(0.6d, 0.6d);

        // RippleHost 跟随电标位移
        RippleHost.RenderTransform = new TranslateTransform(0d, 0d);

        // 电标容器：居中、缩小、透明（变换组与动画轨道一致）
        BoltIcon.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(0.4d, 0.4d), new TranslateTransform(0d, 0d) },
        };
        BoltIcon.Opacity = 0;

        // 波纹 Host：初始在电标正下方 16px（RippleRise 抬升回 0 对齐电标圆心）
        RippleInnerHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleMidHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleOuterHost.RenderTransform = new TranslateTransform(0d, 16d);

        // 三圈波纹复位（从 0 尺寸开始扩散）
        RippleInner.RenderTransform = new ScaleTransform(0d, 0d);
        RippleInner.Opacity = 0;
        RippleMid.RenderTransform = new ScaleTransform(0d, 0d);
        RippleMid.Opacity = 0;
        RippleOuter.RenderTransform = new ScaleTransform(0d, 0d);
        RippleOuter.Opacity = 0;

        // 形态：圆隐藏（由动画淡入）、方隐藏
        CircleForm.Opacity = 0;
        SquareForm.Opacity = 0;

        // 标题态
        TitleHost.RenderTransform = new TranslateTransform(0d, 0d);
        TitleHost.Opacity = 0;

        // 数字态
        NumHost.RenderTransform = new TranslateTransform(0d, 0d);
        NumHost.Opacity = 0;
    }

    /// <summary>调试辅助：把所有可视元素一次性拨到"状态 C（数字态）"末态。</summary>
    private void ShowFullyExpandedStatic()
    {
        ScaleHost.RenderTransform = new ScaleTransform(1d, 1d);
        Pill.Width = 560;
        Pill.Height = 60;
        Pill.CornerRadius = new CornerRadius(30d);
        Pill.Opacity = 1;
        Pill.RenderTransform = new ScaleTransform(1d, 1d);

        RippleHost.RenderTransform = new TranslateTransform(-245d, 0d);

        // 电标：贴胶囊左侧（pill 560, left=320, 方块 18 半宽 9 → icon center = 320+26+9 = 355, TX=-245）
        BoltIcon.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(1d, 1d), new TranslateTransform(-245d, 0d) },
        };
        BoltIcon.Opacity = 1;
        CircleForm.Opacity = 0;
        SquareForm.Opacity = 1;

        TitleHost.Opacity = 0;

        NumHost.RenderTransform = new TranslateTransform(0d, 0d);
        NumHost.Opacity = 1;
    }

    /// <summary>
    /// 简化版专用：在 ResetToInitial 之后，把所有可视元素拨到"状态 C（电量圆胶囊）"静态。
    /// Pill 全圆胶囊 560×60、电标方块贴左（TX=-245）、数字内容待淡入。
    /// </summary>
    private void SetSimpleCState()
    {
        // 胶囊：全圆角（状态 C 圆胶囊），待 SimplePillAppear 弹出
        Pill.Width = 560;
        Pill.Height = 60;
        Pill.CornerRadius = new CornerRadius(30d);
        Pill.Opacity = 0;
        Pill.RenderTransform = new ScaleTransform(0.6d, 0.6d);

        // 电标：方块形态直接贴 pill 左内 26（TX=-245）
        BoltIcon.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(1d, 1d), new TranslateTransform(-245d, 0d) },
        };
        BoltIcon.Opacity = 0;
        CircleForm.Opacity = 0;
        SquareForm.Opacity = 1;

        // 标题 / 波纹全程隐藏（RippleHost 也归位，避免沿用上一段动画残留的位移）
        TitleHost.Opacity = 0;
        RippleHost.Height = 60;
        RippleHost.RenderTransform = new TranslateTransform(0d, 0d);
        RippleInnerHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleMidHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleOuterHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleInner.Opacity = 0;
        RippleMid.Opacity = 0;
        RippleOuter.Opacity = 0;

        // 数字待淡入
        NumHost.RenderTransform = new TranslateTransform(0d, 0d);
        NumHost.Opacity = 0;
    }

    // ---------------- 定位 ----------------

    /// <summary>
    /// 把窗口贴到主显示器顶部居中。
    /// 三个坑都堵掉：
    ///   1) 绝不退回 Screens.ScreenFromVisual——它返回"窗口当前所在屏"，
    ///      一旦某次落到副屏就再也回不来（表现为乱跑到别的显示器）；
    ///   2) 用窗口自身的 RenderScaling 换算物理宽度——隐藏窗口跨屏 / DPI 变化后
    ///      screen.Scaling 与实际生效缩放不一致，宽度算小会让窗口整体偏右；
    ///   3) 不再改写 Width——宽度固定，只算居中偏移，避免重复触发时位置一次比一次偏。
    /// </summary>
    private void PositionTopCenter()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
            return;

        var area = screen.WorkingArea;   // 物理像素

        double scaling = RenderScaling > 0
            ? RenderScaling
            : (screen.Scaling > 0 ? screen.Scaling : 1d);

        int pixelWidth = (int)Math.Round(Width * scaling);
        int x = area.X + (area.Width - pixelWidth) / 2;

        Position = new PixelPoint(x, area.Y);
    }

    /// <summary>
    /// 定位并显示窗口：Show 前先定一次（避免闪现在旧位置），Show 后再定一次，
    /// 并在下一帧补定一次。
    /// 原因：对隐藏窗口的 SetWindowPos 在部分平台会被推迟到显示时才生效，
    /// 且 Show 之后若发生 DPI 变化，窗口物理宽度会变，居中偏移必须重算。
    /// 这是"第一次位置正常、第二次开始偏移"的根治办法——位置每次都重新算，不依赖残留值。
    /// </summary>
    private void ShowPositioned()
    {
        PositionTopCenter();

        if (!IsVisible)
            Show();

        PositionTopCenter();
        Dispatcher.UIThread.Post(() =>
        {
            PositionTopCenter();

            // 兜底自检：多屏 / DPI 变化时系统可能在 Show 之后把窗口挪走，
            // 这里再确认一次"是否真的落在主屏"，不在就强制拉回。
            var current = Screens.ScreenFromWindow(this);
            if (current is not null && !ReferenceEquals(current, Screens.Primary))
                PositionTopCenter();
        }, DispatcherPriority.Loaded);
    }
}
