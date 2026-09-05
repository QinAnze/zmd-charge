using System;
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
using EndfieldCharge.Services;
using EndfieldCharge.Settings;

namespace EndfieldCharge.Views;

/// <summary>完整三态动画的文案主题：充电（超充模式）或省电模式。</summary>
public enum HudPlayMode
{
    Charge,
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

    public HudWindow()
    {
        InitializeComponent();

        TagLineText.Text = Localization.TagLine;
        TitleText.Text = Localization.TitleMode;

        Cursor = new Cursor(StandardCursorType.Hand);
        PointerPressed += (_, _) => _ = DismissAsync();

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

    public async Task ShowSimpleAsync(BatterySnapshot? battery, AnimationOptions? options = null)
    {
        ApplyBattery(battery, acOnline: false);

        var o = options ?? _animOptions;

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
                HudAnimations.SimplePillAppear(o).RunAsync(Pill, ct),
                HudAnimations.SimpleFadeIn(o).RunAsync(BoltIcon, ct),
                HudAnimations.SimpleFadeIn(o).RunAsync(NumHost, ct),
                HudAnimations.SimpleScaleOut(o).RunAsync(ScaleHost, ct));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ct.IsCancellationRequested && IsVisible)
            Hide();
    }

    public async Task ShowAndPlayAsync(
        BatterySnapshot? battery,
        bool acOnline,
        HudPlayMode mode = HudPlayMode.Charge,
        AnimationOptions? options = null)
    {
        ApplyBattery(battery, acOnline);

        // 文案主题：充电 = 超充模式；省电 = 省电模式
        TagLineText.Text = mode == HudPlayMode.PowerSaver ? Localization.TagLineSaver : Localization.TagLine;
        TitleText.Text = mode == HudPlayMode.PowerSaver ? Localization.TitleSaver : Localization.TitleMode;

        var o = options ?? _animOptions;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        bool debugStatic = Array.Exists(Environment.GetCommandLineArgs(), a => a == "--debug-ring");

        ResetToInitial();

        ShowPositioned();

        if (debugStatic)
        {
            ShowFullyExpandedStatic();
            try { await Task.Delay(1500, ct); }
            catch (OperationCanceledException) { return; }
            if (!ct.IsCancellationRequested && IsVisible)
                Hide();
            return;
        }

        try
        {
            await Task.WhenAll(
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
                HudAnimations.Ripple(o, 1.5, 0.50).RunAsync(RippleInner, ct),
                HudAnimations.Ripple(o, 2.0, 0.50).RunAsync(RippleMid, ct),
                HudAnimations.Ripple(o, 2.5, 0.60).RunAsync(RippleOuter, ct),
                HudAnimations.RippleRise(o).RunAsync(RippleInnerHost, ct),
                HudAnimations.RippleRise(o).RunAsync(RippleMidHost, ct),
                HudAnimations.RippleRise(o).RunAsync(RippleOuterHost, ct));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ct.IsCancellationRequested && IsVisible)
            Hide();
    }

    private async Task DismissAsync()
    {
        _cts?.Cancel();

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

    private void ApplyBattery(BatterySnapshot? snap, bool acOnline)
    {
        double fraction = 0d;

        if (snap is null || !snap.HasBattery)
        {
            WhValueText.Text = "--";
            WhMaxText.Text = string.Empty;
            PercentText.Text = "--";
        }
        else
        {
            WhValueText.Text = (snap.RemainingWh * 1000).ToString("F0");
            WhMaxText.Text = $"/{snap.FullWh * 1000:F0}";
            PercentText.Text = snap.Percent.ToString();
            fraction = Math.Clamp(snap.Percent / 100d, 0d, 1d);
        }

        BadgeArc.Data = BuildRingGeometry(fraction, RingDiameter, RingThickness);

        var badgeColor = snap is not null && snap.HasBattery && snap.Percent < 20
            ? BadgeColorLow
            : BadgeColorNormal;
        BadgeArc.Stroke = new SolidColorBrush(badgeColor);
        LaptopScreen.BorderBrush = new SolidColorBrush(badgeColor);
        LaptopBase.Background = new SolidColorBrush(badgeColor);
        BadgeElectrode.Background = new SolidColorBrush(badgeColor);
    }

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

    private void ShowFullyExpandedStatic()
    {
        ScaleHost.RenderTransform = new ScaleTransform(1d, 1d);
        Pill.Width = 560;
        Pill.Height = 60;
        Pill.CornerRadius = new CornerRadius(30d);
        Pill.Opacity = 1;
        Pill.RenderTransform = new ScaleTransform(1d, 1d);

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
}