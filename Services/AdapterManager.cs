using System;
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
    private readonly List<IPowerAdapter> _adapters = [];

    public IReadOnlyList<IPowerAdapter> Adapters => _adapters;

    public AdapterManager()
    {
        // 内置适配器固定第一位：主药丸（完整三态动画 + 插拔电事件）始终展示它
        _adapters.Add(new BuiltInBatteryAdapter());
        LoadPlugins();
    }

    /// <summary>并发读取所有适配器；单个失败记日志并返回 null 快照，不影响其余适配器。</summary>
    public async Task<IReadOnlyList<AdapterReading>> ReadAllAsync()
        => await Task.WhenAll(_adapters.Select(ReadSafeAsync));

    private static async Task<AdapterReading> ReadSafeAsync(IPowerAdapter adapter)
    {
        try
        {
            var snap = await adapter.GetSnapshotAsync();
            return new AdapterReading(adapter, snap);
        }
        catch (Exception ex)
        {
            Logger.Error($"适配器 {adapter.Id} 读取失败：{ex.Message}");
            return new AdapterReading(adapter, null);
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
