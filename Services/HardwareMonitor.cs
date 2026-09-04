using System;
using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace EndfieldCharge.Services;

/// <summary>一次硬件传感器采样（全部为真实传感器读数，读不到即 null）。</summary>
public sealed record HardwareSample(
    double? CpuLoad,
    double? MemoryLoad,
    double? GpuLoad,
    double? CpuWatts,
    double? GpuWatts)
{
    public static readonly HardwareSample Empty = new(null, null, null, null, null);

    /// <summary>是否读到了至少一路真实功率。</summary>
    public bool HasPower => CpuWatts.HasValue || GpuWatts.HasValue;
}

/// <summary>
/// LibreHardwareMonitorLib 封装——AIDA64 / HWiNFO / GPU-Z 同源的标准开源方案：
///   <list type="bullet">
///     <item>CPU / 内存 / GPU 占用率：库内驱动与厂商 API 直读，无需性能计数器技巧；</item>
///     <item>CPU 封装功率：Intel RAPL / AMD 遥测 MSR（内核驱动 WinRing0，需管理员）；</item>
///     <item>GPU 功率：NVIDIA NVML / AMD ADL（显卡驱动自带，普通权限可用）。</item>
///   </list>
/// 只暴露本项目需要的量；任何传感器读不到就返回 null，由上层决定展示，绝不编造数值。
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsMemoryEnabled = true,
        IsGpuEnabled = true,
        // 硬盘速度不走这里：用 PhysicalDisk 性能计数器可直接按盘符映射到 C 盘所在物理盘
    };

    private readonly object _gate = new();
    private Thread? _openThread;
    private volatile bool _opened;
    private int _consecutiveFailures;

    /// <summary>
    /// 后台打开传感器树。首次枚举硬件与加载驱动较慢（可能数秒），
    /// 放后台线程避免阻塞启动与首次弹窗；就绪前 <see cref="Sample"/> 返回空样本。
    /// </summary>
    public void OpenInBackground()
    {
        lock (_gate)
        {
            if (_openThread is not null)
                return;

            _openThread = new Thread(() =>
            {
                try
                {
                    lock (_gate)
                    {
                        _computer.Open();
                        _opened = true;
                    }
                }
                catch
                {
                    // 打开失败（权限 / 平台不支持）：保持未就绪，Sample 永远返回空样本
                    _opened = false;
                }
            })
            {
                IsBackground = true,
                Name = "lhm-open",
            };
            _openThread.Start();
        }
    }

    /// <summary>传感器树是否已就绪。</summary>
    public bool IsReady => _opened;

    /// <summary>
    /// 采样一次：刷新全部硬件后读取 CPU / 内存 / GPU 占用率与 CPU / GPU 功率。
    /// 未就绪或连续失败时返回 <see cref="HardwareSample.Empty"/>。
    /// </summary>
    public HardwareSample Sample()
    {
        if (!_opened)
            return HardwareSample.Empty;

        lock (_gate)
        {
            if (!_opened)
                return HardwareSample.Empty;

            try
            {
                foreach (IHardware hw in _computer.Hardware)
                {
                    hw.Update();
                    foreach (IHardware sub in hw.SubHardware)
                        sub.Update();
                }

                _consecutiveFailures = 0;
                return Read();
            }
            catch
            {
                // 个别传感器抛异常不应拖垮整个采样；连续失败则放弃本次
                if (++_consecutiveFailures >= 5)
                    return HardwareSample.Empty;

                return HardwareSample.Empty;
            }
        }
    }

    private HardwareSample Read()
    {
        double? cpuLoad = null, memLoad = null, gpuLoad = null, cpuWatts = null, gpuWatts = null;

        foreach (IHardware hw in _computer.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    cpuLoad ??= PickLoad(hw, "CPU Total", "Total");
                    cpuWatts ??= PickPower(hw, "package", "ppt", "cpu");
                    break;

                case HardwareType.Memory:
                    memLoad ??= PickLoad(hw, "Memory");
                    break;

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    gpuLoad ??= PickLoad(hw, "GPU Core", "GPU Total", "D3D 3D", "3D");
                    gpuWatts ??= PickPower(hw, "power", "package", "ppt");
                    break;
            }
        }

        return new HardwareSample(cpuLoad, memLoad, gpuLoad, cpuWatts, gpuWatts);
    }

    /// <summary>
    /// 按名称关键词挑占用率传感器。各平台命名不同（NVIDIA "GPU Core"、
    /// Intel "D3D 3D"、AMD "GPU Core"/"GPU Total"），按给定顺序取第一个命中。
    /// </summary>
    private static double? PickLoad(IHardware hw, params string[] nameHints)
    {
        foreach (string hint in nameHints)
        {
            foreach (ISensor s in hw.Sensors)
            {
                if (s.SensorType == SensorType.Load &&
                    s.Value.HasValue &&
                    s.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    return (double)s.Value.Value;
                }
            }
        }
        return null;
    }

    /// <summary>按名称关键词挑功率传感器（瓦）。</summary>
    private static double? PickPower(IHardware hw, params string[] nameHints)
    {
        foreach (string hint in nameHints)
        {
            foreach (ISensor s in hw.Sensors)
            {
                if (s.SensorType == SensorType.Power &&
                    s.Value.HasValue &&
                    s.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    return (double)s.Value.Value;
                }
            }
        }
        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _computer.Close();
            }
            catch
            {
                // 收尾失败不影响退出
            }
            _opened = false;
        }
    }
}
