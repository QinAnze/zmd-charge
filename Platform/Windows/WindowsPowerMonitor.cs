using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace EndfieldCharge.Services;

/// <summary>
/// Windows 电源来源（交流 / 电池）变化 + 省电/节能模式监听。
///
/// 主路径：RegisterPowerSettingNotification 订阅 GUID_ACDC_POWER_SOURCE，
///         由一个后台线程上的 message-only 隐藏窗口接收 WM_POWERBROADCAST。
/// 兜底  ：低频轮询（部分机型/电源管理驱动不派发通知），开销可忽略。
///
/// 注意：事件在后台线程上触发，订阅方需自行切回 UI 线程。
/// 线程模型：单一消息循环线程串行处理所有状态变更，因此本类无需额外加锁；
///           macOS 实现因 runloop + timer 两个线程需显式加锁（见 MacOSPowerMonitor）。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPowerMonitor : IPowerMonitor
{
    private const string ClassName = "EndfieldCharge_PowerMsgWindow";
    private const uint WmDestroy = 0x0002;

    /// <summary>24H2（build 26100）起节能模式取代省电模式：
    /// 只有 GUID_ENERGY_SAVER_STATUS 会推送真实状态，老 GUID_POWER_SAVING_STATUS
    /// 与 SystemStatusFlag 均不再反映该开关（25H2 实测）。按版本分流，避免两个
    /// GUID 的注册回执（各带一次当前状态）在启动时互相打架、误报状态变化。</summary>
    private static readonly bool UseEnergySaverGuid =
        Environment.OSVersion.Version.Build >= 26100;

    /// <summary>轮询兜底间隔。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 状态变化的确认延迟。
    /// Windows 在电池充满、充电器握手、固件上报抖动时会瞬间发一次
    /// "切到电池供电"(DC) 的通知，几秒内又回到 AC。若不确认就采信，
    /// 轮询读回 AC 时就会被当成一次真实的"拔掉→插上"，凭空多弹一次 HUD。
    /// 因此两个方向的变化都要复读确认，确认不符就回滚。
    /// </summary>
    private static readonly TimeSpan ChangeConfirmDelay = TimeSpan.FromMilliseconds(400);

    private readonly WindowsPowerNative.WndProcDelegate _wndProc;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private IntPtr _acdcNotify;
    private IntPtr _saverNotify;
    private IntPtr _energySaverNotify;
    private bool? _lastAcOnline;
    private bool? _lastSaverEnabled;
    private bool _initialized;
    private int _confirmSeq;
    private volatile bool _stopping;
    private bool _disposed;

    public WindowsPowerMonitor()
    {
        // 保持委托存活，防止被 GC 回收后 WndProc 崩溃
        _wndProc = WndProc;
    }

    /// <summary>电源来源变化。参数为当前是否交流电供电（true=已插电）。</summary>
    public event EventHandler<bool>? PowerSourceChanged;

    /// <summary>省电模式开关变化。参数为省电模式是否开启（true=已开启）。</summary>
    public event EventHandler<bool>? PowerSavingChanged;

    /// <summary>已启动并持有初始状态。</summary>
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>当前电池快照（powrprof 主路径 + WMI 兜底）；无电池返回 null。</summary>
    public BatterySnapshot? GetSnapshot() => WindowsBatteryReader.GetSnapshot();

    /// <summary>只取 AC 是否在线（不依赖电池存在）。</summary>
    public bool TryGetAcOnline(out bool acOnline) => WindowsPowerNative.TryGetAcOnline(out acOnline);

    public void Start()
    {
        if (_thread is not null)
            return;

        _stopping = false;
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "PowerWatcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;

        if (_threadId != 0)
            WindowsPowerNative.PostThreadMessageW(_threadId, WindowsPowerNative.WmQuit, IntPtr.Zero, IntPtr.Zero);

        // 消息循环退出时会自行清理窗口与通知句柄
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        _threadId = 0;
    }

    private void MessageLoop()
    {
        _threadId = WindowsPowerNative.GetCurrentThreadId();

        // ---- 注册窗口类 ----
        var wcex = new WindowsPowerNative.WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<WindowsPowerNative.WndClassEx>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            HInstance = WindowsPowerNative.GetModuleHandleW(null),
            LpszClassName = ClassName,
        };
        WindowsPowerNative.RegisterClassExW(ref wcex);

        // ---- message-only 窗口：不可见、不进任务栏、只收消息 ----
        _hwnd = WindowsPowerNative.CreateWindowExW(
            0, ClassName, null, 0,
            0, 0, 0, 0,
            WindowsPowerNative.HwndMessage,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            var guid = WindowsPowerNative.GuidAcdcPowerSource;
            _acdcNotify = WindowsPowerNative.RegisterPowerSettingNotification(
                _hwnd, ref guid, WindowsPowerNative.DeviceNotifyWindowHandle);

            var saverGuid = WindowsPowerNative.GuidPowerSavingStatus;
            _saverNotify = WindowsPowerNative.RegisterPowerSettingNotification(
                _hwnd, ref saverGuid, WindowsPowerNative.DeviceNotifyWindowHandle);

            var esGuid = WindowsPowerNative.GuidEnergySaverStatus;
            _energySaverNotify = WindowsPowerNative.RegisterPowerSettingNotification(
                _hwnd, ref esGuid, WindowsPowerNative.DeviceNotifyWindowHandle);

            Logger.Info($"PowerWatcher: hwnd=0x{_hwnd.ToInt64():X}, acdcNotify=0x{_acdcNotify.ToInt64():X}, saverNotify=0x{_saverNotify.ToInt64():X}, esNotify=0x{_energySaverNotify.ToInt64():X}");
        }
        else
        {
            Logger.Warn("PowerWatcher: message window create failed, only polling available");
        }

        // ---- 记录初始状态：启动时已插电/已开省电则不弹 ----
        // 必须注册通知之后才能开始收事件，否则会在"还不知道当前状态"时
        // 就被某个事件拽到错误的初值上、然后下一次轮询又把它"修正"成真实值，
        // 看起来就像发生了一次状态变化 → 误触 HUD。
        if (WindowsPowerNative.TryGetAcOnline(out bool ac))
        {
            _lastAcOnline = ac;
            _initialized = true;
        }
        if (WindowsPowerNative.TryGetPowerSavingStatus(out bool saver))
        {
            _lastSaverEnabled = saver;
            Logger.Info($"PowerWatcher: initial saver={saver}");
        }

        // ---- 轮询兜底定时器 ----
        using var pollTimer = new Timer(_ => PollOnce(), null, PollInterval, PollInterval);

        // ---- 消息循环 ----
        while (WindowsPowerNative.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            WindowsPowerNative.TranslateMessage(ref msg);
            WindowsPowerNative.DispatchMessageW(ref msg);
        }

        // ---- 线程内清理（窗口/通知必须在此线程销毁）----
        if (_acdcNotify != IntPtr.Zero)
        {
            WindowsPowerNative.UnregisterPowerSettingNotification(_acdcNotify);
            _acdcNotify = IntPtr.Zero;
        }
        if (_saverNotify != IntPtr.Zero)
        {
            WindowsPowerNative.UnregisterPowerSettingNotification(_saverNotify);
            _saverNotify = IntPtr.Zero;
        }
        if (_energySaverNotify != IntPtr.Zero)
        {
            WindowsPowerNative.UnregisterPowerSettingNotification(_energySaverNotify);
            _energySaverNotify = IntPtr.Zero;
        }
        if (_hwnd != IntPtr.Zero)
        {
            WindowsPowerNative.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private void PollOnce()
    {
        if (_stopping) return;
        if (!WindowsPowerNative.TryGetAcOnline(out bool ac)) return;
        TraceEvent($"PollOnce ac={ac}");
        RaiseIfChanged(ac);

        // 省电模式轮询兜底（部分机型不派发 GUID_POWER_SAVING_STATUS 通知）
        if (WindowsPowerNative.TryGetPowerSavingStatus(out bool saver))
        {
            TraceEvent($"PollOnce saver={saver}");
            RaiseSaverIfChanged(saver);
        }
    }

    private void RaiseIfChanged(bool acOnline)
    {
        TraceEvent($"RaiseIfChanged(ac={acOnline}), last={_lastAcOnline}, init={_initialized}");

        // 第一次读到真实状态前，所有事件都吞掉，避免把"未知"误当 DC →
        // 随后读到真实 AC 时被当成状态变化。
        if (!_initialized)
        {
            _lastAcOnline = acOnline;
            _initialized = true;
            return;
        }

        if (_lastAcOnline == acOnline)
            return;

        var previous = _lastAcOnline;
        _lastAcOnline = acOnline;

        // 两个方向的变化都延迟复读一次，滤掉电源状态的瞬时抖动
        _ = ConfirmChangeAsync(previous, acOnline, ++_confirmSeq);
    }

    private async Task ConfirmChangeAsync(bool? previous, bool candidate, int seq)
    {
        try
        {
            await Task.Delay(ChangeConfirmDelay);
        }
        catch
        {
            return;
        }

        // 期间又有更新的变化，或正在停止 → 本次确认作废
        if (_stopping || seq != _confirmSeq)
            return;

        if (!WindowsPowerNative.TryGetAcOnline(out bool current))
            return;

        if (current != candidate)
        {
            // 抖动：回滚，避免后续读回真实值时被当成又一次变化
            _lastAcOnline = previous;
            return;
        }

        // 两个方向都上报，由订阅方决定弹什么（插电=完整三态，拔电=简化电量胶囊）
        PowerSourceChanged?.Invoke(this, candidate);
    }

    /// <summary>
    /// 省电模式状态变化：无确认延迟——开/关省电是用户或系统的明确动作，
    /// 且轮询与通知双路径都以"与上次不同"为闸，不会重复触发。
    /// </summary>
    private void RaiseSaverIfChanged(bool enabled)
    {
        // 第一次读到真实状态前只记录，不上报（启动时已开省电不弹）
        if (_lastSaverEnabled is null)
        {
            _lastSaverEnabled = enabled;
            return;
        }

        if (_lastSaverEnabled == enabled)
            return;

        _lastSaverEnabled = enabled;
        TraceEvent($"PowerSavingChanged(enabled={enabled})");
        Logger.Info($"PowerWatcher: power saving { (enabled ? "ON" : "OFF") }");
        PowerSavingChanged?.Invoke(this, enabled);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void TraceEvent(string msg)
    {
        if (!Array.Exists(Environment.GetCommandLineArgs(), a => a == "--power-log"))
            return;
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "power-log.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch
        {
            // 忽略日志失败
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WindowsPowerNative.WmPowerBroadcast)
        {
            int w = wParam.ToInt32();

            if (w == WindowsPowerNative.PbtPowerSettingChange && lParam != IntPtr.Zero)
            {
                try
                {
                    var setting = Marshal.PtrToStructure<WindowsPowerNative.PowerBroadcastSetting>(lParam);
                    if (setting.PowerSetting == WindowsPowerNative.GuidAcdcPowerSource)
                    {
                        RaiseIfChanged(setting.Data == WindowsPowerNative.AcPowerSource);
                    }
                    else if (setting.PowerSetting == WindowsPowerNative.GuidPowerSavingStatus
                             && !UseEnergySaverGuid)
                    {
                        RaiseSaverIfChanged(setting.Data == 1);
                    }
                    else if (setting.PowerSetting == WindowsPowerNative.GuidEnergySaverStatus
                             && UseEnergySaverGuid)
                    {
                        // 24H2+ 节能模式：0=关, 1=标准, 2=高节能（非 0 即开启）
                        RaiseSaverIfChanged(setting.Data != 0);
                    }
                }
                catch
                {
                    // 结构解析失败则忽略，轮询兜底会补上
                }
            }
            else if (w == WindowsPowerNative.PbtApmPowerStatusChange)
            {
                // 通用电源状态变化：重新读一次真实状态
                if (WindowsPowerNative.TryGetAcOnline(out bool ac))
                    RaiseIfChanged(ac);
                if (WindowsPowerNative.TryGetPowerSavingStatus(out bool saver))
                    RaiseSaverIfChanged(saver);
            }
        }
        else if (msg == WmDestroy)
        {
            return IntPtr.Zero;
        }

        return WindowsPowerNative.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}
