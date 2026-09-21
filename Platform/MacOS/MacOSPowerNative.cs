using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndfieldCharge.Services;

/// <summary>
/// macOS 电源 / 电池相关的原生 API（仅 macOS 目标编译）。
///
/// 两层数据源：
///   1. IORegistry <c>AppleSmartBattery</c> 节点 —— 主路径。
///      <c>BatteryData.FullChargeCapacity / RemainingCapacity / DesignCapacity</c>（mAh）
///      + <c>Voltage</c>（mV）可精确算出 mWh，并支持健康度计算。
///   2. <c>pmset -g batt</c> —— 兜底，仅给百分比与交流/电池状态。
///
/// 一条监听路径：
///   IOKit <c>IOPMPowerSource</c> 的 general interest 通知，
///   挂在该线程的 CFRunLoop 上（见 <see cref="RunLoop"/>）。
///
/// 内存纪律：所有 Create/Copy 返回的 CoreFoundation 对象都必须配对 CFRelease。
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacOSPowerNative
{
    // ============================================================
    //  CoreFoundation
    // ============================================================

    private const string LibCF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(LibCF)]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(LibCF)]
    public static extern IntPtr CFDictionaryGetValue(IntPtr theDict, IntPtr key);

    [DllImport(LibCF)]
    private static extern bool CFNumberGetValue(IntPtr number, int theType, out long value);

    [DllImport(LibCF)]
    private static extern bool CFNumberGetValue(IntPtr number, int theType, out double value);

    [DllImport(LibCF)]
    private static extern bool CFBooleanGetValue(IntPtr boolean);

    [DllImport(LibCF)]
    public static extern IntPtr CFRunLoopGetCurrent();

    [DllImport(LibCF)]
    public static extern void CFRunLoopAddSource(IntPtr rl, IntPtr source, IntPtr mode);

    [DllImport(LibCF)]
    public static extern void CFRunLoopStop(IntPtr rl);

    [DllImport(LibCF)]
    public static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, bool returnAfterSourceHandled);

    /// <summary>kCFNumberSInt64Type</summary>
    private const int CFNumberSInt64Type = 4;

    /// <summary>kCFNumberDoubleType</summary>
    private const int CFNumberDoubleType = 13;

    /// <summary>
    /// <c>kCFRunLoopDefaultMode</c> 在 CoreFoundation 中是一个导出为
    /// <c>CFStringRef</c> 的全局常量；因此需要二次解引用才能拿到字符串对象本身。
    /// </summary>
    private static readonly IntPtr DefaultRunLoopMode =
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(
            NativeLibrary.GetExport(NativeLibrary.Load(LibCF), "kCFRunLoopDefaultMode")));

    // ---------------- CFString 键 ----------------

    private static IntPtr Key(string name) => CFStringFromUtf8(name);

    /// <summary>创建 CFStringRef（调用方负责 CFRelease）。</summary>
    public static IntPtr CFStringFromUtf8(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return CFStringCreateWithCString(IntPtr.Zero, handle.AddrOfPinnedObject(), 0x08000100 /* kCFStringEncodingUTF8 */);
        }
        finally
        {
            handle.Free();
        }
    }

    [DllImport(LibCF)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, IntPtr cStr, uint encoding);

    // ============================================================
    //  IOKit
    // ============================================================

    private const string LibIOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";

    /// <summary>kIOMainPortDefault（kIOMasterPortDefault 已废弃但同为 0）。</summary>
    private const uint MainPortDefault = 0;

    [DllImport(LibIOKit)]
    public static extern IntPtr IOServiceMatching(string name);

    [DllImport(LibIOKit)]
    public static extern uint IOServiceGetMatchingService(uint mainPort, IntPtr matching);

    [DllImport(LibIOKit)]
    public static extern int IORegistryEntryCreateCFProperties(
        uint entry, out IntPtr properties, IntPtr allocator, uint options);

    [DllImport(LibIOKit)]
    public static extern void IOObjectRelease(uint obj);


    // ============================================================
    //  电池属性读取
    // ============================================================

    /// <summary>已知的 IOKit 电源节点类名。AppleSmartBattery 为现代机型使用的主类。</summary>
    private static readonly string[] BatteryServiceClasses = { "AppleSmartBattery", "IOPMPowerSource" };

    /// <summary>mAh + mV → mWh 的换算系数。</summary>
    private const double MahMvToWh = 1_000_000d;

    /// <summary>pmset 可执行文件路径。</summary>
    private const string PmsetPath = "/usr/bin/pmset";

    /// <summary>
    /// 一次电池原始读数。字段缺失为 null。
    ///
    /// 无符号说明（实测踩坑）：IOKit 的 <c>Amperage</c> / <c>InstantAmperage</c> /
    /// <c>BatteryPower</c> 把负值（放电）以 **无符号 32 位** 存储，
    /// 例如 -2092 mA 读出为 <c>18446744073709550344</c>。
    /// 因此这些字段必须经 <see cref="NormalizeSigned32"/> 还原符号。
    /// </summary>
    public readonly record struct BatteryRaw(
        double? VoltageMv,
        double? MaxCapacityPercent,
        double? CurrentCapacityPercent,
        bool? ExternalConnected,
        bool? IsCharging,
        bool? FullyCharged,
        double? AmperageMa,
        double? InstantAmperageMa,
        double? TimeRemainingMinutes,
        double? AvgTimeToEmptyMinutes,
        double? FullChargeCapacityMah,
        double? RemainingCapacityMah,
        double? DesignCapacityMah,
        double? BatteryPowerMw);

    /// <summary>
    /// 把 IOKit 的"无符号 32 位"读法还原为有符号值。
    /// 仅处理落在 <c>[int.MinValue, uint.MaxValue]</c> 的值，避免误伤合法大数。
    /// </summary>
    public static double NormalizeSigned32(double value)
        => value is >= int.MinValue and <= uint.MaxValue
            ? unchecked((int)(uint)(long)value)
            : value;

    /// <summary>
    /// 读取 AppleSmartBattery 节点属性。找不到电池（台式机）或读取失败返回 null。
    /// </summary>
    public static BatteryRaw? TryReadBattery()
    {
        foreach (var cls in BatteryServiceClasses)
        {
            var service = IOServiceGetMatchingService(MainPortDefault, IOServiceMatching(cls));
            if (service == 0)
                continue;

            try
            {
                if (IORegistryEntryCreateCFProperties(service, out var props, IntPtr.Zero, 0) != 0 || props == IntPtr.Zero)
                    continue;

                try
                {
                    return ReadProperties(props);
                }
                finally
                {
                    CFRelease(props);
                }
            }
            finally
            {
                IOObjectRelease(service);
            }
        }

        return null;
    }

    private static BatteryRaw ReadProperties(IntPtr dict)
    {
        // BatteryData 是嵌套字典，容量类字段都在里面
        IntPtr batteryData = IntPtr.Zero;
        var batteryDataKey = Key("BatteryData");
        try
        {
            var nested = CFDictionaryGetValue(dict, batteryDataKey);
            if (nested != IntPtr.Zero)
                batteryData = nested;
        }
        finally
        {
            CFRelease(batteryDataKey);
        }

        return new BatteryRaw(
            VoltageMv: ReadNumber(dict, "Voltage"),
            MaxCapacityPercent: ReadNumber(dict, "MaxCapacity"),
            CurrentCapacityPercent: ReadNumber(dict, "CurrentCapacity"),
            ExternalConnected: ReadBool(dict, "ExternalConnected"),
            IsCharging: ReadBool(dict, "IsCharging"),
            FullyCharged: ReadBool(dict, "FullyCharged"),
            AmperageMa: ReadSignedNumber(dict, "Amperage"),
            InstantAmperageMa: ReadSignedNumber(dict, "InstantAmperage"),
            TimeRemainingMinutes: ReadNumber(dict, "TimeRemaining"),
            AvgTimeToEmptyMinutes: ReadNumber(dict, "AvgTimeToEmpty"),
            FullChargeCapacityMah: ReadNumber(batteryData, "FullChargeCapacity"),
            RemainingCapacityMah: ReadNumber(batteryData, "RemainingCapacity"),
            DesignCapacityMah: ReadNumber(batteryData, "DesignCapacity"),
            BatteryPowerMw: ReadSignedNumber(batteryData, "BatteryPower"));
    }

    /// <summary>从字典读取数值；键不存在或类型不符返回 null。</summary>
    private static double? ReadNumber(IntPtr dict, string keyName)
    {
        if (dict == IntPtr.Zero)
            return null;

        var key = Key(keyName);
        try
        {
            var value = CFDictionaryGetValue(dict, key);
            if (value == IntPtr.Zero)
                return null;

            // 先按整数读（IOKit 绝大多数容量/电压字段是整数），失败再按 double
            if (CFNumberGetValue(value, CFNumberSInt64Type, out long l))
                return l;
            if (CFNumberGetValue(value, CFNumberDoubleType, out double d))
                return d;
            return null;
        }
        finally
        {
            CFRelease(key);
        }
    }

    /// <summary>
    /// 读取可能以无符号 32 位存储负值的字段（电流 / 功率），并还原符号。
    /// 见 <see cref="BatteryRaw"/> 的说明。
    /// </summary>
    private static double? ReadSignedNumber(IntPtr dict, string keyName)
        => ReadNumber(dict, keyName) is { } raw ? NormalizeSigned32(raw) : null;

    /// <summary>从字典读取布尔（IOKit 用 CFBoolean）；键不存在返回 null。</summary>
    private static bool? ReadBool(IntPtr dict, string keyName)
    {
        if (dict == IntPtr.Zero)
            return null;

        var key = Key(keyName);
        try
        {
            var value = CFDictionaryGetValue(dict, key);
            if (value == IntPtr.Zero)
                return null;
            return CFBooleanGetValue(value);
        }
        finally
        {
            CFRelease(key);
        }
    }

    // ============================================================
    //  电源监听：IOPSNotificationCreateRunLoopSource（公开 API）
    // ============================================================

    /// <summary>
    /// IOPS 电源变化回调类型。上下文指针即注册时传入的 refCon。
    /// </summary>
    public delegate void IOPSNotifyCallback(IntPtr context);

    [DllImport(LibIOKit)]
    private static extern IntPtr IOPSNotificationCreateRunLoopSource(IOPSNotifyCallback callback, IntPtr context);

    /// <summary>
    /// 创建"电源来源变化"的 runloop 通知源（公开 API，非私有 IOPM 通知）。
    ///
    /// 相比 IOKit 私有的 <c>IOServiceAddInterestNotification(kIOGeneralInterest)</c>：
    ///   · <c>kIOGeneralInterest</c> 未从 IOKit 导出（实测 dlsym 返回 NULL），无法用 P/Invoke 取得；
    ///   · <c>IOPSNotificationCreateRunLoopSource</c> 是 IOPowerSources.h 的公开接口，稳定可用。
    ///
    /// 返回需 <see cref="ReleaseNotificationSource"/> 释放的 CFTypeRef（不是 IOKit 对象）。
    /// 只表示"电源有变化"，具体数值仍由 <see cref="TryReadBattery"/> 读取
    /// （注意：<c>IOPSPowerSourceStateKey</c> 在"已接电但未充电"时会报 Battery Power，
    ///   因此不能用它判断是否插电，必须用 AppleSmartBattery 的 ExternalConnected）。
    /// </summary>
    public static IntPtr CreatePowerSourceNotificationSource(IOPSNotifyCallback callback, IntPtr context)
    {
        try
        {
            return IOPSNotificationCreateRunLoopSource(callback, context);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>释放 IOPS 通知源创建的 CoreFoundation 对象。</summary>
    public static void ReleaseNotificationSource(IntPtr source)
    {
        if (source != IntPtr.Zero)
            CFRelease(source);
    }

    /// <summary>把通知源挂到当前线程 runloop（幂等）。</summary>
    public static void AttachToCurrentRunLoop(IntPtr runLoopSource)
    {
        if (runLoopSource != IntPtr.Zero)
            CFRunLoopAddSource(CFRunLoopGetCurrent(), runLoopSource, DefaultRunLoopMode);
    }

    public static IntPtr CurrentRunLoop() => CFRunLoopGetCurrent();

    public static void StopRunLoop(IntPtr runLoop)
    {
        if (runLoop != IntPtr.Zero)
            CFRunLoopStop(runLoop);
    }

    /// <summary>跑一次 runloop 迭代；返回是否处理了事件源。</summary>
    public static bool RunLoopOnce(double seconds)
        => CFRunLoopRunInMode(DefaultRunLoopMode, seconds, true) == 1; // kCFRunLoopRunHandledSource

    // ============================================================
    //  pmset 兜底
    // ============================================================


    /// <summary>
    /// 解析 <c>pmset -g batt</c> 输出，返回 (交流电, 百分比)。
    /// 仅作兜底使用（需要 fork 子进程，约 30–80ms）。
    /// 典型输出：
    /// <code>
    /// Now drawing from 'AC Power'
    ///  -InternalBattery-0 (id=35651683)	100%; charged; 0:00 remaining present: true
    /// </code>
    /// </summary>
    public static (bool AcOnline, int? Percent)? TryReadFromPmset()
    {
        try
        {
            using var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = PmsetPath,
                    Arguments = "-g batt",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!p.Start())
                return null;

            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                return null;
            }

            bool acOnline = output.Contains("'AC Power'", StringComparison.Ordinal);
            int? percent = null;

            var percentIdx = output.IndexOf("%;", StringComparison.Ordinal);
            if (percentIdx > 0)
            {
                // 从百分号往前回退取连续数字
                int end = percentIdx;
                int start = end;
                while (start > 0 && char.IsDigit(output[start - 1]))
                    start--;

                if (start < end && int.TryParse(output.AsSpan(start, end - start), out var v))
                    percent = Math.Clamp(v, 0, 100);
            }

            return (acOnline, percent);
        }
        catch
        {
            return null;
        }
    }
}
