using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EndfieldCharge.Services;

/// <summary>
/// AppKit / Objective-C 运行时互操作（仅 macOS 目标编译）。
///
/// 设计纪律（重要）：
///   1. **所有**选择子调用前必须先 <see cref="RespondsToSelector"/> 守卫。
///      macOS 会静默移除/改名选择子，裸调会抛 NSInvalidArgumentException，
///      该异常无法被 .NET 捕获 —— 进程直接终止。
///      实证：macOS 27 上 <c>NSProcessInfo.lowPowerModeEnabled</c> 已不存在，
///      仅 <c>isLowPowerModeEnabled</c> 可用。
///   2. <c>objc_msgSend</c> 必须按返回类型选择正确的 P/Invoke 签名；
///      用错签名不会报错，只会读到垃圾值（值在错误的寄存器里）。
/// </summary>
internal static class MacOSAppKit
{
    private const string LibObjC = "/usr/lib/libobjc.dylib";

    /// <summary>
    /// AppKit 是弱链接的框架：在只走 <c>--selftest</c>（不创建任何窗口）的路径上，
    /// <c>objc_getClass("NSScreen")</c> 等会返回 NULL —— 实测此时只有 Foundation 已加载。
    /// 因此凡是要按类名取 AppKit 类的地方，先调用本方法显式 dlopen 一次。
    /// 返回 false 表示 AppKit 不可用（调用方应安全降级）。
    /// </summary>
    private static bool EnsureAppKitLoaded() => AppKitHandle != IntPtr.Zero;

    private const string LibAppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    [DllImport("libdl.dylib", EntryPoint = "dlopen")]
    private static extern IntPtr NativeDlOpen(string path, int mode);

    private const int RtldNow = 2;
    private const int RtldLazy = 1;

    private static readonly IntPtr AppKitHandle = NativeDlOpen(LibAppKit, RtldNow | RtldLazy);

    /// <summary>
    /// 按类名取 Objective-C 类，必要时先加载 AppKit。
    /// </summary>
    private static IntPtr GetAppKitClass(string name)
    {
        EnsureAppKitLoaded();
        return GetClassRaw(name);
    }

    // ---------------- 运行时基础 ----------------

