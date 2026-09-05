using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using PptConsole.Animations;
using PptConsole.Services;

namespace PptConsole.Views;

/// <summary>
/// 胶囊窗公共行为（静态辅助）：Avalonia 的 XAML 源生成器只对根元素为标准
/// Window/UserControl 的 .axaml 生成代码，因此不能用自建基类作根。
/// 公共逻辑统一放这里，具体胶囊窗 : Window 并调用。
/// </summary>
internal static class CapsuleBehavior
{
    /// <summary>统一窗口壳：无装饰 / 透明 / 置顶 / 不进任务栏 / 不抢焦点（NOACTIVATE）。</summary>
    public static void Init(Window window)
    {
        window.SystemDecorations = SystemDecorations.None;
        window.Background = Brushes.Transparent;
        window.Topmost = true;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Focusable = false;
        window.CanResize = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        window.Opened += (_, _) => ApplyNoActivate(window);
    }

    /// <summary>设置 NOACTIVATE：点击不抢放映焦点。</summary>
    public static void ApplyNoActivate(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        int ex = (int)Win32Interop.GetWindowLongPtr(handle, Win32Interop.GWL_EXSTYLE);
        Win32Interop.SetWindowLongPtr(handle, Win32Interop.GWL_EXSTYLE,
            new IntPtr(ex | Win32Interop.WS_EX_NOACTIVATE));
    }

    /// <summary>重新压回最顶（墨迹层切入交互态后调用）。</summary>
    public static void ReassertTopmost(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        Win32Interop.SetWindowPos(handle, Win32Interop.HWND_TOPMOST, 0, 0, 0, 0,
            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
    }

    /// <summary>在指定显示器上按 DIP 摆放（尺寸为 DIP，坐标为物理像素），底边贴 bottomMarginDip。</summary>
    public static void Place(Window window, Screen screen, double leftDip, double widthDip, double heightDip, double bottomMarginDip)
    {
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1d;

        window.Width = widthDip;
        window.Height = heightDip;

        int x = screen.Bounds.X + (int)Math.Round(leftDip * scaling);
        int y = screen.Bounds.Bottom
                - (int)Math.Round(bottomMarginDip * scaling)
                - (int)Math.Round(heightDip * scaling);

        window.Position = new PixelPoint(x, y);
    }

    public static void EnsureVisible(Window window)
    {
        if (!window.IsVisible) window.Show();
        ReassertTopmost(window);
    }

    public static void ResetPill(Border pill)
    {
        pill.Opacity = 0;
        pill.RenderTransform = new ScaleTransform(0.6, 0.6);
    }

    /// <summary>入场：胶囊缩放放大 + 淡入。</summary>
    public static async Task PlayEntranceAsync(Border pill, CancellationToken ct)
    {
        pill.Opacity = 0;
        pill.RenderTransform = new ScaleTransform(0.6, 0.6);

        try
        {
            await ConsoleAnimations.PillAppear().RunAsync(pill, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>出场：胶囊缩小淡出，结束后复位属性。</summary>
    public static async Task PlayHideAsync(Border pill, CancellationToken ct)
    {
        try
        {
            await ConsoleAnimations.PillHide().RunAsync(pill, ct);
        }
        catch (OperationCanceledException)
        {
        }

        if (!ct.IsCancellationRequested)
            ResetPill(pill);
    }
}