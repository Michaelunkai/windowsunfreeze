using System.Diagnostics;
using System.Security.Principal;

namespace Thaw;

/// <summary>
/// Small cross-process rescue signal carried by <see cref="RescueBroker"/>.
/// A pair of named events is intentionally used as a second, independent wake-up
/// path: one carries the capture pulse and the other lets the main process
/// acknowledge that it accepted the request. Neither event carries recovery
/// policy and neither replaces the normal hotkey event.
/// </summary>
public readonly struct RescueSignal
{
    public RescueSignal(long sequence, long capturedAtStopwatchTicks, long capturedAtUtcTicks, string reason)
    {
        Sequence = sequence;
        CapturedAtStopwatchTicks = capturedAtStopwatchTicks;
        CapturedAtUtcTicks = capturedAtUtcTicks;
        Reason = reason ?? string.Empty;
    }

    public long Sequence { get; }
    public long CapturedAtStopwatchTicks { get; }
    public long CapturedAtUtcTicks { get; }
    public string Reason { get; }

    public DateTime CapturedAtUtc => new(CapturedAtUtcTicks, DateTimeKind.Utc);
}

/// <summary>
/// Per-user named-event rescue channel.
///
/// The default instance only publishes a signal. Call <see cref="Start"/> from a
/// small helper or another process when a receiver is desired; the KeyboardHook
/// deliberately does not start a local receiver so the normal hotkey event cannot
/// be delivered twice. Named events are session-scoped (Local\) and include the
/// current user's SID to avoid crossing user boundaries.
/// </summary>
public sealed class RescueBroker : IDisposable
{
    public const string EventNamePrefix = @"Local\Thaw.RescueBroker.";

    private readonly object _gate = new();
    private readonly object _acknowledgementGate = new();
    private readonly EventWaitHandle? _signalEvent;
    private readonly EventWaitHandle? _acknowledgementEvent;
    private Thread? _listenerThread;
    private Action<RescueSignal>? _handler;
    private RescueSignal _lastSignal;
    private long _sequence;
    private long _lastPublishedSequence;
    private long _lastAcknowledgedSequence;
    private int _disposed;
    private int _listening;

    public RescueBroker(string? eventName = null)
    {
        EventName = string.IsNullOrWhiteSpace(eventName) ? GetDefaultEventName() : eventName;
        AcknowledgementEventName = EventName + ".Ack";
        try
        {
            _signalEvent = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: EventName,
                createdNew: out _);
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            // A named event is an optional redundancy. Never make normal hotkey
            // capture unavailable because a security policy rejected this handle.
            _signalEvent = null;
            IsAvailable = false;
            Log.Debug("Rescue broker named event unavailable: " + ex.Message);
        }

