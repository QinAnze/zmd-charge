using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using EndfieldCharge.Animations;
using EndfieldCharge.Contract;
using EndfieldCharge.Services;
using EndfieldCharge.Settings;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace EndfieldCharge.Views;

/// <summary>
/// 完整三态动画的主题。三者共用同一条时间线（弹电标 → 撑高显示模式名 → 收窄显示电量），
/// 只有文案与波纹方向不同：充电向外扩散（能量注入），电池模式向内收拢（能量流出）。
/// </summary>
public enum HudPlayMode
{
    Charge,

    /// <summary>未插电（电池供电）。</summary>
    Battery,

    PowerSaver,
}

public partial class HudWindow : Window
{
    private static readonly TimeSpan DismissDuration = TimeSpan.FromMilliseconds(160);

    private static readonly Color BadgeColorNormal = Color.Parse("#C6CA4C");
    private static readonly Color BadgeColorLow = Color.Parse("#FF4D4F");

    private CancellationTokenSource? _cts;
    private AppSettings _settings = new();
    private AnimationOptions _animOptions = AnimationOptions.Default;
    private int _fpsFrameCount;
    private DateTime _fpsLastMeasure = DateTime.UtcNow;
    private bool _fpsEnabled;

    // 性能面板（来自 dev）：点击胶囊在圆胶囊 ⇄ 圆角矩形之间形变
    private static readonly TimeSpan PanelRefreshInterval = TimeSpan.FromMilliseconds(500);
    private Func<SystemLoadSnapshot?>? _perfProvider;
    private DispatcherTimer? _panelTimer;
    private bool _panelOpen;
    private bool _panelBusy;
    private readonly bool _isDesktop;

    /// <summary>当前在播哪套动画（None = 没在播）。播放中再次唤起靠它判断能不能"续住"。</summary>
    private PlayKind _play = PlayKind.None;

    /// <summary>本轮展示的插件适配器药丸（主药丸之外，一适配器一粒）。</summary>
    private readonly List<Border> _extraPills = [];

    /// <summary>本轮播放已过去的墙钟时间：判断开场是否走完、是否还在 C 态停留段。</summary>
    private readonly Stopwatch _playClock = new();

    private enum PlayKind
    {
        None,
        Full,
        Simple,
    }

    /// <summary>无参构造：XAML 运行时加载器需要（AVLN3001）。</summary>
    public HudWindow() : this(null, false)
    {
    }

    public HudWindow(Func<SystemLoadSnapshot?>? perfProvider, bool isDesktop = false)
    {
        _perfProvider = perfProvider;
        _isDesktop = isDesktop;
        InitializeComponent();

        TagLineText.Text = Localization.TagLine;
        TitleText.Text = Localization.TitleMode;

        Cursor = new Cursor(StandardCursorType.Hand);
        PointerPressed += (_, _) => TogglePanel();

        _fpsEnabled = Array.Exists(Environment.GetCommandLineArgs(), a => a == "--show-fps");
        if (_fpsEnabled)
        {
            FpsText.IsVisible = true;
            StartFpsCounter();
        }

        ResetToInitial();
    }

    /// <summary>从设置更新 HUD 参数（缩放、动画微调、位置、显示器）。</summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _animOptions = AnimationOptions.FromSettings(settings);

        // 全局缩放
        GlobalScale.RenderTransform = new ScaleTransform(settings.GlobalScale, settings.GlobalScale);

