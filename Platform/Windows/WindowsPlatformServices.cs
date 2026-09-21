using System;
using Avalonia;
using Avalonia.Controls;
using EndfieldCharge.Views;

namespace EndfieldCharge.Services;

/// <summary>
/// Windows 平台服务实现。
///
/// 与 macOS 的关键差异：
///   · 托盘用自绘 <c>TrayMenuWindow</c>（Avalonia Windows 后端会派发 Clicked 事件）
///   · 单个后台消息循环线程，<c>WindowsPowerMonitor</c> 内部无需加锁
///   · 无窗口层级/激活策略需要额外干预（Topmost + ShowInTaskbar=false 已足够）
/// </summary>
internal sealed class WindowsPlatformServices : IPlatformServices
{
    private TrayMenuWindow? _trayMenu;
    private TrayMenuActions? _actions;
    private TrayIcon? _tray;

    public static WindowsPlatformServices Create() => new();

    public string Name => "Windows";

    public bool IsBundle => true;

    public string PowerSaveModeText => "省电模式";

    public IPowerMonitor CreatePowerMonitor() => new WindowsPowerMonitor();

    public IAutoStartManager AutoStart { get; } = new WindowsAutoStart();

    // ---------------- 托盘 ----------------

    public TrayIcon CreateTrayIcon(TrayMenuActions actions)
    {
        _actions = actions;

        // 不设原生 Menu——11.2 中右键仅在 Menu 非空时弹原生菜单，置空后右键无动作；
        // 左键弹出自绘菜单由 App 处理。
        _tray = new TrayIcon
        {
            ToolTipText = Localization.TrayTooltip,
            IsVisible = true,
        };

        _tray.Clicked += (_, _) => OnTrayClicked();
        return _tray;
    }

    public void RefreshTrayLocalization()
    {
        if (_tray is not null)
            _tray.ToolTipText = Localization.TrayTooltip;
    }

    /// <summary>左键托盘图标：先播放电量预览，再弹出自绘菜单。</summary>
    private void OnTrayClicked()
    {
        var actions = _actions;
        if (actions is null)
            return;

        // 关闭已打开的菜单（再次点击 = 收起）
        if (_trayMenu is not null && _trayMenu.IsVisible)
        {
            _trayMenu.Close();
            _trayMenu = null;
            return;
        }

        // 单击托盘图标：立即播放电量预览（真实电池数据，完整三态动画），同时弹出菜单
        actions.OnPreview();

        var menu = new TrayMenuWindow();
        menu.PreviewClicked += () => { menu.Close(); actions.OnPreview(); };
        menu.SettingsClicked += () => { menu.Close(); actions.OnSettings(); };
        menu.CheckUpdateClicked += () => { menu.Close(); actions.OnCheckUpdate(); };
        menu.ExitClicked += () => { menu.Close(); actions.OnExit(); };

        // 刷新本地化文字
        menu.MenuPreviewText.Text = Localization.PreviewHud;
        menu.MenuSettingsText.Text = Localization.Settings;
        menu.MenuCheckUpdateText.Text = Localization.CheckUpdate;
        menu.MenuExitText.Text = Localization.Exit;

        menu.ShowAtTray();
        _trayMenu = menu;
    }

    // ---------------- 窗口与激活 ----------------

    public void ApplyAppActivationPolicy()
    {
        // Windows 无需处理：ShutdownMode.OnExplicitShutdown + ShowInTaskbar=false 已满足需求
    }

    public void OnHudWindowShown(Window window)
    {
        // Windows 无需处理：Topmost=True 已在 XAML 中生效
    }

    public void ActivateForDialog()
    {
        // Windows 无需处理：对话框/WPF 风格的 CenterOwner 已能正确获得焦点
    }

    /// <summary>Windows 无刘海概念，恒为 null → HudWindow 不播放出生前导，行为与改动前一致。</summary>
    public double? TryGetScreenSafeAreaTop(double screenLeft, double screenTop) => null;
}
