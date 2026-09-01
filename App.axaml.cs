using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using EndfieldCharge.Services;
using EndfieldCharge.Views;

namespace EndfieldCharge;

public partial class App : Application
{
    private PowerWatcher? _watcher;
    private HudWindow? _hud;
    private TrayIcon? _tray;

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

            _hud = new HudWindow();

            SetupTrayIcon(desktop);
            StartPowerWatching();

            // 调试/自查入口：不用真的插拔电源也能看动画
            //   --preview         用本机真实电池数据播放一次（完整版：插电）
            //   --demo            用示例数据播放一次（台式机 / 读不到电池时用）
            //   --preview-unplug  用示例数据播放一次（简化版：拔电只弹电量胶囊）
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

    /// <summary>用示例数据播放一次，用于在没有电池或无法插拔时检查动画。</summary>
    private async Task PreviewWithSampleDataAsync()
    {
        if (_hud is null)
            return;

        var sample = new Services.BatterySnapshot(
            RemainingWh: 62.4,
            FullWh: 90.0,
            Percent: 69,
            AcOnline: true,
            Charging: true);

        await _hud.ShowAndPlayAsync(sample, acOnline: true);
    }

    /// <summary>用示例数据播放一次简化版（拔电只弹电量圆胶囊）。</summary>
    private async Task PreviewSimpleAsync()
    {
        if (_hud is null)
            return;

        var sample = new Services.BatterySnapshot(
            RemainingWh: 62.4,
            FullWh: 90.0,
            Percent: 69,
            AcOnline: false,
            Charging: false);

        await _hud.ShowSimpleAsync(sample);
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
                    _ = TriggerHudAsync();       // 插电：完整三态动画
                else
                    _ = TriggerSimpleHudAsync(); // 拔电：只弹电量圆胶囊
            });
        };

        _watcher.Start();
    }

    private async Task TriggerSimpleHudAsync()
    {
        if (_hud is null)
            return;

        // 电池读取可能走 WMI 兜底，放在线程池里避免卡 UI
        var (snapshot, _) = await Task.Run(() =>
        {
            PowerNative.TryGetAcOnline(out bool ac);
            return (BatteryService.GetSnapshot(), ac);
        });

        await _hud.ShowSimpleAsync(snapshot);
    }

    private async Task TriggerHudAsync()
    {
        if (_hud is null)
            return;

        // 电池读取可能走 WMI 兜底，放在线程池里避免卡 UI
        var (snapshot, acOnline) = await Task.Run(() =>
        {
            PowerNative.TryGetAcOnline(out bool ac);
            return (BatteryService.GetSnapshot(), ac);
        });

        await _hud.ShowAndPlayAsync(snapshot, acOnline);
    }

    // ---------------- 托盘 ----------------

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();

        var previewItem = new NativeMenuItem("预览电量 HUD");
        previewItem.Click += (_, _) => _ = TriggerHudAsync();
        menu.Add(previewItem);

        menu.Add(new NativeMenuItemSeparator());

        // 开机自启：勾选项，读写当前用户 Run 键
        var autoStartItem = new NativeMenuItem("开机自启")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = Services.AutoStart.IsEnabled(),
        };
        autoStartItem.Click += (_, _) =>
        {
            if (autoStartItem.IsChecked)
                Services.AutoStart.Enable(Services.AutoStart.CurrentExePath);
            else
                Services.AutoStart.Disable();
        };
        menu.Add(autoStartItem);

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) => desktop.Shutdown();
        menu.Add(exitItem);

        _tray = new TrayIcon
        {
            ToolTipText = "EndfieldCharge · 电量 HUD",
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

        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
            _tray = null;
        }
    }
}
