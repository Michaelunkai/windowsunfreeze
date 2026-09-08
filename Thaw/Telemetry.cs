using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Thaw;

/// <summary>Best-effort telemetry cause. A value is only reported when its evidence is available.</summary>
public enum TelemetryCause
{
    Healthy,
    SchedulerStall,
    CpuPressure,
    MemoryPressure,
    CommitPressure,
    DiskPressure,
    CpuQueuePressure,
    KernelLatency,
    NetworkPressure,
    GpuPressure,
    DisplayFrameDrop,
    ThermalPressure,
    ForegroundProcessPressure,
    LowMemory,
    MixedResourcePressure,
    Unknown,
}

/// <summary>Availability summary for optional PDH and Win32 probes.</summary>
public readonly record struct TelemetryCounterStatus(
    bool PdhAvailable,
    bool UsingFallback,
    int ValidCounters,
    int RequestedCounters,
    long QueryElapsedMilliseconds)
{
    public bool HasData => ValidCounters > 0;
}

/// <summary>Bounded process evidence for the foreground application.</summary>
public sealed class ForegroundProcessSnapshot
{
    public uint ProcessId { get; internal init; }
    public string Name { get; internal init; } = "unknown";
    public bool IsValid { get; internal init; }
    public bool Responding { get; internal init; }
    public bool RespondingSampleValid { get; internal init; }
    public double CpuPercent { get; internal init; } = double.NaN;
    public double KernelCpuPercent { get; internal init; } = double.NaN;
    public double UserCpuPercent { get; internal init; } = double.NaN;
    public long WorkingSetBytes { get; internal init; }
    public long PrivateBytes { get; internal init; }
    public long VirtualBytes { get; internal init; }
    public int HandleCount { get; internal init; } = -1;
    public int ThreadCount { get; internal init; } = -1;
    public long ReadBytesPerSecond { get; internal init; } = -1;
    public long WriteBytesPerSecond { get; internal init; } = -1;
    public long OtherBytesPerSecond { get; internal init; } = -1;
    public long ReadOperationsPerSecond { get; internal init; } = -1;
    public long WriteOperationsPerSecond { get; internal init; } = -1;
    public long OtherOperationsPerSecond { get; internal init; } = -1;
    public bool IoSampleValid { get; internal init; }
    public bool MemorySampleValid { get; internal init; }
    public bool HandleSampleValid { get; internal init; }
    public bool ThreadSampleValid { get; internal init; }
}

/// <summary>Immutable classifier result associated with one telemetry sample.</summary>
public sealed class TelemetryClassification
{
    public TelemetryCause Cause { get; internal init; }
    public double Score { get; internal init; }
    public string Reason { get; internal init; } = "health evidence unavailable";
    public bool EvidenceAvailable { get; internal init; }
}

/// <summary>
/// One bounded, once-per-second health sample. Optional values use NaN when a
/// counter is unavailable; zero is never used to mean that an unavailable
/// counter was healthy.
/// </summary>
public sealed class TelemetrySnapshot
{
    public DateTimeOffset TimestampUtc { get; internal init; }
    public long TimestampTick { get; internal init; }

    // Direct watchdog/system probes.
    public long SchedulingDelayMilliseconds { get; internal init; }
    public double CpuPercent { get; internal init; } = double.NaN;
    public uint MemoryPercent { get; internal init; }
    public ulong PhysicalMemoryTotalBytes { get; internal init; }
    public ulong PhysicalMemoryAvailableBytes { get; internal init; }
    public bool SchedulerSampleValid { get; internal init; }
    public bool CpuSampleValid { get; internal init; }
    public bool MemorySampleValid { get; internal init; }

    // Commit/pagefile and low-memory evidence.
    public ulong CommitLimitBytes { get; internal init; }
    public ulong CommitAvailableBytes { get; internal init; }
    public double CommitPercent { get; internal init; } = double.NaN;
    public ulong PageFileTotalBytes { get; internal init; }
    public ulong PageFileAvailableBytes { get; internal init; }
    public double PageFilePercent { get; internal init; } = double.NaN;
    public bool CommitSampleValid { get; internal init; }
    public bool LowMemorySignal { get; internal init; }
    public bool LowMemorySampleValid { get; internal init; }

    // Optional PDH probes. Values remain NaN when the counter is absent or
    // localized/unsupported; the classifier then uses the next available fact.
    public double DiskLatencyMilliseconds { get; internal init; } = double.NaN;
    public double DiskQueueLength { get; internal init; } = double.NaN;
    public double ProcessorQueueLength { get; internal init; } = double.NaN;
    public double DpcTimePercent { get; internal init; } = double.NaN;
    public double InterruptTimePercent { get; internal init; } = double.NaN;
    /// <summary>Alias for interrupt/ISR time used by integrations that name the kernel path explicitly.</summary>
    public double IsrTimePercent => InterruptTimePercent;
    public double InterruptsPerSecond { get; internal init; } = double.NaN;
    public double NetworkErrorsPerSecond { get; internal init; } = double.NaN;
    public double NetworkDiscardsPerSecond { get; internal init; } = double.NaN;
    public double NetworkRetransmitsPerSecond { get; internal init; } = double.NaN;
    public double GpuEngineUtilizationPercent { get; internal init; } = double.NaN;
    public double DisplayRefreshHz { get; internal init; } = double.NaN;
    public double CompositionRateHz { get; internal init; } = double.NaN;
    public double EstimatedFrameTimeMilliseconds { get; internal init; } = double.NaN;
    public ulong DwmFramesDisplayedDelta { get; internal init; }
    public ulong DwmFramesDroppedDelta { get; internal init; }
    public ulong DwmFramesMissedDelta { get; internal init; }
    public ulong DwmCompositionFramesDelta { get; internal init; }
    public ulong DwmFramesLateDelta { get; internal init; }
    public bool FrameTimingSampleValid { get; internal init; }
    public double ThermalCelsius { get; internal init; } = double.NaN;
    public double EffectiveFrequencyPercent { get; internal init; } = double.NaN;
    public double PowerLimitPercent { get; internal init; } = double.NaN;
    public double PagefilePressurePercent => PageFilePercent;
    public bool QoSDegraded { get; internal init; }
    public bool QoSSampleValid { get; internal init; }

    public ForegroundProcessSnapshot? ForegroundProcess { get; internal init; }
    public TelemetryCounterStatus CounterStatus { get; internal init; }
    public TelemetryClassification Classification { get; internal set; } = new();

    public bool HasOptionalCounterData => CounterStatus.HasData;
    public bool UsingFallbackCounters => CounterStatus.UsingFallback;
}

