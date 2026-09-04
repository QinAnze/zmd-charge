using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using EndfieldCharge.Settings;

namespace EndfieldCharge.Animations;

/// <summary>
/// 动画可调参数（来自设置窗口"动画"页，支持实时预览）。
/// </summary>
public sealed record AnimationOptions
{
    /// <summary>总时长（秒），3~10。</summary>
    public double DurationSeconds { get; init; } = 6.0;

    /// <summary>回弹强度 0~0.5，映射 KS_BackOut 第二控制点 Y = 1 + 值。</summary>
    public double BounceStrength { get; init; } = 0.275;

    /// <summary>波纹强度倍率 0~2。</summary>
    public double RippleIntensity { get; init; } = 1.0;

    /// <summary>波纹幅度倍率 0.5~1.5。</summary>
    public double RippleSpread { get; init; } = 1.0;

    public static AnimationOptions Default { get; } = new();

    public static AnimationOptions FromSettings(AppSettings s) => new()
    {
        DurationSeconds = Math.Clamp(s.DisplayDurationSeconds, 3d, 10d),
        BounceStrength = Math.Clamp(s.BounceStrength, 0d, 0.5d),
        RippleIntensity = Math.Clamp(s.RippleIntensity, 0d, 2d),
        RippleSpread = Math.Clamp(s.RippleSpread, 0.5d, 1.5d),
    };
}

/// <summary>
/// "灵动岛"三态时间线（高度 小→大→小，横向 560 恒定）：
///
///   0.04-0.10  电标单独弹出（缩放回弹）
///   0.07-0.09  胶囊背景弹出（scale 0.6→1, op 0→1）
///   0.09-0.12  A→B：高度 60→90 + CornerRadius 30→18（圆胶囊变高矩形）
///   0.12-0.20  电标从中间平滑滑到左侧 B 位（easeInOutCubic，无过冲）；
///               RippleHost 用同一条曲线同步跟随，三圈波纹同时从 0 扩散
///   0.20-0.25  「/// SUPER CHARGE MODE + 超充模式」居中淡入
///   0.25-0.30  状态 B 短停
///   0.30-0.36  B→C：标题与波纹一起淡化，高度 90→60，CornerRadius 18→30，
///               电标从 B 位滑到贴左 C 位
///   0.36-0.38  状态 C 停留，胶囊已收窄，电量数字短暂隐藏
///   0.38-0.42  电量数字与百分比淡入（等 C 态稳定后约 0.1s 再出）
///   0.42-0.86  完整状态 C 展示
///   0.86-0.89  整体缩小退出
///
/// 以上为 6s 基线时间线。实际播放时：入场段（0→0.42）保持基线绝对时长，
/// 停留段按 DurationSeconds 拉伸/压缩（见 MapCue）。
/// 注意：KeyFrame 的 Cue 必须严格递增（乱序会让某一段被压缩到 30ms，位移看起来像瞬移）。
/// </summary>
internal static class HudAnimations
{
    private const double BaselineSeconds = 6.0;
    private const double IntroEndCue = 0.42;   // 入场段结束（电量数字淡入完成）

    private const double TStart = 0.04;
    private const double TAppear = 0.07;
    private const double TPillOut = 0.09;
    private const double TBoltPop = 0.10;   // 电标弹出到位（缩放回弹峰值）
    private const double TExpand = 0.12;    // pill 撑高到 B 完成
    private const double TMove = 0.20;      // 电标 + RippleHost 平滑滑到左侧 B 位
    private const double TTitle = 0.25;     // 标题淡入完成
    private const double THoldB = 0.30;     // B 态结束
    private const double TContract = 0.36;  // B→C 收窄完成
    private const double THoldC = 0.86;
    private const double TClose = 0.89;

    private const double TNumIn = 0.38;     // 电量数字开始淡入（等 C 态稳定后约 0.1s）
    private const double TNumReady = 0.42;  // 电量数字淡入完成

    private const double PillRadiusA = 30d;
    private const double PillRadiusB = 18d;
    private const double PillHeightA = 60d;   // 状态 A 与 C（圆胶囊等高）
    private const double PillHeightB = 90d;   // 状态 B（工业模式高矩形）
    private const double IconOffsetB = -179d; // 状态 B：pill 560 宽，内 18% 处 → 600-0.32×560=420.8
    private const double IconOffsetC = -245d; // 状态 C：pill 左内 26 + 半方块 9 = 355 → TX=355-600

