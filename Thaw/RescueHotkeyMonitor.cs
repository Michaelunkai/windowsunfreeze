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
    private const uint WM_TIMER = 0x0113;
    private const uint WM_APP_STOP = 0x8000 + 0x71;
    private const uint WM_APP_REFRESH = 0x8000 + 0x72;
    private const uint WM_HOTKEY = (uint)Native.WM_HOTKEY;
    private const int FirstHotkeyId = 0x5B00;
    private const uint HealthTimerId = 0x5BFF;
    private const int HealthTimerIntervalMs = 5_000;
    private const int ForcedRenewalIntervalMs = 30_000;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint WAIT_FAILED = 0xFFFFFFFF;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;

    private readonly string _configPath;
    private readonly Action<RecoveryHotkeyBinding> _onHotkey;
    private readonly object _gate = new();
    private readonly Dictionary<int, RecoveryHotkeyBinding> _registered = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly Thread _thread;
    private Config _config;
    private Config? _pendingConfig;
    private long _lastConfigProbeTick;
    private long _lastConfigWriteTicks;
    private long _lastConfigLength = -1;
    private long _lastRegistrationTick;
    private int _disposed;
    private int _threadId;

    internal RescueHotkeyMonitor(Config config, Action<RecoveryHotkeyBinding> onHotkey)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _configPath = Config.DefaultPath();
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
    internal bool IsAlive => _thread.IsAlive && Volatile.Read(ref _threadId) != 0;
    internal int RegisteredCount
    {
        get { lock (_gate) return _registered.Count; }
    }

    private void ThreadMain()
    {
        Volatile.Write(ref _threadId, unchecked((int)GetCurrentThreadId()));
        uint timerId = 0;
        try
        {
            // RegisterHotKey posts to this thread's queue, so create it before
            // publishing readiness and before the parent can depend on us.
            PeekMessage(out Message initial, IntPtr.Zero, 0, 0, 0);
            try { RegisterAlwaysHotkeys(); }
            catch (Exception ex) { Log.Error("Rescue hotkey registration failed; retrying loop", ex); }
            timerId = SetTimer(IntPtr.Zero, HealthTimerId, HealthTimerIntervalMs, IntPtr.Zero);
            if (timerId == 0)
                Log.Debug("Rescue hotkey health timer unavailable");
            _ready.Set();

            while (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    uint waitResult = MsgWaitForMultipleObjectsEx(
                        0, IntPtr.Zero, (uint)HealthTimerIntervalMs, QS_ALLINPUT,
                        MWMO_INPUTAVAILABLE);
                    if (waitResult == WAIT_TIMEOUT)
                    {
                        // This is also the fallback health tick when SetTimer
                        // was rejected or stopped delivering WM_TIMER.
                        MaintainRegistrations();
                        continue;
                    }
                    if (waitResult == WAIT_FAILED)
                    {
                        if (Volatile.Read(ref _disposed) != 0) break;
                        MaintainRegistrations();
                        try { Thread.Sleep(50); } catch { }
                        continue;
                    }

                    int result = GetMessage(out Message message, IntPtr.Zero, 0, 0);
                    if (result == 0)
                    {
                        if (Volatile.Read(ref _disposed) != 0) break;
                        ReRegisterAfterMessageLoopFault("WM_QUIT");
                        continue;
                    }
                    if (result < 0)
                    {
                        if (Volatile.Read(ref _disposed) != 0) break;
                        ReRegisterAfterMessageLoopFault("GetMessage error");
                        try { Thread.Sleep(50); } catch { }
                        continue;
                    }
                    if (message.message == WM_APP_STOP || message.message == WM_QUIT)
                        break;
                    if (message.message == WM_APP_REFRESH)
                    {
                        ApplyPendingConfiguration();
                        continue;
                    }
                    if (timerId != 0 && message.message == WM_TIMER &&
                        message.wParam == new IntPtr(unchecked((long)timerId)))
                    {
                        MaintainRegistrations();
                        continue;
                    }
                    if (message.message != WM_HOTKEY)
                        continue;

                    RecoveryHotkeyBinding? binding;
                    lock (_gate) _registered.TryGetValue(message.wParam.ToInt32(), out binding);
                    if (binding is null) continue;

                    try { _onHotkey(binding); }
                    catch (Exception ex) { Log.Error("Rescue hotkey handler failed", ex); }
                }
                catch (Exception ex)
                {
                    if (Volatile.Read(ref _disposed) != 0) break;
                    Log.Error("Rescue hotkey message loop failed; retrying", ex);
                    ReRegisterAfterMessageLoopFault(ex.GetType().Name);
                    try { Thread.Sleep(50); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Rescue hotkey monitor failed", ex);
        }
        finally
        {
            if (timerId != 0)
            {
                try { KillTimer(IntPtr.Zero, timerId); } catch { }
            }
            UnregisterAll();
            _ready.Set();
            _stopped.Set();
            Volatile.Write(ref _threadId, 0);
        }
    }

    private void ReRegisterAfterMessageLoopFault(string reason)
    {
        try { UnregisterAll(); } catch { }
        try
        {
            RegisterAlwaysHotkeys();
            Log.Info("Rescue hotkeys re-registered after message-loop fault (" + reason + ")");
        }
        catch (Exception ex)
        {
            Log.Error("Rescue hotkey re-registration failed", ex);
        }
    }

    /// <summary>
    /// Cheap helper-side configuration polling. The main process owns the
    /// authoritative reload path; this keeps the independent RegisterHotKey
    /// safety net aligned when the JSON changes while Thaw is running.
    /// </summary>
    internal void RefreshConfigurationIfChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastConfigProbeTick);
        if (previous != 0 && now - previous < 1_000) return;
        Interlocked.Exchange(ref _lastConfigProbeTick, now);

        try
        {
            var info = new FileInfo(_configPath);
            long writeTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
            long length = info.Exists ? info.Length : 0;
            if (writeTicks == Interlocked.Read(ref _lastConfigWriteTicks) &&
                length == Interlocked.Read(ref _lastConfigLength))
                return;

            // Avoid applying a partially written JSON file. If metadata changes
            // during the read, leave the pending reload for the next poll.
            Config candidate = Config.Load(_configPath);
            var after = new FileInfo(_configPath);
            long afterWriteTicks = after.Exists ? after.LastWriteTimeUtc.Ticks : 0;
            long afterLength = after.Exists ? after.Length : 0;
            if (writeTicks != afterWriteTicks || length != afterLength) return;

            lock (_gate) _pendingConfig = candidate;
            Interlocked.Exchange(ref _lastConfigWriteTicks, writeTicks);
            Interlocked.Exchange(ref _lastConfigLength, length);
            int threadId = Volatile.Read(ref _threadId);
            bool posted = threadId != 0 &&
                          PostThreadMessage((uint)threadId, WM_APP_REFRESH, IntPtr.Zero, IntPtr.Zero);
            if (!posted)
            {
                // Keep the metadata dirty so a live monitor restart can retry
                // the configuration hand-off instead of losing the pending
                // registration update forever.
                Interlocked.Exchange(ref _lastConfigWriteTicks, long.MinValue);
                Interlocked.Exchange(ref _lastConfigLength, long.MinValue);
                Log.Debug("Rescue hotkey configuration refresh post failed");
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Rescue hotkey configuration refresh failed: " + ex.Message);
        }
    }

    private void ApplyPendingConfiguration()
    {
        Config? pending;
        lock (_gate)
        {
            pending = _pendingConfig;
            _pendingConfig = null;
        }
        if (pending is null || Volatile.Read(ref _disposed) != 0) return;

        Volatile.Write(ref _config, pending);
        try { UnregisterAll(); } catch { }
        try
        {
            RegisterAlwaysHotkeys();
            Log.Info("Rescue hotkeys re-registered after configuration reload");
        }
        catch (Exception ex)
        {
            Log.Error("Rescue hotkey configuration reload failed", ex);
        }
    }

    private void RegisterAlwaysHotkeys()
    {
        Config config = Volatile.Read(ref _config);
        int id = FirstHotkeyId;
        foreach (RecoveryHotkeyBinding binding in config.GetAlwaysFallbackHotkeys())
        {
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
        Volatile.Write(ref _lastRegistrationTick, Environment.TickCount64);
    }

    private void MaintainRegistrations()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Config config = Volatile.Read(ref _config);
        int expected = config.GetAlwaysFallbackHotkeys().Count;
        int actual = RegisteredCount;
        long now = Environment.TickCount64;
        long previous = Volatile.Read(ref _lastRegistrationTick);
        bool missing = actual < expected;
        bool renewalDue = previous == 0 || now - previous >= ForcedRenewalIntervalMs;
        if (!missing && !renewalDue) return;

        try { UnregisterAll(); } catch { }
        try
        {
            RegisterAlwaysHotkeys();
            Log.Info("Rescue hotkeys renewed (registered=" + RegisteredCount + ", expected=" + expected + ")");
        }
        catch (Exception ex)
        {
            Log.Error("Rescue hotkey renewal failed", ex);
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
    private static extern uint MsgWaitForMultipleObjectsEx(
        uint count,
        IntPtr handles,
        uint milliseconds,
        uint wakeMask,
        uint flags);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SetTimer(
        IntPtr hWnd,
        uint nIDEvent,
        uint uElapse,
        IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr hWnd, uint uIDEvent);

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