/// <summary>Pre/post evidence for one hard-stall or user-triggered recovery episode.</summary>
public sealed class TelemetryIncident
{
    internal TelemetryIncident(
        long id,
        DateTimeOffset startedAtUtc,
        TelemetrySnapshot? trigger,
        TelemetrySnapshot[] before)
    {
        Id = id;
        StartedAtUtc = startedAtUtc;
        TriggerSnapshot = trigger;
        Before = before;
    }

    public long Id { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; internal set; }
    public TelemetrySnapshot? TriggerSnapshot { get; }
    public IReadOnlyList<TelemetrySnapshot> Before { get; }
    public TelemetrySnapshot? BeforeSnapshot => Before.Count == 0 ? TriggerSnapshot : Before[Before.Count - 1];
    public TelemetrySnapshot? AfterSnapshot { get; internal set; }
    public IReadOnlyList<TelemetrySnapshot> After { get; internal set; } = Array.Empty<TelemetrySnapshot>();
    public bool IsComplete => AfterSnapshot is not null;

    public TelemetryCause Cause => TriggerSnapshot?.Classification.Cause ?? TelemetryCause.Unknown;
}

/// <summary>Sampling limits. Values are clamped by <see cref="Telemetry"/>.</summary>
public sealed class TelemetryOptions
{
    public int RingCapacity { get; init; } = 120;
    public int SamplePeriodMilliseconds { get; init; } = 1000;
    public int SampleBudgetMilliseconds { get; init; } = 150;
    public int BeforeSampleCount { get; init; } = 30;
    public int AfterSampleCount { get; init; } = 10;
}

/// <summary>
/// Fixed-size, thread-safe incident history. The writer holds the lock only
/// long enough to replace one slot; readers may receive an empty result when a
/// write is in progress rather than waiting behind telemetry.
/// </summary>
public sealed class IncidentRingBuffer
{
    private readonly object _gate = new();
    private readonly TelemetrySnapshot?[] _slots;
    private int _next;
    private int _count;

    public IncidentRingBuffer(int capacity = 120)
    {
        capacity = Math.Clamp(capacity, 8, 300);
        _slots = new TelemetrySnapshot?[capacity];
    }

    public int Capacity => _slots.Length;

    public int Count
    {
        get
        {
            if (!Monitor.TryEnter(_gate)) return 0;
            try { return _count; }
            finally { Monitor.Exit(_gate); }
        }
    }

    public TelemetrySnapshot? Latest
    {
        get
        {
            if (!Monitor.TryEnter(_gate)) return null;
            try
            {
                if (_count == 0) return null;
                int index = (_next - 1 + _slots.Length) % _slots.Length;
                return _slots[index];
            }
            finally { Monitor.Exit(_gate); }
        }
    }

    /// <summary>Returns chronological samples, or an empty array if a write is active.</summary>
    public TelemetrySnapshot[] GetRecent(int maximum = 0)
    {
        if (!TryGetRecent(maximum, out TelemetrySnapshot[] result)) return Array.Empty<TelemetrySnapshot>();
        return result;
    }

    public bool TryGetRecent(int maximum, out TelemetrySnapshot[] result)
    {
        result = Array.Empty<TelemetrySnapshot>();
        if (!Monitor.TryEnter(_gate)) return false;
        try
        {
            int count = _count;
            if (maximum > 0) count = Math.Min(count, maximum);
            if (count == 0) return true;

            var copy = new TelemetrySnapshot[count];
            int first = (_next - count + _slots.Length) % _slots.Length;
            for (int i = 0; i < count; i++)
                copy[i] = _slots[(first + i) % _slots.Length]!;
            result = copy;
            return true;
        }
        finally { Monitor.Exit(_gate); }
    }

    internal void Add(TelemetrySnapshot snapshot)
    {
        lock (_gate)
        {
            _slots[_next] = snapshot;
            _next = (_next + 1) % _slots.Length;
            if (_count < _slots.Length) _count++;
        }
    }
}

/// <summary>
/// Best-effort, bounded health telemetry. The worker is intentionally
/// separate from the high-priority watchdog thread: PDH, process and network
/// APIs can be unavailable or unexpectedly slow on a damaged system without
/// changing the existing stall detector or its recovery gate.
/// </summary>
public sealed class Telemetry : IDisposable
{
    private const int DefaultCapacity = 120;
    private const int MaxCounterProbeMilliseconds = 500;
    private const int LowMemoryResourceNotification = 0;

    private readonly TelemetryOptions _options;
    private readonly IncidentRingBuffer _ring;
    private readonly PdhCollector _pdh;
    private readonly object _incidentGate = new();
    private IntPtr _lowMemoryNotification;
    private Thread? _thread;
    private volatile bool _stop;
    private int _started;
    private long _incidentSequence;
    private TelemetryIncident? _activeIncident;
    private TelemetryIncident? _lastIncident;
    private int _healthyTelemetrySamples;
    private long _lastFailureLogTick;

    // Inputs from Watchdog. Individual volatile values avoid allocation and
    // locks on the high-priority sampling path.
    private long _watchdogTick;
    private long _watchdogDelay;
    private long _watchdogCpuBits;
    private int _watchdogMemory;
    private int _watchdogCpuValid;
    private int _watchdogMemoryValid;

    // Foreground process deltas are intentionally one-entry: process churn
    // cannot grow a dictionary or retain old process identities.
    private uint _previousForegroundPid;
    private long _previousForegroundTick;
    private long _previousForegroundCpuTicks;
    private long _previousForegroundKernelTicks;
    private long _previousForegroundUserTicks;
    private ProcessIoCounters _previousForegroundIo;
    private bool _previousForegroundIoValid;
    private NativeDiagnostics.DwmTimingSample? _previousDwmTiming;

    public Telemetry(TelemetryOptions? options = null)
    {
        _options = NormalizeOptions(options ?? new TelemetryOptions());
        _ring = new IncidentRingBuffer(_options.RingCapacity);
        _pdh = new PdhCollector();
        _lowMemoryNotification = TryCreateLowMemoryNotification();
    }

    public IncidentRingBuffer Incidents => _ring;
    public TelemetrySnapshot? LatestSnapshot => _ring.Latest;
    public TelemetryIncident? ActiveIncident
    {
        get { lock (_incidentGate) return _activeIncident; }
    }
    public TelemetryIncident? LastIncident
    {
        get { lock (_incidentGate) return _lastIncident; }
    }

