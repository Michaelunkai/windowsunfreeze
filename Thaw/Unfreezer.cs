using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Thaw;

internal enum TriggerReason { Hotkey, Panic, Manual, Auto, Slowness, FrameDrop }

internal sealed record UnfreezeStats(
    int ProcessesTuned, int AppsBoosted, long FreedMb, long DurationMs, TriggerReason Reason,
    bool GpuReset, bool DwmRestarted, bool ExplorerRestarted)
{
    /// <summary>Number of process snapshots inspected before the recovery budget expired.</summary>
    public int ProcessesScanned { get; init; }

    /// <summary>Process candidates intentionally left untouched by the safety filters.</summary>
    public int ProcessesSkipped { get; init; }

    /// <summary>Number of working-set trim attempts that returned success.</summary>
    public int WorkingSetsTrimmed { get; init; }

    /// <summary>Number of trim attempts that failed or raced with process exit.</summary>
    public int WorkingSetFailures { get; init; }

    /// <summary>Number of recovery actions that returned a positive result.</summary>
    public int ActionsSucceeded { get; init; }

    /// <summary>Number of actions deliberately skipped by a guard or because they were unavailable.</summary>
    public int ActionsSkipped { get; init; }

    /// <summary>True when the bounded recovery budget was reached before all tiers ran.</summary>
    public bool BudgetExpired { get; init; }

    /// <summary>Captured foreground process id; zero means no foreground window was available.</summary>
    public uint ForegroundPid { get; init; }

    /// <summary>Human-readable, bounded diagnostics for logs and support reports.</summary>
    public string Diagnostics { get; init; } = "";

    /// <summary>Cause selected from the watchdog and the trigger policy.</summary>
    public string PrimaryCause { get; init; } = "unknown";

    /// <summary>Per-action outcome summary; API acceptance alone is never proof.</summary>
    public IReadOnlyDictionary<string, RecoveryOutcome> ActionOutcomes { get; init; } =
        new Dictionary<string, RecoveryOutcome>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bounded start/finish/timeout/rollback receipts for the recovery run.</summary>
    public IReadOnlyList<RecoveryProgressReceipt> ProgressReceipts { get; init; } =
        Array.Empty<RecoveryProgressReceipt>();

    /// <summary>Overall evidence result; it is not inferred from API acceptance alone.</summary>
    public RecoveryOutcome OverallOutcome { get; init; } = RecoveryOutcome.Unverified;

    public string Summary
    {
        get
        {
            var parts = new List<string>
            {
                (OverallOutcome switch
                {
                    RecoveryOutcome.Recovered => "Recovery verified",
                    RecoveryOutcome.Unchanged => "Recovery completed; no measurable improvement",
                    RecoveryOutcome.Worse => "Recovery completed; measured degradation",
                    _ => "Recovery completed; outcome unverified",
                }) + $" in {DurationMs} ms",
                $"trimmed {WorkingSetsTrimmed}/{ProcessesScanned} processes",
                $"boosted {AppsBoosted} apps",
                $"+{FreedMb} MB RAM",
            };
            if (GpuReset) parts.Add("GPU reset");
            if (DwmRestarted) parts.Add("dwm restarted (screen was still frozen)");
            if (ExplorerRestarted) parts.Add("explorer restarted");
            if (BudgetExpired) parts.Add("recovery budget reached");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// The bounded, tiered recovery engine.  It gives Thaw enough scheduling priority
/// to observe a stall, gathers responsiveness diagnostics, applies the documented
/// display reset, and temporarily boosts only the foreground/shell processes.
/// Memory/cache relief is pressure-gated and explicit-trigger-only; automatic
/// watchdog recovery remains diagnostics/safe-only.  Explorer/DWM termination is
/// restricted to explicit user requests and exact known shell/compositor PIDs.
/// No arbitrary user process is closed, suspended, paused, or interrupted.
/// </summary>
internal sealed class Unfreezer : IDisposable
{
    // Recovery must remain useful under scheduler pressure, but it must also have a
    // hard upper bound.  In particular, no process enumeration or shell wait is
    // allowed to turn an emergency action into another source of unresponsiveness.
    internal const int RecoveryBudgetMs = 10_000;
    internal const int AltF4ActivationDeadlineMs = 1_000;
    internal const int DisplayProbeDelayMs = 50;
    private const int MaxWorkingSetTrims = 96;
    private const long MinimumTrimWorkingSetBytes = 64L * 1024 * 1024;
    private const long MinimumAvailableMemoryBytes = 512L * 1024 * 1024;
    private const long DnsFlushCooldownMs = 60_000;

    private static readonly HashSet<string> DoNotTrim = new(StringComparer.OrdinalIgnoreCase)
    {
        // Kernel, security, authentication, service-control and networking
        // processes are never part of a best-effort working-set recovery.
        "System", "Idle", "Registry", "Secure System", "Memory Compression",
        "csrss", "smss", "wininit", "winlogon", "lsass", "services", "svchost",
        "MsMpEng", "SecurityHealthService", "SearchIndexer", "audiodg", "wlanext",
    };

    private static readonly HashSet<string> ShellApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
    };

    // These actions collect evidence about a stuck system; a successful query
    // is not itself proof that recovery changed the system.  A worsened sample
    // still remains visible as a warning, but diagnostic success cannot make a
    // recovery with no effective mutation look verified.
    private static readonly HashSet<string> EvidenceOnlyActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio-diagnostic", "dwm-frame-diagnostic", "foreground-probe",
        "foreground-wait-chain", "event-device-diagnostics", "resource-diagnostics",
    };

    private readonly Config _config;
    private readonly Watchdog _watchdog;
    private readonly RecoveryCoordinator _coordinator;
    private readonly AutoResetEvent _recoveryWake = new(false);
    private readonly object _recoveryWorkerGate = new();
    private Thread? _recoveryWorker;
    private int _pendingReason;
    private int _active;
    private int _disposed;
    private static long _lastDnsFlushTick;
    private static readonly object PowerGate = new();
    private static System.Threading.Timer? _powerRestoreTimer;
    private static string? _powerRestoreScheme;

    private sealed class RecoveryCounters
    {
        public int ProcessesScanned;
        public int ProcessesSkipped;
        public int WorkingSetsTrimmed;
        public int WorkingSetFailures;
        public int ActionsSucceeded;
        public int ActionsSkipped;
        public bool BudgetExpired;
        public uint ForegroundPid;
        public string ForegroundName = "unknown";
        public bool ForegroundHung;
        public bool DwmHung;
        public bool ExplorerHung;
        public string MemoryReason = "not measured";
        public readonly List<string> ActionResults = new();

        public void Succeeded(string action, string detail = "")
        {
            ActionsSucceeded++;
            ActionResults.Add(string.IsNullOrWhiteSpace(detail) ? action + "=ok" : action + "=ok(" + detail + ")");
        }

        public void Skipped(string action, string detail)
        {
            ActionsSkipped++;
            ActionResults.Add(action + "=skip(" + detail + ")");
        }

        public string DiagnosticSummary()
        {
            string actions = ActionResults.Count == 0 ? "none" : string.Join(",", ActionResults.Take(16));
            return $"fg={ForegroundPid}/{ForegroundName},fgHung={ForegroundHung},dwmHung={DwmHung},explorerHung={ExplorerHung},memory={MemoryReason},actions={actions}";
        }
    }

    private sealed class PriorityRestore
    {
        public required IntPtr Handle;
        public required uint Pid;
        public required uint PreviousClass;
        public required string Name;
    }

    public event Action? ExplorerRestartedNow;

    public event Action<UnfreezeStats>? Completed;

    public Unfreezer(Config config, Watchdog watchdog)
    {
        _config = config;
        _watchdog = watchdog;
        _coordinator = new RecoveryCoordinator(receipt =>
            Log.Debug($"Recovery progress: {receipt.Action} {receipt.Phase} {receipt.Outcome} " +
                      $"({receipt.ElapsedMs} ms) {receipt.Detail}"));
        EnsureRecoveryWorker("initial");
    }

    internal static (bool Display, bool System, bool Memory) GetRecoveryProfile(TriggerReason reason)
    {
        bool display = reason is TriggerReason.Panic or TriggerReason.Hotkey or TriggerReason.FrameDrop;
        bool system = reason is TriggerReason.Panic or TriggerReason.Manual or TriggerReason.Hotkey or TriggerReason.Slowness;
        return (display, system, system);
    }

    internal static bool IsForceAll(TriggerReason reason) => reason == TriggerReason.Hotkey;

    internal static bool DefersExpensivePreDispatchProbes(TriggerReason reason) => IsForceAll(reason);

    public bool Trigger(TriggerReason reason)
    {
        if (!TryEnter())
        {
            Log.Debug("Unfreeze already running, skipping duplicate trigger");
            return false;
        }

        if (_coordinator.IsBusy)
        {
            Interlocked.Exchange(ref _active, 0);
            Log.Debug("Unfreeze deferred: a timed-out recovery action is still draining");
            return false;
        }

        int encodedReason = EncodeReason(reason);
        Interlocked.Exchange(ref _pendingReason, encodedReason);
        if (!QueueRecoveryRequest())
        {
            // If the request was not consumed, release the active fence so a
            // later shortcut can retry after a transient worker/handle failure.
            if (Interlocked.CompareExchange(ref _pendingReason, 0, encodedReason) == encodedReason)
            {
                Interlocked.Exchange(ref _active, 0);
                Log.Error("Unable to queue unfreeze request on the pre-warmed worker");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reports whether the already-running request handoff thread is available.
    /// A trigger can recreate it if a catastrophic thread failure ever removes it.
    /// </summary>
    public bool RecoveryWorkerAlive => Volatile.Read(ref _recoveryWorker)?.IsAlive == true;

    /// <summary>
    /// True while an existing recovery run or late coordinator action is still
    /// covering the system. The UI uses this to avoid spawning a duplicate
    /// out-of-process force-all rescue for an intentionally overlapping chord.
    /// </summary>
    internal bool IsRecoveryBusy => Volatile.Read(ref _active) != 0 || _coordinator.IsBusy;

    private bool QueueRecoveryRequest()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            if (!EnsureRecoveryWorker(attempt == 0 ? "trigger" : "trigger-retry")) continue;

            Thread? worker = Volatile.Read(ref _recoveryWorker);
            if (worker?.IsAlive != true) continue;
            try
            {
                _recoveryWake.Set();
                // A worker can fail in the tiny window between the liveness
                // check and Set(). Retry once through EnsureRecoveryWorker so
                // the accepted request is not stranded behind a dead thread.
                if (worker.IsAlive) return true;
                Log.Debug("Recovery request worker exited during wake; retrying handoff");
            }
            catch (Exception ex)
            {
                Log.Debug("Unfreeze request wake failed: " + ex.Message);
            }
        }

        return false;
    }

    private bool EnsureRecoveryWorker(string reason)
    {
        lock (_recoveryWorkerGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            if (_recoveryWorker?.IsAlive == true) return true;

            Thread? replacement = null;
            try
            {
                replacement = new Thread(RecoveryRequestWorkerMain)
                {
                    IsBackground = true,
                    Name = "Thaw recovery request worker",
                };
                TrySetThreadPriority(replacement, ThreadPriority.Highest, replacement.Name);
                Volatile.Write(ref _recoveryWorker, replacement);
                replacement.Start();
                if (!string.Equals(reason, "initial", StringComparison.OrdinalIgnoreCase))
                    Log.Info("Pre-warmed recovery request worker started (" + reason + ")");
                return true;
            }
            catch (Exception ex)
            {
                if (replacement is not null && ReferenceEquals(Volatile.Read(ref _recoveryWorker), replacement))
                    Volatile.Write(ref _recoveryWorker, null);
                Log.Error("Unable to start pre-warmed recovery request worker", ex);
                return false;
            }
        }
    }

    private void RecoveryRequestWorkerMain()
    {
        int inFlightEncodedReason = 0;
        bool inFlightFailurePublished = false;
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    _recoveryWake.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error("Recovery request worker wait failed; retrying", ex);
                    if (Volatile.Read(ref _disposed) != 0) break;
                    try { Thread.Sleep(25); } catch { }
                    continue;
                }

                if (Volatile.Read(ref _disposed) != 0) break;
                int encodedReason = Interlocked.Exchange(ref _pendingReason, 0);
                if (encodedReason <= 0) continue;

                inFlightEncodedReason = encodedReason;
                inFlightFailurePublished = false;
                try
                {
                    Run((TriggerReason)(encodedReason - 1));
                }
                catch (Exception ex)
                {
                    // Run has its own final boundary; this guard protects the
                    // pre-warmed handoff if a future change escapes that boundary.
                    Log.Error("Recovery request worker escaped the run boundary", ex);
                    inFlightFailurePublished = true;
                    PublishTerminalWorkerFailure(encodedReason, "run-boundary", ex);
                    Interlocked.Exchange(ref _active, 0);
                }
                finally { inFlightEncodedReason = 0; }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Recovery request worker failed", ex);
            if (inFlightEncodedReason > 0 && !inFlightFailurePublished)
            {
                inFlightFailurePublished = true;
                PublishTerminalWorkerFailure(inFlightEncodedReason, "worker-boundary", ex);
                Interlocked.Exchange(ref _active, 0);
            }
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                int strandedEncodedReason = Interlocked.Exchange(ref _pendingReason, 0);
                if (strandedEncodedReason > 0)
                {
                    PublishTerminalWorkerFailure(
                        strandedEncodedReason,
                        "worker-exited-before-consume",
                        new InvalidOperationException("The pre-warmed recovery worker exited before consuming the request."));
                }
            }
            Interlocked.Exchange(ref _active, 0);
            if (ReferenceEquals(Volatile.Read(ref _recoveryWorker), Thread.CurrentThread))
                Volatile.Write(ref _recoveryWorker, null);
        }
    }

    /// <summary>
    /// Converts a recovery handoff failure into the same terminal completion
    /// contract used by Run().  A request can be accepted by Trigger() before
    /// the worker reaches Run(), so clearing only the active flag would leave
    /// the tray guard and rescue helper waiting for an event that can never
    /// arrive.  The receipt is deliberately unverified: no recovery mutation
    /// was proven to run.
    /// </summary>
    private void PublishTerminalWorkerFailure(int encodedReason, string boundary, Exception exception)
    {
        if (Volatile.Read(ref _disposed) != 0 || encodedReason <= 0) return;

        TriggerReason reason = (TriggerReason)(encodedReason - 1);
        var failure = new UnfreezeStats(0, 0, 0, 0, reason, false, false, false)
        {
            OverallOutcome = RecoveryOutcome.Unverified,
            PrimaryCause = "recovery-worker-failure",
            Diagnostics = $"recovery-worker-boundary={boundary},exception={exception.GetType().Name}",
        };

        try { Completed?.Invoke(failure); }
        catch (Exception notifyEx) { Log.Error("Unable to publish recovery-worker failure result", notifyEx); }
    }

    /// <summary>
    /// Recovery entry point.  The coordinator owns all action dispatch, so a
    /// force-all trigger only queues preallocated workers; no native action is
    /// allowed to hold up the others.  The older implementation is retained
    /// below as a reference-compatible fallback for support builds, but is not
    /// used by normal triggers.
    /// </summary>
    private void Run(TriggerReason reason)
    {
        bool forceAll = IsForceAll(reason);
        var deferredPreDispatchNotes = new List<string>(4);
        if (!forceAll)
        {
            try { _watchdog.CaptureTelemetryBefore(reason.ToString()); }
            catch (Exception ex) { deferredPreDispatchNotes.Add("Pre-recovery telemetry capture unavailable: " + ex.Message); }
        }
        var sw = Stopwatch.StartNew();
        long deadline = Environment.TickCount64 + RecoveryBudgetMs;
        var journal = new RollbackJournal();
        var receipts = new List<RecoveryProgressReceipt>();
        var outcomes = new Dictionary<string, RecoveryOutcome>(StringComparer.OrdinalIgnoreCase);
        var receiptGate = new object();
        var counters = new RecoveryCounters();
        ProcessPriorityClass oldPriority = ProcessPriorityClass.Normal;
        ThreadPriority oldThreadPriority = ThreadPriority.Normal;
        bool oldPriorityCaptured = false;
        bool oldThreadPriorityCaptured = false;
        bool processPriorityChanged = false;
        bool gpuReset = false;
        bool dwmRestarted = false;
        bool explorerRestarted = false;
        ulong availableBefore = 0;
        string primaryCause = "unknown";
        RecoveryDispatch? dispatch = null;
        bool dispatchFinalized = false;
        Guid runId = Guid.NewGuid();

        void CaptureReceipt(RecoveryProgressReceipt receipt)
        {
            lock (receiptGate)
            {
                if (receipts.Count < 256) receipts.Add(receipt);
                if (receipt.Phase == RecoveryReceiptPhase.Completed)
                    outcomes[receipt.Action] = receipt.Outcome;
            }
            Log.Debug($"Recovery receipt: {receipt.Action} {receipt.Phase} {receipt.Outcome} ({receipt.ElapsedMs} ms) {receipt.Detail}");
        }

        try
        {
            // Keep Thaw schedulable without using REALTIME/TIME_CRITICAL.
            try
            {
                using var self = Process.GetCurrentProcess();
                oldPriority = self.PriorityClass;
                oldPriorityCaptured = true;
                if (oldPriority == ProcessPriorityClass.Normal)
                {
                    self.PriorityClass = ProcessPriorityClass.AboveNormal;
                    processPriorityChanged = true;
                    journal.RecordResult("thaw-priority", () =>
                    {
                        try { return Native.SetPriorityClass(Native.GetCurrentProcess(), Native.NORMAL_PRIORITY_CLASS); }
                        catch { return false; }
                    });
                }
            }
            catch (Exception ex) { deferredPreDispatchNotes.Add("Own process priority boost unavailable: " + ex.Message); }

            try
            {
                oldThreadPriority = Thread.CurrentThread.Priority;
                oldThreadPriorityCaptured = true;
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                journal.RecordResult("thaw-thread-priority", () =>
                {
                    try
                    {
                        Thread.CurrentThread.Priority = oldThreadPriority;
                        return true;
                    }
                    catch { return false; }
                });
            }
            catch (Exception ex) { deferredPreDispatchNotes.Add("Managed worker priority unavailable: " + ex.Message); }

            Native.MEMORYSTATUSEX memoryBefore = default;
            bool memorySampleRead = false;
            try
            {
                memoryBefore = Native.GetMemoryStatus();
                memorySampleRead = true;
            }
            catch (Exception ex)
            {
                deferredPreDispatchNotes.Add("Pre-recovery memory sample unavailable: " + ex.Message);
            }
            availableBefore = memoryBefore.ullAvailPhys;
            bool deferExpensiveProbes = DefersExpensivePreDispatchProbes(reason);
            uint foregroundPid = 0;
            try { foregroundPid = Native.GetForegroundPid(); }
            catch (Exception ex) { deferredPreDispatchNotes.Add("Foreground PID probe unavailable: " + ex.Message); }
            counters.ForegroundPid = foregroundPid;
            counters.ForegroundName = deferExpensiveProbes ? "deferred" : TryGetProcessName(foregroundPid);
            // A force-all shortcut deliberately does not wait for the expensive
            // responsiveness probes before queuing recovery. Its action graph
            // already includes the display/shell paths, and the probes can be
            // the very components that are stalled. Cause-directed requests keep
            // the probes for their safer selection gates.
            counters.ForegroundHung = !deferExpensiveProbes && IsForegroundHung(foregroundPid, logFailures: false);
            counters.DwmHung = !deferExpensiveProbes && TryGetDwmHungForSelection();
            counters.ExplorerHung = !deferExpensiveProbes && IsExplorerHung(logFailures: false);
            bool measuredMemoryPressure;
            string memoryReason;
            try
            {
                measuredMemoryPressure = IsMemoryPressure(memoryBefore, out memoryReason);
            }
            catch (Exception ex)
            {
                measuredMemoryPressure = false;
                memoryReason = "memory-pressure probe unavailable";
                deferredPreDispatchNotes.Add("Memory-pressure probe unavailable: " + ex.Message);
            }
            bool memoryPressure = measuredMemoryPressure || _watchdog.MemoryPressure || forceAll;
            if (!memorySampleRead && !forceAll)
                memoryReason = "memory-sample-unavailable; using watchdog evidence";
            if (forceAll)
                memoryReason = $"force-all(load={memoryBefore.dwMemoryLoad}%, available={memoryBefore.ullAvailPhys / (1024 * 1024)} MB)";
            counters.MemoryReason = memoryReason;
            HealthCause cause = _watchdog.Cause;
            if (cause == HealthCause.Healthy)
            {
                cause = counters.ForegroundHung ? HealthCause.SchedulerStall :
                        memoryPressure ? HealthCause.MemoryPressure :
                        _watchdog.CpuPressure ? HealthCause.CpuPressure : HealthCause.Healthy;
            }
            primaryCause = _watchdog.HealthReason;
            if (string.IsNullOrWhiteSpace(primaryCause) || primaryCause == "healthy") primaryCause = cause.ToString();
            var profile = GetRecoveryProfile(reason);
            IReadOnlyList<string> selected = RecoveryCoordinator.SelectCauseDirectedActions(
                reason, cause, forceAll, memoryPressure, counters.ForegroundHung,
                counters.DwmHung, counters.ExplorerHung);

            RecoveryProbe Probe() => RecoveryCoordinator.Probe(_watchdog, () => IsForegroundHung(foregroundPid));

            // Queue every selected action in one coordinator call.  The call only
            // fills preallocated slots; force-all dispatch is measured against the
            // explicit sub-second activation deadline.
            IReadOnlyList<RecoveryActionSpec> actions = BuildActionSpecs(
                reason, cause, forceAll, memoryPressure, memoryReason, foregroundPid,
                profile, selected, counters, journal, Probe,
                () => gpuReset = true, () => dwmRestarted = true, () => explorerRestarted = true);
            long dispatchStarted = Environment.TickCount64;
            dispatch = _coordinator.Dispatch(runId, reason, cause, forceAll, deadline, journal, actions);
            long dispatchMs = Environment.TickCount64 - dispatchStarted;
            if (forceAll)
            {
                try { _watchdog.CaptureTelemetryBefore(reason.ToString()); }
                catch (Exception ex) { deferredPreDispatchNotes.Add("Pre-recovery telemetry capture unavailable: " + ex.Message); }
            }
            // Log only after the coordinator has signalled the pre-warmed action
            // workers. File I/O or a locked log must never precede first recovery
            // mutations on the force-all shortcut path.
            foreach (string note in deferredPreDispatchNotes)
                Log.Debug(note);
            Log.Info($"Unfreeze triggered ({reason})");
            Log.Info($"Recovery diagnostics: reason={reason}, cause={primaryCause}, budget={RecoveryBudgetMs} ms, " +
                     $"elevated={Native.IsElevated()}, fg={foregroundPid}/{counters.ForegroundName}, " +
                     $"fgHung={counters.ForegroundHung}, dwmHung={counters.DwmHung}, explorerHung={counters.ExplorerHung}, " +
                     $"stall={_watchdog.LastStallMs} ms, cpu={_watchdog.CpuPercent:0}%, ram={memoryBefore.dwMemoryLoad}%, " +
                     $"memory={memoryReason}, forceAll={forceAll}, selected={string.Join("|", selected)}");
            if (forceAll && dispatchMs >= RecoveryCoordinator.ForceAllDispatchDeadlineMs)
                Log.Warn($"Alt+F4 force-all dispatch exceeded {RecoveryCoordinator.ForceAllDispatchDeadlineMs} ms: {dispatchMs} ms");
            else if (forceAll)
                Log.Info($"Alt+F4 force-all dispatch ready in {dispatchMs} ms");

            // Let all independent actions run while the owner thread performs
            // only bounded waiting.  No action is allowed to consume this thread.
            bool complete;
            try
            {
                complete = _coordinator.Wait(dispatch, deadline);
            }
            finally
            {
                // RecoveryCoordinator.Wait owns its own finally boundary. This
                // flag tells the outer failure path that the reservation is
                // already closed if the wait itself throws.
                dispatchFinalized = true;
            }
            if (!complete) counters.BudgetExpired = true;

            IReadOnlyList<RecoveryProgressReceipt> batchReceipts = dispatch.Receipts;
            lock (receiptGate)
            {
                foreach (RecoveryProgressReceipt receipt in batchReceipts)
                {
                    if (receipts.Count < 256) receipts.Add(receipt);
                    if (receipt.Phase == RecoveryReceiptPhase.Completed)
                        outcomes[receipt.Action] = receipt.Outcome;
                }
            }
            foreach (RecoveryProgressReceipt receipt in batchReceipts)
            {
                if (receipt.Phase == RecoveryReceiptPhase.Completed)
                {
                    if (receipt.Action == "gpu-reset" && receipt.Outcome != RecoveryOutcome.Worse) gpuReset = true;
                    if (receipt.Action == "dwm-restart" && receipt.Outcome == RecoveryOutcome.Recovered) dwmRestarted = true;
                    if (receipt.Action == "explorer-restart" && receipt.Outcome == RecoveryOutcome.Recovered) explorerRestarted = true;
                }
            }

            // Every temporary mutation is restored in reverse order, even when
            // an action timed out or a later action failed.
            journal.RollbackAll((name, started, detail) =>
            {
                RecoveryReceiptPhase phase = started ? RecoveryReceiptPhase.RollbackStarted : RecoveryReceiptPhase.RollbackCompleted;
                CaptureReceipt(new RecoveryProgressReceipt(runId, name, phase, RecoveryOutcome.Unverified,
                    sw.ElapsedMilliseconds, detail, DateTimeOffset.UtcNow));
            });

            try { _watchdog.CaptureTelemetryAfter(reason.ToString()); }
            catch (Exception ex) { Log.Debug("Post-recovery telemetry capture unavailable: " + ex.Message); }

            Native.MEMORYSTATUSEX memoryAfter = Native.GetMemoryStatus();
            long freedMb = memoryAfter.ullAvailPhys > availableBefore
                ? (long)((memoryAfter.ullAvailPhys - availableBefore) / (1024 * 1024)) : 0;
            counters.ActionsSucceeded = outcomes.Values.Count(value => value == RecoveryOutcome.Recovered);
            counters.ActionsSkipped = actions.Count - counters.ActionsSucceeded;
            counters.WorkingSetsTrimmed = Math.Max(
                _lastWorkingSetsTrimmed,
                outcomes.TryGetValue("working-set-trim", out RecoveryOutcome trimOutcome) &&
                trimOutcome == RecoveryOutcome.Recovered ? 1 : 0);
            counters.ProcessesScanned = Math.Max(counters.ProcessesScanned, _lastProcessScanCount);
            counters.ProcessesSkipped = Math.Max(counters.ProcessesSkipped, _lastProcessSkipCount);
            counters.WorkingSetFailures = _lastWorkingSetFailures;
            counters.ActionResults.AddRange(outcomes.Select(pair => pair.Key + "=" + pair.Value.ToString().ToLowerInvariant()));
            var stats = new UnfreezeStats(counters.WorkingSetsTrimmed, _lastAppsBoosted, freedMb,
                sw.ElapsedMilliseconds, reason, gpuReset, dwmRestarted, explorerRestarted)
            {
                ProcessesScanned = counters.ProcessesScanned,
                ProcessesSkipped = counters.ProcessesSkipped,
                WorkingSetsTrimmed = counters.WorkingSetsTrimmed,
                WorkingSetFailures = counters.WorkingSetFailures,
                ActionsSucceeded = counters.ActionsSucceeded,
                ActionsSkipped = counters.ActionsSkipped,
                BudgetExpired = counters.BudgetExpired,
                ForegroundPid = foregroundPid,
                PrimaryCause = primaryCause,
                OverallOutcome = SummarizeOutcomes(outcomes),
                ActionOutcomes = new Dictionary<string, RecoveryOutcome>(outcomes, StringComparer.OrdinalIgnoreCase),
                ProgressReceipts = receipts.ToArray(),
                Diagnostics = counters.DiagnosticSummary() + $",cause={primaryCause},receipts={receipts.Count}",
            };
            Log.Info("Unfreeze done: " + stats.Summary);
            Log.Info($"Recovery details: cause={primaryCause}, scanned={stats.ProcessesScanned}, skipped={stats.ProcessesSkipped}, " +
                     $"trimmed={stats.WorkingSetsTrimmed}, trimFailures={stats.WorkingSetFailures}, actionsOk={stats.ActionsSucceeded}, " +
                     $"actionsSkipped={stats.ActionsSkipped}, budgetExpired={stats.BudgetExpired}, diagnostics={stats.Diagnostics}");
            try { Completed?.Invoke(stats); } catch (Exception ex) { Log.Error("Completed handler error", ex); }
        }
        catch (Exception ex)
        {
            Log.Error("Unfreeze failed", ex);
            if (dispatch is not null && !dispatchFinalized)
            {
                try { _coordinator.Abort(dispatch); }
                catch (Exception abortEx) { Log.Error("Unfreeze dispatch abort failed", abortEx); }
                dispatchFinalized = true;
            }
            try { journal.RollbackAll(); } catch { }
            try
            {
                // Always publish a terminal result, including failures before
                // dispatch. The tray guard and integrations must be able to
                // retry immediately instead of waiting for a stale timeout.
                var failure = new UnfreezeStats(
                    counters.WorkingSetsTrimmed,
                    _lastAppsBoosted,
                    0,
                    sw.ElapsedMilliseconds,
                    reason,
                    gpuReset,
                    dwmRestarted,
                    explorerRestarted)
                {
                    ProcessesScanned = counters.ProcessesScanned,
                    ProcessesSkipped = counters.ProcessesSkipped,
                    WorkingSetsTrimmed = counters.WorkingSetsTrimmed,
                    WorkingSetFailures = counters.WorkingSetFailures,
                    ActionsSucceeded = counters.ActionsSucceeded,
                    ActionsSkipped = counters.ActionsSkipped,
                    BudgetExpired = Environment.TickCount64 >= deadline,
                    ForegroundPid = counters.ForegroundPid,
                    PrimaryCause = primaryCause,
                    OverallOutcome = RecoveryOutcome.Unverified,
                    Diagnostics = counters.DiagnosticSummary() + ",run-exception=" + ex.GetType().Name,
                };
                Completed?.Invoke(failure);
            }
            catch (Exception notifyEx)
            {
                Log.Error("Unable to publish failed recovery result", notifyEx);
            }
        }
        finally
        {
            // The journal normally performed these restores above.  These
            // explicit guards cover an exception before the journal was filled.
            if (oldThreadPriorityCaptured) { try { Thread.CurrentThread.Priority = oldThreadPriority; } catch { } }
            if (oldPriorityCaptured && processPriorityChanged)
            {
                try { Process.GetCurrentProcess().PriorityClass = oldPriority; } catch { }
            }
            Interlocked.Exchange(ref _active, 0);
        }
    }

    private int _lastProcessScanCount;
    private int _lastProcessSkipCount;
    private int _lastWorkingSetsTrimmed;
    private int _lastWorkingSetFailures;
    private int _lastAppsBoosted;

    private void RunLegacy(TriggerReason reason)
    {
        Log.Info($"Unfreeze triggered ({reason})");
        var sw = Stopwatch.StartNew();
        long deadline = Environment.TickCount64 + RecoveryBudgetMs;
        var counters = new RecoveryCounters();
        var priorityRestores = new List<PriorityRestore>();

        ProcessPriorityClass oldPriority = ProcessPriorityClass.Normal;
        ThreadPriority oldThreadPriority = ThreadPriority.Normal;
        bool oldPriorityCaptured = false;
        bool oldThreadPriorityCaptured = false;
        bool processPriorityChanged = false;

        try
        {
            // Keep Thaw schedulable under load, but never use time-critical
            // priority: it can starve the threads needed for recovery.
            try
            {
                using var self = Process.GetCurrentProcess();
                oldPriority = self.PriorityClass;
                oldPriorityCaptured = true;
                if (oldPriority == ProcessPriorityClass.Normal)
                {
                    self.PriorityClass = ProcessPriorityClass.AboveNormal;
                    processPriorityChanged = true;
                }
            }
            catch (Exception ex) { Log.Debug("Own process priority boost unavailable: " + ex.Message); }

            try
            {
                oldThreadPriority = Thread.CurrentThread.Priority;
                oldThreadPriorityCaptured = true;
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
            catch (Exception ex) { Log.Debug("Managed worker priority unavailable: " + ex.Message); }

            var memoryBefore = Native.GetMemoryStatus();
            ulong availBefore = memoryBefore.ullAvailPhys;
            bool gpuReset = false, explorerRestarted = false, dwmRestarted = false;
            uint fgPid = Native.GetForegroundPid();
            counters.ForegroundPid = fgPid;
            int selfId = Environment.ProcessId;
            counters.ForegroundName = TryGetProcessName(fgPid);
            counters.ForegroundHung = IsForegroundHung(fgPid);
            counters.DwmHung = IsDwmHung();
            counters.ExplorerHung = IsExplorerHung();
            bool measuredMemoryPressure = IsMemoryPressure(memoryBefore, out string memoryReason);
            bool forceAll = IsForceAll(reason);
            bool memoryPressure = measuredMemoryPressure || forceAll;
            if (forceAll)
                memoryReason = $"force-all(load={memoryBefore.dwMemoryLoad}%, available={memoryBefore.ullAvailPhys / (1024 * 1024)} MB)";
            counters.MemoryReason = memoryReason;
            // Alt+F4 is the single "do everything" chord: all tiers are activated
            // immediately in the same bounded recovery pass.
            var profile = GetRecoveryProfile(reason);
            bool allowDisplayRecovery = profile.Display;
            bool allowSystemRecovery = profile.System;
            bool allowMemoryRecovery = profile.Memory;

            Log.Info($"Recovery diagnostics: reason={reason}, budget={RecoveryBudgetMs} ms, " +
                     $"elevated={Native.IsElevated()}, fg={fgPid}/{counters.ForegroundName}, " +
                     $"fgHung={counters.ForegroundHung}, dwmHung={counters.DwmHung}, " +
                     $"explorerHung={counters.ExplorerHung}, stall={_watchdog.LastStallMs} ms, " +
                     $"cpu={_watchdog.CpuPercent:0}%, ram={memoryBefore.dwMemoryLoad}%, memory={memoryReason}, forceAll={forceAll}");

            // The graphics reset may block while Windows recovers the display stack.
            // Run the display tier independently so system/memory/shell work starts
            // immediately instead of waiting behind that OS call.
            Thread? displayWorker = null;
            var displayCounters = new RecoveryCounters { DwmHung = counters.DwmHung };
            bool displayGpuReset = false;
            bool displayDwmRestarted = false;
            if (allowDisplayRecovery && _config.ResetGpuDriver && HasBudget(deadline, counters, "gpu-reset"))
            {
                displayWorker = new Thread(() =>
                {
                    try
                    {
                        displayGpuReset = ResetGpuDriver(displayCounters);
                        // Alt+F4 is the explicit force-all path. Other explicit profiles
                        // retain the compositor evidence gate.
                        if ((reason is TriggerReason.Panic or TriggerReason.Hotkey) && _config.RestartDwmOnFrozenScreen &&
                            HasBudget(deadline, displayCounters, "dwm-probe") &&
                            (forceAll || IsScreenStillFrozen(deadline, displayCounters)))
                            displayDwmRestarted = RestartDwm(deadline, displayCounters);
                    }
                    catch (Exception ex)
                    {
                        displayCounters.Skipped("display-worker", "exception");
                        Log.Error("Display recovery worker failed", ex);
                    }
                })
                {
                    IsBackground = true,
                    Name = "Thaw.DisplayRecovery",
                };
                TrySetThreadPriority(displayWorker, ThreadPriority.Highest, "Thaw.DisplayRecovery");
                displayWorker.Start();
            }
            else if (!allowDisplayRecovery)
            {
                counters.Skipped("gpu-reset", "profile-does-not-request-display-reset");
            }
            else if (!_config.ResetGpuDriver)
            {
                counters.Skipped("gpu-reset", "disabled");
            }

            Thread? shellWorker = null;
            var shellCounters = new RecoveryCounters();
            bool shellExplorerRestarted = false;
            if (forceAll && _config.RestartExplorerOnUnfreeze)
            {
                shellWorker = new Thread(() =>
                {
                    try
                    {
                        shellExplorerRestarted = RestartExplorer(deadline, shellCounters, force: true);
                        if (shellExplorerRestarted)
                        {
                            try { ExplorerRestartedNow?.Invoke(); }
                            catch (Exception ex) { Log.Debug("Explorer event failed: " + ex.Message); }
                        }
                    }
                    catch (Exception ex)
                    {
                        shellCounters.Skipped("explorer-worker", "exception");
                        Log.Error("Explorer force-all worker failed", ex);
                    }
                })
                {
                    IsBackground = true,
                    Name = "Thaw.ExplorerForceAll",
                };
                TrySetThreadPriority(shellWorker, ThreadPriority.Highest, "Thaw.ExplorerForceAll");
                shellWorker.Start();
            }

            Thread? powerWorker = null;
            var powerCounters = new RecoveryCounters();
            if (forceAll && _config.PowerPlanBoost)
            {
                powerWorker = new Thread(() =>
                {
                    if (BoostPowerPlan(_config.PowerPlanRestoreAfterSeconds))
                        powerCounters.Succeeded("power-plan", "high-performance");
                    else
                        powerCounters.Skipped("power-plan", "boost-failed");
                })
                {
                    IsBackground = true,
                    Name = "Thaw.PowerForceAll",
                };
                TrySetThreadPriority(powerWorker, ThreadPriority.Highest, "Thaw.PowerForceAll");
                powerWorker.Start();
            }

            if (reason == TriggerReason.Hotkey)
                Log.Warn($"Alt+F4 FORCE-ALL workers started in {sw.ElapsedMilliseconds} ms: display+dwm+system+memory+shell+power+network");

            // Target responsiveness and gather bounded process candidates.  Every
            // changed process priority is restored in the finally block.
            Process[] processes = Array.Empty<Process>();
            try { processes = Process.GetProcesses(); }
            catch (Exception ex)
            {
                counters.Skipped("process-snapshot", "unavailable");
                Log.Debug("Process snapshot unavailable: " + ex.Message);
            }

            try
            {
                // Complete snapshots are time-budgeted below; never discard a
                // process merely because it appears after an arbitrary prefix.
                int scanLimit = processes.Length;

                var trimCandidates = new List<(Process Process, long WorkingSet)>();
                int currentSession = TryGetCurrentSessionId();
                for (int i = 0; i < scanLimit; i++)
                {
                    Process p = processes[i];
                    try
                    {
                        if (!HasBudget(deadline, counters, "process-scan")) break;
                        counters.ProcessesScanned++;
                        string name = p.ProcessName;
                        int pid = p.Id;
                        if (pid == selfId || pid == 0 || pid == 4 || DoNotTrim.Contains(name))
                        {
                            counters.ProcessesSkipped++;
                            continue;
                        }

                        bool isForeground = (uint)pid == fgPid;
                        bool isShell = ShellApps.Contains(name);
                        if (allowSystemRecovery && (isForeground || isShell) && priorityRestores.Count < 8 &&
                            BoostTemporarily((uint)pid, priorityRestores))
                            counters.Succeeded(isForeground ? "foreground-priority" : "shell-priority", name);

                        // Trimming is pressure-gated and intentionally excludes the
                        // visible app, shell, protected services and other sessions.
                        if (!allowMemoryRecovery || !memoryPressure || isForeground || isShell ||
                            !IsSameInteractiveSession(p, currentSession))
                        {
                            if (memoryPressure) counters.ProcessesSkipped++;
                            continue;
                        }

                        long workingSet = 0;
                        try { workingSet = p.WorkingSet64; } catch { }
                        if (workingSet >= MinimumTrimWorkingSetBytes)
                            trimCandidates.Add((p, workingSet));
                        else
                            counters.ProcessesSkipped++;
                    }
                    catch (Exception ex)
                    {
                        counters.ProcessesSkipped++;
                        Log.Debug($"Process entry skipped: {ex.Message}");
                    }
                }

                if (memoryPressure && allowMemoryRecovery)
                {
                    foreach (var candidate in trimCandidates.OrderByDescending(x => x.WorkingSet).Take(MaxWorkingSetTrims))
                    {
                        if (!HasBudget(deadline, counters, "working-set")) break;
                        if (TrimWorkingSet(candidate.Process.Id))
                        {
                            counters.WorkingSetsTrimmed++;
                            counters.Succeeded("working-set", $"pid={candidate.Process.Id}");
                        }
                        else
                            counters.WorkingSetFailures++;
                    }
                    if (trimCandidates.Count > MaxWorkingSetTrims)
                        Log.Info($"Working-set trim capped at {MaxWorkingSetTrims} candidates");
                }
                else
                {
                    counters.Skipped("working-set", !allowMemoryRecovery ? "profile-safe-mode" : memoryReason);
                }
            }
            finally
            {
                foreach (var p in processes)
                {
                    try { p.Dispose(); } catch { }
                }
            }

            // Memory-cache tier is pressure-gated and admin-aware.  It is not run
            // on a healthy system, where flushing caches would create extra I/O.
            bool elevated = Native.IsElevated();
            if (memoryPressure && elevated && HasBudget(deadline, counters, "memory-cache"))
            {
                if (!allowMemoryRecovery)
                {
                    counters.Skipped("memory-cache", "profile-safe-mode");
                }
                else
                {
                    if (PurgeStandbyList()) counters.Succeeded("standby-purge");
                    if (HasBudget(deadline, counters, "modified-purge") && PurgeModifiedPageList())
                        counters.Succeeded("modified-purge");
                    if (HasBudget(deadline, counters, "file-cache") && FlushFileCache())
                        counters.Succeeded("file-cache");
                }
            }
            else if (!memoryPressure)
                counters.Skipped("memory-cache", memoryReason);
            else if (!elevated)
                counters.Skipped("memory-cache", "administrator-required");

            if (_config.PowerPlanBoost && !forceAll)
            {
                // The old implementation changed the active plan and restored it
                // later, which could leave persistent state behind if Thaw exited.
                // Keep the setting visible but refuse the non-reversible mutation.
                counters.Skipped("power-plan", "safety-policy-no-persistent-changes");
                Log.Info("Power-plan boost skipped by safety policy; active plan left unchanged");
            }

            // Shell refresh and DNS cache flush do not stop services or drop active
            // connections.  Audio is diagnosed but never restarted: doing that
            // would interrupt active playback/capture.
            if (allowSystemRecovery && (forceAll || counters.ExplorerHung) && HasBudget(deadline, counters, "shell-refresh"))
            {
                if (RefreshShellCaches()) counters.Succeeded("shell-refresh");
            }
            else
                counters.Skipped("shell-refresh", !allowSystemRecovery ? "profile-safe-mode" : counters.ExplorerHung ? "budget" : "shell-responsive");

            if (allowSystemRecovery && (forceAll || _watchdog.LastStallMs >= _config.StallThresholdMs || reason == TriggerReason.Panic) &&
                HasBudget(deadline, counters, "dns-refresh"))
            {
                if (FlushDnsCache(forceAll)) counters.Succeeded("dns-refresh");
            }
            else
                counters.Skipped("dns-refresh", "no-network-stall-signal");

            LogAudioDiagnostic(deadline, counters);

            // Restart only the exact interactive shell process, and only when it is
            // demonstrably hung.  Healthy Alt+F4 recovery never kills Explorer.
            if (shellWorker is null && allowSystemRecovery && _config.RestartExplorerOnUnfreeze && counters.ExplorerHung &&
                HasBudget(deadline, counters, "explorer-restart"))
            {
                explorerRestarted = RestartExplorer(deadline, counters, force: false);
                if (explorerRestarted)
                {
                    try { ExplorerRestartedNow?.Invoke(); }
                    catch (Exception ex) { Log.Debug("Explorer event failed: " + ex.Message); }
                }
            }
            else if (shellWorker is not null)
            {
                // The explicit Alt+F4 force-all worker owns this operation.
            }
            else if (!allowSystemRecovery)
                counters.Skipped("explorer-restart", "profile-safe-mode");
            else if (!_config.RestartExplorerOnUnfreeze)
                counters.Skipped("explorer-restart", "disabled");
            else
                counters.Skipped("explorer-restart", "shell-responsive");

            if (displayWorker is not null)
            {
                int waitMs = Math.Max(1, RemainingMs(deadline));
                if (displayWorker.Join(waitMs))
                {
                    gpuReset = displayGpuReset;
                    dwmRestarted = displayDwmRestarted;
                    MergeCounters(counters, displayCounters);
                }
                else
                {
                    counters.BudgetExpired = true;
                    counters.Skipped("display-worker", "completion-timeout");
                    Log.Warn("Display recovery worker exceeded the bounded recovery budget");
                }
            }

            if (shellWorker is not null)
            {
                int waitMs = Math.Max(1, RemainingMs(deadline));
                if (shellWorker.Join(waitMs))
                {
                    explorerRestarted = shellExplorerRestarted;
                    MergeCounters(counters, shellCounters);
                }
                else
                {
                    counters.BudgetExpired = true;
                    counters.Skipped("explorer-worker", "completion-timeout");
                }
            }

            if (powerWorker is not null)
            {
                int waitMs = Math.Max(1, RemainingMs(deadline));
                if (powerWorker.Join(waitMs))
                    MergeCounters(counters, powerCounters);
                else
                    counters.Skipped("power-worker", "completion-timeout");
            }

            ulong availAfter = Native.GetMemoryStatus().ullAvailPhys;
            long freedMb = availAfter > availBefore ? (long)((availAfter - availBefore) / (1024 * 1024)) : 0;
            var stats = new UnfreezeStats(counters.WorkingSetsTrimmed, priorityRestores.Count, freedMb,
                sw.ElapsedMilliseconds, reason, gpuReset, dwmRestarted, explorerRestarted)
            {
                ProcessesScanned = counters.ProcessesScanned,
                ProcessesSkipped = counters.ProcessesSkipped,
                WorkingSetsTrimmed = counters.WorkingSetsTrimmed,
                WorkingSetFailures = counters.WorkingSetFailures,
                ActionsSucceeded = counters.ActionsSucceeded,
                ActionsSkipped = counters.ActionsSkipped,
                BudgetExpired = counters.BudgetExpired,
                ForegroundPid = counters.ForegroundPid,
                Diagnostics = counters.DiagnosticSummary(),
            };
            Log.Info("Unfreeze done: " + stats.Summary);
            Log.Info($"Recovery details: scanned={stats.ProcessesScanned}, skipped={stats.ProcessesSkipped}, " +
                     $"trimmed={stats.WorkingSetsTrimmed}, trimFailures={stats.WorkingSetFailures}, " +
                     $"actionsOk={stats.ActionsSucceeded}, actionsSkipped={stats.ActionsSkipped}, " +
                     $"budgetExpired={stats.BudgetExpired}, diagnostics={stats.Diagnostics}");
            try { Completed?.Invoke(stats); } catch (Exception ex) { Log.Error("Completed handler error", ex); }
        }
        catch (Exception ex)
        {
            Log.Error("Unfreeze failed", ex);
        }
        finally
        {
            RestorePriorities(priorityRestores);
            if (oldThreadPriorityCaptured) { try { Thread.CurrentThread.Priority = oldThreadPriority; } catch { } }
            if (oldPriorityCaptured && processPriorityChanged)
            {
                try { Process.GetCurrentProcess().PriorityClass = oldPriority; } catch { }
            }
            Interlocked.Exchange(ref _active, 0);
        }
    }

    private IReadOnlyList<RecoveryActionSpec> BuildActionSpecs(
        TriggerReason reason,
        HealthCause cause,
        bool forceAll,
        bool memoryPressure,
        string memoryReason,
        uint foregroundPid,
        (bool Display, bool System, bool Memory) profile,
        IReadOnlyList<string> selected,
        RecoveryCounters counters,
        RollbackJournal journal,
        Func<RecoveryProbe> probe,
        Action markGpuReset,
        Action markDwmRestarted,
        Action markExplorerRestarted)
    {
        _lastProcessScanCount = 0;
        _lastProcessSkipCount = 0;
        _lastWorkingSetsTrimmed = 0;
        _lastWorkingSetFailures = 0;
        _lastAppsBoosted = 0;
        var actions = new List<RecoveryActionSpec>(RecoveryCoordinator.MaxActionWorkers);
        bool Wants(string name) =>
            (reason != TriggerReason.Auto ||
             name is "resource-diagnostics" or "foreground-probe") &&
            (forceAll || selected.Contains(name, StringComparer.OrdinalIgnoreCase));

        RecoveryActionSpec Add(
            string name,
            int timeoutMs,
            Func<RecoveryActionContext, RecoveryActionResult> execute,
            string? circuit = null,
            bool safeReversible = true,
            bool verifyWithGenericProbe = true,
            bool useCircuitBreaker = true)
        {
            var spec = new RecoveryActionSpec(name, timeoutMs, execute)
            {
                CircuitKey = circuit ?? name,
                SafeReversible = safeReversible,
                VerifyWithGenericProbe = verifyWithGenericProbe,
                UseCircuitBreaker = useCircuitBreaker,
                CaptureBefore = verifyWithGenericProbe ? _ => probe() : null,
                CaptureAfter = verifyWithGenericProbe ? _ => probe() : null,
            };
            actions.Add(spec);
            return spec;
        }

        if (profile.Display && _config.ResetGpuDriver && Wants("display-reset"))
        {
            Add("gpu-reset", 900, _ =>
            {
                var local = new RecoveryCounters();
                bool ok = ResetGpuDriver(local);
                if (ok) markGpuReset();
                return ok ? RecoveryActionResult.AcceptedRequest("SendInput accepted; display proof pending") :
                            RecoveryActionResult.SkippedResult("graphics reset request unavailable");
            }, "display-reset");
        }

        if (profile.Display && Wants("dwm-mmcss") &&
            (forceAll || reason is TriggerReason.Panic or TriggerReason.FrameDrop))
        {
            Add("dwm-mmcss", 350, _ =>
            {
                IDisposable? revertScope = null;
                if (!NativeDiagnostics.TryEnableDwmMmcss(out revertScope) || revertScope is null)
                    return RecoveryActionResult.SkippedResult("DWM MMCSS helper unavailable");

                try
                {
                    // DwmEnableMMCSS has no query API. The helper scope restores
                    // the documented default after this bounded recovery run.
                    journal.RecordResult("dwm-mmcss", () =>
                    {
                        try { revertScope.Dispose(); return true; }
                        catch { return false; }
                    });
                }
                catch
                {
                    try { revertScope.Dispose(); } catch { }
                    throw;
                }

                return RecoveryActionResult.AcceptedRequest(
                    "DWM MMCSS participation enabled; rollback journal armed");
            }, "dwm-mmcss");
        }

        if (profile.Display && _config.RestartDwmOnFrozenScreen &&
            (forceAll || reason == TriggerReason.Panic) && Wants("display-reset"))
        {
            Add("dwm-restart", 2_500, context =>
            {
                var local = new RecoveryCounters { DwmHung = counters.DwmHung };
                if (!forceAll && !IsScreenStillFrozen(context.RunDeadlineTick, local))
                    return RecoveryActionResult.SkippedResult("compositor probe did not confirm a freeze");
                bool ok = RestartDwm(context.RunDeadlineTick, local);
                if (ok) markDwmRestarted();
                return ok ? RecoveryActionResult.AcceptedRequest("exact compositor respawn observed") :
                            RecoveryActionResult.SkippedResult("compositor restart unavailable");
            }, "dwm-restart", safeReversible: false);
        }

        if (Wants("foreground-qos") && foregroundPid != 0 && foregroundPid != Environment.ProcessId)
        {
            Add("foreground-qos", 550, _ =>
            {
                if (RecoveryCoordinator.TryBoostProcessQoS(foregroundPid, journal))
                {
                    Interlocked.Increment(ref _lastAppsBoosted);
                    return RecoveryActionResult.AcceptedRequest($"power throttling cleared for PID {foregroundPid}");
                }

                if (TryBoostPriorityWithJournal(foregroundPid, journal))
                {
                    Interlocked.Increment(ref _lastAppsBoosted);
                    return RecoveryActionResult.AcceptedRequest($"temporary Above Normal priority for PID {foregroundPid}");
                }
                return RecoveryActionResult.SkippedResult("QoS and priority helpers unavailable");
            });
        }

        if (Wants("background-demotion"))
        {
            Add("background-demotion", 800, context =>
            {
                ProcessCandidate? offender = FindTopOffender(context, foregroundPid, memoryPressure);
                if (offender is null) return RecoveryActionResult.SkippedResult("no eligible background offender");
                if (!TryDemotePriorityWithJournal(offender.Pid, journal, out string detail))
                    return RecoveryActionResult.SkippedResult(detail);
                return RecoveryActionResult.AcceptedRequest(detail);
            });
        }

        if (profile.Memory && memoryPressure && Wants("working-set-trim"))
        {
            Add("working-set-trim", 3_000, context => RunOffenderAwareTrim(
                context, foregroundPid, memoryReason));
        }

        if (profile.Memory && memoryPressure && Wants("memory-cache"))
        {
            Add("memory-cache", 1_800, context =>
            {
                if (!Native.IsElevated()) return RecoveryActionResult.SkippedResult("administrator token required");
                bool changed = false;
                if (!context.IsExpired && PurgeStandbyList()) changed = true;
                if (!context.IsExpired && PurgeModifiedPageList()) changed = true;
                if (!context.IsExpired && FlushFileCache()) changed = true;
                return changed ? RecoveryActionResult.AcceptedRequest("memory cache requests accepted") :
                                  RecoveryActionResult.SkippedResult("memory cache helpers unavailable");
            });
        }

        if (Wants("shell-refresh") && profile.System)
        {
            Add("shell-refresh", 450, _ => RefreshShellCaches()
                ? RecoveryActionResult.AcceptedRequest("shell refresh notification sent")
                : RecoveryActionResult.SkippedResult("shell refresh helper unavailable"));
        }

        if (Wants("desktop-refresh") && profile.System)
        {
            Add("desktop-refresh", 500, context =>
            {
                if (context.IsExpired)
                    return RecoveryActionResult.SkippedResult("recovery budget expired before desktop refresh");
                bool ok = Native.TryBroadcastDesktopRefresh(out int delivered);
                return ok
                    ? RecoveryActionResult.AcceptedRequest($"bounded desktop refresh broadcast delivered={delivered}")
                    : RecoveryActionResult.SkippedResult("desktop refresh broadcast unavailable");
            });
        }

        if (Wants("dns-refresh") && profile.System &&
            (forceAll || reason == TriggerReason.Panic || _watchdog.LastStallMs >= _config.StallThresholdMs))
        {
            Add("dns-refresh", 700, _ => FlushDnsCache(forceAll)
                ? RecoveryActionResult.AcceptedRequest("resolver cache flush accepted")
                : RecoveryActionResult.SkippedResult("DNS helper unavailable or cooldown active"));
        }

        if (Wants("foreground-probe") && foregroundPid != 0)
        {
            Add("foreground-probe", 350, _ =>
            {
                IntPtr hwnd = Native.GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                    return RecoveryActionResult.SkippedResult("foreground window unavailable");
                Native.GetWindowThreadProcessId(hwnd, out uint ownerPid);
                if (ownerPid != foregroundPid)
                    return RecoveryActionResult.Unverified($"foreground changed to PID {ownerPid} before probe");

                bool completed = NativeDiagnostics.TryProbeWindow(
                    hwnd,
                    TimeSpan.FromMilliseconds(150),
                    out NativeDiagnostics.WindowMessageProbe probeResult);
                string detail = completed
                    ? $"WM_NULL completed in {probeResult.ElapsedMilliseconds} ms"
                    : probeResult.TimedOut
                        ? "WM_NULL timed out; foreground window is not responsive"
                        : $"WM_NULL unavailable/error={probeResult.ErrorCode}";
                Log.Info($"Foreground responsiveness diagnostic: PID {foregroundPid}; {detail}");
                return RecoveryActionResult.Unverified(detail);
            }, "foreground-probe", safeReversible: true,
                verifyWithGenericProbe: false, useCircuitBreaker: false);
        }

        if (Wants("resource-diagnostics"))
        {
            Add("resource-diagnostics", 750, _ =>
            {
                string detail = BuildResourceDiagnostic(foregroundPid);
                Log.Info("Recovery resource diagnostics: " + detail);
                return RecoveryActionResult.Unverified(detail);
            }, "resource-diagnostics", safeReversible: true,
                verifyWithGenericProbe: false, useCircuitBreaker: false);
        }

        if (forceAll && _config.PowerPlanBoost)
        {
            Add("power-plan", 1_800, _ => TryBoostPowerPlanWithJournal(journal)
                ? RecoveryActionResult.AcceptedRequest("High Performance selected; rollback journal armed")
                : RecoveryActionResult.SkippedResult("power plan helper unavailable"));
        }

        if (Wants("mmcss"))
        {
            Add("mmcss", 400, _ => RecoveryCoordinator.TryEnterMmcss("Games", journal)
                ? RecoveryActionResult.AcceptedRequest("MMCSS task entered; rollback journal armed")
                : RecoveryActionResult.SkippedResult("MMCSS helper unavailable"));
        }

        if (Wants("cpu-sets"))
        {
            Add("cpu-sets", 500, _ =>
            {
                // CPU-set APIs are optional.  Preserve the exact current set
                // list and only apply it when Windows exposes both calls; this
                // keeps the recovery worker schedulable on older builds.
                IntPtr self = Native.GetCurrentProcess();
                if (!RecoveryCoordinator.TryReadCpuSets(self, out uint[] existing) || existing.Length == 0)
                    return RecoveryActionResult.SkippedResult("CPU-set helper unavailable");
                return RecoveryCoordinator.TryApplyCpuSets(self, existing, journal)
                    ? RecoveryActionResult.AcceptedRequest("CPU-set helper accepted reversible set")
                    : RecoveryActionResult.SkippedResult("CPU-set mutation unavailable");
            });
        }

        if (forceAll && Wants("audio-diagnostic"))
        {
            Add("audio-diagnostic", 350, _ =>
            {
                bool present;
                try
                {
                    Process[] audio = Process.GetProcessesByName("audiodg");
                    present = audio.Length != 0;
                    foreach (Process process in audio) process.Dispose();
                }
                catch { return RecoveryActionResult.SkippedResult("audio diagnostic unavailable"); }
                Log.Info($"Audio responsiveness diagnostic: audiodgPresent={present}; restart not attempted");
                return RecoveryActionResult.Unverified("audio presence observed; restart prohibited");
            }, "audio-diagnostic", safeReversible: true,
                verifyWithGenericProbe: false, useCircuitBreaker: false);
        }

        if (forceAll)
        {
            Add("dwm-frame-diagnostic", 500, context =>
            {
                using var cancellation = new CancellationTokenSource(
                    Math.Max(50, (int)Math.Min(350, context.ActionDeadlineTick - Environment.TickCount64)));
                NativeDiagnostics.DwmProgressComparison frame = NativeDiagnostics.MeasureDwmProgressAsync(
                    TimeSpan.FromMilliseconds(120), IntPtr.Zero, cancellation.Token).GetAwaiter().GetResult();
                if (!frame.Supported)
                    return RecoveryActionResult.SkippedResult("DWM composition timing unavailable");
                string detail = $"composed={frame.CompositionFramesDelta},displayed={frame.FramesDisplayedDelta}," +
                                $"late={frame.LateFramesDelta},dropped={frame.FramesDroppedDelta}," +
                                $"missed={frame.FramesMissedDelta},vblank={frame.VBlankProgressed}," +
                                $"elapsed={frame.ElapsedMilliseconds}ms";
                if (frame.Cancelled) return RecoveryActionResult.Unverified("DWM timing cancelled; " + detail);
                if (frame.Worsened) return RecoveryActionResult.Worse(detail);
                return frame.Progressed ? RecoveryActionResult.Recovered(detail) : RecoveryActionResult.Unchanged(detail);
            }, "dwm-frame-diagnostic", safeReversible: true,
                verifyWithGenericProbe: false, useCircuitBreaker: false);

            Add("foreground-wait-chain", 650, context =>
            {
                IntPtr foregroundWindow = Native.GetForegroundWindow();
                uint threadId = foregroundWindow == IntPtr.Zero
                    ? 0
                    : Native.GetWindowThreadProcessId(foregroundWindow, out _);
                if (threadId == 0) return RecoveryActionResult.SkippedResult("foreground thread unavailable");
                using var cancellation = new CancellationTokenSource(
                    Math.Max(50, (int)Math.Min(500, context.ActionDeadlineTick - Environment.TickCount64)));
                NativeDiagnostics.WaitChainResult waitChain = NativeDiagnostics.GetWaitChainAsync(
                    threadId, TimeSpan.FromMilliseconds(400), false, cancellation.Token).GetAwaiter().GetResult();
                if (!waitChain.Supported) return RecoveryActionResult.SkippedResult("wait-chain API unavailable");
                if (waitChain.TimedOut || waitChain.Cancelled)
                    return RecoveryActionResult.Unverified("wait-chain query bounded timeout");
                return waitChain.Succeeded
                    ? RecoveryActionResult.Recovered($"nodes={waitChain.Nodes.Count},cycle={waitChain.IsCycle}")
                    : RecoveryActionResult.Unverified($"wait-chain error={waitChain.ErrorCode}");
            }, "foreground-wait-chain", safeReversible: true,
                verifyWithGenericProbe: false, useCircuitBreaker: false);

            Add("event-device-diagnostics", 900, context =>
            {
                using var cancellation = new CancellationTokenSource(
                    Math.Max(50, (int)Math.Min(700, context.ActionDeadlineTick - Environment.TickCount64)));
                IReadOnlyList<NativeDiagnostics.RecentEventCorrelation> events =
                    NativeDiagnostics.CorrelateRecentEvents(
                        TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(350),
                        cancellationToken: cancellation.Token);
                IReadOnlyList<NativeDiagnostics.DeviceProblem> deviceProblems =
                    cancellation.IsCancellationRequested
                        ? Array.Empty<NativeDiagnostics.DeviceProblem>()
                        : NativeDiagnostics.EnumerateDeviceProblems(
                            displayOnly: false,
                            includeHealthy: false,
                            budget: TimeSpan.FromMilliseconds(300),
                            cancellationToken: cancellation.Token);
                int relevantEvents = events.Count(item => item.Relevant);
                int displayProblems = deviceProblems.Count(item => item.IsDisplay);
                string codes = string.Join("/", deviceProblems
                    .Take(8)
                    .Select(item => item.ProblemCode.ToString()));
                return RecoveryActionResult.Unverified(
                    $"events={events.Count},relevant={relevantEvents},deviceProblems={deviceProblems.Count}," +
                    $"displayProblems={displayProblems},codes={(string.IsNullOrWhiteSpace(codes) ? "none" : codes)}");
            }, "event-device-diagnostics", safeReversible: true,
                verifyWithGenericProbe: false, useCircuitBreaker: false);
        }

        if (profile.System && _config.RestartExplorerOnUnfreeze &&
            (forceAll || counters.ExplorerHung) && Wants("shell-refresh"))
        {
            Add("explorer-restart", 4_000, context =>
            {
                var local = new RecoveryCounters();
                bool ok = RestartExplorer(context.RunDeadlineTick, local, forceAll);
                if (ok) markExplorerRestarted();
                return ok ? RecoveryActionResult.AcceptedRequest("exact interactive shell respawn observed") :
                            RecoveryActionResult.SkippedResult("interactive shell restart unavailable");
            }, "explorer-restart", safeReversible: false);
        }

        if (actions.Count == 0)
            counters.Skipped("recovery-actions", "cause-directed selector returned no safe action");
        return actions;
    }

    /// <summary>
    /// Converts the newest bounded telemetry sample into one compact incident
    /// receipt. This is deliberately read-only: storage, network, thermal,
    /// kernel, GPU, commit, and foreground deadlocks need different remedies,
    /// and a hotkey must not guess by mutating unrelated system components.
    /// </summary>
    private string BuildResourceDiagnostic(uint foregroundPid)
    {
        try
        {
            TelemetrySnapshot? sample = _watchdog.LatestTelemetry;
            if (sample is null)
            {
                return $"telemetry=unavailable,watchdogCause={_watchdog.Cause}," +
                       $"stall={_watchdog.LastStallMs}ms,cpu={_watchdog.CpuPercent:0}%," +
                       $"ram={_watchdog.MemPercent}%";
            }

            ForegroundProcessSnapshot? foreground = sample.ForegroundProcess;
            string foregroundText = foreground is { IsValid: true }
                ? $"fg={foreground.ProcessId}/{foreground.Name},responding=" +
                  (foreground.RespondingSampleValid ? foreground.Responding.ToString() : "n/a") +
                  $",fgCpu={FormatMetric(foreground.CpuPercent)}%,fgIo=" +
                  (foreground.IoSampleValid
                      ? $"r{foreground.ReadBytesPerSecond}/w{foreground.WriteBytesPerSecond}"
                      : "n/a")
                : $"fg={foregroundPid}/unavailable";

            return string.Join(",", new[]
            {
                $"cause={sample.Classification.Cause}",
                $"score={sample.Classification.Score:0.00}",
                $"reason={sample.Classification.Reason}",
                $"stall={sample.SchedulingDelayMilliseconds}ms",
                $"cpu={FormatMetric(sample.CpuPercent)}%",
                $"ram={sample.MemoryPercent}%",
                $"commit={FormatMetric(sample.CommitPercent)}%",
                $"pagefile={FormatMetric(sample.PageFilePercent)}%",
                $"disk={FormatMetric(sample.DiskLatencyMilliseconds)}ms/q{FormatMetric(sample.DiskQueueLength)}",
                $"queue={FormatMetric(sample.ProcessorQueueLength)}",
                $"dpc={FormatMetric(sample.DpcTimePercent)}%/isr={FormatMetric(sample.InterruptTimePercent)}%",
                $"net=err{FormatMetric(sample.NetworkErrorsPerSecond)}/drop{FormatMetric(sample.NetworkDiscardsPerSecond)}/rtx{FormatMetric(sample.NetworkRetransmitsPerSecond)}",
                $"gpu={FormatMetric(sample.GpuEngineUtilizationPercent)}%",
                $"frames=drop{sample.DwmFramesDroppedDelta}/miss{sample.DwmFramesMissedDelta}/late{sample.DwmFramesLateDelta}",
                $"thermal={FormatMetric(sample.ThermalCelsius)}C/freq={FormatMetric(sample.EffectiveFrequencyPercent)}%/limit={FormatMetric(sample.PowerLimitPercent)}%",
                $"lowmem={sample.LowMemorySignal}",
                $"counters={sample.CounterStatus.ValidCounters}/{sample.CounterStatus.RequestedCounters}",
                foregroundText,
            });
        }
        catch (Exception ex)
        {
            return "telemetry=error:" + ex.GetType().Name;
        }
    }

    private static RecoveryOutcome SummarizeOutcomes(IReadOnlyDictionary<string, RecoveryOutcome> outcomes)
    {
        if (outcomes.Values.Any(value => value == RecoveryOutcome.Worse)) return RecoveryOutcome.Worse;
        RecoveryOutcome[] actionable = outcomes
            .Where(pair => !EvidenceOnlyActions.Contains(pair.Key))
            .Select(pair => pair.Value)
            .ToArray();
        if (actionable.Any(value => value == RecoveryOutcome.Recovered)) return RecoveryOutcome.Recovered;
        if (actionable.Length > 0 && actionable.All(value => value == RecoveryOutcome.Unchanged))
            return RecoveryOutcome.Unchanged;
        return RecoveryOutcome.Unverified;
    }

    private static string FormatMetric(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? "n/a"
            : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record ProcessCandidate(uint Pid, string Name, long WorkingSet, double OffenderScore, int SessionId);

    private ProcessCandidate? FindTopOffender(RecoveryActionContext context, uint foregroundPid, bool memoryPressure)
    {
        ProcessCandidate? best = null;
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception ex)
        {
            Log.Debug("Offender snapshot unavailable: " + ex.Message);
            return null;
        }
        int currentSession = TryGetCurrentSessionId();
        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    Interlocked.Increment(ref _lastProcessScanCount);
                    if (context.IsExpired) break;
                    int pid = process.Id;
                    string name = process.ProcessName;
                    if (!IsEligibleOffender(process, pid, name, foregroundPid, currentSession))
                    {
                        Interlocked.Increment(ref _lastProcessSkipCount);
                        continue;
                    }
                    long workingSet = 0;
                    try { workingSet = process.WorkingSet64; } catch { }
                    if (workingSet < MinimumTrimWorkingSetBytes)
                    {
                        Interlocked.Increment(ref _lastProcessSkipCount);
                        continue;
                    }
                    double cpu = 0;
                    try { cpu = process.TotalProcessorTime.TotalMilliseconds; } catch { }
                    double score = workingSet / (double)(1024 * 1024) + cpu / 1000.0;
                    if (!memoryPressure) score = cpu / 1000.0;
                    var candidate = new ProcessCandidate((uint)pid, name, workingSet, score, process.SessionId);
                    if (best is null || candidate.OffenderScore > best.OffenderScore) best = candidate;
                }
                catch { Interlocked.Increment(ref _lastProcessSkipCount); }
            }
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
        return best;
    }

    private RecoveryActionResult RunOffenderAwareTrim(RecoveryActionContext context, uint foregroundPid, string memoryReason)
    {
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception ex)
        {
            Log.Debug("Working-set offender snapshot unavailable: " + ex.Message);
            return RecoveryActionResult.SkippedResult("process snapshot unavailable");
        }
        int currentSession = TryGetCurrentSessionId();
        var candidates = new List<ProcessCandidate>();
        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    Interlocked.Increment(ref _lastProcessScanCount);
                    if (context.IsExpired) break;
                    int pid = process.Id;
                    string name = process.ProcessName;
                    if (!IsEligibleOffender(process, pid, name, foregroundPid, currentSession))
                    {
                        Interlocked.Increment(ref _lastProcessSkipCount);
                        continue;
                    }
                    long workingSet = 0;
                    try { workingSet = process.WorkingSet64; } catch { }
                    if (workingSet < MinimumTrimWorkingSetBytes)
                    {
                        Interlocked.Increment(ref _lastProcessSkipCount);
                        continue;
                    }
                    double cpu = 0;
                    try { cpu = process.TotalProcessorTime.TotalMilliseconds; } catch { }
                    candidates.Add(new ProcessCandidate((uint)pid, name, workingSet,
                        workingSet / (double)(1024 * 1024) + cpu / 1000.0, process.SessionId));
                }
                catch { Interlocked.Increment(ref _lastProcessSkipCount); }
            }
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }

        int trimmed = 0;
        foreach (ProcessCandidate candidate in candidates.OrderByDescending(item => item.OffenderScore))
        {
            if (context.IsExpired || trimmed >= MaxWorkingSetTrims) break;
            if (TrimWorkingSet((int)candidate.Pid))
            {
                trimmed++;
                Interlocked.Increment(ref _lastWorkingSetsTrimmed);
            }
            else Interlocked.Increment(ref _lastWorkingSetFailures);
        }
        if (trimmed > 0)
            return RecoveryActionResult.AcceptedRequest($"trimmed {trimmed} offender(s); reason={memoryReason}");
        return candidates.Count == 0
            ? RecoveryActionResult.SkippedResult("no eligible background offender")
            : RecoveryActionResult.Unchanged("eligible offenders found but trim was denied");
    }

    private static bool IsEligibleOffender(Process process, int pid, string name, uint foregroundPid, int currentSession)
    {
        if (pid == Environment.ProcessId || pid == 0 || pid == 4 || (uint)pid == foregroundPid) return false;
        if (DoNotTrim.Contains(name) || ShellApps.Contains(name)) return false;
        if (string.Equals(name, "audiodg", StringComparison.OrdinalIgnoreCase)) return false;
        try { if (currentSession < 0 || process.SessionId != currentSession) return false; } catch { return false; }
        return true;
    }

    private static bool TryBoostPriorityWithJournal(uint pid, RollbackJournal journal)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;
            uint previous = Native.GetPriorityClass(handle);
            if (previous == 0 || previous == Native.REALTIME_PRIORITY_CLASS || previous == Native.HIGH_PRIORITY_CLASS)
                return false;
            if (!Native.SetPriorityClass(handle, Native.ABOVE_NORMAL_PRIORITY_CLASS)) return false;
            IntPtr captured = handle;
            journal.RecordResult("priority-pid-" + pid, () =>
            {
                try { return Native.SetPriorityClass(captured, previous); }
                finally { Native.CloseHandle(captured); }
            });
            handle = IntPtr.Zero;
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"Foreground priority helper unavailable for PID {pid}: {ex.Message}");
            return false;
        }
        finally { if (handle != IntPtr.Zero) Native.CloseHandle(handle); }
    }

    private static bool TryDemotePriorityWithJournal(uint pid, RollbackJournal journal, out string detail)
    {
        detail = "background offender not demoted";
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;
            uint previous = Native.GetPriorityClass(handle);
            if (previous == 0 || previous == Native.REALTIME_PRIORITY_CLASS || previous == Native.HIGH_PRIORITY_CLASS)
            {
                detail = "background offender priority is protected";
                return false;
            }
            const uint BelowNormalPriorityClass = 0x00004000;
            if (!Native.SetPriorityClass(handle, BelowNormalPriorityClass)) return false;
            IntPtr captured = handle;
            journal.RecordResult("priority-demotion-pid-" + pid, () =>
            {
                try { return Native.SetPriorityClass(captured, previous); }
                finally { Native.CloseHandle(captured); }
            });
            handle = IntPtr.Zero;
            detail = $"background offender PID {pid} demoted temporarily";
            return true;
        }
        catch (Exception ex)
        {
            detail = "background demotion unavailable: " + ex.GetType().Name;
            return false;
        }
        finally { if (handle != IntPtr.Zero) Native.CloseHandle(handle); }
    }

    private static bool TryBoostPowerPlanWithJournal(RollbackJournal journal)
    {
        string? previous = TryGetActivePowerScheme();
        if (string.IsNullOrWhiteSpace(previous)) return false;
        if (!RunPowerCfg(out string output, "/setactive", "SCHEME_MIN"))
        {
            Log.Debug("Power-plan boost unavailable: " + output);
            return false;
        }
        journal.RecordResult("power-plan", () =>
        {
            if (!RunPowerCfg(out string restoreOutput, "/setactive", previous))
            {
                Log.Debug("Power-plan rollback unavailable: " + restoreOutput);
                return false;
            }
            return true;
        });
        Log.Info("Power plan force-all boost accepted; exact rollback journal armed");
        return true;
    }

    private bool TryEnter()
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        return Interlocked.CompareExchange(ref _active, 1, 0) == 0;
    }

    private static bool HasBudget(long deadline, RecoveryCounters counters, string tier)
    {
        if (Environment.TickCount64 < deadline) return true;
        if (!counters.BudgetExpired)
            Log.Warn($"Recovery budget reached before {tier}; remaining tiers skipped");
        counters.BudgetExpired = true;
        return false;
    }

    private static void MergeCounters(RecoveryCounters target, RecoveryCounters source)
    {
        target.ActionsSucceeded += source.ActionsSucceeded;
        target.ActionsSkipped += source.ActionsSkipped;
        target.BudgetExpired |= source.BudgetExpired;
        target.DwmHung = source.DwmHung;
        target.ActionResults.AddRange(source.ActionResults);
    }

    private static bool TrimWorkingSet(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_SET_QUOTA, false, (uint)pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            return Native.EmptyWorkingSet(h);
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    private static bool BoostTemporarily(uint pid, List<PriorityRestore> restores)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            uint previous = Native.GetPriorityClass(h);
            if (previous != Native.NORMAL_PRIORITY_CLASS)
            {
                Native.CloseHandle(h);
                h = IntPtr.Zero;
                return false;
            }

            if (!Native.SetPriorityClass(h, Native.ABOVE_NORMAL_PRIORITY_CLASS))
            {
                Native.CloseHandle(h);
                h = IntPtr.Zero;
                return false;
            }

            restores.Add(new PriorityRestore
            {
                Handle = h,
                Pid = pid,
                PreviousClass = previous,
                Name = TryGetProcessName(pid),
            });
            h = IntPtr.Zero; // ownership transferred to the restore list
            return true;
        }
        catch (Exception ex)
        {
            if (h != IntPtr.Zero)
            {
                try { Native.CloseHandle(h); } catch { }
            }
            Log.Debug($"Priority boost skipped for PID {pid}: {ex.Message}");
            return false;
        }
    }

    private static void RestorePriorities(List<PriorityRestore> restores)
    {
        for (int i = restores.Count - 1; i >= 0; i--)
        {
            PriorityRestore restore = restores[i];
            try
            {
                bool ok = Native.SetPriorityClass(restore.Handle, restore.PreviousClass);
                Log.Debug($"Priority restored for {restore.Name} PID {restore.Pid}: {ok}");
            }
            catch (Exception ex)
            {
                Log.Debug($"Priority restore skipped for PID {restore.Pid}: {ex.Message}");
            }
            finally
            {
                try { Native.CloseHandle(restore.Handle); } catch { }
            }
        }
        restores.Clear();
    }

    private bool IsMemoryPressure(Native.MEMORYSTATUSEX memory, out string reason)
    {
        int configured = Math.Clamp(_config.MemStressPercent, 80, 99);
        ulong lowMemoryCutoff = Math.Max((ulong)MinimumAvailableMemoryBytes,
            memory.ullTotalPhys > 0 ? memory.ullTotalPhys / 10 : (ulong)MinimumAvailableMemoryBytes);
        bool highLoad = memory.dwMemoryLoad >= configured;
        bool lowAvailable = memory.ullAvailPhys > 0 && memory.ullAvailPhys <= lowMemoryCutoff;
        if (highLoad || lowAvailable)
        {
            reason = highLoad
                ? $"pressure(load={memory.dwMemoryLoad}% >= {configured}%)"
                : $"pressure(available={memory.ullAvailPhys / (1024 * 1024)} MB)";
            return true;
        }

        reason = $"not-pressure(load={memory.dwMemoryLoad}%, available={memory.ullAvailPhys / (1024 * 1024)} MB)";
        return false;
    }

    private static string TryGetProcessName(uint pid)
    {
        if (pid == 0) return "none";
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return "exited-or-denied"; }
    }

    private static bool IsForegroundHung(uint foregroundPid, bool logFailures = true)
    {
        try
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == foregroundPid && Native.IsHungAppWindow(hwnd);
        }
        catch (Exception ex)
        {
            if (logFailures) Log.Debug("Foreground responsiveness probe failed: " + ex.Message);
            return false;
        }
    }

    private static int TryGetCurrentSessionId()
    {
        try { return Process.GetCurrentProcess().SessionId; }
        catch { return -1; }
    }

    private static bool IsSameInteractiveSession(Process process, int currentSession)
    {
        if (currentSession < 0) return false;
        try { return process.SessionId == currentSession; }
        catch { return false; }
    }

    private static bool IsExplorerHung(bool logFailures = true)
    {
        var explorerPids = new HashSet<uint>();
        int activeSession;
        try { activeSession = unchecked((int)Native.WTSGetActiveConsoleSessionId()); }
        catch (Exception ex)
        {
            if (logFailures) Log.Debug("Explorer session probe unavailable: " + ex.Message);
            return false;
        }
        try
        {
            foreach (var process in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    if (activeSession < 0 || process.SessionId == activeSession)
                        explorerPids.Add((uint)process.Id);
                }
                catch { }
                finally { process.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            if (logFailures) Log.Debug("Explorer responsiveness probe unavailable: " + ex.Message);
            return false;
        }

        if (explorerPids.Count == 0) return true; // missing shell is actionable
        bool foundVisible = false;
        bool hung = false;
        try
        {
            Native.EnumWindows((hwnd, _) =>
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (!explorerPids.Contains(pid)) return true;
                foundVisible = true;
                if (Native.IsHungAppWindow(hwnd)) { hung = true; return false; }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            if (logFailures) Log.Debug("Explorer window probe failed: " + ex.Message);
        }
        if (logFailures)
            Log.Debug($"Explorer check: processes={explorerPids.Count}, visible={foundVisible}, hung={hung}");
        return hung;
    }

    /// <summary>
    /// True when the screen still appears frozen ~1 s after the GPU reset:
    /// the desktop compositor window is unresponsive (IsHungAppWindow) or the
    /// watchdog's latest sample still shows a scheduling stall.
    /// </summary>
    private bool IsScreenStillFrozen(long deadline, RecoveryCounters counters)
    {
        int delay = (int)Math.Clamp(deadline - Environment.TickCount64, 0, DisplayProbeDelayMs);
        if (delay > 0) Thread.Sleep(delay); // let a driver reset settle, but stay bounded
        try
        {
            bool hung = IsDwmHung();
            counters.DwmHung = hung;
            // IsHungAppWindow is a hint and can be stale.  Require the watchdog's
            // independent scheduler-stall signal before considering DWM termination.
            if (hung && _watchdog.LastStallMs >= _config.StallThresholdMs)
            {
                Log.Info("DWM compositor window is not responding — screen still frozen");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("DWM responsiveness check failed: " + ex.Message);
        }

        Log.Info($"Display probe: dwmHung={counters.DwmHung}, stall={_watchdog.LastStallMs} ms; no compositor termination");
        return false;
    }

    /// <summary>
    /// True if the desktop compositor's window is unresponsive. Tries both known DWM
    /// window classes (pre- and post-Win11), then falls back to enumerating top-level
    /// windows owned by the dwm process.
    /// </summary>
    private static bool IsDwmHung(bool logDetails = true)
    {
        if (!TryGetValidatedDwmProcess(out Process? compositor) || compositor is null)
            return false;

        using Process p = compositor;
        uint dwmPid = (uint)p.Id;

        IntPtr wnd = Native.FindWindow("DwmNotificationWindow", null);
        if (wnd == IntPtr.Zero) wnd = Native.FindWindow("Dwm", null);
        if (wnd != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(wnd, out uint ownerPid);
            bool ownedByDwm = ownerPid == dwmPid;
            bool directHung = ownedByDwm && Native.IsHungAppWindow(wnd);
            if (logDetails)
                Log.Debug($"DWM check: hwnd=0x{wnd.ToInt64():X}, pid={ownerPid}, owned={ownedByDwm}, hung={directHung}");
            return directHung;
        }

        // Fallback: any top-level window owned by the dwm process.
        bool found = false, hung = false;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == dwmPid)
            {
                found = true;
                if (Native.IsHungAppWindow(h)) { hung = true; return false; }
            }
            return true;
        }, IntPtr.Zero);
        if (logDetails) Log.Debug($"DWM check (enum): found={found} hung={hung}");
        return found && hung;
    }

    private static bool TryGetDwmHungForSelection()
    {
        try { return IsDwmHung(logDetails: false); }
        catch { return false; }
    }

    /// <summary>
    /// Resolves only the current-session compositor whose executable is the
    /// Windows System32 dwm.exe. Process.GetProcessesByName alone is not a
    /// sufficient termination target because names can be spoofed and other
    /// sessions can contain a compositor too.
    /// </summary>
    private static bool TryGetValidatedDwmProcess(out Process? process)
    {
        process = null;
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDirectory))
            windowsDirectory = Environment.GetEnvironmentVariable("WINDIR") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(windowsDirectory)) return false;

        string expectedPath;
        try { expectedPath = Path.GetFullPath(Path.Combine(windowsDirectory, "System32", "dwm.exe")); }
        catch { return false; }

        int currentSession;
        try
        {
            using Process current = Process.GetCurrentProcess();
            currentSession = current.SessionId;
        }
        catch { return false; }

        Process[] candidates;
        try { candidates = Process.GetProcessesByName("dwm"); }
        catch { return false; }

        foreach (Process candidate in candidates)
        {
            bool keep = false;
            try
            {
                if (candidate.SessionId == currentSession)
                {
                    string? imagePath = candidate.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(imagePath) &&
                        string.Equals(Path.GetFullPath(imagePath), expectedPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        process = candidate;
                        keep = true;
                    }
                }
            }
            catch { }

            if (keep) break;
            try { candidate.Dispose(); } catch { }
        }

        if (process is not null)
        {
            foreach (Process candidate in candidates)
            {
                if (!ReferenceEquals(process, candidate))
                {
                    try { candidate.Dispose(); } catch { }
                }
            }
        }
        return process is not null;
    }

    /// <summary>
    /// Restarts the desktop compositor: terminates dwm.exe and lets Windows respawn it.
    /// The screen goes black for ~1–2 s, then the compositor comes back fresh.
    /// </summary>
    private static bool RestartDwm(long deadline, RecoveryCounters counters)
    {
        if (!Native.IsElevated())
        {
            counters.Skipped("dwm-restart", "administrator-required");
            Log.Info("DWM restart skipped: an elevated token is required");
            return false;
        }

        Process? target = null;
        try
        {
            // Only target a validated current-session System32 compositor, and
            // at most one PID. No name-based broad kill or process-tree
            // termination is permitted.
            if (!TryGetValidatedDwmProcess(out target) || target is null)
            {
                counters.Skipped("dwm-restart", "validated-process-not-found");
                return false;
            }
            int pid = target.Id;
            Log.Warn($"User-requested DWM escalation: terminating exact dwm PID {pid}");
            target.Kill(entireProcessTree: false);
            bool exited = target.WaitForExit(Math.Min(1_500, RemainingMs(deadline)));
            if (!exited)
            {
                Log.Warn($"dwm PID {pid} did not exit within bounded wait");
                counters.Skipped("dwm-restart", "exit-timeout");
                return false;
            }

            int pollMs = Math.Min(2_500, RemainingMs(deadline));
            long end = Environment.TickCount64 + Math.Max(0, pollMs);
            while (Environment.TickCount64 < end)
            {
                try
                {
                    if (TryGetValidatedDwmProcess(out Process? replacementCandidate) &&
                        replacementCandidate is not null)
                    {
                        using Process replacement = replacementCandidate;
                        if (replacement.Id != pid)
                        {
                            Log.Info($"validated dwm.exe respawned after terminating PID {pid}; " +
                                     $"replacement PID {replacement.Id}");
                            counters.Succeeded("dwm-restart", $"pid={pid},replacement={replacement.Id}");
                            return true;
                        }
                    }
                }
                catch { }
                Thread.Sleep(Math.Min(100, Math.Max(1, RemainingMs(end))));
            }
            Log.Warn($"dwm PID {pid} exited but no respawn was observed before the recovery deadline");
            counters.Skipped("dwm-restart", "respawn-not-observed");
            return false;
        }
        catch (Exception ex)
        {
            counters.Skipped("dwm-restart", "failed");
            Log.Warn("DWM restart failed: " + ex.Message);
            return false;
        }
        finally
        {
            target?.Dispose();
        }
    }

    /// <summary>Public entry for the tray menu "Restart Explorer" item.</summary>
    public void RestartExplorerNow()
    {
        if (!TryEnter())
        {
            Log.Debug("Recovery already running, skipping duplicate Explorer restart");
            return;
        }

        try
        {
            var t = new Thread(() =>
            {
                var counters = new RecoveryCounters();
                long deadline = Environment.TickCount64 + 7_000;
                try
                {
                    if (RestartExplorer(deadline, counters, force: true))
                    {
                        Log.Info("Manual Explorer restart returned success");
                    }
                    Log.Info($"Manual Explorer restart complete: ok={counters.ActionsSucceeded}, skipped={counters.ActionsSkipped}");
                }
                catch (Exception ex) { Log.Error("Manual Explorer restart failed", ex); }
                finally
                {
                    try { ExplorerRestartedNow?.Invoke(); } catch (Exception ex) { Log.Debug("Explorer event failed: " + ex.Message); }
                    Interlocked.Exchange(ref _active, 0);
                }
            })
            { IsBackground = true, Name = "Thaw.ExplorerRestart" };
            TrySetThreadPriority(t, ThreadPriority.Highest, "Thaw.ExplorerRestart");
            t.Start();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _active, 0);
            Log.Error("Unable to start Explorer restart worker", ex);
        }
    }

    /// <summary>Ctrl+Shift+Win+B — the documented Windows graphics-driver reset.</summary>
    private static bool ResetGpuDriver(RecoveryCounters counters)
    {
        bool ok = TryEmergencyDisplayReset();
        if (ok) counters.Succeeded("gpu-reset", "accepted=8");
        else counters.Skipped("gpu-reset", "graphics reset request unavailable");
        return ok;
    }

    /// <summary>
    /// Last-resort, allocation-light display recovery for the rescue helper. It
    /// deliberately bypasses the full coordinator so a resource-starved helper
    /// can still submit the documented graphics reset.
    /// </summary>
    internal static bool TryEmergencyDisplayReset()
    {
        try
        {
            // Submit the whole sequence in one call so a partial failure cannot
            // leave Ctrl/Shift/Win logically held down.  The return count is the
            // only success signal; merely calling SendInput is not proof of reset.
            var inputs = new[]
            {
                Key(0x11, up: false), Key(0x10, up: false), Key(0x5B, up: false), Key(0x42, up: false),
                Key(0x42, up: true), Key(0x5B, up: true), Key(0x10, up: true), Key(0x11, up: true),
            };
            uint accepted = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
            bool ok = accepted == (uint)inputs.Length;
            Log.Info($"GPU driver reset request: requested={inputs.Length}, accepted={accepted}, success={ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("GPU reset failed", ex);
            return false;
        }
    }

    private static Native.INPUT Key(ushort vk, bool up)
    {
        var input = new Native.INPUT { type = 1 };
        input.U.ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u };
        return input;
    }

    private bool PurgeModifiedPageList()
    {
        try
        {
            if (!Native.EnablePrivilege("SeProfileSingleProcessPrivilege"))
            {
                Log.Debug("Modified purge unavailable: SeProfileSingleProcessPrivilege not assigned");
                return false;
            }
            IntPtr ptr = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(ptr, 2); // MemoryPurgeModifiedPageList
                int status = Native.NtSetSystemInformation(Native.SystemMemoryListInformation, ptr, 4);
                Log.Info($"Modified page list purge: NTSTATUS 0x{status:X8}");
                return status == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Modified purge unavailable: " + ex.Message);
            return false;
        }
    }

    private bool PurgeStandbyList()
    {
        try
        {
            if (!Native.EnablePrivilege("SeProfileSingleProcessPrivilege"))
            {
                Log.Debug("Standby purge unavailable: SeProfileSingleProcessPrivilege not assigned");
                return false;
            }
            IntPtr ptr = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(ptr, Native.MemoryPurgeStandbyList);
                int status = Native.NtSetSystemInformation(Native.SystemMemoryListInformation, ptr, 4);
                Log.Info($"Standby list purge: NTSTATUS 0x{status:X8}");
                return status == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Standby purge unavailable: " + ex.Message);
            return false;
        }
    }

    private bool FlushFileCache()
    {
        try
        {
            if (!Native.EnablePrivilege("SeIncreaseQuotaPrivilege"))
            {
                Log.Debug("File cache flush unavailable: SeIncreaseQuotaPrivilege not assigned");
                return false;
            }
            bool ok = Native.SetSystemFileCacheSize((IntPtr)(-1L), (IntPtr)(-1L), 0);
            Log.Info($"System file cache flush: {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Debug("File cache flush unavailable: " + ex.Message);
            return false;
        }
    }

    private static bool RefreshShellCaches()
    {
        try
        {
            // SHChangeNotify asks the existing shell to refresh association/icon
            // state.  It does not stop Explorer or disturb open windows.
            Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, Native.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            Log.Info("Shell association/icon refresh notification sent");
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("Shell cache refresh unavailable: " + ex.Message);
            return false;
        }
    }

    private static bool BoostPowerPlan(int restoreAfterSeconds)
    {
        string? current = TryGetActivePowerScheme();
        lock (PowerGate)
        {
            if (_powerRestoreScheme is null && !string.IsNullOrWhiteSpace(current))
                _powerRestoreScheme = current;
        }

        bool ok = RunPowerCfg(out string output, "/setactive", "SCHEME_MIN");
        Log.Info($"Power plan force-all boost -> High Performance: ok={ok}, output={output}");
        if (!ok) return false;

        if (restoreAfterSeconds > 0)
        {
            int dueMs = (int)Math.Min(int.MaxValue, Math.Max(1L, restoreAfterSeconds) * 1000L);
            lock (PowerGate)
            {
                _powerRestoreTimer ??= new System.Threading.Timer(RestorePowerPlan, null,
                    Timeout.Infinite, Timeout.Infinite);
                _powerRestoreTimer.Change(dueMs, Timeout.Infinite);
            }
            Log.Info($"Power plan restore scheduled in {restoreAfterSeconds} seconds");
        }
        return true;
    }

    private static void RestorePowerPlan(object? _)
    {
        string? scheme;
        lock (PowerGate)
        {
            scheme = _powerRestoreScheme;
            _powerRestoreScheme = null;
            _powerRestoreTimer?.Dispose();
            _powerRestoreTimer = null;
        }

        if (string.IsNullOrWhiteSpace(scheme)) return;
        bool ok = RunPowerCfg(out string output, "/setactive", scheme);
        Log.Info($"Power plan restored: scheme={scheme}, ok={ok}, output={output}");
    }

    private static string? TryGetActivePowerScheme()
    {
        if (!RunPowerCfg(out string output, "/getactivescheme")) return null;
        Match match = Regex.Match(output,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return match.Success ? match.Value : null;
    }

    private static bool RunPowerCfg(out string output, params string[] arguments)
    {
        output = "";
        try
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "powercfg.exe");
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in arguments) psi.ArgumentList.Add(argument);
            using Process? process = Process.Start(psi);
            if (process is null) return false;
            if (!process.WaitForExit(1_000))
            {
                try { process.Kill(entireProcessTree: false); } catch { }
                output = "timeout";
                return false;
            }
            output = (process.StandardOutput.ReadToEnd() + " " + process.StandardError.ReadToEnd()).Trim();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            output = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool FlushDnsCache(bool force = false)
    {
        long now = Environment.TickCount64;
        long previous = Volatile.Read(ref _lastDnsFlushTick);
        if (!force && now - previous < DnsFlushCooldownMs)
        {
            Log.Debug("DNS cache refresh skipped: cooldown active");
            return false;
        }
        if (force)
            Interlocked.Exchange(ref _lastDnsFlushTick, now);
        else if (Interlocked.CompareExchange(ref _lastDnsFlushTick, now, previous) != previous)
            return false;

        try
        {
            bool ok = Native.DnsFlushResolverCache();
            Log.Info($"DNS resolver cache refresh: {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Debug("DNS cache refresh unavailable: " + ex.Message);
            return false;
        }
    }

    private static void LogAudioDiagnostic(long deadline, RecoveryCounters counters)
    {
        if (!HasBudget(deadline, counters, "audio-diagnostic")) return;
        try
        {
            var audio = Process.GetProcessesByName("audiodg");
            bool present = audio.Any();
            foreach (var process in audio) process.Dispose();
            // Restarting audiodg or AudioEndpointBuilder would interrupt active
            // streams, so this tier is deliberately diagnostic-only.
            Log.Info($"Audio responsiveness diagnostic: audiodgPresent={present}; service restart not attempted");
            counters.ActionResults.Add("audio-diagnostic=observed(" + (present ? "present" : "absent") + ")");
        }
        catch (Exception ex)
        {
            Log.Debug("Audio diagnostic unavailable: " + ex.Message);
        }
    }

    private bool RestartExplorer(long deadline, RecoveryCounters counters, bool force)
    {
        uint shellPid = 0;
        try
        {
            IntPtr shellWindow = Native.GetShellWindow();
            if (shellWindow != IntPtr.Zero)
                Native.GetWindowThreadProcessId(shellWindow, out shellPid);
        }
        catch (Exception ex)
        {
            Log.Debug("Shell PID lookup failed: " + ex.Message);
        }

        if (shellPid == 0)
        {
            counters.Skipped("explorer-restart", "exact-shell-pid-unavailable");
            Log.Warn("Explorer restart skipped: exact shell window PID was unavailable");
            return false;
        }

        int targetSession = unchecked((int)Native.WTSGetActiveConsoleSessionId());
        if (targetSession < 0 || targetSession == -1)
        {
            counters.Skipped("explorer-restart", "no-active-console-session");
            return false;
        }

        using Process? shell = TryGetProcess((int)shellPid);
        if (shell is null || !string.Equals(shell.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            counters.Skipped("explorer-restart", "shell-pid-not-explorer");
            return false;
        }
        try
        {
            if (shell.SessionId != targetSession)
            {
                counters.Skipped("explorer-restart", "session-mismatch");
                return false;
            }
        }
        catch
        {
            counters.Skipped("explorer-restart", "session-unavailable");
            return false;
        }

        if (!force && !IsExplorerHung())
        {
            counters.Skipped("explorer-restart", "shell-responsive");
            return false;
        }

        try
        {
            Log.Warn($"Restarting exact interactive explorer PID {shellPid} (force={force})");
            shell.Kill(entireProcessTree: false);
            bool exited = shell.WaitForExit(Math.Min(1_200, RemainingMs(deadline)));
            if (!exited)
            {
                counters.Skipped("explorer-restart", "exit-timeout");
                Log.Warn($"explorer PID {shellPid} did not exit within the bounded wait");
                return false;
            }

            bool launched = TryStartExplorerAsInteractiveUser();
            if (!launched && !Native.IsElevated())
                launched = TryStartExplorerAsCurrentUserFallback();
            if (!launched)
            {
                // Never launch an elevated shell as a fallback.  A missing token
                // is safer than silently changing the user's integrity boundary.
                counters.Skipped("explorer-restart", "interactive-token-unavailable");
                return false;
            }

            int pollMs = Math.Min(2_500, RemainingMs(deadline));
            long end = Environment.TickCount64 + Math.Max(0, pollMs);
            while (Environment.TickCount64 < end)
            {
                try
                {
                    IntPtr shellWindow = Native.GetShellWindow();
                    if (shellWindow != IntPtr.Zero)
                    {
                        Native.GetWindowThreadProcessId(shellWindow, out uint newPid);
                        if (newPid != 0)
                        {
                            Log.Info($"Explorer shell respawn observed: PID {newPid}");
                            counters.Succeeded("explorer-restart", $"oldPid={shellPid},newPid={newPid}");
                            return true;
                        }
                    }
                }
                catch { }
                Thread.Sleep(Math.Min(100, Math.Max(1, RemainingMs(end))));
            }
            counters.Skipped("explorer-restart", "respawn-not-observed");
            return false;
        }
        catch (Exception ex)
        {
            counters.Skipped("explorer-restart", "failed");
            Log.Warn("Explorer restart failed: " + ex.Message);
            return false;
        }
    }

    private static Process? TryGetProcess(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch { return null; }
    }

    private static bool TryStartExplorerAsCurrentUserFallback()
    {
        try
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
            });
            bool started = process is not null;
            Log.Info($"explorer current-user fallback: {started}");
            return started;
        }
        catch (Exception ex)
        {
            Log.Debug("explorer current-user fallback unavailable: " + ex.Message);
            return false;
        }
    }

    /// <summary>Starts explorer under the interactive user's token (normal integrity).</summary>
    private static bool TryStartExplorerAsInteractiveUser()
    {
        try
        {
            uint sessionId = Native.WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF) return false;
            if (!Native.WTSQueryUserToken(sessionId, out IntPtr token))
            {
                Log.Debug("WTSQueryUserToken failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return false;
            }

            try
            {
                var si = new Native.STARTUPINFO
                {
                    cb = Marshal.SizeOf<Native.STARTUPINFO>(),
                    lpDesktop = "winsta0\\default",
                };
                string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                bool ok = Native.CreateProcessAsUser(token, exe, null, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref si, out Native.PROCESS_INFORMATION processInfo);
                if (!ok)
                    Log.Debug("CreateProcessAsUser failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                else
                {
                    // The new process owns these handles; close our inherited
                    // copies immediately to avoid leaking one pair per recovery.
                    Native.CloseHandle(processInfo.hProcess);
                    Native.CloseHandle(processInfo.hThread);
                    Log.Info("explorer started as interactive user");
                }
                return ok;
            }
            finally
            {
                Native.CloseHandle(token);
            }
        }
        catch (Exception ex)
        {
            Log.Error("explorer WTS start failed", ex);
            return false;
        }
    }

    private static int RemainingMs(long deadline)
    {
        long remaining = deadline - Environment.TickCount64;
        return (int)Math.Clamp(remaining, 0, int.MaxValue);
    }

    private static int EncodeReason(TriggerReason reason) => (int)reason + 1;

    private static void TrySetThreadPriority(Thread thread, ThreadPriority priority, string name)
    {
        try { thread.Priority = priority; }
        catch (Exception ex) { Log.Debug($"{name} priority unavailable: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _pendingReason, 0);
        try { _recoveryWake.Set(); } catch { }

        Thread? worker = Volatile.Read(ref _recoveryWorker);
        if (worker is not null && worker != Thread.CurrentThread)
        {
            try { worker.Join(2000); } catch { }
        }
        bool workerStopped = worker is null || !worker.IsAlive;
        if (!workerStopped)
            Log.Debug("Recovery request worker did not stop within 2 s; preserving its wake handle");

        try { _coordinator.Dispose(); } catch (Exception ex) { Log.Debug("Recovery coordinator dispose failed: " + ex.Message); }
        if (workerStopped)
        {
            try { _recoveryWake.Dispose(); } catch { }
        }
    }
}
