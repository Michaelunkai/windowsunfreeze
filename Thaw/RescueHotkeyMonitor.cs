using System.Runtime.InteropServices;

namespace Thaw;

/// <summary>
/// Small out-of-process hotkey receiver used by the Thaw-only rescue helper.
/// It deliberately uses RegisterHotKey instead of another low-level hook, so a
/// wedged main process and its hook threads cannot prevent the helper from
/// receiving the configured always-on emergency chords.
/// </summary>
internal sealed class RescueHotkeyMonitor : IDisposable
{
    private const uint WM_QUIT = 0x0012;
    private const uint WM_APP_STOP = 0x8000 + 0x71;
    private const uint WM_HOTKEY = (uint)Native.WM_HOTKEY;
    private const int FirstHotkeyId = 0x5B00;

    private readonly Config _config;
    private readonly Action<RecoveryHotkeyBinding> _onHotkey;
    private readonly object _gate = new();
    private readonly Dictionary<int, RecoveryHotkeyBinding> _registered = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly Thread _thread;
    private int _disposed;
    private int _threadId;

    internal RescueHotkeyMonitor(Config config, Action<RecoveryHotkeyBinding> onHotkey)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _onHotkey = onHotkey ?? throw new ArgumentNullException(nameof(onHotkey));
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "Thaw rescue hotkey monitor",
        };
        try { _thread.Priority = ThreadPriority.Highest; }
        catch (Exception ex) { Log.Debug("Rescue hotkey priority unavailable: " + ex.Message); }
        _thread.Start();
        if (!_ready.Wait(3000))
            Log.Warn("Rescue hotkey monitor did not report readiness within 3 s");
    }

    internal bool IsReady => _ready.IsSet;
    internal int RegisteredCount
    {
        get { lock (_gate) return _registered.Count; }
    }

    private void ThreadMain()
    {
        Volatile.Write(ref _threadId, unchecked((int)GetCurrentThreadId()));
        try
        {
            // RegisterHotKey posts to this thread's queue, so create it before
            // publishing readiness and before the parent can depend on us.
            PeekMessage(out Message initial, IntPtr.Zero, 0, 0, 0);
            RegisterAlwaysHotkeys();
            _ready.Set();

            while (Volatile.Read(ref _disposed) == 0)
            {
                int result = GetMessage(out Message message, IntPtr.Zero, 0, 0);
                if (result <= 0) break;
                if (message.message == WM_APP_STOP || message.message == WM_QUIT)
                    break;
                if (message.message != WM_HOTKEY)
                    continue;

                RecoveryHotkeyBinding? binding;
                lock (_gate) _registered.TryGetValue(message.wParam.ToInt32(), out binding);
                if (binding is null) continue;

                try { _onHotkey(binding); }
                catch (Exception ex) { Log.Error("Rescue hotkey handler failed", ex); }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Rescue hotkey monitor failed", ex);
        }
        finally
        {
            UnregisterAll();
            _ready.Set();
            _stopped.Set();
            Volatile.Write(ref _threadId, 0);
        }
    }

    private void RegisterAlwaysHotkeys()
    {
        int id = FirstHotkeyId;
        foreach (RecoveryHotkeyBinding binding in _config.GetRecoveryHotkeys())
        {
            // RegisterHotKey cannot provide the exact close suppression contract
            // of WH_KEYBOARD_LL, so Alt+F4 remains exclusively hook-based.
            if (binding.Mode != RecoveryHotkeyMode.Always ||
                binding.Action == RecoveryHotkeyAction.AltF4)
                continue;

            uint modifiers = GetNativeModifiers(binding.Chord);
            bool registered = false;
            try
            {
                registered = Native.RegisterHotKey(
                    IntPtr.Zero, id, modifiers | Native.MOD_NOREPEAT, (uint)binding.Chord.Vk);
                if (!registered)
                    registered = Native.RegisterHotKey(
                        IntPtr.Zero, id, modifiers, (uint)binding.Chord.Vk);
            }
            catch (Exception ex)
            {
                Log.Debug($"Rescue RegisterHotKey unavailable for {binding.Chord.Text}: {ex.Message}");
            }

            if (registered)
            {
                lock (_gate) _registered[id] = binding;
                Log.Info("Rescue hotkey registered: " + binding.Chord.Text);
            }
            else
            {
                Log.Warn("Rescue hotkey could not be registered: " + binding.Chord.Text);
            }
            id++;
        }
    }

    private void UnregisterAll()
    {
        int[] ids;
        lock (_gate) ids = _registered.Keys.ToArray();
        foreach (int id in ids)
        {
            try { Native.UnregisterHotKey(IntPtr.Zero, id); } catch { }
        }
        lock (_gate) _registered.Clear();
    }

    private static uint GetNativeModifiers(RecoveryHotkeyChord chord)
    {
        uint modifiers = 0;
        if (chord.Alt) modifiers |= Native.MOD_ALT;
        if (chord.Ctrl) modifiers |= Native.MOD_CONTROL;
        if (chord.Shift) modifiers |= Native.MOD_SHIFT;
        if (chord.Win) modifiers |= Native.MOD_WIN;
        return modifiers;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        int threadId = Volatile.Read(ref _threadId);
        if (threadId != 0)
        {
            try { PostThreadMessage((uint)threadId, WM_APP_STOP, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }
        if (Thread.CurrentThread != _thread)
        {
            try { _thread.Join(1500); } catch { }
            try { _stopped.Wait(1500); } catch { }
        }

        if (Thread.CurrentThread == _thread)
            UnregisterAll();

        if (_stopped.IsSet)
        {
            try { _ready.Dispose(); } catch { }
            try { _stopped.Dispose(); } catch { }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out Message message,
        IntPtr hWnd,
        uint minFilter,
        uint maxFilter,
        uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(
        out Message message,
        IntPtr hWnd,
        uint minFilter,
        uint maxFilter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr hWnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pointX;
        public int pointY;
    }
}
