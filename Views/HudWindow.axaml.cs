using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
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

    /// <summary>填入电量数据并播放一次完整动画。</summary>
    public async Task ShowAndPlayAsync(BatterySnapshot? battery, bool acOnline)
    {
        ApplyBattery(battery, acOnline);
        PositionTopCenter();

        // 打断上一次播放
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        bool debugStatic = Array.Exists(Environment.GetCommandLineArgs(), a => a == "--debug-ring");

        ResetToInitial();

        if (!IsVisible)
            Show();

        if (!IsVisible)
            Show();

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

    private void ApplyBattery(BatterySnapshot? snap, bool acOnline)
    {
        if (snap is null || !snap.HasBattery)
        {
            // 台式机 / 读不到电池：不编数字
            WhValueText.Text = "--";
            WhMaxText.Text = string.Empty;
            PercentText.Text = "--";
            return;
        }

        // mWh（原视频即显示 mWh 整数，如 "3725 /4240"）
        WhValueText.Text = (snap.RemainingWh * 1000).ToString("F0");
        WhMaxText.Text = $"/{snap.FullWh * 1000:F0}";
        PercentText.Text = snap.Percent.ToString();

        // 电量圈变色：≥20% 黄绿，<20% 红色
        var badgeColor = snap.Percent < 20 ? BadgeColorLow : BadgeColorNormal;
        BadgeRing.Stroke = new SolidColorBrush(badgeColor);
        LaptopScreen.BorderBrush = new SolidColorBrush(badgeColor);
        LaptopBase.Background = new SolidColorBrush(badgeColor);
        BadgeElectrode.Background = new SolidColorBrush(badgeColor);
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

        RippleHost.RenderTransform = new TranslateTransform(-259d, 0d);

        // 电标：贴胶囊左侧（pill 560, left=320, 方块 18 半宽 9 → icon center = 320+12+9 = 341, TX=-259）
        BoltIcon.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(1d, 1d), new TranslateTransform(-259d, 0d) },
        };
        BoltIcon.Opacity = 1;
        CircleForm.Opacity = 0;
        SquareForm.Opacity = 1;

        TitleHost.Opacity = 0;

        NumHost.RenderTransform = new TranslateTransform(0d, 0d);
        NumHost.Opacity = 1;
    }

    // ---------------- 定位 ----------------

    /// <summary>把窗口贴到当前屏幕的顶部居中。</summary>
    private void PositionTopCenter()
    {
        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        double scaling = screen.Scaling <= 0 ? 1d : screen.Scaling;

        // 窗口逻辑宽度不得超过屏幕（小屏 / 高 DPI 下 1200 会超出）
        double maxLogicalWidth = area.Width / scaling;
        if (Width > maxLogicalWidth)
            Width = maxLogicalWidth;

        int pixelWidth = (int)Math.Round(Width * scaling);
        int x = area.X + (area.Width - pixelWidth) / 2;
        int y = area.Y;

        Position = new PixelPoint(x, y);
    }
}
