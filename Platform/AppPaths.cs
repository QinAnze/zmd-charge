using System;
using System.IO;

namespace EndfieldCharge.Services;

/// <summary>
/// 平台相关的标准目录。
///
/// Windows：<c>%APPDATA%\EndfieldCharge</c>、<c>%TEMP%\EndfieldCharge</c>
/// macOS  ：<c>~/Library/Application Support/EndfieldCharge</c>、<c>~/Library/Logs/EndfieldCharge</c>
///          （macOS 的 <c>Path.GetTempPath()</c> 落在每用户随机目录下，日志不便排查，故改用 Logs 目录）
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "EndfieldCharge";

    /// <summary>设置 / 锁文件所在目录。</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    /// <summary>日志目录。</summary>
    public static string LogDirectory
    {
        get
        {
#if ENDFIELD_MACOS
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
                return Path.Combine(home, "Library", "Logs", AppFolderName);
#endif
            return Path.Combine(Path.GetTempPath(), AppFolderName);
        }
    }

    /// <summary>单实例锁文件路径（仅非 Windows 平台使用）。</summary>
    public static string LockFilePath => Path.Combine(DataDirectory, ".lock");

    /// <summary>设置文件路径。</summary>
    public static string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");
}
