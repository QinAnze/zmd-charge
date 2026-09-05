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
    private ConsoleController? _console;
    private InkOverlayWindow? _ink;
    private SlideshowWatcher? _watcher;
    private PptComBridge? _bridge;
    private TrayIcon? _tray;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    private bool _comPages;          // COM 页码是否接管（接管后墨迹页码由轮询校准）
    private Screen? _activeScreen;   // 当前放映屏（页面列表面板定位用）
    private PageListWindow? _pageList;   // 当前页面列表面板（同一时刻至多一个）

    private const int DemoSlideCount = 8;   // --demo / 无 COM 连接时的兜底页数

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

        _console = new ConsoleController();
        _ink = new InkOverlayWindow();

        // COM 桥：页码轮询校准（任意翻页方式都同步墨迹"按页记忆"）
        _bridge = new PptComBridge();
        _bridge.CurrentSlideChanged += page =>
            Dispatcher.UIThread.Post(() => _ink?.SetCurrentPage(page));

        // 控制条 → 放映：翻页键击 + 墨迹层页码联动（自绘墨迹按页记忆）
        _console.PrevRequested += () => Dispatcher.UIThread.Post(() =>
        {
            InputNative.SendArrowKey(forward: false);
            if (!_comPages)                       // COM 接管时页码由轮询校准
                _ink?.NotifyPageChanged(-1);
        });
        _console.NextRequested += () => Dispatcher.UIThread.Post(() =>
        {
            InputNative.SendArrowKey(forward: true);
            if (!_comPages)
                _ink?.NotifyPageChanged(1);
        });

        // 页面列表入口 → 弹出缩略图列表（跳页走 GotoSlide）
        _console.ListRequested += () =>
            Dispatcher.UIThread.Post(() => OnListRequested());

        // 控制条 → 墨迹层：工具联动（选择=穿透；笔/橡皮=接管）+ 面板设置
        _console.ToolChanged += tool => Dispatcher.UIThread.Post(() => ApplyTool(MapTool(tool)));
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

        _activeScreen = screen;

        // COM 桥：附着 PowerPoint，取真实页数/当前页并开始轮询校准。
        // 连接失败（PowerPoint 未运行 / 未放映）则回到内部计数兜底。
        _comPages = _bridge?.Connect() ?? false;
        if (_comPages && _bridge is not null && _bridge.CurrentSlide > 0)
            _ink.SetCurrentPage(_bridge.CurrentSlide);

        _ink.AttachTo(screen);      // 墨迹层先就位（控制条保持在最上）
        _console.ShowOn(screen);
        ApplyTool(ConsoleTool.Select);
        _console.ReassertTopmost();
    }

    private void HideConsole()
    {
        if (_console is null || _ink is null) return;

        _pageList?.Close();
        _pageList = null;

        _bridge?.Disconnect();      // 停止轮询，页码回到内部计数
        _comPages = false;

        _ = _console.HideAsync();   // 收回动画结束后窗口隐藏
        _ink.Detach();
    }

    /// <summary>中心窗口工具枚举 → 应用层工具枚举。</summary>
    private static ConsoleTool MapTool(CenterCapsuleWindow.Tool tool) => tool switch
    {
        CenterCapsuleWindow.Tool.Pen => ConsoleTool.Pen,
        CenterCapsuleWindow.Tool.Eraser => ConsoleTool.Eraser,
        _ => ConsoleTool.Select,
    };

    /// <summary>页面列表入口：弹出缩略图网格（COM 导出 / 兜底页码），点击跳页。</summary>
    private void OnListRequested()
    {
        if (_activeScreen is null || _ink is null) return;

        // 同一时刻至多一个面板：重复点 ☰ 时只刷新到当前页，不叠窗
        _pageList?.Close();

        int count = _comPages
            ? Math.Max(1, _bridge?.SlideCount ?? DemoSlideCount)
            : DemoSlideCount;
        int current = _comPages
            ? Math.Max(1, _bridge?.CurrentSlide ?? _ink.CurrentPage)
            : _ink.CurrentPage;

        var list = new PageListWindow(_comPages ? _bridge : null, current, OnPageJump);
        list.Build(count);
        list.Place(_activeScreen);
        _pageList = list;
        list.Show();
        list.Topmost = true;
    }

    /// <summary>页面列表跳页：COM 路径走 GotoSlide（轮询校准墨迹页码），兜底路径直设墨迹页码。</summary>
    private void OnPageJump(int page)
    {
        _pageList?.Close();
        _pageList = null;

        if (_comPages)
            _bridge?.GotoSlide(page);
        else
            _ink?.SetCurrentPage(page);
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
            if (_console is { IsShown: true })
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
