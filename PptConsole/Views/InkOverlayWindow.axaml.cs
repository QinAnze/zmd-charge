using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using PptConsole.Services;
using Path = Avalonia.Controls.Shapes.Path;

namespace PptConsole.Views;

/// <summary>
/// 全屏自绘墨迹层（方案 B：墨迹不写入 PPT 文件，颜色/粗细/撤销全在自己手里）。
///
/// 穿透切换：SetPassthrough(true) 加 WS_EX_TRANSPARENT，触控直达放映层；
/// 交互态去掉该样式接管触控。两种状态都保持 WS_EX_NOACTIVATE，不抢放映焦点。
///
/// 手掌误触防护：同一时刻只跟踪第一个按下的指针（Pen/Touch/Mouse 均可作画，
/// 其余触点全部忽略——触屏上自然形成"单指/单笔作画"）。
///
/// 按页记忆：_pages[页码]，页码由控制台翻页事件驱动（MVP 内部计数，
/// 不感知键盘/遥控翻页——COM 接入 SlideShowNextSlide 事件后消除该盲区）。
/// </summary>
public partial class InkOverlayWindow : Window
{
    /// <summary>相邻采样点最小位移（DIP），低于不加点（120Hz 触摸事件限流）。</summary>
    private const double EpsilonDip = 1.5;

    private sealed class StrokeRecord
    {
        public required List<Point> Points { get; init; }
        public Color Color { get; init; }
        public double Thickness { get; init; }
        public Rect Bounds { get; init; }
    }

    private readonly Dictionary<int, List<StrokeRecord>> _pages = new();
    private int _currentPage = 1;

    // 当前工具属性（由控制台面板驱动）
    private Color _penColor = Color.Parse("#C6CA4C");
    private double _penThickness = 3.5;
    private double _eraserRadius = 28;

    private bool _interactive;
    private bool _passthrough = true;
    private ConsoleTool _toolMode = ConsoleTool.Select;

    // 活动笔画
    private IPointer? _activePointer;
    private List<Point>? _livePoints;
    private Path? _livePath;
    private double _pressureSum;
    private int _pressureCount;

    // 橡皮指示圈
    private Ellipse? _eraserCursor;

    public InkOverlayWindow()
    {
        InitializeComponent();

        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerCaptureLost += OnPointerCaptureLost;

        Opened += (_, _) => ApplyWindowExStyles();
    }

    // ---------------- 生命周期 ----------------

    /// <summary>铺满指定显示器并显示（放映开始时由 App 调用）。</summary>
    public void AttachTo(Screen screen)
    {
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1d;

        Width = screen.Bounds.Width / scaling;
        Height = screen.Bounds.Height / scaling;

        Position = screen.Bounds.Position;
        if (!IsVisible)
            Show();
        Position = screen.Bounds.Position;

        ApplyWindowExStyles();
    }

    /// <summary>隐藏墨迹层（墨迹内容保留，下次吊起原样回来）。</summary>
    public void Detach() => Hide();

    // ---------------- 穿透切换 ----------------

    /// <summary>true = 点击穿透（选择态）；false = 接管触控（笔/橡皮态）。</summary>
    public void SetPassthrough(bool pass)
    {
        _passthrough = pass;
        _interactive = !pass;
        ApplyWindowExStyles();

        if (pass)
            HideEraserCursor();
    }

    /// <summary>当前工具模式（笔/橡皮），由 App 在工具切换时设置。</summary>
    public void SetToolMode(ConsoleTool tool)
    {
        _toolMode = tool;
        if (tool != ConsoleTool.Eraser)
            HideEraserCursor();
    }

    private void ApplyWindowExStyles()
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return;

        int ex = (int)Win32Interop.GetWindowLongPtr(handle, Win32Interop.GWL_EXSTYLE);
        ex |= Win32Interop.WS_EX_NOACTIVATE;                       // 永不抢焦点
        if (_passthrough)
            ex |= Win32Interop.WS_EX_TRANSPARENT;                  // 触控直达放映层
        else
            ex &= ~Win32Interop.WS_EX_TRANSPARENT;