    private static readonly KeySpline KS_In = new(0.42, 0, 1, 1);
    private static readonly KeySpline KS_Out = new(0, 0, 0.58, 1);
    private static readonly KeySpline KS_InOut = new(0.42, 0, 0.58, 1);
    /// <summary>easeInOutCubic：位移专用——两端慢中间快，且不过冲，滑动观感最顺。</summary>
    private static readonly KeySpline KS_Smooth = new(0.65, 0, 0.35, 1);

    /// <summary>回弹曲线：过冲量由 BounceStrength 控制（0 = 无过冲的快出曲线）。</summary>
    private static KeySpline BackOut(AnimationOptions o) =>
        new(0.175, 0.885, 0.32, 1d + o.BounceStrength);

    /// <summary>
    /// 基线 cue → 实际时间线 cue。
    /// 入场段（≤0.42）固定占 0.42×6s=2.52s，剩余时间全给停留+退出段线性分配。
    /// DurationSeconds=6 时为恒等映射。
    /// </summary>
    private static double MapCue(AnimationOptions o, double cue)
    {
        double d = Math.Clamp(o.DurationSeconds, 3d, 10d);
        double introFrac = IntroEndCue * BaselineSeconds / d;
        if (cue <= IntroEndCue)
            return cue / IntroEndCue * introFrac;
        return introFrac + (cue - IntroEndCue) / (1 - IntroEndCue) * (1 - introFrac);
    }

    // ===================================================================

    /// <summary>胶囊圆角：30(全圆) ↔ 18(矩形圆角)。</summary>
    public static Animation PillCorner(AnimationOptions o)
    {
        var a = New(o);
        a.Children.Add(KF(MapCue(o, 0d), null, CR(PillRadiusA)));
        a.Children.Add(KF(MapCue(o, TAppear), KS_In, CR(PillRadiusA)));
        a.Children.Add(KF(MapCue(o, TExpand), KS_InOut, CR(PillRadiusB)));
        a.Children.Add(KF(MapCue(o, THoldB), KS_In, CR(PillRadiusB)));
        a.Children.Add(KF(MapCue(o, TContract), KS_InOut, CR(PillRadiusA)));
        return a;
    }

    /// <summary>
    /// 胶囊背景弹出：scale 0.6→1, op 0→1。
    /// 用回弹曲线带过冲效果，胶囊弹出时先略微过冲再回落，视觉张力更强。
    /// </summary>
    public static Animation PillAppear(AnimationOptions o)
    {
        var a = New(o);
        a.Children.Add(KF(MapCue(o, 0d), null, Op(0), SX(0.6), SY(0.6)));
        a.Children.Add(KF(MapCue(o, TStart), KS_In, Op(0), SX(0.6), SY(0.6)));
        a.Children.Add(KF(MapCue(o, TAppear), KS_In, Op(0), SX(0.6), SY(0.6)));
        a.Children.Add(KF(MapCue(o, TPillOut), BackOut(o), Op(1), SX(1d), SY(1d)));
        a.Children.Add(KF(MapCue(o, THoldC), KS_In, Op(1), SX(1d), SY(1d)));
        return a;
    }

    /// <summary>
    /// 胶囊高度：60(电标圆胶囊) → 90(工业模式高矩形，带回弹) → 60(电量圆胶囊)。
    /// 横向长度 560 恒定；独立于 CornerRadius 轨道，两者同段配合形成"胶囊撑高/收回"。
    /// 同时给 RippleHost 用同一动画，让波纹裁剪范围随 pill 高度同步变化。
    /// </summary>
    public static Animation PillHeight(AnimationOptions o)
    {
        var a = New(o);
        a.Children.Add(KF(MapCue(o, 0d), null, H(PillHeightA)));
        a.Children.Add(KF(MapCue(o, TAppear), KS_In, H(PillHeightA)));
        a.Children.Add(KF(MapCue(o, TExpand), BackOut(o), H(PillHeightB)));
        a.Children.Add(KF(MapCue(o, THoldB), KS_In, H(PillHeightB)));
        a.Children.Add(KF(MapCue(o, TContract), KS_InOut, H(PillHeightA)));
        a.Children.Add(KF(MapCue(o, THoldC), KS_In, H(PillHeightA)));
        return a;
    }

