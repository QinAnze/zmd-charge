using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using EndfieldCharge.Contract;
using EndfieldCharge.Services;
using EndfieldCharge.Settings;
using EndfieldCharge.Views;

namespace EndfieldCharge;

public partial class App : Application
{
    private PowerWatcher? _watcher;
    private HudWindow? _hud;
    private TrayIcon? _tray;
    private TrayMenuWindow? _trayMenu;
    private AppSettings _settings = new();
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _lastLowBatteryNotified;
    private DateTime _lastAdapterPopupUtc;
    private HotkeyService? _hotkeys;
    private AdapterManager? _adapters;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        _desktop = desktop;

        // 加载设置
        _settings = SettingsManager.Load();
        Localization.UseSettings(_settings);
        Logger.Enabled = true; // 可改为设置项

        // 全局未捕获异常兜底
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Logger.Error(e.ExceptionObject as Exception ?? new Exception("Unknown unhandled error"));
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error(e.Exception);
            e.SetObserved();
        };

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.Exit += OnDesktopExit;

        _hud = new HudWindow();
        _hud.ApplySettings(_settings);

        // 适配器：内置电池（主药丸）+ adapters\ 目录下的插件（各自一粒药丸）。
        // 构造只装内置适配器，插件在后台加载——慢插件（蓝牙/ADB 初始化）不能拖住托盘图标。
        _adapters = new AdapterManager();
        _ = _adapters.LoadPluginsAsync();
        // 插件状态变化（事件回调 / 15 秒轮询兜底）→ 主动弹 HUD
        _adapters.AdapterStateChanged += OnAdapterStateChanged;

        SetupTrayIcon();
        StartPowerWatching();

        StartHotkeys();
        // 调试命令行参数
        if (HasCommandLineArg("--demo"))
            _ = PreviewWithSampleDataAsync();
        else if (HasCommandLineArg("--demo-multi"))
            _ = PreviewMultiAdapterAsync();
        else if (HasCommandLineArg("--preview-unplug"))
            _ = PreviewBatteryAsync();
        else if (HasCommandLineArg("--preview-simple"))
            _ = PreviewSimpleAsync();
        else if (HasCommandLineArg("--preview"))
            _ = TriggerHudAsync();
        else if (HasCommandLineArg("--settings"))
            OpenSettingsWindow();

        base.OnFrameworkInitializationCompleted();
    }

    // ---------------- 设置 ----------------

    /// <summary>应用新设置。返回全局快捷键是否注册成功（失败通常是组合键被其它程序占用）。</summary>
    public bool OnSettingsChanged(AppSettings settings)
    {
        _settings = settings;
        Localization.UseSettings(settings);
        _hud?.ApplySettings(settings);

        // 更新托盘提示
        if (_tray is not null)
            _tray.ToolTipText = Localization.TrayTooltip;

        // 快捷键可能被改过（开关 / 组合键），重新注册
        return ApplyHotkey(settings);
    }

    // ---------------- 命令行参数 ----------------

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

    // ---------------- 预览 ----------------

    /// <summary>把样本快照包装成单适配器读数（主药丸不显示名称，适配器仅作占位）。</summary>
    private static AdapterReading SampleReading(BatterySnapshot? snap)
        => new(new StaticAdapter("sample", "Sample"), snap);

    private async Task PreviewWithSampleDataAsync()
    {
        if (_hud is null) return;

        var sample = new BatterySnapshot(
            RemainingWh: 62.4, FullWh: 90.0,
            Percent: 69, AcOnline: true, Charging: true);

        await _hud.ShowAndPlayAsync([SampleReading(sample)], acOnline: true);
    }

    /// <summary>--preview-unplug：未插电（电池模式）三态动画，样本数据。</summary>
    private async Task PreviewBatteryAsync()
    {
        if (_hud is null) return;

        var sample = new BatterySnapshot(
            RemainingWh: 62.4, FullWh: 90.0,
            Percent: 69, AcOnline: false, Charging: false);

        await _hud.ShowAndPlayAsync([SampleReading(sample)], acOnline: false, HudPlayMode.Battery);
    }

    private async Task PreviewSimpleAsync()
    {
        if (_hud is null) return;

        var sample = new BatterySnapshot(
            RemainingWh: 62.4, FullWh: 90.0,
            Percent: 69, AcOnline: false, Charging: false);

        await _hud.ShowSimpleAsync([SampleReading(sample)]);
    }

    /// <summary>--demo-multi：多适配器预览——主药丸（内置）+ 两粒插件样例药丸，验证纵向堆叠。</summary>
    private async Task PreviewMultiAdapterAsync()
    {
        if (_hud is null) return;

        var readings = new List<AdapterReading>
        {
            SampleReading(new BatterySnapshot(
                RemainingWh: 62.4, FullWh: 90.0, Percent: 69, AcOnline: true, Charging: true)),
            new AdapterReading(
                new StaticAdapter("demo-phone", "演示手机"),
                new BatterySnapshot(RemainingWh: 20.9, FullWh: 36.0, Percent: 58, AcOnline: false, Charging: false)),
            new AdapterReading(
                new StaticAdapter("demo-pad", "演示平板"),
                new BatterySnapshot(RemainingWh: 3.6, FullWh: 30.0, Percent: 12, AcOnline: false, Charging: false)),
        };

        await _hud.ShowAndPlayAsync(readings, acOnline: true);
    }

    // ---------------- 电源监听 ----------------

    private void StartPowerWatching()
    {
        _watcher = new PowerWatcher();

        _watcher.PowerSourceChanged += (_, acOnline) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (acOnline)
                    _ = TriggerHudAsync();
                else
                    _ = TriggerBatteryHudAsync();
            });
        };

        // 省电模式开关：开启 → 完整三态（省电文案）；关闭 → 简化电量胶囊
        _watcher.PowerSavingChanged += (_, enabled) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_settings.EnablePowerSaverNotify)
                    return;

                if (enabled)
                    _ = TriggerSaverHudAsync();
                else
                    _ = TriggerSimpleHudAsync();
            });
        };

        _watcher.Start();
    }

    // ---------------- 全局快捷键 ----------------

    private void StartHotkeys()
    {
        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += (_, _) =>
        {
            Logger.Info("HotkeyService: hotkey pressed -> show HUD");
            Dispatcher.UIThread.Post(() => _ = TriggerHudAsync(checkAlerts: false, replayIntro: false));
        };
        _hotkeys.Start();
        ApplyHotkey(_settings);
    }

    /// <summary>按设置注册 / 注销全局快捷键。返回是否注册成功（关闭快捷键时返回 true）。</summary>
    private bool ApplyHotkey(AppSettings settings)
    {
        if (_hotkeys is null)
            return true;

        uint key = settings.HotkeyKey <= 0 ? 0u : (uint)settings.HotkeyKey;
        return _hotkeys.Apply(settings.HotkeyModifiers, key, settings.EnableHotkey);
    }

    private async Task TriggerSaverHudAsync()
    {
        if (_hud is null || _adapters is null) return;

        var readings = await _adapters.ReadAllAsync();
        await _hud.ShowAndPlayAsync(readings, acOnline: true, HudPlayMode.PowerSaver);
    }

    private async Task TriggerSimpleHudAsync()
    {
        if (_hud is null || _adapters is null) return;

        var readings = await _adapters.ReadAllAsync();
        await _hud.ShowSimpleAsync(readings);
    }

    /// <summary>拔电：完整三态动画的「电池模式」——波纹向内收拢，暗示能量在流出。</summary>
    private async Task TriggerBatteryHudAsync()
    {
        if (_hud is null || _adapters is null) return;

        var readings = await _adapters.ReadAllAsync();
        await _hud.ShowAndPlayAsync(readings, acOnline: false, HudPlayMode.Battery);
    }

    /// <summary>弹出电量 HUD（所有适配器读数 + 完整三态动画；主药丸 = 第一位适配器：有内置电池即内置，台式机则为第一个插件）。</summary>
    /// <param name="checkAlerts">是否顺带检查低电量 / 充满提醒。插拔电源时为 true，快捷键唤起时为 false。</param>
    /// <param name="replayIntro">
    /// HUD 正在播三态动画时是否重播开场。快捷键唤起传 false —— 重播会先撤掉上一轮动画，
    /// 画面会"灭"一下再重新弹出电标；传 false 时 HudWindow 改成续住当前停留段。
    /// 插拔电源 / 托盘预览保持 true（状态变了就该重新播一遍）。
    /// </param>
    private async Task TriggerHudAsync(bool checkAlerts = true, bool replayIntro = true)
    {
        if (_hud is null || _adapters is null) return;

        // AC 状态优先读本机电源：决定文案主题与波纹方向。台式机没有电池读不到时，
        // 退回主药丸（此时是插件，如 UPS）快照里的 AC 状态
        var acTask = Task.Run(() =>
        {
            bool ok = PowerNative.TryGetAcOnline(out bool ac);
            return (ok, ac);
        });
        var readings = await _adapters.ReadAllAsync();
        var (acRead, acNative) = await acTask;

        var snapshot = readings.Count > 0 ? readings[0].Snapshot : null;
        bool acOnline = acRead ? acNative : snapshot?.AcOnline == true;

        // 文案/波纹跟着真实电源状态走：未插电时不该再显示「超充模式」
        var mode = acOnline ? HudPlayMode.Charge : HudPlayMode.Battery;
        await _hud.ShowAndPlayAsync(readings, acOnline, mode, replayIntro: replayIntro);
        // 检查提醒条件（低电量 / 充满提醒只看本机电池）
        if (checkAlerts && snapshot is not null)
            CheckAlerts(snapshot);
    }

    /// <summary>插件适配器状态变化（充电/外接翻转，事件回调或轮询发现）→ 弹 HUD 展示全部药丸。</summary>
    private void OnAdapterStateChanged(IPowerAdapter adapter)
    {
        // 事件可能来自插件的任意线程，统一转回 UI 线程；单适配器防抖已在 AdapterManager（5 秒）
        Dispatcher.UIThread.Post(() =>
        {
            if (_hud is null || _adapters is null)
                return;

            // 多个插件同时变化时合并成一次弹出，避免动画连续重播抖动
            if (DateTime.UtcNow - _lastAdapterPopupUtc < TimeSpan.FromSeconds(1.5))
                return;
            _lastAdapterPopupUtc = DateTime.UtcNow;

            _ = TriggerHudAsync(checkAlerts: false, replayIntro: true);
        });
    }

    /// <summary>检查并触发低电量 / 充满提醒。</summary>
    private void CheckAlerts(BatterySnapshot snap)
    {
        if (!snap.HasBattery) return;

        // 充满提醒（充电中且 >= 99%）
        if (_settings.EnableFullChargeAlert && snap.Charging && snap.Percent >= 99)
        {
            _ = ShowAlertAsync(Localization.FullChargeTitle, Localization.FullChargeMsg);
        }

        // 低电量提醒（放电中且低于阈值，每轮只提醒一次）
        if (_settings.EnableLowBatteryAlert && !snap.Charging && snap.Percent <= _settings.LowBatteryThreshold)
        {
            if (!_lastLowBatteryNotified)
            {
                _lastLowBatteryNotified = true;
                _ = ShowAlertAsync(Localization.LowBatteryTitle, Localization.LowBatteryMsg(snap.Percent));
            }
        }
        else
        {
            _lastLowBatteryNotified = false;
        }
    }

    /// <summary>弹出一个卡牌风格提醒窗口，4 秒后自动消失。</summary>
    private static async Task ShowAlertAsync(string title, string message)
    {
        var alert = new Window
        {
            Title = title,
            Width = 340, Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#18181A")),
            Foreground = Avalonia.Media.Brushes.White,
            CanResize = false,
            SystemDecorations = SystemDecorations.BorderOnly,
            Topmost = true,
            FontFamily = new Avalonia.Media.FontFamily("HarmonyOS Sans SC, HarmonyOS Sans, Inter, Microsoft YaHei UI, sans-serif"),
        };

        var card = new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#232325")),
            CornerRadius = new Avalonia.CornerRadius(10),
            Margin = new Avalonia.Thickness(12),
            Padding = new Avalonia.Thickness(18, 16),
        };

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C6CA4C")),
        });
        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C8C8C8")),
        });
        card.Child = stack;
        alert.Content = card;

        // 入场动画
        alert.Opacity = 0;
        alert.RenderTransform = new Avalonia.Media.ScaleTransform(0.92, 0.92);
        alert.RenderTransformOrigin = new Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative);

        alert.Show();

        var fadeIn = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(150),
            FillMode = Avalonia.Animation.FillMode.Forward,
            Easing = new Avalonia.Animation.Easings.QuadraticEaseOut(),
            Children =
            {
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0d), Setters = { new Avalonia.Styling.Setter(Avalonia.Visual.OpacityProperty, 0d) } },
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1d), Setters = { new Avalonia.Styling.Setter(Avalonia.Visual.OpacityProperty, 1d) } },
            },
        };
        _ = fadeIn.RunAsync(alert);

        await Task.Delay(4000);

        // 退场
        var fadeOut = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(120),
            FillMode = Avalonia.Animation.FillMode.Forward,
            Easing = new Avalonia.Animation.Easings.QuadraticEaseIn(),
            Children =
            {
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0d), Setters = { new Avalonia.Styling.Setter(Avalonia.Visual.OpacityProperty, 1d) } },
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1d), Setters = { new Avalonia.Styling.Setter(Avalonia.Visual.OpacityProperty, 0d) } },
            },
        };
        await fadeOut.RunAsync(alert);
        if (alert.IsVisible)
            alert.Close();
    }

    // ---------------- 托盘 ----------------

    private void SetupTrayIcon()
    {
        // 自定义菜单（TrayMenuWindow）：左键托盘弹出。
        // 不设原生 Menu——11.2 中右键仅在 Menu 非空时弹原生菜单，置空后右键无动作。
        _tray = new TrayIcon
        {
            ToolTipText = Localization.TrayTooltip,
            IsVisible = true,
        };

        _tray.Clicked += OnTrayClicked;

        try
        {
            var uri = new Uri("avares://EndfieldCharge/Assets/tray_bolt.png");
            using var stream = AssetLoader.Open(uri);
            _tray.Icon = new WindowIcon(new Bitmap(stream));
        }
        catch
        {
        }

        var icons = new TrayIcons { _tray };
        TrayIcon.SetIcons(this, icons);
    }

    private void OnTrayClicked(object? sender, EventArgs e)
    {
        // 关闭已打开的菜单
        if (_trayMenu is not null && _trayMenu.IsVisible)
        {
            _trayMenu.Close();
            _trayMenu = null;
            return;
        }

        // 单击托盘图标：立即播放电量预览（真实电池数据，完整三态动画），同时弹出菜单
        _ = TriggerHudAsync();

        _trayMenu = new TrayMenuWindow();
        _trayMenu.PreviewClicked += () => { _trayMenu.Close(); _ = TriggerHudAsync(); };
        _trayMenu.SettingsClicked += () => { _trayMenu.Close(); OpenSettingsWindow(); };
        _trayMenu.CheckUpdateClicked += async () =>
        {
            _trayMenu.Close();
            _trayMenu = null;
            try
            {
                var (hasUpdate, version, url) = await UpdateChecker.CheckAsync();
                if (hasUpdate && url is not null)
                {
                    var result = await MessageBox.Show(
                        _hud ?? new HudWindow(),
                        Localization.UpdateMsg(version ?? "?"),
                        Localization.UpdateTitle,
                        MessageBoxButton.OkCancel);

                    if (result == MessageBoxResult.Ok)
                        Platform.Start(url);
                }
                else
                {
                    await ShowAlertAsync(Localization.CheckUpdate, Localization.UpToDate);
                }
            }
            catch
            {
                await ShowAlertAsync(Localization.CheckUpdate, Localization.UpdateCheckFailed);
            }
        };
        _trayMenu.ExitClicked += () => { _trayMenu.Close(); _desktop?.Shutdown(); };

        // 刷新本地化文字
        _trayMenu.MenuPreviewText.Text = Localization.PreviewHud;
        _trayMenu.MenuSettingsText.Text = Localization.Settings;
        _trayMenu.MenuCheckUpdateText.Text = Localization.CheckUpdate;
        _trayMenu.MenuExitText.Text = Localization.Exit;

        _trayMenu.ShowAtTray();
    }

    private void OpenSettingsWindow(string initialTab = "General")
    {
        // _hud 在 OnFrameworkInitializationCompleted 中先于托盘创建，此处必非空
        var win = new SettingsWindow(_settings, _hud!, initialTab);
        win.Show();
    }

    // ---------------- 收尾 ----------------

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {

        _hotkeys?.Dispose();
        _hotkeys = null;
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