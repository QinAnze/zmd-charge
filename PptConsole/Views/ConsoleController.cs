using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Media;

namespace PptConsole.Views;

/// <summary>
/// 三窗口协调器：左右胶囊 + 中胶囊（胶囊+面板）分别独立成窗。
/// 负责在三颗胶囊各自命中区域安全的坐标上摆放、错峰入场（左→中→右）、出场。
///
/// 拆分收益：
///   - 胶囊之间与两侧的空白不属于任何控制窗 → 触控可直达放映层；
///   - 中窗口随面板开合自适应高度，不占无用空白。
/// </summary>
public sealed class ConsoleController
{
    private readonly LeftCapsuleWindow _left = new();
    private readonly CenterCapsuleWindow _center = new();
    private readonly RightCapsuleWindow _right = new();

    private Screen? _screen;
    private CancellationTokenSource? _showCts;
    private bool _shown;

    // 汇总事件（转发自各子窗口）
    public event Action? PrevRequested;
    public event Action? NextRequested;
    public event Action? ListRequested;
    public event Action<CenterCapsuleWindow.Tool>? ToolChanged;
    public event Action<Avalonia.Media.Color, double>? PenSettingsChanged;
    public event Action<double>? EraserSettingsChanged;
    public event Action? InkUndo;
    public event Action? InkCleared;

    public Screens Screens => _left.Screens;

    public ConsoleController()
    {
        _left.PrevRequested += () => PrevRequested?.Invoke();
        _left.ListRequested += () => ListRequested?.Invoke();
        _right.NextRequested += () => NextRequested?.Invoke();
        _right.ListRequested += () => ListRequested?.Invoke();

        _center.ToolChanged += t => ToolChanged?.Invoke(t);
        _center.PenSettingsChanged += (c, t) => PenSettingsChanged?.Invoke(c, t);
        _center.EraserSettingsChanged += r => EraserSettingsChanged?.Invoke(r);
        _center.InkUndo += () => InkUndo?.Invoke();
        _center.InkCleared += () => InkCleared?.Invoke();
    }

    public bool IsShown => _shown;

    /// <summary>在指定显示器吊起三颗胶囊（带错峰入场动画）。</summary>
    public void ShowOn(Screen screen)
    {
        _screen = screen;
        _center.RememberScreen(screen);

        _showCts?.Cancel();
        _showCts?.Dispose();
        _showCts = new CancellationTokenSource();
        var ct = _showCts.Token;

        // 会话重置：工具回选择、面板瞬间收起
        _center.ResetSession();
        (Window w, Border pod)[] pods =
        {
            (_left, _left.Pod),
            (_center, _center.Pod),
            (_right, _right.Pod),
        };
        foreach (var (w, pod) in pods)
        {
            CapsuleBehavior.ResetPill(pod);
            Place(w);
            CapsuleBehavior.EnsureVisible(w);
        }

        _shown = true;
        ReassertTopmost();
        _ = PlayEntranceAsync(ct);
    }

    /// <summary>出场动画（三颗胶囊依次缩小淡出），完成后隐藏全部子窗口。</summary>
    public async Task HideAsync()
    {
        _showCts?.Cancel();
        _showCts?.Dispose();
        _showCts = new CancellationTokenSource();
        var ct = _showCts.Token;

        _center.CollapsePanelFireForget();

        var pills = new (Border pod, int delay)[]
        {
            (_left.Pod, 0),
            (_center.Pod, 60),
            (_right.Pod, 120),
        };

        try
        {
            await Task.WhenAll(pills.Select(async p =>
            {
                if (p.delay > 0)
                    await Task.Delay(p.delay, ct);
                await CapsuleBehavior.PlayHideAsync(p.pod, ct);
            }));
        }
        catch (OperationCanceledException)
        {
        }

        if (!ct.IsCancellationRequested)
        {
            _left.Hide();
            _center.Hide();
            _right.Hide();
            _shown = false;
        }
    }

    /// <summary>把三个控制窗重新压回最顶（墨迹层切入交互态后调用）。</summary>
    public void ReassertTopmost()
    {
        CapsuleBehavior.ReassertTopmost(_left);
        CapsuleBehavior.ReassertTopmost(_center);
        CapsuleBehavior.ReassertTopmost(_right);
    }

    private async Task PlayEntranceAsync(CancellationToken ct)
    {
        var pills = new (Border pod, int delay)[]
        {
            (_left.Pod, 0),
            (_center.Pod, 80),
            (_right.Pod, 160),
        };

        try
        {
            await Task.WhenAll(pills.Select(async p =>
            {
                if (p.delay > 0)
                    await Task.Delay(p.delay, ct);
                await CapsuleBehavior.PlayEntranceAsync(p.pod, ct);
            }));
        }
        catch (OperationCanceledException)
        {
            // 入场被打断——由下一次 ShowOn/Hide 复位
        }
    }

    private void Place(Window window)
    {
        if (_screen is null)
            return;

        double scaling = _screen.Scaling > 0 ? _screen.Scaling : 1d;
        double screenWidthDip = _screen.Bounds.Width / scaling;

        if (window == _left)
        {
            CapsuleBehavior.Place(_left, _screen, ConsoleMetrics.SideMarginDip,
                ConsoleMetrics.SidePillWidth, ConsoleMetrics.SidePillHeight, ConsoleMetrics.BottomMarginDip);
        }
        else if (window == _right)
        {
            double leftDip = screenWidthDip - ConsoleMetrics.SideMarginDip - ConsoleMetrics.SidePillWidth;
            CapsuleBehavior.Place(_right, _screen, leftDip,
                ConsoleMetrics.SidePillWidth, ConsoleMetrics.SidePillHeight, ConsoleMetrics.BottomMarginDip);
        }
        else if (window == _center)
        {
            double leftDip = (screenWidthDip - ConsoleMetrics.ToolWidth) / 2;
            CapsuleBehavior.Place(_center, _screen, leftDip,
                ConsoleMetrics.ToolWidth, ConsoleMetrics.ToolHeight, ConsoleMetrics.BottomMarginDip);
        }
    }
}