using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Threading;

namespace EndfieldCharge.Services;

/// <summary>
/// 一次系统负载 / 功耗采样。占用率与功率全部来自真实硬件传感器，
/// 读不到的量是 null（不编数）。
/// </summary>
public sealed record SystemLoadSnapshot(
    double CpuPercent,
    double MemoryPercent,
    double? GpuPercent,
    double LoadPercent,
    double? Watts,
    double? DiskMBs,
    double NetUpKBs,
    double NetDownKBs)
{
    public static readonly SystemLoadSnapshot Empty = new(0d, 0d, null, 0d, null, null, 0d, 0d);

    /// <summary>GPU 占用率是否可用（不可用时权重已按 CPU : 内存 原比例重分配）。</summary>
    public bool GpuAvailable => GpuPercent.HasValue;

    /// <summary>是否读到了真实功率（CPU 封装 / GPU 任一路）。</summary>
    public bool HasWatts => Watts.HasValue;
}

/// <summary>
/// 系统负载采样服务。
///
/// 数据源：LibreHardwareMonitorLib 真实传感器（与 AIDA64 / HWiNFO 同源），
/// 不做任何估算建模——读不到的传感器就是 null，界面上显示占位。
///
/// 设备负载 = CPU × 0.45 + 内存 × 0.25 + GPU × 0.30：
///   CPU 权重最高（最灵敏、与体感最相关）；GPU 次之（渲染 / 游戏主要负载）；
///   内存最低（慢变量，占用高只代表装得多）。GPU 缺失时把 0.30 按
///   CPU : 内存 原比例重分配，保证负载值仍满量程。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemLoadService : IDisposable
{
    // ---------------- 负载权重 ----------------

    public const double WeightCpu = 0.45;
    public const double WeightMemory = 0.25;
    public const double WeightGpu = 0.30;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1.5);

    private readonly HardwareMonitor _hw = new();
    private readonly DiskProbe _disk = new();
    private readonly object _gate = new();
    private SystemLoadSnapshot _current = SystemLoadSnapshot.Empty;
    private Timer? _timer;
    private bool _disposed;

    public SystemLoadService()
    {
        // 后台初始化传感器树（首次加载驱动较慢），不阻塞启动与首次弹窗
        _hw.OpenInBackground();
        _disk.StartInBackground();

        _timer = new Timer(_ => Tick(), null, SampleInterval, SampleInterval);
    }

    /// <summary>最近一次采样结果（线程安全读取；未就绪时为全零空样本）。</summary>
    public SystemLoadSnapshot Current
    {
        get { lock (_gate) { return _current; } }
    }

    /// <summary>传感器层是否就绪（用于诊断输出）。</summary>
    public bool HardwareReady => _hw.IsReady;

    /// <summary>每次采样完成后触发（后台线程，订阅方需自行切回 UI 线程）。</summary>
    public event Action<SystemLoadSnapshot>? Sampled;

    /// <summary>立即采样一次（弹窗时用，避免等到下一个采样周期）。</summary>
    public SystemLoadSnapshot Sample()
    {
        if (!_hw.IsReady)
            return Current;

        HardwareSample s = _hw.Sample();
        if (!s.HasPower && s.CpuLoad is null && s.MemoryLoad is null && s.GpuLoad is null)
            return Current; // 传感器层还没出数，沿用上次结果

        double cpu = Math.Clamp(s.CpuLoad ?? 0d, 0d, 100d);
        double mem = Math.Clamp(s.MemoryLoad ?? 0d, 0d, 100d);
        double? gpu = s.GpuLoad is null ? null : Math.Clamp(s.GpuLoad.Value, 0d, 100d);

        // 真实功率 = 各路传感器之和；一路都没有就是 null（不估算）
        double? watts = s.HasPower ? (s.CpuWatts ?? 0d) + (s.GpuWatts ?? 0d) : null;

        (double upKBs, double downKBs) = ReadNetwork();

        var snap = new SystemLoadSnapshot(
            CpuPercent: cpu,
            MemoryPercent: mem,
            GpuPercent: gpu,
            LoadPercent: ComputeLoad(cpu, mem, gpu),
            Watts: watts,
            DiskMBs: _disk.ReadMBs(),
            NetUpKBs: upKBs,
            NetDownKBs: downKBs);

        lock (_gate)
        {
            _current = snap;
        }
        return snap;
    }

    // ---------------- 硬盘速度（C 盘所在物理盘，PhysicalDisk 性能计数器） ----------------

    /// <summary>
    /// 读 <b>C 盘所在物理盘</b>的读写速度（MB/s，读 + 写）。
    /// PhysicalDisk 性能计数器的实例名自带盘符映射（如 "0 C:" 或 "0 C: D:"），
    /// 这是 Windows 标准、且唯一可靠的"盘符 → 物理盘"对应方式。
    /// 首次枚举实例较慢，放后台；读不到（权限 / 实例缺失）返回 null。
    /// </summary>
    private sealed class DiskProbe
    {
        private const double BytesPerMB = 1024d * 1024d;

        private PerformanceCounter? _read;
        private PerformanceCounter? _write;
        private volatile bool _ready;
        private int _failures;

        public void StartInBackground()
        {
            Task.Run(() =>
            {
                try
                {
                    string? instance = new PerformanceCounterCategory("PhysicalDisk")
                        .GetInstanceNames()
                        .FirstOrDefault(n => n.Contains("C:", StringComparison.OrdinalIgnoreCase));

                    if (instance is null)
                        return;

                    _read = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instance, readOnly: true);
                    _write = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instance, readOnly: true);

                    // 速率型计数器首读返回 0，预热一次
                    _read.NextValue();
                    _write.NextValue();
                    _ready = true;
                }
                catch
                {
                    // 计数器不可用：面板上硬盘显示 "--"
                }
            });
        }

        public double? ReadMBs()
        {
            if (!_ready)
                return null;

            try
            {
                double mbs = (_read!.NextValue() + _write!.NextValue()) / BytesPerMB;
                _failures = 0;
                return mbs;
            }
            catch
            {
                if (++_failures >= 3)
                    _ready = false;   // 连续失败放弃，不再反复抛
                return null;
            }
        }
    }

    // ---------------- 网络速率（.NET 标准 NetworkInterface 统计差分） ----------------

    private long _prevBytesSent;
    private long _prevBytesReceived;
    private bool _hasPrevNet;
    private double _upKBs;
    private double _downKBs;

    /// <summary>
    /// 汇总所有在线物理网卡的收发字节数，与上次采样差分算速率（KB/s，含 IPv4+IPv6）。
    /// 指数平滑滤毛刺；首次采样只有基准，返回 0。
    /// </summary>
    private (double Up, double Down) ReadNetwork()
    {
        long sent = 0, recv = 0;
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                IPInterfaceStatistics st = ni.GetIPStatistics();
                sent += st.BytesSent;
                recv += st.BytesReceived;
            }
        }
        catch
        {
            return (_upKBs, _downKBs);   // 枚举失败沿用上次平滑值
        }

        if (!_hasPrevNet)
        {
            _prevBytesSent = sent;
            _prevBytesReceived = recv;
            _hasPrevNet = true;
            return (0d, 0d);
        }

        double seconds = SampleInterval.TotalSeconds;
        double up = Math.Max(0d, sent - _prevBytesSent) / 1024d / seconds;
        double down = Math.Max(0d, recv - _prevBytesReceived) / 1024d / seconds;
        _prevBytesSent = sent;
        _prevBytesReceived = recv;

        const double alpha = 0.5;
        _upKBs += alpha * (up - _upKBs);
        _downKBs += alpha * (down - _downKBs);
        return (_upKBs, _downKBs);
    }

    /// <summary>
    /// 加权负载。GPU 不可用时把它那一份按 CPU : 内存 的原比例重分配，
    /// 保证结果仍是满量程的 0-100，不会因为缺一项而整体偏低。
    /// </summary>
    public static double ComputeLoad(double cpu, double mem, double? gpu)
    {
        if (gpu is null)
        {
            double sum = WeightCpu + WeightMemory;
            return cpu * (WeightCpu / sum) + mem * (WeightMemory / sum);
        }
        return cpu * WeightCpu + mem * WeightMemory + gpu.Value * WeightGpu;
    }

    private void Tick()
    {
        if (_disposed)
            return;

        try
        {
            var snap = Sample();
            Sampled?.Invoke(snap);
        }
        catch
        {
            // 采样失败保持上次值，不拖垮定时器
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _timer?.Dispose();
        _timer = null;
        _hw.Dispose();
    }
}
