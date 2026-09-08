using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Thaw;

/// <summary>Details of one accepted recovery chord.</summary>
internal sealed class RecoveryHotkeyEventArgs : EventArgs
{
    internal RecoveryHotkeyEventArgs(RecoveryHotkeyBinding binding)
    {
        Action = binding.Action;
        Mode = binding.Mode;
        SettingName = binding.SettingName;
        Hotkey = binding.Chord.Text;
    }

    internal RecoveryHotkeyEventArgs(RecoveryHotkeyAction action, RecoveryHotkeyMode mode, string settingName, string hotkey)
    {
        Action = action;
        Mode = mode;
        SettingName = settingName;
        Hotkey = hotkey;
    }

    public RecoveryHotkeyAction Action { get; }
    public RecoveryHotkeyMode Mode { get; }
    public string SettingName { get; }
    public string Hotkey { get; }
}

/// <summary>
/// Allocation-free receipt describing the key capture boundary. The receipt is
/// intentionally separate from <see cref="RecoveryHotkeyEventArgs"/> so a
/// recovery subscriber cannot delay the low-level hook callback.
/// </summary>
internal readonly struct KeyboardCaptureReceipt
{
    internal KeyboardCaptureReceipt(
        long sequence,
        RecoveryHotkeyAction action,
        string hotkey,
        long capturedAtStopwatchTicks,
        long capturedAtUtcTicks,
        long rescueSequence = 0)
    {
        Sequence = sequence;
        Action = action;
        Hotkey = hotkey;
        CapturedAtStopwatchTicks = capturedAtStopwatchTicks;
        CapturedAtUtcTicks = capturedAtUtcTicks;
        RescueSequence = rescueSequence;
    }

    public long Sequence { get; }
    public RecoveryHotkeyAction Action { get; }
    public string Hotkey { get; }
    public long CapturedAtStopwatchTicks { get; }
    public long CapturedAtUtcTicks { get; }
    /// <summary>Exact rescue-broker sequence, or zero when no rescue pulse was published.</summary>
    public long RescueSequence { get; }
    public DateTime CapturedAtUtc => new(CapturedAtUtcTicks, DateTimeKind.Utc);
}

