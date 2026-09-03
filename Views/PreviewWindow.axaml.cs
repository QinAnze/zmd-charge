using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EndfieldCharge.Services;

namespace EndfieldCharge.Views;

public partial class PreviewWindow : Window
{
    private readonly HudWindow _hud;

    public PreviewWindow(HudWindow hud)
    {
        InitializeComponent();
        _hud = hud;

        // 窗口图标
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://EndfieldCharge/Assets/tray_bolt.png"));
            Icon = new WindowIcon(new Bitmap(stream));
        }
        catch { }

        WinTitle.Text = Localization.PreviewTitle;
        LabelScale.Text = Localization.LabelPreviewScale;
        LabelDuration.Text = Localization.LabelPreviewDuration;
        LabelBounce.Text = Localization.LabelPreviewBounce;
        PlayBtn.Content = Localization.BtnPlay;
        CloseBtn.Content = Localization.BtnClose;

        PillScaleSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                PillScaleValue.Text = PillScaleSlider.Value.ToString("F2");
        };
        DurationSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                DurationValue.Text = $"{DurationSlider.Value:F1}s";
        };
        BounceSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                BounceValue.Text = BounceSlider.Value.ToString("F3");
        };
        HoldSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                HoldValue.Text = HoldSlider.Value.ToString("F2");
        };

        PlayBtn.Click += OnPlay;
        CloseBtn.Click += (_, _) => Close();
    }

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {
        PlayBtn.IsEnabled = false;

        var sample = new BatterySnapshot(
            RemainingWh: 62.4, FullWh: 90.0,
            Percent: 69, AcOnline: true, Charging: true);

        bool isUnplug = ModeCombo.SelectedIndex == 1;

        try
        {
            if (isUnplug)
                await _hud.ShowSimpleAsync(sample);
            else
                await _hud.ShowAndPlayAsync(sample, acOnline: true);
        }
        catch
        {
        }
        finally
        {
            PlayBtn.IsEnabled = true;
        }
    }
}