using System;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;

namespace EndfieldCharge.Services;

/// <summary>
/// macOS 平台服务实现。
///
/// 与 Windows 的关键差异：
///   · 托盘用原生 <c>NativeMenu</c>（Avalonia 的 macOS 后端不派发 TrayIcon.Clicked）
///   · 需显式把 NSApplication 设为 Accessory 策略（不占 Dock、不进 Cmd-Tab）
///   · HUD 需把 NSWindow 提升到弹窗层级并加入所有 Space，否则被菜单栏/全屏应用遮住
///   · 打开设置窗/对话框前必须显式激活应用
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOSPlatformServices : IPlatformServices
{
    private MacOSTrayMenu? _trayMenu;
    private TrayIcon? _tray;

    public static MacOSPlatformServices Create() => new();

    public string Name => "macOS";

    public bool IsBundle => MacOSAppKit.IsInAppBundle;

    public string PowerSaveModeText => "低电量模式";

    public IPowerMonitor CreatePowerMonitor() => new MacOSPowerMonitor();

    public IAutoStartManager AutoStart { get; } = new MacOSAutoStart();

    // ---------------- 托盘 ----------------

    public TrayIcon CreateTrayIcon(TrayMenuActions actions)
    {
        _trayMenu = new MacOSTrayMenu(actions);

        _tray = new TrayIcon
        {
            ToolTipText = Localization.TrayTooltip,
            IsVisible = true,
            Menu = _trayMenu.Menu,
        };

        return _tray;
    }

    public void RefreshTrayLocalization()
    {
        if (_tray is not null)
            _tray.ToolTipText = Localization.TrayTooltip;

        _trayMenu?.RefreshLocalization();
    }

    // ---------------- 窗口与激活 ----------------

    public void ApplyAppActivationPolicy()
    {
        try
        {
            if (!MacOSAppKit.ApplyAccessoryActivationPolicy())
            {
                Logger.Warn("MacOSPlatformServices: setActivationPolicy 不可用，依赖 Info.plist 的 LSUIElement");
                return;
            }

            Logger.Info("MacOSPlatformServices: activation policy = Accessory");
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    public void OnHudWindowShown(Window window)
    {
        try
        {
            var handle = window.TryGetPlatformHandle();
            if (handle is null || handle.Handle == IntPtr.Zero)
            {
                // 窗口原生句柄尚未创建（极早期调用），ShowPositioned 会再补一次
                Logger.Warn("MacOSPlatformServices: 原生窗口句柄尚未就绪");
                return;
            }

            // Avalonia macOS 后端暴露的句柄是 `AvnWindow`，而它是 NSWindow 的**子类**
            // （源码依据：MacOSTopLevelHandle 的 NSWindow 分支 + OSX/AvnWindow.mm
            //   的 @interface AvnWindow : NSWindow），因此可以直接发 NSWindow 消息，
            //   不需要沿 superview 解包。实测 object_getClassName 确认为 "AvnWindow"。
            var nsWindow = handle.Handle;

            // 兜底：若未来版本改为暴露 NSView（descriptor 会是 "NSView"），走解包路径
            if (!MacOSAppKit.RespondsToSelector(nsWindow, MacOSAppKit.SelSetLevel) &&
                MacOSAppKit.RespondsToSelector(nsWindow, MacOSAppKit.SelWindow))
            {
                nsWindow = MacOSAppKit.ResolveNSWindow(handle.Handle);
            }

            if (nsWindow == IntPtr.Zero || !MacOSAppKit.EnhanceOverlayWindow(nsWindow))
            {
                Logger.Warn(
                    $"MacOSPlatformServices: 无法增强窗口层级 " +
                    $"(class={MacOSAppKit.GetObjectClassName(handle.Handle) ?? "<not-objc>"})");
                return;
            }

            var transparent = MacOSAppKit.IsWindowTransparent(nsWindow);
            Logger.Info(
                $"MacOSPlatformServices: NSWindow level={MacOSAppKit.GetWindowLevel(nsWindow)} " +
                $"collectionBehavior={MacOSAppKit.GetCollectionBehavior(nsWindow)} " +
                $"transparent={transparent}");

            if (!transparent)
            {
                Logger.Warn(
                    "MacOSPlatformServices: 窗口背景不透明（会显示为黑底）。" +
                    "检查 HudWindow.axaml 的 TransparencyLevelHint 是否被移除");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    public void ActivateForDialog()
    {
        try
        {
            MacOSAppKit.ActivateApp();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    // ---------------- 刘海安全区 ----------------

    /// <summary>安全区合理上限（DIP）：超出即认为 msgSend 结构体返回约定不符，放弃该读数。</summary>
    private const double MaxPlausibleSafeAreaTop = 200d;

    public double? TryGetScreenSafeAreaTop(double screenLeft, double screenTop)
    {
        try
        {
            var screens = MacOSAppKit.GetScreenGeometries();
            if (screens.Count == 0)
                return null;

            double primaryFrameTop = screens[0].FrameTop;

            foreach (var s in screens)
            {
                // 参数为目标屏幕左上角（pt），同时匹配两轴，区分并排显示器。
                bool matches = Math.Abs(s.FrameLeft - screenLeft) <= 2d &&
                    Math.Abs(primaryFrameTop - s.FrameTop - screenTop) <= 2d;

                if (!matches)
                    continue;

                if (!IsPlausible(s.SafeAreaTop))
                {
                    Logger.Warn($"MacOSPlatformServices: safeAreaInsets.top 读数不可信（{s.SafeAreaTop}），忽略");
                    return null;
                }

                return s.SafeAreaTop;
            }

            // 没匹配上：若只有一块屏幕，直接采信（多屏坐标换算出偏差时仍能工作）
            if (screens.Count == 1 && IsPlausible(screens[0].SafeAreaTop))
                return screens[0].SafeAreaTop;

            Logger.Warn(
                $"MacOSPlatformServices: 未匹配到 screenLeft={screenLeft} " +
                $"screenTop={screenTop} 对应的 NSScreen（screens={screens.Count}）");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return null;
        }
    }

    private static bool IsPlausible(double safeAreaTop)
        => safeAreaTop is >= 0d and <= MaxPlausibleSafeAreaTop;
}
