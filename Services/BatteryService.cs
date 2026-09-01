using System;
using System.Management;
using System.Runtime.Versioning;

namespace EndfieldCharge.Services;

/// <summary>一次电池采样结果。</summary>
public sealed record BatterySnapshot(
    double RemainingWh,
    double FullWh,
    int Percent,
    bool AcOnline,
    bool Charging)
{
    /// <summary>充/放电功率（瓦）。正=充电，负=放电；未知为 null。</summary>
    public double? RateWatts { get; init; }

    /// <summary>剩余时间估计；未知为 null。</summary>
    public TimeSpan? EstimatedRemaining { get; init; }

    public bool HasBattery => FullWh > 0;
}

/// <summary>
/// 电池信息读取。主路径走 powrprof（快、准、同步），失败时退回 WMI Win32_Battery。
/// </summary>
[SupportedOSPlatform("windows")]
public static class BatteryService
{
    /// <summary>取当前电池快照；无电池或读取失败返回 null。</summary>
    public static BatterySnapshot? GetSnapshot()
    {
        if (TryFromPowerProf(out var snap))
            return snap;

        return TryFromWmi();
    }

    private static bool TryFromPowerProf(out BatterySnapshot? snapshot)
    {
        snapshot = null;
        if (!PowerNative.TryGetBatteryState(out var s))
            return false;

        // 有的固件 MaxCapacity 给的是"设计容量"而非"当前满充容量"，这里只做合理性校验
        if (s.MaxCapacity == 0)
            return false;

        double fullWh = s.MaxCapacity / 1000.0;
        double remainingWh = s.RemainingCapacity / 1000.0;

        // 百分比直接用容量比算，比 EstimatedChargeRemaining 更连续（后者常为整数跳变）
        int percent = (int)Math.Round(remainingWh / fullWh * 100.0);
        percent = Math.Clamp(percent, 0, 100);

        snapshot = new BatterySnapshot(
            RemainingWh: remainingWh,
            FullWh: fullWh,
            Percent: percent,
            AcOnline: s.AcOnLine != 0,
            Charging: s.Charging != 0)
        {
            RateWatts = s.Rate == 0 ? null : s.Rate / 1000.0,
            EstimatedRemaining = s.EstimatedTime is 0 or 0x80000000
                ? null
                : TimeSpan.FromSeconds(s.EstimatedTime),
        };
        return true;
    }

    private static BatterySnapshot? TryFromWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT EstimatedChargeRemaining, FullChargeCapacity, DesignCapacity, BatteryStatus FROM Win32_Battery");

            foreach (ManagementObject mo in searcher.Get())
            {
                int? pct = ReadUInt16(mo["EstimatedChargeRemaining"]);
                uint? fullMwh = ReadUInt32(mo["FullChargeCapacity"]) ?? ReadUInt32(mo["DesignCapacity"]);

                if (pct is null || fullMwh is 0 or null)
                    continue;

                double fullWh = fullMwh.Value / 1000.0;
                double remainingWh = fullWh * pct.Value / 100.0;

                // BatteryStatus: 2 = 正在充电, 1 = 放电, 其他见 WMI 文档
                ushort status = ReadUInt16(mo["BatteryStatus"]) ?? 0;

                return new BatterySnapshot(
                    RemainingWh: remainingWh,
                    FullWh: fullWh,
                    Percent: Math.Clamp(pct.Value, 0, 100),
                    AcOnline: status is 2 or 6 or 7 or 8 or 9,
                    Charging: status is 2 or 6 or 7 or 8 or 9);
            }
        }
        catch
        {
            // WMI 被禁用或服务未启动时静默失败
        }

        return null;

        static ushort? ReadUInt16(object? v) => v is null ? null : Convert.ToUInt16(v);
        static uint? ReadUInt32(object? v) => v is null ? null : Convert.ToUInt32(v);
    }
}