        // 更新本地化文本（可能语言变了）
        TagLineText.Text = Localization.TagLine;
        TitleText.Text = Localization.TitleMode;
    }

    // ---------------- 动画播放 ----------------

    public async Task ShowSimpleAsync(IReadOnlyList<AdapterReading> readings, AnimationOptions? options = null)
    {
        ResetPanel();
        ApplyReadings(readings);

        var o = options ?? _animOptions;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ResetToInitial();
        SetSimpleCState();

        ShowPositioned();

        _play = PlayKind.Simple;
        _playClock.Restart();

        var tasks = new List<Task>
        {
            HudAnimations.SimplePillAppear(o).RunAsync(Pill, ct),
            HudAnimations.SimpleFadeIn(o).RunAsync(BoltIcon, ct),
            HudAnimations.SimpleFadeIn(o).RunAsync(NumHost, ct),
            HudAnimations.SimpleScaleOut(o).RunAsync(ScaleHost, ct),
        };
        tasks.AddRange(_extraPills.Select(
            (pill, i) => HudAnimations.ExtraPillSimple(o, i).RunAsync(pill, ct)));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ct.IsCancellationRequested && IsVisible)
            Hide();

        _play = PlayKind.None;
    }

    public async Task ShowAndPlayAsync(
        IReadOnlyList<AdapterReading> readings,
        bool acOnline,
        HudPlayMode mode = HudPlayMode.Charge,
        AnimationOptions? options = null,
        bool replayIntro = true)
    {
        ResetPanel();
        ApplyReadings(readings);

        // 文案主题：充电 = 充电模式；电池 = 电池模式；省电 = 省电模式；台式机 = 功耗模式
        (TagLineText.Text, TitleText.Text) = mode switch
        {
            HudPlayMode.Battery => (Localization.TagLineBattery, Localization.TitleBattery),
            HudPlayMode.PowerSaver => (Localization.TagLineSaver, Localization.TitleSaver),
            _ when _isDesktop => (Localization.TagLinePowerDraw, Localization.TitlePowerDraw),
            _ => (Localization.TagLine, Localization.TitleMode),
        };

        // 省电模式用叶子，其余模式用闪电（圆形态 / 方形态一致）
        Geometry? modeIcon = ResolveGeometry(
            mode == HudPlayMode.PowerSaver ? "Geo.Leaf" : "Geo.Bolt");
        CircleBoltIcon.Data = modeIcon;
        SquareBoltIcon.Data = modeIcon;

        var o = options ?? _animOptions;

        // 已经在播完整三态动画：不重播入场。重播会把上一轮动画整条撤掉 —— 属性回落到
        // "不可见"的局部值，画面先"灭"一下、再等 0.24s 才有电标弹出，连按快捷键时看到
        // 的就是这个。这里改成续住停留，见 ContinueDwellAsync。
        if (!replayIntro && _play == PlayKind.Full && IsVisible && await ContinueDwellAsync(o))
            return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        bool debugStatic = Array.Exists(Environment.GetCommandLineArgs(), a => a == "--debug-ring");

        ResetToInitial();

        ShowPositioned();

        _play = PlayKind.Full;
        _playClock.Restart();

        if (debugStatic)
        {
            ApplyCState();
            try { await Task.Delay(1500, ct); }
            catch (OperationCanceledException) { return; }
            if (!ct.IsCancellationRequested && IsVisible)
                Hide();

            _play = PlayKind.None;
            return;
        }

        // 波纹方向 = 能量方向：充电向外扩散，电池模式向内收拢到电标圆心。
        // 三圈共用同一个函数签名，直接选方法组，避免把整段时间线复制两遍。
        Func<AnimationOptions, double, double, Animation> ripple =
            mode == HudPlayMode.Battery ? HudAnimations.RippleIn : HudAnimations.Ripple;

        var tasks = new List<Task>
        {
            HudAnimations.PillCorner(o).RunAsync(Pill, ct),
            HudAnimations.PillAppear(o).RunAsync(Pill, ct),
            HudAnimations.PillHeight(o).RunAsync(Pill, ct),
            HudAnimations.PillHeight(o).RunAsync(RippleHost, ct),
            HudAnimations.ScaleOut(o).RunAsync(ScaleHost, ct),
            HudAnimations.BoltIcon(o).RunAsync(BoltIcon, ct),
            HudAnimations.RippleHost(o).RunAsync(RippleHost, ct),
            HudAnimations.CircleForm(o).RunAsync(CircleForm, ct),
            HudAnimations.SquareForm(o).RunAsync(SquareForm, ct),
            HudAnimations.TitleHost(o).RunAsync(TitleHost, ct),
            HudAnimations.NumHost(o).RunAsync(NumHost, ct),
            ripple(o, 1.5, 0.50).RunAsync(RippleInner, ct),
            ripple(o, 2.0, 0.50).RunAsync(RippleMid, ct),
            ripple(o, 2.5, 0.60).RunAsync(RippleOuter, ct),
            HudAnimations.RippleRise(o).RunAsync(RippleInnerHost, ct),
            HudAnimations.RippleRise(o).RunAsync(RippleMidHost, ct),
            HudAnimations.RippleRise(o).RunAsync(RippleOuterHost, ct),
        };
        tasks.AddRange(_extraPills.Select(
            (pill, i) => HudAnimations.ExtraPill(o, i).RunAsync(pill, ct)));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ct.IsCancellationRequested && IsVisible)
            Hide();

        _play = PlayKind.None;
    }

    /// <summary>
    /// 播放中再次唤起：不重播开场，改为"续住"当前画面。
    ///
    /// 只有正处在 C 态停留段（开场已走完、收尾还没开始）才续 —— 这段时间里 17 路动画
    /// 全是静止值，把 C 态钉成局部值后画面与刚才逐像素一致，再重跑一遍「停留 → 收尾」，
    /// 等于把停留计时重新开始，全程没有画面断裂。开场中、或已经进入收尾之后返回 false，
    /// 交由调用方走正常重播（那时屏上本来就没内容 / 正在缩没，重播看不到闪灭）。
    /// </summary>
    /// <returns>true = 已接管本次唤起（调用方直接返回）；false = 请调用方走正常重播。</returns>
    private async Task<bool> ContinueDwellAsync(AnimationOptions o)
    {
        double elapsed = _playClock.Elapsed.TotalSeconds;
        if (elapsed < HudAnimations.IntroSeconds || elapsed >= HudAnimations.CloseStartSeconds(o))
            return false;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 撤掉动画后属性会回落到局部值：先把 C 态钉住，否则整块 HUD 会瞬间变成不可见
        ApplyCState();

        _playClock.Restart();

        try
        {
            await HudAnimations.DwellTail(o).RunAsync(ScaleHost, ct);
        }
        catch (OperationCanceledException)
        {
            return true;
        }

        _play = PlayKind.None;
        if (!ct.IsCancellationRequested && IsVisible)
            Hide();

        return true;
    }

    private async Task DismissAsync()
    {
        _cts?.Cancel();
        _play = PlayKind.None;

        var fade = new Animation
        {
            Duration = DismissDuration,
            FillMode = FillMode.Forward,
            Easing = new QuadraticEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 0d) } },
            },
        };

        await fade.RunAsync(Root);
        Hide();
    }

    // ---------------- 数据绑定 ----------------

    private const double RingDiameter = 46d;
    private const double RingThickness = 4.5d;

    /// <summary>应用适配器读数：第一位适配器驱动主药丸（有内置电池即内置，台式机则为第一个插件），其余各自生成一粒药丸排在下方。</summary>
    private void ApplyReadings(IReadOnlyList<AdapterReading> readings)
    {
        var primary = readings.Count > 0 ? readings[0] : null;

        ApplyBattery(primary?.Snapshot);
        BuildExtraPills(readings.Skip(1));
    }

    private void ApplyBattery(BatterySnapshot? snap)
    {
        double fraction = 0d;
        double loadPercent = 0d;
        bool noBattery = snap is null || !snap.HasBattery;

        if (noBattery)
        {
            // 台式机 / 读不到电池：功耗模式（来自 dev），显示整机瓦数与设备负载
            var load = _perfProvider?.Invoke();
            WhValueText.Text = load?.Watts is { } w ? w.ToString("F0") : "--";
            WhMaxText.Text = "W";
            loadPercent = load?.LoadPercent ?? 0d;
            PercentText.Text = load is null ? "--" : loadPercent.ToString("F0");
            fraction = Math.Clamp(loadPercent / 100d, 0d, 1d);
        }
        else if (snap is not null)
        {
            WhValueText.Text = (snap.RemainingWh * 1000).ToString("F0");
            WhMaxText.Text = $"/{snap.FullWh * 1000:F0}";
            PercentText.Text = snap.Percent.ToString();
            fraction = Math.Clamp(snap.Percent / 100d, 0d, 1d);
        }

        BadgeArc.Data = BuildRingGeometry(fraction, RingDiameter, RingThickness);

        var badgeColor = (snap is not null && snap.HasBattery && snap.Percent < 20)
                         || (noBattery && loadPercent >= 90d)
            ? BadgeColorLow
            : BadgeColorNormal;
        BadgeArc.Stroke = new SolidColorBrush(badgeColor);
        BadgeLaptopIcon.Foreground = new SolidColorBrush(badgeColor);
        BadgeChipIcon.Foreground = new SolidColorBrush(badgeColor);

        // 台式机 / 读不到电池：徽章显示开发板
        BadgeLaptop.IsVisible = !noBattery;
        BadgeChip.IsVisible = noBattery;
    }

    // ---------------- 插件适配器药丸 ----------------

    /// <summary>重建插件适配器药丸（每次展示前调用，数据总是最新）。</summary>
    private void BuildExtraPills(IEnumerable<AdapterReading> extras)
    {
        _extraPills.Clear();
        ExtraPillsHost.Children.Clear();

        foreach (var reading in extras)
        {
            var pill = BuildExtraPill(reading);
            _extraPills.Add(pill);
            ExtraPillsHost.Children.Add(pill);
        }
    }

    /// <summary>
    /// 构建一粒插件适配器药丸：与主药丸同款 560×60 圆胶囊，
    /// 左侧电标方块 + 适配器名，右侧百分比 + 插头徽章（进度环按电量着色，&lt;20% 变红）。
    /// </summary>
    private Border BuildExtraPill(AdapterReading reading)
    {
        var snap = reading.Snapshot;
        bool low = snap is not null && snap.HasBattery && snap.Percent < 20;
        var accent = new SolidColorBrush(low ? BadgeColorLow : BadgeColorNormal);

        var pill = new Border
        {
            Width = 560,
            Height = 60,
            CornerRadius = new CornerRadius(30),
            Background = new SolidColorBrush(Color.Parse("#FF312F30")),
            ClipToBounds = true,
            Opacity = 0,
            RenderTransform = new TranslateTransform(0, 14),
        };

        // 列结构对齐主药丸 NumHost：左内边距 32 + 电标 18 + 间隙 14 + 名称 + 留空 + 百分比 + 间隙 + 徽章 + 右内边距 5
        var layout = new Grid { Width = 560, VerticalAlignment = VerticalAlignment.Center };
        layout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(32)));
        layout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(18)));
        layout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(14)));
        layout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        layout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        layout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        layout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(14)));
        layout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        layout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(5)));

        // 电标方块（与主药丸 C 态同款）
        var bolt = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4.5),
            Background = new SolidColorBrush(Color.Parse("#FFE9E7E4")),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new PathIcon
            {
                Width = 12,
                Height = 12,
                Data = ResolveGeometry("Geo.Bolt"),
                Foreground = new SolidColorBrush(Color.Parse("#FF141313")),
            },
        };
        Grid.SetColumn(bolt, 1);
        layout.Children.Add(bolt);

        // 适配器名
        var name = new TextBlock
        {
            Text = reading.Adapter.Name,
            FontFamily = ResolveFont("Hud.FontFamily"),
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            Opacity = 0.85,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 3);
        layout.Children.Add(name);

        // 百分比（与主药丸同款数字字体）
        var pct = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
        };
        pct.Children.Add(new TextBlock
        {
            Text = snap is not null && snap.HasBattery ? snap.Percent.ToString() : "--",
            FontFamily = ResolveFont("Hud.FontFamilyNumeric"),
            FontSize = 22,
            FontWeight = FontWeight.Medium,
            Foreground = Brushes.White,
        });
        pct.Children.Add(new TextBlock
        {
            Text = "%",
            FontFamily = ResolveFont("Hud.FontFamilyNumeric"),
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            Foreground = Brushes.White,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(pct, 5);
        layout.Children.Add(pct);

        // 插头徽章：进度环 + 插头图标（区别于主药丸的笔记本电脑徽章）
        double fraction = snap is not null && snap.HasBattery
            ? Math.Clamp(snap.Percent / 100d, 0d, 1d)
            : 0d;

        var badge = new Grid { Width = 46, Height = 46, VerticalAlignment = VerticalAlignment.Center };
        badge.Children.Add(new Ellipse
        {
            Width = 46,
            Height = 46,
            Fill = new SolidColorBrush(Color.Parse("#FF262425")),
        });
        badge.Children.Add(new ShapePath
        {
            Width = 46,
            Height = 46,
            Stroke = accent,
            StrokeThickness = 4.5,
            StrokeLineCap = PenLineCap.Round,
            Data = BuildRingGeometry(fraction, RingDiameter, RingThickness),
        });
        badge.Children.Add(new PathIcon
        {
            Width = 12,
            Height = 12,
            Data = ResolveGeometry("Geo.Cable"),
            Foreground = accent,
        });
        Grid.SetColumn(badge, 7);
        layout.Children.Add(badge);

        pill.Child = layout;
        return pill;
    }

    /// <summary>把额外药丸钉在"完全可见"状态：撤掉动画后属性回落到局部值，不钉住会瞬间消失。</summary>
    private void PinExtraPills()
    {
        foreach (var pill in _extraPills)
        {
            pill.Opacity = 1;
            if (pill.RenderTransform is TranslateTransform t)
                t.Y = 0;
        }
    }

    /// <summary>从窗口资源里取图标几何；取不到返回 null（图标不渲染，不影响其余内容）。</summary>
    private Geometry? ResolveGeometry(string key)
        => TryGetResource(key, ActualThemeVariant, out var v) && v is Geometry g ? g : null;

    /// <summary>从窗口资源里取字体；取不到回落到默认字体（仅防御，正常必有）。</summary>
    private FontFamily ResolveFont(string key)
        => TryGetResource(key, ActualThemeVariant, out var v) && v is FontFamily f ? f : FontFamily.Default;

    private static Geometry BuildRingGeometry(double fraction, double diameter, double thickness)
    {
        double radius = (diameter - thickness) / 2d;
        var center = new Point(diameter / 2d, diameter / 2d);

        double sweep = 360d * Math.Clamp(fraction, 0d, 1d);
        if (sweep < 0.5d) sweep = 0.5d;
        if (sweep > 359.5d) sweep = 359.5d;

        const double startAngle = -90d;
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

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180d;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }

    // ---------------- FPS 计数器 ----------------

    private void StartFpsCounter()
    {
        DispatcherTimer.Run(() =>
        {
            if (!IsVisible)
            {
                _fpsFrameCount = 0;
                _fpsLastMeasure = DateTime.UtcNow;
                return true;
            }

            _fpsFrameCount++;

            var now = DateTime.UtcNow;
            var elapsed = (now - _fpsLastMeasure).TotalSeconds;
            if (elapsed >= 1.0)
            {
                double fps = _fpsFrameCount / elapsed;
                FpsText.Text = $"{fps:F0} FPS";
                _fpsFrameCount = 0;
                _fpsLastMeasure = now;
            }

            return true;
        }, TimeSpan.FromMilliseconds(200));
    }

    // ---------------- 动画复位 ----------------

    private void ResetToInitial()
    {
        ResetPanel();
        Root.Opacity = 1;

        ScaleHost.RenderTransform = new ScaleTransform(1d, 1d);

        Pill.Width = 560;
        Pill.Height = 60;
        Pill.CornerRadius = new CornerRadius(30d);
        Pill.Opacity = 0;
        Pill.RenderTransform = new ScaleTransform(0.6d, 0.6d);

        RippleHost.RenderTransform = new TranslateTransform(0d, 0d);

        BoltIcon.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(0.4d, 0.4d), new TranslateTransform(0d, 0d) },
        };
        BoltIcon.Opacity = 0;

        RippleInnerHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleMidHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleOuterHost.RenderTransform = new TranslateTransform(0d, 16d);

        RippleInner.RenderTransform = new ScaleTransform(0d, 0d);
        RippleInner.Opacity = 0;
        RippleMid.RenderTransform = new ScaleTransform(0d, 0d);
        RippleMid.Opacity = 0;
        RippleOuter.RenderTransform = new ScaleTransform(0d, 0d);
        RippleOuter.Opacity = 0;

        CircleForm.Opacity = 0;
        SquareForm.Opacity = 0;

        TitleHost.RenderTransform = new TranslateTransform(0d, 0d);
        TitleHost.Opacity = 0;

        NumHost.RenderTransform = new TranslateTransform(0d, 0d);
        NumHost.Opacity = 0;
    }

    /// <summary>
    /// 把画面钉成 C 态（收窄后的电量胶囊：电标贴左、波纹归零、百分比可见）的静态局部值。
    /// 两处用：--debug-ring 的静态呈现；以及播放中再次唤起"续住停留"之前 —— 撤掉动画后
    /// 属性会回落到局部值，不先钉住整块 HUD 会瞬间变不可见（那正是连按时看到的那一"闪"）。
    /// </summary>
    private void ApplyCState()
    {
        Root.Opacity = 1;
        ScaleHost.RenderTransform = new ScaleTransform(1d, 1d);
        Pill.Width = 560;
        Pill.Height = 60;
        Pill.CornerRadius = new CornerRadius(30d);
        Pill.Opacity = 1;
        Pill.RenderTransform = new ScaleTransform(1d, 1d);

        RippleHost.Height = 60;
        RippleHost.RenderTransform = new TranslateTransform(-245d, 0d);

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

        // 额外药丸一并钉住（C 态停留段里它们同样只有静止值）
        PinExtraPills();
    }

    private void SetSimpleCState()
    {
        Pill.Width = 560;
        Pill.Height = 60;
        Pill.CornerRadius = new CornerRadius(30d);
        Pill.Opacity = 0;
        Pill.RenderTransform = new ScaleTransform(0.6d, 0.6d);

        BoltIcon.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(1d, 1d), new TranslateTransform(-245d, 0d) },
        };
        BoltIcon.Opacity = 0;
        CircleForm.Opacity = 0;
        SquareForm.Opacity = 1;

        TitleHost.Opacity = 0;
        RippleHost.Height = 60;
        RippleHost.RenderTransform = new TranslateTransform(0d, 0d);
        RippleInnerHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleMidHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleOuterHost.RenderTransform = new TranslateTransform(0d, 16d);
        RippleInner.Opacity = 0;
        RippleMid.Opacity = 0;
        RippleOuter.Opacity = 0;

        NumHost.RenderTransform = new TranslateTransform(0d, 0d);
        NumHost.Opacity = 0;
    }

    // ---------------- 定位（多显示器 + 位置选择） ----------------

    private void PositionTopCenter()
    {
        var screen = ResolveScreen(_settings.MonitorIndex);
        if (screen is null) return;

        var area = screen.WorkingArea;

        // screen.Scaling 来自显示器 DPI 枚举，比窗口的 RenderScaling 可靠（后者首帧前可能未更新）
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        int pixelWidth = (int)Math.Round(Width * scaling);

        int x = _settings.HudPosition switch
        {
            HudPosition.TopLeft => area.X + 10,
            HudPosition.TopRight => area.X + area.Width - pixelWidth - 10,
            _ => area.X + (area.Width - pixelWidth) / 2, // TopCenter
        };

        Position = new PixelPoint(x, area.Y + 4);
    }

    /// <summary>
    /// 解析目标显示器：-1 = 主显示器（默认），0..N-1 = 显示器列表索引，越界退回主显示器。
    /// </summary>
    private Avalonia.Platform.Screen? ResolveScreen(int monitorIndex)
    {
        var screens = Screens.All;
        var primary = Screens.Primary;

        if (monitorIndex < 0)
            return primary ?? screens.FirstOrDefault();

        if (monitorIndex < screens.Count)
            return screens[monitorIndex];

        return primary ?? screens.FirstOrDefault();
    }

    private void ShowPositioned()
    {
        PositionTopCenter();

        if (!IsVisible)
            Show();

        PositionTopCenter();
        Dispatcher.UIThread.Post(() =>
        {
            PositionTopCenter();
        }, DispatcherPriority.Loaded);
    }
    // ---------------- 性能面板：点击胶囊 ⇄ 圆角矩形形变（来自 dev） ----------------

    private void TogglePanel()
    {
        if (_panelBusy)
            return;

        if (_panelOpen)
            _ = ClosePanelAsync();
        else
            _ = OpenPanelAsync();
    }

    private async Task OpenPanelAsync()
    {
        _panelBusy = true;
        _panelOpen = true;

        _cts?.Cancel();
        ApplyCState();

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
        }

        NumHost.Opacity = 0;
        _panelBusy = false;
    }

    private async Task ClosePanelAsync()
    {
        _panelBusy = true;
        _panelOpen = false;
        StopPanelTimer();

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
        }

        PerfPanel.IsVisible = false;
        NumHost.Opacity = 1;
        _panelBusy = false;
    }

    private void ResetPanel()
    {
        _panelOpen = false;
        _panelBusy = false;
        StopPanelTimer();
        PerfPanel.IsVisible = false;
        PerfPanel.Opacity = 1;
        SquareBoltIcon.IsVisible = true;
        SquarePerfIcon.IsVisible = false;
        NumHost.Opacity = 1;
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

}