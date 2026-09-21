using System;

namespace EndfieldCharge.Services;

/// <summary>平台实现的唯一运行时分发点。</summary>
public static class PlatformServices
{
    /// <summary>
    /// 按当前操作系统创建平台服务。不支持的平台直接抛异常（本项目只覆盖 Windows / macOS）。
    ///
    /// CA1416 抑制说明：每个目标框架下只有一个平台实现参与编译（见 csproj 的
    /// <c>Compile Remove="Platform/{Windows,MacOS}/**"</c>），且下方已有运行时平台分支，
    /// 因此这里的 [SupportedOSPlatform] 提示是重复且无法在调用点内联消除的噪音。
    /// </summary>
#pragma warning disable CA1416
    public static IPlatformServices Create()
    {
#if ENDFIELD_WINDOWS
        return WindowsPlatformServices.Create();
#elif ENDFIELD_MACOS
        return MacOSPlatformServices.Create();
#else
        throw new PlatformNotSupportedException(
            "EndfieldCharge 仅支持 Windows 与 macOS。");
#endif
    }
#pragma warning restore CA1416
}
