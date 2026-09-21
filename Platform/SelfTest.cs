using System;
using System.Text;

namespace EndfieldCharge.Services;

/// <summary>
/// <c>--selftest</c> 诊断：打印一份 JSON 报告后退出（不进 UI 循环）。
///
/// 用途：
///   · CI 的 macOS runner 上做真实冒烟，验证 IOKit / CoreFoundation / AppKit 互操作在干净环境可用；
///   · 用户报障时一键取证（电池数值、数据源、bundle 状态、自启状态）。
/// </summary>
public static class SelfTest
{
    /// <summary>运行自检。返回进程退出码（0 = 成功，即使在无电池机型上）。</summary>
    public static int Run()
    {
        var sb = new StringBuilder();
        sb.Append("{\n");

        try
        {
            IPlatformServices platform;
            try
            {
                platform = PlatformServices.Create();
            }
            catch (Exception ex)
            {
                Append(sb, "platform", "unsupported");
                Append(sb, "error", ex.Message);
                sb.Append("}\n");
                Console.Write(sb.ToString());
                return 1;
            }

            Append(sb, "platform", platform.Name);
            Append(sb, "bundle", platform.IsBundle.ToString().ToLowerInvariant());
            Append(sb, "powerSaveModeText", platform.Name == "macOS" ? "低电量模式" : "省电模式");
            Append(sb, "dataDir", AppPaths.DataDirectory);
            Append(sb, "logDir", AppPaths.LogDirectory);
            Append(sb, "settingsPath", AppPaths.SettingsFilePath);

            // 自启状态
            try
            {
                Append(sb, "autoStartSupported", platform.AutoStart.IsSupported.ToString().ToLowerInvariant());
                Append(sb, "autoStartEnabled", platform.AutoStart.IsEnabled().ToString().ToLowerInvariant());
            }
            catch (Exception ex)
            {
                Append(sb, "autoStartError", ex.Message);
            }

            // 电池与电源
            using var monitor = platform.CreatePowerMonitor();

            Append(sb, "acReadable", monitor.TryGetAcOnline(out bool ac).ToString().ToLowerInvariant());
            Append(sb, "acOnline", ac.ToString().ToLowerInvariant());

            var snap = monitor.GetSnapshot();
            if (snap is null)
            {
                Append(sb, "battery", "null");
            }
            else
            {
                Append(sb, "batteryPercent", snap.Percent.ToString());
                Append(sb, "remainingWh", snap.RemainingWh.ToString("F3"));
                Append(sb, "fullWh", snap.FullWh.ToString("F3"));
                Append(sb, "designWh", snap.DesignCapacityWh?.ToString("F3") ?? "null");
                Append(sb, "healthPercent", snap.HealthPercent?.ToString("F1") ?? "null");
                Append(sb, "charging", snap.Charging.ToString().ToLowerInvariant());
                Append(sb, "rateWatts", snap.RateWatts?.ToString("F3") ?? "null");
                Append(sb, "estimatedRemainingMinutes",
                    snap.EstimatedRemaining?.TotalMinutes.ToString("F0") ?? "null");
                Append(sb, "hasBattery", snap.HasBattery.ToString().ToLowerInvariant());
            }

            // 平台专属诊断
#if ENDFIELD_MACOS
            AppendMacOSDiagnostics(sb, platform);
#endif
        }
        catch (Exception ex)
        {
            Append(sb, "fatalError", Describe(ex));
            sb.Append("}\n");
            Console.Write(sb.ToString());
            return 1;
        }

        sb.Append("}\n");
        Console.Write(sb.ToString());
        return 0;
    }

#if ENDFIELD_MACOS
    private static void AppendMacOSDiagnostics(StringBuilder sb, IPlatformServices platform)
    {
        Append(sb, "inAppBundle", MacOSAppKit.IsInAppBundle.ToString().ToLowerInvariant());
        Append(sb, "bundlePath", MacOSAppKit.BundlePath() ?? "null");
        Append(sb, "bundleIdentifier", MacOSAppKit.BundleIdentifier() ?? "null");

#pragma warning disable CA1416 // 本方法仅在 ENDFIELD_MACOS 编译符号下存在
        Append(sb, "bundleExecutable", MacOSAutoStart.AppBundleExecutablePath() ?? "null");
#pragma warning restore CA1416

        var lowPower = MacOSAppKit.TryGetLowPowerMode();
        Append(sb, "lowPowerModeSource", lowPower is null ? "pmset-or-unsupported" : "iokit");
        Append(sb, "lowPowerMode", lowPower?.ToString().ToLowerInvariant() ?? "unknown");

        // 刘海安全区：验证 objc_msgSend 的结构体返回约定是否正确
        // （safeAreaInsets 返回 NSEdgeInsets、frame/visibleFrame 返回 NSRect，共用一个签名）。
        // 判读：safeAreaTop 应当等于"菜单栏高度"（本机 32），且 visibleTopCocoaY 应为正且合理。
        var screens = MacOSAppKit.GetScreenGeometries();
        Append(sb, "screenCount", screens.Count.ToString());
        for (int i = 0; i < screens.Count; i++)
        {
            var s2 = screens[i];
            Append(sb, $"screen{i}.safeAreaTop", s2.SafeAreaTop.ToString("F1"));
            Append(sb, $"screen{i}.frameTop", s2.FrameTop.ToString("F1"));
            Append(sb, $"screen{i}.visibleFrameTop", s2.VisibleFrameTop.ToString("F1"));
            Append(sb, $"screen{i}.menuBarHeight", (s2.FrameTop - s2.VisibleFrameTop).ToString("F1"));
        }

        double? safeTop = platform.TryGetScreenSafeAreaTop(
            screenLeft: 0d,
            screenTop: 0d);
        Append(sb, "resolvedSafeAreaTop", safeTop?.ToString("F1") ?? "null");
        Append(sb, "notchDetected", (safeTop is > 0d).ToString().ToLowerInvariant());
    }
#endif

    private static void Append(StringBuilder sb, string key, string value)
    {
        if (sb.Length > 2)
            sb.Append(",\n");
        sb.Append("  \"").Append(key).Append("\": \"").Append(Escape(value)).Append('"');
    }

    /// <summary>展开异常链，便于定位 TypeInitializationException 的真实内因。</summary>
    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (sb.Length > 0)
                sb.Append(" <- ");
            sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
        }

        return sb.ToString();
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
