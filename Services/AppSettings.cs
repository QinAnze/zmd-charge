using System;
using System.IO;
using System.Text.Json;

namespace EndfieldCharge.Services;

/// <summary>
/// 本地设置持久化（%APPDATA%\EndfieldCharge\settings.json）。
/// 只存少量开关与上次判定结果，不做配置系统。读写全部静默失败，
/// 设置存不下来最多是"记不住"，不能因此崩掉托盘程序。
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>常驻模式：开机即显示且不再自动收起，只能从托盘退出。</summary>
    public bool ResidentMode { get; set; }

    /// <summary>上次判定到的充电器是快充（用于下次插入时立刻定档）。</summary>
    public bool LastChargerFast { get; set; }

    /// <summary>上次充电会话观测到的峰值充电功率（瓦），用于判定的可参考上界。</summary>
    public double LastChargerPeakWatts { get; set; }

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EndfieldCharge",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            string path = FilePath;
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(text, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // 文件损坏 / 无权限 → 用默认值
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 写不进去就算了，不影响本次运行
        }
    }
}
