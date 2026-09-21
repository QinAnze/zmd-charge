using System;
using System.Collections.Generic;
using System.Threading;

namespace EndfieldCharge.Services;

/// <summary>
/// HUD 触发协调器：把「同一个物理动作引发的多条事件」合并为一次播放。
///
/// **为什么需要它（实测现象）**：
///   macOS 在插拔电源时，除了上报 AC 变化，还会**紧接着自动切换低电量模式**。
///   实测（`~/Library/Logs/EndfieldCharge/`）滞后分别为 402 ms / 674 ms / 674 ms。
///   而 <c>PowerSavingChanged</c> 也会触发 HUD，于是同一个物理动作连弹两次：
///   第二次进入 <c>HudWindow.ShowAndPlayAsync</c> 时执行的 <c>_cts.Cancel()</c>
///   会把第一次的动画**中途掐断**，表现为"先闪一下简化胶囊，再（或再也不）播完整三态"。
///
/// **策略**：
///   1. **优先级**：交流电变化(AC, 高) &gt; 低电量模式变化(Saver, 低)。
///      两条都在窗口内到达时只播 AC 的那次 —— 因为低电量模式变化是电源切换的副作用。
///   2. **自适应窗口**：每次到达把派发时刻推后 <see cref="_settleWindow"/>。
///      高优先级事件窗口短（120 ms，保证插拔手感接近即时）；
///      低优先级事件窗口长（1100 ms，用来吸收滞后数百毫秒的副作用事件）。
///   3. 窗口内出现更高优先级事件 → 直接丢弃低优先级事件。
///   4. 手动开关低电量模式时不会有 AC 事件，Saver 事件单独到达照常播放。
/// </summary>
internal sealed class HudDisplayCoordinator : IDisposable
{
    /// <summary>HUD 播放语义。与 <c>App</c> 的播放方法一一对应。</summary>
    internal enum HudKind
    {
        /// <summary>交流电接入 → 完整三态动画（超充模式）</summary>
        AcConnected,

        /// <summary>交流电断开 → 简化电量胶囊</summary>
        AcDisconnected,

        /// <summary>低电量模式开启 → 完整三态动画（低电量模式）</summary>
        SaverOn,

        /// <summary>低电量模式关闭 → 简化电量胶囊</summary>
        SaverOff,
    }

    /// <summary>高优先级事件（交流电）的稳定窗口。短，保证插拔手感。</summary>
    private static readonly TimeSpan AcSettleWindow = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// 低优先级事件（低电量模式）的稳定窗口。
    /// 需要足够长以吸收 macOS 自动切换低电量模式的滞后。
    /// </summary>
    private static readonly TimeSpan SaverSettleWindow = TimeSpan.FromMilliseconds(1100);

    /// <summary>
    /// 交流电事件派发后的"副作用抑制窗"。
    ///
    /// 实测 macOS 切换低电量模式可能滞后电源切换 **2 秒以上**（观测到 402 / 674 / 674 / 2128 ms），
    /// 单靠 <see cref="SaverSettleWindow"/> 的 1.1 s 窗口吸收不完。因此在派发过 AC 事件后的
    /// <see cref="AcCorrelationWindow"/> 内到达的低电量模式事件，视为该次电源切换的副作用直接丢弃 ——
    /// 低电量模式是 macOS 自己跟着电源状态切的，不该再单独弹一次 HUD。
    ///
    /// 只在 <see cref="_suppressAcCorrelatedSaver"/> 为 true 的平台（macOS）生效：
    /// Windows 的低电量模式（省电模式）不会被电源切换自动改写，那里用户手动开关必须照常提示。
    /// </summary>
    private static readonly TimeSpan AcCorrelationWindow = TimeSpan.FromSeconds(4);

    private readonly object _gate = new();
    private readonly Action<HudKind> _dispatch;
    private readonly bool _suppressAcCorrelatedSaver;

    /// <summary>可注入的定时器：默认用 <see cref="Timer"/>，测试可传假实现。</summary>
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;

    /// <summary>可注入时钟：默认 <see cref="DateTime.UtcNow"/>，测试可传假时钟。</summary>
    private readonly Func<DateTime> _utcNow;

    /// <summary>可注入日志输出。</summary>
    private readonly Action<string> _log;

    private readonly List<Entry> _queue = new();
    private Entry? _armed;
    private long _seq;

    /// <summary>最近一次派发 AC 事件的时刻，用于副作用抑制。仅 <see cref="_gate"/> 下访问。</summary>
    private DateTime _lastAcDispatchUtc = DateTime.MinValue;

    private bool _disposed;