    /// <summary>收尾整体缩小（scale 1→0），ease-in 慢起快收。</summary>
    public static Animation ScaleOut(AnimationOptions o)
    {
        var a = New(o);
        a.Children.Add(KF(MapCue(o, 0d), null, SX(1d), SY(1d)));
        a.Children.Add(KF(MapCue(o, THoldC), KS_In, SX(1d), SY(1d)));
        a.Children.Add(KF(MapCue(o, TClose), KS_In, SX(0d), SY(0d)));
        return a;
    }

    /// <summary>
    /// 电标：弹出（缩放回弹）→ 停留中央 → 平滑滑到左侧 B 位 → B→C 再滑到贴左 C 位。
    /// 位移一律用 KS_Smooth（easeInOutCubic）+ 独立时间片，不用回弹曲线
    /// （它 80% 的行程在前 20% 时间内跑完，179px 看着就是瞬移）。
    /// </summary>
    public static Animation BoltIcon(AnimationOptions o)
    {
        var a = New(o);
        a.Children.Add(KF(MapCue(o, 0.00), null, Op(0), SX(0.4), SY(0.4), TX(0)));
        a.Children.Add(KF(MapCue(o, TStart), KS_In, Op(0), SX(0.4), SY(0.4), TX(0)));
        a.Children.Add(KF(MapCue(o, TBoltPop), BackOut(o), Op(1), SX(1.12), SY(1.12), TX(0)));
        a.Children.Add(KF(MapCue(o, TExpand), KS_Out, Op(1), SX(1), SY(1), TX(0)));
        a.Children.Add(KF(MapCue(o, TMove), KS_Smooth, Op(1), SX(1), SY(1), TX(IconOffsetB)));
        a.Children.Add(KF(MapCue(o, THoldB), KS_In, Op(1), SX(1), SY(1), TX(IconOffsetB)));
        a.Children.Add(KF(MapCue(o, TContract), KS_Smooth, Op(1), SX(1), SY(1), TX(IconOffsetC)));
        a.Children.Add(KF(MapCue(o, THoldC), KS_In, Op(1), SX(1), SY(1), TX(IconOffsetC)));
        return a;
    }

    /// <summary>
    /// RippleHost 跟随电标位移：状态 A 居中 → 状态 B 左移（与 BoltIcon 同步）。
    /// 必须与 BoltIcon 使用同一条曲线、同一段时间片，否则波纹会和电标脱开。
    /// </summary>
    public static Animation RippleHost(AnimationOptions o)
    {
        var a = New(o);
        a.Children.Add(KF(MapCue(o, 0d), null, TX(0)));
        a.Children.Add(KF(MapCue(o, TExpand), KS_In, TX(0)));
        a.Children.Add(KF(MapCue(o, TMove), KS_Smooth, TX(IconOffsetB)));
        a.Children.Add(KF(MapCue(o, THoldB), KS_In, TX(IconOffsetB)));
        a.Children.Add(KF(MapCue(o, TContract), KS_Smooth, TX(IconOffsetC)));
        return a;
    }

    /// <summary>圆形形态：随电标淡入，B→C 交叉淡化时让位给方形态。</summary>
    public static Animation CircleForm(AnimationOptions o)
    {
        var a = New(o);
        a.Children.Add(KF(MapCue(o, 0d), null, Op(0)));
        a.Children.Add(KF(MapCue(o, TStart), KS_In, Op(0)));
        a.Children.Add(KF(MapCue(o, TAppear), KS_Out, Op(1)));
        a.Children.Add(KF(MapCue(o, THoldB), KS_In, Op(1)));
        a.Children.Add(KF(MapCue(o, TContract), KS_InOut, Op(0)));
        return a;
    }

    /// <summary>方形态：B→C 交叉淡化时浮现。</summary>
    public static Animation SquareForm(AnimationOptions o)
    {
        var a = New(o);
        a.Children.Add(KF(MapCue(o, 0d), null, Op(0)));
        a.Children.Add(KF(MapCue(o, THoldB), KS_In, Op(0)));
        a.Children.Add(KF(MapCue(o, TContract), KS_InOut, Op(1)));
        return a;
    }

