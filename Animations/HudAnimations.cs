using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace EndfieldCharge.Animations;

/// <summary>
/// "灵动岛"三态时间线（高度 小→大→小，横向 560 恒定）：
///
///   0.04-0.10  电标单独弹出（缩放回弹）
///   0.07-0.09  胶囊背景弹出（scale 0.6→1, op 0→1，KS_Out 避免过冲闪屏）
///   0.09-0.12  A→B：高度 60→90 + CornerRadius 30→18（圆胶囊变高矩形）
///   0.12-0.20  电标从中间平滑滑到左侧 B 位（0.48s，easeInOutCubic，无过冲）；
///               RippleHost 用同一条曲线同步跟随，三圈波纹同时从 0 扩散
///   0.20-0.25  「/// SUPER CHARGE MODE + 超充模式」居中淡入（等电标停稳再出）
///   0.25-0.30  状态 B 短停
///   0.30-0.36  B→C：标题与波纹一起淡化，高度 90→60，CornerRadius 18→30，
///               电标从 B 位滑到贴左 C 位，数字内容淡入
///   0.36-0.86  状态 C 停留（拉长，让电量信息是重点）
///   0.86-0.89  整体缩小
///
/// 横向长度 560 全程恒定；高度：A(60) → B(90) → C(60)。
/// 注意：KeyFrame 的 Cue 必须严格递增（乱序会让某一段被压缩到 30ms，位移看起来像瞬移）。
/// </summary>
internal static class HudAnimations
{
    public static readonly TimeSpan Timeline = TimeSpan.FromSeconds(6.0);

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

    private const double PillRadiusA = 30d;
    private const double PillRadiusB = 18d;
    private const double PillHeightA = 60d;   // 状态 A 与 C（圆胶囊等高）
    private const double PillHeightB = 90d;   // 状态 B（工业模式高矩形）
    private const double IconOffsetB = -179d; // 状态 B：pill 560 宽，内 18% 处 → 600-0.32×560=420.8
    private const double IconOffsetC = -245d; // 状态 C：pill 左内 26 + 半方块 9 = 355 → TX=355-600

    private static readonly KeySpline KS_In = new(0.42, 0, 1, 1);
    private static readonly KeySpline KS_Out = new(0, 0, 0.58, 1);
    private static readonly KeySpline KS_InOut = new(0.42, 0, 0.58, 1);
    private static readonly KeySpline KS_BackOut = new(0.175, 0.885, 0.32, 1.275);
    /// <summary>easeInOutCubic：位移专用——两端慢中间快，且不过冲，滑动观感最顺。</summary>
    private static readonly KeySpline KS_Smooth = new(0.65, 0, 0.35, 1);

    // ===================================================================

    /// <summary>胶囊圆角：30(全圆) ↔ 12(矩形圆角)。</summary>
    public static Animation PillCorner()
    {
        var a = New();
        a.Children.Add(KF(0d, null, CR(PillRadiusA)));
        a.Children.Add(KF(TAppear, KS_In, CR(PillRadiusA)));
        a.Children.Add(KF(TExpand, KS_InOut, CR(PillRadiusB)));
        a.Children.Add(KF(THoldB, KS_In, CR(PillRadiusB)));
        a.Children.Add(KF(TContract, KS_InOut, CR(PillRadiusA)));
        return a;
    }

    /// <summary>
    /// 胶囊背景弹出：scale 0.6→1, op 0→1。
    /// 用 KS_Out（不再用 BackOut）—— BackOut 的过冲会缩放到 1.1+ 再回落，
    /// 与 PillCorner 的 CornerRadius 插值在同一帧同时作用时偶发渲染异常（背景闪透明），
    /// 改 KS_Out 后稳定。
    /// </summary>
    public static Animation PillAppear()
    {
        var a = New();
        a.Children.Add(KF(0d, null, Op(0), SX(0.6), SY(0.6)));
        a.Children.Add(KF(TStart, KS_In, Op(0), SX(0.6), SY(0.6)));
        a.Children.Add(KF(TAppear, KS_In, Op(0), SX(0.6), SY(0.6)));
        a.Children.Add(KF(TPillOut, KS_Out, Op(1), SX(1d), SY(1d)));
        a.Children.Add(KF(THoldC, KS_In, Op(1), SX(1d), SY(1d)));
        return a;
    }

