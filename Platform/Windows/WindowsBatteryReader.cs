using System;
using System.Management;
using System.Runtime.Versioning;

namespace EndfieldCharge.Services;

/// <summary>
/// Windows 电池信息读取。主路径走 powrprof（快、准、同步），失败时退回 WMI Win32_Battery。
/// 共享模型 <see cref="BatterySnapshot"/> 定义于 Platform/BatterySnapshot.cs。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsBatteryReader
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
        if (!WindowsPowerNative.TryGetBatteryState(out var s))
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
            // powrprof 不提供设计容量，健康度仅 WMI 路径可读
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
                uint? designMwh = ReadUInt32(mo["DesignCapacity"]);

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
                    Charging: status is 2 or 6 or 7 or 8 or 9)
                {
                    DesignCapacityWh = designMwh.HasValue && designMwh > 0
                        ? designMwh.Value / 1000.0
                        : null,
                };
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
