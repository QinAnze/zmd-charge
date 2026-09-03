using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace EndfieldCharge.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        // 窗口图标
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://EndfieldCharge/Assets/tray_bolt.png"));
            Icon = new WindowIcon(new Bitmap(stream));
        }
        catch { }

        Title = Localization.SettingsTitle;
        InitLanguageCombo();
        InitPositionCombo();
        ApplyLocalization();

        PopulateMonitors();
        LoadSettings(settings);

        // Tab 切换
        TabGeneralBtn.PointerPressed += (_, _) => SwitchTab(TabGeneralBtn, GeneralPanel);
        TabNotificationsBtn.PointerPressed += (_, _) => SwitchTab(TabNotificationsBtn, NotificationsPanel);
        TabAboutBtn.PointerPressed += (_, _) => SwitchTab(TabAboutBtn, AboutPanel);

        // 滑块值同步
        ScaleSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                ScaleValue.Text = ScaleSlider.Value.ToString("F2");
        };
        DurationSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                DurationValue.Text = DurationSlider.Value.ToString("F1");
        };
        LowBatterySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                LowBatteryValue.Text = $"{LowBatterySlider.Value:F0}%";
        };

        // 低电量开关联动
        LowBatterySwitch.IsCheckedChanged += (_, _) =>
        {
            LowBatterySlider.IsEnabled = LowBatterySwitch.IsChecked == true;
        };

        // 事件
        SaveBtn.Click += OnSave;
        CheckUpdateBtn.Click += OnCheckUpdate;

        // 默认 Tab
        SwitchTab(TabGeneralBtn, GeneralPanel);
    }

    // ---------------- 初始化 ComboBox 项 ----------------

    private void InitLanguageCombo()
    {
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem { Tag = "auto" });
        LanguageCombo.Items.Add(new ComboBoxItem { Tag = "zh" });
        LanguageCombo.Items.Add(new ComboBoxItem { Tag = "en" });
    }

    private void InitPositionCombo()
    {
        PositionCombo.Items.Clear();
        PositionCombo.Items.Add(new ComboBoxItem { Tag = "TopCenter" });
        PositionCombo.Items.Add(new ComboBoxItem { Tag = "TopRight" });
        PositionCombo.Items.Add(new ComboBoxItem { Tag = "TopLeft" });
    }

    // ---------------- 本地化 ----------------

    private void ApplyLocalization()
    {
        WinTitle.Text = Localization.SettingsTitle;
        TabGeneralText.Text = Localization.TabGeneral;
        TabNotificationsText.Text = Localization.TabNotifications;
        TabAboutText.Text = Localization.TabAbout;
        LabelScale.Text = Localization.LabelScale;
        LabelDuration.Text = Localization.LabelDuration;
        LabelPosition.Text = Localization.LabelPosition;
        LabelMonitor.Text = Localization.LabelMonitor;
        LabelLanguage.Text = Localization.LabelLanguage;
        LabelAutoStart.Text = Localization.LabelAutoStart;
        LabelLowBatteryEnable.Text = Localization.LabelLowBatteryEnable;
        LabelLowBattery.Text = Localization.LabelLowBattery;
        LabelFullChargeEnable.Text = Localization.LabelFullChargeEnable;
        LabelVersion.Text = Localization.LabelVersion;
        SaveBtn.Content = Localization.BtnSave;
        CheckUpdateBtn.Content = Localization.BtnCheckUpdate;

        if (LanguageCombo.Items.Count >= 3)
        {
            if (LanguageCombo.Items[0] is ComboBoxItem ci0) ci0.Content = Localization.ValueAuto;
            if (LanguageCombo.Items[1] is ComboBoxItem ci1) ci1.Content = Localization.ValueChinese;
            if (LanguageCombo.Items[2] is ComboBoxItem ci2) ci2.Content = Localization.ValueEnglish;
        }

        if (PositionCombo.Items.Count >= 3)
        {
            if (PositionCombo.Items[0] is ComboBoxItem pi0) pi0.Content = Localization.PosTopCenter;
            if (PositionCombo.Items[1] is ComboBoxItem pi1) pi1.Content = Localization.PosTopRight;
            if (PositionCombo.Items[2] is ComboBoxItem pi2) pi2.Content = Localization.PosTopLeft;
        }
    }

    // ---------------- 显示器 ----------------

    private void PopulateMonitors()
    {
        var screens = Screens.All;
        MonitorCombo.Items.Clear();
        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            var name = s.IsPrimary
                ? $"显示器 {i + 1}（主）"
                : $"显示器 {i + 1}";
            MonitorCombo.Items.Add(new ComboBoxItem { Content = name, Tag = i });
        }

        if (MonitorCombo.Items.Count == 0)
            MonitorCombo.Items.Add(new ComboBoxItem { Content = "默认", Tag = 0 });
    }

    // ---------------- 加载 / 收集 ----------------

    private void LoadSettings(AppSettings s)
    {
        ScaleSlider.Value = s.GlobalScale;
        DurationSlider.Value = s.DisplayDurationSeconds;
        PositionCombo.SelectedIndex = (int)s.HudPosition;
        MonitorCombo.SelectedIndex = s.MonitorIndex >= 0 && s.MonitorIndex < MonitorCombo.Items.Count
            ? s.MonitorIndex
            : 0;

        LanguageCombo.SelectedIndex = s.Language switch
        {
            "zh" => 1,
            "en" => 2,
            _ => 0,
        };

        LowBatterySwitch.IsChecked = s.EnableLowBatteryAlert;
        LowBatterySlider.Value = s.LowBatteryThreshold;
        LowBatterySlider.IsEnabled = s.EnableLowBatteryAlert;
        FullChargeSwitch.IsChecked = s.EnableFullChargeAlert;
        AutoStartSwitch.IsChecked = s.EnableAutoStart;

        VersionText.Text = GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    private AppSettings CollectSettings() => new()
    {
        GlobalScale = Math.Round(ScaleSlider.Value, 2),
        DisplayDurationSeconds = Math.Round(DurationSlider.Value, 1),
        HudPosition = (HudPosition)PositionCombo.SelectedIndex,
        MonitorIndex = MonitorCombo.SelectedItem is ComboBoxItem item && item.Tag is int idx
            ? idx
            : 0,
        Language = LanguageCombo.SelectedIndex switch
        {
            1 => "zh",
            2 => "en",
            _ => "auto",
        },
        EnableLowBatteryAlert = LowBatterySwitch.IsChecked == true,
        LowBatteryThreshold = (int)LowBatterySlider.Value,
        EnableFullChargeAlert = FullChargeSwitch.IsChecked == true,
        EnableAutoStart = AutoStartSwitch.IsChecked == true,
    };

    // ---------------- Tab 切换 ----------------

    private void SwitchTab(Border tabBtn, StackPanel panel)
    {
        TabGeneralBtn.Background = Brushes.Transparent;
        TabNotificationsBtn.Background = Brushes.Transparent;
        TabAboutBtn.Background = Brushes.Transparent;

        tabBtn.Background = new SolidColorBrush(Color.Parse("#2A2A2D"));

        GeneralPanel.IsVisible = panel == GeneralPanel;
        NotificationsPanel.IsVisible = panel == NotificationsPanel;
        AboutPanel.IsVisible = panel == AboutPanel;
    }

    // ---------------- 保存 ----------------

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var settings = CollectSettings();
        SettingsManager.Save(settings);

        // 处理开机自启
        if (settings.EnableAutoStart)
            Services.AutoStart.Enable(Services.AutoStart.CurrentExePath);
        else
            Services.AutoStart.Disable();

        if (Application.Current is App app)
            app.OnSettingsChanged(settings);

        SavedHint.Text = Localization.SavedToast;
        SavedHint.Opacity = 1;
        Dispatcher.UIThread.Post(async () =>
        {
            await System.Threading.Tasks.Task.Delay(2000);
            SavedHint.Opacity = 0;
        });
    }

    // ---------------- 检查更新 ----------------

    private async void OnCheckUpdate(object? sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateStatusText.Text = "...";

        try
        {
            var (hasUpdate, version, url) = await Services.UpdateChecker.CheckAsync();
            if (hasUpdate && url is not null)
            {
                var result = await MessageBox.Show(
                    this,
                    Localization.UpdateMsg(version ?? "?"),
                    Localization.UpdateTitle,
                    MessageBoxButton.OkCancel);

                if (result == MessageBoxResult.Ok)
                    Platform.Start(url);
            }
            else
            {
                UpdateStatusText.Text = Localization.UpToDate;
            }
        }
        catch
        {
            UpdateStatusText.Text = Localization.UpdateCheckFailed;
        }
        finally
        {
            CheckUpdateBtn.IsEnabled = true;
        }
    }
}

