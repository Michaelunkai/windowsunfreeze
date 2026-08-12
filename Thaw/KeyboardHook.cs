using System.Runtime.InteropServices;

namespace Thaw;

/// <summary>
/// Global low-level keyboard hook (WH_KEYBOARD_LL).
///
/// Alt+F4: when the system is slow/stuck, it is intercepted and converted into an
/// "unfreeze" command instead of closing the focused window. When the system is
/// healthy it passes through untouched, so normal Alt+F4 window closing still works.
///
/// Panic hotkey (default Ctrl+Alt+U): always intercepted, always unfreezes.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly Native.HookProc _proc;
    private readonly Func<bool> _shouldInterceptAltF4;
    private readonly Action _onAltF4Intercepted;
    private readonly Action _onPanic;
    private readonly (bool Alt, bool Ctrl, bool Shift, int Vk) _panic;
    private IntPtr _handle;
    private bool _installed;

    // Modifier state tracked from hook events (robust for synthetic/injected input,
    // RDP and cases where GetAsyncKeyState lags behind the key event).
    private bool _altDown, _ctrlDown, _shiftDown;

    public KeyboardHook(Func<bool> shouldInterceptAltF4, Action onAltF4Intercepted, Action onPanic, Config config)
    {
        _shouldInterceptAltF4 = shouldInterceptAltF4;
        _onAltF4Intercepted = onAltF4Intercepted;
        _onPanic = onPanic;
        _panic = config.GetPanicHotkey();
        _proc = HookCallback;
        _altDown = (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;
        _ctrlDown = (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
        _shiftDown = (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;
        _handle = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        _installed = _handle != IntPtr.Zero;
        Log.Info($"Keyboard hook installed: {(_installed ? "yes" : "NO")} (panic {config.PanicHotkey})");
        if (!_installed)
            Log.Error("SetWindowsHookEx failed: " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    public bool IsInstalled => _installed;

    /// <summary>Dev/test helper: drives the real hook callback with a synthetic Alt+F4 event
    /// (proves the decision + swallow + trigger chain without relying on OS input delivery).</summary>
    public void SimulateAltF4ForTest()
    {
        _altDown = true;
        var kbd = new KBDLLHOOKSTRUCT { vkCode = Native.VK_F4, flags = 0x20 }; // LLKHF_ALTDOWN
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<KBDLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(kbd, ptr, false);
            HookCallback(0, (IntPtr)Native.WM_SYSKEYDOWN, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Dev/test helper: simulates the panic hotkey through the real callback.</summary>
    public void SimulatePanicForTest()
    {
        _altDown = _panic.Alt;
        _ctrlDown = _panic.Ctrl;
        _shiftDown = _panic.Shift;
        var kbd = new KBDLLHOOKSTRUCT { vkCode = (uint)_panic.Vk, flags = 0 };
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<KBDLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(kbd, ptr, false);
            HookCallback(0, (IntPtr)Native.WM_KEYDOWN, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN || msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP)
                {
                    var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    TrackModifiers(kbd, msg);
                    Log.Debug($"KEY vk=0x{kbd.vkCode:X2} flags=0x{kbd.flags:X2} alt={_altDown} ctrl={_ctrlDown} shift={_shiftDown}");

                    if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                    {
                        if (kbd.vkCode == Native.VK_F4 && (_altDown || (kbd.flags & 0x20) != 0)) // LLKHF_ALTDOWN
                        {
                            if (_shouldInterceptAltF4())
                            {
                                Log.Debug($"Alt+F4 intercepted -> unfreeze");
                                _onAltF4Intercepted();
                                return (IntPtr)1; // swallow
                            }
                        }
                        else if (MatchesPanic(kbd.vkCode))
                        {
                            Log.Debug($"Panic hotkey -> unfreeze");
                            _onPanic();
                            return (IntPtr)1; // swallow
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Hook callback error", ex);
        }
        return Native.CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    private void TrackModifiers(in KBDLLHOOKSTRUCT kbd, int msg)
    {
        bool up = (kbd.flags & 0x80) != 0; // LLKHF_UP
        switch (kbd.vkCode)
        {
            case 0x12: // VK_MENU (generic)
            case 0xA4: // VK_LMENU
            case 0xA5: // VK_RMENU
                _altDown = !up; break;
            case 0x11: // VK_CONTROL (generic)
            case 0xA2: // VK_LCONTROL
            case 0xA3: // VK_RCONTROL
                _ctrlDown = !up; break;
            case 0x10: // VK_SHIFT (generic)
            case 0xA0: // VK_LSHIFT
            case 0xA1: // VK_RSHIFT
                _shiftDown = !up; break;
        }
    }

    private bool MatchesPanic(uint vk)
    {
        if (vk != (uint)_panic.Vk) return false;
        // Prefer tracked state; fall back to GetAsyncKeyState in case tracking desynced.
        bool alt = _altDown || (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;
        bool ctrl = _ctrlDown || (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
        bool shift = _shiftDown || (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;
        return alt == _panic.Alt && ctrl == _panic.Ctrl && shift == _panic.Shift;
    }

    public void Dispose()
    {
        if (_installed)
        {
            Native.UnhookWindowsHookEx(_handle);
            _installed = false;
            _handle = IntPtr.Zero;
            Log.Info("Keyboard hook removed");
        }
    }

    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
