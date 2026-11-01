using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EndfieldCharge.Contract;

namespace EndfieldCharge.Services;

/// <summary>一次适配器读取结果：适配器 + 其快照（读取失败为 null）。</summary>
public sealed record AdapterReading(IPowerAdapter Adapter, BatterySnapshot? Snapshot);

/// <summary>预览 / 演示用的一次性适配器：固定返回构造时给定的快照。</summary>
public sealed class StaticAdapter(string id, string name, BatterySnapshot? snapshot = null) : IPowerAdapter
{
    public string Id => id;
    public string Name => name;
    public Task<BatterySnapshot?> GetSnapshotAsync() => Task.FromResult(snapshot);
}

/// <summary>
/// 适配器注册表：内置适配器排第一（HUD 主药丸），其后是从 adapters\ 目录
/// 加载的原生插件适配器（每个插件各自一粒药丸，显示在主药丸下方）。
/// 后台探测到本机没有内置电池（台式机）时，会摘掉内置适配器，让第一个插件
/// 升为主药丸；而台式机上 Windows 识别到的"电池"通常就是 UPS，会照常按
/// 内置电池处理（UPS 即主药丸）。
///
/// 插件是 C 语言风格 ABI 的原生 DLL（导出 ec_* C 函数；C/C++/Rust/Go/Zig 等
/// 语言都能写，契约见 Contract/ec_adapter.h），放在主程序目录的 adapters\ 下，
/// 重启后生效。单个插件加载或读取失败只记日志，不影响其余适配器。
///
/// 插件状态变化的主动弹出是双通道的（内置适配器不走，它有自己的系统电源事件）：
///  1. 事件驱动——插件导出 ec_set_change_callback 即可主动上报（即时弹出）；
///  2. 轮询兜底——每 3 秒后台读一次插件，充电/外接状态翻转同样触发，不依赖插件配合。
/// </summary>
public sealed class AdapterManager
{
    /// <summary>单次读取超时：插件可能阻塞（蓝牙 / ADB 等慢源），超时即放弃本次、回退上次成功值，不拖累 HUD。</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(800);

    /// <summary>插件轮询间隔（秒）。</summary>
    private const int PollIntervalSeconds = 3;

    /// <summary>同一适配器两次"状态变化弹出"的最小间隔（防抖，防止状态抖动刷屏）。</summary>
    private static readonly TimeSpan ChangeCooldown = TimeSpan.FromSeconds(5);

    private readonly List<IPowerAdapter> _adapters = [];

    /// <summary>插件在后台线程加载，与 HUD 读取并发；对 _adapters / 轮询基线 / 防抖表的访问都经此锁串行化。</summary>
    private readonly object _loadLock = new();

    /// <summary>每个适配器最近一次成功读取的快照（按 Id 索引），超时时回退显示。</summary>
    private readonly ConcurrentDictionary<string, BatterySnapshot?> _lastGood = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上次轮询到的插件状态基线。</summary>
    private readonly Dictionary<string, (bool AcOnline, bool Charging)> _lastPollState = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>各适配器最近一次触发弹出的时间（UTC），用于防抖。</summary>
    private readonly Dictionary<string, DateTime> _lastRaisedAt = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IPowerAdapter> Adapters
    {
        get { lock (_loadLock) return [.. _adapters]; }
    }

    /// <summary>插件适配器状态变化（事件回调或轮询发现）时触发；可能在任意线程。</summary>
    public event Action<IPowerAdapter>? AdapterStateChanged;

    public AdapterManager()
    {
        // 内置适配器固定第一位：主药丸（完整三态动画 + 插拔电事件）始终展示它。
        // 只装内置的，插件留给 LoadPluginsAsync 后台加载——慢插件不能拖住托盘出现。
        _adapters.Add(new BuiltInBatteryAdapter());

        // 轮询兜底通道：插件不注册回调也能靠这个发现状态变化
        _ = Task.Run(PollLoopAsync);
    }

    /// <summary>后台加载 adapters\ 目录下的插件 DLL。立即返回，加载完成后插件药丸自动出现。</summary>
    public Task LoadPluginsAsync() => Task.Run(() =>
    {
        try
        {
            LoadPlugins();
        }
        catch (Exception ex)
        {
            Logger.Error($"扫描适配器插件目录失败：{ex.Message}");
        }

        ProbeBuiltInBattery();
    });

    /// <summary>
    /// 探测本机是否有内置电池（含 Windows 识别为电池的 UPS）：没有就摘掉内置适配器，
    /// 让第一个插件升为主药丸，避免台式机上常驻一粒永远 "--" 的空主药丸。
    /// </summary>
    private void ProbeBuiltInBattery()
    {
        try
        {
            // BatteryService 内部就是 powrprof + WMI 兜底：台式机上 Win32_Battery
            // 能查到的"电池"就是 UPS，有它就该按内置电池处理，保留为主药丸
            if (BatteryService.GetSnapshot() is not null)
                return;

            lock (_loadLock)
            {
                var builtin = _adapters.FirstOrDefault(a => a.Id == BuiltInBatteryAdapter.AdapterId);
                if (builtin is null)
                    return;
                _adapters.Remove(builtin);
            }

            _lastGood.TryRemove(BuiltInBatteryAdapter.AdapterId, out _);
            Logger.Info("本机没有内置电池，已隐藏内置电池适配器，插件适配器升为主药丸");
        }
        catch (Exception ex)
        {
            Logger.Error($"探测内置电池失败：{ex.Message}");
        }
    }

