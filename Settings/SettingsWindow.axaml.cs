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
using EndfieldCharge.Animations;
using EndfieldCharge.Services;
using EndfieldCharge.Views;

namespace EndfieldCharge.Settings;

public partial class SettingsWindow : Window
{
    private readonly HudWindow _hud;

    // 当前选中的全局快捷键（未保存也生效于预览）
    private uint _hotkeyModifiers;
    private uint _hotkeyKey;
    private bool _recordingHotkey;

    public SettingsWindow(AppSettings settings, HudWindow hud, string initialTab = "General")
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

        Title = Localization.SettingsTitle;
        InitLanguageCombo();
        InitPositionCombo();
        InitPreviewModeCombo();
        ApplyLocalization();

        PopulateMonitors();
        LoadSettings(settings);

        // Tab 切换
        TabGeneralBtn.PointerPressed += (_, _) => SwitchTab(TabGeneralBtn, GeneralPanel);
        TabAnimationBtn.PointerPressed += (_, _) => SwitchTab(TabAnimationBtn, AnimationPanel);
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
                DurationValue.Text = $"{DurationSlider.Value:F1}s";
        };
        BounceSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                BounceValue.Text = BounceSlider.Value.ToString("F3");
        };
        RippleIntensitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                RippleIntensityValue.Text = RippleIntensitySlider.Value.ToString("F2");
        };
        RippleSpreadSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                RippleSpreadValue.Text = RippleSpreadSlider.Value.ToString("F2");
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
        PreviewPlayBtn.Click += OnPlayPreview;
        FontInstallBtn.Click += OnInstallFont;

        // 全局快捷键：点击方框后录制新组合键。
        // KeyDown 挂在窗口上（隧道阶段）而不是方框上 —— 无论当前焦点在哪个控件，
        // 录制期间按键都能被截获，不依赖 Border 能否拿到键盘焦点。
        HotkeyBox.PointerPressed += (_, _) => BeginRecordHotkey();
        AddHandler(KeyDownEvent, OnHotkeyKeyDown, RoutingStrategies.Tunnel);
        HotkeySwitch.IsCheckedChanged += (_, _) => OnHotkeyToggleChanged();
        // 默认 Tab
        var (tab, panel) = initialTab switch
        {
            "Animation" => (TabAnimationBtn, AnimationPanel),
            "Notifications" => (TabNotificationsBtn, NotificationsPanel),
            "About" => (TabAboutBtn, AboutPanel),
            _ => (TabGeneralBtn, GeneralPanel),
        };
        SwitchTab(tab, panel);
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

    private void InitPreviewModeCombo()
    {
        PreviewModeCombo.Items.Clear();
        PreviewModeCombo.Items.Add(new ComboBoxItem { Tag = "plug" });
        PreviewModeCombo.Items.Add(new ComboBoxItem { Tag = "saver" });
        PreviewModeCombo.Items.Add(new ComboBoxItem { Tag = "unplug" });
    }

    // ---------------- 本地化 ----------------

    private void ApplyLocalization()
    {
        WinTitle.Text = Localization.SettingsTitle;
        TabGeneralText.Text = Localization.TabGeneral;
        TabAnimationText.Text = Localization.TabAnimation;
        TabNotificationsText.Text = Localization.TabNotifications;
        TabAboutText.Text = Localization.TabAbout;
        LabelScale.Text = Localization.LabelScale;
        LabelDuration.Text = Localization.LabelDuration;
        LabelPosition.Text = Localization.LabelPosition;
        LabelMonitor.Text = Localization.LabelMonitor;
        LabelLanguage.Text = Localization.LabelLanguage;
        LabelAutoStart.Text = Localization.LabelAutoStart;
        DescAutoStartText.Text = Localization.DescAutoStart;
        LabelLowBatteryEnable.Text = Localization.LabelLowBatteryEnable;
        DescLowBatteryAlertText.Text = Localization.DescLowBatteryAlert;
        LabelLowBattery.Text = Localization.LabelLowBattery;
        LabelFullChargeEnable.Text = Localization.LabelFullChargeEnable;
        DescFullChargeAlertText.Text = Localization.DescFullChargeAlert;
        LabelPowerSaverNotify.Text = Localization.LabelPowerSaverNotify;
        PowerSaverNotifyDesc.Text = Localization.PowerSaverNotifyDesc;
        LabelVersion.Text = Localization.LabelVersion;
        LabelAuthor.Text = Localization.LabelAuthor;
        SaveBtn.Content = Localization.BtnSave;
        CheckUpdateBtn.Content = Localization.BtnCheckUpdate;
        AboutSubtitleText.Text = Localization.AboutSubtitle;
        FontSectionTitle.Text = Localization.FontSectionTitle;
        FontDescText.Text = Localization.FontDesc;
        FontInstallBtn.Content = Localization.BtnInstallFont;
        SectionHotkeyText.Text = Localization.SectionHotkey;
        LabelHotkeyEnable.Text = Localization.LabelHotkeyEnable;
        DescHotkeyText.Text = Localization.DescHotkey;
        LabelHotkeyKeys.Text = Localization.LabelHotkeyKeys;

        SectionDisplayText.Text = Localization.SectionDisplay;
        SectionPositionText.Text = Localization.SectionPosition;
        SectionStartupText.Text = Localization.SectionStartup;
        SectionAlertSettingsText.Text = Localization.SectionAlertSettings;
        SectionAnimParams.Text = Localization.SectionAnimParams;
        SectionPreview.Text = Localization.SectionPreview;
        LabelBounce.Text = Localization.LabelBounce;
        LabelRippleIntensity.Text = Localization.LabelRippleIntensity;
        LabelRippleSpread.Text = Localization.LabelRippleSpread;
        LabelPlayMode.Text = Localization.LabelPlayMode;
        PreviewPlayBtn.Content = Localization.BtnPlay;

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

        if (PreviewModeCombo.Items.Count >= 3)
        {
            if (PreviewModeCombo.Items[0] is ComboBoxItem mi0) mi0.Content = Localization.ModePlug;
            if (PreviewModeCombo.Items[1] is ComboBoxItem mi1) mi1.Content = Localization.ModeSaver;
            if (PreviewModeCombo.Items[2] is ComboBoxItem mi2) mi2.Content = Localization.ModeUnplug;
        }
    }

    // ---------------- 显示器 ----------------

    private void PopulateMonitors()
    {
        var screens = Screens.All;
        MonitorCombo.Items.Clear();

        // 第一项：主显示器（默认），MonitorIndex 存 -1
        MonitorCombo.Items.Add(new ComboBoxItem
        {
            Content = Localization.MonitorPrimaryDefault,
            Tag = -1,
        });

        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            MonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = Localization.MonitorName(i, s.IsPrimary),
                Tag = i,
            });
        }
    }

    // ---------------- 加载 / 收集 ----------------

    private void LoadSettings(AppSettings s)
    {
        ScaleSlider.Value = s.GlobalScale;
        DurationSlider.Value = s.DisplayDurationSeconds;
        BounceSlider.Value = s.BounceStrength;
        RippleIntensitySlider.Value = s.RippleIntensity;
        RippleSpreadSlider.Value = s.RippleSpread;
        PositionCombo.SelectedIndex = (int)s.HudPosition;
        // 下拉第一项是「主显示器（默认）」(-1)，物理显示器 i 对应下拉第 i+1 项
        int monitorSel = s.MonitorIndex < 0 ? 0 : s.MonitorIndex + 1;
        MonitorCombo.SelectedIndex = monitorSel >= 0 && monitorSel < MonitorCombo.Items.Count
            ? monitorSel
            : 0;

        LanguageCombo.SelectedIndex = s.Language switch
        {
            "zh" => 1,
            "en" => 2,
            _ => 0,
        };

        PowerSaverSwitch.IsChecked = s.EnablePowerSaverNotify;
        LowBatterySwitch.IsChecked = s.EnableLowBatteryAlert;
        LowBatterySlider.Value = s.LowBatteryThreshold;
        LowBatterySlider.IsEnabled = s.EnableLowBatteryAlert;
        FullChargeSwitch.IsChecked = s.EnableFullChargeAlert;
        AutoStartSwitch.IsChecked = s.EnableAutoStart;

        _hotkeyModifiers = s.HotkeyModifiers;
        _hotkeyKey = (uint)s.HotkeyKey;
        HotkeySwitch.IsChecked = s.EnableHotkey;
        HotkeyBox.IsEnabled = s.EnableHotkey;
        HotkeyBox.Opacity = s.EnableHotkey ? 1d : 0.45d;
        RefreshHotkeyUi();

        VersionText.Text = GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        FontStatusText.Text = string.Empty;
    }

    private AppSettings CollectSettings() => new()
    {
        GlobalScale = Math.Round(ScaleSlider.Value, 2),
        DisplayDurationSeconds = Math.Round(DurationSlider.Value, 1),
        BounceStrength = Math.Round(BounceSlider.Value, 3),
        RippleIntensity = Math.Round(RippleIntensitySlider.Value, 2),
        RippleSpread = Math.Round(RippleSpreadSlider.Value, 2),
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
        EnablePowerSaverNotify = PowerSaverSwitch.IsChecked == true,
        EnableAutoStart = AutoStartSwitch.IsChecked == true,
        EnableHotkey = HotkeySwitch.IsChecked == true,
        HotkeyModifiers = _hotkeyModifiers,
        HotkeyKey = (int)_hotkeyKey,
    };

    // ---------------- Tab 切换 ----------------

    private void SwitchTab(Border tabBtn, StackPanel panel)
    {
        TabGeneralBtn.Background = Brushes.Transparent;
        TabAnimationBtn.Background = Brushes.Transparent;
        TabNotificationsBtn.Background = Brushes.Transparent;
        TabAboutBtn.Background = Brushes.Transparent;

        tabBtn.Background = new SolidColorBrush(Color.Parse("#2A2A2D"));

        GeneralPanel.IsVisible = panel == GeneralPanel;
        AnimationPanel.IsVisible = panel == AnimationPanel;
        NotificationsPanel.IsVisible = panel == NotificationsPanel;
        AboutPanel.IsVisible = panel == AboutPanel;
    }

    // ---------------- 动画预览 ----------------

    /// <summary>用当前滑块值（未保存也生效）实时预览动画。</summary>
    private async void OnPlayPreview(object? sender, RoutedEventArgs e)
    {
        PreviewPlayBtn.IsEnabled = false;

        // 用当前滑块值构造参数，无需保存即可预览效果
        var options = new AnimationOptions
        {
            DurationSeconds = Math.Clamp(DurationSlider.Value, 3d, 10d),
            BounceStrength = Math.Clamp(BounceSlider.Value, 0d, 0.5d),
            RippleIntensity = Math.Clamp(RippleIntensitySlider.Value, 0d, 2d),
            RippleSpread = Math.Clamp(RippleSpreadSlider.Value, 0.5d, 1.5d),
        };

        var pluggedIn = new BatterySnapshot(
            RemainingWh: 62.4, FullWh: 90.0,
            Percent: 69, AcOnline: true, Charging: true);

        var unplugged = new BatterySnapshot(
            RemainingWh: 62.4, FullWh: 90.0,
            Percent: 69, AcOnline: false, Charging: false);

        try
        {
            switch (PreviewModeCombo.SelectedIndex)
            {
                case 1:
                    await _hud.ShowAndPlayAsync(pluggedIn, acOnline: true,
                        HudPlayMode.PowerSaver, options);
                    break;
                case 2:
                    await _hud.ShowAndPlayAsync(unplugged, acOnline: false,
                        HudPlayMode.Battery, options);
                    break;
                default:
                    await _hud.ShowAndPlayAsync(pluggedIn, acOnline: true,
                        HudPlayMode.Charge, options);
                    break;
            }
        }
        catch
        {
        }
        finally
        {
            PreviewPlayBtn.IsEnabled = true;
        }
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

        // 应用设置（同时把全局快捷键重新注册一遍），并把注册结果反馈到界面
        bool hotkeyOk = true;
        if (Application.Current is App app)
            hotkeyOk = app.OnSettingsChanged(settings);

        UpdateHotkeyHint(hotkeyOk);
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

    // ---------------- 字体安装 ----------------

    private async void OnInstallFont(object? sender, RoutedEventArgs e)
    {
        FontInstallBtn.IsEnabled = false;
        FontStatusText.Text = Localization.FontInstalling;

        // Inter 字体 GitHub Releases 下载页
        const string fontUrl = "https://github.com/rsms/inter/releases/latest";

        try
        {
            Platform.Start(fontUrl);
            await Task.Delay(500);
            FontStatusText.Text = Localization.FontInstalled;
        }
        catch
        {
            FontStatusText.Text = Localization.UpdateCheckFailed;
        }
        finally
        {
            FontInstallBtn.IsEnabled = true;
        }
    }

    // ---------------- 全局快捷键 ----------------

    /// <summary>进入录制状态：接下来按下的组合键成为新的全局快捷键。</summary>
    private void BeginRecordHotkey()
    {
        if (HotkeySwitch.IsChecked != true)
            return;

        _recordingHotkey = true;
        HotkeyText.Text = Localization.HotkeyRecording;
        HotkeyHintText.Foreground = new SolidColorBrush(Color.Parse("#888888"));
        HotkeyHintText.Text = Localization.HotkeyIdleHint;
        HotkeyBox.BorderBrush = new SolidColorBrush(Color.Parse("#C6CA4C"));
        HotkeyBox.Focus();
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_recordingHotkey)
            return;

        // 录制期间吞掉所有按键，避免 Tab / 方向键把焦点移走
        e.Handled = true;

        var key = e.Key;

        // Esc / Backspace / Delete 视为取消，保留原快捷键
        if (key is Key.Escape or Key.Back or Key.Delete)
        {
            CancelRecordHotkey();
            return;
        }

        // 只按了修饰键，继续等主键
        if (IsModifierKey(key))
            return;

        uint vk = HotkeyService.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            ShowHotkeyHint(Localization.HotkeyUnsupported, isError: true);
            return;
        }

        uint modifiers = ModifiersFrom(e.KeyModifiers);

        // 不带修饰键的普通按键会全局抢占输入，只放行 F1–F24
        if (modifiers == 0 && !HotkeyService.IsFunctionKey(key))
        {
            ShowHotkeyHint(Localization.HotkeyNeedModifier, isError: true);
            return;
        }

        _recordingHotkey = false;
        _hotkeyModifiers = modifiers;
        _hotkeyKey = vk;
        HotkeyBox.BorderBrush = new SolidColorBrush(Color.Parse("#3A3A3C"));
        RefreshHotkeyUi();
    }

    private void CancelRecordHotkey()
    {
        _recordingHotkey = false;
        HotkeyBox.BorderBrush = new SolidColorBrush(Color.Parse("#3A3A3C"));
        RefreshHotkeyUi();
    }

    private void OnHotkeyToggleChanged()
    {
        bool on = HotkeySwitch.IsChecked == true;

        if (!on)
            _recordingHotkey = false;

        HotkeyBox.IsEnabled = on;
        HotkeyBox.Opacity = on ? 1d : 0.45d;
        RefreshHotkeyUi();
    }

    /// <summary>把当前组合键写回方框，并复位提示文案。</summary>
    private void RefreshHotkeyUi()
    {
        HotkeyText.Text = HotkeyService.ToDisplayString(_hotkeyModifiers, _hotkeyKey);
        ShowHotkeyHint(
            HotkeySwitch.IsChecked == true ? Localization.HotkeyIdleHint : Localization.HotkeyDisabled,
            isError: false);
    }

    /// <summary>保存后把注册结果反馈给用户（被占用时提示冲突）。</summary>
    private void UpdateHotkeyHint(bool registered)
    {
        if (HotkeySwitch.IsChecked != true)
        {
            ShowHotkeyHint(Localization.HotkeyDisabled, isError: false);
            return;
        }

        ShowHotkeyHint(
            registered ? Localization.HotkeyIdleHint : Localization.HotkeyConflict,
            isError: !registered);
    }

    private void ShowHotkeyHint(string text, bool isError)
    {
        HotkeyHintText.Text = text;
        HotkeyHintText.Foreground = new SolidColorBrush(Color.Parse(isError ? "#FF4D4F" : "#888888"));
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static uint ModifiersFrom(KeyModifiers km)
    {
        uint m = 0;
        if (km.HasFlag(KeyModifiers.Control)) m |= PowerNative.ModControl;
        if (km.HasFlag(KeyModifiers.Alt)) m |= PowerNative.ModAlt;
        if (km.HasFlag(KeyModifiers.Shift)) m |= PowerNative.ModShift;
        if (km.HasFlag(KeyModifiers.Meta)) m |= PowerNative.ModWin;
        return m;
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