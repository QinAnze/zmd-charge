using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PptConsole.Services;
using PptConsole.Views;

namespace PptConsole;

public enum ConsoleTool
{
    Select,   // 选择：墨迹层穿透，触控直达放映
    Pen,      // 笔：墨迹层接管触控
    Eraser,   // 橡皮：墨迹层接管触控
}

public partial class App : Application
{
    private ConsoleWindow? _console;
    private InkOverlayWindow? _ink;
    private SlideshowWatcher? _watcher;
    private TrayIcon? _tray;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        _desktop = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _console = new ConsoleWindow();
        _ink = new InkOverlayWindow();

        // 控制条 → 放映：翻页键击 + 墨迹层页码联动（自绘墨迹按页记忆）
        _console.PrevRequested += () => Dispatcher.UIThread.Post(() =>
        {
            InputNative.SendArrowKey(forward: false);
            _ink?.NotifyPageChanged(-1);
        });
        _console.NextRequested += () => Dispatcher.UIThread.Post(() =>
        {
            InputNative.SendArrowKey(forward: true);
            _ink?.NotifyPageChanged(1);
        });

        // 控制条 → 墨迹层：工具联动（选择=穿透；笔/橡皮=接管）+ 面板设置
        _console.ToolChanged += tool => Dispatcher.UIThread.Post(() => ApplyTool(tool));
        _console.PenSettingsChanged += (color, thickness) =>
            Dispatcher.UIThread.Post(() => _ink?.SetPenSettings(color, thickness));
        _console.EraserSettingsChanged += radius =>
            Dispatcher.UIThread.Post(() => _ink?.SetEraserRadius(radius));
        _console.InkUndo += () => Dispatcher.UIThread.Post(() => _ink?.Undo());
        _console.InkCleared += () => Dispatcher.UIThread.Post(() => _ink?.ClearCurrentPage());

        // 放映检测 → 控制台吊起/收回
        _watcher = new SlideshowWatcher();
        _watcher.SlideshowStarted += monitorBounds =>
            Dispatcher.UIThread.Post(() =>
            {
                var screen = FindScreen(monitorBounds);
                if (screen is not null)
                    ShowConsole(screen);
            });
        _watcher.SlideshowEnded += () =>
            Dispatcher.UIThread.Post(() => HideConsole());
        _watcher.Start();

        SetupTrayIcon();

        // --demo：无 PowerPoint 时在主屏预览控制台
        if (HasCommandLineArg("--demo"))
        {
            Dispatcher.UIThread.Post(() =>
            {
                var screen = _console?.Screens.Primary;
                if (screen is not null && _console is not null)
                    ShowConsole(screen);
            }, DispatcherPriority.Loaded);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ---------------- 控制台生命周期 ----------------

    private void ShowConsole(Screen screen)
    {
        if (_console is null || _ink is null) return;

        _ink.AttachTo(screen);      // 墨迹层先就位（控制条保持在最上）
        _console.ShowOn(screen);
        ApplyTool(ConsoleTool.Select);
        _console.ReassertTopmost();
    }

    private void HideConsole()
    {
        if (_console is null || _ink is null) return;

        _ = _console.HideAnimatedAsync();   // 收回动画结束后窗口隐藏
        _ink.Detach();
    }

    private void ApplyTool(ConsoleTool tool)
    {
        if (_ink is null) return;

        switch (tool)
        {
            case ConsoleTool.Select:
                _ink.SetToolMode(ConsoleTool.Select);
                _ink.SetPassthrough(true);
                break;
            case ConsoleTool.Pen:
                _ink.SetToolMode(ConsoleTool.Pen);
                _ink.SetPassthrough(false);
                _console?.ReassertTopmost();    // 墨迹层切入交互态后，控制条压回最上
                break;
            case ConsoleTool.Eraser:
                _ink.SetToolMode(ConsoleTool.Eraser);
                _ink.SetPassthrough(false);
                _console?.ReassertTopmost();
                break;
        }
    }

    /// <summary>放映窗口所在显示器（物理边界 → Avalonia Screen）。</summary>
    private Screen? FindScreen(PixelRect bounds)
    {
        if (_console is null) return null;

        foreach (var s in _console.Screens.All)
            if (s.Bounds == bounds)
                return s;

        return _console.Screens.All.FirstOrDefault(s => s.Bounds.Contains(bounds))
            ?? _console.Screens.Primary;
    }

    // ---------------- 托盘 ----------------

    private void SetupTrayIcon()
    {
        _tray = new TrayIcon
        {
            ToolTipText = "PPT 控制台",
            IsVisible = true,
        };

        try
        {
            var uri = new Uri("avares://PptConsole/Assets/tray_bolt.png");
            using var stream = AssetLoader.Open(uri);
            _tray.Icon = new WindowIcon(new Bitmap(stream));
        }
        catch
        {
        }

        // 左键托盘：手动吊起/收回（调试与无放映场景）
        _tray.Clicked += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_console is { IsVisible: true })
            {
                HideConsole();
            }
            else
            {
                var screen = _console?.Screens.Primary;
                if (screen is not null && _console is not null)
                    ShowConsole(screen);
            }
        });

        var menu = new NativeMenu();
        var exitItem = new NativeMenuItem { Header = "退出" };
        exitItem.Click += (_, _) => _desktop?.Shutdown();
        menu.Items.Add(exitItem);
        _tray.Menu = menu;

        var icons = new TrayIcons { _tray };
        TrayIcon.SetIcons(this, icons);
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
}
