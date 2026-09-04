using System;
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
    double? Watts)
{
    public static readonly SystemLoadSnapshot Empty = new(0d, 0d, null, 0d, null);

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
    private readonly object _gate = new();
    private SystemLoadSnapshot _current = SystemLoadSnapshot.Empty;
    private Timer? _timer;
    private bool _disposed;

    public SystemLoadService()
    {
        // 后台初始化传感器树（首次加载驱动较慢），不阻塞启动与首次弹窗
        _hw.OpenInBackground();

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

        var snap = new SystemLoadSnapshot(
            CpuPercent: cpu,
            MemoryPercent: mem,
            GpuPercent: gpu,
            LoadPercent: ComputeLoad(cpu, mem, gpu),
            Watts: watts);

        lock (_gate)
        {
            _current = snap;
        }
        return snap;
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
