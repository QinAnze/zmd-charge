using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EndfieldCharge.Contract;

namespace EndfieldCharge.Services;

/// <summary>原生插件导出函数的 C# 委托签名（cdecl，见 Contract/ec_adapter.h）。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr EcAdapterString();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int EcGetSnapshotNative(ref EcSnapshotNative snapshot);

/// <summary>插件状态变化回调签名（插件在任意线程调用，见 ec_set_change_callback）。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void EcChangeCallback(IntPtr user);

/// <summary>ec_set_change_callback(on_change, user) 的签名。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void EcSetChangeCallback(IntPtr onChange, IntPtr user);

/// <summary>
/// ec_snapshot 的 C# 侧镜像：字段顺序/类型必须与 Contract/ec_adapter.h 完全一致
/// （自然对齐，x64 下 Marshal.SizeOf == 64）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EcSnapshotNative
{
    public uint StructSize;
    public int HasBattery;
    public int Percent;
    public int AcOnline;
    public int Charging;
    public double RemainingWh;
    public double FullWh;
    public double RateWatts;
    public double DesignCapacityWh;
    public long RemainingSeconds;
}

/// <summary>
/// C 语言风格原生 DLL 适配器：插件用任何能导出 C 函数的语言编写
/// （C/C++/Rust/Go/Zig/Nim…，ABI 见 Contract/ec_adapter.h），
/// 放进主程序 adapters\ 目录即被本类包装接入，各自显示一粒 HUD 药丸。
/// </summary>
internal sealed class NativeAdapter : IPowerAdapter
{
    /// <summary>主程序认识的结构体大小（= 传给插件的缓冲区容量）。</summary>
    private const int ExpectedStructSize = 64;

    private readonly EcGetSnapshotNative _getSnapshot;
    private readonly string _dllName;
    private readonly EcAdapterString? _getName;

    /// <summary>插件主动上报状态变化时触发（来自插件的任意线程，订阅方自行切线程）。</summary>
    public event Action? StateChanged;

    // 原生回调 thunk：委托实例必须被强引用，否则其函数指针会随 GC 失效
    private readonly EcChangeCallback _changeCallback;

    // 传给插件的 user 指针（指回本对象；回调定位靠 per-adapter thunk，句柄兼作调试锚点，随进程存活）
    private readonly GCHandle _selfHandle;

    public string Id { get; }

    public string Name => ReadString(_getName) ?? _dllName;

    private NativeAdapter(string dllName, string id, EcAdapterString? getName, EcGetSnapshotNative getSnapshot)
    {
        _dllName = dllName;
        _getName = getName;
        _getSnapshot = getSnapshot;
        Id = id;
        _changeCallback = _ => StateChanged?.Invoke();
        _selfHandle = GCHandle.Alloc(this);
    }

    /// <summary>
    /// 尝试把原生 DLL 加载为适配器：必须导出 ec_get_snapshot，id/name 导出可选。
    /// 缺少必需导出返回 false（比如恰好放在 adapters\ 里的其他 DLL，不算错误）。
    /// </summary>
    public static bool TryCreate(string dllPath, out NativeAdapter? adapter)
    {
        adapter = null;

        var size = Marshal.SizeOf<EcSnapshotNative>();
        if (size != ExpectedStructSize)
        {
            Logger.Error($"ec_snapshot 结构体布局与预期不符（{size} != {ExpectedStructSize}），无法加载原生适配器插件");
            return false;
        }

        var library = NativeLibrary.Load(dllPath);
        var dllName = Path.GetFileNameWithoutExtension(dllPath);

        if (!NativeLibrary.TryGetExport(library, "ec_get_snapshot", out var snapAddr))
        {
            NativeLibrary.Free(library);
            return false;
        }

        var getId = ResolveStringExport(library, "ec_adapter_id");
        var getName = ResolveStringExport(library, "ec_adapter_name");
        var getSnapshot = Marshal.GetDelegateForFunctionPointer<EcGetSnapshotNative>(snapAddr);

        var id = ReadString(getId) ?? dllName;
        adapter = new NativeAdapter(dllName, id, getName, getSnapshot);

        // 事件驱动通道：插件导出了回调注册接口就接上（轮询兜底在 AdapterManager）
        if (NativeLibrary.TryGetExport(library, "ec_set_change_callback", out var setCbAddr))
        {
            var setCallback = Marshal.GetDelegateForFunctionPointer<EcSetChangeCallback>(setCbAddr);
            setCallback(
                Marshal.GetFunctionPointerForDelegate(adapter._changeCallback),
                GCHandle.ToIntPtr(adapter._selfHandle));
        }

        return true;
    }

    private static EcAdapterString? ResolveStringExport(IntPtr library, string name)
        => NativeLibrary.TryGetExport(library, name, out var addr)
            ? Marshal.GetDelegateForFunctionPointer<EcAdapterString>(addr)
            : null;

    /// <summary>阻塞/耗时的原生调用放在后台线程；插件异常只影响本次读数（上层已捕获）。</summary>
    public Task<BatterySnapshot?> GetSnapshotAsync()
        => Task.Run(() =>
        {
            // struct_size 告知插件缓冲区容量（调用方设置，插件校验后填充）
            var s = new EcSnapshotNative { StructSize = ExpectedStructSize };
            int rc = _getSnapshot(ref s);

            if (rc != 0 || s.HasBattery == 0)
                return null;

            return new BatterySnapshot(
                RemainingWh: s.RemainingWh,
                FullWh: s.FullWh,
                Percent: s.Percent,
                AcOnline: s.AcOnline != 0,
                Charging: s.Charging != 0)
            {
                RateWatts = double.IsNaN(s.RateWatts) ? null : s.RateWatts,
                DesignCapacityWh = double.IsNaN(s.DesignCapacityWh) ? null : s.DesignCapacityWh,
                EstimatedRemaining = s.RemainingSeconds < 0 ? null : TimeSpan.FromSeconds(s.RemainingSeconds),
            };
        });

    private static string? ReadString(EcAdapterString? read)
    {
        if (read is null)
            return null;

        try
        {
            var p = read();
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
        }
        catch
        {
            return null;
        }
    }
}
