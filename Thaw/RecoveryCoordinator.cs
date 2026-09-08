using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Thaw;

/// <summary>
/// The four possible conclusions for an action.  A native API accepting a
/// request is deliberately not considered proof of recovery; the action may
/// therefore remain Unverified until an after-probe says otherwise.
/// </summary>
internal enum RecoveryOutcome
{
    Recovered,
    Unchanged,
    Worse,
    Unverified,
}

internal enum RecoveryReceiptPhase
{
    Started,
    Completed,
    TimedOut,
    Skipped,
    CircuitOpened,
    RollbackStarted,
    RollbackCompleted,
}

internal readonly record struct RecoveryProbe(
    bool ForegroundHung,
    bool MemoryPressure,
    long AvailableMemoryBytes,
    long StallMs,
    double CpuPercent)
{
    internal static RecoveryOutcome Compare(RecoveryProbe before, RecoveryProbe after)
    {
        int beforeBad = (before.ForegroundHung ? 3 : 0) + (before.MemoryPressure ? 2 : 0) +
                        (before.StallMs >= 800 ? 2 : 0) + (before.CpuPercent >= 95 ? 1 : 0);
        int afterBad = (after.ForegroundHung ? 3 : 0) + (after.MemoryPressure ? 2 : 0) +
                       (after.StallMs >= 800 ? 2 : 0) + (after.CpuPercent >= 95 ? 1 : 0);

        if (afterBad < beforeBad || (!before.ForegroundHung && after.ForegroundHung == false &&
                                     after.AvailableMemoryBytes > before.AvailableMemoryBytes + 4L * 1024 * 1024))
            return RecoveryOutcome.Recovered;
        if (afterBad > beforeBad || (before.AvailableMemoryBytes > 0 &&
                                     after.AvailableMemoryBytes + 64L * 1024 * 1024 < before.AvailableMemoryBytes))
            return RecoveryOutcome.Worse;
        if (before.Equals(after)) return RecoveryOutcome.Unchanged;
        return RecoveryOutcome.Unverified;
    }
}

internal sealed record RecoveryEvidence(
    RecoveryOutcome Outcome,
    RecoveryProbe? Before,
    RecoveryProbe? After,
    string Detail);

internal sealed record RecoveryActionResult(
    RecoveryOutcome Outcome,
    bool Accepted,
    bool Skipped,
    string Detail,
    RecoveryProbe? Before = null,
    RecoveryProbe? After = null)
{
    internal static RecoveryActionResult AcceptedRequest(string detail = "request accepted") =>
        new(RecoveryOutcome.Unverified, true, false, detail);

    internal static RecoveryActionResult Recovered(string detail = "verified") =>
        new(RecoveryOutcome.Recovered, true, false, detail);

    internal static RecoveryActionResult Unchanged(string detail = "no observable change") =>
        new(RecoveryOutcome.Unchanged, true, false, detail);

    internal static RecoveryActionResult Unverified(string detail = "verification unavailable") =>
        new(RecoveryOutcome.Unverified, true, false, detail);

    internal static RecoveryActionResult Worse(string detail = "post-probe degraded") =>
        new(RecoveryOutcome.Worse, false, false, detail);

    internal static RecoveryActionResult SkippedResult(string detail) =>
        new(RecoveryOutcome.Unverified, false, true, detail);
}

/// <summary>A recovery operation and its independent budget/circuit policy.</summary>
internal sealed record RecoveryActionSpec
{
    internal RecoveryActionSpec(
        string name,
        int deadlineMs,
        Func<RecoveryActionContext, RecoveryActionResult> execute)
    {
        Name = name;
        DeadlineMs = Math.Clamp(deadlineMs, 25, 10_000);
        Execute = execute;
        CircuitKey = name;
    }

    internal string Name { get; }
    internal int DeadlineMs { get; }
    internal string CircuitKey { get; init; }
    internal bool SafeReversible { get; init; } = true;
    /// <summary>Whether generic before/after system probes may classify an unverified result.</summary>
    internal bool VerifyWithGenericProbe { get; init; } = true;
    /// <summary>Diagnostics do not open a circuit merely because proof is unavailable.</summary>
    internal bool UseCircuitBreaker { get; init; } = true;
    internal Func<RecoveryActionContext, RecoveryProbe>? CaptureBefore { get; init; }
    internal Func<RecoveryActionContext, RecoveryProbe>? CaptureAfter { get; init; }
    internal Func<RecoveryProbe, RecoveryProbe, RecoveryOutcome>? Evaluate { get; init; }
    internal Func<RecoveryActionContext, RecoveryActionResult> Execute { get; }
}

