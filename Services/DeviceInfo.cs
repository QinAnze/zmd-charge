using System.Management;
using System.Runtime.Versioning;

namespace EndfieldCharge.Services;

/// <summary>
/// 设备类型判定——WMI 标准字段 <c>Win32_ComputerSystem.PCSystemType</c>
/// （1 = Desktop，2 = Mobile）。WMI 不可用时回退电池判定。
/// </summary>
[SupportedOSPlatform("windows")]
public static class DeviceInfo
{
    /// <summary>是否为台式机。</summary>
    public static bool IsDesktop()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PCSystemType FROM Win32_ComputerSystem");

            foreach (ManagementBaseObject o in searcher.Get())
            {
                // 2 = Mobile（笔记本 / 平板）；其余（Desktop=1 等）按台式机处理
                return Convert.ToUInt32(o["PCSystemType"]) != 2;
            }
        }
        catch
        {
            // WMI 查询失败：回退到电池判定（有电池 → 移动设备）
        }

        var battery = BatteryService.GetSnapshot();
        return battery is null || !battery.HasBattery;
    }
}
