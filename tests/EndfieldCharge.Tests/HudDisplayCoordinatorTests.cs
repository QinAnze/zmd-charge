using System;
using System.Collections.Generic;
using System.Threading;

using EndfieldCharge.Services;

using Xunit;

namespace EndfieldCharge.Tests;

/// <summary>
/// 可手动推进的假时钟 + 假定时器，让 <see cref="HudDisplayCoordinator"/> 的时间行为确定可测。
/// </summary>
internal sealed class FakeRuntime
{
    private readonly List<(DateTime Due, Action Callback)> _timers = new();
    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public DateTime Now => _now;

    /// <summary>推进时间并按到期顺序执行定时器回调。</summary>
    public void Advance(TimeSpan delta)
    {
        var target = _now + delta;

        while (true)
        {
            var index = -1;
            for (int i = 0; i < _timers.Count; i++)
            {
                if (_timers[i].Due <= target && (index < 0 || _timers[i].Due < _timers[index].Due))
                    index = i;
            }

            if (index < 0)
                break;

            var (due, callback) = _timers[index];
            _timers.RemoveAt(index);
            _now = due;
            callback();
        }

        _now = target;
    }

    /// <summary>与 <c>Timer</c> 同形的工厂，交给被测类注入。</summary>
    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        var entry = (_now + delay, callback);
        _timers.Add(entry);

        return new Cancellation(this, entry);
    }

    private sealed class Cancellation : IDisposable
    {
        private readonly FakeRuntime _owner;
        private readonly (DateTime Due, Action Callback) _entry;

        public Cancellation(FakeRuntime owner, (DateTime, Action) entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public void Dispose() => _owner._timers.Remove(_entry);
    }
}

/// <summary>
/// HUD 事件合并的回归测试。
///
/// 修复的真实缺陷：macOS 插拔电源时会**紧接着自动切换低电量模式**
/// （实测滞后 402 / 674 / 674 / 2128 ms），使 <c>PowerSourceChanged</c> 与
/// <c>PowerSavingChanged</c> 连弹两次 HUD；第二次进入 <c>HudWindow.ShowAndPlayAsync</c>
/// 时执行 <c>_cts.Cancel()</c> 把第一次动画掐断，
/// 表现为"先闪一下简化胶囊，再（或再也不）播完整三态"。
/// </summary>
public sealed class HudDisplayCoordinatorTests
{
    private static (HudDisplayCoordinator Coordinator, FakeRuntime Runtime, List<string> Shown) Build(
        bool suppressAcCorrelatedSaver = true)
    {
        var runtime = new FakeRuntime();
        var shown = new List<string>();

        var coordinator = new HudDisplayCoordinator(
            dispatch: kind => shown.Add(kind.ToString()),
            suppressAcCorrelatedSaver: suppressAcCorrelatedSaver,
            schedule: runtime.Schedule,
            utcNow: () => runtime.Now,
            log: _ => { });

        return (coordinator, runtime, shown);
    }

    /// <summary>回归主用例：插电 + 402ms 后 macOS 自动关闭低电量模式。</summary>
    [Fact]
    public void PlugIn_WithLaggedSaverOff_PlaysOnlyFullSequence()
    {
        var (c, rt, shown) = Build();

        c.OnAcChanged(acOnline: true);
        rt.Advance(TimeSpan.FromMilliseconds(402));
        c.OnSaverChanged(enabled: false);

        rt.Advance(TimeSpan.FromSeconds(5));

        // 关键断言：只播一次，且是完整三态（AcConnected）—— 不再先闪简化胶囊
        Assert.Equal(new[] { "AcConnected" }, shown);
    }

    /// <summary>回归主用例：拔电 + 674ms 后 macOS 自动开启低电量模式。</summary>
    [Fact]
    public void Unplug_WithLaggedSaverOn_PlaysOnlySimpleCapsule()
    {
        var (c, rt, shown) = Build();

        c.OnAcChanged(acOnline: false);
        rt.Advance(TimeSpan.FromMilliseconds(674));
        c.OnSaverChanged(enabled: true);

        rt.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "AcDisconnected" }, shown);
    }

    /// <summary>Saver 先到、AC 紧随其后（实测 23:54:47 的时序：saver 领先 AC 406ms）。</summary>
    [Fact]
    public void SaverArrivesBeforeAc_AcWins()
    {
        var (c, rt, shown) = Build();

        c.OnSaverChanged(enabled: true);
        rt.Advance(TimeSpan.FromMilliseconds(406));
        c.OnAcChanged(acOnline: false);

        rt.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "AcDisconnected" }, shown);
    }

    /// <summary>超过相关窗（4s）的 saver 事件不再被当作副作用，必须播 —— 否则用户手动开关会静默失效。</summary>
    [Fact]
    public void SaverFarAfterAc_StillPlays()
    {
        var (c, rt, shown) = Build();

        c.OnAcChanged(acOnline: false);
        rt.Advance(TimeSpan.FromSeconds(1));

        // 超过 4s 相关窗
        rt.Advance(TimeSpan.FromSeconds(5));
        c.OnSaverChanged(enabled: true);
        rt.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(new[] { "AcDisconnected", "SaverOn" }, shown);
    }

    /// <summary>纯手动切换低电量模式（无 AC 事件）必须照常播放。</summary>
    [Theory]
    [InlineData(true, "SaverOn")]
    [InlineData(false, "SaverOff")]
    public void ManualSaverToggle_Plays(bool enabled, string expected)
    {
        var (c, rt, shown) = Build();

        c.OnSaverChanged(enabled);
        rt.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(new[] { expected }, shown);
    }

    /// <summary>Windows 不抑制：省电模式不会被电源切换改写，手动开关必须提示。</summary>
    [Fact]
    public void Windows_DoesNotSuppressSaverAfterAc()
    {
        var (c, rt, shown) = Build(suppressAcCorrelatedSaver: false);

        c.OnAcChanged(acOnline: true);
        rt.Advance(TimeSpan.FromMilliseconds(600));
        c.OnSaverChanged(enabled: false);
        rt.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "AcConnected", "SaverOff" }, shown);
    }

    /// <summary>同类事件连发（固件抖动）只播一次。</summary>
    [Fact]
    public void RepeatedSameKind_CoalescesToSinglePlay()
    {
        var (c, rt, shown) = Build();

        c.OnAcChanged(acOnline: true);
        rt.Advance(TimeSpan.FromMilliseconds(60));
        c.OnAcChanged(acOnline: true);
        rt.Advance(TimeSpan.FromMilliseconds(60));
        c.OnAcChanged(acOnline: true);

        rt.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(new[] { "AcConnected" }, shown);
    }

    /// <summary>AC 播放延迟必须远小于"用户体验阈值"，且短于轮询兜底周期。</summary>
    [Fact]
    public void AcDisplayLatency_StaysWithinBudget()
    {
        var (c, rt, shown) = Build();

        c.OnAcChanged(acOnline: true);

        // 120ms 稳定窗之后应已派发（±20ms 容差）
        rt.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Empty(shown);

        rt.Advance(TimeSpan.FromMilliseconds(40));
        Assert.Single(shown);
    }

    /// <summary>Dispose 后不得再派发（避免退出进程时弹 HUD）。</summary>
    [Fact]
    public void AfterDispose_NoDispatch()
    {
        var (c, rt, shown) = Build();

        c.OnAcChanged(acOnline: true);
        c.Dispose();
        rt.Advance(TimeSpan.FromSeconds(5));

        Assert.Empty(shown);
    }
}
