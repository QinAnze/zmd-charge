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
using EndfieldCharge.Settings;
using EndfieldCharge.Views;

namespace EndfieldCharge;

public partial class App : Application
{
    private IPowerMonitor? _watcher;
    private HudDisplayCoordinator? _hudTrigger;
    private HudWindow? _hud;
    private TrayIcon? _tray;
    private AppSettings _settings = new();
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _lastLowBatteryNotified;

    /// <summary>平台服务（Windows / macOS 由 <see cref="PlatformServices.Create"/> 分发）。</summary>
    public static IPlatformServices Platform { get; private set; } = null!;

    /// <summary>当前应用实例（HudWindow / SettingsWindow 用来取平台服务与设置）。</summary>
    public static new App? Current => Application.Current as App;

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

        // 平台服务：必须在建窗口之前就绪（macOS 需要先设定激活策略，避免 Dock 图标闪现）
        Platform = PlatformServices.Create();
        Platform.ApplyAppActivationPolicy();

        Logger.Info($"App: platform={Platform.Name}, bundle={Platform.IsBundle}");

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

        SetupTrayIcon();
        StartPowerWatching();

        // 调试命令行参数
        if (HasCommandLineArg("--demo"))
            _ = PreviewWithSampleDataAsync();
        else if (HasCommandLineArg("--preview-unplug"))
            _ = PreviewSimpleAsync();
        else if (HasCommandLineArg("--preview"))
            _ = TriggerHudAsync();

