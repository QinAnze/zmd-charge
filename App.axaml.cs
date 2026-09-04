using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using EndfieldCharge.Models;
using EndfieldCharge.Services;
using EndfieldCharge.Views;

namespace EndfieldCharge;

public partial class App : Application
{
    private PowerWatcher? _watcher;
    private HudWindow? _hud;
    private TrayIcon? _tray;

    private AppSettings _settings = new();
    private SystemLoadService? _load;
    private ChargeModeService? _charge;

    /// <summary>是否按台式机处理（读不到电池）。决定显示功耗模式还是电量模式。</summary>
    private bool _isDesktop;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 没有常驻主窗口，退出必须由托盘菜单显式触发
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += OnDesktopExit;

            _settings = AppSettings.Load();
            _charge = new ChargeModeService(_settings);

            // 设备类型：WMI PCSystemType 标准判定（读不到时回退电池判定）
            _isDesktop = DeviceInfo.IsDesktop();

            // 调试用：强制按台式机渲染（在笔记本上检查功耗模式）
            if (HasCommandLineArg("--force-desktop"))
                _isDesktop = true;

            _load = new SystemLoadService();

            _hud = new HudWindow();

            SetupTrayIcon(desktop);
            StartPowerWatching();

            // 诊断：把一次采样结果追加到 %TEMP%\endfield-diag.txt。
            // 功耗模型是估算，需要用真实机器的数据校准 PowerModel 里的常量，
            // 所以留一个开关，配合任务管理器 / 功率计读数对比。
            if (HasCommandLineArg("--diag"))
                _ = Task.Run(WriteDiagnostics);

            // 常驻模式：开机即显示，之后不再自动收起（只有托盘退出 / 关机才消失）
            if (_settings.ResidentMode)
                Dispatcher.UIThread.Post(() => _ = EnterResidentAsync(), DispatcherPriority.Loaded);

            // 调试 / 自查入口：不用真的插拔电源也能看动画
            //   --preview         用本机真实数据播放一次（完整三态）
            //   --preview-unplug  播放一次简化版（只弹内容胶囊）
            //   --demo            用示例电池数据播放一次
            //   --force-desktop   强制按台式机（功耗模式）渲染，配合上面几个参数使用
            if (HasCommandLineArg("--demo"))
                _ = PreviewWithSampleDataAsync();
            else if (HasCommandLineArg("--preview-unplug"))
                _ = PreviewSimpleAsync();
            else if (HasCommandLineArg("--preview"))
                _ = TriggerHudAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static bool HasCommandLineArg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ---------------- 诊断（真实传感器读数自查） ----------------

