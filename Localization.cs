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
    public static string LabelFullChargeEnable => IsChinese ? "充满时提醒" : "Alert When Fully Charged";
    public static string LabelVersion => IsChinese ? "版本" : "Version";
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
    public static string LabelPreviewScale => IsChinese ? "胶囊缩放" : "Pill Scale";
    public static string LabelPreviewDuration => IsChinese ? "总时长" : "Total Duration";
    public static string LabelPreviewBounce => IsChinese ? "回弹强度" : "Bounce Strength";
    public static string BtnPlay => IsChinese ? "播放" : "Play";
    public static string BtnClose => IsChinese ? "关闭" : "Close";
}