    /// <summary>
    /// 标题态：居中于胶囊。等电标滑到位（TMove）之后才淡入 0.20→0.25，
    /// 保证"电标先滑到左边 → 再出超充模式"的先后顺序。
    /// </summary>
    public static Animation TitleHost(AnimationOptions o)
    {
        var a = New(o);
        a.Children.Add(KF(MapCue(o, 0d), null, Op(0)));
        a.Children.Add(KF(MapCue(o, TMove), KS_In, Op(0)));
        a.Children.Add(KF(MapCue(o, TTitle), KS_Out, Op(1)));
        a.Children.Add(KF(MapCue(o, THoldB), KS_In, Op(1)));
        a.Children.Add(KF(MapCue(o, TContract), KS_InOut, Op(0)));
        return a;
    }

    /// <summary>数字态：等 C 态完全稳定（胶囊收窄、电标到位）之后才淡入。</summary>
    public static Animation NumHost(AnimationOptions o)
    {
        var a = New(o);
        a.Children.Add(KF(MapCue(o, 0d), null, Op(0)));
        a.Children.Add(KF(MapCue(o, TContract), KS_In, Op(0)));  // C 态已就绪，隐藏
        a.Children.Add(KF(MapCue(o, TNumIn), KS_In, Op(0)));     // 保持隐藏
        a.Children.Add(KF(MapCue(o, TNumReady), KS_Out, Op(1))); // 淡入完成
        a.Children.Add(KF(MapCue(o, THoldC), KS_In, Op(1)));
        return a;
    }

    /// <summary>
    /// 波纹 Host 抬升：扩散起点在电标正下方 16px，随扩散过程（TExpand+0.02 → THoldB）
    /// 抬升到 0（与电标圆心对齐），扩散完成态环与电标圆心同心。
    /// </summary>
    public static Animation RippleRise(AnimationOptions o)
    {
        var a = New(o);
        a.Children.Add(KF(MapCue(o, 0d), null, TY(16)));
        a.Children.Add(KF(MapCue(o, TExpand), KS_In, TY(16)));
        a.Children.Add(KF(MapCue(o, TExpand + 0.02), KS_Out, TY(16)));
        a.Children.Add(KF(MapCue(o, THoldB), KS_Out, TY(0)));
        a.Children.Add(KF(MapCue(o, TContract), KS_InOut, TY(0)));
        return a;
    }

    /// <summary>
    /// 单圈波纹：起点 TExpand（圆胶囊变矩形时），从 0 尺寸开始扩散（scale 0 → endScale），
    /// 淡入到峰值透明度，扩散到目标 scale 停住，随工业模式淡出。
    /// endScale / peakOp 分别乘上 RippleSpread / RippleIntensity 倍率。
    /// </summary>
    public static Animation Ripple(AnimationOptions o, double endScale, double peakOp)
    {
        double spread = Math.Clamp(o.RippleSpread, 0.5d, 1.5d);
        double intensity = Math.Clamp(o.RippleIntensity, 0d, 2d);
        double target = endScale * spread;
        double peak = Math.Min(1d, peakOp * intensity);

        var a = New(o);
        a.Children.Add(KF(MapCue(o, 0d), null, Op(0), SX(0), SY(0)));
        a.Children.Add(KF(MapCue(o, TExpand), KS_In, Op(0), SX(0), SY(0)));
        a.Children.Add(KF(MapCue(o, TExpand + 0.02), KS_Out, Op(peak), SX(0.05), SY(0.05)));
        a.Children.Add(KF(MapCue(o, THoldB), KS_Out, Op(peak), SX(target), SY(target)));
        a.Children.Add(KF(MapCue(o, TContract), KS_InOut, Op(0), SX(target), SY(target)));
        return a;
    }

    // ---------------- 简化版（拔电显示电量） ----------------
    // 只弹"电量圆胶囊"：无电标先出、无工业模式矩形、无波纹。直接全圆胶囊 + 电量内容。

    private const double SimpleBaselineSeconds = 5.0;
    private const double SimpleIntroEndCue = 0.08;  // 内容淡入完成