internal sealed class RecoveryActionContext
{
    private readonly Action<RecoveryReceiptPhase, RecoveryOutcome, string> _report;

    internal RecoveryActionContext(
        Guid runId,
        TriggerReason reason,
        HealthCause cause,
        bool forceAll,
        long runDeadlineTick,
        long actionDeadlineTick,
        RollbackJournal journal,
        Action<RecoveryReceiptPhase, RecoveryOutcome, string> report)
    {
        RunId = runId;
        Reason = reason;
        Cause = cause;
        ForceAll = forceAll;
        RunDeadlineTick = runDeadlineTick;
        ActionDeadlineTick = actionDeadlineTick;
        Journal = journal;
        _report = report;
    }

    internal Guid RunId { get; }
    internal TriggerReason Reason { get; }
    internal HealthCause Cause { get; }
    internal bool ForceAll { get; }
    internal long RunDeadlineTick { get; }
    internal long ActionDeadlineTick { get; }
    internal RollbackJournal Journal { get; }
    internal bool IsExpired => Environment.TickCount64 >= Math.Min(RunDeadlineTick, ActionDeadlineTick);
    internal int RemainingMs => (int)Math.Clamp(Math.Min(RunDeadlineTick, ActionDeadlineTick) - Environment.TickCount64, 0, int.MaxValue);

    internal void Progress(string detail) =>
        _report(RecoveryReceiptPhase.Started, RecoveryOutcome.Unverified, detail);
}

/// <summary>
/// LIFO rollback journal for every temporary mutation made during one run.
/// Rollbacks are best effort but each entry is independently reported.  The
/// journal is safe for concurrent action workers and can be drained exactly
/// once from the recovery owner thread.
/// </summary>
internal sealed class RollbackJournal
{
    private sealed record Entry(string Name, Func<bool> Undo);
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private int _rolledBack;

    internal int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    internal void Record(string name, Action undo)
    {
        if (undo is null) return;
        RecordResult(name, () =>
        {
            undo();
            return true;
        });
    }

    /// <summary>Records a rollback that can report whether the native restore succeeded.</summary>
    internal void RecordResult(string name, Func<bool> undo)
    {
        if (string.IsNullOrWhiteSpace(name) || undo is null) return;
        lock (_gate)
        {
            if (Volatile.Read(ref _rolledBack) != 0)
            {
                try
                {
                    if (!undo()) Log.Debug($"Late rollback for {name} reported failure");
                }
                catch (Exception ex) { Log.Debug($"Late rollback for {name} failed: {ex.Message}"); }
                return;
            }
            _entries.Add(new Entry(name, undo));
        }
    }

    internal IReadOnlyList<string> RollbackAll(Action<string, bool, string>? receipt = null)
    {
        if (Interlocked.Exchange(ref _rolledBack, 1) != 0) return Array.Empty<string>();

        Entry[] entries;
        lock (_gate) entries = _entries.ToArray();
        var restored = new List<string>(entries.Length);
        for (int i = entries.Length - 1; i >= 0; i--)
        {
            Entry entry = entries[i];
            receipt?.Invoke(entry.Name, true, "rollback started");
            try
            {
                if (!entry.Undo())
                {
                    Log.Debug($"Rollback reported failure for {entry.Name}");
                    receipt?.Invoke(entry.Name, false, "rollback failed: native restore returned false");
                    continue;
                }
                restored.Add(entry.Name);
                receipt?.Invoke(entry.Name, false, "rollback completed");
            }
            catch (Exception ex)
            {
                Log.Debug($"Rollback unavailable for {entry.Name}: {ex.Message}");
                receipt?.Invoke(entry.Name, false, "rollback failed: " + ex.GetType().Name);
            }
        }
        lock (_gate) _entries.Clear();
        return restored;
    }
}

/// <summary>A bounded, reusable dispatch batch returned by <see cref="RecoveryCoordinator"/>.</summary>
internal sealed class RecoveryDispatch
{
    internal sealed class PendingAction
    {
        internal RecoveryActionSpec? Spec;
        internal RecoveryActionContext? Context;
        internal RecoveryDispatch? Batch;
        internal long ActionDeadlineTick;
        internal long StartedTick;
        internal int State; // 0 queued/running, 1 completed, 2 timed out

