using System;
using System.Threading;
using Avalonia;

namespace PptConsole;

class Program
{
    private const string SingleInstanceMutexName = @"Local\PptConsole_SingleInstance_9E4A";

    [STAThread]
    public static void Main(string[] args)
    {
        // 只允许一个实例常驻；已运行时静默退出
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);

        if (!createdNew)
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        GC.KeepAlive(mutex);
    }

    /// <summary>Avalonia 配置入口，设计器也会用到，勿删。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