        Win32Interop.SetWindowLongPtr(handle, Win32Interop.GWL_EXSTYLE, new IntPtr(ex));
    }

    // ---------------- 工具属性（控制台面板 → App 转发） ----------------

    public void SetPenSettings(Color color, double thickness)
    {
        _penColor = color;
        _penThickness = thickness;
    }

    public void SetEraserRadius(double radius) => _eraserRadius = radius;

    // ---------------- 页 / 撤销 / 清空 ----------------

    /// <summary>翻页时由控制台事件驱动（+1/-1）。</summary>
    public void NotifyPageChanged(int delta)
    {
        _currentPage = Math.Max(1, _currentPage + delta);
        RebuildCanvas();
    }

    public void Undo()
    {
        if (_pages.TryGetValue(_currentPage, out var strokes) && strokes.Count > 0)
        {
            strokes.RemoveAt(strokes.Count - 1);
            RebuildCanvas();
        }
    }

    public void ClearCurrentPage()
    {
        _pages.Remove(_currentPage);
        RebuildCanvas();
    }

    // ---------------- 指针处理 ----------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_interactive || _activePointer is not null)
            return; // 已有活动指针（手掌误触防护）或穿透态

        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        _activePointer = e.Pointer;
        e.Pointer.Capture(Root);

        if (_toolMode == ConsoleTool.Eraser)
        {
            ShowEraserCursor(point.Position);
            EraseAt(point.Position);
        }
        else
        {
            BeginStroke(point);
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_interactive || e.Pointer != _activePointer)
            return;

        var point = e.GetCurrentPoint(Root);

        if (_toolMode == ConsoleTool.Eraser)
        {
            MoveEraserCursor(point.Position);
            EraseAt(point.Position);
        }
        else
        {
            ExtendStroke(point);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer != _activePointer)
            return;

        EndStroke();
        HideEraserCursor();
        _activePointer = null;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_livePoints is not null)
            EndStroke();
        HideEraserCursor();
        _activePointer = null;
    }

    // ---------------- 笔迹绘制 ----------------

    private void BeginStroke(PointerPoint point)
    {
        _livePoints = new List<Point> { point.Position };
        _pressureSum = point.Properties.Pressure;
        _pressureCount = 1;

        _livePath = new Path
        {
            Stroke = new SolidColorBrush(_penColor),
            StrokeThickness = _penThickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        UpdateLiveGeometry();
        InkCanvas.Children.Add(_livePath);
    }

    private void ExtendStroke(PointerPoint point)
    {
        if (_livePoints is null || _livePath is null)
            return;

        var last = _livePoints[^1];
        if (Math.Abs(point.Position.X - last.X) < EpsilonDip &&
            Math.Abs(point.Position.Y - last.Y) < EpsilonDip)
            return;

        _livePoints.Add(point.Position);
        _pressureSum += point.Properties.Pressure;
        _pressureCount++;

        UpdateLiveGeometry();
    }

    private void EndStroke()
    {
        if (_livePoints is null)
            return;

        // 压感 → 笔画最终粗细（鼠标压感恒 0.5，恰好落在基线附近）
        double avg = _pressureCount > 0 ? _pressureSum / _pressureCount : 0.5;
        double thickness = _penThickness * Math.Clamp(0.55 + 0.9 * avg, 0.6, 2.0);

        if (_livePoints.Count >= 2)
        {
            var record = new StrokeRecord
            {
                Points = _livePoints,
                Color = _penColor,
                Thickness = thickness,
                Bounds = ComputeBounds(_livePoints),
            };

            if (!_pages.TryGetValue(_currentPage, out var list))
                _pages[_currentPage] = list = new List<StrokeRecord>();
            list.Add(record);

            // 用最终粗细重建（替换实时路径）
            if (_livePath is not null)
            {
                InkCanvas.Children.Remove(_livePath);
                InkCanvas.Children.Add(BuildPath(record));
            }
        }
        else if (_livePath is not null)
        {
            InkCanvas.Children.Remove(_livePath); // 单点丢弃
        }

        _livePoints = null;
        _livePath = null;
    }

    private void UpdateLiveGeometry()
    {
        if (_livePath is null || _livePoints is null || _livePoints.Count == 0)
            return;

        _livePath.Data = new PolylineGeometry(_livePoints, false);
    }

    private static Path BuildPath(StrokeRecord record) => new()
    {
        Data = new PolylineGeometry(record.Points, false),
        Stroke = new SolidColorBrush(record.Color),
        StrokeThickness = record.Thickness,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
    };

    private static Rect ComputeBounds(List<Point> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    // ---------------- 橡皮 ----------------

    /// <summary>笔画级擦除：橡皮圈碰到任意采样点 → 整笔移除。</summary>
    private void EraseAt(Point position)
    {
        if (!_pages.TryGetValue(_currentPage, out var strokes) || strokes.Count == 0)
            return;

        bool removed = false;
        for (int i = strokes.Count - 1; i >= 0; i--)
        {
            var s = strokes[i];
            var inflated = s.Bounds.Inflate(new Thickness(_eraserRadius));
            if (!inflated.Contains(position))
                continue;

            foreach (var p in s.Points)
            {
                double dx = p.X - position.X;
                double dy = p.Y - position.Y;
                if (dx * dx + dy * dy <= _eraserRadius * _eraserRadius)
                {
                    strokes.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
        }

        if (removed)
            RebuildCanvas();
    }

    private void RebuildCanvas()
    {
        InkCanvas.Children.Clear();

        if (!_pages.TryGetValue(_currentPage, out var strokes))
            return;

        foreach (var s in strokes)
            InkCanvas.Children.Add(BuildPath(s));
    }

    // ---------------- 橡皮指示圈 ----------------

    private void ShowEraserCursor(Point position)
    {
        if (_eraserCursor is null)
        {
            _eraserCursor = new Ellipse
            {
                Stroke = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)),
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            CursorCanvas.Children.Add(_eraserCursor);
        }

        double d = _eraserRadius * 2;
        _eraserCursor.Width = d;
        _eraserCursor.Height = d;
        Canvas.SetLeft(_eraserCursor, position.X - _eraserRadius);
        Canvas.SetTop(_eraserCursor, position.Y - _eraserRadius);
        _eraserCursor.IsVisible = true;
    }

    private void MoveEraserCursor(Point position)
    {
        if (_eraserCursor is { IsVisible: true })
        {
            Canvas.SetLeft(_eraserCursor, position.X - _eraserRadius);
            Canvas.SetTop(_eraserCursor, position.Y - _eraserRadius);
        }
    }

    private void HideEraserCursor()
    {
        if (_eraserCursor is not null)
            _eraserCursor.IsVisible = false;
    }
}
