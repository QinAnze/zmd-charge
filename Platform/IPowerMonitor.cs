using System;

namespace EndfieldCharge.Services;

/// <summary>
/// 电源来源与省电/低电量模式监听 + 电池采样。
///
/// 事件在后台线程上触发，订阅方需自行切回 UI 线程（现有调用方已用
/// <c>Dispatcher.UIThread.Post</c> 包装）。
/// </summary>
public interface IPowerMonitor : IDisposable
{
    /// <summary>电源来源变化。参数为当前是否交流电供电（true=已插电）。</summary>
    event EventHandler<bool>? PowerSourceChanged;

    /// <summary>省电 / 低电量模式开关变化。参数为是否开启（true=已开启）。</summary>
    event EventHandler<bool>? PowerSavingChanged;

    /// <summary>当前电池快照；无电池或读取失败返回 null。</summary>
    BatterySnapshot? GetSnapshot();

    /// <summary>只取 AC 是否在线（不依赖电池存在）。返回 false 表示读取失败。</summary>
    bool TryGetAcOnline(out bool acOnline);

    /// <summary>启动监听。重复调用应幂等。</summary>
    void Start();

    /// <summary>停止监听。可重复调用。</summary>
    void Stop();
}
