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

namespace EndfieldCharge.Views;

/// <summary>
/// 工业模式风格的电量 HUD：三态动画——
/// 中心小胶囊（电标居中）→ 长条矩形（电标左移 + 工业模式）→
/// 左侧小胶囊（数字 + 徽章）→ 停留数秒 → 收尾关掉。
/// </summary>
public partial class HudWindow : Window
{
    private static readonly TimeSpan DismissDuration = TimeSpan.FromMilliseconds(160);

    // 徽章电量颜色：≥20% 黄绿，<20% 红
    private static readonly Color BadgeColorNormal = Color.Parse("#C6CA4C");
    private static readonly Color BadgeColorLow = Color.Parse("#FF4D4F");

    private CancellationTokenSource? _cts;

    public HudWindow()
    {
        InitializeComponent();

        Cursor = new Cursor(StandardCursorType.Hand);
        PointerPressed += (_, _) => _ = DismissAsync();

        ResetToInitial();
    }

    /// <summary>
    /// 简化版：拔电时只弹"电量圆胶囊"——无电标先出、无工业模式矩形、无波纹。
    /// 全圆胶囊直接带电量内容弹出 → 停留 → 整体缩小收回。
    /// </summary>
    public async Task ShowSimpleAsync(BatterySnapshot? battery)
    {
        ApplyBattery(battery, acOnline: false);

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

    /// <summary>填入电量数据并播放一次完整动画。</summary>
    public async Task ShowAndPlayAsync(BatterySnapshot? battery, bool acOnline)
    {
        ApplyBattery(battery, acOnline);

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

        try
        {
            await Task.WhenAll(
                HudAnimations.PillCorner().RunAsync(Pill, ct),
                HudAnimations.PillAppear().RunAsync(Pill, ct),
                HudAnimations.PillHeight().RunAsync(Pill, ct),
                HudAnimations.PillHeight().RunAsync(RippleHost, ct),
                HudAnimations.ScaleOut().RunAsync(ScaleHost, ct),
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
                HudAnimations.RippleRise().RunAsync(RippleOuterHost, ct));
        }
        catch (OperationCanceledException)
        {
            return; // 被新的触发或手动收起打断，保留当前状态交由下一轮处理
        }

        if (!ct.IsCancellationRequested && IsVisible)
            Hide();
    }

    /// <summary>提前收起（点击时）。</summary>
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

    // ---------------- 数据绑定（直接赋值，无 MVVM 开销） ----------------

    private const double RingDiameter = 46d;
    private const double RingThickness = 4.5d;

    private void ApplyBattery(BatterySnapshot? snap, bool acOnline)
    {
        double fraction = 0d;

        if (snap is null || !snap.HasBattery)
        {
            // 台式机 / 读不到电池：不编数字，圆环留空
            WhValueText.Text = "--";
            WhMaxText.Text = string.Empty;
            PercentText.Text = "--";
        }
        else
        {
            // mWh（原视频即显示 mWh 整数，如 "3725 /4240"）
            WhValueText.Text = (snap.RemainingWh * 1000).ToString("F0");
            WhMaxText.Text = $"/{snap.FullWh * 1000:F0}";
            PercentText.Text = snap.Percent.ToString();
            fraction = Math.Clamp(snap.Percent / 100d, 0d, 1d);
        }

        // 电量圆环：弧长按电量百分比绘制（0% 时不画，100% 时几乎闭合）
        BadgeArc.Data = BuildRingGeometry(fraction, RingDiameter, RingThickness);

        // 电量圈变色：≥20% 黄绿，<20% 红色
        var badgeColor = snap is not null && snap.HasBattery && snap.Percent < 20
            ? BadgeColorLow
            : BadgeColorNormal;
        BadgeArc.Stroke = new SolidColorBrush(badgeColor);
        LaptopScreen.BorderBrush = new SolidColorBrush(badgeColor);
        LaptopBase.Background = new SolidColorBrush(badgeColor);
        BadgeElectrode.Background = new SolidColorBrush(badgeColor);
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
