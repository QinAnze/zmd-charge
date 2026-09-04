using System;
using System.IO;

namespace EndfieldCharge.Services;

/// <summary>
/// 简单文件日志。写入 %TEMP%/EndfieldCharge/log-{yyyyMMdd}.txt。
/// 仅当 Enabled 为 true 时写入（由设置控制）。
/// </summary>
public static class Logger
{
    private static readonly string LogDir = Path.Combine(
        Path.GetTempPath(), "EndfieldCharge");

    public static bool Enabled { get; set; }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Error(Exception ex) => Write("ERROR", $"{ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        if (!Enabled)
            return;

        try
        {
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, $"log-{DateTime.Now:yyyyMMdd}.txt");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}\n");
        }
        catch
        {
            // 日志写入失败忽略
        }
    }
}