        base.OnFrameworkInitializationCompleted();
    }

    // ---------------- 设置 ----------------

    public void OnSettingsChanged(AppSettings settings)
    {
        _settings = settings;
        Localization.UseSettings(settings);
        _hud?.ApplySettings(settings);

        // 更新托盘提示与菜单文案
        Platform.RefreshTrayLocalization();
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

    private async Task PreviewWithSampleDataAsync()
    {
        if (_hud is null) return;

        var sample = new BatterySnapshot(
            RemainingWh: 62.4, FullWh: 90.0,
            Percent: 69, AcOnline: true, Charging: true);

        await _hud.ShowAndPlayAsync(sample, acOnline: true);
    }

    private async Task PreviewSimpleAsync()
    {
        if (_hud is null) return;

        var sample = new BatterySnapshot(
            RemainingWh: 62.4, FullWh: 90.0,
            Percent: 69, AcOnline: false, Charging: false);

        await _hud.ShowSimpleAsync(sample);
    }

    // ---------------- 电源监听 ----------------

    private void StartPowerWatching()
    {
        _watcher = Platform.CreatePowerMonitor();

        // 同一物理动作会引发多条事件（macOS 插拔电源时会紧接着自动切低电量模式，
        // 实测滞后 400ms–2.1s），必须合并，否则后一次 HUD 会把前一次的动画掐断。
        _hudTrigger = new HudDisplayCoordinator(
            dispatch: kind =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    switch (kind)
                    {
                        case HudDisplayCoordinator.HudKind.AcConnected:
                            _ = TriggerHudAsync();
                            break;
                        case HudDisplayCoordinator.HudKind.AcDisconnected:
                            _ = TriggerSimpleHudAsync();
                            break;
                        case HudDisplayCoordinator.HudKind.SaverOn:
                            if (_settings.EnablePowerSaverNotify)
                                _ = TriggerSaverHudAsync();
                            break;
                        case HudDisplayCoordinator.HudKind.SaverOff:
                            if (_settings.EnablePowerSaverNotify)
                                _ = TriggerSimpleHudAsync();
                            break;
                    }
                });
            },
            // macOS 会在插拔电源时自动跟着切低电量模式 → 该事件是副作用，不单独播。
            // Windows 的省电模式不会被电源切换改写，用户手动开关必须照常提示。
            suppressAcCorrelatedSaver: Platform.Name == "macOS");

        _watcher.PowerSourceChanged += (_, acOnline) => _hudTrigger.OnAcChanged(acOnline);
        _watcher.PowerSavingChanged += (_, enabled) => _hudTrigger.OnSaverChanged(enabled);

        _watcher.Start();
    }

    private async Task TriggerSaverHudAsync()
    {
        if (_hud is null || _watcher is null) return;

        var monitor = _watcher;
        var snapshot = await Task.Run(() => monitor.GetSnapshot());
        await _hud.ShowAndPlayAsync(snapshot, acOnline: true, HudPlayMode.PowerSaver);
    }

    private async Task TriggerSimpleHudAsync()
    {
        if (_hud is null || _watcher is null) return;

        var monitor = _watcher;
        var snapshot = await Task.Run(() => monitor.GetSnapshot());

        await _hud.ShowSimpleAsync(snapshot);
    }

    private async Task TriggerHudAsync()
    {
        if (_hud is null || _watcher is null) return;

        var monitor = _watcher;
        var (snapshot, acOnline) = await Task.Run(() =>
        {
            monitor.TryGetAcOnline(out bool ac);
            return (monitor.GetSnapshot(), ac);
        });

        await _hud.ShowAndPlayAsync(snapshot, acOnline);

        // 检查提醒条件
        if (snapshot is not null)
            CheckAlerts(snapshot);
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
            FontFamily = new Avalonia.Media.FontFamily("HarmonyOS Sans SC, HarmonyOS Sans, Inter, PingFang SC, Microsoft YaHei UI, Hiragino Sans GB, sans-serif"),
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
        // 菜单呈现方式由平台决定：
        //   Windows —— TrayIcon.Clicked 可用，点击时弹出 TrayMenuWindow 自绘深色菜单
        //   macOS   —— Clicked 永不触发，改用 NativeMenu（NSStatusItem 左键自动弹出）
        _tray = Platform.CreateTrayIcon(new TrayMenuActions(
            OnPreviewRequested,
            OnSettingsRequested,
            OnCheckUpdateRequested,
            ExitApp));

        SetTrayIconImage(_tray);

        var icons = new TrayIcons { _tray };
        TrayIcon.SetIcons(this, icons);
    }

    /// <summary>托盘图标统一使用 PNG（.ico 仅 Windows 目标引入，macOS 上不支持）。</summary>
    private static void SetTrayIconImage(TrayIcon tray)
    {
        try
        {
            var uri = new Uri("avares://EndfieldCharge/Assets/tray_bolt.png");
            using var stream = AssetLoader.Open(uri);
            tray.Icon = new WindowIcon(new Bitmap(stream));
        }
        catch
        {
            // 图标加载失败不致命：托盘仍可用，只是没有图标
        }
    }

    // ---------------- 托盘动作 ----------------

    private void OnPreviewRequested() => _ = TriggerHudAsync();

    private void OnSettingsRequested() => OpenSettingsWindow();

    private void OnCheckUpdateRequested() => _ = CheckForUpdatesAsync();

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var (hasUpdate, version, url) = await UpdateChecker.CheckAsync();
            if (hasUpdate && url is not null)
            {
                Platform.ActivateForDialog();

                var result = await MessageBox.Show(
                    _hud ?? new HudWindow(),
                    Localization.UpdateMsg(version ?? "?"),
                    Localization.UpdateTitle,
                    MessageBoxButton.OkCancel);

                if (result == MessageBoxResult.Ok)
                    ShellOpen.Start(url);
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
    }

    private void ExitApp() => _desktop?.Shutdown();

    private void OpenSettingsWindow(string initialTab = "General")
    {
        // _hud 在 OnFrameworkInitializationCompleted 中先于托盘创建，此处必非空
        var win = new SettingsWindow(_settings, _hud!, initialTab);

        // macOS Accessory 策略下必须显式激活，否则窗口会出现在所有应用后面
        Platform.ActivateForDialog();
        win.Show();
        win.Activate();
    }

    // ---------------- 收尾 ----------------

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _hudTrigger?.Dispose();
        _hudTrigger = null;

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