    /// <summary>并发读取所有适配器；单个失败记日志并返回 null 快照，不影响其余适配器。</summary>
    public async Task<IReadOnlyList<AdapterReading>> ReadAllAsync()
    {
        // 后台可能正在加载插件，取快照后再并发读取，避免枚举中被修改
        IPowerAdapter[] adapters;
        lock (_loadLock)
            adapters = [.. _adapters];

        return await Task.WhenAll(adapters.Select(ReadSafeAsync));
    }

    private async Task<AdapterReading> ReadSafeAsync(IPowerAdapter adapter)
    {
        try
        {
            // WaitAsync 只是放弃等待：阻塞中的原生调用无法取消，任其在后台线程自生自灭
            var snap = await adapter.GetSnapshotAsync().WaitAsync(ReadTimeout);
            _lastGood[adapter.Id] = snap;
            return new AdapterReading(adapter, snap);
        }
        catch (TimeoutException)
        {
            Logger.Error($"适配器 {adapter.Id} 读取超时（>{ReadTimeout.TotalMilliseconds:0}ms），回退上次读数");
            return new AdapterReading(adapter, _lastGood.GetValueOrDefault(adapter.Id));
        }
        catch (Exception ex)
        {
            Logger.Error($"适配器 {adapter.Id} 读取失败：{ex.Message}");
            return new AdapterReading(adapter, _lastGood.GetValueOrDefault(adapter.Id));
        }
    }

    // ---------------- 状态变化：事件 + 轮询双通道 ----------------

    /// <summary>插件通过 ec_set_change_callback 主动上报（可能来自插件的任意线程）。</summary>
    private void OnPluginNotified(NativeAdapter adapter)
    {
        RaiseChanged(adapter);

        // 立即刷新该插件的轮询基线，避免下一轮轮询把同一次变化再弹一遍
        _ = Task.Run(async () =>
        {
            var reading = await ReadSafeAsync(adapter);
            if (reading.Snapshot is { HasBattery: true })
            {
                lock (_loadLock)
                    _lastPollState[adapter.Id] = (reading.Snapshot.AcOnline, reading.Snapshot.Charging);
            }
        });
    }

    private async Task PollLoopAsync()
    {
        using var timer = new System.Threading.PeriodicTimer(TimeSpan.FromSeconds(PollIntervalSeconds));
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                await PollOnceAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"适配器轮询失败：{ex.Message}");
            }
        }
    }

    /// <summary>读一遍插件适配器（内置的不算，它有自己的系统电源事件），对比上轮的充电/外接状态，翻转即触发；百分比变化不触发，避免刷屏。</summary>
    private async Task PollOnceAsync()
    {
        IPowerAdapter[] adapters;
        lock (_loadLock)
            adapters = [.. _adapters];

        var plugins = adapters
            .Where(a => !string.Equals(a.Id, BuiltInBatteryAdapter.AdapterId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (plugins.Count == 0)
            return;

        var readings = await Task.WhenAll(plugins.Select(ReadSafeAsync));
        var flips = new List<IPowerAdapter>();

        lock (_loadLock)
        {
            foreach (var r in readings)
            {
                // 无数据/读取失败不参与比较，也不清旧状态（设备短暂休眠不应误报"变化"）
                if (r.Snapshot is not { HasBattery: true })
                    continue;

                var state = (r.Snapshot.AcOnline, r.Snapshot.Charging);
                if (_lastPollState.TryGetValue(r.Adapter.Id, out var prev) && prev != state)
                    flips.Add(r.Adapter);
                _lastPollState[r.Adapter.Id] = state;
            }
        }

        foreach (var adapter in flips)
            RaiseChanged(adapter);
    }

    private void RaiseChanged(IPowerAdapter adapter)
    {
        var now = DateTime.UtcNow;
        lock (_loadLock)
        {
            if (_lastRaisedAt.TryGetValue(adapter.Id, out var t) && now - t < ChangeCooldown)
                return;
            _lastRaisedAt[adapter.Id] = now;
        }

        try
        {
            AdapterStateChanged?.Invoke(adapter);
        }
        catch (Exception ex)
        {
            Logger.Error($"适配器 {adapter.Id} 状态变化通知处理失败：{ex.Message}");
        }
    }

    // ---------------- 插件加载 ----------------

    private void LoadPlugins()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "adapters");
        if (!Directory.Exists(dir))
            return;

        foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
        {
            try
            {
                if (!NativeAdapter.TryCreate(dll, out var adapter) || adapter is null)
                {
                    Logger.Info($"{Path.GetFileName(dll)} 未导出 ec_get_snapshot，不是适配器插件，已跳过");
                    continue;
                }

                if (_adapters.Any(a => string.Equals(a.Id, adapter.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    Logger.Info($"适配器插件 {Path.GetFileName(dll)}：ID \"{adapter.Id}\" 与已有适配器重复，已忽略");
                    continue;
                }

                // 事件驱动通道：插件导出了回调注册接口，NativeAdapter 已在加载时接上
                adapter.StateChanged += () => OnPluginNotified(adapter);
                lock (_loadLock)
                    _adapters.Add(adapter);
                Logger.Info($"已加载原生适配器插件：{adapter.Id}（{Path.GetFileName(dll)}）");
            }
            catch (Exception ex)
            {
                Logger.Error($"加载适配器插件 {Path.GetFileName(dll)} 失败：{ex.Message}");
            }
        }
    }
}