    private const double TSimpleAppear = 0.05;  // 弹出完成（scale 0.6→1, op 0→1）
    private const double TSimpleHold = 0.75;    // 停留结束
    private const double TSimpleClose = 0.80;   // 收回完成（scale 1→0）

    private static double MapCueSimple(AnimationOptions o, double cue)
    {
        double d = Math.Clamp(o.DurationSeconds, 3d, 10d);
        double introFrac = SimpleIntroEndCue * SimpleBaselineSeconds / d;
        if (cue <= SimpleIntroEndCue)
            return cue / SimpleIntroEndCue * introFrac;
        return introFrac + (cue - SimpleIntroEndCue) / (1 - SimpleIntroEndCue) * (1 - introFrac);
    }

    /// <summary>胶囊弹出：scale 0.6→1 + op 0→1（KS_Out 避免过冲闪屏），停留后保持。</summary>
    public static Animation SimplePillAppear(AnimationOptions o)
    {
        var a = NewSimple(o);
        a.Children.Add(KF(MapCueSimple(o, 0d), null, Op(0), SX(0.6), SY(0.6)));
        a.Children.Add(KF(MapCueSimple(o, TSimpleAppear), KS_Out, Op(1), SX(1d), SY(1d)));
        a.Children.Add(KF(MapCueSimple(o, TSimpleHold), KS_In, Op(1), SX(1d), SY(1d)));
        return a;
    }

    /// <summary>
    /// 内容淡入（电标方块 + 数字 + 徽章）：等胶囊完全弹出（TSimpleAppear）后，
    /// 快速显现。
    /// </summary>
    public static Animation SimpleFadeIn(AnimationOptions o)
    {
        var a = NewSimple(o);
        a.Children.Add(KF(MapCueSimple(o, 0d), null, Op(0)));
        a.Children.Add(KF(MapCueSimple(o, TSimpleAppear), KS_In, Op(0)));
        a.Children.Add(KF(MapCueSimple(o, TSimpleAppear + 0.03), KS_Out, Op(1)));
        a.Children.Add(KF(MapCueSimple(o, TSimpleHold), KS_In, Op(1)));
        return a;
    }

    /// <summary>收尾整体缩小（scale 1→0），ease-in 慢起快收。</summary>
    public static Animation SimpleScaleOut(AnimationOptions o)
    {
        var a = NewSimple(o);
        a.Children.Add(KF(MapCueSimple(o, 0d), null, SX(1d), SY(1d)));
        a.Children.Add(KF(MapCueSimple(o, TSimpleHold), KS_In, SX(1d), SY(1d)));
        a.Children.Add(KF(MapCueSimple(o, TSimpleClose), KS_In, SX(0d), SY(0d)));
        return a;
    }

    // ---------------- 构造辅助 ----------------

    private static Animation New(AnimationOptions o) => new()
    {
        Duration = TimeSpan.FromSeconds(Math.Clamp(o.DurationSeconds, 3d, 10d)),
        FillMode = FillMode.Forward,
    };

    private static Animation NewSimple(AnimationOptions o) => new()
    {
        Duration = TimeSpan.FromSeconds(Math.Clamp(o.DurationSeconds, 3d, 10d)),
        FillMode = FillMode.Forward,
    };

    private static KeyFrame KF(double cue, KeySpline? ks, params Setter[] setters)
    {
        var kf = new KeyFrame { Cue = new Cue(cue) };
        if (ks is not null)
            kf.KeySpline = ks;
        foreach (var s in setters)
            kf.Setters.Add(s);
        return kf;
    }

    private static Setter Op(double v) => Set(Visual.OpacityProperty, v);
    private static Setter TX(double v) => Set(TranslateTransform.XProperty, v);
    private static Setter TY(double v) => Set(TranslateTransform.YProperty, v);
    private static Setter SX(double v) => Set(ScaleTransform.ScaleXProperty, v);
    private static Setter SY(double v) => Set(ScaleTransform.ScaleYProperty, v);
    private static Setter CR(double v) => Set(Border.CornerRadiusProperty, new CornerRadius(v));
    private static Setter H(double v) => Set(Border.HeightProperty, v);

    private static Setter Set(AvaloniaProperty property, object value) => new(property, value);
}
