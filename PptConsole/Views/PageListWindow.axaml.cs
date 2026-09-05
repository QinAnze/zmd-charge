using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PptConsole.Services;

namespace PptConsole.Views;

/// <summary>
/// 页面列表面板：以网格缩略图列出所有页，当前页描边高亮，点击跳页。
/// 缩略图来自 COM 导出（ExportSlideImage）；生成失败或演示模式时退化为页码块。
/// </summary>
public partial class PageListWindow : Window
{
    private const double TileWidth = 110;
    private const double TileHeight = 88;
    private static readonly Color CurrentRing = Color.Parse("#C6CA4C");

    private readonly PptComBridge? _bridge;
    private readonly int _current;
    private readonly Action<int> _onJump;

    public PageListWindow(PptComBridge? bridge, int current, Action<int> onJump)
    {
        CapsuleBehavior.Init(this);
        InitializeComponent();

        _bridge = bridge;
        _current = current;
        _onJump = onJump;
    }

    /// <summary>重建页面网格。页码从 1 到 pageCount。</summary>
    public void Build(int pageCount)
    {
        Grid.Children.Clear();
        for (int i = 1; i <= pageCount; i++)
            AddTile(i);
    }

    /// <summary>摆放在指定显示器上、中胶囊正上方（水平居中，下缘留间隔）。</summary>
    public void Place(Screen screen)
    {
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        double screenWidthDip = screen.Bounds.Width / scaling;
        double screenHeightDip = screen.Bounds.Height / scaling;

        // 先测量内容实际高度（至少 64，最多 520，面板 MaxHeight 会截断滚动）
        Panel.Measure(new Size(400, 520));
        double contentH = Math.Max(64, Panel.DesiredSize.Height);
        double contentW = Math.Max(64, Panel.DesiredSize.Width);
        contentH = Math.Min(contentH, 520);
        contentW = Math.Min(contentW, 400);

        const double gapDip = 12; // 面板下缘与中胶囊顶的间隔
        double capsuleTopDip = screenHeightDip - ConsoleMetrics.BottomMarginDip - ConsoleMetrics.ToolHeight;
        double topDip = capsuleTopDip - gapDip - contentH;
        double leftDip = (screenWidthDip - contentW) / 2;

        Width = contentW;
        Height = contentH;

        int x = screen.Bounds.X + (int)Math.Round(leftDip * scaling);
        int y = screen.Bounds.Y + (int)Math.Round(topDip * scaling);
        Position = new PixelPoint(x, y);
    }

    private void AddTile(int index)
    {
        bool isCurrent = index == _current;

        var tile = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(isCurrent ? Color.Parse("#3B393A") : Color.Parse("#262425")),
            BorderBrush = new SolidColorBrush(isCurrent ? CurrentRing : Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(isCurrent ? 2 : 1),
            Margin = new Thickness(4),
            ClipToBounds = true,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        tile.PointerPressed += (_, _) => _onJump(index);

        var grid = new Grid();
        grid.Children.Add(new TextBlock
        {
            Text = index.ToString(),
            FontFamily = (FontFamily?)(this.FindResource("Hud.FontFamilyNumeric") ?? FontFamily.Default)!,
            FontSize = 26,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ZIndex = 2,
        });
        tile.Child = grid;
        Grid.Children.Add(tile);

        var tmp = _bridge?.ExportSlideImage(index, (int)TileWidth * 2, (int)TileHeight * 2);
        if (tmp is not null)
            _ = LoadThumbnailAsync(grid, tmp);
    }

    private static async Task LoadThumbnailAsync(Grid grid, string path)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch
        {
            TryDelete(path);
            return;
        }
        TryDelete(path);

        try
        {
            var bmp = new Bitmap(new MemoryStream(bytes));
            // 放到页码块之下：图片 + 页码叠加
            grid.Children.Insert(0, new Image
            {
                Source = bmp,
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false,
            });
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}