    /// <summary>Starts at most one low-priority sampler; safe to call repeatedly.</summary>
    public void Start()
    {
        if (_stop || Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        try
        {
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "Thaw.Telemetry",
            };
            try { _thread.Priority = ThreadPriority.BelowNormal; }
            catch (Exception ex) { Log.Debug("Telemetry thread priority unavailable: " + ex.Message); }
            _thread.Start();
        }
        catch
        {
            _thread = null;
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    /// <summary>
    /// Publishes the watchdog's already-collected values without taking a
    /// lock, allocating, or invoking native APIs. This is the only call made
    /// on the high-priority watchdog loop.
    /// </summary>
    internal void PublishWatchdogSample(
        long timestampTick,
        long schedulingDelayMilliseconds,
        double cpuPercent,
        uint memoryPercent,
        bool cpuSampleValid,
        bool memorySampleValid)
    {
        Volatile.Write(ref _watchdogTick, timestampTick);
        Volatile.Write(ref _watchdogDelay, schedulingDelayMilliseconds);
        Volatile.Write(ref _watchdogCpuBits, BitConverter.DoubleToInt64Bits(cpuPercent));
        Volatile.Write(ref _watchdogMemory, unchecked((int)memoryPercent));
        Volatile.Write(ref _watchdogCpuValid, cpuSampleValid ? 1 : 0);
        Volatile.Write(ref _watchdogMemoryValid, memorySampleValid ? 1 : 0);
        // A sequence is not needed by the sampler: it runs at the same
        // bounded cadence and always consumes the newest primitive values.
    }

    /// <summary>Captures bounded pre-trigger evidence and opens an incident.</summary>
    public TelemetryIncident BeginIncident(string? reason = null)
    {
        lock (_incidentGate)
        {
            if (_activeIncident is not null) return _activeIncident;
            _healthyTelemetrySamples = 0;
        }

        TelemetrySnapshot[] before = _ring.GetRecent(_options.BeforeSampleCount);
        TelemetrySnapshot? trigger = LatestSnapshot;
        var incident = new TelemetryIncident(
            Interlocked.Increment(ref _incidentSequence),
            DateTimeOffset.UtcNow,
            trigger,
            before);

        lock (_incidentGate)
        {
            if (_activeIncident is not null) return _activeIncident;
            _activeIncident = incident;
        }

        if (!string.IsNullOrWhiteSpace(reason))
            Log.Debug($"Telemetry incident started: {reason}");
        return incident;
    }

    /// <summary>
    /// Completes the active incident with a nonblocking copy of post-trigger
    /// samples. Calling it when no incident is open is harmless.
    /// </summary>
    public bool CompleteIncident(string? reason = null)
    {
        lock (_incidentGate)
        {
            if (_activeIncident is null) return false;

            TelemetryIncident incident = _activeIncident;
            incident.After = _ring.GetRecent(_options.AfterSampleCount);
            incident.AfterSnapshot = LatestSnapshot;
            incident.CompletedAtUtc = DateTimeOffset.UtcNow;
            _lastIncident = incident;
            _activeIncident = null;

            if (!string.IsNullOrWhiteSpace(reason))
                Log.Debug($"Telemetry incident completed: {reason}");
            return true;
        }
    }

    /// <summary>Alias used by recovery integrations that call the result a post-snapshot.</summary>
    public bool CaptureAfterSnapshot(string? reason = null) => CompleteIncident(reason);

    private void Loop()
    {
        long nextTick = Environment.TickCount64;
        while (!_stop)
        {
            long started = Environment.TickCount64;
            try
            {
                TelemetrySnapshot sample = Sample(started);
                _ring.Add(sample);

                // A stable recovery is diagnostic only. It never changes the
                // watchdog's stress flag or hard-stall gate.
                if (sample.Classification.Cause == TelemetryCause.Healthy && sample.Classification.EvidenceAvailable)
                {
                    _healthyTelemetrySamples = Math.Min(8, _healthyTelemetrySamples + 1);
                    if (_healthyTelemetrySamples >= 3)
                        CompleteIncident("healthy sample");
                }
                else
                {
                    _healthyTelemetrySamples = 0;
                }
            }
            catch (Exception ex)
            {
                // A telemetry failure must not stop the watchdog. Keep one
                // concise diagnostic and continue on the next tick.
                long now = Environment.TickCount64;
                long last = Volatile.Read(ref _lastFailureLogTick);
                if (last == 0 || now - last >= 10_000)
                {
                    Volatile.Write(ref _lastFailureLogTick, now);
                    Log.Debug("Telemetry sample unavailable: " + ex.Message);
                }
            }

            nextTick += _options.SamplePeriodMilliseconds;
            long wait = nextTick - Environment.TickCount64;
            if (wait <= 0)
            {
                // Preserve the 1 Hz bound even if an optional provider ran
                // over budget; never spin a damaged counter provider.
                nextTick = Environment.TickCount64 + _options.SamplePeriodMilliseconds;
                wait = _options.SamplePeriodMilliseconds;
            }
            Thread.Sleep((int)Math.Min(wait, _options.SamplePeriodMilliseconds));

            // A defensive budget guard is useful if a vendor PDH provider
            // returns slowly. The sample is retained, but the worker never
            // loops faster than the configured cadence.
            _ = Math.Max(0, Environment.TickCount64 - started);
        }
    }

    private TelemetrySnapshot Sample(long sampleTick)
    {
        long budgetDeadline = sampleTick + _options.SampleBudgetMilliseconds;
        long watchdogTick = Volatile.Read(ref _watchdogTick);
        long watchdogDelay = Volatile.Read(ref _watchdogDelay);
        double watchdogCpu = BitConverter.Int64BitsToDouble(Volatile.Read(ref _watchdogCpuBits));
        uint watchdogMemory = unchecked((uint)Volatile.Read(ref _watchdogMemory));
        bool cpuValid = Volatile.Read(ref _watchdogCpuValid) != 0;
        bool memoryValid = Volatile.Read(ref _watchdogMemoryValid) != 0;

        Native.MEMORYSTATUSEX memory = Native.GetMemoryStatus();
        bool memoryStatusValid = memory.dwMemoryLoad is >= 1 and <= 100;
        if (!memoryValid && memoryStatusValid)
        {
            watchdogMemory = memory.dwMemoryLoad;
            memoryValid = true;
        }

        ulong commitLimit = memory.ullTotalPageFile;
        ulong commitAvailable = memory.ullAvailPageFile;
        bool commitValid = commitLimit > 0 && commitAvailable <= commitLimit;
        double commitPercent = commitValid
            ? Math.Clamp(100.0 * (1.0 - ((double)commitAvailable / commitLimit)), 0, 100)
            : double.NaN;
        ulong pageFileTotal = memory.ullTotalPageFile > memory.ullTotalPhys
            ? memory.ullTotalPageFile - memory.ullTotalPhys
            : 0;
        ulong pageFileAvailable = memory.ullAvailPageFile > memory.ullAvailPhys
            ? memory.ullAvailPageFile - memory.ullAvailPhys
            : 0;
        double pageFilePercent = pageFileTotal > 0 && pageFileAvailable <= pageFileTotal
            ? Math.Clamp(100.0 * (1.0 - ((double)pageFileAvailable / pageFileTotal)), 0, 100)
            : double.NaN;

        bool lowMemory = false;
        bool lowMemoryValid = false;
        if (_lowMemoryNotification != IntPtr.Zero)
        {
            lowMemoryValid = TryQueryLowMemoryNotification(_lowMemoryNotification, out lowMemory);
        }
        if (memoryStatusValid && memory.dwMemoryLoad >= 90)
        {
            lowMemory = true;
            lowMemoryValid = true;
        }

        RawCounters counters = default;
        long queryStarted = Environment.TickCount64;
        _pdh.TryCollect(ref counters);
        long queryElapsed = Math.Max(0, Environment.TickCount64 - queryStarted);
        if (queryElapsed > MaxCounterProbeMilliseconds)
            counters.MarkUnavailableAfterBudget();

        if (Environment.TickCount64 <= budgetDeadline)
            AddManagedNetworkFallback(ref counters, sampleTick);
        ForegroundProcessSnapshot? foreground = Environment.TickCount64 <= budgetDeadline
            ? TryCaptureForeground(sampleTick)
            : null;

        if (foreground is not null && Environment.TickCount64 <= budgetDeadline)
        {
            try
            {
                if (NativeDiagnostics.TryGetProcessPowerThrottling(
                        foreground.ProcessId,
                        out NativeDiagnostics.ProcessPowerThrottlingSnapshot qos))
                {
                    counters.QoSSampleValid = qos.Supported;
                    counters.QoSDegraded = qos.Supported &&
                        (qos.ControlMask & Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0 &&
                        (qos.StateMask & Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0;
                }
            }
            catch
            {
                // Optional QoS evidence must never delay or fail watchdog sampling.
            }
        }

        NativeDiagnostics.DwmTimingSample? dwmTiming = null;
        NativeDiagnostics.DwmProgressComparison? dwmProgress = null;
        if (Environment.TickCount64 <= budgetDeadline)
        {
            try
            {
                if (NativeDiagnostics.TryGetDwmTiming(out NativeDiagnostics.DwmTimingSample currentDwm))
                {
                    dwmTiming = currentDwm;
                    NativeDiagnostics.DwmTimingSample? previousDwm = _previousDwmTiming;
                    if (previousDwm is not null)
                    {
                        long elapsed = Math.Max(1,
                            (long)(currentDwm.CapturedAtUtc - previousDwm.CapturedAtUtc).TotalMilliseconds);
                        dwmProgress = NativeDiagnostics.CompareDwmProgress(previousDwm, currentDwm, elapsed);
                    }
                    _previousDwmTiming = currentDwm;
                }
            }
            catch
            {
                // Optional composition evidence must never delay watchdog sampling.
            }
        }

        ulong frameProgress = dwmProgress is null
            ? 0
            : Math.Max(dwmProgress.FramesDisplayedDelta, dwmProgress.CompositionFramesDelta);
        double estimatedFrameTime = dwmProgress is { Supported: true } && frameProgress > 0
            ? dwmProgress.ElapsedMilliseconds / (double)frameProgress
            : double.NaN;

        var sample = new TelemetrySnapshot
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            TimestampTick = sampleTick,
            SchedulingDelayMilliseconds = watchdogTick > 0 ? Math.Max(0, watchdogDelay) : 0,
            CpuPercent = cpuValid ? Math.Clamp(watchdogCpu, 0, 100) : double.NaN,
            MemoryPercent = watchdogMemory,
            PhysicalMemoryTotalBytes = memory.ullTotalPhys,
            PhysicalMemoryAvailableBytes = memory.ullAvailPhys,
            SchedulerSampleValid = watchdogTick > 0,
            CpuSampleValid = cpuValid,
            MemorySampleValid = memoryValid,
            CommitLimitBytes = commitLimit,
            CommitAvailableBytes = commitAvailable,
            CommitPercent = commitPercent,
            PageFileTotalBytes = pageFileTotal,
            PageFileAvailableBytes = pageFileAvailable,
            PageFilePercent = pageFilePercent,
            CommitSampleValid = commitValid,
            LowMemorySignal = lowMemory,
            LowMemorySampleValid = lowMemoryValid,
            DiskLatencyMilliseconds = counters.DiskLatencyMilliseconds,
            DiskQueueLength = counters.DiskQueueLength,
            ProcessorQueueLength = counters.ProcessorQueueLength,
            DpcTimePercent = counters.DpcTimePercent,
            InterruptTimePercent = counters.InterruptTimePercent,
            InterruptsPerSecond = counters.InterruptsPerSecond,
            NetworkErrorsPerSecond = counters.NetworkErrorsPerSecond,
            NetworkDiscardsPerSecond = counters.NetworkDiscardsPerSecond,
            NetworkRetransmitsPerSecond = counters.NetworkRetransmitsPerSecond,
            GpuEngineUtilizationPercent = counters.GpuEngineUtilizationPercent,
            DisplayRefreshHz = dwmTiming?.RefreshRateHz ?? double.NaN,
            CompositionRateHz = dwmTiming?.ComposeRateHz ?? double.NaN,
            EstimatedFrameTimeMilliseconds = estimatedFrameTime,
            DwmFramesDisplayedDelta = dwmProgress?.FramesDisplayedDelta ?? 0,
            DwmFramesDroppedDelta = dwmProgress?.FramesDroppedDelta ?? 0,
            DwmFramesMissedDelta = dwmProgress?.FramesMissedDelta ?? 0,
            DwmCompositionFramesDelta = dwmProgress?.CompositionFramesDelta ?? 0,
            DwmFramesLateDelta = dwmProgress?.LateFramesDelta ?? 0,
            FrameTimingSampleValid = dwmProgress?.Supported == true,
            ThermalCelsius = counters.ThermalCelsius,
            EffectiveFrequencyPercent = counters.EffectiveFrequencyPercent,
            PowerLimitPercent = counters.PowerLimitPercent,
            QoSDegraded = counters.QoSDegraded ||
                (!double.IsNaN(counters.PowerLimitPercent) && counters.PowerLimitPercent < 80),
            QoSSampleValid = counters.QoSSampleValid || !double.IsNaN(counters.PowerLimitPercent),
            ForegroundProcess = foreground,
            CounterStatus = new TelemetryCounterStatus(
                _pdh.IsAvailable,
                counters.UsingFallback || !_pdh.IsAvailable || counters.ValidCount < PdhCollector.CounterCount,
                counters.ValidCount,
                PdhCollector.CounterCount,
                queryElapsed),
        };
        sample.Classification = Classify(sample);
        return sample;
    }

    private TelemetryClassification Classify(TelemetrySnapshot sample)
    {
        double best = 0;
        TelemetryCause cause = TelemetryCause.Healthy;
        string reason = "healthy";
        bool evidence = false;

        void Consider(TelemetryCause candidate, double score, string text)
        {
            if (double.IsNaN(score) || score <= 0) return;
            evidence = true;
            if (score > best)
            {
                best = Math.Clamp(score, 0, 1);
                cause = candidate;
                reason = text;
            }
        }

        if (sample.SchedulingDelayMilliseconds >= 800)
            Consider(TelemetryCause.SchedulerStall, Math.Clamp(sample.SchedulingDelayMilliseconds / 8000.0, 0, 1), "scheduler delay");
        if (sample.CpuSampleValid && sample.CpuPercent >= 90)
            Consider(TelemetryCause.CpuPressure, Math.Clamp((sample.CpuPercent - 75) / 25.0, 0.2, 1), "high CPU");
        if (sample.MemorySampleValid && sample.MemoryPercent >= 90)
            Consider(TelemetryCause.MemoryPressure, Math.Clamp((sample.MemoryPercent - 75) / 25.0, 0.2, 1), "high physical memory");
        if (sample.CommitSampleValid && sample.CommitPercent >= 90)
            Consider(TelemetryCause.CommitPressure, Math.Clamp((sample.CommitPercent - 80) / 20.0, 0.2, 1), "commit/pagefile pressure");
        if (sample.LowMemorySignal)
            Consider(TelemetryCause.LowMemory, 0.85, "low-memory notification");
        if (!double.IsNaN(sample.DiskLatencyMilliseconds) && sample.DiskLatencyMilliseconds >= 50)
            Consider(TelemetryCause.DiskPressure, Math.Clamp(sample.DiskLatencyMilliseconds / 500.0, 0.2, 1), "disk latency");
        if (!double.IsNaN(sample.DiskQueueLength) && sample.DiskQueueLength >= 2)
            Consider(TelemetryCause.DiskPressure, Math.Clamp(sample.DiskQueueLength / 16.0, 0.2, 1), "disk queue");
        if (!double.IsNaN(sample.ProcessorQueueLength) && sample.ProcessorQueueLength >= Math.Max(2, Environment.ProcessorCount * 2))
            Consider(TelemetryCause.CpuQueuePressure, Math.Clamp(sample.ProcessorQueueLength / Math.Max(4.0, Environment.ProcessorCount * 8.0), 0.2, 1), "processor queue");
        if (!double.IsNaN(sample.DpcTimePercent) && sample.DpcTimePercent >= 10)
            Consider(TelemetryCause.KernelLatency, Math.Clamp(sample.DpcTimePercent / 50.0, 0.2, 1), "DPC time");
        if (!double.IsNaN(sample.InterruptTimePercent) && sample.InterruptTimePercent >= 10)
            Consider(TelemetryCause.KernelLatency, Math.Clamp(sample.InterruptTimePercent / 50.0, 0.2, 1), "interrupt time");
        if (!double.IsNaN(sample.InterruptsPerSecond) && sample.InterruptsPerSecond >= 5000)
            Consider(TelemetryCause.KernelLatency, Math.Clamp(sample.InterruptsPerSecond / 50000.0, 0.2, 1), "interrupt rate");
        if (!double.IsNaN(sample.NetworkErrorsPerSecond) && sample.NetworkErrorsPerSecond > 0)
            Consider(TelemetryCause.NetworkPressure, Math.Clamp(sample.NetworkErrorsPerSecond / 100.0, 0.2, 1), "network errors");
        if (!double.IsNaN(sample.NetworkRetransmitsPerSecond) && sample.NetworkRetransmitsPerSecond > 1)
            Consider(TelemetryCause.NetworkPressure, Math.Clamp(sample.NetworkRetransmitsPerSecond / 1000.0, 0.2, 1), "network retransmits");
        if (!double.IsNaN(sample.NetworkDiscardsPerSecond) && sample.NetworkDiscardsPerSecond > 0)
            Consider(TelemetryCause.NetworkPressure, Math.Clamp(sample.NetworkDiscardsPerSecond / 100.0, 0.2, 1), "network discards");
        if (!double.IsNaN(sample.GpuEngineUtilizationPercent) && sample.GpuEngineUtilizationPercent >= 90)
            Consider(TelemetryCause.GpuPressure, Math.Clamp((sample.GpuEngineUtilizationPercent - 75) / 25.0, 0.2, 1), "GPU engine utilization");
        if (sample.FrameTimingSampleValid &&
            (sample.DwmFramesDroppedDelta > 0 || sample.DwmFramesMissedDelta > 0 || sample.DwmFramesLateDelta > 0))
        {
            double dropped = sample.DwmFramesDroppedDelta + sample.DwmFramesMissedDelta + sample.DwmFramesLateDelta;
            double total = Math.Max(1, Math.Max(sample.DwmFramesDisplayedDelta, sample.DwmCompositionFramesDelta) + dropped);
            Consider(TelemetryCause.DisplayFrameDrop, Math.Clamp(dropped / total * 4.0, 0.35, 1),
                "DWM dropped or missed frames");
        }
        if (!double.IsNaN(sample.ThermalCelsius) && sample.ThermalCelsius >= 85)
            Consider(TelemetryCause.ThermalPressure, Math.Clamp((sample.ThermalCelsius - 70) / 30.0, 0.2, 1), "thermal zone");
        if (!double.IsNaN(sample.EffectiveFrequencyPercent) && sample.EffectiveFrequencyPercent <= 70 && sample.CpuPercent >= 50)
            Consider(TelemetryCause.ThermalPressure, Math.Clamp((80 - sample.EffectiveFrequencyPercent) / 40.0, 0.2, 1), "reduced effective frequency/QoS");

        ForegroundProcessSnapshot? process = sample.ForegroundProcess;
        if (process is { IsValid: true } && process.CpuPercent >= 80)
            Consider(TelemetryCause.ForegroundProcessPressure, Math.Clamp((process.CpuPercent - 60) / 40.0, 0.2, 1), "foreground CPU");
        if (process is { IsValid: true } && process.RespondingSampleValid && !process.Responding)
            Consider(TelemetryCause.ForegroundProcessPressure, 0.8, "foreground not responding");
        if (process is { IsValid: true, IoSampleValid: true } &&
            Math.Max(process.ReadBytesPerSecond, Math.Max(process.WriteBytesPerSecond, process.OtherBytesPerSecond)) >= 10_000_000)
            Consider(TelemetryCause.ForegroundProcessPressure, 0.55, "foreground I/O");
        ulong halfPhysical = sample.PhysicalMemoryTotalBytes / 2;
        if (process is { IsValid: true, MemorySampleValid: true } && halfPhysical > 0 &&
            (process.WorkingSetBytes > 0 && (ulong)process.WorkingSetBytes >= halfPhysical ||
             process.PrivateBytes > 0 && (ulong)process.PrivateBytes >= halfPhysical))
            Consider(TelemetryCause.ForegroundProcessPressure, 0.65, "foreground memory");
        if (process is { IsValid: true, HandleSampleValid: true } && process.HandleCount >= 100_000)
            Consider(TelemetryCause.ForegroundProcessPressure, 0.5, "foreground handles");
        if (process is { IsValid: true, ThreadSampleValid: true } && process.ThreadCount >= 512)
            Consider(TelemetryCause.ForegroundProcessPressure, 0.5, "foreground threads");

        if (!evidence) return new TelemetryClassification { Cause = TelemetryCause.Healthy, Score = 0, Reason = "healthy", EvidenceAvailable = sample.SchedulerSampleValid || sample.MemorySampleValid };
        return new TelemetryClassification { Cause = cause, Score = Math.Clamp(best, 0, 1), Reason = reason, EvidenceAvailable = true };
    }

    private void AddManagedNetworkFallback(ref RawCounters counters, long sampleTick)
    {
        if (counters.HasNetworkCounterData) return;
        try
        {
            long errors = 0;
            long discards = 0;
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                IPv4InterfaceStatistics stats;
                try { stats = adapter.GetIPv4Statistics(); }
                catch { continue; }
                errors = SaturatingAdd(errors, stats.IncomingPacketsWithErrors);
                errors = SaturatingAdd(errors, stats.OutgoingPacketsWithErrors);
                discards = SaturatingAdd(discards, stats.IncomingPacketsDiscarded);
                discards = SaturatingAdd(discards, stats.OutgoingPacketsDiscarded);
            }

            if (_networkFallbackTick > 0 && sampleTick > _networkFallbackTick)
            {
                double seconds = Math.Max(0.001, (sampleTick - _networkFallbackTick) / 1000.0);
                counters.NetworkErrorsPerSecond = Math.Max(0, (errors - _networkFallbackErrors) / seconds);
                counters.NetworkDiscardsPerSecond = Math.Max(0, (discards - _networkFallbackDiscards) / seconds);
                counters.UsingFallback = true;
                counters.ValidCount += 2;
            }
            _networkFallbackErrors = errors;
            _networkFallbackDiscards = discards;
            _networkFallbackTick = sampleTick;
        }
        catch
        {
            // NetworkInterface is optional diagnostics; no fallback value is asserted.
        }
    }

    private long _networkFallbackTick;
    private long _networkFallbackErrors;
    private long _networkFallbackDiscards;

    private ForegroundProcessSnapshot? TryCaptureForeground(long sampleTick)
    {
        uint pid;
        try { pid = Native.GetForegroundPid(); }
        catch { return null; }
        if (pid == 0) return null;

        try
        {
            using Process process = Process.GetProcessById((int)pid);
            process.Refresh();
            string name = "unknown";
            try { name = process.ProcessName; } catch { }

            TimeSpan total = process.TotalProcessorTime;
            TimeSpan kernel = process.PrivilegedProcessorTime;
            TimeSpan user = process.UserProcessorTime;
            long elapsedMs = _previousForegroundPid == pid && _previousForegroundTick > 0
                ? Math.Max(1, sampleTick - _previousForegroundTick)
                : 0;
            double cpu = double.NaN;
            if (elapsedMs > 0 && _previousForegroundPid == pid)
            {
                double deltaCpuMs = Math.Max(0, total.TotalMilliseconds - _previousForegroundCpuTicks / (double)TimeSpan.TicksPerMillisecond);
                cpu = Math.Clamp(deltaCpuMs / Math.Max(1, elapsedMs) * 100.0 / Math.Max(1, Environment.ProcessorCount), 0, 100);
            }

            double kernelCpu = double.NaN;
            double userCpu = double.NaN;
            if (elapsedMs > 0 && _previousForegroundPid == pid)
            {
                kernelCpu = Math.Clamp(Math.Max(0, kernel.TotalMilliseconds - _previousForegroundKernelTicks / (double)TimeSpan.TicksPerMillisecond) /
                    elapsedMs * 100.0 / Math.Max(1, Environment.ProcessorCount), 0, 100);
                userCpu = Math.Clamp(Math.Max(0, user.TotalMilliseconds - _previousForegroundUserTicks / (double)TimeSpan.TicksPerMillisecond) /
                    elapsedMs * 100.0 / Math.Max(1, Environment.ProcessorCount), 0, 100);
            }

            ProcessIoCounters io = default;
            bool ioValid = TryGetProcessIoCounters(process.Handle, out io);
            long readBps = -1, writeBps = -1, otherBps = -1;
            long readOps = -1, writeOps = -1, otherOps = -1;
            if (ioValid && _previousForegroundPid == pid && _previousForegroundIoValid && elapsedMs > 0)
            {
                double seconds = elapsedMs / 1000.0;
                readBps = Rate(io.ReadTransferCount, _previousForegroundIo.ReadTransferCount, seconds);
                writeBps = Rate(io.WriteTransferCount, _previousForegroundIo.WriteTransferCount, seconds);
                otherBps = Rate(io.OtherTransferCount, _previousForegroundIo.OtherTransferCount, seconds);
                readOps = Rate(io.ReadOperationCount, _previousForegroundIo.ReadOperationCount, seconds);
                writeOps = Rate(io.WriteOperationCount, _previousForegroundIo.WriteOperationCount, seconds);
                otherOps = Rate(io.OtherOperationCount, _previousForegroundIo.OtherOperationCount, seconds);
            }

            bool responding = false;
            bool respondingValid = false;
            try
            {
                responding = process.Responding;
                respondingValid = true;
            }
            catch { }

            var result = new ForegroundProcessSnapshot
            {
                ProcessId = pid,
                Name = name,
                IsValid = true,
                Responding = responding,
                RespondingSampleValid = respondingValid,
                CpuPercent = cpu,
                KernelCpuPercent = kernelCpu,
                UserCpuPercent = userCpu,
                WorkingSetBytes = SafeLong(() => process.WorkingSet64),
                PrivateBytes = SafeLong(() => process.PrivateMemorySize64),
                VirtualBytes = SafeLong(() => process.VirtualMemorySize64),
                HandleCount = SafeInt(() => process.HandleCount),
                ThreadCount = SafeInt(() => process.Threads.Count),
                ReadBytesPerSecond = readBps,
                WriteBytesPerSecond = writeBps,
                OtherBytesPerSecond = otherBps,
                ReadOperationsPerSecond = readOps,
                WriteOperationsPerSecond = writeOps,
                OtherOperationsPerSecond = otherOps,
                IoSampleValid = ioValid,
                MemorySampleValid = true,
                HandleSampleValid = true,
                ThreadSampleValid = true,
            };

            _previousForegroundPid = pid;
            _previousForegroundTick = sampleTick;
            _previousForegroundCpuTicks = total.Ticks;
            _previousForegroundKernelTicks = kernel.Ticks;
            _previousForegroundUserTicks = user.Ticks;
            _previousForegroundIo = io;
            _previousForegroundIoValid = ioValid;
            return result;
        }
        catch
        {
            _previousForegroundPid = 0;
            _previousForegroundIoValid = false;
            return null;
        }
    }

    private static int SafeInt(Func<int> getter)
    {
        try { return getter(); } catch { return -1; }
    }

    private static long SafeLong(Func<long> getter)
    {
        try { return getter(); } catch { return 0; }
    }

    private static long Rate(ulong current, ulong previous, double seconds)
    {
        if (current < previous || seconds <= 0) return -1;
        double value = (current - previous) / seconds;
        return value >= long.MaxValue ? long.MaxValue : (long)Math.Max(0, value);
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0) return left;
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private static TelemetryOptions NormalizeOptions(TelemetryOptions source) => new()
    {
        RingCapacity = Math.Clamp(source.RingCapacity <= 0 ? DefaultCapacity : source.RingCapacity, 8, 300),
        SamplePeriodMilliseconds = Math.Clamp(source.SamplePeriodMilliseconds <= 0 ? 1000 : source.SamplePeriodMilliseconds, 500, 5000),
        SampleBudgetMilliseconds = Math.Clamp(source.SampleBudgetMilliseconds <= 0 ? 150 : source.SampleBudgetMilliseconds, 25, 500),
        BeforeSampleCount = Math.Clamp(source.BeforeSampleCount <= 0 ? 30 : source.BeforeSampleCount, 1, 300),
        AfterSampleCount = Math.Clamp(source.AfterSampleCount <= 0 ? 10 : source.AfterSampleCount, 1, 300),
    };

    private static IntPtr TryCreateLowMemoryNotification()
    {
        try { return CreateMemoryResourceNotification(LowMemoryResourceNotification); }
        catch { return IntPtr.Zero; }
    }

    private static bool TryQueryLowMemoryNotification(IntPtr handle, out bool low)
    {
        low = false;
        try { return QueryMemoryResourceNotification(handle, out low); }
        catch { return false; }
    }

    private static bool TryGetProcessIoCounters(IntPtr processHandle, out ProcessIoCounters counters)
    {
        counters = default;
        if (processHandle == IntPtr.Zero) return false;
        try { return GetProcessIoCounters(processHandle, out counters); }
        catch { return false; }
    }

    public void Dispose()
    {
        _stop = true;
        Thread? thread = _thread;
        if (thread is not null && !ReferenceEquals(thread, Thread.CurrentThread))
            thread.Join(500);
        _thread = null;
        try { _pdh.Dispose(); } catch { }
        if (_lowMemoryNotification != IntPtr.Zero)
        {
            try { CloseHandle(_lowMemoryNotification); } catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessIoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out ProcessIoCounters lpIoCounters);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateMemoryResourceNotification(int notificationType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryMemoryResourceNotification(IntPtr resourceNotificationHandle, [MarshalAs(UnmanagedType.Bool)] out bool resourceState);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private struct RawCounters
    {
        public double DiskLatencyMilliseconds;
        public double DiskQueueLength;
        public double ProcessorQueueLength;
        public double DpcTimePercent;
        public double InterruptTimePercent;
        public double InterruptsPerSecond;
        public double NetworkErrorsPerSecond;
        public double NetworkDiscardsPerSecond;
        public double NetworkRetransmitsPerSecond;
        public double GpuEngineUtilizationPercent;
        public double ThermalCelsius;
        public double EffectiveFrequencyPercent;
        public double PowerLimitPercent;
        public bool QoSDegraded;
        public bool QoSSampleValid;
        public bool UsingFallback;
        public int ValidCount;

        public bool HasNetworkCounterData =>
            !double.IsNaN(NetworkErrorsPerSecond) ||
            !double.IsNaN(NetworkDiscardsPerSecond) ||
            !double.IsNaN(NetworkRetransmitsPerSecond);

        public void MarkUnavailableAfterBudget()
        {
            DiskLatencyMilliseconds = double.NaN;
            DiskQueueLength = double.NaN;
            ProcessorQueueLength = double.NaN;
            DpcTimePercent = double.NaN;
            InterruptTimePercent = double.NaN;
            InterruptsPerSecond = double.NaN;
            NetworkErrorsPerSecond = double.NaN;
            NetworkDiscardsPerSecond = double.NaN;
            NetworkRetransmitsPerSecond = double.NaN;
            GpuEngineUtilizationPercent = double.NaN;
            ThermalCelsius = double.NaN;
            EffectiveFrequencyPercent = double.NaN;
            PowerLimitPercent = double.NaN;
            ValidCount = 0;
            UsingFallback = true;
        }
    }

    private sealed class PdhCollector : IDisposable
    {
        private const uint PdhFmtDouble = 0x00000200;
        private const uint PdhFmtLarge = 0x00000400;
        private const uint PdhErrorSuccess = 0;
        private const uint PdhCStatusValidData = 0;
        private const uint PdhCStatusNewData = 1;

        private readonly IntPtr _query;
        private readonly CounterSlot[] _slots;
        private readonly bool _opened;

        private enum Metric
        {
            DiskLatency,
            DiskQueue,
            ProcessorQueue,
            Dpc,
            Interrupt,
            Interrupts,
            NetworkErrors,
            NetworkDiscards,
            NetworkRetransmits,
            Gpu,
            Thermal,
            EffectiveFrequency,
            PowerLimit,
        }

        private readonly record struct Definition(Metric Metric, string Path, double Scale, bool Large);

        private static readonly Definition[] Definitions =
        {
            new(Metric.DiskLatency, @"\PhysicalDisk(_Total)\Avg. Disk sec/Transfer", 1000, false),
            new(Metric.DiskQueue, @"\PhysicalDisk(_Total)\Current Disk Queue Length", 1, false),
            new(Metric.ProcessorQueue, @"\System\Processor Queue Length", 1, false),
            new(Metric.Dpc, @"\Processor(_Total)\% DPC Time", 1, false),
            new(Metric.Interrupt, @"\Processor(_Total)\% Interrupt Time", 1, false),
            new(Metric.Interrupts, @"\Processor(_Total)\Interrupts/sec", 1, false),
            new(Metric.NetworkErrors, @"\Network Interface(_Total)\Packets Received Errors", 1, false),
            new(Metric.NetworkDiscards, @"\Network Interface(_Total)\Packets Received Discarded", 1, false),
            new(Metric.NetworkRetransmits, @"\TCPv4\Segments Retransmitted/sec", 1, false),
            new(Metric.Gpu, @"\GPU Engine(_Total)\Utilization Percentage", 1, false),
            new(Metric.Thermal, @"\Thermal Zone Information(_Total)\Temperature", 0.1, true),
            new(Metric.EffectiveFrequency, @"\Processor Information(_Total)\% of Maximum Frequency", 1, false),
            new(Metric.PowerLimit, @"\Processor Information(_Total)\% Performance Limit", 1, false),
        };

        public const int CounterCount = 13;
        public bool IsAvailable => _opened;

        public PdhCollector()
        {
            _query = IntPtr.Zero;
            _slots = new CounterSlot[Definitions.Length];
            _opened = false;
            try
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out IntPtr query) != PdhErrorSuccess)
                    return;
                _query = query;
                _opened = true;
                for (int i = 0; i < Definitions.Length; i++)
                {
                    Definition definition = Definitions[i];
                    uint addStatus = PdhAddEnglishCounter(_query, definition.Path, IntPtr.Zero, out IntPtr counter);
                    if (addStatus != PdhErrorSuccess)
                        addStatus = PdhAddCounter(_query, definition.Path, IntPtr.Zero, out counter);
                    if (addStatus == PdhErrorSuccess)
                        _slots[i] = new CounterSlot(definition.Metric, counter, definition.Scale, definition.Large);
                }
                // The first collection establishes baselines for rate counters.
                _ = PdhCollectQueryData(_query);
            }
            catch
            {
                _opened = false;
            }
        }

        public void TryCollect(ref RawCounters result)
        {
            result.DiskLatencyMilliseconds = double.NaN;
            result.DiskQueueLength = double.NaN;
            result.ProcessorQueueLength = double.NaN;
            result.DpcTimePercent = double.NaN;
            result.InterruptTimePercent = double.NaN;
            result.InterruptsPerSecond = double.NaN;
            result.NetworkErrorsPerSecond = double.NaN;
            result.NetworkDiscardsPerSecond = double.NaN;
            result.NetworkRetransmitsPerSecond = double.NaN;
            result.GpuEngineUtilizationPercent = double.NaN;
            result.ThermalCelsius = double.NaN;
            result.EffectiveFrequencyPercent = double.NaN;
            result.PowerLimitPercent = double.NaN;
            result.ValidCount = 0;
            result.UsingFallback = false;
            if (!_opened || _query == IntPtr.Zero) return;

            try
            {
                uint status = PdhCollectQueryData(_query);
                if (status != PdhErrorSuccess && status != PdhCStatusNewData) return;
                for (int i = 0; i < _slots.Length; i++)
                {
                    CounterSlot slot = _slots[i];
                    if (slot.Handle == IntPtr.Zero) continue;
                    double value;
                    if (!TryGet(slot, out value)) continue;
                    switch (slot.Metric)
                    {
                        case Metric.DiskLatency: result.DiskLatencyMilliseconds = value; break;
                        case Metric.DiskQueue: result.DiskQueueLength = value; break;
                        case Metric.ProcessorQueue: result.ProcessorQueueLength = value; break;
                        case Metric.Dpc: result.DpcTimePercent = value; break;
                        case Metric.Interrupt: result.InterruptTimePercent = value; break;
                        case Metric.Interrupts: result.InterruptsPerSecond = value; break;
                        case Metric.NetworkErrors: result.NetworkErrorsPerSecond = value; break;
                        case Metric.NetworkDiscards: result.NetworkDiscardsPerSecond = value; break;
                        case Metric.NetworkRetransmits: result.NetworkRetransmitsPerSecond = value; break;
                        case Metric.Gpu: result.GpuEngineUtilizationPercent = value; break;
                        case Metric.Thermal: result.ThermalCelsius = value - 273.15; break;
                        case Metric.EffectiveFrequency: result.EffectiveFrequencyPercent = value; break;
                        case Metric.PowerLimit: result.PowerLimitPercent = value; break;
                    }
                    result.ValidCount++;
                }
            }
            catch
            {
                result.UsingFallback = true;
            }
        }

        private static bool TryGet(CounterSlot slot, out double value)
        {
            value = double.NaN;
            try
            {
                uint format = slot.Large ? PdhFmtLarge : PdhFmtDouble;
                uint status = PdhGetFormattedCounterValue(slot.Handle, format, out _, out PdhFormattedCounterValue formatted);
                if (status != PdhErrorSuccess && status != PdhCStatusValidData && status != PdhCStatusNewData)
                    return false;
                if (formatted.CStatus != PdhCStatusValidData && formatted.CStatus != PdhCStatusNewData)
                    return false;
                value = slot.Large ? formatted.LargeValue * slot.Scale : formatted.DoubleValue * slot.Scale;
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch { return false; }
        }

        public void Dispose()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Handle == IntPtr.Zero) continue;
                try { PdhRemoveCounter(_slots[i].Handle); } catch { }
            }
            if (_query != IntPtr.Zero)
            {
                try { PdhCloseQuery(_query); } catch { }
            }
        }

        private readonly record struct CounterSlot(Metric Metric, IntPtr Handle, double Scale, bool Large);

        [StructLayout(LayoutKind.Explicit)]
        private struct PdhFormattedCounterValue
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public int LongValue32;
            [FieldOffset(8)] public long LargeValue;
            [FieldOffset(8)] public double DoubleValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll", EntryPoint = "PdhAddCounterW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint PdhAddCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint counterType, out PdhFormattedCounterValue value);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern uint PdhRemoveCounter(IntPtr counter);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern uint PdhCloseQuery(IntPtr query);
    }
}
