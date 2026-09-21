using System;

namespace EndfieldCharge.Services;

/// <summary>一次电池采样结果。跨平台共享模型，各平台读取器负责填充。</summary>
public sealed record BatterySnapshot(
    double RemainingWh,
    double FullWh,
    int Percent,
    bool AcOnline,
    bool Charging)
{
    /// <summary>充/放电功率（瓦）。正=充电，负=放电；未知为 null。</summary>
    public double? RateWatts { get; init; }

    /// <summary>设计容量（Wh），用于计算健康度。</summary>
    public double? DesignCapacityWh { get; init; }

    /// <summary>电池健康度百分比（当前满充容量 / 设计容量）。</summary>
    public double? HealthPercent => DesignCapacityWh.HasValue && DesignCapacityWh.Value > 0
        ? Math.Round(FullWh / DesignCapacityWh.Value * 100, 1)
        : null;

    /// <summary>剩余时间估计；未知为 null。</summary>
    public TimeSpan? EstimatedRemaining { get; init; }

    public bool HasBattery => FullWh > 0;
}