/// <summary>
/// Global low-level keyboard hook (WH_KEYBOARD_LL).
///
/// Recovery chords are validated by <see cref="Config"/>, require at least two modifiers,
/// and are compared against the complete modifier state. The callback only decides whether
/// to swallow a key and queues an event; recovery subscribers always run on a dedicated
/// pre-warmed dispatch worker, never inside the low-level hook callback. The hook itself is
/// installed and pumped by a separate high-priority native message thread, not WinForms.
///
/// Alt+F4 retains the legacy mode behavior. The default panic chord is Ctrl+Alt+U; the
/// additional defaults Ctrl+Alt+S (slowness) and Ctrl+Alt+G (frame drops) are configurable.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const uint WM_QUIT = 0x0012;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_APP = 0x8000;
    private const uint WM_APP_STOP = WM_APP + 0x41;
    private const uint WM_APP_REINSTALL = WM_APP + 0x42;
    private const uint WM_APP_RESCUE_STOP = WM_APP + 0x43;
    private const uint WM_APP_RESCUE_REINSTALL = WM_APP + 0x44;
    private const uint HOOK_TIMER_ID = 0x5448;
    private const uint RESCUE_HOOK_TIMER_ID = 0x5449;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint WAIT_FAILED = 0xFFFFFFFF;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;
    private const uint MESSAGE_POLL_MS = 500;
    private const int HOOK_HEARTBEAT_INTERVAL_MS = 250;
    private const int HOOK_STALE_MS = 2500;
    private const int HOOK_RENEWAL_MS = 3000;
    private const int RESCUE_HOOK_HEARTBEAT_INTERVAL_MS = 500;
    private const int RESCUE_HOOK_RENEWAL_MS = 5000;
    private const int HOOK_SUPERVISOR_INTERVAL_MS = 500;
    private const int HOOK_SUPERVISOR_RETRY_MS = 1000;
    private const int FALLBACK_HOTKEY_RETRY_MS = 1000;
    private const int FALLBACK_HOTKEY_RENEWAL_MS = 30_000;
    private const int DISPATCH_SLOT_COUNT = 32;
    private const int DEBOUNCE_SLOT_COUNT = 16;
    private const int EMERGENCY_WRITING = 3;
    private const string DEBOUNCE_CLAIM = "\0";

    private const uint LLKHF_LOWER_IL_INJECTED = 0x02;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLKHF_ALTDOWN = 0x20;
    private const uint LLKHF_UP = 0x80;

    private readonly Native.HookProc _proc;
    private readonly Native.HookProc _rescueProc;
    private readonly Func<bool>? _shouldInterceptAltF4;
    private readonly Func<bool>? _shouldAllowStressed;
    private readonly Action? _onAltF4Intercepted;
    private readonly Action? _onPanic;
    private readonly RescueBroker _rescueBroker;
    private readonly bool _ownsRescueBroker;
    private Config _config;
    private readonly object _threadGate = new();
    private readonly object _dispatchGate = new();
    // Capture/debounce state is touched by both independent hook callbacks and
    // the registered-hotkey message path. Fixed atomic slots keep config reloads
    // and simultaneous callbacks from ever waiting on a managed collection lock.
    private readonly DebounceSlot[] _debounceSlots = CreateDebounceSlots();
    private readonly HashSet<uint> _downKeys = new();
    private readonly HashSet<uint> _rescueDownKeys = new();
    private readonly DispatchSlot[] _dispatchSlots = CreateDispatchSlots();
    private readonly Queue<DispatchSlot> _dispatchQueue = new(DISPATCH_SLOT_COUNT);
    private readonly AutoResetEvent _dispatchWake = new(false);
    private readonly ManualResetEventSlim _hookReady = new(false);
    private readonly ManualResetEventSlim _hookStopped = new(false);
    private readonly ManualResetEventSlim _rescueReady = new(false);
    private readonly ManualResetEventSlim _rescueStopped = new(false);
    private readonly ManualResetEventSlim _hookSupervisorStop = new(false);
    private readonly ManualResetEventSlim _hookSupervisorStopped = new(false);
    private readonly ManualResetEventSlim _fallbackDispatchStopped = new(false);
    private readonly AutoResetEvent _fallbackDispatchWake = new(false);
    private Thread? _hookThread;
    private Thread? _rescueThread;
    private Thread? _dispatchThread;
    private Thread? _fallbackDispatchThread;
    private Thread? _hookSupervisorThread;

    private RecoveryHotkeyBinding[] _bindings = Array.Empty<RecoveryHotkeyBinding>();
    private long _debounceTicks;
    private IntPtr _handle;
    private IntPtr _rescueHandle;
    private int _disposed;
    private bool _installed;
    private bool _rescueInstalled;
    private int _hookThreadId;
    private int _rescueThreadId;
    private int _dispatchThreadId;
    private long _lastHookCallbackTicks;
    private long _lastHookInstallTicks;
    private long _lastRescueCallbackTicks;
    private long _lastRescueInstallTicks;
    private long _lastHeartbeatTicks;
    private long _lastHookSupervisorRepairTicks;
    private long _lastRescueSupervisorRepairTicks;
    private long _lastFallbackHotkeyRefreshTicks;
    private long _captureCount;
    private CaptureBox? _lastCapture;
    private readonly Dictionary<int, RecoveryHotkeyBinding> _registeredFallbackHotkeys = new();
    private readonly DispatchSlot _emergencyDispatchSlot = new();
    private readonly DispatchSlot _fallbackDispatchSlot = new();
    private int _emergencyDispatchState;
    private int _fallbackDispatchState;
    private bool _fallbackHotkeysEnabled;
    private int _registeredFallbackHotkeyCount;
    private long _droppedDispatchCount;
    private long _callbackErrorCount;

    // Modifier state tracked from hook events (robust for synthetic/injected input,
    // RDP and cases where GetAsyncKeyState lags behind the key event).
    private bool _altDown, _ctrlDown, _shiftDown, _winDown;
    private bool _rescueAltDown, _rescueCtrlDown, _rescueShiftDown, _rescueWinDown;

    /// <summary>
    /// Raised for every accepted recovery action. Delivery is asynchronous and exceptions
    /// from subscribers are isolated so one subscriber cannot break the hook.
    /// </summary>
    public event EventHandler<RecoveryHotkeyEventArgs>? RecoveryActionRequested;

    /// <summary>Short alias for callers that prefer action-oriented naming.</summary>
    public event EventHandler<RecoveryHotkeyEventArgs>? ActionRequested
    {
        add => RecoveryActionRequested += value;
        remove => RecoveryActionRequested -= value;
    }

    /// <summary>Alias retained for callers that model all recovery requests uniformly.</summary>
    public event EventHandler<RecoveryHotkeyEventArgs>? RecoveryRequested
    {
        add => RecoveryActionRequested += value;
        remove => RecoveryActionRequested -= value;
    }

    /// <summary>New API: create a hook with queued action events.</summary>
    public KeyboardHook(
        Config config,
        Func<bool>? shouldAllowStressed = null,
        RescueBroker? rescueBroker = null)
        : this(config, shouldAllowStressed, null, null, null, rescueBroker)
    {
    }

    /// <summary>
    /// Compatibility constructor for the original Alt+F4/panic callback API. Callbacks are
    /// still delivered asynchronously, so old subscribers also cannot run recovery work on
    /// the low-level hook callback thread.
    /// </summary>
    public KeyboardHook(
        Func<bool> shouldInterceptAltF4,
        Action onAltF4Intercepted,
        Action onPanic,
        Config config,
        RescueBroker? rescueBroker = null)
        : this(config, shouldInterceptAltF4, shouldInterceptAltF4, onAltF4Intercepted, onPanic, rescueBroker)
    {
    }

    private KeyboardHook(
        Config config,
        Func<bool>? shouldAllowStressed,
        Func<bool>? shouldInterceptAltF4,
        Action? onAltF4Intercepted,
        Action? onPanic,
        RescueBroker? rescueBroker)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _shouldAllowStressed = shouldAllowStressed;
        _shouldInterceptAltF4 = shouldInterceptAltF4;
        _onAltF4Intercepted = onAltF4Intercepted;
        _onPanic = onPanic;
        _proc = HookCallback;
        _rescueProc = RescueHookCallback;
        _rescueBroker = rescueBroker ?? new RescueBroker();
        _ownsRescueBroker = rescueBroker is null;
        _lastHookCallbackTicks = Stopwatch.GetTimestamp();
        _lastHookInstallTicks = _lastHookCallbackTicks;
        _lastRescueCallbackTicks = _lastHookCallbackTicks;
        _lastRescueInstallTicks = _lastHookCallbackTicks;
        // Keep an in-process RegisterHotKey route even when the independent
        // rescue helper is enabled. The normal low-level hook swallows the
        // chord, while either registered route can recover if both hooks are
        // unavailable; the broker acknowledgement prevents helper duplication.
        Volatile.Write(ref _fallbackHotkeysEnabled, true);

        ApplyConfiguration(config);

        _altDown = (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;
        _ctrlDown = (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
        _shiftDown = (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;
        _winDown = (Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
                   (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0;

        StartDispatchThreadIfNeeded(fallback: false, reason: "initial");

        // Keep a second, independent delivery worker pre-warmed. It is only
        // used when the normal ring and its reserved slot are both occupied,
        // so a burst or a transient dispatch-worker fault cannot turn a
        // captured chord into a silent no-op.
        StartDispatchThreadIfNeeded(fallback: true, reason: "initial");

        StartHookThreadIfNeeded(rescue: false, reason: "initial");

        if (!_hookReady.Wait(3000))
            Log.Error("Keyboard hook thread did not report readiness within 3 s");

        StartHookThreadIfNeeded(rescue: true, reason: "initial");

        if (!_rescueReady.Wait(3000))
            Log.Error("Emergency keyboard hook thread did not report readiness within 3 s");

        try
        {
            Thread supervisor = new Thread(HookSupervisorThreadMain)
            {
                IsBackground = true,
                Name = "Thaw keyboard hook supervisor"
            };
            _hookSupervisorThread = supervisor;
            SetHighPriority(supervisor);
            supervisor.Start();
        }
        catch (Exception ex)
        {
            _hookSupervisorThread = null;
            Log.Error("Keyboard hook supervisor start failed; hook heartbeats will supervise dispatch", ex);
        }

        Log.Info($"Keyboard hook paths ready: primary={(IsInstalled ? "installed" : "NO hook")}, " +
                 $"emergency={(IsEmergencyInstalled ? "installed" : "NO hook")}, " +
                 $"registered-fallback={RegisteredFallbackHotkeyCount} " +
                 $"(panic {config.PanicHotkey}, slow {config.SlownessHotkey}, frame {config.FrameDropHotkey})");
    }

    public bool IsInstalled => Volatile.Read(ref _installed);

    /// <summary>Whether the independent emergency hook is currently installed.</summary>
    public bool IsEmergencyInstalled => Volatile.Read(ref _rescueInstalled);

    /// <summary>Number of always-on RegisterHotKey fallbacks owned by this instance.</summary>
    public int RegisteredFallbackHotkeyCount => Volatile.Read(ref _registeredFallbackHotkeyCount);

    /// <summary>Whether at least one capture route is currently available.</summary>
    public bool HasCapturePath => IsInstalled || IsEmergencyInstalled || RegisteredFallbackHotkeyCount > 0;

    /// <summary>Accepted captures that could not fit even the reserved emergency slot.</summary>
    public long DroppedDispatchCount => Interlocked.Read(ref _droppedDispatchCount);

    /// <summary>Callback faults are counted without doing file I/O on the hook thread.</summary>
    public long CallbackErrorCount => Interlocked.Read(ref _callbackErrorCount);

    /// <summary>Named-event redundancy owned by this hook.</summary>
    public RescueBroker RescueBroker => _rescueBroker;

    /// <summary>Most recent accepted capture receipt; default means no accepted capture yet.</summary>
    public KeyboardCaptureReceipt LastCapture
    {
        get
        {
            CaptureBox? box = Volatile.Read(ref _lastCapture);
            return box?.Value ?? default;
        }
    }

    public long CaptureCount => Interlocked.Read(ref _captureCount);

    /// <summary>
    /// Lets an integration explicitly acknowledge the latest delivered capture.
    /// The normal dispatch worker acknowledges automatically after delivery; this
    /// compatibility method is sequence-gated so it cannot create duplicate pulses.
    /// </summary>
    public bool AcknowledgeLastCapture()
    {
        KeyboardCaptureReceipt capture = LastCapture;
        return capture.RescueSequence != 0 && _rescueBroker.Acknowledge(capture.RescueSequence);
    }

    /// <summary>Heartbeat age in milliseconds, useful to a support/diagnostics surface.</summary>
    public long HeartbeatAgeMs
    {
        get
        {
            long heartbeat = Volatile.Read(ref _lastHeartbeatTicks);
            if (heartbeat <= 0) return long.MaxValue;
            return (long)((Stopwatch.GetTimestamp() - heartbeat) * 1000d / Stopwatch.Frequency);
        }
    }

    /// <summary>Requests an immediate reinstall on the dedicated hook thread.</summary>
    public bool RequestReinstall()
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        bool posted = false;
        int threadId = Volatile.Read(ref _hookThreadId);
        StartHookThreadIfNeeded(rescue: false, reason: "requested");
        if (threadId != 0)
            posted |= PostThreadMessage((uint)threadId, WM_APP_REINSTALL, IntPtr.Zero, IntPtr.Zero);

        StartHookThreadIfNeeded(rescue: true, reason: "requested");
        int rescueThreadId = Volatile.Read(ref _rescueThreadId);
        if (rescueThreadId != 0)
            posted |= PostThreadMessage((uint)rescueThreadId, WM_APP_RESCUE_REINSTALL, IntPtr.Zero, IntPtr.Zero);
        return posted;
    }

    /// <summary>
    /// Restarts a hook message thread after an escaped message-loop/native failure.
    /// Each hook normally repairs itself, but a dead thread cannot run its own
    /// heartbeat; this supervisor is deliberately independent of both hook loops.
    /// </summary>
    private void HookSupervisorThreadMain()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    if (_hookSupervisorStop.Wait(HOOK_SUPERVISOR_INTERVAL_MS)) break;
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Log.Error("Keyboard hook supervisor wait failed; retrying", ex);
                    try { Thread.Sleep(25); } catch { }
                    continue;
                }

                if (Volatile.Read(ref _disposed) != 0) break;
                try { EnsureHookPath(rescue: false); }
                catch (Exception ex)
                {
                    Log.Error("Primary keyboard hook supervision failed; retrying", ex);
                }
                try { EnsureHookPath(rescue: true); }
                catch (Exception ex)
                {
                    Log.Error("Emergency keyboard hook supervision failed; retrying", ex);
                }
                try { EnsureDispatchPath(fallback: false); }
                catch (Exception ex)
                {
                    Log.Error("Primary keyboard dispatch supervision failed; retrying", ex);
                }
                try { EnsureDispatchPath(fallback: true); }
                catch (Exception ex)
                {
                    Log.Error("Emergency keyboard dispatch supervision failed; retrying", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Keyboard hook supervisor failed", ex);
        }
        finally
        {
            try { _hookSupervisorStopped.Set(); } catch { }
        }
    }

    private void EnsureDispatchPath(bool fallback)
    {
        Thread? worker = fallback
            ? Volatile.Read(ref _fallbackDispatchThread)
            : Volatile.Read(ref _dispatchThread);
        if (worker?.IsAlive == true) return;

        StartDispatchThreadIfNeeded(fallback, "supervisor");
    }

    private void StartDispatchThreadIfNeeded(bool fallback, string reason)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        lock (_threadGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            Thread? existing = fallback
                ? Volatile.Read(ref _fallbackDispatchThread)
                : Volatile.Read(ref _dispatchThread);
            if (existing?.IsAlive == true) return;

            Thread? replacement = null;
            try
            {
                replacement = new Thread(fallback ? FallbackDispatchThreadMain : DispatchThreadMain)
                {
                    IsBackground = true,
                    Name = fallback ? "Thaw emergency hotkey dispatch" : "Thaw hotkey dispatch",
                };
                SetHighPriority(replacement);
                if (fallback)
                    Volatile.Write(ref _fallbackDispatchThread, replacement);
                else
                    Volatile.Write(ref _dispatchThread, replacement);

                replacement.Start();
                if (!string.Equals(reason, "initial", StringComparison.OrdinalIgnoreCase))
                    Log.Info((fallback ? "Emergency" : "Primary") +
                             " hotkey dispatch worker restarted (" + reason + ")");
            }
            catch (Exception ex)
            {
                if (replacement is not null && fallback)
                {
                    if (ReferenceEquals(Volatile.Read(ref _fallbackDispatchThread), replacement))
                        Volatile.Write(ref _fallbackDispatchThread, null);
                }
                else if (replacement is not null && ReferenceEquals(Volatile.Read(ref _dispatchThread), replacement))
                {
                    Volatile.Write(ref _dispatchThread, null);
                }
                Log.Error((fallback ? "Emergency" : "Primary") +
                          " hotkey dispatch worker start failed", ex);
            }
        }
    }

    private void EnsureHookPath(bool rescue)
    {
        Thread? thread = rescue
            ? Volatile.Read(ref _rescueThread)
            : Volatile.Read(ref _hookThread);
        bool alive = thread?.IsAlive == true;
        bool installed = rescue ? IsEmergencyInstalled : IsInstalled;
        if (!alive)
        {
            Log.Warn((rescue ? "Emergency" : "Primary") +
                     " keyboard hook thread exited; restarting capture path");
            StartHookThreadIfNeeded(rescue, "supervisor");
            return;
        }

        if (installed) return;

        ref long lastRepair = ref (rescue
            ? ref _lastRescueSupervisorRepairTicks
            : ref _lastHookSupervisorRepairTicks);
        long now = Stopwatch.GetTimestamp();
        long previous = Volatile.Read(ref lastRepair);
        if (previous != 0 && now - previous < ToStopwatchTicks(HOOK_SUPERVISOR_RETRY_MS))
            return;
        Volatile.Write(ref lastRepair, now);

        int threadId = rescue
            ? Volatile.Read(ref _rescueThreadId)
            : Volatile.Read(ref _hookThreadId);
        uint message = rescue ? WM_APP_RESCUE_REINSTALL : WM_APP_REINSTALL;
        if (threadId != 0 && !PostThreadMessage((uint)threadId, message, IntPtr.Zero, IntPtr.Zero))
            Log.Debug((rescue ? "Emergency" : "Primary") +
                      " keyboard hook supervisor reinstall post failed");
    }

    private void StartHookThreadIfNeeded(bool rescue, string reason)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        lock (_threadGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            Thread? existing = rescue ? _rescueThread : _hookThread;
            if (existing?.IsAlive == true) return;

            ThreadStart entry = rescue ? RescueHookThreadMain : HookThreadMain;
            Thread? replacement = null;
            try
            {
                replacement = new Thread(entry)
                {
                    IsBackground = true,
                    Name = rescue ? "Thaw emergency keyboard hook" : "Thaw low-level keyboard hook",
                };
                SetHighPriority(replacement);
                if (rescue) _rescueThread = replacement;
                else _hookThread = replacement;

                replacement.Start();
                if (!string.Equals(reason, "initial", StringComparison.OrdinalIgnoreCase))
                    Log.Info((rescue ? "Emergency" : "Primary") +
                             " keyboard hook thread restarted (" + reason + ")");
            }
            catch (Exception ex)
            {
                if (rescue)
                {
                    if (replacement is not null && ReferenceEquals(_rescueThread, replacement)) _rescueThread = null;
                    _rescueReady.Set();
                }
                else
                {
                    if (replacement is not null && ReferenceEquals(_hookThread, replacement)) _hookThread = null;
                    _hookReady.Set();
                }
                Log.Error((rescue ? "Emergency" : "Primary") +
                          " keyboard hook thread start failed", ex);
            }
        }
    }

    /// <summary>
    /// Rebuilds the validated binding table after a config reload. The caller owns the
    /// Config instance; this method does not rewrite raw values. The subsequent bounded
    /// reinstall clears stale pressed-key state so a missed key-up cannot suppress the
    /// next real trigger.
    /// </summary>
    public IReadOnlyList<HotkeyValidationIssue> ReloadConfiguration(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        IReadOnlyList<HotkeyValidationIssue> issues = ApplyConfiguration(config);
        Volatile.Write(ref _fallbackHotkeysEnabled, true);
        _ = RequestReinstall();
        return issues;
    }

    /// <summary>Dev/test helper: drives the real Alt+F4 decision path with synthetic input.</summary>
    public void SimulateAltF4ForTest()
    {
        _altDown = true;
        _downKeys.Remove((uint)Native.VK_F4);
        var kbd = new KBDLLHOOKSTRUCT { vkCode = Native.VK_F4, flags = LLKHF_ALTDOWN };
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

    /// <summary>Dev/test helper: simulates the configured panic hotkey through the real callback.</summary>
    public void SimulatePanicForTest()
    {
        RecoveryHotkeyChord panic = GetPanicChord();
        _altDown = panic.Alt;
        _ctrlDown = panic.Ctrl;
        _shiftDown = panic.Shift;
        _winDown = panic.Win;
        _downKeys.Remove((uint)panic.Vk);
        var kbd = new KBDLLHOOKSTRUCT { vkCode = (uint)panic.Vk, flags = 0 };
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

    private IReadOnlyList<HotkeyValidationIssue> ApplyConfiguration(Config config)
    {
        Volatile.Write(ref _config, config);
        RecoveryHotkeyBinding[] bindings = config.GetRecoveryHotkeys().ToArray();
        IReadOnlyList<HotkeyValidationIssue> issues = config.ValidateHotkeys();
        Volatile.Write(ref _bindings, bindings);
        Volatile.Write(ref _debounceTicks, ToStopwatchTicks(config.GetHotkeyDebounceMs()));
        ClearDebounceSlots();

        foreach (HotkeyValidationIssue issue in issues)
            Log.Warn($"Hotkey config {issue.SettingName}: {issue.Message}; effective {issue.EffectiveBinding}");
        return issues;
    }

    private void HookThreadMain()
    {
        uint timerId = 0;
        try
        {
            Volatile.Write(ref _hookThreadId, unchecked((int)GetCurrentThreadId()));

            // Force creation of this thread's message queue before the constructor
            // publishes readiness; PostThreadMessage is otherwise allowed to fail.
            PeekMessage(out HookMessage initialMessage, IntPtr.Zero, 0, 0, 0);
            InstallHook("initial");
            RegisterFallbackHotkeys("initial");
            timerId = SetTimer(IntPtr.Zero, HOOK_TIMER_ID, HOOK_HEARTBEAT_INTERVAL_MS, IntPtr.Zero);
            _hookReady.Set();

            if (timerId == 0)
                Log.Error("Keyboard hook heartbeat timer could not be created");

            while (Volatile.Read(ref _disposed) == 0)
            {
                uint waitResult = MsgWaitForMultipleObjectsEx(
                    0, IntPtr.Zero, MESSAGE_POLL_MS, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                if (waitResult == WAIT_TIMEOUT)
                {
                    HookHeartbeat();
                    continue;
                }
                if (waitResult == WAIT_FAILED)
                {
                    if (Volatile.Read(ref _disposed) != 0) break;
                    HookHeartbeat();
                    try { Thread.Sleep(25); } catch { }
                    continue;
                }

                int result = GetMessage(out HookMessage message, IntPtr.Zero, 0, 0);
                if (result <= 0) break;

                if (message.message == WM_APP_STOP || message.message == WM_QUIT)
                    break;
                if (message.message == WM_APP_REINSTALL)
                {
                    InstallHook("requested");
                    RegisterFallbackHotkeys("requested");
                    continue;
                }
                if (message.message == (uint)Native.WM_HOTKEY)
                {
                    HandleRegisteredHotkey(message.wParam.ToInt32());
                    continue;
                }
                if (message.message == WM_TIMER && message.wParam == new IntPtr(unchecked((long)timerId)))
                {
                    HookHeartbeat();
                    continue;
                }

                // The low-level hook itself does not require TranslateMessage or
                // DispatchMessage; retaining a tiny message loop avoids WinForms
                // reentrancy and keeps this thread independent of the tray UI.
            }
        }
        catch (Exception ex)
        {
            Log.Error("Keyboard hook thread failed", ex);
        }
        finally
        {
            if (timerId != 0)
            {
                try { KillTimer(IntPtr.Zero, timerId); } catch { }
            }

            UnregisterFallbackHotkeys();
            RemoveHook();
            _hookReady.Set();
            _hookStopped.Set();
            if (ReferenceEquals(Volatile.Read(ref _hookThread), Thread.CurrentThread))
                Volatile.Write(ref _hookThreadId, 0);
        }
    }

    /// <summary>
    /// A second WH_KEYBOARD_LL hook lives on its own message-pump thread. Windows
    /// calls hooks in chain order; this hook is installed after the primary hook,
    /// so it becomes the first capture opportunity while the primary remains a
    /// fallback if this thread or handle ever fails. It shares only the bounded
    /// action queue and debounce table with the primary path.
    /// </summary>
    private void RescueHookThreadMain()
    {
        uint timerId = 0;
        try
        {
            Volatile.Write(ref _rescueThreadId, unchecked((int)GetCurrentThreadId()));
            PeekMessage(out HookMessage initialMessage, IntPtr.Zero, 0, 0, 0);
            InstallRescueHook("initial");
            timerId = SetTimer(IntPtr.Zero, RESCUE_HOOK_TIMER_ID,
                RESCUE_HOOK_HEARTBEAT_INTERVAL_MS, IntPtr.Zero);
            _rescueReady.Set();

            if (timerId == 0)
                Log.Error("Emergency keyboard hook heartbeat timer could not be created");

            while (Volatile.Read(ref _disposed) == 0)
            {
                uint waitResult = MsgWaitForMultipleObjectsEx(
                    0, IntPtr.Zero, MESSAGE_POLL_MS, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                if (waitResult == WAIT_TIMEOUT)
                {
                    RescueHookHeartbeat();
                    continue;
                }
                if (waitResult == WAIT_FAILED)
                {
                    if (Volatile.Read(ref _disposed) != 0) break;
                    RescueHookHeartbeat();
                    try { Thread.Sleep(25); } catch { }
                    continue;
                }

                int result = GetMessage(out HookMessage message, IntPtr.Zero, 0, 0);
                if (result <= 0) break;

                if (message.message == WM_APP_RESCUE_STOP || message.message == WM_QUIT)
                    break;
                if (message.message == WM_APP_RESCUE_REINSTALL)
                {
                    InstallRescueHook("requested");
                    continue;
                }
                if (message.message == WM_TIMER &&
                    message.wParam == new IntPtr(unchecked((long)timerId)))
                {
                    RescueHookHeartbeat();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Emergency keyboard hook thread failed", ex);
        }
        finally
        {
            if (timerId != 0)
            {
                try { KillTimer(IntPtr.Zero, timerId); } catch { }
            }

            RemoveRescueHook();
            _rescueReady.Set();
            _rescueStopped.Set();
            if (ReferenceEquals(Volatile.Read(ref _rescueThread), Thread.CurrentThread))
                Volatile.Write(ref _rescueThreadId, 0);
        }
    }

    private void InstallRescueHook(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        // A hook can disappear while a key is held, so do not carry a stale
        // down-state across a reinstall and suppress the next real chord.
        _rescueDownKeys.Clear();
        _rescueAltDown = IsKeyDown(Native.VK_MENU);
        _rescueCtrlDown = IsKeyDown(Native.VK_CONTROL);
        _rescueShiftDown = IsKeyDown(Native.VK_SHIFT);
        _rescueWinDown = IsKeyDown(0x5B) || IsKeyDown(0x5C);

        if (_rescueHandle != IntPtr.Zero)
        {
            try { Native.UnhookWindowsHookEx(_rescueHandle); } catch { }
            _rescueHandle = IntPtr.Zero;
            Volatile.Write(ref _rescueInstalled, false);
        }

        IntPtr handle = Native.SetWindowsHookEx(
            Native.WH_KEYBOARD_LL,
            _rescueProc,
            Native.GetModuleHandle(null),
            0);
        if (handle == IntPtr.Zero)
        {
            Volatile.Write(ref _rescueInstalled, false);
            Log.Error("Emergency SetWindowsHookEx failed (" + reason + "): " +
                      new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
            return;
        }

        _rescueHandle = handle;
        Volatile.Write(ref _rescueInstalled, true);
        long now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastRescueInstallTicks, now);
        Volatile.Write(ref _lastRescueCallbackTicks, now);
        Log.Info("Emergency keyboard hook installed on independent thread (" + reason + ")");
    }

    private void RemoveRescueHook()
    {
        IntPtr handle = _rescueHandle;
        _rescueHandle = IntPtr.Zero;
        Volatile.Write(ref _rescueInstalled, false);
        if (handle == IntPtr.Zero) return;

        try
        {
            if (Native.UnhookWindowsHookEx(handle)) Log.Info("Emergency keyboard hook removed");
        }
        catch (Exception ex)
        {
            Log.Debug("Emergency keyboard hook removal failed: " + ex.Message);
        }
    }

    private void RescueHookHeartbeat()
    {
        long now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastRescueCallbackTicks, now);
        if (Volatile.Read(ref _disposed) != 0) return;

        long installAge = now - Volatile.Read(ref _lastRescueInstallTicks);
        if (!IsEmergencyInstalled || _rescueHandle == IntPtr.Zero ||
            installAge >= ToStopwatchTicks(RESCUE_HOOK_RENEWAL_MS))
            InstallRescueHook("heartbeat");

        try { EnsureDispatchPath(fallback: true); }
        catch (Exception ex) { Log.Error("Emergency dispatch heartbeat supervision failed", ex); }
    }

    private IntPtr RescueHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
                Volatile.Write(ref _lastRescueCallbackTicks, Stopwatch.GetTimestamp());

            if (nCode >= 0 && Volatile.Read(ref _disposed) == 0)
            {
                int msg = (int)wParam;
                if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN ||
                    msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP)
                {
                    var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    if ((kbd.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0)
                        return CallNextHook(_rescueHandle, nCode, wParam, lParam);

                    bool keyUp = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP ||
                                 (kbd.flags & LLKHF_UP) != 0;
                    TrackRescueModifiers(kbd, keyUp);
                    if (keyUp)
                    {
                        _rescueDownKeys.Remove(kbd.vkCode);
                        return CallNextHook(_rescueHandle, nCode, wParam, lParam);
                    }

                    bool repeat = !_rescueDownKeys.Add(kbd.vkCode);
                    // The emergency Alt+F4 branch uses only callback-local state
                    // and the LLKHF_ALTDOWN flag. This avoids extra user32 state
                    // probes while Windows is waiting for the low-level hook.
                    bool alt = (kbd.flags & LLKHF_ALTDOWN) != 0 || _rescueAltDown;

                    // Keep the exact Alt+F4 swallowing behavior available even if
                    // the primary hook thread is stalled or its handle was removed.
                    if (kbd.vkCode == Native.VK_F4 && alt && ShouldInterceptAltF4())
                    {
                        if (!repeat)
                        {
                            RecoveryHotkeyMode mode = ParseAltF4Mode();
                            if (TryAccept("Alt+F4", repeat))
                            {
                                var args = new RecoveryHotkeyEventArgs(
                                    RecoveryHotkeyAction.AltF4, mode,
                                    nameof(Config.AltF4Mode), "Alt+F4");
                                long rescueSequence = RecordCapture(args);
                                QueueAction(args, _onAltF4Intercepted, rescueSequence);
                            }
                        }
                        return (IntPtr)1;
                    }

                    bool ctrl = _rescueCtrlDown;
                    bool shift = _rescueShiftDown;
                    bool win = _rescueWinDown;
                    bool altGr = false;
                    RecoveryHotkeyBinding[] bindings = Volatile.Read(ref _bindings);
                    foreach (RecoveryHotkeyBinding binding in bindings)
                    {
                        if (binding.Mode == RecoveryHotkeyMode.Off ||
                            !IsModeAllowed(binding.Mode) ||
                            !Matches(binding.Chord, kbd.vkCode, alt, ctrl, shift, win, altGr))
                            continue;

                        if (!repeat && TryAccept(binding.Chord.Signature, repeat))
                        {
                            var args = new RecoveryHotkeyEventArgs(binding);
                            long rescueSequence = RecordCapture(args);
                            Action? legacy = binding.Action == RecoveryHotkeyAction.Panic
                                ? _onPanic
                                : _onAltF4Intercepted;
                            QueueAction(args, legacy, rescueSequence);
                        }
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch
        {
            // Keep the rescue hook inside Windows' callback budget. Do not log
            // synchronously from this path.
            Interlocked.Increment(ref _callbackErrorCount);
        }

        return CallNextHook(_rescueHandle, nCode, wParam, lParam);
    }

    private static bool IsKeyDown(int virtualKey) =>
        (Native.GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;

    private void InstallHook(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        // A hook can disappear while a key is held, so do not carry a stale
        // down-state across a reinstall and suppress the next real chord.
        _downKeys.Clear();
        _altDown = IsKeyDown(Native.VK_MENU);
        _ctrlDown = IsKeyDown(Native.VK_CONTROL);
        _shiftDown = IsKeyDown(Native.VK_SHIFT);
        _winDown = IsKeyDown(0x5B) || IsKeyDown(0x5C);

        if (_handle != IntPtr.Zero)
        {
            try { Native.UnhookWindowsHookEx(_handle); } catch { }
            _handle = IntPtr.Zero;
            Volatile.Write(ref _installed, false);
        }

        IntPtr handle = Native.SetWindowsHookEx(
            Native.WH_KEYBOARD_LL,
            _proc,
            Native.GetModuleHandle(null),
            0);
        if (handle == IntPtr.Zero)
        {
            Volatile.Write(ref _installed, false);
            Log.Error("SetWindowsHookEx failed (" + reason + "): " +
                      new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
            return;
        }

        _handle = handle;
        Volatile.Write(ref _installed, true);
        long now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastHookInstallTicks, now);
        Volatile.Write(ref _lastHookCallbackTicks, now);
        Log.Info("Keyboard hook installed on dedicated thread (" + reason + ")");
    }

    /// <summary>
    /// RegisterHotKey is a third capture route for always-on chords.
    /// The main process keeps this route, and the out-of-process rescue helper
    /// adds an independent copy when enabled.
    /// Alt+F4 is included only in its explicit "always" mode. This route can wake
    /// recovery if both low-level hooks fail, but it cannot promise the same
    /// close-suppression contract as WH_KEYBOARD_LL.
    /// </summary>
    private void RegisterFallbackHotkeys(string reason)
    {
        UnregisterFallbackHotkeys();
        if (!Volatile.Read(ref _fallbackHotkeysEnabled)) return;

        Config config = Volatile.Read(ref _config);
        IReadOnlyList<RecoveryHotkeyBinding> bindings = config.GetAlwaysFallbackHotkeys();
        int nextId = 0x5A00;
        foreach (RecoveryHotkeyBinding binding in bindings)
        {
            int id = nextId++;
            uint modifiers = GetNativeModifiers(binding.Chord);
            bool registered = false;
            try
            {
                registered = Native.RegisterHotKey(
                    IntPtr.Zero, id, modifiers | Native.MOD_NOREPEAT, (uint)binding.Chord.Vk);
                if (!registered)
                {
                    // MOD_NOREPEAT is available on supported Windows versions,
                    // but a compatibility retry keeps capture alive on older
                    // shells or restricted test hosts.
                    registered = Native.RegisterHotKey(
                        IntPtr.Zero, id, modifiers, (uint)binding.Chord.Vk);
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"RegisterHotKey fallback unavailable for {binding.SettingName}: {ex.Message}");
            }

            if (registered)
            {
                _registeredFallbackHotkeys[id] = binding;
                Log.Info($"Registered hotkey fallback: {binding.Chord.Text} ({reason})");
            }
            else
            {
                Log.Debug($"RegisterHotKey fallback rejected for {binding.Chord.Text}: " +
                          new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
        }

        Volatile.Write(ref _registeredFallbackHotkeyCount, _registeredFallbackHotkeys.Count);
        Volatile.Write(ref _lastFallbackHotkeyRefreshTicks, Stopwatch.GetTimestamp());
    }

    private void UnregisterFallbackHotkeys()
    {
        foreach (int id in _registeredFallbackHotkeys.Keys.ToArray())
        {
            try { Native.UnregisterHotKey(IntPtr.Zero, id); } catch { }
        }
        _registeredFallbackHotkeys.Clear();
        Volatile.Write(ref _registeredFallbackHotkeyCount, 0);
    }

    private void HandleRegisteredHotkey(int id)
    {
        if (!_registeredFallbackHotkeys.TryGetValue(id, out RecoveryHotkeyBinding? binding) ||
            binding.Mode == RecoveryHotkeyMode.Off || !IsModeAllowed(binding.Mode))
            return;

        if (!TryAccept(binding.Chord.Signature, repeat: false))
            return;

        var args = new RecoveryHotkeyEventArgs(binding);
        long rescueSequence = RecordCapture(args);
        Action? legacy = binding.Action == RecoveryHotkeyAction.Panic
            ? _onPanic
            : _onAltF4Intercepted;
        QueueAction(args, legacy, rescueSequence);
        Log.Debug($"RegisterHotKey fallback captured {binding.Chord.Text}");
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

    private void RemoveHook()
    {
        IntPtr handle = _handle;
        _handle = IntPtr.Zero;
        Volatile.Write(ref _installed, false);
        if (handle == IntPtr.Zero) return;

        try
        {
            if (Native.UnhookWindowsHookEx(handle)) Log.Info("Keyboard hook removed");
        }
        catch (Exception ex)
        {
            Log.Debug("Keyboard hook removal failed: " + ex.Message);
        }
    }

    private void HookHeartbeat()
    {
        long now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastHeartbeatTicks, now);
        if (Volatile.Read(ref _disposed) != 0) return;

        long callbackAge = now - Volatile.Read(ref _lastHookCallbackTicks);
        long installAge = now - Volatile.Read(ref _lastHookInstallTicks);
        long staleTicks = ToStopwatchTicks(HOOK_STALE_MS);
        long renewalTicks = ToStopwatchTicks(HOOK_RENEWAL_MS);
        bool activeInput = (Native.GetAsyncKeyState(Native.VK_MENU) & unchecked((short)0x8000)) != 0 ||
                            (Native.GetAsyncKeyState(Native.VK_CONTROL) & unchecked((short)0x8000)) != 0 ||
                            (Native.GetAsyncKeyState(Native.VK_SHIFT) & unchecked((short)0x8000)) != 0;

        // Windows provides no query that proves a low-level hook is still in the
        // chain. A missing handle, a stale callback while a modifier is held, or a
        // bounded renewal window therefore causes a reinstall. The renewal also
        // repairs a silently removed hook when the desktop has no input activity.
        if (!IsInstalled || _handle == IntPtr.Zero ||
            (callbackAge >= staleTicks && activeInput) || installAge >= renewalTicks)
            InstallHook("heartbeat");

        MaintainFallbackHotkeys(now);
        try { EnsureDispatchPath(fallback: false); }
        catch (Exception ex) { Log.Error("Primary dispatch heartbeat supervision failed", ex); }
    }

    private void MaintainFallbackHotkeys(long now)
    {
        Config config = Volatile.Read(ref _config);
        int expected = config.GetAlwaysFallbackHotkeys().Count;
        int actual = RegisteredFallbackHotkeyCount;
        long previous = Volatile.Read(ref _lastFallbackHotkeyRefreshTicks);
        long retryTicks = ToStopwatchTicks(FALLBACK_HOTKEY_RETRY_MS);
        long renewalTicks = ToStopwatchTicks(FALLBACK_HOTKEY_RENEWAL_MS);
        if (previous != 0 && now - previous < retryTicks) return;
        if (actual == expected && previous != 0 && now - previous < renewalTicks) return;

        RegisterFallbackHotkeys("health check");
        Volatile.Write(ref _lastFallbackHotkeyRefreshTicks, now);
    }

    private void DispatchThreadMain()
    {
        Volatile.Write(ref _dispatchThreadId, unchecked((int)GetCurrentThreadId()));
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    // The event is the fast wake-up, but a short bounded poll
                    // prevents a transient Set/handle failure from stranding
                    // a queued capture forever.
                    _dispatchWake.WaitOne(100);
                    while (TryTakeDispatch(out DispatchSlot slot))
                    {
                        try { DeliverDispatch(slot); }
                        catch (Exception ex)
                        {
                            // Delivery is already isolated per subscriber, but
                            // retain a last-resort guard around the whole slot so
                            // one unexpected queue/managed failure cannot strand
                            // the emergency slot or kill the dispatch worker.
                            Log.Error("Hotkey dispatch delivery failed; slot cleared", ex);
                            try { ClearDispatchSlot(slot); } catch { }
                        }
                    }

                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        ClearQueuedDispatch();
                        break;
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error("Hotkey dispatch loop failed; retrying", ex);
                    if (Volatile.Read(ref _disposed) != 0) break;
                    try { Thread.Sleep(25); } catch { }
                }
            }
        }
        finally
        {
            Volatile.Write(ref _dispatchThreadId, 0);
        }
    }

    /// <summary>
    /// Independent last-resort delivery worker. This worker has its own wait
    /// handle, slot, and managed thread so a full normal ring or an unexpected
    /// dispatch-loop failure does not silently discard an already captured chord.
    /// </summary>
    private void FallbackDispatchThreadMain()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    // Keep a bounded poll so a transient wake-handle failure
                    // cannot strand the reserved emergency delivery slot.
                    _fallbackDispatchWake.WaitOne(100);
                    if (Interlocked.CompareExchange(ref _fallbackDispatchState, 2, 1) == 1)
                    {
                        try { DeliverDispatch(_fallbackDispatchSlot); }
                        catch (Exception ex)
                        {
                            Log.Error("Emergency hotkey dispatch failed; slot cleared", ex);
                            try { ClearDispatchSlot(_fallbackDispatchSlot); } catch { }
                        }
                    }

                    if (Volatile.Read(ref _disposed) != 0)
                        break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error("Emergency hotkey dispatch loop failed; retrying", ex);
                    if (Volatile.Read(ref _disposed) != 0) break;
                    try { Thread.Sleep(25); } catch { }
                }
            }
        }
        finally
        {
            try { ClearDispatchSlot(_fallbackDispatchSlot); } catch { }
            _fallbackDispatchStopped.Set();
        }
    }

    private bool TryTakeDispatch(out DispatchSlot slot)
    {
        lock (_dispatchGate)
        {
            if (_dispatchQueue.Count != 0)
            {
                slot = _dispatchQueue.Dequeue();
                return true;
            }

            if (Interlocked.CompareExchange(ref _emergencyDispatchState, 2, 1) == 1)
            {
                slot = _emergencyDispatchSlot;
                return true;
            }

            slot = null!;
            return false;
        }
    }

    private void DeliverDispatch(DispatchSlot slot)
    {
        RecoveryHotkeyEventArgs? args = slot.Args;
        Action? legacyCallback = slot.LegacyCallback;
        if (args is null)
        {
            ClearDispatchSlot(slot);
            return;
        }

        bool delivered = false;
        if (Volatile.Read(ref _disposed) == 0)
        {
            Delegate[]? subscribers = RecoveryActionRequested?.GetInvocationList();
            if (subscribers is not null)
            {
                foreach (Delegate subscriber in subscribers)
                {
                    try
                    {
                        ((EventHandler<RecoveryHotkeyEventArgs>)subscriber)(this, args);
                        delivered = true;
                    }
                    catch (Exception ex)
                    {
                        // Multicast event invocation stops at the first
                        // throwing subscriber. Invoke the snapshot one handler
                        // at a time so later recovery consumers still run.
                        Log.Error("Recovery hotkey event handler failed", ex);
                    }
                }
            }

            if (legacyCallback is not null)
            {
                try { legacyCallback(); delivered = true; }
                catch (Exception ex) { Log.Error("Legacy hotkey callback failed", ex); }
            }

            // Acknowledge only after a live dispatch worker has delivered the
            // request. Queue insertion alone is not proof that recovery can run.
            if (delivered && slot.RescueSequence > 0)
                _ = _rescueBroker.TryAcknowledgeFast(slot.RescueSequence);
        }

        ClearDispatchSlot(slot);
    }

    private void ClearDispatchSlot(DispatchSlot slot)
    {
        if (ReferenceEquals(slot, _emergencyDispatchSlot))
        {
            slot.Args = null;
            slot.LegacyCallback = null;
            slot.RescueSequence = 0;
            Volatile.Write(ref _emergencyDispatchState, 0);
            return;
        }

        if (ReferenceEquals(slot, _fallbackDispatchSlot))
        {
            slot.Args = null;
            slot.LegacyCallback = null;
            slot.RescueSequence = 0;
            Volatile.Write(ref _fallbackDispatchState, 0);
            return;
        }

        lock (_dispatchGate)
        {
            slot.Args = null;
            slot.LegacyCallback = null;
            slot.RescueSequence = 0;
        }
    }

    private void ClearQueuedDispatch()
    {
        lock (_dispatchGate)
        {
            while (_dispatchQueue.Count != 0)
            {
                DispatchSlot slot = _dispatchQueue.Dequeue();
                slot.Args = null;
                slot.LegacyCallback = null;
                slot.RescueSequence = 0;
            }

            if (Interlocked.CompareExchange(ref _emergencyDispatchState, 0, 1) == 1)
            {
                _emergencyDispatchSlot.Args = null;
                _emergencyDispatchSlot.LegacyCallback = null;
                _emergencyDispatchSlot.RescueSequence = 0;
            }

            if (Interlocked.CompareExchange(ref _fallbackDispatchState, 0, 1) == 1)
            {
                _fallbackDispatchSlot.Args = null;
                _fallbackDispatchSlot.LegacyCallback = null;
                _fallbackDispatchSlot.RescueSequence = 0;
            }
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
                Volatile.Write(ref _lastHookCallbackTicks, Stopwatch.GetTimestamp());

            if (nCode >= 0 && Volatile.Read(ref _disposed) == 0)
            {
                int msg = (int)wParam;
                if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN ||
                    msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP)
                {
                    var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                    // Ignore injected input. Apart from preserving the promise that Thaw
                    // never injects input, this prevents another automation tool from
                    // accidentally triggering a recovery action or corrupting modifier state.
                    if ((kbd.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0)
                        return CallNextHook(_handle, nCode, wParam, lParam);

                    bool keyUp = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP ||
                                 (kbd.flags & LLKHF_UP) != 0;
                    // This callback is executed under Windows' low-level hook
                    // timeout. Do not take a managed lock here: configuration
                    // reloads and dispatch cleanup must never delay key capture.
                    TrackModifiers(kbd, keyUp);
                    if (keyUp)
                    {
                        _downKeys.Remove(kbd.vkCode);
                        return CallNextHook(_handle, nCode, wParam, lParam);
                    }

                    bool repeat = !_downKeys.Add(kbd.vkCode);
                    bool alt = _altDown;
                    bool ctrl = _ctrlDown;
                    bool shift = _shiftDown;
                    bool win = _winDown;
                    bool altGr = ctrl && IsKeyDown(0xA5);

                    // Alt+F4 is deliberately handled as its own legacy mode and remains
                    // collision-free with the validated two-modifier table.
                    if (kbd.vkCode == Native.VK_F4 && (alt || (kbd.flags & LLKHF_ALTDOWN) != 0))
                    {
                        if (ShouldInterceptAltF4())
                        {
                            bool accepted = !repeat && TryAccept("Alt+F4", repeat);
                            if (accepted)
                            {
                                var args = new RecoveryHotkeyEventArgs(
                                    RecoveryHotkeyAction.AltF4,
                                    ParseAltF4Mode(),
                                    nameof(Config.AltF4Mode),
                                    "Alt+F4");
                                long rescueSequence = RecordCapture(args);
                                QueueAction(args, _onAltF4Intercepted, rescueSequence);
                            }
                            // Swallow held repeats too, otherwise Windows may close the
                            // focused window after the first accepted event.
                            return (IntPtr)1;
                        }
                    }

                    RecoveryHotkeyBinding[] bindings = _bindings;
                    foreach (RecoveryHotkeyBinding binding in bindings)
                    {
                        if (binding.Mode == RecoveryHotkeyMode.Off ||
                            !IsModeAllowed(binding.Mode) ||
                            !Matches(binding.Chord, kbd.vkCode, alt, ctrl, shift, win, altGr))
                            continue;

                        bool accepted = !repeat && TryAccept(binding.Chord.Signature, repeat);
                        if (accepted)
                        {
                            var args = new RecoveryHotkeyEventArgs(binding);
                            long rescueSequence = RecordCapture(args);
                            Action? legacy = binding.Action == RecoveryHotkeyAction.Panic ? _onPanic : _onAltF4Intercepted;
                            QueueAction(args, legacy, rescueSequence);
                        }
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch
        {
            // Never perform file I/O while Windows is waiting for the low-level
            // hook. The next callback can still preserve the capture path.
            Interlocked.Increment(ref _callbackErrorCount);
        }
        return CallNextHook(_handle, nCode, wParam, lParam);
    }

    private bool ShouldInterceptAltF4()
    {
        if (_shouldInterceptAltF4 is not null)
        {
            try { return _shouldInterceptAltF4(); }
            catch
            {
                Interlocked.Increment(ref _callbackErrorCount);
                return false;
            }
        }

        return ParseAltF4Mode() switch
        {
            RecoveryHotkeyMode.Always => true,
            RecoveryHotkeyMode.Stressed => IsModeAllowed(RecoveryHotkeyMode.Stressed),
            _ => false,
        };
    }

    private RecoveryHotkeyMode ParseAltF4Mode()
    {
        // The compatibility constructor supplies a predicate, but the new constructor reads
        // the live Config so a normal in-place AppContext reload also takes effect. The
        // binding table intentionally does not duplicate Alt+F4.
        Config config = Volatile.Read(ref _config);
        return config.AltF4Mode?.Trim().ToLowerInvariant() switch
        {
            "always" => RecoveryHotkeyMode.Always,
            "stressed" => RecoveryHotkeyMode.Stressed,
            _ => RecoveryHotkeyMode.Off,
        };
    }

    private bool IsModeAllowed(RecoveryHotkeyMode mode)
    {
        if (mode == RecoveryHotkeyMode.Always) return true;
        if (mode != RecoveryHotkeyMode.Stressed || _shouldAllowStressed is null) return false;
        try { return _shouldAllowStressed(); }
        catch
        {
            Interlocked.Increment(ref _callbackErrorCount);
            return false;
        }
    }

    private bool Matches(
        RecoveryHotkeyChord chord,
        uint vk,
        bool alt,
        bool ctrl,
        bool shift,
        bool win,
        bool altGr)
    {
        if (vk != (uint)chord.Vk) return false;

        // Prefer tracked state, but recover from a missed modifier event. The hook only
        // reads key state; it never synthesizes or injects input.
        alt |= (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;
        ctrl |= (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
        shift |= (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;
        win |= (Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
               (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0;
        altGr |= ctrl && (Native.GetAsyncKeyState(0xA5) & 0x8000) != 0;
        // Right-Alt is reported as Ctrl+Alt by Windows on many keyboard
        // layouts. Do not steal ordinary AltGr text input just because a
        // user-defined recovery chord happens to use the same modifiers.
        if (altGr && chord.Alt && chord.Ctrl) return false;
        return alt == chord.Alt && ctrl == chord.Ctrl && shift == chord.Shift && win == chord.Win;
    }

    private bool TryAccept(string signature, bool repeat)
    {
        if (repeat) return false;
        long now = Stopwatch.GetTimestamp();
        long debounceTicks = Volatile.Read(ref _debounceTicks);
        int start = (StringComparer.OrdinalIgnoreCase.GetHashCode(signature) & int.MaxValue) % _debounceSlots.Length;
        for (int offset = 0; offset < _debounceSlots.Length; offset++)
        {
            DebounceSlot slot = _debounceSlots[(start + offset) % _debounceSlots.Length];
            string? existing = Volatile.Read(ref slot.Signature);
            if (existing is null)
            {
                if (Interlocked.CompareExchange(ref slot.Signature, DEBOUNCE_CLAIM, null) is null)
                {
                    Volatile.Write(ref slot.AcceptedTicks, now);
                    Volatile.Write(ref slot.Signature, signature);
                    return true;
                }
                continue;
            }

            // Another hook callback is publishing the same slot. Treat this
            // event as a duplicate instead of probing another slot and allowing
            // a tiny publication window to bypass debounce.
            if (existing == DEBOUNCE_CLAIM) return false;

            if (!StringComparer.OrdinalIgnoreCase.Equals(existing, signature)) continue;
            long previous = Volatile.Read(ref slot.AcceptedTicks);
            if (now - previous < debounceTicks) return false;
            if (Interlocked.CompareExchange(ref slot.AcceptedTicks, now, previous) == previous)
                return true;
            offset--;
        }

        // Validated configuration has only four possible capture signatures
        // (Alt+F4 plus three bindings), so reaching this point indicates an
        // unexpected corruption rather than normal pressure.
        Interlocked.Increment(ref _droppedDispatchCount);
        return false;
    }

    private bool QueueAction(
        RecoveryHotkeyEventArgs args,
        Action? legacyCallback,
        long rescueSequence)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;

        // The low-level callback must return promptly. In particular, do not call
        // Unfreezer.Trigger here: a preallocated slot transfers both the typed event
        // and legacy callback to the already-running dispatch worker instead.
        if (Monitor.TryEnter(_dispatchGate))
        {
            try
            {
                if (Volatile.Read(ref _disposed) != 0) return false;
                foreach (DispatchSlot slot in _dispatchSlots)
                {
                    if (slot.Args is not null) continue;
                    slot.Args = args;
                    slot.LegacyCallback = legacyCallback;
                    slot.RescueSequence = rescueSequence;
                    _dispatchQueue.Enqueue(slot);
                    try { _dispatchWake.Set(); }
                    catch { Interlocked.Increment(ref _callbackErrorCount); }
                    return true;
                }
            }
            finally
            {
                Monitor.Exit(_dispatchGate);
            }
        }

        // A reserved slot keeps the emergency path lossless when dispatch cleanup
        // briefly owns the normal ring. It is never allowed to block the hook.
        if (Interlocked.CompareExchange(ref _emergencyDispatchState, EMERGENCY_WRITING, 0) == 0)
        {
            _emergencyDispatchSlot.Args = args;
            _emergencyDispatchSlot.LegacyCallback = legacyCallback;
            _emergencyDispatchSlot.RescueSequence = rescueSequence;
            Volatile.Write(ref _emergencyDispatchState, 1);
            try { _dispatchWake.Set(); }
            catch { Interlocked.Increment(ref _callbackErrorCount); }
            return true;
        }

        // A second pre-warmed worker owns the final non-blocking delivery slot.
        // It is independent of the normal dispatch wait handle and therefore
        // remains useful if that worker is stalled or its ring is saturated.
        if (Interlocked.CompareExchange(ref _fallbackDispatchState, EMERGENCY_WRITING, 0) == 0)
        {
            _fallbackDispatchSlot.Args = args;
            _fallbackDispatchSlot.LegacyCallback = legacyCallback;
            _fallbackDispatchSlot.RescueSequence = rescueSequence;
            Volatile.Write(ref _fallbackDispatchState, 1);
            try
            {
                _fallbackDispatchWake.Set();
                return true;
            }
            catch { Interlocked.Increment(ref _callbackErrorCount); return true; }
        }

        // This is only reachable after an unusually large burst or simultaneous
        // failure of both pre-warmed dispatch workers. Never run recovery
        // synchronously in WH_KEYBOARD_LL while the machine is under pressure.
        Interlocked.Increment(ref _droppedDispatchCount);
        return false;
    }

    private long RecordCapture(RecoveryHotkeyEventArgs args)
    {
        long sequence = Interlocked.Increment(ref _captureCount);
        _ = _rescueBroker.TrySignalFast(args.Hotkey, out long rescueSequence);
        var receipt = new KeyboardCaptureReceipt(
            sequence,
            args.Action,
            args.Hotkey,
            Stopwatch.GetTimestamp(),
            DateTime.UtcNow.Ticks,
            rescueSequence);
        Volatile.Write(ref _lastCapture, new CaptureBox(receipt));

        // Signal before queueing the typed event. A helper waiting on the named
        // event therefore receives a capture pulse even if the main dispatcher
        // is briefly starved or the UI thread is blocked.
        return rescueSequence;
    }

    private void TrackModifiers(in KBDLLHOOKSTRUCT kbd, bool keyUp)
    {
        bool down = !keyUp;
        switch (kbd.vkCode)
        {
            case 0x12: // VK_MENU (generic)
            case 0xA4: // VK_LMENU
            case 0xA5: // VK_RMENU
                _altDown = down; break;
            case 0x11: // VK_CONTROL (generic)
            case 0xA2: // VK_LCONTROL
            case 0xA3: // VK_RCONTROL
                _ctrlDown = down; break;
            case 0x10: // VK_SHIFT (generic)
            case 0xA0: // VK_LSHIFT
            case 0xA1: // VK_RSHIFT
                _shiftDown = down; break;
            case 0x5B: // VK_LWIN
            case 0x5C: // VK_RWIN
                _winDown = down; break;
        }
    }

    private void TrackRescueModifiers(in KBDLLHOOKSTRUCT kbd, bool keyUp)
    {
        bool down = !keyUp;
        switch (kbd.vkCode)
        {
            case 0x12: // VK_MENU (generic)
            case 0xA4: // VK_LMENU
            case 0xA5: // VK_RMENU
                _rescueAltDown = down; break;
            case 0x11: // VK_CONTROL (generic)
            case 0xA2: // VK_LCONTROL
            case 0xA3: // VK_RCONTROL
                _rescueCtrlDown = down; break;
            case 0x10: // VK_SHIFT (generic)
            case 0xA0: // VK_LSHIFT
            case 0xA1: // VK_RSHIFT
                _rescueShiftDown = down; break;
            case 0x5B: // VK_LWIN
            case 0x5C: // VK_RWIN
                _rescueWinDown = down; break;
        }
    }

    private RecoveryHotkeyChord GetPanicChord()
    {
        RecoveryHotkeyBinding[] bindings = Volatile.Read(ref _bindings);
        RecoveryHotkeyBinding? binding = bindings.FirstOrDefault(b => b.Action == RecoveryHotkeyAction.Panic);
        return binding is null ? new Config().GetPanicChord() : binding.Chord;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Stop the supervisor first. Once it has observed disposal it must not
        // create a replacement while the owner is tearing the hook threads down.
        try { _hookSupervisorStop.Set(); } catch { }
        Thread? supervisorThread = Volatile.Read(ref _hookSupervisorThread);
        bool supervisorOwn = ReferenceEquals(Thread.CurrentThread, supervisorThread);
        if (!supervisorOwn && supervisorThread is not null)
        {
            try { supervisorThread.Join(2000); } catch { }
            try { _hookSupervisorStopped.Wait(2000); } catch { }
        }
        bool supervisorStopped = supervisorOwn ? false : supervisorThread is null || !supervisorThread.IsAlive;

        Thread? hookThread = Volatile.Read(ref _hookThread);
        bool hookThreadOwn = ReferenceEquals(Thread.CurrentThread, hookThread);
        int hookThreadId = Volatile.Read(ref _hookThreadId);
        if (!hookThreadOwn)
        {
            if (hookThreadId != 0)
                _ = PostThreadMessage((uint)hookThreadId, WM_APP_STOP, IntPtr.Zero, IntPtr.Zero);
            if (hookThread is not null)
            {
                try { hookThread.Join(2000); } catch { }
                try { _hookStopped.Wait(2000); } catch { }
            }
        }
        bool hookStopped = !hookThreadOwn && (hookThread is null || !hookThread.IsAlive);

        Thread? rescueThread = Volatile.Read(ref _rescueThread);
        bool rescueThreadOwn = ReferenceEquals(Thread.CurrentThread, rescueThread);
        int rescueThreadId = Volatile.Read(ref _rescueThreadId);
        if (!rescueThreadOwn)
        {
            if (rescueThreadId != 0)
                _ = PostThreadMessage((uint)rescueThreadId, WM_APP_RESCUE_STOP, IntPtr.Zero, IntPtr.Zero);
            if (rescueThread is not null)
            {
                try { rescueThread.Join(2000); } catch { }
                try { _rescueStopped.Wait(2000); } catch { }
            }
        }
        bool rescueStopped = !rescueThreadOwn && (rescueThread is null || !rescueThread.IsAlive);

        // Wake and join the pre-warmed dispatch worker. No callback can enqueue
        // after _disposed is published, and any queued slots are cleared there.
        try { _dispatchWake.Set(); } catch { }
        Thread? dispatchThread = Volatile.Read(ref _dispatchThread);
        if (dispatchThread is not null && Thread.CurrentThread != dispatchThread)
        {
            try { dispatchThread.Join(2000); } catch { }
        }

        bool dispatchStopped = dispatchThread is null || Thread.CurrentThread == dispatchThread || !dispatchThread.IsAlive;

        // Wake and join the independent last-resort delivery worker before
        // disposing its wait handle. It owns a separate slot and event from
        // the normal dispatch path.
        try { _fallbackDispatchWake.Set(); } catch { }
        Thread? fallbackDispatchThread = Volatile.Read(ref _fallbackDispatchThread);
        if (fallbackDispatchThread is not null && Thread.CurrentThread != fallbackDispatchThread)
        {
            try { fallbackDispatchThread.Join(2000); } catch { }
            try { _fallbackDispatchStopped.Wait(2000); } catch { }
        }
        bool fallbackDispatchStopped = fallbackDispatchThread is null ||
                                       Thread.CurrentThread == fallbackDispatchThread ||
                                       !fallbackDispatchThread.IsAlive;

        if (hookThreadOwn)
            RemoveHook();
        else if (!hookStopped)
            Log.Debug("Keyboard hook thread did not stop within 2 s");

        if (rescueThreadOwn)
            RemoveRescueHook();
        else if (!rescueStopped)
            Log.Debug("Emergency keyboard hook thread did not stop within 2 s");

        if (!supervisorStopped)
            Log.Debug("Keyboard hook supervisor did not stop within 2 s");

        // Do not dispose a wait handle while its worker may still be inside WaitOne
        // or publishing its final state; preservation beats an eager cleanup race.
        if (dispatchStopped)
        {
            try { _dispatchWake.Dispose(); } catch { }
        }
        if (fallbackDispatchStopped)
        {
            try { _fallbackDispatchWake.Dispose(); } catch { }
            try { _fallbackDispatchStopped.Dispose(); } catch { }
        }
        if (hookStopped)
        {
            try { _hookReady.Dispose(); } catch { }
            try { _hookStopped.Dispose(); } catch { }
        }
        if (rescueStopped)
        {
            try { _rescueReady.Dispose(); } catch { }
            try { _rescueStopped.Dispose(); } catch { }
        }
        if (supervisorStopped)
        {
            try { _hookSupervisorStop.Dispose(); } catch { }
            try { _hookSupervisorStopped.Dispose(); } catch { }
        }
        if (_ownsRescueBroker)
        {
            try { _rescueBroker.Dispose(); } catch { }
        }
    }

    private IntPtr CallNextHook(IntPtr handle, int nCode, IntPtr wParam, IntPtr lParam)
    {
        try { return Native.CallNextHookEx(handle, nCode, wParam, lParam); }
        catch
        {
            // Passing zero is the documented pass-through result when the
            // optional native chain call is unavailable; never let an exception
            // escape back into Windows' low-level hook dispatcher.
            Interlocked.Increment(ref _callbackErrorCount);
            return IntPtr.Zero;
        }
    }

    private static long ToStopwatchTicks(int milliseconds)
    {
        double ticks = milliseconds * (double)Stopwatch.Frequency / 1000d;
        return Math.Max(1L, (long)Math.Ceiling(ticks));
    }

    private static void SetHighPriority(Thread thread)
    {
        try { thread.Priority = ThreadPriority.Highest; }
        catch (Exception ex) { Log.Debug("Hotkey thread priority unavailable: " + ex.Message); }
    }

    private static DispatchSlot[] CreateDispatchSlots()
    {
        var slots = new DispatchSlot[DISPATCH_SLOT_COUNT];
        for (int i = 0; i < slots.Length; i++) slots[i] = new DispatchSlot();
        return slots;
    }

    private void ClearDebounceSlots()
    {
        foreach (DebounceSlot slot in _debounceSlots)
        {
            Volatile.Write(ref slot.AcceptedTicks, 0L);
            Volatile.Write(ref slot.Signature, (string?)null);
        }
    }

    private static DebounceSlot[] CreateDebounceSlots()
    {
        var slots = new DebounceSlot[DEBOUNCE_SLOT_COUNT];
        for (int i = 0; i < slots.Length; i++) slots[i] = new DebounceSlot();
        return slots;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out HookMessage lpMsg,
        IntPtr hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax,
        uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(
        uint count,
        IntPtr handles,
        uint milliseconds,
        uint wakeMask,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(
        out HookMessage lpMsg,
        IntPtr hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint idThread,
        uint msg,
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
    private struct HookMessage
    {
        public IntPtr hWnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private sealed class DispatchSlot
    {
        public RecoveryHotkeyEventArgs? Args;
        public Action? LegacyCallback;
        public long RescueSequence;
    }

    private sealed class DebounceSlot
    {
        internal string? Signature;
        internal long AcceptedTicks;
    }

    private sealed class CaptureBox
    {
        internal CaptureBox(KeyboardCaptureReceipt value) => Value = value;
        internal KeyboardCaptureReceipt Value { get; }
    }

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
