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

    private static readonly object Gate = new();

    // volatile：日志开关由 UI 线程写入，PowerWatcher / HotkeyService 的后台消息线程读取。
    // 不用 volatile 时后台线程可能长期读到缓存里的 false，导致关键告警（如隐藏窗口创建失败）静默丢失。
    private static volatile bool _enabled;

    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Error(Exception ex) => Write("ERROR", $"{ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        if (!Enabled)
            return;

        // 加锁：后台消息线程 / UI 线程 / 电源线程可能同时写日志，
        // 并发 AppendAllText 会因共享冲突抛异常并被吞掉，导致个别行静默丢失。
        lock (Gate)
        {
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
}