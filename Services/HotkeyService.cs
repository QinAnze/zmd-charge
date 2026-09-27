using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Avalonia.Input;

namespace EndfieldCharge.Services;

/// <summary>
/// 全局快捷键监听（RegisterHotKey + WM_HOTKEY）。
///
/// 结构与 PowerWatcher 同构：在独立 STA 线程上创建 message-only 隐藏窗口收消息。
/// RegisterHotKey / UnregisterHotKey 必须由创建窗口的那个线程调用，因此运行时改键
/// 走 PostThreadMessage 投递到消息线程执行，再用信号量把「是否注册成功」带回调用方
/// —— 组合键被别的程序占用时 UI 需要给用户提示。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HotkeyService : IDisposable
{
    private const string ClassName = "EndfieldCharge_HotkeyMsgWindow";
    private const int HotkeyId = 0xE171;
    private const uint WmDestroy = 0x0002;

    /// <summary>自定义消息：请消息线程重新应用（注册 / 注销）快捷键。</summary>
    private const uint WmApplyHotkey = PowerNative.WmApp + 1;

    private readonly PowerNative.WndProcDelegate _wndProc;
    private readonly ManualResetEventSlim _ready = new(false);

    /// <summary>保护「待应用的快捷键配置」与回执信号。</summary>
    private readonly object _gate = new();

    private uint _pendingModifiers;
    private uint _pendingKey;
    private bool _pendingEnabled;
    private ManualResetEventSlim? _applyDone;
    private bool _applyOk;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private volatile bool _registered;
    private bool _disposed;

    public HotkeyService()
    {
        // 保持委托存活，防止被 GC 回收后 WndProc 崩溃
        _wndProc = WndProc;
    }

    /// <summary>快捷键被按下。事件在后台线程触发，订阅方需自行切回 UI 线程。</summary>
    public event EventHandler? Pressed;

    /// <summary>当前组合键是否已被系统成功注册。</summary>
    public bool IsRegistered => _registered;

    // ---------------- 生命周期 ----------------

    /// <summary>启动消息线程并等待隐藏窗口就绪。之后用 <see cref="Apply"/> 注册组合键。</summary>
    public void Start()
    {
        if (_thread is not null)
            return;

        _ready.Reset();

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "HotkeyWatcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// 应用一组快捷键配置。<paramref name="enabled"/> 为 false 时仅注销。
    /// 返回 false 表示注册失败（通常是被其它程序占用）。
    /// </summary>
    public bool Apply(uint modifiers, uint virtualKey, bool enabled)
    {
        if (_disposed)
            return false;

        if (_thread is null)
            Start();

        if (_threadId == 0 || _hwnd == IntPtr.Zero)
{
            Logger.Warn($"HotkeyService: not ready (thread=0x{_threadId:X}, hwnd=0x{_hwnd.ToInt64():X})");
            return false;
}

        var done = new ManualResetEventSlim(false);

        lock (_gate)
        {
            _pendingModifiers = modifiers;
            _pendingKey = virtualKey;
            _pendingEnabled = enabled;
            _applyDone = done;
            _applyOk = false;
        }

        // 投递到隐藏窗口（不能用 PostThreadMessage：线程消息 hwnd 为 NULL，DispatchMessage 不会派发到 WndProc）
        if (!PowerNative.PostMessageW(_hwnd, WmApplyHotkey, IntPtr.Zero, IntPtr.Zero))
            return false;

        // 消息线程若已意外退出，PostMessage 会成功但没人处理，故必须有超时
        if (!done.Wait(TimeSpan.FromSeconds(3)))
        {
            Logger.Warn("HotkeyService: apply request timed out");
            return false;
        }

        lock (_gate)
        {
            _applyDone = null;
            _registered = _applyOk && enabled;
            return _applyOk;
        }
    }

    /// <summary>注销当前组合键（可再 Apply 重新注册）。</summary>
    public void Unregister()
    {
        if (_threadId == 0 || _hwnd == IntPtr.Zero)
            return;

        var done = new ManualResetEventSlim(false);

        lock (_gate)
        {
            _pendingEnabled = false;
            _applyDone = done;
            _applyOk = false;
        }

        if (!PowerNative.PostMessageW(_hwnd, WmApplyHotkey, IntPtr.Zero, IntPtr.Zero))
            return;

        done.Wait(TimeSpan.FromSeconds(3));
        _registered = false;

        lock (_gate)
        {
            _applyDone = null;
        }
    }

    public void Stop()
    {
        if (_threadId != 0)
            PowerNative.PostThreadMessageW(_threadId, PowerNative.WmQuit, IntPtr.Zero, IntPtr.Zero);

        // 消息循环退出时会自行注销热键并销毁窗口
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        _threadId = 0;
        _hwnd = IntPtr.Zero;
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        _ready.Dispose();
    }

    // ---------------- 消息线程 ----------------

    private void MessageLoop()
    {
        _threadId = PowerNative.GetCurrentThreadId();

        var wcex = new PowerNative.WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<PowerNative.WndClassEx>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            HInstance = PowerNative.GetModuleHandleW(null),
            LpszClassName = ClassName,
        };
        PowerNative.RegisterClassExW(ref wcex);

        _hwnd = PowerNative.CreateWindowExW(
            0, ClassName, null, 0,
            0, 0, 0, 0,
            PowerNative.HwndMessage,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            Logger.Warn("HotkeyService: message window create failed, global hotkey unavailable");

        _ready.Set();

        while (PowerNative.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            PowerNative.TranslateMessage(ref msg);
            PowerNative.DispatchMessageW(ref msg);
        }

        if (_hwnd != IntPtr.Zero)
        {
            PowerNative.UnregisterHotKey(_hwnd, HotkeyId);
            PowerNative.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    /// <summary>在消息线程上执行：按最新配置注销后重新注册。</summary>
    private void HandleApply()
    {
        uint modifiers;
        uint key;
        bool enabled;
        ManualResetEventSlim? done;

        lock (_gate)
        {
            modifiers = _pendingModifiers;
            key = _pendingKey;
            enabled = _pendingEnabled;
            done = _applyDone;
        }

        bool ok = false;

        if (_hwnd != IntPtr.Zero)
        {
            // 先注销旧的：RegisterHotKey 对同一 (hWnd, id) 重复调用不会覆盖，会直接失败
            // （未注册过时这次调用只是白跑一次，返回 false，无副作用）
            PowerNative.UnregisterHotKey(_hwnd, HotkeyId);

            _registered = false;

            if (enabled && key != 0)
            {
                ok = PowerNative.RegisterHotKey(
                    _hwnd, HotkeyId, modifiers | PowerNative.ModNoRepeat, key);

                _registered = ok;

                if (ok)
                    Logger.Info($"HotkeyService: registered {ToDisplayString(modifiers, key)}");
                else
                    Logger.Warn($"HotkeyService: register failed (mods=0x{modifiers:X}, vk=0x{key:X})");
            }
        }

        lock (_gate)
        {
            _applyOk = ok || !enabled;
            _registered = enabled && ok;
        }
        done?.Set();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == PowerNative.WmHotkey)
        {
            if (wParam.ToInt32() == HotkeyId)
            {
                try
                {
                    Pressed?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
            }
            return IntPtr.Zero;
        }

        if (msg == WmApplyHotkey)
        {
            HandleApply();
            return IntPtr.Zero;
        }

        if (msg == WmDestroy)
            return IntPtr.Zero;

        return PowerNative.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ---------------- 显示 / 映射 ----------------

    private static readonly Dictionary<Key, uint> KeyToVk = new();
    private static readonly Dictionary<uint, string> VkToName = new();

    static HotkeyService()
    {
        void Add(Key key, uint vk, string name)
        {
            KeyToVk[key] = vk;
            VkToName[vk] = name;
        }

        for (int i = 0; i < 26; i++)
        {
            char c = (char)('A' + i);
            if (Enum.TryParse<Key>(c.ToString(), out var k))
                Add(k, (uint)(0x41 + i), c.ToString());
        }

        for (int i = 0; i < 10; i++)
        {
            if (Enum.TryParse<Key>($"D{i}", out var k))
                Add(k, (uint)(0x30 + i), i.ToString());
            if (Enum.TryParse<Key>($"NumPad{i}", out var nk))
                Add(nk, (uint)(0x60 + i), $"Num{i}");
        }

        for (int i = 1; i <= 24; i++)
        {
            if (Enum.TryParse<Key>($"F{i}", out var k))
                Add(k, (uint)(0x6F + i), $"F{i}");
        }

        Add(Key.Space, 0x20, "Space");
        Add(Key.Return, 0x0D, "Enter");
        Add(Key.Tab, 0x09, "Tab");
        Add(Key.Back, 0x08, "Backspace");
        Add(Key.Escape, 0x1B, "Esc");
        Add(Key.Insert, 0x2D, "Insert");
        Add(Key.Delete, 0x2E, "Delete");
        Add(Key.Home, 0x24, "Home");
        Add(Key.End, 0x23, "End");
        Add(Key.PageUp, 0x21, "PageUp");
        Add(Key.PageDown, 0x22, "PageDown");
        Add(Key.Left, 0x25, "Left");
        Add(Key.Up, 0x26, "Up");
        Add(Key.Right, 0x27, "Right");
        Add(Key.Down, 0x28, "Down");
        Add(Key.OemMinus, 0xBD, "-");
        Add(Key.OemPlus, 0xBB, "=");
        Add(Key.OemComma, 0xBC, ",");
        Add(Key.OemPeriod, 0xBE, ".");
        Add(Key.OemQuestion, 0xBF, "/");
        Add(Key.OemTilde, 0xC0, "`");
        Add(Key.OemOpenBrackets, 0xDB, "[");
        Add(Key.OemPipe, 0xDC, "\\");
        Add(Key.OemCloseBrackets, 0xDD, "]");
        Add(Key.OemQuotes, 0xDE, "'");
        Add(Key.OemBackslash, 0xE2, "\\");
    }

    /// <summary>Avalonia 按键 → Win32 虚键码。不支持返回 0。</summary>
    public static uint VirtualKeyFromKey(Key key) => KeyToVk.TryGetValue(key, out var vk) ? vk : 0u;

    /// <summary>是否是可用的快捷键主键（不带修饰键也允许的 F1–F24 单独放行）。</summary>
    public static bool IsFunctionKey(Key key)
    {
        // 走虚键码判断，不依赖 Avalonia Key 枚举的取值连续性（VK_F1..VK_F24 = 0x70..0x87）
        uint vk = VirtualKeyFromKey(key);
        return vk >= 0x70 && vk <= 0x87;
    }

    /// <summary>把修饰键位掩码 + 虚键码格式化成 "Ctrl + Alt + H"。</summary>
    public static string ToDisplayString(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>(4);

        if ((modifiers & PowerNative.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & PowerNative.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & PowerNative.ModShift) != 0) parts.Add("Shift");
        if ((modifiers & PowerNative.ModWin) != 0) parts.Add("Win");

        parts.Add(VkToName.TryGetValue(virtualKey, out var name) ? name : $"0x{virtualKey:X2}");

        return string.Join(" + ", parts);
    }
}
