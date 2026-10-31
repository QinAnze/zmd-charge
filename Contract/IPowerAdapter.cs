namespace EndfieldCharge.Contract;

/// <summary>
/// 电量适配器：HUD 电量数据源的内部统一抽象。
/// 内置实现读取本机电池；外部插件走 C 语言风格原生 ABI（导出 ec_* C 函数，
/// 契约见 Contract/ec_adapter.h），由 NativeAdapter 包装成本接口接入。
/// 每个适配器在 HUD 上各自显示一粒药丸（内置适配器为主药丸，插件药丸排在下方）。
/// </summary>
public interface IPowerAdapter
{
    /// <summary>稳定唯一 ID。内置适配器为 "builtin-battery"；ID 重复的会被忽略。</summary>
    string Id { get; }

    /// <summary>显示名（HUD 药丸上的名称）。支持本地化时请在每次读取时返回新值。</summary>
    string Name { get; }

    /// <summary>读取一次电量快照。无数据 / 失败返回 null。耗时读取请在内部移到后台线程。</summary>
    Task<BatterySnapshot?> GetSnapshotAsync();
}
