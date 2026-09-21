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

        // 平台窗口增强：
        //   macOS 需要把 NSWindow 提升到弹窗层级并加入所有 Space，
        //   否则 HUD 会被菜单栏（层级 24）与全屏应用遮住 —— Avalonia 的 Topmost 只到层级 3。
        //   Windows 下是空实现。
        Opened += (_, _) => App.Platform?.OnHudWindowShown(this);

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

        // 缩放改变胶囊边界，下次播放重新测量布局
        _birthGeometry = null;

        // 更新本地化文本（可能语言变了）
        TagLineText.Text = Localization.TagLine;
        TitleText.Text = Localization.TitleMode;
    }

    // ---------------- 出生前导（macOS 刘海） ----------------

    /// <summary>
    /// HUD 相对工作区顶部的固定内缩（沿用改动前的位置：<c>WorkingArea.Y + 4</c>）。
    /// 必须与 <see cref="PositionTopCenter"/> 中的取值保持一致，
    /// 否则出生的落点与最终落点会对不上（几何换算在同名常量上做）。
    /// </summary>
    private const double HudTopOffset = 4d;

    /// <summary>缓存的出生几何；缩放/位置/显示器/语言变化时置空重算。</summary>
    private NotchBirthGeometry? _birthGeometry;

    // 屏幕原点为窗口原点，避免在全局混合 DPI 坐标中减减除除。
    private double _notchHeight;
    private bool _macOverlay;

    private void RefreshBirthGeometry()
    {
        _birthGeometry = null;
        if (!_macOverlay || _notchHeight <= 0) return;

        // 布局完成后测量真实顶边中心，代替多层 Center 容器的推测公式。
        Pill.RenderTransform = new ScaleTransform(1d, 1d);
        UpdateLayout();
        var top = Pill.TranslatePoint(new Point(Pill.Bounds.Width / 2d, 0d), this);
        if (top is null) return;
        _birthGeometry = NotchBirthGeometry.FromLayout(Width, _notchHeight,
            _settings.GlobalScale, top.Value.X, top.Value.Y);
        Logger.Info($"HudWindow: {_birthGeometry}");
    }

    private static double BirthLeadSeconds(NotchBirthGeometry geometry)
        => geometry.IsActive ? Math.Clamp(0.42d + Math.Abs(geometry.OffX) / 2000d, 0.42d, 0.65d) : 0d;

    /// <summary>
    /// 把出生前导参数并入要播放的 <paramref name="o"/>。
    /// 调用方显式传入 options（设置页实时预览）时，只覆盖动画微调参数，
    /// 出生前导仍按当前平台/屏幕几何附加 —— 否则预览里看不到这段前导。
    /// </summary>
    private AnimationOptions WithBirthGeometry(AnimationOptions o)
    {
        RefreshBirthGeometry();

        if (_birthGeometry is not { } geometry)
            return o;

        BirthHost.RenderTransform = new TranslateTransform(geometry.OffX, geometry.OffY);
        Pill.RenderTransform = new ScaleTransform(geometry.BirthScaleX, geometry.BirthScaleY);
        return o with
        {
            BirthLeadSeconds = BirthLeadSeconds(geometry),
            BirthOffsetX = geometry.OffX,
            BirthOffsetY = geometry.OffY,
            BirthScaleX = geometry.BirthScaleX,
            BirthScaleY = geometry.BirthScaleY,
        };
    }

    /// <summary>
    /// 出生几何实测探针（仅当启动参数含 <c>--probe-notch</c> 时输出）。
    ///
    /// 目的：把"公式推导"换成"实测数字"。胶囊位于 GlobalScale / ScaleHost 两层缩放之内，
    /// 渲染顶点与布局顶点相差若干缩放项，靠推导极易出错（本次已踩两次）。
    /// 这里直接量三种状态的真实顶点：
    ///   1. final              —— offY=0, sY=1
    ///   2. birth-with-offset  —— offY=BirthOffsetY, sY=BirthScaleY
    ///   3. birth-no-offset    —— offY=0,            sY=BirthScaleY
    /// 由 (1)(3) 分离出"缩放贡献"，由 (2)(3) 分离出"位移贡献"，即可反解出正确的 offY。
    /// </summary>
    private void ProbeBirthGeometry(AnimationOptions o)
    {
        if (!Array.Exists(Environment.GetCommandLineArgs(), a => a == "--probe-notch"))
            return;

        try
        {
            if (Pill.RenderTransform is not ScaleTransform scale ||
                BirthHost.RenderTransform is not TranslateTransform birthHost)
                return;

            double savedScaleX = scale.ScaleX, savedScaleY = scale.ScaleY;
            double savedOffX = birthHost.X, savedOffY = birthHost.Y;

            double MeasureTop(string label)
            {
                BirthHost.UpdateLayout();
                var point = Pill.TranslatePoint(new Avalonia.Point(0, 0), Root);
                double top = point?.Y ?? double.NaN;
                Logger.Info($"NOTCHPROBE {label,-20} pillTopInRoot={top,8:F3} " +
                            $"offY={birthHost.Y,8:F3} sY={scale.ScaleY:F4}");
                return top;
            }

            birthHost.X = 0d;
            birthHost.Y = 0d;
            scale.ScaleX = 1d;
            scale.ScaleY = 1d;
            double topFinal = MeasureTop("final");

            birthHost.X = o.BirthOffsetX;
            birthHost.Y = o.BirthOffsetY;
            scale.ScaleX = o.BirthScaleX;
            scale.ScaleY = o.BirthScaleY;
            double topBirth = MeasureTop("birth-with-offset");

            birthHost.X = 0d;
            birthHost.Y = 0d;
            double topBirthNoOffset = MeasureTop("birth-no-offset");

            var g = _birthGeometry!.Value;
            double ui = g.UiScale;

            Logger.Info(
                $"NOTCHPROBE 期望: safeBottom={g.SafeAreaBottom:F2} pillTopFinal={g.PillTopFinal:F2} ui={ui:F2}");
            Logger.Info(
                $"NOTCHPROBE 实测: final={topFinal:F3} birth(带位移)={topBirth:F3} birth(无位移)={topBirthNoOffset:F3}");
            Logger.Info(
                $"NOTCHPROBE 拆解: 缩放贡献={(topBirthNoOffset - topFinal) / ui:F3}(GlobalScale 坐标) " +
                $"位移贡献={(topBirth - topBirthNoOffset) / ui:F3} 当前offY={o.BirthOffsetY:F3}");
            Logger.Info(
                $"NOTCHPROBE 结论: 出生顶点偏差={(topBirth - g.SafeAreaBottom) / ui:F3}(GlobalScale 坐标)，" +
                $"offY 应为 {o.BirthOffsetY - (topBirth - g.SafeAreaBottom) / ui:F3}");

            birthHost.X = savedOffX;
            birthHost.Y = savedOffY;
            scale.ScaleX = savedScaleX;
            scale.ScaleY = savedScaleY;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
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

        // Pill 仍透明：先完成窗口布局，再按真实控件坐标准备出生状态。
        ShowPositioned();
        o = WithBirthGeometry(o);

        try
        {
            await Task.WhenAll(
                HudAnimations.SimpleBirthOffset(o).RunAsync(BirthHost, ct),
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

        // Pill 仍透明：先完成窗口布局，再按真实控件坐标准备出生状态。
        ShowPositioned();
        o = WithBirthGeometry(o);

        if (o.HasBirthLead)
            ProbeBirthGeometry(o);

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
                HudAnimations.BirthOffset(o).RunAsync(BirthHost, ct),
                HudAnimations.BirthPillScale(o).RunAsync(Pill, ct),
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

        // 出生前导的位移必须复位，否则取消/重复触发后下一次会从残留位置起播
        BirthHost.RenderTransform = new TranslateTransform(0d, 0d);

        ScaleHost.Opacity = 1;
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
        ScaleHost.Opacity = 1;
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

        if (OperatingSystem.IsMacOS())
        {
            double scale = screen.Scaling > 0 ? screen.Scaling : 1d;
            var bounds = screen.Bounds;
            double screenWidth = bounds.Width / scale;
            double workTop = (screen.WorkingArea.Y - bounds.Y) / scale;
            _notchHeight = App.Platform?.TryGetScreenSafeAreaTop(bounds.X / scale, bounds.Y / scale) ?? 0d;
            if (_notchHeight <= 0 && Array.Exists(Environment.GetCommandLineArgs(), a => a == "--notch-birth"))
                _notchHeight = Math.Max(32d, workTop);
            // 无刘海的 macOS 屏幕也按胶囊边缘对齐左上/右上，但不播放刘海轨道。
            _macOverlay = true;
            {
                // 覆盖整个运动路径；Root 保持原 HUD 布局，只有定位变换改变。
                Width = screenWidth;
                Height = Math.Max(workTop, _notchHeight) + 180d * Math.Max(1d, _settings.GlobalScale);
                Position = bounds.Position;
#if ENDFIELD_MACOS
                var handle = TryGetPlatformHandle();
                if (handle is not null && handle.Handle != IntPtr.Zero)
                    MacOSAppKit.PositionOverlay(handle.Handle, bounds.X / scale, bounds.Y / scale);
#endif
                Root.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                // Root 内部已有居中留白；仅留 6 DIP，容纳撑高阶段的轻微回弹。
                Root.Margin = new Thickness(0, Math.Max(workTop, _notchHeight) + HudTopOffset + 6d, 0, 0);
                int side = _settings.HudPosition == HudPosition.TopLeft ? -1 :
                    _settings.HudPosition == HudPosition.TopRight ? 1 : 0;
                Root.RenderTransform = new TranslateTransform(
                    NotchBirthGeometry.FinalCenter(screenWidth, _settings.GlobalScale, side) - screenWidth / 2d, 0);
                return;
            }
        }
        _macOverlay = false;
        Width = 1200;
        Height = 160;
        Root.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Root.Margin = new Thickness(0);
        Root.RenderTransform = null;

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

        // 与 NotchBirthGeometry 使用同一个内缩常量，保证"出生落点"与"最终落点"一致
        Position = new PixelPoint(x, (int)Math.Round(area.Y + HudTopOffset * scaling));
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

        // macOS：Show() 之后原生 NSWindow 才存在，这里再补一次窗口层级增强
        App.Platform?.OnHudWindowShown(this);

        UpdateLayout();
    }
}
