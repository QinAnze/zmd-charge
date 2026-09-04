using System.Threading;
using EndfieldCharge.Settings;

namespace EndfieldCharge;

/// <summary>
/// 本地化：支持运行时语言切换。
/// 优先使用 settings.Language，其次系统 UI 语言。
/// </summary>
public static class Localization
{
    private static AppSettings? _settings;

    public static void UseSettings(AppSettings settings) => _settings = settings;

    private static bool IsChinese
    {
        get
        {
            if (_settings?.Language is not null && _settings.Language != "auto")
                return _settings.Language.StartsWith("zh");
            return Thread.CurrentThread.CurrentUICulture.Name.StartsWith("zh");
        }
    }

    // ---- HUD ----
    public static string TagLine => IsChinese ? "/// 超充模式" : "/// SUPER CHARGE MODE";
    public static string TitleMode => IsChinese ? "超充模式" : "Super Charge Mode";
    public static string TagLineSaver => IsChinese ? "/// 省电模式" : "/// POWER SAVING MODE";
    public static string TitleSaver => IsChinese ? "省电模式" : "Power Saving Mode";

    // ---- 托盘菜单 ----
    public static string PreviewHud => IsChinese ? "预览电量 HUD" : "Preview Power HUD";
    public static string AutoStart => IsChinese ? "开机自启" : "Auto Start";
    public static string Settings => IsChinese ? "设置" : "Settings";
    public static string CheckUpdate => IsChinese ? "检查更新" : "Check for Updates";
    public static string Exit => IsChinese ? "退出" : "Exit";
    public static string TrayTooltip => IsChinese ? "EndfieldCharge · 电量 HUD" : "EndfieldCharge · Power HUD";

    // ---- 设置窗口 ----
    public static string SettingsTitle => IsChinese ? "设置" : "Settings";
    public static string TabGeneral => IsChinese ? "通用" : "General";
    public static string TabNotifications => IsChinese ? "通知" : "Notifications";
    public static string TabAbout => IsChinese ? "关于" : "About";
    public static string LabelScale => IsChinese ? "全局缩放" : "Global Scale";
    public static string LabelDuration => IsChinese ? "显示时长（秒）" : "Display Duration (s)";
    public static string LabelPosition => IsChinese ? "HUD 位置" : "HUD Position";
    public static string LabelMonitor => IsChinese ? "显示器" : "Monitor";
    public static string LabelLanguage => IsChinese ? "语言" : "Language";
    public static string ValueAuto => IsChinese ? "自动" : "Auto";
    public static string ValueChinese => IsChinese ? "中文" : "Chinese";
    public static string ValueEnglish => IsChinese ? "英文" : "English";
    public static string PosTopCenter => IsChinese ? "顶部居中" : "Top Center";
    public static string PosTopRight => IsChinese ? "顶部靠右" : "Top Right";
    public static string PosTopLeft => IsChinese ? "顶部靠左" : "Top Left";
    public static string LabelLowBattery => IsChinese ? "低电量提醒阈值" : "Low Battery Alert Threshold";
    public static string LabelLowBatteryEnable => IsChinese ? "启用低电量提醒" : "Enable Low Battery Alert";
    public static string LabelFullChargeEnable => IsChinese ? "充满时提醒" : "Alert When Fully Charged";// ---- 设置窗口段落标题 ----
    public static string SectionDisplay => IsChinese ? "显示" : "Display";
    public static string SectionPosition => IsChinese ? "位置与语言" : "Position & Language";
    public static string SectionStartup => IsChinese ? "启动" : "Startup";
    public static string SectionAlertSettings => IsChinese ? "提醒设置" : "Alert Settings";
    public static string DescAutoStart => IsChinese ? "登录 Windows 时自动启动" : "Auto start on Windows login";
    public static string DescLowBatteryAlert => IsChinese ? "电量低于阈值时弹窗提醒" : "Alert when battery drops below threshold";
    public static string DescFullChargeAlert => IsChinese ? "电池充满后弹窗通知" : "Notify when battery is fully charged";

    // ---- 关于 ----
    public static string LabelVersion => IsChinese ? "版本" : "Version";
    public static string LabelAuthor => IsChinese ? "作者" : "Author";
    public static string AboutSubtitle => IsChinese ? "终末地风格电量 HUD" : "Endfield-style Power HUD";

