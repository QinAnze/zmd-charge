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
/// 适配器注册表：内置适配器永远排第一（HUD 主药丸），其后是从 adapters\ 目录
/// 加载的原生插件适配器（每个插件各自一粒药丸，显示在主药丸下方）。
///
/// 插件是 C 语言风格 ABI 的原生 DLL（导出 ec_* C 函数；C/C++/Rust/Go/Zig 等
/// 语言都能写，契约见 Contract/ec_adapter.h），放在主程序目录的 adapters\ 下，
/// 重启后生效。单个插件加载或读取失败只记日志，不影响其余适配器。
/// </summary>
public sealed class AdapterManager
{
    /// <summary>单次读取超时：插件可能阻塞（蓝牙 / ADB 等慢源），超时即放弃本次、回退上次成功值，不拖累 HUD。</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(800);

    private readonly List<IPowerAdapter> _adapters = [];

    /// <summary>插件在后台线程加载，与 HUD 读取并发；两处都经此锁串行化对 _adapters 的访问。</summary>
    private readonly object _loadLock = new();

    /// <summary>每个适配器最近一次成功读取的快照（按 Id 索引），超时时回退显示。</summary>
    private readonly ConcurrentDictionary<string, BatterySnapshot?> _lastGood = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IPowerAdapter> Adapters
    {
        get { lock (_loadLock) return [.. _adapters]; }
    }

    public AdapterManager()
    {
        // 内置适配器固定第一位：主药丸（完整三态动画 + 插拔电事件）始终展示它。
        // 只装内置的，插件留给 LoadPluginsAsync 后台加载——慢插件不能拖住托盘出现。
        _adapters.Add(new BuiltInBatteryAdapter());
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
    });

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
