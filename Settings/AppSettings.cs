namespace EndfieldCharge.Settings;

public sealed record AppSettings
{
    public double GlobalScale { get; init; } = 0.8;

    /// <summary>HUD 总时长（秒）。前段入场动画保持固定节奏，停留段随该值伸缩。</summary>
    public double DisplayDurationSeconds { get; init; } = 6.0;

    /// <summary>回弹强度 0~0.5。映射到 KS_BackOut 第二控制点 Y = 1 + 值，越大过冲越明显。</summary>
    public double BounceStrength { get; init; } = 0.275;

    /// <summary>波纹强度倍率 0~2。乘到各圈波纹峰值透明度上。</summary>
    public double RippleIntensity { get; init; } = 1.0;

    /// <summary>波纹幅度倍率 0.5~1.5。乘到各圈波纹最终扩散 scale 上。</summary>
    public double RippleSpread { get; init; } = 1.0;

    public HudPosition HudPosition { get; init; } = HudPosition.TopCenter;
    public int MonitorIndex { get; init; } = 0;
    public string Language { get; init; } = "auto";
    public int LowBatteryThreshold { get; init; } = 20;
    public bool EnableLowBatteryAlert { get; init; } = true;
    public bool EnableFullChargeAlert { get; init; } = true;
    public bool EnablePowerSaverNotify { get; init; } = true;
    public bool EnableAutoStart { get; init; } = false;
}

public enum HudPosition
{
    TopCenter,
    TopRight,
    TopLeft,
}