        internal void Reset(RecoveryActionSpec spec, RecoveryActionContext context, RecoveryDispatch batch, long deadline)
        {
            Spec = spec;
            Context = context;
            Batch = batch;
            ActionDeadlineTick = deadline;
            StartedTick = 0;
            Volatile.Write(ref State, 0);
        }
    }

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _completed = new(false);
    private readonly ManualResetEventSlim _drained = new(false);
    private readonly List<PendingAction> _items;
    private readonly List<RecoveryProgressReceipt> _receipts = new();
    private int _remaining;
    private int _closed;
    private readonly Guid _runId;
    private readonly long _dispatchTick;
    private readonly long _runDeadlineTick;
    private int _workersRemaining;

    internal RecoveryDispatch(Guid runId, long dispatchTick, long runDeadlineTick, int count)
    {
        _runId = runId;
        _dispatchTick = dispatchTick;
        _runDeadlineTick = runDeadlineTick;
        _remaining = 0;
        _items = new List<PendingAction>(count);
    }

    internal Guid RunId => _runId;
    internal long DispatchTick => _dispatchTick;
    internal long RunDeadlineTick => _runDeadlineTick;
    internal bool IsComplete => Volatile.Read(ref _remaining) == 0;
    internal bool IsDrained => Volatile.Read(ref _workersRemaining) == 0;
    internal bool IsClosed => Volatile.Read(ref _closed) != 0;
    internal IReadOnlyList<PendingAction> Items => _items;

    internal void Add(PendingAction item)
    {
        lock (_gate) _items.Add(item);
    }

    internal void Activate(int count)
    {
        Volatile.Write(ref _remaining, count);
        Volatile.Write(ref _workersRemaining, count);
        if (count == 0) _completed.Set();
        if (count == 0) _drained.Set();
    }

    internal IReadOnlyList<RecoveryProgressReceipt> Receipts
    {
        get { lock (_gate) return _receipts.ToArray(); }
    }

    internal void Receipt(string action, RecoveryReceiptPhase phase, RecoveryOutcome outcome, long startedTick, string detail)
    {
        long elapsed = startedTick > 0 ? Math.Max(0, Environment.TickCount64 - startedTick) : 0;
        lock (_gate)
        {
            _receipts.Add(new RecoveryProgressReceipt(
                _runId, action, phase, outcome, elapsed, detail, DateTimeOffset.UtcNow));
        }
    }

    internal bool Complete(PendingAction item, RecoveryOutcome outcome, string detail)
    {
        if (Interlocked.CompareExchange(ref item.State, 1, 0) != 0) return false;
        Receipt(item.Spec?.Name ?? "unknown", RecoveryReceiptPhase.Completed, outcome,
            item.StartedTick, detail);
        if (Interlocked.Decrement(ref _remaining) == 0) _completed.Set();
        return true;
    }

    internal void MarkExpiredActions(Func<string, bool> openCircuit)
    {
        long now = Environment.TickCount64;
        PendingAction[] snapshot;
        lock (_gate) snapshot = _items.ToArray();
        foreach (PendingAction item in snapshot)
        {
            if (Volatile.Read(ref item.State) != 0 || now < item.ActionDeadlineTick) continue;
            if (Interlocked.CompareExchange(ref item.State, 2, 0) != 0) continue;
            string name = item.Spec?.Name ?? "unknown";
            Receipt(name, RecoveryReceiptPhase.TimedOut, RecoveryOutcome.Unverified,
                item.StartedTick, "independent deadline exceeded");
            if (item.Spec?.UseCircuitBreaker == true && openCircuit(item.Spec.CircuitKey))
                Receipt(name, RecoveryReceiptPhase.CircuitOpened, RecoveryOutcome.Unverified,
                    item.StartedTick, "circuit opened after timeout");
            if (Interlocked.Decrement(ref _remaining) == 0) _completed.Set();
        }
    }

    internal bool WaitUntil(long deadlineTick, Func<string, bool> openCircuit)
    {
        while (!IsComplete)
        {
            MarkExpiredActions(openCircuit);
            long remaining = Math.Min(deadlineTick, _runDeadlineTick) - Environment.TickCount64;
            if (remaining <= 0) break;
            _completed.Wait((int)Math.Clamp(Math.Min(remaining, 20), 1, 20));
        }
        MarkExpiredActions(openCircuit);
        return IsComplete;
    }

    internal void WorkerFinished()
    {
        if (Interlocked.Decrement(ref _workersRemaining) == 0)
            _drained.Set();
    }

    internal void WaitForDrain() => _drained.Wait();

    internal void Close() => Interlocked.Exchange(ref _closed, 1);
}

