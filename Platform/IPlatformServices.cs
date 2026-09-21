using System;
using Avalonia.Controls;

namespace EndfieldCharge.Services;

/// <summary>
/// 登录时自动启动。
///
/// Windows：写 <c>HKCU\...\CurrentVersion\Run</c>（无需管理员）。
/// macOS  ：写 <c>~/Library/LaunchAgents/com.lenkmat.endfieldcharge.plist</c>，
///          需要 .app bundle 路径；非 bundle 运行（dotnet run）时 <see cref="IsSupported"/> 为 false。
/// </summary>
public interface IAutoStartManager
{
    /// <summary>当前运行环境是否支持开机自启（如 macOS 非 bundle 环境为 false）。</summary>
    bool IsSupported { get; }

    /// <summary>当前是否已启用。</summary>
    bool IsEnabled();

    /// <summary>启用。失败时静默，由调用方通过 <see cref="IsEnabled"/> 复核并回滚 UI 状态。</summary>
    void Enable();

    /// <summary>禁用。失败时静默。</summary>
    void Disable();
}

/// <summary>
/// 托盘图标工厂 + 平台行为钩子。
///
/// 托盘差异（重要）：
///   Windows —— Avalonia 会派发 <c>TrayIcon.Clicked</c>，本项目用自绘的
///              <c>TrayMenuWindow</c> 呈现深色菜单，由 <see cref="TrayMenuWindow"/> 承载。
///   macOS   —— Avalonia 的 <c>AvnTrayIcon</c> 只实现 <c>setMenu:</c>，<c>Clicked</c>
///              **永不触发**；必须把 <c>NativeMenu</c> 赋给 <c>TrayIcon.Menu</c>，
///              由 <c>NSStatusItem</c> 左键自动弹出。
/// </summary>
public interface IPlatformServices
{
    /// <summary>平台可读名（"Windows" / "macOS"）。</summary>
    string Name { get; }

    /// <summary>当前是否运行在完整应用包内（macOS: 位于 .app bundle；Windows: 恒为 true）。</summary>
    bool IsBundle { get; }

    /// <summary>省电模式在平台上的用户可见名称。</summary>
    string PowerSaveModeText { get; }

    /// <summary>创建电源/电池监听器。调用方负责 Start/Dispose。</summary>
    IPowerMonitor CreatePowerMonitor();

    /// <summary>开机自启管理器。</summary>
    IAutoStartManager AutoStart { get; }

    // ---------------- 托盘 ----------------

    /// <summary>
    /// 创建托盘图标并装配平台菜单。
    /// Windows：装配 Clicked 事件，菜单由调用方在点击时用 <see cref="TrayMenuWindow"/> 呈现。
    /// macOS  ：装配 NativeMenu，点击即弹系统菜单，回调 actions。
    /// </summary>
    TrayIcon CreateTrayIcon(TrayMenuActions actions);

    /// <summary>语言切换后刷新托盘菜单文案（macOS 需重建 NativeMenu）。</summary>
    void RefreshTrayLocalization();

    // ---------------- 窗口与激活 ----------------

    /// <summary>应用启动最早时机调用：设定应用激活策略（macOS: Accessory，不占 Dock）。</summary>
    void ApplyAppActivationPolicy();

    /// <summary>HUD 窗口首次显示后调用：平台窗口层级/集合行为增强（macOS: NSWindow level + collectionBehavior）。</summary>
    void OnHudWindowShown(Window window);

    /// <summary>对话框/设置窗口显示前调用：确保应用被激活到前台（macOS Accessory 策略下必需）。</summary>
    void ActivateForDialog();

    // ---------------- 刘海安全区（macOS） ----------------

    /// <summary>
    /// 取指定屏幕的刘海安全区上边距（DIP，即 <c>NSScreen.safeAreaInsets.top</c>）。
    /// 平台不支持 / 非刘海屏 / 该屏幕无安全区时返回 null。
    ///
    /// screenLeft / screenTop 为目标屏幕在 macOS 全局坐标中的左上角（pt）。
    /// 同时匹配两轴，避免并排屏幕共享工作区顶部时误用另一块屏幕的刘海。
    /// </summary>
    double? TryGetScreenSafeAreaTop(double screenLeft, double screenTop);
}

/// <summary>托盘菜单四个动作。平台实现只负责"呈现与派发"，动作内容由 App 提供。</summary>
public sealed record TrayMenuActions(
    Action OnPreview,
    Action OnSettings,
    Action OnCheckUpdate,
    Action OnExit);
