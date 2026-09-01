using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndfieldCharge.Services;

/// <summary>
/// Windows 电源 / 电池相关的原生 API。
/// 两条读取路径：
///   1. powrprof!CallNtPowerInformation(SystemBatteryState) —— 主路径，同步、无 WMI 开销，直接给 mWh。
///   2. WMI Win32_Battery —— 兜底，部分机型 powrprof 返回 MaxCapacity=0。
/// 一条监听路径：
///   RegisterPowerSettingNotification + 隐藏消息窗，接收 WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PowerNative
{
    // ---------- CallNtPowerInformation ----------

    private const int SystemBatteryStateLevel = 5;

    /// <summary>Windows 电池状态（InformationLevel = SystemBatteryState）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemBatteryState
    {
        public byte AcOnLine;
        public byte BatteryPresent;
        public byte Charging;
        public byte Discharging;
        public byte Spare1;
        public byte Spare2;
        public byte Spare3;
        public byte Spare4;

        /// <summary>满充容量，毫瓦时（mWh）。</summary>
        public uint MaxCapacity;

        /// <summary>剩余容量，毫瓦时（mWh）。</summary>
        public uint RemainingCapacity;

        /// <summary>充放电速率，毫瓦（mW）。正=充电，负=放电。</summary>
        public int Rate;

        /// <summary>剩余时间估计，秒。未知时为 0x80000000。</summary>
        public uint EstimatedTime;

        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint CallNtPowerInformation(
        int informationLevel,
        IntPtr inputBuffer,
        int inputBufferSize,
        IntPtr outputBuffer,
        int outputBufferSize);

    /// <summary>读取系统电池状态。返回 false 表示无电池或读取失败。</summary>
    public static bool TryGetBatteryState(out SystemBatteryState state)
    {
        state = default;
        int size = Marshal.SizeOf<SystemBatteryState>();
        IntPtr ptr = IntPtr.Zero;
        try
        {
            ptr = Marshal.AllocHGlobal(size);
            // NTSTATUS：0 = STATUS_SUCCESS
            if (CallNtPowerInformation(SystemBatteryStateLevel, IntPtr.Zero, 0, ptr, size) != 0)
                return false;

            state = Marshal.PtrToStructure<SystemBatteryState>(ptr);
            return state.BatteryPresent != 0 && state.MaxCapacity > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (ptr != IntPtr.Zero)
                Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>只取 AC 是否在线（不依赖电池存在）。</summary>
    public static bool TryGetAcOnline(out bool acOnline)
    {
        acOnline = false;
        if (!TryGetBatteryState(out var s))
            return false;
        acOnline = s.AcOnLine != 0;
        return true;
    }

    // ---------- 电源设置通知 ----------

    /// <summary>GUID_ACDC_POWER_SOURCE：交流电 / 电池供电切换。</summary>
    public static readonly Guid GuidAcdcPowerSource = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");

    /// <summary>GUID_BATTERY_PERCENTAGE_REMAINING：电量百分比变化。</summary>
    public static readonly Guid GuidBatteryPercentageRemaining = new("a7ad8041-b45a-4cae-87a3-eecbb468a9e1");

    public const int WmPowerBroadcast = 0x0218;
    public const int PbtPowerSettingChange = 0x8013;
    public const int PbtApmPowerStatusChange = 0x000A;
    public const int DeviceNotifyWindowHandle = 0x00000000;

    public const int AcPowerSource = 1;
    public const int DcPowerSource = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(
        IntPtr hRecipient,
        ref Guid powerSettingGuid,
        int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    // ---------- 隐藏消息窗 ----------

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public IntPtr LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        public string? LpszMenuName;
        public string LpszClassName;
        public IntPtr HIconSm;
    }

    /// <summary>HWND_MESSAGE：创建一个只收消息、不可见的 message-only 窗口。</summary>
    public static readonly IntPtr HwndMessage = new(-3);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassExW(ref WndClassEx lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string? lpWindowName,
        uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref Msg lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr HWnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    public const uint WmQuit = 0x0012;
    public const uint WmApp = 0x8000;

    // ---------- 壳通知（托盘图标弹气泡用，可选） ----------

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
}
