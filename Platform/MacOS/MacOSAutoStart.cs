using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace EndfieldCharge.Services;

/// <summary>
/// macOS 登录时自启：写 <c>~/Library/LaunchAgents/com.lenkmat.endfieldcharge.plist</c>。
///
/// 为什么用 LaunchAgent 而不是 SMAppService / SMLoginItemSetEnabled：
///   · <c>SMLoginItemSetEnabled</c> 需要额外的 login-item helper bundle；
///   · <c>SMAppService.mainApp</c>（macOS 13+）对签名与公证要求更高，而本项目首版只有
///     ad-hoc / Apple Development 证书。
///   LaunchAgent 零依赖、可审计、用户可直接删除，是最稳的落地方式。
///
/// 只支持在 .app bundle 内运行 —— 裸可执行文件的路径在升级/移动后会失效，
/// 因此 <see cref="IsSupported"/> 在非 bundle 环境下返回 false，UI 应据此禁用开关。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOSAutoStart : IAutoStartManager
{
    private const string Label = "com.lenkmat.endfieldcharge";

    private static string LaunchAgentsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents");

    private static string PlistPath => Path.Combine(LaunchAgentsDir, $"{Label}.plist");

    /// <summary>仅在 .app bundle 内支持（裸可执行文件路径不稳定）。</summary>
    public bool IsSupported => AppBundleExecutablePath() is not null;

    /// <summary>当前是否已启用：plist 存在且 Label 匹配。</summary>
    public bool IsEnabled()
    {
        try
        {
            if (!File.Exists(PlistPath))
                return false;

            var content = File.ReadAllText(PlistPath);
            return content.Contains($"<string>{Label}</string>", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>写入 LaunchAgent plist，并尽力让 launchd 立即加载。</summary>
    public void Enable()
    {
        var exePath = AppBundleExecutablePath();
        if (exePath is null)
        {
            Logger.Warn("MacOSAutoStart: 非 .app bundle 环境，跳过自启注册");
            return;
        }

        try
        {
            Directory.CreateDirectory(LaunchAgentsDir);
            File.WriteAllText(PlistPath, BuildPlist(exePath));
            Logger.Info($"MacOSAutoStart: wrote {PlistPath}");

            // 尽力而为：bootstrap 失败也不影响下次登录时 launchd 自动加载
            RunLaunchctl($"bootstrap gui/{UserId()} \"{PlistPath}\"");
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    /// <summary>卸载并删除 LaunchAgent plist。</summary>
    public void Disable()
    {
        try
        {
            RunLaunchctl($"bootout gui/{UserId}/{Label}");

            if (File.Exists(PlistPath))
            {
                File.Delete(PlistPath);
                Logger.Info($"MacOSAutoStart: removed {PlistPath}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    // ---------------- 内部 ----------------

    /// <summary>
    /// 解析 .app bundle 内的可执行文件绝对路径。
    /// 优先用 NSBundle.mainBundle.bundlePath（最可靠），失败则从进程路径向上找 .app。
    /// 非 bundle 环境返回 null。
    /// </summary>
    public static string? AppBundleExecutablePath()
    {
        var bundlePath = MacOSAppKit.BundlePath();
        if (bundlePath is not null &&
            bundlePath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(bundlePath))
        {
            var candidate = Path.Combine(bundlePath, "Contents", "MacOS", "EndfieldCharge");
            return File.Exists(candidate) ? candidate : null;
        }

        // 兜底：从当前进程可执行文件路径向上找 .app 目录
        var exe = Environment.ProcessPath;
        if (exe is null)
            return null;

        var dir = Path.GetDirectoryName(exe);
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return exe;

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static string BuildPlist(string exePath) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>{Label}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{exePath}</string>
          </array>
          <key>RunAtLoad</key>
          <true/>
          <key>ProcessType</key>
          <string>Interactive</string>
        </dict>
        </plist>
        """;

    private static int UserId() => GetUid();

    /// <summary>当前用户 uid（launchctl 的 gui/&lt;uid&gt; 域需要）。</summary>
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern uint getuid();

    private static int GetUid()
    {
        try
        {
            return (int)getuid();
        }
        catch
        {
            // getuid 在 macOS 上必定存在；真失败时退化为 501（macOS 首个用户），
            // launchctl 调用失败只意味着"未立即加载"，不影响下次登录自启。
            return 501;
        }
    }

    private static void RunLaunchctl(string arguments)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/launchctl",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!p.Start())
                return;

            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(3000);
        }
        catch
        {
            // launchctl 缺失或调用失败时静默：plist 已落地，下次登录仍会加载
        }
    }
}
