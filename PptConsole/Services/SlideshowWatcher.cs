using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;

namespace PptConsole.Services;

/// <summary>
/// 轮询 PowerPoint 放映窗口（窗口类名 screenClass），
/// 报告放映开始/结束与其所在显示器的物理边界。
/// WPS 的放映窗口类名未验证，后续在 SlideClasses 里追加即可。
/// </summary>
internal sealed class SlideshowWatcher
{
    /// <summary>
    /// 放映窗口类名候选（MS PowerPoint = screenClass）。
    /// 以下 WPS 候选由公开资料推断，未在真机逐项验证——
    /// 冗余项只是让 FindWindow 多试一次，命中错类名会得到空句柄、无副作用。
    /// </summary>
    private static readonly string[] SlideClasses =
    {
        "screenClass",          // MS PowerPoint
        "wppslideshowwnd",      // WPS 演示（候选，未验证）
        "KWMainFrame",          // WPS 通用主窗（候选，未验证）
    };

    private const int PollMilliseconds = 1000;
    /// <summary>连续 N 次找不到才判定放映结束（防放映切换期的窗口抖动）。</summary>
    private const int EndConfirmCount = 2;

    private DispatcherTimer? _timer;
    private bool _running;
    private int _missCount;

    /// <summary>放映开始（参数 = 放映所在显示器物理边界）。</summary>
    public event Action<PixelRect>? SlideshowStarted;

    /// <summary>放映结束。</summary>
    public event Action? SlideshowEnded;

    public void Start()
    {
        if (_timer is not null) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollMilliseconds) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        IntPtr hwnd = Find();
        bool found = hwnd != IntPtr.Zero;

        if (found)
        {
            _missCount = 0;
            if (_running)
                return; // 仍在放映

            _running = true;
            SlideshowStarted?.Invoke(GetMonitorBounds(hwnd));
        }
        else if (_running)
        {
            if (++_missCount >= EndConfirmCount)
            {
                _running = false;
                _missCount = 0;
                SlideshowEnded?.Invoke();
            }
        }
    }

    private static IntPtr Find()
    {
        foreach (var cls in SlideClasses)
        {
            var hwnd = Win32Interop.FindWindow(cls, null);
            if (hwnd != IntPtr.Zero)
                return hwnd;
        }
        return IntPtr.Zero;
    }

    private static PixelRect GetMonitorBounds(IntPtr hwnd)
    {
        var monitor = Win32Interop.MonitorFromWindow(hwnd, Win32Interop.MONITOR_DEFAULTTONEAREST);

        var info = new Win32Interop.MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<Win32Interop.MONITORINFOEX>(),
        };

        if (monitor != IntPtr.Zero && Win32Interop.GetMonitorInfo(monitor, ref info))
        {
            return new PixelRect(
                info.rcMonitor.Left, info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top);
        }

        return default;
    }
}
