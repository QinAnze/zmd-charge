using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using EndfieldCharge.Services;

namespace EndfieldCharge;

class Program
{
    private const string SingleInstanceMutexName = @"Local\EndfieldCharge_SingleInstance_7C1D";

    [STAThread]
    public static void Main(string[] args)
    {
        // 诊断模式：不进 UI 循环，直接打印 JSON 后退出
        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }
        // 只允许一个实例常驻；已运行时静默退出
        if (!TryAcquireSingleInstance(out var mutex, out var lockStream))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // 保持单实例句柄存活到进程结束
        GC.KeepAlive(mutex);
        GC.KeepAlive(lockStream);
    }

    /// <summary>
    /// 单实例保护。
    ///
    /// Windows：命名互斥体（<c>Local\</c> 前缀 = 当前会话）。
    /// 其他平台：.NET 的命名 Mutex 在 Unix 上是进程内的，跨进程**无效**，
    ///           因此改用文件排他锁（<see cref="FileStream.Lock"/>）。
    /// </summary>
    private static bool TryAcquireSingleInstance(out Mutex? mutex, out FileStream? lockStream)
    {
        mutex = null;
        lockStream = null;

        if (OperatingSystem.IsWindows())
        {
            mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
                return false;

            GC.KeepAlive(mutex);
            return true;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);

            // FileShare.None：第二个实例打开同一文件会抛 IOException。
            // 不使用 FileStream.Lock —— 该 API 在 macOS 上不受支持（CA1416 亦会报警）。
            FileStream stream;
            try
            {
                stream = new FileStream(
                    AppPaths.LockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                // 已被另一个实例独占 → 本实例静默退出
                return false;
            }

            lockStream = stream;
            return true;
        }
        catch (Exception ex)
        {
            // 锁文件不可用时退化为"允许启动"，避免因为路径权限问题完全无法运行
            Logger.Enabled = true;
            Logger.Warn($"Program: 单实例锁创建失败，将允许多实例启动：{ex.Message}");
            return true;
        }
    }

    /// <summary>Avalonia 配置入口，设计器也会用到，勿删。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