internal sealed record RecoveryProgressReceipt(
    Guid RunId,
    string Action,
    RecoveryReceiptPhase Phase,
    RecoveryOutcome Outcome,
    long ElapsedMs,
    string Detail,
    DateTimeOffset At);

/// <summary>
/// Fixed worker coordinator used by every trigger.  Workers are created at
/// construction time and wait on a reusable queue, so an Alt+F4 dispatch only
/// fills preallocated slots and signals them.  A blocked operation can consume
/// one worker, but its independent action deadline opens that action's circuit
/// without delaying the other workers.
/// </summary>
internal sealed class RecoveryCoordinator : IDisposable
{
    internal const int ForceAllDispatchDeadlineMs = 1_000;
    // Keep enough pre-warmed workers for every safe action plus the bounded
    // diagnostic tail. A force-all request must never silently omit an action.
    internal const int MaxActionWorkers = 24;
    internal const int CircuitFailureThreshold = 2;
    internal const int CircuitCooldownMs = 15_000;

    private sealed class CircuitState
    {
        internal int Failures;
        internal long OpenUntil;
    }

    private readonly object _gate = new();
    private readonly Queue<RecoveryDispatch.PendingAction> _queue = new(MaxActionWorkers);
    private readonly RecoveryDispatch.PendingAction[] _slots =
        Enumerable.Range(0, MaxActionWorkers).Select(_ => new RecoveryDispatch.PendingAction()).ToArray();
    private readonly Thread[] _workers;
    private readonly ManualResetEventSlim _workAvailable = new(false);
    private readonly ConcurrentDictionary<string, CircuitState> _circuits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<RecoveryProgressReceipt>? _progress;
    private volatile bool _stop;
    private int _activeDispatch;
    private int _disposed;

    internal RecoveryCoordinator(Action<RecoveryProgressReceipt>? progress = null)
    {
        _progress = progress;
        var workers = new List<Thread>(MaxActionWorkers);
        for (int i = 0; i < MaxActionWorkers; i++)
        {
            try
            {
                Thread thread = new(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "Thaw.RecoveryWorker." + i,
                };
                TrySetPriority(thread, ThreadPriority.Highest, thread.Name);
                thread.Start();
                workers.Add(thread);
            }
            catch (Exception ex)
            {
                // A resource-starved machine must not crash the tray merely
                // because one pre-warmed slot could not start. Other workers
                // can drain the bounded queue; a zero-worker coordinator is
                // rejected explicitly by Dispatch() below.
                Log.Error("Unable to start pre-warmed recovery worker " + i, ex);
            }
        }
        _workers = workers.ToArray();
    }

    internal static IReadOnlyList<string> SafeReversibleActions { get; } = new[]
    {
        "display-reset", "foreground-qos", "background-demotion", "working-set-trim",
        "memory-cache", "shell-refresh", "dns-refresh", "power-plan", "mmcss", "cpu-sets",
        "audio-diagnostic", "desktop-refresh", "foreground-probe", "resource-diagnostics",
    };

    internal event Action<RecoveryProgressReceipt>? Progress;

    internal bool IsBusy => Volatile.Read(ref _activeDispatch) != 0;

