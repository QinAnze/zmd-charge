namespace EndfieldCharge.Settings;

public sealed record AppSettings
{
    public double GlobalScale { get; init; } = 0.8;
    public double DisplayDurationSeconds { get; init; } = 5.0;
    public HudPosition HudPosition { get; init; } = HudPosition.TopCenter;
    public int MonitorIndex { get; init; } = 0;
    public string Language { get; init; } = "auto";
    public int LowBatteryThreshold { get; init; } = 20;
    public bool EnableLowBatteryAlert { get; init; } = true;
    public bool EnableFullChargeAlert { get; init; } = true;
    public bool EnableAutoStart { get; init; } = false;
}

public enum HudPosition
{
    TopCenter,
    TopRight,
    TopLeft,
}