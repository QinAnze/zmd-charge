using System;
using System.IO;
using System.Text.Json;
using EndfieldCharge.Services;

namespace EndfieldCharge.Settings;

public static class SettingsManager
{
    private static readonly string Folder = AppPaths.DataDirectory;

    private static readonly string FilePath = AppPaths.SettingsFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 保存失败静默忽略
        }
    }
}