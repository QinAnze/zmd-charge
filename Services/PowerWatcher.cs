using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace EndfieldCharge.Services;

/// <summary>
/// 电源来源（交流 / 电池）变化监听。
///
/// 主路径：RegisterPowerSettingNotification 订阅 GUID_ACDC_POWER_SOURCE，
///         由一个后台线程上的 message-only 隐藏窗口接收 WM_POWERBROADCAST。
/// 兜底  ：低频轮询（部分机型/电源管理驱动不派发通知），开销可忽略。
///
/// 注意：事件在后台线程上触发，订阅方需自行切回 UI 线程。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerWatcher : IDisposable
{
    private const string ClassName = "EndfieldCharge_PowerMsgWindow";
    private const uint WmDestroy = 0x0002;

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

    private readonly PowerNative.WndProcDelegate _wndProc;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private IntPtr _acdcNotify;
    private bool? _lastAcOnline;
    private bool _initialized;
    private int _confirmSeq;
    private volatile bool _stopping;
    private bool _disposed;

    public PowerWatcher()
    {
        // 保持委托存活，防止被 GC 回收后 WndProc 崩溃
        _wndProc = WndProc;
    }

    /// <summary>电源来源变化。参数为当前是否交流电供电（true=已插电）。</summary>
    public event EventHandler<bool>? PowerSourceChanged;

    /// <summary>已启动并持有初始状态。</summary>
    public bool IsRunning => _thread is { IsAlive: true };

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
            PowerNative.PostThreadMessageW(_threadId, PowerNative.WmQuit, IntPtr.Zero, IntPtr.Zero);

        // 消息循环退出时会自行清理窗口与通知句柄
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        _threadId = 0;
    }

    private void MessageLoop()
    {
        _threadId = PowerNative.GetCurrentThreadId();

        // ---- 注册窗口类 ----
        var wcex = new PowerNative.WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<PowerNative.WndClassEx>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            HInstance = PowerNative.GetModuleHandleW(null),
            LpszClassName = ClassName,
        };
        PowerNative.RegisterClassExW(ref wcex);

        // ---- message-only 窗口：不可见、不进任务栏、只收消息 ----
        _hwnd = PowerNative.CreateWindowExW(
            0, ClassName, null, 0,
            0, 0, 0, 0,
            PowerNative.HwndMessage,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            var guid = PowerNative.GuidAcdcPowerSource;
            _acdcNotify = PowerNative.RegisterPowerSettingNotification(
                _hwnd, ref guid, PowerNative.DeviceNotifyWindowHandle);
        }

        // ---- 记录初始状态：启动时已插电则不弹 ----
        // 必须注册通知之后才能开始收事件，否则会在"还不知道当前状态"时
        // 就被某个事件拽到错误的初值上、然后下一次轮询又把它"修正"成真实值，
        // 看起来就像发生了一次状态变化 → 误触 HUD。
        if (PowerNative.TryGetAcOnline(out bool ac))
        {
            _lastAcOnline = ac;
            _initialized = true;
        }

        // ---- 轮询兜底定时器 ----
        using var pollTimer = new Timer(_ => PollOnce(), null, PollInterval, PollInterval);

        // ---- 消息循环 ----
        while (PowerNative.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            PowerNative.TranslateMessage(ref msg);
            PowerNative.DispatchMessageW(ref msg);
        }

        // ---- 线程内清理（窗口/通知必须在此线程销毁）----
        if (_acdcNotify != IntPtr.Zero)
        {
            PowerNative.UnregisterPowerSettingNotification(_acdcNotify);
            _acdcNotify = IntPtr.Zero;
        }
        if (_hwnd != IntPtr.Zero)
        {
            PowerNative.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private void PollOnce()
    {
        if (_stopping) return;
        if (!PowerNative.TryGetAcOnline(out bool ac)) return;
        TraceEvent($"PollOnce ac={ac}");
        RaiseIfChanged(ac);
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

        if (!PowerNative.TryGetAcOnline(out bool current))
            return;

        if (current != candidate)
        {
            // 抖动：回滚，避免后续读回真实值时被当成又一次变化
            _lastAcOnline = previous;
            return;
        }

        // 只有"插上充电器"这一侧才上报
        if (candidate)
            PowerSourceChanged?.Invoke(this, true);
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
        if (msg == PowerNative.WmPowerBroadcast)
        {
            int w = wParam.ToInt32();

            if (w == PowerNative.PbtPowerSettingChange && lParam != IntPtr.Zero)
            {
                try
                {
                    var setting = Marshal.PtrToStructure<PowerNative.PowerBroadcastSetting>(lParam);
                    if (setting.PowerSetting == PowerNative.GuidAcdcPowerSource)
                    {
                        RaiseIfChanged(setting.Data == PowerNative.AcPowerSource);
                    }
                }
                catch
                {
                    // 结构解析失败则忽略，轮询兜底会补上
                }
            }
            else if (w == PowerNative.PbtApmPowerStatusChange)
            {
                // 通用电源状态变化：重新读一次真实状态
                if (PowerNative.TryGetAcOnline(out bool ac))
                    RaiseIfChanged(ac);
            }
        }
        else if (msg == WmDestroy)
        {
            return IntPtr.Zero;
        }

        return PowerNative.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}