    internal RecoveryDispatch Dispatch(
        Guid runId,
        TriggerReason reason,
        HealthCause cause,
        bool forceAll,
        long runDeadlineTick,
        RollbackJournal journal,
        IReadOnlyList<RecoveryActionSpec> actions)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(RecoveryCoordinator));
        if (_workers.Length == 0)
            throw new InvalidOperationException("No pre-warmed recovery worker is available");
        if (Interlocked.CompareExchange(ref _activeDispatch, 1, 0) != 0)
            throw new InvalidOperationException("a recovery dispatch is already active");

        var dispatch = new RecoveryDispatch(runId, Environment.TickCount64, runDeadlineTick,
            Math.Min(actions.Count, MaxActionWorkers));
        int queued = 0;
        try
        {
            lock (_gate)
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    RecoveryActionSpec spec = actions[i];
                    if (queued >= MaxActionWorkers)
                    {
                        dispatch.Receipt(spec.Name, RecoveryReceiptPhase.Skipped,
                            RecoveryOutcome.Unverified, 0,
                            $"worker capacity {MaxActionWorkers} reached");
                        continue;
                    }
                    if (spec.UseCircuitBreaker && IsCircuitOpen(spec.CircuitKey))
                    {
                        dispatch.Receipt(spec.Name, RecoveryReceiptPhase.CircuitOpened,
                            RecoveryOutcome.Unverified, 0, "circuit open; action skipped");
                        continue;
                    }

                    RecoveryActionContext context = new(
                        runId, reason, cause, forceAll, runDeadlineTick,
                        Math.Min(runDeadlineTick, Environment.TickCount64 + spec.DeadlineMs), journal,
                        (phase, outcome, detail) => dispatch.Receipt(spec.Name, phase, outcome, 0, detail));
                    // The coordinator keeps a reservation until every worker
                    // from this batch drains, so these preallocated records can
                    // be safely reused without a late action racing new data.
                    RecoveryDispatch.PendingAction item = _slots[queued++];
                    item.Reset(spec, context, dispatch, context.ActionDeadlineTick);
                    dispatch.Add(item);
                    _queue.Enqueue(item);
                }
                dispatch.Activate(queued);
                _workAvailable.Set();
            }

            Log.Info($"Recovery dispatch queued {queued} action(s) in {Environment.TickCount64 - dispatch.DispatchTick} ms; forceAll={forceAll}");
            return dispatch;
        }
        catch
        {
            Interlocked.Exchange(ref _activeDispatch, 0);
            throw;
        }
    }

    internal bool Wait(RecoveryDispatch dispatch, long deadlineTick)
    {
        try
        {
            bool complete = dispatch.WaitUntil(deadlineTick, OpenCircuit);
            if (!complete) Log.Warn("Recovery dispatch reached its global deadline; late actions are isolated by circuit breakers");
            return complete;
        }
        finally
        {
            // A fault in the bounded wait must not strand the coordinator's
            // reservation or leave late workers outside the closed batch.
            FinishDispatch(dispatch);
        }
    }

    private void FinishDispatch(RecoveryDispatch dispatch)
    {
        dispatch.Close();
        if (dispatch.IsDrained)
        {
            Interlocked.Exchange(ref _activeDispatch, 0);
        }
        else
        {
            // Keep the coordinator reservation until every old worker has
            // actually left the batch. This prevents a late native mutation
            // and its rollback from overlapping a newer recovery run.
            try
            {
                var drainThread = new Thread(() =>
                {
                    try { dispatch.WaitForDrain(); }
                    finally { Interlocked.Exchange(ref _activeDispatch, 0); }
                })
                {
                    IsBackground = true,
                    Name = "Thaw.RecoveryDrain",
                };
                TrySetPriority(drainThread, ThreadPriority.BelowNormal, drainThread.Name);
                drainThread.Start();
            }
            catch (Exception ex)
            {
                // Thread creation can fail under severe resource pressure.
                // Wait synchronously as a safe fallback so a late native
                // mutation never overlaps a later recovery run.
                Log.Error("Unable to start recovery drain watcher; waiting synchronously", ex);
                try { dispatch.WaitForDrain(); }
                finally { Interlocked.Exchange(ref _activeDispatch, 0); }
            }
        }
    }

    internal bool OpenCircuit(string key)
    {
        CircuitState state = _circuits.GetOrAdd(key, _ => new CircuitState());
        lock (state)
        {
            state.Failures = Math.Max(CircuitFailureThreshold, state.Failures + 1);
            state.OpenUntil = Environment.TickCount64 + CircuitCooldownMs;
            return true;
        }
    }

    private bool IsCircuitOpen(string key)
    {
        if (!_circuits.TryGetValue(key, out CircuitState? state)) return false;
        lock (state)
        {
            if (state.OpenUntil <= Environment.TickCount64)
            {
                state.OpenUntil = 0;
                state.Failures = 0;
                return false;
            }
            return true;
        }
    }

    private void RecordCircuit(RecoveryActionSpec spec, RecoveryActionResult result)
    {
        if (!spec.UseCircuitBreaker) return;
        if (result.Outcome == RecoveryOutcome.Recovered)
        {
            _circuits.TryRemove(spec.CircuitKey, out _);
            return;
        }
        if (result.Skipped) return;
        CircuitState state = _circuits.GetOrAdd(spec.CircuitKey, _ => new CircuitState());
        lock (state)
        {
            if (result.Outcome == RecoveryOutcome.Worse || result.Outcome == RecoveryOutcome.Unverified)
                state.Failures++;
            if (state.Failures >= CircuitFailureThreshold)
                state.OpenUntil = Environment.TickCount64 + CircuitCooldownMs;
        }
    }

    private static void TrySetPriority(Thread thread, ThreadPriority priority, string? name)
    {
        try { thread.Priority = priority; }
        catch (Exception ex)
        {
            Log.Debug("Recovery thread priority unavailable" +
                      (string.IsNullOrWhiteSpace(name) ? string.Empty : " (" + name + ")") +
                      ": " + ex.Message);
        }
    }

    private void WorkerLoop()
    {
        while (!_stop)
        {
            try
            {
                try
                {
                    _workAvailable.Wait(100);
                }
                catch (ObjectDisposedException)
                {
                    // Dispose can race the final worker wake-up. The coordinator is
                    // already shutting down, so this worker has no work to resume.
                    break;
                }
                if (_stop) break;

                while (true)
                {
                    RecoveryDispatch.PendingAction? item = null;
                    lock (_gate)
                    {
                        if (_queue.Count > 0) item = _queue.Dequeue();
                        else
                        {
                            try { _workAvailable.Reset(); }
                            catch (ObjectDisposedException) { _stop = true; }
                        }
                    }
                    if (item is null) break;

                    try
                    {
                        Execute(item);
                    }
                    catch (Exception ex)
                    {
                        // Execute normally contains its own action guard. Keep
                        // this outer boundary too: an unexpected coordinator
                        // failure must complete the slot and leave the worker
                        // available for the next shortcut.
                        Log.Error("Recovery worker execution failed; continuing", ex);
                        try
                        {
                            item.Batch?.Complete(item, RecoveryOutcome.Unverified,
                                "worker exception: " + ex.GetType().Name);
                        }
                        catch { }
                    }
                    finally
                    {
                        try { item.Batch?.WorkerFinished(); }
                        catch (Exception ex) { Log.Debug("Recovery worker drain accounting failed: " + ex.Message); }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failure in the reusable wait/queue boundary must not
                // permanently remove one of the pre-warmed recovery workers.
                Log.Error("Recovery worker loop failed; retrying", ex);
                if (!_stop)
                {
                    try { Thread.Sleep(25); } catch { }
                }
            }
        }
    }

    private void Execute(RecoveryDispatch.PendingAction item)
    {
        RecoveryActionSpec? spec = item.Spec;
        RecoveryActionContext? context = item.Context;
        RecoveryDispatch? dispatch = item.Batch;
        if (spec is null || context is null || dispatch is null) return;
        if (Volatile.Read(ref item.State) != 0) return;
        if (dispatch.IsClosed)
        {
            // A wait boundary may have faulted before its normal expiry pass.
            // Do not start a queued native mutation after the batch is closed.
            dispatch.Complete(item, RecoveryOutcome.Unverified,
                "dispatch closed before action execution");
            return;
        }

        item.StartedTick = Environment.TickCount64;
        dispatch.Receipt(spec.Name, RecoveryReceiptPhase.Started, RecoveryOutcome.Unverified,
            item.StartedTick, "action started");
        RecoveryProbe? before = null;
        RecoveryProbe? after = null;
        RecoveryActionResult result;
        try
        {
            if (context.IsExpired)
            {
                result = RecoveryActionResult.SkippedResult("action deadline elapsed before dispatch");
            }
            else
            {
                if (spec.CaptureBefore is not null) before = spec.CaptureBefore(context);
                result = context.IsExpired
                    ? RecoveryActionResult.SkippedResult("action deadline elapsed before execution")
                    : spec.Execute(context) ?? RecoveryActionResult.SkippedResult("action returned no result");
                if (spec.CaptureAfter is not null && !context.IsExpired) after = spec.CaptureAfter(context);

                if (spec.VerifyWithGenericProbe && !result.Skipped &&
                    result.Outcome == RecoveryOutcome.Unverified && before.HasValue && after.HasValue)
                {
                    RecoveryOutcome compared = spec.Evaluate?.Invoke(before.Value, after.Value) ??
                                                RecoveryProbe.Compare(before.Value, after.Value);
                    result = result with { Outcome = compared, Before = before, After = after };
                }
                else if (before.HasValue || after.HasValue)
                {
                    result = result with { Before = before, After = after };
                }
            }
        }
        catch (Exception ex)
        {
            result = RecoveryActionResult.SkippedResult("exception: " + ex.GetType().Name);
            Log.Debug($"Recovery action {spec.Name} failed: {ex.Message}");
        }

        bool completed = dispatch.Complete(item, result.Outcome, result.Detail);
        // A worker that returned after the owner marked its action timed out
        // must not clear the timeout circuit with a late optimistic result.
        if (completed) RecordCircuit(spec, result);
        if (completed)
        {
            Publish(dispatch, spec.Name, RecoveryReceiptPhase.Completed, result.Outcome,
                item.StartedTick, result.Detail);
        }
    }

    private void Publish(RecoveryDispatch dispatch, string action, RecoveryReceiptPhase phase,
        RecoveryOutcome outcome, long startedTick, string detail)
    {
        var receipt = new RecoveryProgressReceipt(dispatch.RunId, action, phase, outcome,
            startedTick > 0 ? Math.Max(0, Environment.TickCount64 - startedTick) : 0,
            detail, DateTimeOffset.UtcNow);
        try { _progress?.Invoke(receipt); } catch { }
        try { Progress?.Invoke(receipt); } catch { }
    }

    /// <summary>
    /// Uses the optional MMCSS helper when present.  Failure means only that
    /// this optimization was unavailable; normal recovery continues.
    /// </summary>
    internal static bool TryEnterMmcss(string taskName, RollbackJournal journal)
    {
        IntPtr task = IntPtr.Zero;
        try
        {
            task = AvSetMmThreadCharacteristics(taskName, out uint taskIndex);
            if (task == IntPtr.Zero) return false;
            IntPtr captured = task;
            journal.RecordResult("mmcss", () =>
            {
                try { return AvRevertMmThreadCharacteristics(captured); }
                catch { return false; }
            });
            Log.Debug($"MMCSS task entered: {taskName}, index={taskIndex}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("MMCSS helper unavailable: " + ex.Message);
            return false;
        }
    }

    /// <summary>Captures and applies a reversible process CPU-set selection.</summary>
    internal static bool TryApplyCpuSets(IntPtr process, uint[] desired, RollbackJournal journal)
    {
        if (process == IntPtr.Zero || desired is null || desired.Length == 0) return false;
        if (!TryReadCpuSets(process, out uint[] previous)) return false;
        if (!TryWriteCpuSets(process, desired)) return false;
        journal.RecordResult("cpu-sets", () => TryWriteCpuSets(process, previous));
        return true;
    }

    internal static bool TryReadCpuSets(IntPtr process, out uint[] ids)
    {
        ids = Array.Empty<uint>();
        try
        {
            if (!GetProcessDefaultCpuSets(process, IntPtr.Zero, 0, out uint count) || count == 0)
                return false;
            IntPtr ptr = Marshal.AllocHGlobal(checked((int)count * sizeof(uint)));
            try
            {
                if (!GetProcessDefaultCpuSets(process, ptr, count, out uint returned) || returned == 0) return false;
                var signedIds = new int[returned];
                Marshal.Copy(ptr, signedIds, 0, checked((int)returned));
                ids = Array.ConvertAll(signedIds, static id => unchecked((uint)id));
                return true;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException) { return false; }
        catch (Exception ex)
        {
            Log.Debug("CPU-set probe unavailable: " + ex.Message);
            return false;
        }
    }

    internal static bool TryWriteCpuSets(IntPtr process, uint[] ids)
    {
        if (process == IntPtr.Zero) return false;
        IntPtr ptr = IntPtr.Zero;
        try
        {
            ptr = ids is { Length: > 0 } ? Marshal.AllocHGlobal(ids.Length * sizeof(uint)) : IntPtr.Zero;
            if (ptr != IntPtr.Zero)
            {
                int[] signedIds = Array.ConvertAll(ids, static id => unchecked((int)id));
                Marshal.Copy(signedIds, 0, ptr, signedIds.Length);
            }
            return SetProcessDefaultCpuSets(process, ptr, (uint)(ids?.Length ?? 0));
        }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException) { return false; }
        catch (Exception ex)
        {
            Log.Debug("CPU-set helper unavailable: " + ex.Message);
            return false;
        }
        finally { if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>
    /// Temporarily clears power throttling for one exact process and journals
    /// the previous state.  GetProcessInformation is optional on older builds;
    /// in that case the helper cleanly declines the mutation.
    /// </summary>
    internal static bool TryBoostProcessQoS(uint pid, RollbackJournal journal)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;

            var before = new PROCESS_POWER_THROTTLING_STATE();
            int size = Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
            bool captured = GetProcessInformation(handle, ProcessPowerThrottling, ref before, size);
            // Without the exact prior state, do not make a one-way QoS change.
            // Older Windows builds simply fall back to the reversible priority
            // helper in Unfreezer.
            if (!captured) return false;
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = ProcessPowerThrottlingExecutionSpeed,
                StateMask = 0,
            };
            if (!SetProcessInformation(handle, ProcessPowerThrottling, ref state, size)) return false;

            IntPtr capturedHandle = handle;
            journal.RecordResult("qos", () =>
            {
                try
                {
                    var restore = before;
                    return SetProcessInformation(capturedHandle, ProcessPowerThrottling, ref restore, size);
                }
                finally { Native.CloseHandle(capturedHandle); }
            });
            handle = IntPtr.Zero;
            return true;
        }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException) { return false; }
        catch (Exception ex)
        {
            Log.Debug($"QoS helper unavailable for PID {pid}: {ex.Message}");
            return false;
        }
        finally { if (handle != IntPtr.Zero) Native.CloseHandle(handle); }
    }

    private const int ProcessPowerThrottling = ProcessInfoClassPowerThrottling;
    private const int ProcessInfoClassPowerThrottling = 9;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        internal uint Version;
        internal uint ControlMask;
        internal uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetProcessInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(
        IntPtr hProcess, int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation, int processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetProcessInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess, int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation, int processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetProcessDefaultCpuSets")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessDefaultCpuSets(
        IntPtr process, IntPtr cpuSetIds, uint cpuSetIdCount, out uint returnedCpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetProcessDefaultCpuSets")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDefaultCpuSets(
        IntPtr process, IntPtr cpuSetIds, uint cpuSetIdCount);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr taskHandle);

    internal static RecoveryProbe Probe(Watchdog watchdog, Func<bool> foregroundHung)
    {
        Native.MEMORYSTATUSEX memory = Native.GetMemoryStatus();
        bool memoryPressure = memory.dwMemoryLoad >= 92 ||
                              (memory.ullAvailPhys > 0 && memory.ullAvailPhys < 512UL * 1024 * 1024);
        bool hung = false;
        try { hung = foregroundHung(); } catch { }
        return new RecoveryProbe(hung, memoryPressure,
            memory.ullAvailPhys > long.MaxValue ? long.MaxValue : (long)memory.ullAvailPhys,
            watchdog.LastStallMs, watchdog.CpuPercent);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop = true;
        _workAvailable.Set();
        foreach (Thread worker in _workers)
        {
            try { worker.Join(250); } catch { }
        }
        _workAvailable.Dispose();
    }

    /// <summary>Cause-directed action names used by non-Alt+F4 triggers.</summary>
    internal static IReadOnlyList<string> SelectCauseDirectedActions(
        TriggerReason reason,
        HealthCause cause,
        bool forceAll,
        bool memoryPressure,
        bool foregroundHung,
        bool dwmHung,
        bool explorerHung)
    {
        if (forceAll || reason == TriggerReason.Hotkey)
            return SafeReversibleActions;

        var selected = new List<string>(12);
        void Add(string name) { if (!selected.Contains(name, StringComparer.OrdinalIgnoreCase)) selected.Add(name); }

        // Read-only evidence is cheap enough to keep on every cause-directed
        // path. It tells the next action (and the incident receipt) whether
        // the symptom was display, process, memory, storage, kernel, network,
        // thermal, or an unobservable deadlock.
        Add("resource-diagnostics");
        if (foregroundHung || reason is TriggerReason.Hotkey or TriggerReason.Panic)
            Add("foreground-probe");

        // Automatic watchdog recovery must remain evidence-only. A scheduler
        // delay is not enough evidence to mutate process priority, CPU sets,
        // memory, display, shell, DNS, or power state without an explicit user
        // shortcut or manual request.
        if (reason == TriggerReason.Auto)
            return selected;

        switch (cause)
        {
            case HealthCause.MemoryPressure:
                Add("working-set-trim");
                Add("background-demotion");
                Add("foreground-qos");
                Add("memory-cache");
                break;
            case HealthCause.CpuPressure:
                Add("background-demotion");
                Add("foreground-qos");
                Add("cpu-sets");
                break;
            case HealthCause.ResourcePressure:
                Add("display-reset");
                Add("foreground-qos");
                Add("background-demotion");
                Add("working-set-trim");
                break;
            case HealthCause.SchedulerStall:
                Add("foreground-qos");
                Add("cpu-sets");
                Add("shell-refresh");
                break;
            default:
                if (reason == TriggerReason.FrameDrop) { Add("display-reset"); Add("mmcss"); }
                else if (reason == TriggerReason.Panic) { Add("display-reset"); Add("foreground-qos"); }
                else if (reason == TriggerReason.Manual) Add("foreground-qos");
                break;
        }

        if (foregroundHung) Add("foreground-qos");
        if (dwmHung && reason == TriggerReason.Panic) Add("display-reset");
        if (explorerHung) Add("shell-refresh");
        if (memoryPressure && reason != TriggerReason.Auto) Add("working-set-trim");
        return selected;
    }
}
