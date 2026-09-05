using System;
using System.Runtime.InteropServices;

namespace PptConsole.Services;

/// <summary>
/// 键击注入：放映窗口保持前台（控制台窗口 WS_EX_NOACTIVATE 不抢焦点），
/// 方向键对 PowerPoint / WPS / Keynote 等一切放映软件通用。
/// 注意：若放映程序以管理员运行而本程序未提权，SendInput 会被 UIPI 拦截（返回 0）。
/// </summary>
internal static class InputNative
{
    /// <summary>发送一次方向键（右=下一页，左=上一页）。</summary>
    public static bool SendArrowKey(bool forward)
    {
        ushort vk = forward ? Win32Interop.VK_RIGHT : Win32Interop.VK_LEFT;

        var inputs = new Win32Interop.INPUT[2];
        inputs[0].type = Win32Interop.INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = vk;
        inputs[1].type = Win32Interop.INPUT_KEYBOARD;
        inputs[1].U.ki.wVk = vk;
        inputs[1].U.ki.dwFlags = Win32Interop.KEYEVENTF_KEYUP;

        return Win32Interop.SendInput(2, inputs, Marshal.SizeOf<Win32Interop.INPUT>()) == 2;
    }
}
