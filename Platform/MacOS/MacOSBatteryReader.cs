using System;
using System.Runtime.Versioning;

namespace EndfieldCharge.Services;

/// <summary>
/// macOS 电池信息读取。
///
/// 主路径：IORegistry <c>AppleSmartBattery</c> —— 无子进程、无 TCC 权限、同步返回。
///   mAh + mV → mWh： <c>Wh = mAh × mV / 1_000_000</c>
///   这也让健康度（满充/设计）在 macOS 上可算，而 Windows 的 powrprof 主路径拿不到设计容量。
/// 兜底  ：<c>pmset -g batt</c>，仅能给出百分比与交流/电池状态（需要 fork 子进程）。
///
/// 无电池机型（Mac mini / iMac / Mac Studio）返回 null，与 Windows 侧 "无电池 → null" 语义一致。
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacOSBatteryReader
{
    /// <summary>mAh × mV → Wh 的换算系数。</summary>
    private const double MahMvToWh = 1_000_000d;

    /// <summary><c>TimeRemaining</c> 的"未知"哨兵值（分钟）。</summary>
    private const double TimeRemainingUnknown = 65535d;

    /// <summary><c>TimeRemaining</c> 的另一个"未知"哨兵值（0x80000000 分钟）。</summary>
    private const double TimeRemainingUnknownAlt = 2147483648d;

    /// <summary>取当前电池快照；无电池或读取失败返回 null。</summary>
    public static BatterySnapshot? GetSnapshot()
        => TryFromIOKit() ?? TryFromPmset();

    /// <summary>只取 AC 是否在线（不依赖电池存在）。返回 false 表示两项数据源都失败。</summary>
    public static bool TryGetAcOnline(out bool acOnline)
    {
        acOnline = false;

        // 主路径：AppleSmartBattery 的 ExternalConnected
        var raw = MacOSPowerNative.TryReadBattery();
        if (raw is { ExternalConnected: { } external })
        {
            acOnline = external;
            return true;
        }

        // 兜底：pmset 输出里的 'AC Power' / 'Battery Power'
        if (MacOSPowerNative.TryReadFromPmset() is { } fallback)
        {
            acOnline = fallback.AcOnline;
            return true;
        }

        return false;
    }

    // ---------------- IOKit 主路径 ----------------

    private static BatterySnapshot? TryFromIOKit()
    {
        var raw = MacOSPowerNative.TryReadBattery();
        if (raw is null)
            return null;

        var r = raw.Value;

        double voltageMv = r.VoltageMv ?? 0d;
        double fullMah = r.FullChargeCapacityMah ?? 0d;
        double remainMah = r.RemainingCapacityMah ?? 0d;

        double fullWh = fullMah * voltageMv / MahMvToWh;
        double remainingWh = remainMah * voltageMv / MahMvToWh;

        // pmset 兜底最多解释一次，避免同一轮读取里多次 fork 子进程
        (bool AcOnline, int? Percent)? pmsetOnce = null;
        (bool AcOnline, int? Percent)? ReadPmsetOnce()
        {
            pmsetOnce ??= MacOSPowerNative.TryReadFromPmset();
            return pmsetOnce;
        }

        int percent;
        if (fullMah > 0d && remainMah >= 0d)
        {
            // 容量比算百分比：比平台给出的整数 CurrentCapacity 更连续
            percent = (int)Math.Round(remainMah / fullMah * 100d);
        }
        else if (r.CurrentCapacityPercent is { } currentPct)
        {
            percent = (int)Math.Round(currentPct);
        }
        else if (ReadPmsetOnce() is { Percent: { } pmsetPct })
        {
            percent = pmsetPct;
        }
        else
        {
            percent = 0;
        }

        bool acOnline = r.ExternalConnected ?? ReadPmsetOnce()?.AcOnline ?? false;

        var snapshot = new BatterySnapshot(
            RemainingWh: remainingWh,
            FullWh: fullWh,
            Percent: Math.Clamp(percent, 0, 100),
            AcOnline: acOnline,
            Charging: r.IsCharging ?? false)
        {
            RateWatts = ComputeRateWatts(r, voltageMv),
            DesignCapacityWh = ComputeDesignWh(r, voltageMv),
            EstimatedRemaining = ComputeRemainingTime(r),
        };

        return snapshot;
    }

    /// <summary>设计容量（Wh）。设计容量缺失、非正，或电压未知时返回 null。</summary>
    private static double? ComputeDesignWh(MacOSPowerNative.BatteryRaw r, double voltageMv)
    {
        if (r.DesignCapacityMah is not { } designMah || designMah <= 0d || voltageMv <= 0d)
            return null;

        return designMah * voltageMv / MahMvToWh;
    }

    /// <summary>
    /// 充/放电功率。正 = 流入电池（充电）。
    ///
    /// 优先 <c>Amperage</c>（mA，符号已还原）× 电压 —— 实测这一路最准确；
    /// <c>BatteryData.BatteryPower</c> 在放电时可能给出接近 0 的滞后值，故只作兜底。
    /// 电流为 0（充满后）→ null，与 Windows 侧 <c>Rate == 0 → null</c> 语义一致。
    /// </summary>
    private static double? ComputeRateWatts(MacOSPowerNative.BatteryRaw r, double voltageMv)
    {
        double? amperageMa = FirstNonZero(r.AmperageMa, r.InstantAmperageMa);
        if (amperageMa is not null && voltageMv > 0d)
            return Math.Round(amperageMa.Value * voltageMv / MahMvToWh, 3);

        if (r.BatteryPowerMw is { } batteryPowerMw && batteryPowerMw != 0d && voltageMv > 0d)
            return Math.Round(batteryPowerMw / 1000d, 3);

        return null;
    }

    private static double? FirstNonZero(double? primary, double? secondary)
    {
        if (primary is { } p && p != 0d)
            return p;
        if (secondary is { } s && s != 0d)
            return s;
        return null;
    }

    /// <summary>
    /// 剩余时间估计。按可信度依次尝试 <c>TimeRemaining</c> 与 <c>AvgTimeToEmpty</c>。
    ///
    /// 实测踩坑：<c>TimeRemaining</c> 在插电转为放电后可能**滞留在哨兵值 65535**，
    /// 此时真实估计值在 <c>AvgTimeToEmpty</c>；已充满时两者都无意义，返回 null。
    /// </summary>
    private static TimeSpan? ComputeRemainingTime(MacOSPowerNative.BatteryRaw r)
    {
        if (r.FullyCharged == true && r.ExternalConnected != false)
            return null;

        if (TryUsableMinutes(r.TimeRemainingMinutes) is { } minutes)
            return TimeSpan.FromMinutes(minutes);

        if (TryUsableMinutes(r.AvgTimeToEmptyMinutes) is { } avgMinutes)
            return TimeSpan.FromMinutes(avgMinutes);

        return null;
    }

    /// <summary>过滤掉 <= 0 与两个哨兵值（65535 / 0x80000000）。</summary>
    private static double? TryUsableMinutes(double? raw)
    {
        if (raw is not { } minutes)
            return null;

        if (minutes <= 0d || minutes == TimeRemainingUnknown || minutes == TimeRemainingUnknownAlt)
            return null;

        return minutes;
    }

    // ---------------- pmset 兜底路径 ----------------

    /// <summary>
    /// pmset 完全无法提供 mWh 容量，因此这里用百分比 + 名义 100Wh 满充容量构造快照，
    /// 保证 HUD 至少能显示百分比与环形进度（mWh 数值为名义值，仅供占位）。
    /// 该路径只在 IOKit 完全不可用时走到。
    /// </summary>
    private static BatterySnapshot? TryFromPmset()
    {
        if (MacOSPowerNative.TryReadFromPmset() is not { } result)
            return null;

        if (result.Percent is not { } percent)
            return null;

        const double AssumedFullWh = 100d;

        return new BatterySnapshot(
            RemainingWh: AssumedFullWh * percent / 100d,
            FullWh: AssumedFullWh,
            Percent: percent,
            AcOnline: result.AcOnline,
            Charging: false);
    }
}
