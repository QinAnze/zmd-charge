using System.Runtime.Versioning;
using System.Threading.Tasks;
using EndfieldCharge.Contract;

namespace EndfieldCharge.Services;

/// <summary>
/// 内置适配器：读取本机电池（powrprof 主路径 + WMI 兜底，见 <see cref="BatteryService"/>）。
/// 排在适配器列表第一位，HUD 主药丸（完整三态动画 + 插拔电事件）展示的就是它；
/// 本机没有电池（台式机）时由 AdapterManager 在后台探测后摘除，UPS 亦按本机电池处理。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BuiltInBatteryAdapter : IPowerAdapter
{
    public const string AdapterId = "builtin-battery";

    public string Id => AdapterId;

    public string Name => Localization.BuiltInAdapterName;

    /// <summary>WMI 兜底可能较慢，挪到后台线程，避免卡 UI。</summary>
    public Task<BatterySnapshot?> GetSnapshotAsync() => Task.Run(() => BatteryService.GetSnapshot());
}