    /// <param name="dispatch">实际播放 HUD 的回调（在定时器线程上调用，实现方需自行切回 UI 线程）。</param>
    /// <param name="suppressAcCorrelatedSaver">
    /// 是否丢弃"紧跟电源切换之后"的低电量模式事件。macOS 传 true（系统会自动跟着切），
    /// Windows 传 false（省电模式只由用户手动切换）。
    /// </param>
    /// <param name="schedule">定时器工厂，默认 <see cref="Timer"/>。测试传假实现。</param>
    /// <param name="utcNow">时钟，默认 <see cref="DateTime.UtcNow"/>。测试传假时钟。</param>
    /// <param name="log">日志回调，默认写 <see cref="Logger"/>。测试可传 null 静默。</param>
    public HudDisplayCoordinator(
        Action<HudKind> dispatch,
        bool suppressAcCorrelatedSaver,
        Func<TimeSpan, Action, IDisposable>? schedule = null,
        Func<DateTime>? utcNow = null,
        Action<string>? log = null)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _suppressAcCorrelatedSaver = suppressAcCorrelatedSaver;
        _schedule = schedule ?? DefaultSchedule;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log ?? Logger.Info;
    }

    /// <summary>交流电接入 / 断开。</summary>
    public void OnAcChanged(bool acOnline) => Enqueue(acOnline ? HudKind.AcConnected : HudKind.AcDisconnected);

    /// <summary>低电量模式开启 / 关闭。</summary>
    public void OnSaverChanged(bool enabled) => Enqueue(enabled ? HudKind.SaverOn : HudKind.SaverOff);

    private void Enqueue(HudKind kind)
    {
        var now = _utcNow();

        lock (_gate)
        {
            if (_disposed)
                return;

            var isAc = IsAc(kind);

            // 副作用抑制：AC 事件派发后不久到达的低电量模式事件，是系统跟着电源状态自动切的，
            // 不该再单独弹一次（否则用户会看到"完整三态播完又弹一个"）。
            if (_suppressAcCorrelatedSaver &&
                !isAc &&
                now - _lastAcDispatchUtc < AcCorrelationWindow &&
                !_queue.Exists(e => IsAc(e.Kind)))
            {
                _log(
                    $"HudTrigger: {kind} 距上次 AC 派发 " +
                    $"{(now - _lastAcDispatchUtc).TotalMilliseconds:F0}ms，判定为电源切换副作用，丢弃（不播）");
                return;
            }

            var window = isAc ? AcSettleWindow : SaverSettleWindow;
            var deadline = now + window;

            // 窗口内已有更高优先级事件 → 低优先级事件直接丢弃（副作用事件，不播）
            var droppedByPriority = _queue.Exists(e => Priority(e.Kind) > Priority(kind));
            if (droppedByPriority)
            {
                _log($"HudTrigger: {kind} 被更高优先级事件压制，丢弃（不播）");
            }
            else
            {
                // 清理被本次更高优先级事件压制的待播项
                var suppressed = _queue.RemoveAll(e => Priority(e.Kind) < Priority(kind));
                if (suppressed > 0)
                    _log($"HudTrigger: {kind} 压制了 {suppressed} 个低优先级待播项");

                // 同类事件连发（固件抖动 / 通知 + 轮询重复上报）：只推后已排队项的稳定窗，
                // 不再新增一项 —— 否则会产生多个同类 Entry，播完第一个后第二个又被派发。
                var existing = _queue.Find(e => e.Kind == kind);
                if (existing is not null)
                {
                    existing.Deadline = deadline;
                    _log($"HudTrigger: {kind} 重复到达，稳定窗顺延至 {window.TotalMilliseconds:F0}ms");
                }
                else
                {
                    _queue.Add(new Entry(kind, deadline, ++_seq));
                }
            }

            if (_queue.Count == 0)
                return;

            // 需要重新武装定时器？取队首（最早派发）作为武装目标
            var head = EarliestLocked();
            if (head is null)
                return;

            // 队首没变时，若是同类事件顺延了稳定窗，需要重新武装到新的派发时刻
            if (_armed is not null && _armed.Token == head.Token && _armed.Deadline == head.Deadline)
                return;

            _armed?.Timer.Dispose();
            _armed = head;
            head.Timer = _schedule(head.Deadline - now, () => Dispatch(head.Token));
        }
    }

    private void Dispatch(long token)
    {
        HudKind kind;

        lock (_gate)
        {
            if (_disposed)
                return;

            var now = _utcNow();
            var head = EarliestLocked();
            if (head is null || head.Token != token)
                return;

            // 队首还没到派发时刻（被后来的同类事件推后了）→ 重新武装
            if (head.Deadline > now)
            {
                _armed = head;
                head.Timer = _schedule(head.Deadline - now, () => Dispatch(head.Token));
                return;
            }

            kind = head.Kind;
            _queue.Remove(head);
            _armed = null;

            // 记录 AC 派发时刻，供"副作用抑制"判断后续低电量模式事件
            if (IsAc(kind))
                _lastAcDispatchUtc = now;

            // 队列里还有同优先级或更低优先级的项 —— 它们的窗口是"自上次到达起算"，
            // 但既然已经为本次物理动作播过了，剩下的同类副作用一并丢弃。
            var discarded = _queue.Count;
            _queue.Clear();

            if (discarded > 0)
                _log($"HudTrigger: 派发 {kind}，丢弃剩余 {discarded} 个待播项");
            else
                _log($"HudTrigger: 派发 {kind}");
        }

        try
        {
            _dispatch(kind);
        }
        catch
        {
            // 播放失败不应影响后续事件
        }
    }

    private Entry? EarliestLocked()
    {
        Entry? best = null;
        foreach (var e in _queue)
        {
            if (best is null || e.Deadline < best.Deadline ||
                (e.Deadline == best.Deadline && e.Token < best.Token))
            {
                best = e;
            }
        }

        return best;
    }

    private static int Priority(HudKind kind) => IsAc(kind) ? 1 : 0;

    private static bool IsAc(HudKind kind)
        => kind is HudKind.AcConnected or HudKind.AcDisconnected;

    private static IDisposable DefaultSchedule(TimeSpan delay, Action callback)
    {
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        return new Timer(_ => callback(), null, delay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var e in _queue)
                e.Timer.Dispose();
            _queue.Clear();
            _armed = null;
        }
    }

    private sealed class Entry(HudKind kind, DateTime deadline, long token)
    {
        public HudKind Kind { get; } = kind;
        public DateTime Deadline { get; set; } = deadline;
        public long Token { get; } = token;

        /// <summary>当前武装在该项上的定时器（可能为 <see cref="System.Threading.Timer"/> 或测试假实现）。</summary>
        public IDisposable Timer { get; set; } = NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
