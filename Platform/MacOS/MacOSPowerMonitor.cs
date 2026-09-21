using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace EndfieldCharge.Services;

/// <summary>
/// macOS 电源来源（交流 / 电池）变化 + 低电量模式监听。
///
/// 架构与 <see cref="WindowsPowerMonitor"/> 严格同构，便于两端行为一致：
///   主路径： IOKit <c>IOPMPowerSource</c> general interest 通知，挂在本类的后台线程 runloop 上
///   兜底  ： 2s 低频轮询（与 Windows 同间隔），IOKit 通知不可用时仍能工作
///   去抖  ： 400ms 双向复读确认，语义与 Windows 完全一致
///   静默  ： 注册通知后才记录初值，'未知 → 真实值' 不算变化，避免启动即弹 HUD
///
/// **与 Windows 的唯一语义差异（线程模型）**：
///   Windows 侧所有状态变更都在单一消息循环线程上串行处理，因此无需加锁；
///   macOS 侧 runloop 回调在线程 A、轮询定时器在线程 B，故本类用 <see cref="_gate"/>
///   保护全部共享状态。
///
/// 注意：事件在后台线程上触发，订阅方需自行切回 UI 线程。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOSPowerMonitor : IPowerMonitor
{
    /// <summary>
    /// 轮询兜底间隔。
    /// Windows 侧没有可用的 push 通知（部分机型/驱动不派发），2s 轮询是主路径；
    /// macOS 侧 <c>IOPSNotificationCreateRunLoopSource</c> 承担主路径，
    /// 轮询只用于"通知源创建失败"和"补齐低电量模式状态"两种兜底场景，因此放宽到 5s。
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>状态变化的确认延迟（与 Windows 侧一致，滤掉固件瞬时抖动）。</summary>
    private static readonly TimeSpan ChangeConfirmDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>runloop 单次迭代的超时（只用于周期性检查停止标志，不承担轮询职责）。</summary>
    private const double RunLoopSliceSeconds = 0.25;

    /// <summary>主路径读取失败后多久才允许再次尝试 <c>pmset</c> 兜底，避免频繁 fork。</summary>
    private static readonly TimeSpan PmsetCooldown = TimeSpan.FromSeconds(30);

    /// <summary>回调期间需要跨 native 边界传递的状态（当前只需钉住委托）。</summary>
    private sealed class CallbackState
    {
        /// <summary>钉住委托，防止 GC 回收后 IOKit 回调调进已释放的托管对象。</summary>
        public MacOSPowerNative.IOPSNotifyCallback? Callback;
    }

    private readonly object _gate = new();
    private readonly CallbackState _callbackState = new();

    private Thread? _thread;
    private IntPtr _runLoop;
    private IntPtr _notificationSource;
    private GCHandle _callbackStateHandle;
    private volatile bool _stopping;
    private bool _disposed;

    // ---- 受 _gate 保护的共享状态 ----
    private bool? _lastAcOnline;
    private bool? _lastSaverEnabled;
    private bool _initialized;
    private int _confirmSeq;
    private volatile bool _pmsetFallbackActive;
    private DateTime _pmsetNextAllowedUtc = DateTime.MinValue;

    /// <summary>电源来源变化。参数为当前是否交流电供电（true=已插电）。</summary>
    public event EventHandler<bool>? PowerSourceChanged;

    /// <summary>低电量模式开关变化。参数为是否开启（true=已开启）。</summary>
    public event EventHandler<bool>? PowerSavingChanged;

    /// <summary>已启动。</summary>
    public bool IsRunning => _thread is { IsAlive: true };

    // ---------------- 数据读取 ----------------

    /// <summary>当前电池快照；无电池或读取失败返回 null。</summary>
    public BatterySnapshot? GetSnapshot()
        => MacOSBatteryReader.GetSnapshot();

    /// <summary>只取 AC 是否在线（不依赖电池存在）。</summary>
    public bool TryGetAcOnline(out bool acOnline)
        => MacOSBatteryReader.TryGetAcOnline(out acOnline);

    /// <summary>
    /// 采样一次。优先免 fork 的 IOKit 路径；主路径读不到容量时启用带冷却的 pmset 兜底。
    /// </summary>
    private (bool Ok, bool AcOnline, BatterySnapshot? Snapshot) Read()
    {
        // 1) IOKit 主路径
        var snapshot = MacOSBatteryReader.GetSnapshot();
        if (MacOSBatteryReader.TryGetAcOnline(out bool ac) && snapshot is { HasBattery: true })
        {
            _pmsetFallbackActive = false;
            return (true, ac, snapshot);
        }

        // 2) pmset 兜底（IOKit 完全不可用，或无电池机型需要 AC 状态）
        bool canTryPmset;
        lock (_gate)
        {
            canTryPmset = DateTime.UtcNow >= _pmsetNextAllowedUtc;
            if (canTryPmset)
                _pmsetNextAllowedUtc = DateTime.UtcNow + PmsetCooldown;
        }

        if (!canTryPmset)
        {
            // 冷却期内沿用 IOKit 能给出的部分结果
            return snapshot is not null && MacOSBatteryReader.TryGetAcOnline(out bool acOnly)
                ? (true, acOnly, snapshot)
                : (false, false, null);
        }

        if (MacOSPowerNative.TryReadFromPmset() is { } fallback)
        {
            if (!_pmsetFallbackActive)
            {
                _pmsetFallbackActive = true;
                Logger.Warn("MacOSPowerMonitor: IOKit 主路径不可用，已切换到 pmset 兜底（mWh 数值为估算值）");
            }

            var pmsetSnapshot = snapshot ?? new BatterySnapshot(
                RemainingWh: 0d, FullWh: 0d, Percent: fallback.Percent ?? 0,
                AcOnline: fallback.AcOnline, Charging: false);

            return (true, fallback.AcOnline, pmsetSnapshot);
        }

        return snapshot is not null && MacOSBatteryReader.TryGetAcOnline(out bool ac2)
            ? (true, ac2, snapshot)
            : (false, false, null);
    }

    // ---------------- 生命周期 ----------------

    public void Start()
    {
        if (_thread is not null)
            return;

        _stopping = false;

        // 先钉住回调状态与委托，再注册通知（IOKit 可能在注册返回前就回调）
        _callbackState.Callback = OnIOKitNotification;
        _callbackStateHandle = GCHandle.Alloc(_callbackState, GCHandleType.Normal);

        _thread = new Thread(MonitorLoop)
        {
            IsBackground = true,
            Name = "MacPowerMonitor",
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;

        // 让 CFRunLoopRun 退出：CFRunLoopStop 会令其立即返回
        var runLoop = _runLoop;
        if (runLoop != IntPtr.Zero)
            MacOSPowerNative.StopRunLoop(runLoop);

        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        _runLoop = IntPtr.Zero;

        // 释放 IOPS 通知源（CFTypeRef）
        MacOSPowerNative.ReleaseNotificationSource(_notificationSource);
        _notificationSource = IntPtr.Zero;

        if (_callbackStateHandle.IsAllocated)
        {
            _callbackState.Callback = null;
            _callbackStateHandle.Free();
        }
    }

    // ---------------- 后台线程 ----------------

    private void MonitorLoop()
    {
        _runLoop = MacOSPowerNative.CurrentRunLoop();

        // ---- 注册电源变化通知源（失败则退化为纯轮询）----
        _notificationSource = MacOSPowerNative.CreatePowerSourceNotificationSource(
            _callbackState.Callback!, GCHandle.ToIntPtr(_callbackStateHandle));

        if (_notificationSource != IntPtr.Zero)
        {
            MacOSPowerNative.AttachToCurrentRunLoop(_notificationSource);
            Logger.Info("MacOSPowerMonitor: IOPS 电源变化通知源已挂载");
        }
        else
        {
            Logger.Warn("MacOSPowerMonitor: IOPS 通知源创建失败，仅使用 2s 轮询兜底");
        }

        // ---- 记录初值：启动时已插电 / 已开低电量模式则不弹 ----
        // 必须在注册通知之后读取，否则会在"还不知道当前状态"时被事件拽到错误初值上。
        var initial = Read();
        lock (_gate)
        {
            if (initial.Ok)
            {
                _lastAcOnline = initial.AcOnline;
                _initialized = true;
            }

            if (TryReadPowerSaving(out bool saver))
                _lastSaverEnabled = saver;
        }

        Logger.Info($"MacOSPowerMonitor: runloop start, initialAc={_lastAcOnline?.ToString() ?? "?"}, " +
                    $"initialSaver={_lastSaverEnabled?.ToString() ?? "?"}, " +
                    $"pollInterval={PollInterval.TotalSeconds}s");

        // ---- 轮询兜底定时器 ----
        using var pollTimer = new Timer(_ => PollOnce(), null, PollInterval, PollInterval);

        // ---- runloop：分片运行以便周期性检查停止标志 ----
        while (!_stopping)
        {
            MacOSPowerNative.RunLoopOnce(RunLoopSliceSeconds);
        }

        Logger.Info("MacOSPowerMonitor: runloop exit");
    }

    /// <summary>
    /// 电源变化回调。在 runloop 线程上执行；异常绝不能外泄到 native 栈，否则进程崩溃。
    /// </summary>
    private void OnIOKitNotification(IntPtr context)
    {
        try
        {
            PollOnce();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    // ---------------- 采样与上报 ----------------

    private void PollOnce()
    {
        if (_stopping)
            return;

        try
        {
            bool needsConfirm = false;
            bool? pendingPrevious = null;
            bool pendingCandidate = false;
            int confirmSeq = 0;

            lock (_gate)
            {
                var sample = Read();

                // 第一次读到真实状态前，所有事件都吞掉，避免把"未知"误当 DC →
                // 随后读到真实 AC 时被当成状态变化。
                if (sample.Ok)
                {
                    if (!_initialized)
                    {
                        _lastAcOnline = sample.AcOnline;
                        _initialized = true;
                    }
                    else if (_lastAcOnline != sample.AcOnline)
                    {
                        pendingPrevious = _lastAcOnline;
                        pendingCandidate = sample.AcOnline;
                        _lastAcOnline = sample.AcOnline;
                        confirmSeq = ++_confirmSeq;
                        needsConfirm = true;
                    }
                }

                if (TryReadPowerSaving(out bool saver))
                    RaiseSaverIfChangedLocked(saver);
            }

            if (needsConfirm)
                _ = ConfirmChangeAsync(pendingPrevious ?? false, pendingCandidate, confirmSeq);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    private async Task ConfirmChangeAsync(bool previous, bool candidate, int seq)
    {
        try
        {
            await Task.Delay(ChangeConfirmDelay);
        }
        catch
        {
            return;
        }

        if (_stopping)
            return;

        lock (_gate)
        {
            // 期间又有更新的变化 → 本次确认作废
            if (seq != _confirmSeq)
                return;
        }

        if (!TryGetAcOnline(out bool current))
            return;

        if (current != candidate)
        {
            // 抖动：回滚，避免后续读回真实值时被当成又一次变化
            lock (_gate)
            {
                if (seq == _confirmSeq)
                    _lastAcOnline = previous;
            }
            return;
        }

        // 两个方向都上报，由订阅方（HudDisplayCoordinator）合并后决定播什么
        Logger.Info($"MacOSPowerMonitor: AC {(candidate ? "connected" : "disconnected")} → 通知订阅方");
        PowerSourceChanged?.Invoke(this, candidate);
    }

    /// <summary>
    /// 低电量模式状态变化：无确认延迟 —— 开/关是用户或系统的明确动作，
    /// 且通知与轮询双路径都以"与上次不同"为闸，不会重复触发。
    /// </summary>
    private void RaiseSaverIfChangedLocked(bool enabled)
    {
        // 第一次读到真实状态前只记录，不上报（启动时已开低电量模式不弹）
        if (_lastSaverEnabled is null)
        {
            _lastSaverEnabled = enabled;
            return;
        }

        if (_lastSaverEnabled == enabled)
            return;

        _lastSaverEnabled = enabled;
        Logger.Info($"MacOSPowerMonitor: low power mode {(enabled ? "ON" : "OFF")} → 通知订阅方");
        PowerSavingChanged?.Invoke(this, enabled);
    }

    /// <summary>
    /// 读取低电量模式。
    /// 主路径：<c>NSProcessInfo.isLowPowerModeEnabled</c>；
    /// 回退：<c>pmset -g</c> 的 <c>powermode</c>（1 = 低功耗）。
    /// **绝不**调用已被移除的 <c>lowPowerModeEnabled</c> 选择子（会崩溃）。
    /// </summary>
    private static bool TryReadPowerSaving(out bool enabled)
    {
        if (MacOSAppKit.TryGetLowPowerMode() is { } fromAppKit)
        {
            enabled = fromAppKit;
            return true;
        }

        return TryReadPowerSavingFromPmset(out enabled);
    }

    private static bool TryReadPowerSavingFromPmset(out bool enabled)
    {
        enabled = false;
        try
        {
            using var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/bin/pmset",
                    Arguments = "-g",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!p.Start())
                return false;

            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                return false;
            }

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("powermode", StringComparison.Ordinal))
                    continue;

                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out int mode))
                {
                    // 0/2 = 正常；1 = 低功耗模式
                    enabled = mode == 1;
                    return true;
                }
            }
        }
        catch
        {
            // pmset 不可用时静默
        }

        return false;
    }

    // ---------------- 释放 ----------------

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}