    /// <summary>
    /// 胶囊高度：60(电标圆胶囊) → 90(工业模式高矩形，带回弹) → 60(电量圆胶囊)。
    /// 横向长度 560 恒定；独立于 CornerRadius 轨道，两者同段配合形成"胶囊撑高/收回"。
    /// 同时给 RippleHost 用同一动画，让波纹裁剪范围随 pill 高度同步变化。
    /// </summary>
    public static Animation PillHeight()
    {
        var a = New();
        a.Children.Add(KF(0d, null, H(PillHeightA)));
        a.Children.Add(KF(TAppear, KS_In, H(PillHeightA)));
        a.Children.Add(KF(TExpand, KS_BackOut, H(PillHeightB)));
        a.Children.Add(KF(THoldB, KS_In, H(PillHeightB)));
        a.Children.Add(KF(TContract, KS_InOut, H(PillHeightA)));
        a.Children.Add(KF(THoldC, KS_In, H(PillHeightA)));
        return a;
    }

    /// <summary>收尾整体缩小（scale 1→0），ease-in 慢起快收。</summary>
    public static Animation ScaleOut()
    {
        var a = New();
        a.Children.Add(KF(0d, null, SX(1d), SY(1d)));
        a.Children.Add(KF(THoldC, KS_In, SX(1d), SY(1d)));
        a.Children.Add(KF(TClose, KS_In, SX(0d), SY(0d)));
        return a;
    }

    /// <summary>
    /// 电标：弹出（缩放回弹）→ 停留中央 → 平滑滑到左侧 B 位 → B→C 再滑到贴左 C 位。
    /// 位移一律用 KS_Smooth（easeInOutCubic）+ 0.48s / 0.36s 的独立时间片，
    /// 不再用 KS_BackOut（它 80% 的行程在前 20% 时间内跑完，179px 看着就是瞬移）。
    /// Cue 严格递增：0 → 0.04 → 0.10 → 0.12 → 0.20 → 0.30 → 0.36 → 0.86。
    /// </summary>
    public static Animation BoltIcon()
    {
        var a = New();
        a.Children.Add(KF(0.00, null, Op(0), SX(0.4), SY(0.4), TX(0)));
        a.Children.Add(KF(TStart, KS_In, Op(0), SX(0.4), SY(0.4), TX(0)));
        a.Children.Add(KF(TBoltPop, KS_BackOut, Op(1), SX(1.12), SY(1.12), TX(0)));
        a.Children.Add(KF(TExpand, KS_Out, Op(1), SX(1), SY(1), TX(0)));
        a.Children.Add(KF(TMove, KS_Smooth, Op(1), SX(1), SY(1), TX(IconOffsetB)));
        a.Children.Add(KF(THoldB, KS_In, Op(1), SX(1), SY(1), TX(IconOffsetB)));
        a.Children.Add(KF(TContract, KS_Smooth, Op(1), SX(1), SY(1), TX(IconOffsetC)));
        a.Children.Add(KF(THoldC, KS_In, Op(1), SX(1), SY(1), TX(IconOffsetC)));
        return a;
    }

    /// <summary>
    /// RippleHost 跟随电标位移：状态 A 居中 → 状态 B 左移（与 BoltIcon 同步）。
    /// 必须与 BoltIcon 使用同一条曲线、同一段时间片，否则波纹会和电标脱开。
    /// </summary>
    public static Animation RippleHost()
    {
        var a = New();
        a.Children.Add(KF(0d, null, TX(0)));
        a.Children.Add(KF(TExpand, KS_In, TX(0)));
        a.Children.Add(KF(TMove, KS_Smooth, TX(IconOffsetB)));
        a.Children.Add(KF(THoldB, KS_In, TX(IconOffsetB)));
        a.Children.Add(KF(TContract, KS_Smooth, TX(IconOffsetC)));
        return a;
    }

    /// <summary>圆形形态：随电标淡入，B→C 交叉淡化时让位给方形态。</summary>
    public static Animation CircleForm()
    {
        var a = New();
        a.Children.Add(KF(0d, null, Op(0)));
        a.Children.Add(KF(TStart, KS_In, Op(0)));
        a.Children.Add(KF(TAppear, KS_Out, Op(1)));
        a.Children.Add(KF(THoldB, KS_In, Op(1)));
        a.Children.Add(KF(TContract, KS_InOut, Op(0)));
        return a;
    }

    /// <summary>方形态：B→C 交叉淡化时浮现。</summary>
    public static Animation SquareForm()
    {
        var a = New();
        a.Children.Add(KF(0d, null, Op(0)));
        a.Children.Add(KF(THoldB, KS_In, Op(0)));
        a.Children.Add(KF(TContract, KS_InOut, Op(1)));
        return a;
    }

    /// <summary>
    /// 标题态：居中于胶囊。等电标滑到位（TMove）之后才淡入 0.20→0.25，
    /// 保证"电标先滑到左边 → 再出超充模式"的先后顺序。
    /// </summary>
    public static Animation TitleHost()
    {
        var a = New();
        a.Children.Add(KF(0d, null, Op(0)));
        a.Children.Add(KF(TMove, KS_In, Op(0)));
        a.Children.Add(KF(TTitle, KS_Out, Op(1)));
        a.Children.Add(KF(THoldB, KS_In, Op(1)));
        a.Children.Add(KF(TContract, KS_InOut, Op(0)));
        return a;
    }

