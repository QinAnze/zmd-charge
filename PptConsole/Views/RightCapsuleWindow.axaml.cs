using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;

namespace PptConsole.Views;

public partial class RightCapsuleWindow : Window
{
    public event Action? NextRequested;
    public event Action? ListRequested;

    /// <summary>动画用胶囊内容。</summary>
    public Border Pod => Pill;

    public RightCapsuleWindow()
    {
        CapsuleBehavior.Init(this);
        InitializeComponent();

        BindTap(NextZone, NextCanvas, () => NextRequested?.Invoke());
        BindTap(ListZone, ListCanvas, () => ListRequested?.Invoke());
    }

    private void BindTap(Border zone, Canvas rippleHost, Action action)
    {
        zone.Cursor = new Cursor(StandardCursorType.Hand);
        zone.PointerPressed += (_, e) =>
        {
            PlayRipple(rippleHost, e.GetPosition(zone));
            action();
        };
    }

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

    private async System.Threading.Tasks.Task RunRippleAsync(Canvas host, Ellipse ellipse)
    {
        try
        {
            await Animations.ConsoleAnimations.TapRipple(2.2, 0.45).RunAsync(ellipse);
        }
        catch
        {
        }

        host.Children.Remove(ellipse);
    }
}