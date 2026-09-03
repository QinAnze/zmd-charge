using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace EndfieldCharge.Views;

public partial class TrayMenuWindow : Window
{
    public event Action? PreviewClicked;
    public event Action? SettingsClicked;
    public event Action? CheckUpdateClicked;
    public event Action? PreviewToolClicked;
    public event Action? ExitClicked;

    public TrayMenuWindow()
    {
        InitializeComponent();

        // 窗口图标
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://EndfieldCharge/Assets/tray_bolt.png"));
            Icon = new WindowIcon(new Bitmap(stream));
        }
        catch { }

        // 失焦自动关闭
        Deactivated += (_, _) => Close();

        // 每个菜单项的 hover 和点击
        SetupMenuItem(MenuPreview, MenuPreviewText, () => PreviewClicked?.Invoke());
        SetupMenuItem(MenuSettings, MenuSettingsText, () => SettingsClicked?.Invoke());
        SetupMenuItem(MenuCheckUpdate, MenuCheckUpdateText, () => CheckUpdateClicked?.Invoke());
        SetupMenuItem(MenuPreviewTool, MenuPreviewToolText, () => PreviewToolClicked?.Invoke());
        SetupMenuItem(MenuExit, MenuExitText, () => ExitClicked?.Invoke());
    }

    private static void SetupMenuItem(Border border, TextBlock text, Action onClick)
    {
        var normalBg = Brushes.Transparent;
        var hoverBg = new SolidColorBrush(Color.Parse("#2A2A2D"));
        var normalFg = new SolidColorBrush(Color.Parse("#E8E8E8"));
        var hoverFg = new SolidColorBrush(Color.Parse("#FFFFFF"));

        border.PointerEntered += (_, _) =>
        {
            border.Background = hoverBg;
            text.Foreground = hoverFg;
        };
        border.PointerExited += (_, _) =>
        {
            border.Background = normalBg;
            text.Foreground = normalFg;
        };
        border.PointerPressed += (_, _) => onClick();
    }

    // ---------------- 定位：菜单出现在托盘图标上方 ----------------

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Win32Point lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    /// <summary>在托盘图标上方显示菜单。</summary>
    public void ShowAtTray()
    {
        if (Screens.Primary is { } screen)
        {
            double scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
            var wa = screen.WorkingArea;

            int winW = (int)Math.Round(Width * scaling);
            int winH = (int)Math.Round(Height * scaling);

            int x, y;
            // 点击托盘图标时光标正好停在图标上：菜单右缘对齐光标、底缘贴任务栏上方。
            // 光标位置与 WorkingArea 同为物理像素，天然规避缩放换算问题。
            if (GetCursorPos(out var pt))
            {
                x = pt.X - winW;
                y = wa.Bottom - winH - 8;
            }
            else
            {
                x = wa.Right - winW - 8;
                y = wa.Bottom - winH - 8;
            }

            // 夹紧，保证菜单完整落在工作区内
            x = Math.Clamp(x, wa.X + 8, Math.Max(wa.X + 8, wa.Right - winW - 8));
            y = Math.Clamp(y, wa.Y + 8, Math.Max(wa.Y + 8, wa.Bottom - winH - 8));

            Position = new PixelPoint(x, y);
            Services.Logger.Info(
                $"TrayMenu: cursor=({pt.X},{pt.Y}) winPx={winW}x{winH} wa={wa} pos=({x},{y})");
        }

        Show();
        Focus();
    }
}