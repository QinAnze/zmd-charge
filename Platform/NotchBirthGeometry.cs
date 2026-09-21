using System;

namespace EndfieldCharge.Services;

/// <summary>
/// 刘海动画几何：所有位置均为窗口内 DIP。BirthHost 在 GlobalScale 外部，
/// 位移不除以 UI 缩放；Pill 顶边中心为缩放原点，缩放不移动顶边中心。
/// </summary>
public readonly record struct NotchBirthGeometry
{
    public bool Enabled { get; init; }
    public double UiScale { get; init; }
    public double BirthWidth { get; init; }
    public double BirthHeight { get; init; }
    public double BirthCenterX { get; init; }
    public double BirthTop { get; init; }
    public double PillCenterFinal { get; init; }
    public double PillTopFinal { get; init; }
    public double SafeAreaBottom { get; init; }
    public bool IsActive => Enabled && UiScale > 0;
    public double BirthScaleX => IsActive ? BirthWidth / 560d : 1d;
    public double BirthScaleY => IsActive ? BirthHeight / 60d : 1d;
    public double OffX => IsActive ? BirthCenterX - PillCenterFinal : 0d;
    public double OffY => IsActive ? BirthTop - PillTopFinal : 0d;
    public static NotchBirthGeometry Disabled => new() { UiScale = 1d };

    public static double FinalCenter(double screenWidth, double uiScale, int side)
    {
        double halfWidth = Math.Min(560d * uiScale / 2d, screenWidth / 2d);
        double inset = Math.Min(20d + halfWidth, screenWidth / 2d);
        return side < 0 ? inset : side > 0 ? screenWidth - inset : screenWidth / 2d;
    }

    /// <summary>出生条完整藏在刘海高度之内，下缘比刘海底部高 2 DIP。</summary>
    public static NotchBirthGeometry FromLayout(double screenWidth, double notchHeight,
        double uiScale, double finalCenter, double finalTop)
    {
        if (notchHeight <= 0 || uiScale <= 0 || screenWidth <= 0)
            return Disabled;
        double height = Math.Min(28d * uiScale, Math.Max(1d, notchHeight - 4d));
        return new()
        {
            Enabled = true, UiScale = uiScale,
            BirthWidth = Math.Min(180d, 120d / uiScale),
            BirthHeight = height / uiScale,
            BirthCenterX = screenWidth / 2d,
            BirthTop = Math.Max(0d, notchHeight - height - 2d),
            SafeAreaBottom = notchHeight,
            PillCenterFinal = finalCenter, PillTopFinal = finalTop,
        };
    }

    public override string ToString() => $"birth={IsActive} offset=({OffX:F1},{OffY:F1}) " +
        $"origin=({BirthCenterX:F1},{BirthTop:F1}) final=({PillCenterFinal:F1},{PillTopFinal:F1})";
}
