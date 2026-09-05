using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using PptConsole.Animations;
using PptConsole.Services;

namespace PptConsole.Views;

/// <summary>
/// 底部控制条：三颗胶囊吊在放映屏底边上方 28px。
/// 左短胶囊 = 列表 + 上一页；右短胶囊 = 下一页 + 列表（对称）；
/// 中长胶囊 = 笔 / 选择 / 橡皮，选中笔或橡皮时向上撑出工具面板。
///
/// 动画打断模式与原 HudWindow 相同：每次新动画先 _cts.Cancel()，
/// 取消后必须显式复位属性（ResetXxx），不依赖取消后的残留状态。
/// </summary>
public partial class ConsoleWindow : Window
{
    // ---------------- 度量（与 ConsoleTheme.axaml 对应） ----------------
    private const double StripHeight = 204;      // 吊条窗口高度（面板104+胶囊56+底距28+余量）
    private const double PanelHeight = 104;      // 工具面板撑高目标
    private const double PillRadius = 28;        // 胶囊全圆半径
    private const double PanelRadius = 18;       // 面板底座圆角（= 原 PillRadiusB）

    private static readonly Color[] PenColors =
    {
        Color.Parse("#C6CA4C"),   // 原项目唯一彩色：徽章黄绿（默认）
        Color.Parse("#E03131"),
        Color.Parse("#38BDF8"),
        Color.Parse("#E9E7E4"),
    };
    private static readonly double[] PenThicknesses = { 2.0, 3.5, 5.0 };
    private static readonly double[] EraserRadii = { 16, 28, 44 };

    // ---------------- 对外事件 ----------------
    public event Action? PrevRequested;
    public event Action? NextRequested;
    public event Action? ListRequested;
    public event Action? InkUndo;
    public event Action? InkCleared;
    public event Action<ConsoleTool>? ToolChanged;
    public event Action<Color, double>? PenSettingsChanged;
    public event Action<double>? EraserSettingsChanged;

    // ---------------- 状态 ----------------
    private ConsoleTool _tool = ConsoleTool.Select;
    private bool _panelOpen;

    private int _penColorIndex;
    private int _penSizeIndex;
    private int _eraserSizeIndex = 1;

    private CancellationTokenSource? _showCts;   // 吊起/收回（整组胶囊）
    private CancellationTokenSource? _panelCts;  // 面板撑高/收回/内容切换

    public ConsoleWindow()
    {
        InitializeComponent();

        BindTap(PrevZone, PrevCanvas, () => PrevRequested?.Invoke());
        BindTap(NextZone, NextCanvas, () => NextRequested?.Invoke());
        BindTap(ListZoneL, ListCanvasL, OnListClicked);
        BindTap(ListZoneR, ListCanvasR, OnListClicked);

        BindTap(PenZone, PenCanvas, () => OnToolTapped(ConsoleTool.Pen));
        BindTap(SelectZone, SelectCanvas, () => OnToolTapped(ConsoleTool.Select));
        BindTap(EraserZone, EraserCanvas, () => OnToolTapped(ConsoleTool.Eraser));

        BindPanelDots();
        BindPanelButtons();

        Opened += (_, _) =>
        {
            EnsureNoActivate();
            ReassertTopmost();
        };

        ResetPills();
        UpdateToolVisuals();
    }

    // ---------------- 吊起 / 收回 ----------------

    /// <summary>在指定显示器上吊起控制条（带入场动画；重复调用会重播）。</summary>
    public void ShowOn(Screen screen)
    {
        _showCts?.Cancel();
        _showCts?.Dispose();
        _showCts = new CancellationTokenSource();
        _panelCts?.Cancel();
        var ct = _showCts.Token;

        // 会话重置：工具回选择、面板瞬间收起
        _tool = ConsoleTool.Select;
        _panelOpen = false;
        UpdateToolVisuals();
        CollapsePanelInstant();
        ResetPills();

        PositionStrip(screen);
        if (!IsVisible)
            Show();
        PositionStrip(screen);
        Dispatcher.UIThread.Post(() => PositionStrip(screen), DispatcherPriority.Loaded);

        _ = PlayEntranceAsync(ct);
    }