// 极简消息框辅助
public enum MessageBoxButton { Ok, OkCancel }
public enum MessageBoxResult { Ok, Cancel }

public static class MessageBox
{
    public static async Task<MessageBoxResult> Show(
        Window owner, string message, string title,
        MessageBoxButton button = MessageBoxButton.Ok)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
            Foreground = Brushes.White,
            CanResize = false,
            SystemDecorations = SystemDecorations.None,
            FontFamily = new FontFamily("HarmonyOS Sans SC, HarmonyOS Sans, Inter, Microsoft YaHei UI, sans-serif"),
        };

        var result = MessageBoxResult.Ok;
        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        stack.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });

        var btnPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };

        var okBtn = new Button
        {
            Content = Localization.BtnDownload,
            Width = 80, Height = 32,
            Background = new SolidColorBrush(Color.Parse("#C6CA4C")),
            Foreground = new SolidColorBrush(Color.Parse("#1E1E1E")),
            FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        okBtn.Click += (_, _) => { result = MessageBoxResult.Ok; dialog.Close(); };
        btnPanel.Children.Add(okBtn);

        if (button == MessageBoxButton.OkCancel)
        {
            var cancelBtn = new Button
            {
                Content = Localization.BtnCancel,
                Width = 80, Height = 32,
                CornerRadius = new CornerRadius(6),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            cancelBtn.Click += (_, _) => { result = MessageBoxResult.Cancel; dialog.Close(); };
            btnPanel.Children.Insert(0, cancelBtn);
        }

        stack.Children.Add(btnPanel);
        dialog.Content = stack;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        await dialog.ShowDialog(owner);
        return result;
    }
}

internal static class Platform
{
    public static void Start(string url)
    {
        using var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            },
        };
        p.Start();
    }
}