    /// <summary>
    /// 诊断：把一次传感器采样 / 电池 / 充电器判定结果追加写入 <c>%TEMP%\endfield-diag.txt</c>。
    /// 用于核对 LibreHardwareMonitorLib 在本机的传感器覆盖情况
    /// （运行 <c>EndfieldCharge.exe --diag</c>；读 CPU 封装功率需管理员权限）。
    /// 只做只读采样，不改动常驻状态。
    /// </summary>
    private void WriteDiagnostics()
    {
        try
        {
            // 传感器树是后台初始化的，等它就绪再采样（最多 15 秒），否则打出的是空值
            for (int i = 0; i < 30 && _load is { HardwareReady: false }; i++)
                Thread.Sleep(500);

            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] EndfieldCharge 诊断");
            sb.AppendLine($"设备类型 : {(_isDesktop ? "台式机" : "笔记本 / 有电池")}");

            var load = _load?.Sample();
            if (load is not null)
            {
                sb.AppendLine("── 系统负载（LibreHardwareMonitorLib 传感器）──");
                sb.AppendLine($"  传感器层 : {(_load!.HardwareReady ? "已就绪" : "初始化中 / 不可用")}");
                sb.AppendLine($"  CPU      : {load.CpuPercent:F1} %");
                sb.AppendLine($"  内存     : {load.MemoryPercent:F1} %");
                sb.AppendLine($"  GPU      : {(load.GpuPercent is { } gpu ? gpu.ToString("F1") + " %" : "不可用 (权重已按 CPU:内存 重分配)")}");
                sb.AppendLine($"  加权负载 : {load.LoadPercent:F1} %");
                sb.AppendLine($"  实测功耗 : {(load.Watts.HasValue ? load.Watts.Value.ToString("F1") + " W (CPU 封装 + GPU 传感器之和)" : "无功率传感器（CPU 封装功率需管理员权限；GPU 功率需 NVIDIA/AMD 驱动接口）")}");
            }

            var battery = _isDesktop ? null : BatteryService.GetSnapshot();
            if (battery is not null)
            {
                sb.AppendLine("── 电池 ──");
                sb.AppendLine($"  剩余     : {battery.RemainingWh:F1} / {battery.FullWh:F1} Wh ({battery.Percent}%)");
                sb.AppendLine($"  状态     : {(battery.AcOnline ? "已插电" : "电池供电")}{(battery.Charging ? " (充电中)" : "")}");
                sb.AppendLine($"  充电功率 : {(battery.RateWatts.HasValue ? battery.RateWatts.Value.ToString("F1") + " W" : "未知")}");
                string tier = battery.RateWatts.HasValue
                    ? (battery.RateWatts.Value >= ChargeModeService.FastThresholdWatts
                        ? $"Fast (≥{ChargeModeService.FastThresholdWatts:F0}W 阈值)"
                        : $"Normal (<{ChargeModeService.FastThresholdWatts:F0}W)")
                    : "未知 (无速率数据)";
                sb.AppendLine($"  充电器档 : {tier}");
            }

            sb.AppendLine();
            var path = Path.Combine(Path.GetTempPath(), "endfield-diag.txt");
            File.AppendAllText(path, sb.ToString());
        }
        catch
        {
            // 诊断失败不影响主流程
        }
    }

    // ---------------- 内容组装 ----------------

    /// <summary>
    /// 组装一份 HUD 内容。
    /// </summary>
    /// <param name="full">true = 三态完整动画用；false = 简化胶囊用。</param>
    /// <param name="freshLoad">
    /// true = 立即采样一次负载；false = 用采样服务的缓存值。
    /// 常驻模式每秒刷新时用缓存，避免把 GPU 性能计数器也拖进刷新节奏。
    /// </param>
    private HudContent BuildContent(bool full, bool freshLoad = true)
    {
        var battery = _isDesktop ? null : BatteryService.GetSnapshot();

        SystemLoadSnapshot load = _load is null
            ? SystemLoadSnapshot.Empty
            : freshLoad ? _load.Sample() : _load.Current;

        var mode = _charge?.Update(battery) ?? ChargeMode.Unknown;

        // 刚插上还没有速率数据时，用上次记录的档位兜底（没有历史就保守给慢充）
        if (mode == ChargeMode.Unknown && _charge is not null)
            mode = _charge.InitialGuess();

        return full
            ? HudContentFactory.CreateFull(battery, load, mode)
            : HudContentFactory.CreateSimple(battery, load);
    }

    /// <summary>用示例数据播放一次，用于无法插拔或没有电池时检查动画。</summary>
    private async Task PreviewWithSampleDataAsync()
    {
        if (_hud is null)
            return;

        var sample = new BatterySnapshot(
            RemainingWh: 62.4,
            FullWh: 90.0,
            Percent: 69,
            AcOnline: true,
            Charging: true);

        // 演示用负载快照（与真实传感器同构，仅供 --demo/--force-desktop 预览动画）
        var demoLoad = new SystemLoadSnapshot(
            CpuPercent: 12d,
            MemoryPercent: 40d,
            GpuPercent: 8d,
            LoadPercent: SystemLoadService.ComputeLoad(12d, 40d, 8d),
            Watts: 68.4d);

        // 强制台式机时走功耗模式，否则用示例电池数据
        var content = _isDesktop
            ? HudContentFactory.CreateFull(null, demoLoad, ChargeMode.Unknown)
            : HudContentFactory.CreateFull(sample, demoLoad, ChargeMode.Fast);

        await _hud.ShowFullAsync(content);
    }

    /// <summary>播放一次简化版（拔电时只弹内容胶囊）。</summary>
    private async Task PreviewSimpleAsync()
    {
        if (_hud is null)
            return;

        var load = _load?.Sample() ?? SystemLoadSnapshot.Empty;
        var battery = _isDesktop ? null : BatteryService.GetSnapshot();

        await _hud.ShowSimpleAsync(HudContentFactory.CreateSimple(battery, load));
    }

    // ---------------- 常驻模式 ----------------

    private Task EnterResidentAsync()
    {
        if (_hud is null)
            return Task.CompletedTask;

        // 常驻期间的刷新用缓存负载值，不额外触发 GPU 计数器采样
        return _hud.ShowResidentAsync(() => BuildContent(full: true, freshLoad: false));
    }

    private void ExitResident()
    {
        _hud?.StopResident();
    }

    // ---------------- 电源监听 ----------------

    private void StartPowerWatching()
    {
        _watcher = new PowerWatcher();

        // 事件在后台线程触发，切回 UI 线程再操作窗口
        _watcher.PowerSourceChanged += (_, acOnline) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (acOnline)
                    _ = TriggerHudAsync();        // 插电：完整三态动画
                else
                    _ = TriggerSimpleHudAsync();  // 拔电：只弹内容胶囊
            });
        };

        _watcher.Start();
    }

    private async Task TriggerSimpleHudAsync()
    {
        if (_hud is null)
            return;

        // 电池读取可能走 WMI 兜底，放线程池避免卡 UI
        var content = await Task.Run(() => BuildContent(full: false));

        if (_hud.IsResident)
        {
            // 常驻模式不重播动画，原地更新即可
            _hud.RefreshContent();
            return;
        }

        await _hud.ShowSimpleAsync(content);
    }

    private async Task TriggerHudAsync()
    {
        if (_hud is null)
            return;

        if (_hud.IsResident)
        {
            // 常驻模式下插电也重播一次完整动画（模式标题只在动画里出现），
            // 播完自动回到常驻末态继续刷新。
            await EnterResidentAsync();
            return;
        }

        var content = await Task.Run(() => BuildContent(full: true));
        await _hud.ShowFullAsync(content);
    }

    // ---------------- 托盘 ----------------

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();

        var previewItem = new NativeMenuItem("预览 HUD");
        previewItem.Click += (_, _) => _ = TriggerHudAsync();
        menu.Add(previewItem);

        menu.Add(new NativeMenuItemSeparator());

        // 常驻模式：勾选后开机自启，且启动后常驻不消失（只能从托盘退出）
        var residentItem = new NativeMenuItem("常驻模式")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _settings.ResidentMode,
        };

        // 开机自启：勾选项，读写当前用户 Run 键
        var autoStartItem = new NativeMenuItem("开机自启")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = Services.AutoStart.IsEnabled(),
        };

        residentItem.Click += (_, _) =>
        {
            if (residentItem.IsChecked)
            {
                // 常驻 = 默认开机自启
                Services.AutoStart.Enable(Services.AutoStart.CurrentExePath);
                autoStartItem.IsChecked = Services.AutoStart.IsEnabled();
            }

            _settings.ResidentMode = residentItem.IsChecked;
            _settings.Save();

            if (residentItem.IsChecked)
                _ = EnterResidentAsync();
            else
                ExitResident();
        };

        autoStartItem.Click += (_, _) =>
        {
            if (autoStartItem.IsChecked)
                Services.AutoStart.Enable(Services.AutoStart.CurrentExePath);
            else
                Services.AutoStart.Disable();
        };

        menu.Add(residentItem);
        menu.Add(autoStartItem);

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) => desktop.Shutdown();
        menu.Add(exitItem);

        _tray = new TrayIcon
        {
            ToolTipText = _isDesktop ? "EndfieldCharge · 功耗模式" : "EndfieldCharge · 电量 HUD",
            Menu = menu,
            IsVisible = true,
        };

        try
        {
            var uri = new Uri("avares://EndfieldCharge/Assets/tray_bolt.png");
            using var stream = AssetLoader.Open(uri);
            _tray.Icon = new WindowIcon(new Bitmap(stream));
        }
        catch
        {
            // 图标加载失败也不影响功能，托盘仍可点击
        }

        var icons = new TrayIcons { _tray };
        TrayIcon.SetIcons(this, icons);
    }

    // ---------------- 收尾 ----------------

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _watcher?.Dispose();
        _watcher = null;

        _load?.Dispose();
        _load = null;

        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
            _tray = null;
        }
    }
}
