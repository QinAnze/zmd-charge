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
///   0.08-0.14  电标单独弹出
///   0.14-0.18  胶囊背景弹出（scale 0.6→1, op 0→1，KS_Out 避免过冲闪屏）
///   0.18-0.24  A→B：高度 60→90 + CornerRadius 30→18（圆胶囊变高矩形，加速）
///   0.24-0.34  状态 B 短停：电标左移到 pill 内 18%，RippleHost 跟随左移；
///                「/// INDUSTRIAL MODE + 工业模式」居中淡入；
///                三圈波纹从 0 开始扩散（中心在电标正下方 16px，内粗外细、圈距拉开）
///   0.34-0.40  B→C（加速）：标题与波纹一起淡化，高度 90→60，CornerRadius 18→30，
///                RippleHost 跟随电标到贴左位置，数字内容淡入
///   0.40-0.86  状态 C 停留（拉长，让电量信息是重点）
///   0.86-0.92  整体缩小
///
/// 横向长度 560 全程恒定；高度：A(60) → B(90) → C(60)。
/// 工业模式段压缩到 0.18-0.40（1.32s），重点留给电量显示。
/// </summary>
internal static class HudAnimations
{
    public static readonly TimeSpan Timeline = TimeSpan.FromSeconds(6.0);

    private const double TStart = 0.08;
    private const double TAppear = 0.14;
    private const double TPillOut = 0.18;
    private const double TExpand = 0.24;
    private const double THoldB = 0.34;
    private const double TContract = 0.40;
    private const double THoldC = 0.86;
    private const double TClose = 0.92;

    private const double PillRadiusA = 30d;
    private const double PillRadiusB = 18d;
    private const double PillHeightA = 60d;   // 状态 A 与 C（圆胶囊等高）
    private const double PillHeightB = 90d;   // 状态 B（工业模式高矩形）
    private const double IconOffsetB = -179d; // 状态 B：pill 560 宽，内 18% 处 → 600-0.32×560=420.8
    private const double IconOffsetC = -259d; // 状态 C：pill 左内 12 + 半方块 9 = 341 → TX=341-600

    private static readonly KeySpline KS_In = new(0.42, 0, 1, 1);
    private static readonly KeySpline KS_Out = new(0, 0, 0.58, 1);
    private static readonly KeySpline KS_InOut = new(0.42, 0, 0.58, 1);
    private static readonly KeySpline KS_BackOut = new(0.175, 0.885, 0.32, 1.275);

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

    /// <summary>电标：先弹出（0.08→0.14）→ 胶囊完全弹出后微移（0.20→0.28 左移到 B 位置）→ B→C 时再微移到 C 位置。</summary>
    public static Animation BoltIcon()
    {
        var a = New();
        a.Children.Add(KF(0.00, null, Op(0), SX(0.4), SY(0.4), TX(0)));
        a.Children.Add(KF(TStart, KS_In, Op(0), SX(0.4), SY(0.4), TX(0)));
        a.Children.Add(KF(0.115, KS_BackOut, Op(1), SX(1.12), SY(1.12), TX(0)));
        a.Children.Add(KF(TAppear, KS_Out, Op(1), SX(1), SY(1), TX(0)));
        a.Children.Add(KF(TExpand, KS_BackOut, Op(1), SX(1), SY(1), TX(IconOffsetB)));
        a.Children.Add(KF(THoldB, KS_InOut, Op(1), SX(1), SY(1), TX(IconOffsetB)));
        a.Children.Add(KF(TContract, KS_InOut, Op(1), SX(1), SY(1), TX(IconOffsetC)));
        return a;
    }

    /// <summary>
    /// RippleHost 跟随电标位移：状态 A 居中 → 状态 B 左移（与 BoltIcon 同步）。
    /// 不用 KS_BackOut：位移过冲会让波纹瞬间跳出胶囊外。
    /// </summary>
    public static Animation RippleHost()
    {
        var a = New();
        a.Children.Add(KF(0d, null, TX(0)));
        a.Children.Add(KF(TExpand, KS_InOut, TX(IconOffsetB)));
        a.Children.Add(KF(THoldB, KS_InOut, TX(IconOffsetB)));
        a.Children.Add(KF(TContract, KS_InOut, TX(IconOffsetC)));
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

    /// <summary>标题态：居中于胶囊，0.28→0.34 淡入，0.44→0.50 随工业模式一起淡出。</summary>
    public static Animation TitleHost()
    {
        var a = New();
        a.Children.Add(KF(0d, null, Op(0)));
        a.Children.Add(KF(TExpand, KS_In, Op(0)));
        a.Children.Add(KF(TExpand + 0.06, KS_Out, Op(1)));
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
