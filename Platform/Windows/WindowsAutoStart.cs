using System;
using Microsoft.Win32;

namespace EndfieldCharge.Services;

/// <summary>
/// Windows 开机自启：写入当前用户注册表 Run 键（HKCU\...\CurrentVersion\Run）。
/// 只写当前用户，不需要管理员权限。
/// </summary>
internal sealed class WindowsAutoStart : IAutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EndfieldCharge";

    /// <summary>Windows 下恒为支持（注册表方式无前置条件）。</summary>
    public bool IsSupported => true;

    /// <summary>当前是否已启用开机自启。</summary>
    public bool IsEnabled()
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
    public void Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key?.SetValue(ValueName, $"\"{CurrentExePath}\"");
        }
        catch
        {
            // 注册表写入失败时静默（设置页选中状态回滚由调用方通过 IsEnabled 复核）
        }
    }

    /// <summary>禁用开机自启。</summary>
    public void Disable()
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
