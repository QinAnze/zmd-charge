using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace PptConsole.Animations;

/// <summary>
/// 控制台动画库：曲线（KS_In/Out/InOut/Smooth/BackOut）与构造辅助（KF/Op/SX/SY/CR/H）
/// 完整继承 EndfieldCharge HudAnimations 的设计手感。
///
/// 区别于原库的"一次性单向时间线"：这里每条动画都是固定时长的单段可逆动画，
/// 展开/收起成对出现（同曲线、镜像方向），打断与复位由调用方（ConsoleWindow）负责——
/// 与原 HudWindow 的 _cts.Cancel() + ResetToInitial() 模式一致。
///
/// 时长档位（触控交互基线）：
///   吊起 320ms（BackOut 回弹）/ 收回 160ms（与原 DismissAsync 同档）
///   面板撑高 320ms（BackOut 生长，对应原 PillHeight 60→90 的段）/ 收回 260ms
///   面板行错峰淡入 180ms（延迟 70ms/行）/ 淡出 100ms（延迟 40ms/行）
///   触控波纹 500ms（原 Ripple 三圈的时间结构压缩为单圈）
/// </summary>
internal static class ConsoleAnimations
{
    // ---------------- 曲线库（与 HudAnimations 完全一致） ----------------

    private static readonly KeySpline KS_In = new(0.42, 0, 1, 1);
    private static readonly KeySpline KS_Out = new(0, 0, 0.58, 1);
    private static readonly KeySpline KS_InOut = new(0.42, 0, 0.58, 1);
    /// <summary>easeInOutCubic：位移专用——两端慢中间快，且不过冲。</summary>
    private static readonly KeySpline KS_Smooth = new(0.65, 0, 0.35, 1);

    /// <summary>
    /// 回弹曲线：过冲量由 bounce 控制（0.275 为原 AnimationOptions.BounceStrength 基线，
    /// 即原 PillAppear / PillHeight 撑高段的手感）。
    /// </summary>
    private static KeySpline BackOut(double bounce = 0.275) => new(0.175, 0.885, 0.32, 1d + bounce);

    // ---------------- 胶囊吊起 / 收回 ----------------

    /// <summary>胶囊吊起：scale 0.6→1 + op 0→1（与原 PillAppear 同参数，BackOut 回弹）。</summary>
    public static Animation PillAppear(double bounce = 0.275)
    {
        var a = New(320);
        a.Children.Add(KF(0d, null, Op(0), SX(0.6), SY(0.6)));
        a.Children.Add(KF(1d, BackOut(bounce), Op(1), SX(1d), SY(1d)));
        return a;
    }

    /// <summary>胶囊收回：scale 1→0.6 + op 1→0（160ms，ease-in 慢起快收）。</summary>
    public static Animation PillHide()
    {
        var a = New(160);
        a.Children.Add(KF(0d, null, Op(1), SX(1d), SY(1d)));
        a.Children.Add(KF(1d, KS_In, Op(0), SX(0.6), SY(0.6)));
        return a;
    }

    // ---------------- 工具面板撑高 / 收回（对应原 PillHeight + PillCorner 段） ----------------

    /// <summary>
    /// 面板撑高：高度 0→height（BackOut 生长，对应原 60→90 撑高段），
    /// 不透明度在前 30% 先行到位，让内容尽早可见。
    /// </summary>
    public static Animation PanelExpand(double height, double bounce = 0.275)
    {
        var a = New(320);
        a.Children.Add(KF(0d, null, H(0d), Op(0)));
        a.Children.Add(KF(0.3d, KS_Out, Op(1)));
        a.Children.Add(KF(1d, BackOut(bounce), H(height)));
        return a;
    }

    /// <summary>面板收回：高度→0（easeInOut），不透明度前 40% 先退场。</summary>
    public static Animation PanelCollapse(double height)
    {
        var a = New(260);
        a.Children.Add(KF(0d, null, H(height), Op(1)));
        a.Children.Add(KF(0.4d, KS_In, Op(0)));
        a.Children.Add(KF(1d, KS_InOut, H(0d)));
        return a;
    }

    /// <summary>
    /// 工具胶囊圆角过渡：胶囊(28 全圆) ↔ 面板底座(18 圆角矩形)。
    /// 与原 PillCorner 30↔18 的"胶囊变高矩形"同一变形语言。
    /// </summary>
    public static Animation PillCorner(double from, double to, bool expand)
    {
        var a = New(expand ? 320 : 260);
        a.Children.Add(KF(0d, null, CR(from)));
        a.Children.Add(KF(1d, expand ? BackOut() : KS_InOut, CR(to)));
        return a;
    }

    // ---------------- 面板内容错峰淡入 / 淡出（对应原 TitleHost/NumHost 的先后节奏） ----------------

    /// <summary>
    /// 面板行淡入：每行自带 (120 + index*70)ms 的入场延迟（延迟段内保持 0），
    /// 呈现"一行接一行"的错峰节奏，与原 TitleHost 等 TMove 后才淡入的先后原则一致。
    /// </summary>
    public static Animation PanelRowIn(int index)
    {
        int delay = 120 + index * 70;
        var a = New(delay + 180);
        double delayFrac = (double)delay / (delay + 180);

        a.Children.Add(KF(0d, null, Op(0)));
        a.Children.Add(KF(delayFrac, KS_In, Op(0)));
        a.Children.Add(KF(1d, KS_Out, Op(1)));
        return a;
    }

    /// <summary>面板行淡出：靠后的行先退（镜像错峰）。</summary>
    public static Animation PanelRowOut(int index, int rowCount)
    {
        int delay = (rowCount - 1 - index) * 40;
        var a = New(delay + 100);
        double delayFrac = (double)delay / (delay + 100);

        a.Children.Add(KF(0d, null, Op(1)));
        a.Children.Add(KF(delayFrac, KS_In, Op(1)));
        a.Children.Add(KF(1d, KS_In, Op(0)));
        return a;
    }

    // ---------------- 触控波纹（原三圈波纹的单圈触控版） ----------------

    /// <summary>
    /// 触控波纹：从点按位置扩散的实心圆（原 RippleInner 的"填充圆"形态），
    /// 快速起波（12% 达到峰值透明度）→ 边扩散边淡出。
    /// </summary>
    public static Animation TapRipple(double endScale, double peakOp)
    {
        var a = New(500);
        a.Children.Add(KF(0d, null, Op(0), SX(0d), SY(0d)));
        a.Children.Add(KF(0.12d, KS_Out, Op(peakOp), SX(0.1), SY(0.1)));
        a.Children.Add(KF(1d, KS_Out, Op(0), SX(endScale), SY(endScale)));
        return a;
    }

    // ---------------- 构造辅助（与 HudAnimations 同源） ----------------

    private static Animation New(double ms) => new()
    {
        Duration = TimeSpan.FromMilliseconds(ms),
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
    private static Setter SX(double v) => Set(ScaleTransform.ScaleXProperty, v);
    private static Setter SY(double v) => Set(ScaleTransform.ScaleYProperty, v);
    private static Setter CR(double v) => Set(Border.CornerRadiusProperty, new CornerRadius(v));
    private static Setter H(double v) => Set(Border.HeightProperty, v);

    private static Setter Set(AvaloniaProperty property, object value) => new(property, value);
}