    [DllImport(LibObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClassRaw(string name);

    [DllImport(LibObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRaw(string name);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_Utf8(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_Long(IntPtr receiver, IntPtr selector, long arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_VoidLong(IntPtr receiver, IntPtr selector, long arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_Void(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_VoidInt(IntPtr receiver, IntPtr selector, long arg);

    /// <summary>Objective-C BOOL 在 64 位平台上为 signed char，故按 byte 读取。</summary>
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern byte MsgSend_Byte(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern byte MsgSend_ByteSel(IntPtr receiver, IntPtr selector, IntPtr sel);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern long MsgSend_RetLong(IntPtr receiver, IntPtr selector);

    /// <summary>
    /// 返回 4 个 double 的结构体（<c>NSEdgeInsets</c> / <c>NSRect</c>）。
    /// arm64 使用 objc_msgSend；Intel 的 32 字节结构体必须使用 objc_msgSend_stret。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FourDoubles
    {
        public double A;
        public double B;
        public double C;
        public double D;
    }

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern FourDoubles MsgSend_FourDoublesArm64(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend_stret")]
    private static extern void MsgSend_FourDoublesX64(out FourDoubles result, IntPtr receiver, IntPtr selector);

    private static FourDoubles MsgSend_RetFourDoubles(IntPtr receiver, IntPtr selector)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return MsgSend_FourDoublesArm64(receiver, selector);
        MsgSend_FourDoublesX64(out var result, receiver, selector);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public double X; public double Y; }

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_Point(IntPtr receiver, IntPtr selector, NativePoint point);

    /// <summary>绕过 Avalonia 初次显示时的工作区约束，让透明 HUD 覆盖刘海。</summary>
    public static void PositionOverlay(IntPtr window, double left, double top)
    {
        var selector = SelRaw("setFrameTopLeftPoint:");
        var primaryHeight = GetPrimaryFrameHeight();
        if (primaryHeight is null || !RespondsToSelector(window, selector)) return;
        EnhanceOverlayWindow(window);
        MsgSend_Point(window, selector, new NativePoint { X = left, Y = primaryHeight.Value - top });
    }

    // ---------------- 缓存的选择子 ----------------

    private static readonly IntPtr SelRespondsToSelector = SelRaw("respondsToSelector:");
    private static readonly IntPtr SelUTF8String = SelRaw("UTF8String");
    private static readonly IntPtr SelSharedApplication = SelRaw("sharedApplication");
    private static readonly IntPtr SelProcessInfo = SelRaw("processInfo");
    private static readonly IntPtr SelIsLowPowerModeEnabled = SelRaw("isLowPowerModeEnabled");
    private static readonly IntPtr SelSetActivationPolicy = SelRaw("setActivationPolicy:");
    private static readonly IntPtr SelMainBundle = SelRaw("mainBundle");
    private static readonly IntPtr SelBundlePath = SelRaw("bundlePath");
    private static readonly IntPtr SelBundleIdentifier = SelRaw("bundleIdentifier");
    private static readonly IntPtr SelCollectionBehavior = SelRaw("collectionBehavior");
    private static readonly IntPtr SelSetCollectionBehavior = SelRaw("setCollectionBehavior:");
    public static readonly IntPtr SelSetLevel = SelRaw("setLevel:");
    private static readonly IntPtr SelActivate = SelRaw("activate");
    private static readonly IntPtr SelActivateIgnoringOtherApps = SelRaw("activateIgnoringOtherApps:");
    /// <summary>NSView.window / NSWindow 自身不会响应，用于区分句柄类型。</summary>
    public static readonly IntPtr SelWindow = SelRaw("window");
    private static readonly IntPtr SelSuperview = SelRaw("superview");
    private static readonly IntPtr SelBackgroundColor = SelRaw("backgroundColor");
    private static readonly IntPtr SelClearColor = SelRaw("clearColor");
    private static readonly IntPtr SelScreens = SelRaw("screens");
    private static readonly IntPtr SelSafeAreaInsets = SelRaw("safeAreaInsets");
    private static readonly IntPtr SelVisibleFrame = SelRaw("visibleFrame");
    private static readonly IntPtr SelFrame = SelRaw("frame");
    private static readonly IntPtr SelWindowNumber = SelRaw("windowNumber");
    private static readonly IntPtr SelObjectAtIndex = SelRaw("objectAtIndex:");
    private static readonly IntPtr SelCount = SelRaw("count");

    /// <summary>Objective-C 发送消息前检查目标是否响应选择子（防止未捕获的 NSInvalidArgumentException）。</summary>
    public static bool RespondsToSelector(IntPtr obj, IntPtr selector)
        => obj != IntPtr.Zero && MsgSend_ByteSel(obj, SelRespondsToSelector, selector) != 0;

    /// <summary>
    /// 诊断用：判断给定指针是否指向一个可安全发送消息的 Objective-C 对象。
    /// 非 ObjC 指针（例如 Avalonia 的 MicroCom 代理）调用 <c>object_getClassName</c> 会崩溃。
    /// </summary>
    public static bool IsKnownObjectClass(IntPtr obj)
    {
        if (obj == IntPtr.Zero)
            return false;

        try
        {
            return ObjectGetClassName(obj) != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>诊断用：返回对象的 Objective-C 类名。</summary>
    public static string? GetObjectClassName(IntPtr obj)
    {
        if (obj == IntPtr.Zero)
            return null;

        try
        {
            var cls = ObjectGetClassName(obj);
            return cls == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(cls);
        }
        catch
        {
            return null;
        }
    }

    [DllImport(LibObjC, EntryPoint = "object_getClassName")]
    private static extern IntPtr ObjectGetClassName(IntPtr obj);

    private static IntPtr Send(IntPtr obj, IntPtr selector, bool guarded = true)
    {
        if (obj == IntPtr.Zero)
            return IntPtr.Zero;
        if (guarded && !RespondsToSelector(obj, selector))
            return IntPtr.Zero;
        return MsgSend(obj, selector);
    }

    /// <summary>把 NSString 转成 .NET 字符串（NSUTF8StringEncoding）。</summary>
    public static string? ToString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero)
            return null;
        if (!RespondsToSelector(nsString, SelUTF8String))
            return null;
        var ptr = MsgSend(nsString, SelUTF8String);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    // ---------------- NSApplication ----------------

    /// <summary>NSApplicationActivationPolicyAccessory —— 不占 Dock、不进 Cmd-Tab。</summary>
    public const long ActivationPolicyAccessory = 1;

    /// <summary>
    /// 设置应用激活策略为 Accessory。
    /// 理由：bundle 启动时 Info.plist 的 LSUIElement 已覆盖，但通过
    /// <c>dotnet run</c> / 直接执行 bundle 内可执行文件时不生效，需运行时兜底。
    /// </summary>
    public static bool ApplyAccessoryActivationPolicy()
    {
        var app = Send(GetAppKitClass("NSApplication"), SelSharedApplication);
        if (app == IntPtr.Zero || !RespondsToSelector(app, SelSetActivationPolicy))
            return false;

        MsgSend_VoidLong(app, SelSetActivationPolicy, ActivationPolicyAccessory);
        return true;
    }

    /// <summary>把应用激活到前台（Accessory 策略下打开设置窗/对话框前必需）。</summary>
    public static void ActivateApp()
    {
        var app = Send(GetAppKitClass("NSApplication"), SelSharedApplication);
        if (app == IntPtr.Zero)
            return;

        if (RespondsToSelector(app, SelActivateIgnoringOtherApps))
        {
            MsgSend_VoidInt(app, SelActivateIgnoringOtherApps, 1);
        }
        else if (RespondsToSelector(app, SelActivate))
        {
            MsgSend_Void(app, SelActivate);
        }
    }

    // ---------------- NSProcessInfo ----------------

    /// <summary>
    /// 读取低电量模式（Low Power Mode）是否开启。
    /// 优先 <c>isLowPowerModeEnabled</c>；该选择子不存在时返回 null，由调用方改用 pmset 兜底。
    /// **绝不**调用 <c>lowPowerModeEnabled</c>：macOS 27 上该选择子已被移除，调用会直接崩溃。
    /// </summary>
    public static bool? TryGetLowPowerMode()
    {
        var info = Send(GetAppKitClass("NSProcessInfo"), SelProcessInfo);
        if (info == IntPtr.Zero || !RespondsToSelector(info, SelIsLowPowerModeEnabled))
            return null;

        return MsgSend_Byte(info, SelIsLowPowerModeEnabled) != 0;
    }

    // ---------------- NSBundle ----------------

    /// <summary>当前是否运行在 .app bundle 内。</summary>
    public static bool IsInAppBundle => BundlePath() is { } p
        && p.EndsWith(".app", StringComparison.OrdinalIgnoreCase);

    /// <summary>NSBundle.mainBundle.bundlePath；非 bundle 运行时为可执行文件所在目录。</summary>
    public static string? BundlePath()
    {
        var bundle = Send(GetAppKitClass("NSBundle"), SelMainBundle);
        if (bundle == IntPtr.Zero)
            return null;
        return ToString(Send(bundle, SelBundlePath));
    }

    /// <summary>CFBundleIdentifier；无 bundle 时为 null。</summary>
    public static string? BundleIdentifier()
    {
        var bundle = Send(GetAppKitClass("NSBundle"), SelMainBundle);
        if (bundle == IntPtr.Zero)
            return null;
        return ToString(Send(bundle, SelBundleIdentifier));
    }

    // ---------------- NSWindow ----------------

    /// <summary>NSPopUpMenuWindowLevel —— 高于菜单栏(24)与浮动窗口(3)，能盖住全屏应用。</summary>
    public const long WindowLevelPopUpMenu = 101;

    /// <summary>NSWindowCollectionBehavior 位标志。</summary>
    public const long CollectionBehaviorCanJoinAllSpaces = 1 << 0;   // 1

    public const long CollectionBehaviorStationary = 1 << 4;         // 16
    public const long CollectionBehaviorFullScreenAuxiliary = 1 << 8; // 256

    /// <summary>
    /// 增强 HUD 窗口：提升窗口层级到弹窗级，并允许加入所有 Space / 全屏辅助显示。
    /// Avalonia 的 Topmost 只设置 NSFloatingWindowLevel(3)，低于菜单栏(24)，
    /// 因此必须显式提升；collectionBehavior 默认为 128（FullScreenPrimary），
    /// 不会跨 Space 显示。
    /// </summary>
    public static bool EnhanceOverlayWindow(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero)
            return false;

        var ok = false;

        if (RespondsToSelector(nsWindow, SelSetLevel))
        {
            MsgSend_VoidLong(nsWindow, SelSetLevel, WindowLevelPopUpMenu);
            ok = true;
        }

        if (RespondsToSelector(nsWindow, SelSetCollectionBehavior))
        {
            var behavior = CollectionBehaviorCanJoinAllSpaces
                         | CollectionBehaviorStationary
                         | CollectionBehaviorFullScreenAuxiliary;
            MsgSend_VoidLong(nsWindow, SelSetCollectionBehavior, behavior);
            ok = true;
        }

        return ok;
    }

    /// <summary>
    /// 从 NSView 句柄沿 superview 链向上找到 NSWindow。
    ///
    /// 当前 Avalonia 11.2.1 的 macOS 后端直接给出 `AvnWindow`（NSWindow 子类），
    /// 因此正常路径不需要本方法；保留它用于兼容"句柄改为 NSView"的将来版本。
    /// </summary>
    public static IntPtr ResolveNSWindow(IntPtr nsView)
    {
        if (nsView == IntPtr.Zero)
            return IntPtr.Zero;

        // NSView.window 优先（最直接）
        if (RespondsToSelector(nsView, SelWindow))
        {
            var w = MsgSend(nsView, SelWindow);
            if (w != IntPtr.Zero)
                return w;
        }

        // 兜底：沿 superview 链向上找带 window 的视图
        var current = nsView;
        for (int i = 0; i < 64 && current != IntPtr.Zero; i++)
        {
            if (RespondsToSelector(current, SelWindow))
            {
                var w = MsgSend(current, SelWindow);
                if (w != IntPtr.Zero)
                    return w;
            }

            if (!RespondsToSelector(current, SelSuperview))
                break;
            current = MsgSend(current, SelSuperview);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 仅用于诊断：窗口是否透出桌面。
    /// 判据是 <c>backgroundColor == [NSColor clearColor]</c> —— Avalonia 的 macOS 后端
    /// 依据 <c>TransparencyLevelHint</c> 调 <c>setBackgroundColor:</c>，
    /// 未声明该属性时会得到 <c>[NSColor windowBackgroundColor]</c>（不透明系统底色，
    /// 表现为整块黑底）。
    /// </summary>
    public static bool IsWindowTransparent(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero || !RespondsToSelector(nsWindow, SelBackgroundColor))
            return false;

        var bg = MsgSend(nsWindow, SelBackgroundColor);
        var clear = Send(GetAppKitClass("NSColor"), SelClearColor);
        return bg != IntPtr.Zero && bg == clear;
    }

    // ---------------- NSScreen 刘海安全区 ----------------

    /// <summary>单个 NSScreen 的关键几何。</summary>
    public readonly record struct ScreenGeometry(
        double SafeAreaTop,
        double FrameTop,
        double VisibleFrameTop,
        double FrameLeft);

    /// <summary>
    /// 枚举所有 NSScreen 的几何信息（DIP）。
    ///
    /// Cocoa 与 Avalonia 的全局 Y 轴满足恒等关系：
    ///   cocoaY = primaryFrameTop − avaloniaY
    /// 其中 <c>primaryFrameTop</c> 是主屏 frame 的高度（其 cocoa 原点为 (0,0)）。
    /// 因此
    ///   Avalonia WorkingArea.Y       ↔  cocoa primaryFrameTop − visibleFrameTop
    ///   Avalonia WorkingArea.Bottom  ↔  cocoa primaryFrameTop − visibleFrame.origin.y
    /// 这正是 <c>HudWindow</c> 用来挑选目标 NSScreen 的两个匹配键。
    ///
    /// <c>safeAreaInsets</c> 需 <c>respondsToSelector:</c> 守卫（旧系统无此 API）；
    /// 同样，若 <c>msgSend</c> 的结构体返回约定与预期不符，读到的小数会被上层校验拦掉并回退。
    /// </summary>
    public static IReadOnlyList<ScreenGeometry> GetScreenGeometries()
    {
        var result = new List<ScreenGeometry>();

        // 必须用 **类方法** [NSScreen screens]，不能用 [NSApp screens]：
        // Avalonia 的 NSApplication 也不是原生的 —— 它替换成了自己的 AvnApplication，
        // 实测该实例既不响应 screens 也会在调用时抛 NSInvalidArgumentException。
        // （NSScreen 的类方法则是标准 AppKit API，respondsToSelector 返回 true。）
        var nsScreenClass = GetAppKitClass("NSScreen");
        if (!RespondsToSelector(nsScreenClass, SelScreens))
            return result;

        var screens = MsgSend(nsScreenClass, SelScreens);
        if (screens == IntPtr.Zero)
            return result;

        // NSArray 用 objectAtIndex: / count 取值
        var countSel = SelCount;
        var objectAtIndexSel = SelObjectAtIndex;
        if (!RespondsToSelector(screens, countSel) || !RespondsToSelector(screens, objectAtIndexSel))
            return result;

        long count = MsgSend_RetLong(screens, countSel);

        for (long i = 0; i < count && i < 16; i++)
        {
            var screen = MsgSend_Long(screens, objectAtIndexSel, i);
            if (screen == IntPtr.Zero)
                continue;

            double safeTop = 0d;
            if (RespondsToSelector(screen, SelSafeAreaInsets))
            {
                var insets = MsgSend_RetFourDoubles(screen, SelSafeAreaInsets);
                safeTop = insets.A;   // NSEdgeInsets { top, left, bottom, right }
            }

            var frame = RespondsToSelector(screen, SelFrame)
                ? MsgSend_RetFourDoubles(screen, SelFrame)
                : default;

            var visible = RespondsToSelector(screen, SelVisibleFrame)
                ? MsgSend_RetFourDoubles(screen, SelVisibleFrame)
                : default;

            result.Add(new ScreenGeometry(
                SafeAreaTop: safeTop,
                FrameTop: frame.B + frame.D,             // origin.y + size.height
                VisibleFrameTop: visible.B + visible.D,
                FrameLeft: frame.A));
        }

        return result;
    }

    /// <summary>主屏 frame 高度（用于 cocoaY ↔ avaloniaY 换算）。取不到时返回 null。</summary>
    public static double? GetPrimaryFrameHeight()
    {
        var list = GetScreenGeometries();
        return list.Count > 0 ? list[0].FrameTop : null;
    }

    /// <summary>仅用于诊断：返回窗口层级（失败返回 long.MinValue）。</summary>
    public static long GetWindowLevel(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero || !RespondsToSelector(nsWindow, SelRaw("level")))
            return long.MinValue;
        return MsgSend_RetLong(nsWindow, SelRaw("level"));
    }

    /// <summary>仅用于诊断：返回集合行为位标志。</summary>
    public static long GetCollectionBehavior(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero || !RespondsToSelector(nsWindow, SelCollectionBehavior))
            return long.MinValue;
        return MsgSend_RetLong(nsWindow, SelCollectionBehavior);
    }
}
