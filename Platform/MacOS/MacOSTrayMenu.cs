using System;
using Avalonia.Controls;

namespace EndfieldCharge.Services;

/// <summary>
/// macOS 托盘（菜单栏）菜单。
///
/// 为什么必须用原生 <c>NativeMenu</c>：
///   Avalonia 的 macOS 后端实现（<c>native/Avalonia.Native/src/OSX/trayicon.mm</c>）只暴露
///   <c>SetIcon</c> / <c>SetMenu</c> / <c>SetIsVisible</c> / <c>SetToolTipText</c>，
///   **没有任何 click 回调** —— 因此 <c>TrayIcon.Clicked</c> 在 macOS 上永不触发。
///   平台侧唯一支持的入口是把 <c>NativeMenu</c> 赋给 <c>TrayIcon.Menu</c>，
///   由 <c>NSStatusItem</c> 左键自动弹出（这也是 macOS 菜单栏应用的标准交互）。
/// </summary>
internal sealed class MacOSTrayMenu
{
    private readonly NativeMenu _menu = new();

    private NativeMenuItem? _preview;
    private NativeMenuItem? _settings;
    private NativeMenuItem? _checkUpdate;
    private NativeMenuItem? _exit;

    public MacOSTrayMenu(TrayMenuActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        _preview = new NativeMenuItem();
        _settings = new NativeMenuItem();
        _checkUpdate = new NativeMenuItem();
        _exit = new NativeMenuItem();

        _preview.Click += (_, _) => actions.OnPreview();
        _settings.Click += (_, _) => actions.OnSettings();
        _checkUpdate.Click += (_, _) => actions.OnCheckUpdate();
        _exit.Click += (_, _) => actions.OnExit();

        _menu.Items.Add(_preview);
        _menu.Items.Add(_settings);
        _menu.Items.Add(_checkUpdate);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(_exit);

        RefreshLocalization();
    }

    /// <summary>可赋给 <c>TrayIcon.Menu</c> 的原生菜单。</summary>
    public NativeMenu Menu => _menu;

    /// <summary>刷新菜单文案（语言切换后调用）。</summary>
    public void RefreshLocalization()
    {
        if (_preview is not null) _preview.Header = Localization.PreviewHud;
        if (_settings is not null) _settings.Header = Localization.Settings;
        if (_checkUpdate is not null) _checkUpdate.Header = Localization.CheckUpdate;
        if (_exit is not null) _exit.Header = Localization.Exit;
    }
}