    // ---- 显示器 ----
    public static string MonitorName(int index, bool isPrimary) => IsChinese
        ? isPrimary ? $"显示器 {index + 1}（主）" : $"显示器 {index + 1}"
        : isPrimary ? $"Monitor {index + 1} (Primary)" : $"Monitor {index + 1}";
    public static string MonitorDefault => IsChinese ? "默认" : "Default";

    // ---- 动画页 ----
    public static string TabAnimation => IsChinese ? "动画" : "Animation";
    public static string SectionAnimParams => IsChinese ? "动画参数" : "Animation Parameters";
    public static string SectionPreview => IsChinese ? "预览" : "Preview";
    public static string LabelBounce => IsChinese ? "回弹强度" : "Bounce Strength";
    public static string LabelRippleIntensity => IsChinese ? "波纹强度" : "Ripple Intensity";
    public static string LabelRippleSpread => IsChinese ? "波纹幅度" : "Ripple Spread";
    public static string LabelPlayMode => IsChinese ? "播放模式" : "Play Mode";
    public static string ModePlug => IsChinese ? "插电（完整三态）" : "Plug In (Full Sequence)";
    public static string ModeUnplug => IsChinese ? "拔电（简化胶囊）" : "Unplug (Simple Capsule)";
    public static string ModeSaver => IsChinese ? "省电模式（完整三态）" : "Battery Saver (Full Sequence)";

    // ---- 通知 ----
    public static string LabelPowerSaverNotify => IsChinese ? "省电模式切换提示" : "Battery Saver Toggle Notify";
    public static string PowerSaverNotifyDesc => IsChinese
        ? "开启 / 关闭系统省电模式时显示 HUD"
        : "Show HUD when battery saver is toggled";
    public static string LabelUpdateCheck => IsChinese ? "自动检查更新" : "Auto Check for Updates";
    public static string LabelAutoStart => IsChinese ? "开机自启" : "Auto Start";
    public static string BtnCheckUpdate => IsChinese ? "检查更新" : "Check Now";
    public static string BtnSave => IsChinese ? "保存" : "Save";
    public static string SavedToast => IsChinese ? "设置已保存" : "Settings saved";

    // ---- 提醒 ----
    public static string LowBatteryTitle => IsChinese ? "电量不足" : "Low Battery";
    public static string LowBatteryMsg(int pct) => IsChinese
        ? $"电量仅剩 {pct}%，请及时充电"
        : $"Battery at {pct}%, please charge soon";
    public static string FullChargeTitle => IsChinese ? "已充满" : "Fully Charged";
    public static string FullChargeMsg => IsChinese ? "电池已充满，可以拔掉电源了" : "Battery is fully charged, you can unplug now";

    // ---- 更新 ----
    public static string UpdateTitle => IsChinese ? "发现新版本" : "Update Available";
    public static string UpdateMsg(string ver) => IsChinese
        ? $"EndfieldCharge {ver} 已发布，是否前往下载？"
        : $"EndfieldCharge {ver} is available. Download now?";
    public static string UpToDate => IsChinese ? "已是最新版本" : "You're up to date";
    public static string UpdateCheckFailed => IsChinese ? "检查更新失败" : "Update check failed";
    public static string BtnDownload => IsChinese ? "下载" : "Download";
    public static string BtnCancel => IsChinese ? "取消" : "Cancel";

    // ---- 预览 ----
    public static string PreviewTitle => IsChinese ? "动画预览" : "Animation Preview";
    public static string BtnPlay => IsChinese ? "播放" : "Play";

    // ---- 字体 ----
    public static string FontSectionTitle => IsChinese ? "字体" : "Font";
    public static string FontDesc => IsChinese
        ? "HUD 数字与英文字体使用 Inter Medium，可获得更清晰的渲染效果"
        : "HUD uses Inter Medium for digital and English text for sharper rendering";
    public static string BtnInstallFont => IsChinese ? "安装 Inter 字体" : "Install Inter Font";
    public static string FontInstalling => IsChinese ? "正在打开字体下载页…" : "Opening font download page...";
    public static string FontInstalled => IsChinese ? "下载后双击 Inter.ttf 文件即可安装" : "Download and double-click Inter.ttf to install";
}