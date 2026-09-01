using System;
using Microsoft.Win32;

namespace EndfieldCharge.Services;

/// <summary>
/// 开机自启：写入当前用户注册表 Run 键（HKCU\...\CurrentVersion\Run）。
/// 只写当前用户，不需要管理员权限。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EndfieldCharge";

    /// <summary>当前是否已启用开机自启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrEmpty(s);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启用开机自启（记录 exe 全路径，带引号防空格）。</summary>
    public static void Enable(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key?.SetValue(ValueName, $"\"{exePath}\"");
        }
        catch
        {
            // 注册表写入失败时静默（托盘已选中状态回滚由调用方处理）
        }
    }

    /// <summary>禁用开机自启。</summary>
    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 忽略删除失败
        }
    }

    /// <summary>当前进程的 exe 完整路径（自启用）。</summary>
    public static string CurrentExePath =>
        Environment.ProcessPath
        ?? System.IO.Path.Combine(AppContext.BaseDirectory, "EndfieldCharge.exe");
}