    /// <summary>数字态：B→C 交叉淡化时浮现。</summary>
    public static Animation NumHost()
    {
        var a = New();
        a.Children.Add(KF(0d, null, Op(0)));
        a.Children.Add(KF(THoldB, KS_In, Op(0)));
        a.Children.Add(KF(TContract, KS_InOut, Op(1)));
        return a;
    }

    /// <summary>
    /// 波纹 Host 抬升：扩散起点在电标正下方 16px，随扩散过程（TExpand+0.02 → THoldB）
    /// 抬升到 0（与电标圆心对齐），扩散完成态环与电标圆同心。
    /// </summary>
    public static Animation RippleRise()
    {
        var a = New();
        a.Children.Add(KF(0d, null, TY(16)));
        a.Children.Add(KF(TExpand, KS_In, TY(16)));
        a.Children.Add(KF(TExpand + 0.02, KS_Out, TY(16)));
        a.Children.Add(KF(THoldB, KS_Out, TY(0)));
        a.Children.Add(KF(TContract, KS_InOut, TY(0)));
        return a;
    }

    /// <summary>
    /// 单圈波纹：起点 TExpand（圆胶囊变矩形时），从 0 尺寸开始扩散（scale 0 → endScale），
    /// 0.24→0.26 淡入到峰值透明度，0.26→0.34 扩散到目标 scale 停住，0.34→0.40 随工业模式淡出。
    /// startScale 传 0 表示从电标正下方的小点向外扩散。
    /// </summary>
    public static Animation Ripple(double startScale, double endScale, double peakOp)
    {
        var a = New();
        a.Children.Add(KF(0d, null, Op(0), SX(startScale), SY(startScale)));
        a.Children.Add(KF(TExpand, KS_In, Op(0), SX(startScale), SY(startScale)));
        a.Children.Add(KF(TExpand + 0.02, KS_Out, Op(peakOp), SX(0.05), SY(0.05)));
        a.Children.Add(KF(THoldB, KS_Out, Op(peakOp), SX(endScale), SY(endScale)));
        a.Children.Add(KF(TContract, KS_InOut, Op(0), SX(endScale), SY(endScale)));
        return a;
    }

    // ---------------- 简化版（拔电显示电量） ----------------
    // 只弹"电量圆胶囊"：无电标先出、无工业模式矩形、无波纹。直接全圆胶囊 + 电量内容。

    public static readonly TimeSpan SimpleTimeline = TimeSpan.FromSeconds(5.0);

    private const double TSimpleAppear = 0.05;  // 弹出完成（scale 0.6→1, op 0→1，加速一倍）
    private const double TSimpleHold = 0.75;    // 停留结束
    private const double TSimpleClose = 0.80;   // 收回完成（scale 1→0，加速一倍）

    /// <summary>胶囊弹出：scale 0.6→1 + op 0→1（KS_Out 避免过冲闪屏），停留后保持。</summary>
    public static Animation SimplePillAppear()
    {
        var a = NewSimple();
        a.Children.Add(KF(0d, null, Op(0), SX(0.6), SY(0.6)));
        a.Children.Add(KF(TSimpleAppear, KS_Out, Op(1), SX(1d), SY(1d)));
        a.Children.Add(KF(TSimpleHold, KS_In, Op(1), SX(1d), SY(1d)));
        return a;
    }

    /// <summary>
    /// 内容淡入（电标方块 + 数字 + 徽章）：等胶囊完全弹出（TSimpleAppear）后，
    /// 在 0.05→0.08 之间快速显现（0.03 单位 = 0.15s）。
    /// </summary>
    public static Animation SimpleFadeIn()
    {
        var a = NewSimple();
        a.Children.Add(KF(0d, null, Op(0)));
        a.Children.Add(KF(TSimpleAppear, KS_In, Op(0)));
        a.Children.Add(KF(TSimpleAppear + 0.03, KS_Out, Op(1)));
        a.Children.Add(KF(TSimpleHold, KS_In, Op(1)));
        return a;
    }

    /// <summary>收尾整体缩小（scale 1→0），ease-in 慢起快收。</summary>
    public static Animation SimpleScaleOut()
    {
        var a = NewSimple();
        a.Children.Add(KF(0d, null, SX(1d), SY(1d)));
        a.Children.Add(KF(TSimpleHold, KS_In, SX(1d), SY(1d)));
        a.Children.Add(KF(TSimpleClose, KS_In, SX(0d), SY(0d)));
        return a;
    }

    private static Animation NewSimple() => new()
    {
        Duration = SimpleTimeline,
        FillMode = FillMode.Forward,
    };

    // ---------------- 构造辅助 ----------------

    private static Animation New() => new()
    {
        Duration = Timeline,
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