        try
        {
            _acknowledgementEvent = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: AcknowledgementEventName,
                createdNew: out _);
        }
        catch (Exception ex)
        {
            // Acknowledgement is an optimization for the out-of-process fallback;
            // capture remains available when a policy rejects the second handle.
            _acknowledgementEvent = null;
            Log.Debug("Rescue broker acknowledgement event unavailable: " + ex.Message);
        }
    }

    public RescueBroker(Action<RescueSignal> handler, string? eventName = null)
        : this(eventName)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Start(handler);
    }

    public string EventName { get; }
    public string AcknowledgementEventName { get; }
    public bool IsAvailable { get; }
    public bool IsAcknowledgementAvailable => _acknowledgementEvent is not null && Volatile.Read(ref _disposed) == 0;
    public bool IsListening => Volatile.Read(ref _listening) != 0;
    public long SignalCount => Interlocked.Read(ref _sequence);

    /// <summary>Last signal published or observed by this broker instance.</summary>
    public RescueSignal LastSignal
    {
        get
        {
            lock (_gate) return _lastSignal;
        }
    }

    /// <summary>Raised on the broker listener thread when the named event is signaled.</summary>
    public event Action<RescueSignal>? SignalReceived;

    /// <summary>
    /// Starts a bounded listener. This is normally called by an optional helper process,
    /// not by the main WinForms process, so a receiver cannot recursively trigger itself.
    /// </summary>
    public bool Start(Action<RescueSignal>? handler = null)
    {
        if (Volatile.Read(ref _disposed) != 0 || !IsAvailable || _signalEvent is null)
            return false;

        lock (_gate)
        {
            if (handler is not null) _handler = handler;
            if (Volatile.Read(ref _listening) != 0) return true;

            try
            {
                _listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "Thaw rescue broker"
                };
                try { _listenerThread.Priority = ThreadPriority.AboveNormal; }
                catch (Exception ex) { Log.Debug("Rescue broker priority unavailable: " + ex.Message); }
                Volatile.Write(ref _listening, 1);
                _listenerThread.Start();
                return true;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _listening, 0);
                Log.Error("Rescue broker listener start failed", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Publishes a rescue signal. The event Set call is the fast redundant path;
    /// the caller remains responsible for its normal typed hotkey dispatch.
    /// </summary>
    public bool Signal(string reason = "Alt+F4")
    {
        if (Volatile.Read(ref _disposed) != 0 || _signalEvent is null)
            return false;

        long sequence = Interlocked.Increment(ref _sequence);
        var signal = new RescueSignal(
            sequence,
            Stopwatch.GetTimestamp(),
            DateTime.UtcNow.Ticks,
            reason);

        try
        {
            if (!_signalEvent.Set()) return false;
            MarkPublished(signal, nonBlocking: false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("Rescue broker signal failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Alias suitable for integrations that treat the broker as a notifier.</summary>
    public bool TrySignal(string reason = "Alt+F4") => Signal(reason);

    /// <summary>
    /// Signals the named event without taking the broker state lock. This is the
    /// low-level-hook path: it uses the already-open kernel handle and a bounded
    /// native SetEvent call so a diagnostic reader cannot hold up WH_KEYBOARD_LL.
    /// The cross-process receiver only needs the pulse; the typed capture receipt
    /// remains in the publishing process.
    /// </summary>
    public bool TrySignalFast(string reason = "Alt+F4")
    {
        return TrySignalFast(reason, out _);
    }

    /// <summary>
    /// Fast signal overload that returns the exact broker sequence assigned to
    /// this capture. The sequence is carried through the dispatch slot so an
    /// older queued request cannot acknowledge a newer request by accident.
    /// </summary>
    public bool TrySignalFast(string reason, out long sequence)
    {
        sequence = 0;
        if (Volatile.Read(ref _disposed) != 0 || _signalEvent is null)
            return false;

        sequence = Interlocked.Increment(ref _sequence);
        var signal = new RescueSignal(
            sequence,
            Stopwatch.GetTimestamp(),
            DateTime.UtcNow.Ticks,
            reason);

        try
        {
            IntPtr handle = _signalEvent.SafeWaitHandle.DangerousGetHandle();
            bool published = handle != IntPtr.Zero && Native.SetEvent(handle);
            if (!published)
            {
                sequence = 0;
                return false;
            }
            // Diagnostics should never be able to delay the capture edge. The
            // native pulse is already committed; this metadata update is best effort.
            MarkPublished(signal, nonBlocking: true);
            return published;
        }
        catch
        {
            // This method is called from a low-level keyboard callback. Do not
            // perform file I/O or wait on a logger lock on the capture edge.
            sequence = 0;
            return false;
        }
    }

    /// <summary>Acknowledges the latest capture for a helper waiting on fallback readiness.</summary>
    public bool Acknowledge(long sequence = 0)
    {
        if (Volatile.Read(ref _disposed) != 0 || _acknowledgementEvent is null)
            return false;

        try
        {
            long effectiveSequence = sequence == 0
                ? Interlocked.Read(ref _lastPublishedSequence)
                : sequence;
            if (effectiveSequence <= 0 ||
                effectiveSequence > Interlocked.Read(ref _lastPublishedSequence))
                return false;
            return TryPublishAcknowledgement(effectiveSequence);
        }
        catch (Exception ex)
        {
            Log.Debug("Rescue broker acknowledgement failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Fast acknowledgement used immediately after a hook request enters the
    /// pre-warmed dispatch ring. It intentionally avoids the broker state lock.
    /// </summary>
    public bool TryAcknowledgeFast()
    {
        long sequence = Interlocked.Read(ref _lastPublishedSequence);
        return TryAcknowledgeFast(sequence);
    }

    /// <summary>Acknowledges the exact capture sequence delivered by a dispatch slot.</summary>
    public bool TryAcknowledgeFast(long sequence)
    {
        if (Volatile.Read(ref _disposed) != 0 || _acknowledgementEvent is null)
            return false;

        try
        {
            if (sequence <= 0 || sequence > Interlocked.Read(ref _lastPublishedSequence))
                return false;
            return TryPublishAcknowledgement(sequence);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Waits for the main process to acknowledge a rescue request.</summary>
    public bool WaitForAcknowledgement(int millisecondsTimeout)
    {
        if (Volatile.Read(ref _disposed) != 0 || _acknowledgementEvent is null)
            return false;

        try { return _acknowledgementEvent.WaitOne(Math.Clamp(millisecondsTimeout, 0, 10_000)); }
        catch (Exception ex)
        {
            Log.Debug("Rescue broker acknowledgement wait failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Consumes stale acknowledgements before a helper begins a new wait.</summary>
    public void DrainAcknowledgements()
    {
        if (_acknowledgementEvent is null) return;
        try { while (_acknowledgementEvent.WaitOne(0)) { } }
        catch (Exception ex) { Log.Debug("Rescue broker acknowledgement drain failed: " + ex.Message); }
    }

    private bool TryPublishAcknowledgement(long sequence)
    {
        // Keep the short native publish and the sequence commit together. The
        // old reserve-before-publish order could permanently suppress retries
        // after a transient SetEvent/handle failure.
        lock (_acknowledgementGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _acknowledgementEvent is null)
                return false;

            if (sequence > Volatile.Read(ref _lastPublishedSequence)) return false;
            if (sequence <= Volatile.Read(ref _lastAcknowledgedSequence))
                return true;

            IntPtr handle = _acknowledgementEvent.SafeWaitHandle.DangerousGetHandle();
            if (handle == IntPtr.Zero || !Native.SetEvent(handle))
                return false;

            Volatile.Write(ref _lastAcknowledgedSequence, sequence);
            return true;
        }
    }

    /// <summary>
    /// Waits for one named-event signal. Payload is available only in the publishing
    /// process; a receiver opened in another process gets a fresh observation receipt.
    /// </summary>
    public bool Wait(int millisecondsTimeout, out RescueSignal signal)
    {
        signal = default;
        if (Volatile.Read(ref _disposed) != 0 || _signalEvent is null)
            return false;

        try
        {
            if (!_signalEvent.WaitOne(millisecondsTimeout)) return false;
            lock (_gate)
            {
                signal = _lastSignal;
                if (signal.Sequence == 0)
                {
                    signal = new RescueSignal(
                        Interlocked.Increment(ref _sequence),
                        Stopwatch.GetTimestamp(),
                        DateTime.UtcNow.Ticks,
                        "named-event");
                    _lastSignal = signal;
                    AdvancePublishedSequence(signal.Sequence);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("Rescue broker wait failed: " + ex.Message);
            return false;
        }
    }

    private void ListenLoop()
    {
        while (Volatile.Read(ref _disposed) == 0 && _signalEvent is not null)
        {
            bool signaled;
            try { signaled = _signalEvent.WaitOne(500); }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Debug("Rescue broker listener wait failed: " + ex.Message);
                if (Volatile.Read(ref _disposed) == 0)
                {
                    try { Thread.Sleep(50); } catch { }
                    continue;
                }
                break;
            }

            if (!signaled || Volatile.Read(ref _disposed) != 0) continue;

            RescueSignal signal;
            lock (_gate)
            {
                signal = _lastSignal;
                if (signal.Sequence == 0)
                {
                    signal = new RescueSignal(
                        Interlocked.Increment(ref _sequence),
                        Stopwatch.GetTimestamp(),
                        DateTime.UtcNow.Ticks,
                        "named-event");
                    _lastSignal = signal;
                    AdvancePublishedSequence(signal.Sequence);
                }
            }

            Dispatch(signal);
        }

        Volatile.Write(ref _listening, 0);
    }

    private void MarkPublished(RescueSignal signal, bool nonBlocking)
    {
        AdvancePublishedSequence(signal.Sequence);
        if (nonBlocking)
        {
            if (!Monitor.TryEnter(_gate)) return;
            try
            {
                if (signal.Sequence >= _lastSignal.Sequence) _lastSignal = signal;
            }
            finally { Monitor.Exit(_gate); }
            return;
        }

        lock (_gate)
        {
            if (signal.Sequence >= _lastSignal.Sequence) _lastSignal = signal;
        }
    }

    private void AdvancePublishedSequence(long sequence)
    {
        if (sequence <= 0) return;
        while (true)
        {
            long previous = Volatile.Read(ref _lastPublishedSequence);
            if (previous >= sequence) return;
            if (Interlocked.CompareExchange(ref _lastPublishedSequence, sequence, previous) == previous)
                return;
        }
    }

    private void Dispatch(RescueSignal signal)
    {
        Action<RescueSignal>? handler;
        lock (_gate) handler = _handler;
        try { handler?.Invoke(signal); }
        catch (Exception ex) { Log.Error("Rescue broker handler failed", ex); }

        Delegate[]? subscribers = SignalReceived?.GetInvocationList();
        if (subscribers is null) return;
        foreach (Delegate subscriber in subscribers)
        {
            try { ((Action<RescueSignal>)subscriber)(signal); }
            catch (Exception ex) { Log.Error("Rescue broker subscriber failed", ex); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _signalEvent?.Set(); } catch { }
        lock (_acknowledgementGate)
        {
            try { _acknowledgementEvent?.Set(); } catch { }
        }

        Thread? listener;
        lock (_gate) listener = _listenerThread;
        if (listener is not null && listener != Thread.CurrentThread)
        {
            try { listener.Join(1000); } catch { }
        }

        try { _signalEvent?.Dispose(); } catch { }
        lock (_acknowledgementGate)
        {
            try { _acknowledgementEvent?.Dispose(); } catch { }
        }
        Volatile.Write(ref _listening, 0);
    }

    public static string GetDefaultEventName()
    {
        string identity = Environment.UserName;
        try
        {
            using WindowsIdentity current = WindowsIdentity.GetCurrent();
            string? sid = current?.User?.Value;
            if (!string.IsNullOrWhiteSpace(sid)) identity = sid;
        }
        catch (Exception ex) { Log.Debug("Rescue broker SID unavailable: " + ex.Message); }

        // SID and the fallback user name are safe event-name components. Keep the
        // fallback conservative in case a host supplies unusual account characters.
        Span<char> buffer = stackalloc char[identity.Length];
        int written = 0;
        foreach (char c in identity)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.') buffer[written++] = c;
        }
        string component = written == 0 ? "current-user" : new string(buffer[..written]);
        return EventNamePrefix + component;
    }
}