    /// <summary>收回动画（三颗胶囊依次缩小淡出），完成后隐藏窗口。</summary>
    public async Task HideAnimatedAsync()
    {
        _showCts?.Cancel();
        _showCts?.Dispose();
        _showCts = new CancellationTokenSource();
        _panelCts?.Cancel();
        var ct = _showCts.Token;

        CollapsePanelInstant();

        try
        {
            await Task.WhenAll(
                ConsoleAnimations.PillHide().RunAsync(LeftPill, ct),
                ConsoleAnimations.PillHide().RunAsync(ToolPill, ct),
                ConsoleAnimations.PillHide().RunAsync(RightPill, ct));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ct.IsCancellationRequested && IsVisible)
            Hide();
    }

    /// <summary>把窗口重新压回 Topmost（墨迹层切入交互态后调用，保证控制条在其上）。</summary>
    public void ReassertTopmost()
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        Win32Interop.SetWindowPos(handle, Win32Interop.HWND_TOPMOST, 0, 0, 0, 0,
            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
    }

    private void EnsureNoActivate()
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        int ex = (int)Win32Interop.GetWindowLongPtr(handle, Win32Interop.GWL_EXSTYLE);
        Win32Interop.SetWindowLongPtr(handle, Win32Interop.GWL_EXSTYLE,
            new IntPtr(ex | Win32Interop.WS_EX_NOACTIVATE));
    }

    /// <summary>入场：左(0ms) → 中(80ms) → 右(160ms) 的吊起波。</summary>
    private async Task PlayEntranceAsync(CancellationToken ct)
    {
        var pills = new (Border pill, int delayMs)[]
        {
            (LeftPill, 0),
            (ToolPill, 80),
            (RightPill, 160),
        };

        try
        {
            await Task.WhenAll(pills.Select(async p =>
            {
                if (p.delayMs > 0)
                    await Task.Delay(p.delayMs, ct);
                await ConsoleAnimations.PillAppear().RunAsync(p.pill, ct);
            }));
        }
        catch (OperationCanceledException)
        {
            // 吊起被打断（重新吊起或收回）——属性由下一个动画或 ResetPills 负责
        }
    }

    private void ResetPills()
    {
        foreach (var pill in new[] { LeftPill, ToolPill, RightPill })
        {
            pill.Opacity = 0;
            pill.RenderTransform = new ScaleTransform(0.6, 0.6);
        }
        ToolPill.CornerRadius = new CornerRadius(PillRadius);
    }

    private void PositionStrip(Screen screen)
    {
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1d;

        Width = screen.Bounds.Width / scaling;
        Height = StripHeight;

        // 贴住放映屏底边
        int y = screen.Bounds.Bottom - (int)Math.Round(StripHeight * scaling);
        Position = new PixelPoint(screen.Bounds.X, y);
    }

    // ---------------- 工具切换 + 面板 ----------------

    private void OnToolTapped(ConsoleTool tool)
    {
        if (tool == _tool && tool != ConsoleTool.Select)
        {
            // 再点一次当前工具 = 开关面板
            _ = _panelOpen ? CollapsePanelAsync() : ExpandPanelAsync(tool);
            return;
        }

        _tool = tool;
        UpdateToolVisuals();
        ToolChanged?.Invoke(tool);

        if (tool == ConsoleTool.Select)
            _ = CollapsePanelAsync();
        else if (_panelOpen)
            _ = SwitchPanelContentAsync(tool);
        else
            _ = ExpandPanelAsync(tool);
    }

    private async Task ExpandPanelAsync(ConsoleTool tool)
    {
        _panelCts?.Cancel();
        _panelCts?.Dispose();
        _panelCts = new CancellationTokenSource();
        var ct = _panelCts.Token;

        ShowPanelFor(tool, resetRows: true);
        PanelHost.IsVisible = true;
        _panelOpen = true;

        var rows = RowsFor(tool);
        try
        {
            await Task.WhenAll(
                ConsoleAnimations.PanelExpand(PanelHeight).RunAsync(PanelHost, ct),
                ConsoleAnimations.PillCorner(PillRadius, PanelRadius, expand: true).RunAsync(ToolPill, ct),
                Task.WhenAll(rows.Select((r, i) => ConsoleAnimations.PanelRowIn(i).RunAsync(r, ct))));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CollapsePanelAsync()
    {
        if (!_panelOpen)
            return;

        _panelCts?.Cancel();
        _panelCts?.Dispose();
        _panelCts = new CancellationTokenSource();
        var ct = _panelCts.Token;

        var rows = RowsFor(_tool);
        try
        {
            await Task.WhenAll(
                ConsoleAnimations.PanelCollapse(PanelHeight).RunAsync(PanelHost, ct),
                ConsoleAnimations.PillCorner(PanelRadius, PillRadius, expand: false).RunAsync(ToolPill, ct),
                Task.WhenAll(rows.Select((r, i) => ConsoleAnimations.PanelRowOut(i, rows.Length).RunAsync(r, ct))));
        }
        catch (OperationCanceledException)
        {
        }

        if (!ct.IsCancellationRequested)
            CollapsePanelInstant();
    }

    /// <summary>面板已开时切换笔/橡皮内容：旧行错峰退场 → 新面板就位 → 新行错峰进场。</summary>
    private async Task SwitchPanelContentAsync(ConsoleTool tool)
    {
        _panelCts?.Cancel();
        _panelCts?.Dispose();
        _panelCts = new CancellationTokenSource();
        var ct = _panelCts.Token;

        var oldRows = RowsFor(tool == ConsoleTool.Pen ? ConsoleTool.Eraser : ConsoleTool.Pen);
        try
        {
            await Task.WhenAll(oldRows.Select((r, i) =>
                ConsoleAnimations.PanelRowOut(i, oldRows.Length).RunAsync(r, ct)));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        ShowPanelFor(tool, resetRows: true);

        var rows = RowsFor(tool);
        try
        {
            await Task.WhenAll(rows.Select((r, i) => ConsoleAnimations.PanelRowIn(i).RunAsync(r, ct)));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CollapsePanelInstant()
    {
        PanelHost.Height = 0;
        PanelHost.Opacity = 0;
        PanelHost.IsVisible = false;
        ToolPill.CornerRadius = new CornerRadius(PillRadius);
        _panelOpen = false;
    }

    private StackPanel[] RowsFor(ConsoleTool tool) => tool == ConsoleTool.Pen
        ? new[] { PenRowColor, PenRowSize }
        : new[] { EraserRowSize };

    private void ShowPanelFor(ConsoleTool tool, bool resetRows)
    {
        PenPanel.IsVisible = tool == ConsoleTool.Pen;
        EraserPanel.IsVisible = tool == ConsoleTool.Eraser;

        if (resetRows)
        {
            foreach (var row in RowsFor(tool))
                row.Opacity = 0;
        }
    }

    private void UpdateToolVisuals()
    {
        (PenPlate, PenIcon) = SetToolVisual(_tool == ConsoleTool.Pen, PenPlate, PenIcon);
        (SelectPlate, SelectIcon) = SetToolVisual(_tool == ConsoleTool.Select, SelectPlate, SelectIcon);
        (EraserPlate, EraserIcon) = SetToolVisual(_tool == ConsoleTool.Eraser, EraserPlate, EraserIcon);
    }

    private static (Border, PathIcon) SetToolVisual(bool active, Border plate, PathIcon icon)
    {
        plate.IsVisible = active;   // 激活：白圆底 + 深色图标（原 SquareForm 的方块形态）
        icon.IsVisible = !active;   // 未激活：白色描线图标
        return (plate, icon);
    }

    // ---------------- 面板控件 ----------------

    private void BindPanelDots()
    {
        var colors = new[] { PenColor0, PenColor1, PenColor2, PenColor3 };
        for (int i = 0; i < colors.Length; i++)
        {
            int idx = i;
            BindTap(colors[idx], null, () =>
            {
                _penColorIndex = idx;
                UpdateDotSelections();
                PenSettingsChanged?.Invoke(PenColors[_penColorIndex], PenThicknesses[_penSizeIndex]);
            });
        }

        var penSizes = new[] { PenSize0, PenSize1, PenSize2 };
        for (int i = 0; i < penSizes.Length; i++)
        {
            int idx = i;
            BindTap(penSizes[idx], null, () =>
            {
                _penSizeIndex = idx;
                UpdateDotSelections();
                PenSettingsChanged?.Invoke(PenColors[_penColorIndex], PenThicknesses[_penSizeIndex]);
            });
        }

        var eraserSizes = new[] { EraserSize0, EraserSize1, EraserSize2 };
        for (int i = 0; i < eraserSizes.Length; i++)
        {
            int idx = i;
            BindTap(eraserSizes[idx], null, () =>
            {
                _eraserSizeIndex = idx;
                UpdateDotSelections();
                EraserSettingsChanged?.Invoke(EraserRadii[_eraserSizeIndex]);
            });
        }
    }

    private void BindPanelButtons()
    {
        BindTap(PenUndoBtn, null, () => InkUndo?.Invoke());
        BindTap(PenClearBtn, null, () => InkCleared?.Invoke());
        BindTap(EraserUndoBtn, null, () => InkUndo?.Invoke());
        BindTap(EraserClearBtn, null, () => InkCleared?.Invoke());
    }

    private void UpdateDotSelections()
    {
        var colors = new[] { PenColor0, PenColor1, PenColor2, PenColor3 };
        for (int i = 0; i < colors.Length; i++)
            colors[i].BorderBrush = new SolidColorBrush(i == _penColorIndex ? Colors.White : Color.Parse("#00FFFFFF"));

        var penSizes = new[] { PenSize0, PenSize1, PenSize2 };
        for (int i = 0; i < penSizes.Length; i++)
            penSizes[i].BorderBrush = new SolidColorBrush(i == _penSizeIndex ? Colors.White : Color.Parse("#00FFFFFF"));

        var eraserSizes = new[] { EraserSize0, EraserSize1, EraserSize2 };
        for (int i = 0; i < eraserSizes.Length; i++)
            eraserSizes[i].BorderBrush = new SolidColorBrush(i == _eraserSizeIndex ? Colors.White : Color.Parse("#00FFFFFF"));
    }

    private void OnListClicked()
    {
        // TODO（COM 阶段）：打开页面列表面板（页数 / 缩略图 / 点击跳页）
        ListRequested?.Invoke();
    }

    // ---------------- 触控点按 + 波纹 ----------------

    /// <summary>
    /// 把一个 Border 变成触控区：点按触发动作 + 波纹反馈（点按位置扩散，ClipToBounds 裁剪）。
    /// 无 Canvas 时（面板小圆点）只触发动作不播波纹。
    /// </summary>
    private void BindTap(Border zone, Canvas? rippleHost, Action action)
    {
        zone.Cursor = new Cursor(StandardCursorType.Hand);
        zone.PointerPressed += (_, e) =>
        {
            if (rippleHost is not null)
                PlayRipple(rippleHost, e.GetPosition(zone));
            action();
        };
    }

    /// <summary>波纹：原 RippleInner 的"填充圆"形态，从点按位置扩散 500ms 后移除。</summary>
    private void PlayRipple(Canvas host, Point position)
    {
        const double size = 44d;

        var ellipse = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new SolidColorBrush(Color.Parse("#FF656363")),
            Opacity = 0,
            RenderTransform = new ScaleTransform(0, 0),
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(ellipse, position.X - size / 2);
        Canvas.SetTop(ellipse, position.Y - size / 2);
        host.Children.Add(ellipse);

        _ = RunRippleAsync(host, ellipse);
    }

    private static async Task RunRippleAsync(Canvas host, Ellipse ellipse)
    {
        try
        {
            await ConsoleAnimations.TapRipple(2.2, 0.45).RunAsync(ellipse);
        }
        catch
        {
            // 窗口关闭等场景，波纹自然消失
        }

        host.Children.Remove(ellipse);
    }
}
