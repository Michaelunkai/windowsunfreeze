namespace Thaw;

/// <summary>
/// The strongest currently observed health signal. ResourcePressure is
/// intentionally phrased as a likely cause: the available Win32 probes do not
/// expose a compositor frame counter, so recurring scheduler jitter is only a
/// useful proxy for frame drops/resource pressure, not proof of one.
/// </summary>
internal enum HealthCause
{
    Healthy,
    SchedulerStall,
    CpuPressure,
    MemoryPressure,
    ResourcePressure,
}

/// <summary>
/// Watches system health every second from a high-priority background thread:
///  - scheduling delay (how long the machine actually froze before this thread ran again)
///  - CPU utilization (GetSystemTimes)
///  - RAM pressure (GlobalMemoryStatusEx)
/// Produces a stress flag with hysteresis and fires an event on a claimed hard
/// stall (used for the automatic unfreeze).
///
/// The detector deliberately uses only the two already-available system probes
/// and one timer sample per second. CPU and RAM pressure need two consecutive
/// hot samples, while three cool samples clear each pressure latch. This keeps
/// a short burst from changing the tray state and makes the reported cause more
/// useful than a single blended score.
/// </summary>
internal sealed class Watchdog : IDisposable
{
    private const int SamplePeriodMs = 1000;
    private const int StallRecordThresholdMs = 800;
    private const int FrameDropJitterMinMs = 250;
    private const int FrameDropJitterMaxMs = 500;
    private const int FrameDropWindowSize = 6;
    private const int FrameDropSamplesRequired = 2;
    private const int FrameDropClearSamples = 3;

    private const int PressureAssertSamples = 2;
    private const int PressureClearSamples = 3;
    private const int HealthySamplesToClearStress = 5;
    private const int HardStallHealthySamples = 8;
    private const int HardStallCooldownMs = 30_000;
    private const int HardStallBreatherMs = 2000;
    private const int CpuHysteresisPercent = 5;
    private const uint MemoryHysteresisPercent = 3;

    private const double CpuBaselinePercent = 60.0;
    private const double MemoryBaselinePercent = 70.0;
    private const double StressActivateScore = 0.40;
    private const double StressClearScore = 0.25;
    private const int StressClearDelayMs = 1200;

    private readonly Config _config;
    private readonly Telemetry _telemetry;
    private Thread? _thread;
    private volatile bool _stop;
    private int _started;

    // Sampled values (updated by the watchdog thread, read by the UI thread).
    private volatile bool _stressed;
    private double _cpuPercent;
    private uint _memPercent;
    private long _lastStallMs;
    private long _lastStallAtTick;
    private double _stressScore;
    private int _healthCause = (int)HealthCause.Healthy;
    private string _healthReason = "healthy";
    private bool _schedulerStall;
    private bool _cpuPressure;
    private bool _memoryPressure;
    private bool _likelyFrameDrop;
    private bool _cpuSampleValid;
    private bool _memorySampleValid;

    private long _prevIdle, _prevKernel, _prevUser;
    private int _healthySamples;
    private long _lastSampleErrorTick;

    // Pressure latches avoid treating one busy second as sustained pressure.
    private int _cpuHotSamples;
    private int _cpuClearSamples;
    private bool _cpuPressureActive;
    private int _memoryHotSamples;
    private int _memoryClearSamples;
    private bool _memoryPressureActive;

    // A six-second rolling window is enough to recognize recurring jitter
    // without allocating or polling a graphics API. It is only a hint.
    private int _jitterMask;
    private int _jitterCount;
    private bool _frameDropActive;
    private int _frameDropClearSamples;

    // Hard-stall recovery is one-shot per episode. The gate is re-armed only
    // after a stable recovery window and a cooldown, so a still-hung machine
    // cannot repeatedly invoke the unfreezer.
    private bool _hardStallArmed = true;
    private bool _hardStallSuppressionLogged;
    private int _hardStallRecoverySamples;
    private long _hardStallCooldownUntilTick;
    private long _lastHardStallAtTick;
    private long _hardStallCount;

    public event Action? HardStallDetected;

    public Watchdog(Config config)
    {
        _config = config;
        _telemetry = new Telemetry();
    }

    /// <summary>Read-only telemetry integration surface; watchdog stress semantics remain separate.</summary>
    public Telemetry Telemetry => _telemetry;

    public TelemetrySnapshot? LatestTelemetry => _telemetry.LatestSnapshot;

    public TelemetryIncident? ActiveTelemetryIncident => _telemetry.ActiveIncident;

    public TelemetryIncident? LastTelemetryIncident => _telemetry.LastIncident;

    /// <summary>Captures the bounded pre-recovery ring without changing stress state.</summary>
    public TelemetryIncident CaptureTelemetryBefore(string? reason = null) => _telemetry.BeginIncident(reason);

    /// <summary>Captures bounded post-recovery evidence without changing stress state.</summary>
    public bool CaptureTelemetryAfter(string? reason = null) => _telemetry.CaptureAfterSnapshot(reason);

    public bool Stressed => _stressed;

    /// <summary>True when a stall happened within the last 30 s (used for the Alt+F4 decision).</summary>
    public bool RecentStall
    {
        get
        {
            long at = Volatile.Read(ref _lastStallAtTick);
            return at > 0 && Environment.TickCount64 - at < 30_000;
        }
    }

    public double CpuPercent => Volatile.Read(ref _cpuPercent);
    public uint MemPercent => Volatile.Read(ref _memPercent);
    public long LastStallMs => Volatile.Read(ref _lastStallMs);

    /// <summary>Primary cause of the current (or still-clearing) stressed state.</summary>
    public HealthCause Cause => (HealthCause)Volatile.Read(ref _healthCause);

    /// <summary>Alias for callers that prefer a health-oriented name.</summary>
    public HealthCause Health => Cause;

    public string HealthReason => Volatile.Read(ref _healthReason);
    public bool SchedulerStall => Volatile.Read(ref _schedulerStall);
    public bool CpuPressure => Volatile.Read(ref _cpuPressure);
    public bool MemoryPressure => Volatile.Read(ref _memoryPressure);

    /// <summary>
    /// True when recurring 250–500 ms scheduling jitter looks consistent with a
    /// frame-drop/resource-pressure episode. This is a likelihood signal, not
    /// direct proof that a display frame was dropped.
    /// </summary>
    public bool LikelyFrameDrop => Volatile.Read(ref _likelyFrameDrop);

    /// <summary>0..1 weighted severity score; inspect Cause for the likely source.</summary>
    public double StressScore => Volatile.Read(ref _stressScore);

    public bool CpuSampleValid => Volatile.Read(ref _cpuSampleValid);
    public bool MemorySampleValid => Volatile.Read(ref _memorySampleValid);
    public int ConsecutiveHealthySamples => Volatile.Read(ref _healthySamples);
    public long HardStallCount => Volatile.Read(ref _hardStallCount);
    public long LastHardStallAtTick => Volatile.Read(ref _lastHardStallAtTick);
    public bool HardStallRecoveryArmed => Volatile.Read(ref _hardStallArmed);

    /// <summary>Remaining hard-stall cooldown in milliseconds, or zero when clear.</summary>
    public long HardStallCooldownRemainingMs =>
        Math.Max(0, Volatile.Read(ref _hardStallCooldownUntilTick) - Environment.TickCount64);

    public string StatusText
    {
        get
        {
            double cpu = Volatile.Read(ref _cpuPercent);
            uint mem = Volatile.Read(ref _memPercent);
            string state = _stressed
                ? $"STRESSED — unfreeze ({ShortCauseText(Cause)})"
                : "healthy — Alt+F4 = unfreeze";
            return $"CPU {cpu:0}% · RAM {mem}% · {state}";
        }
    }

    public void Start()
    {
        if (_stop || Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;

        try
        {
            try { _telemetry.Start(); }
            catch (Exception ex) { Log.Debug("Telemetry start unavailable: " + ex.Message); }

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "Thaw.Watchdog",
            };
            try { _thread.Priority = ThreadPriority.Highest; }
            catch (Exception ex) { Log.Debug("Watchdog thread priority unavailable: " + ex.Message); }
            _thread.Start();
        }
        catch
        {
            _thread = null;
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    private void Loop()
    {
        bool haveTimes;
        long idle;
        long kernel;
        long user;
        try
        {
            haveTimes = Native.GetSystemTimes(out idle, out kernel, out user);
        }
        catch (Exception ex)
        {
            haveTimes = false;
            idle = kernel = user = 0;
            LogSampleFailure(ex);
        }
        if (!haveTimes) idle = kernel = user = 0;
        _prevIdle = idle; _prevKernel = kernel; _prevUser = user;

        while (!_stop)
        {
            try
            {
                long t0 = Environment.TickCount64;
                Thread.Sleep(SamplePeriodMs);
                if (_stop) break;

                long now = Environment.TickCount64;
                long wallMs = Math.Max(0, now - t0);
                long delayMs = Math.Max(0, wallMs - SamplePeriodMs);

                // CPU % (GetSystemTimes includes idle time in kernel time).
                bool cpuSampleValid = false;
                if (Native.GetSystemTimes(out long idle2, out long kernel2, out long user2))
                {
                    long idleD = idle2 - _prevIdle;
                    long kernD = kernel2 - _prevKernel;
                    long userD = user2 - _prevUser;
                    long total = kernD + userD;
                    if (total > 0 && idleD >= 0 && idleD <= total && kernD >= 0 && userD >= 0)
                    {
                        Volatile.Write(ref _cpuPercent,
                            Math.Clamp(100.0 * (1.0 - (double)idleD / total), 0, 100));
                        cpuSampleValid = true;
                    }

                    _prevIdle = idle2; _prevKernel = kernel2; _prevUser = user2;
                }

                // RAM % (GlobalMemoryStatusEx is cheap; keep the last value on a failed sample).
                var mem = Native.GetMemoryStatus();
                bool memorySampleValid = mem.dwMemoryLoad is >= 1 and <= 100;
                if (memorySampleValid) Volatile.Write(ref _memPercent, mem.dwMemoryLoad);

                Volatile.Write(ref _cpuSampleValid, cpuSampleValid);
                Volatile.Write(ref _memorySampleValid, memorySampleValid);

                // This is a lock-free handoff. Optional PDH/process/network
                // probes run on Telemetry's lower-priority worker and therefore
                // cannot delay the existing watchdog or hard-stall gate.
                _telemetry.PublishWatchdogSample(
                    now,
                    delayMs,
                    Volatile.Read(ref _cpuPercent),
                    Volatile.Read(ref _memPercent),
                    cpuSampleValid,
                    memorySampleValid);

                Volatile.Write(ref _lastStallMs, delayMs);
                if (delayMs > StallRecordThresholdMs)
                    Volatile.Write(ref _lastStallAtTick, now);

                bool hardStall = delayMs >= HardStallThresholdMs;
                UpdateHealth(
                    delayMs,
                    Volatile.Read(ref _cpuPercent),
                    Volatile.Read(ref _memPercent),
                    cpuSampleValid,
                    memorySampleValid,
                    hardStall);

                bool claimHardStall = TryClaimHardStall(now, delayMs, hardStall);
                if (claimHardStall && !_stop)
                {
                    try { _telemetry.BeginIncident("hard stall"); }
                    catch (Exception ex) { Log.Debug("Telemetry incident start unavailable: " + ex.Message); }
                    Log.Warn($"HARD STALL detected: {delayMs} ms — auto-unfreeze (one-shot; cooldown {HardStallCooldownMs / 1000}s)");
                    try { HardStallDetected?.Invoke(); }
                    catch (Exception ex) { Log.Error("HardStall handler error", ex); }
                }

                // Small breather after a claimed or suppressed hard stall so the
                // recovery path can make progress; the gate prevents repeat events.
                if (hardStall) Thread.Sleep(HardStallBreatherMs);
            }
            catch (Exception ex)
            {
                // A single native/probe failure must not terminate the only
                // automatic-stall detector. Preserve the last valid sample and
                // retry on the next bounded interval.
                LogSampleFailure(ex);
            }
        }
    }

    private void LogSampleFailure(Exception ex)
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastSampleErrorTick);
        if (previous != 0 && now - previous < 10_000) return;
        Interlocked.Exchange(ref _lastSampleErrorTick, now);
        Log.Error("Watchdog sample failed; monitoring continues", ex);
    }

    private void UpdateHealth(
        long delayMs,
        double cpu,
        uint mem,
        bool cpuSampleValid,
        bool memorySampleValid,
        bool hardStall)
    {
        int cpuThreshold = CpuStressThreshold;
        uint memoryThreshold = MemoryStressThreshold;

        bool cpuPressure = UpdateCpuPressure(cpu, cpuSampleValid, cpuThreshold);
        bool memoryPressure = UpdateMemoryPressure(mem, memorySampleValid, memoryThreshold);
        bool schedulerStall = hardStall || delayMs >= StallThresholdMs;

        bool frameDropCandidate;
        if (schedulerStall)
        {
            // A true stall has precedence over the weaker jitter proxy.
            _jitterMask = 0;
            _jitterCount = 0;
            _frameDropActive = false;
            _frameDropClearSamples = 0;
            frameDropCandidate = false;
        }
        else
        {
            bool jitter = delayMs >= FrameDropJitterThresholdMs;
            frameDropCandidate = UpdateJitterWindow(jitter);
            // One larger jitter during an already hot interval is enough to
            // expose a likely resource-pressure episode.
            if (!frameDropCandidate && delayMs >= FrameDropJitterMaxMs && (cpuPressure || memoryPressure))
                frameDropCandidate = true;
        }

        if (_frameDropActive)
        {
            if (frameDropCandidate)
            {
                _frameDropClearSamples = 0;
            }
            else if (++_frameDropClearSamples >= FrameDropClearSamples)
            {
                _frameDropActive = false;
                _frameDropClearSamples = 0;
            }
        }
        else if (frameDropCandidate)
        {
            _frameDropActive = true;
            _frameDropClearSamples = 0;
        }

        bool likelyFrameDrop = _frameDropActive;

        TelemetryClassification? telemetryClassification = _telemetry.LatestSnapshot?.Classification;
        bool telemetryStress = telemetryClassification is
        {
            EvidenceAvailable: true,
            Cause: not TelemetryCause.Healthy and not TelemetryCause.Unknown,
            Score: >= 0.35,
        };
        double score = ComputeStressScore(delayMs, cpu, mem, likelyFrameDrop, cpuPressure, memoryPressure);
        if (telemetryStress)
            score = Math.Max(score, telemetryClassification!.Score);
        bool mixedResourcePressure = !schedulerStall && score >= StressActivateScore &&
            (cpuPressure || memoryPressure) && cpu >= 70 && mem >= 70;
        bool resourcePressure = !schedulerStall &&
            (likelyFrameDrop || (cpuPressure && memoryPressure) || mixedResourcePressure);
        bool rawStress = schedulerStall || cpuPressure || memoryPressure || resourcePressure || telemetryStress;

        bool clearCandidate = !rawStress && delayMs < StressClearDelayMs && score < StressClearScore;
        if (clearCandidate) _healthySamples++;
        else _healthySamples = 0;

        bool wasStressed = _stressed;
        if (wasStressed)
            _stressed = _healthySamples < HealthySamplesToClearStress;
        else
            _stressed = rawStress || score >= StressActivateScore;

        HealthCause previousCause = (HealthCause)Volatile.Read(ref _healthCause);
        HealthCause cause = schedulerStall
            ? HealthCause.SchedulerStall
            : resourcePressure
                ? HealthCause.ResourcePressure
                : cpuPressure
                    ? HealthCause.CpuPressure
                    : memoryPressure
                        ? HealthCause.MemoryPressure
                        : HealthCause.Healthy;

        if (!schedulerStall && telemetryStress &&
            (cause == HealthCause.Healthy || telemetryClassification!.Score >= score))
        {
            TelemetryClassification detailedCause = telemetryClassification!;
            cause = detailedCause.Cause switch
            {
                TelemetryCause.SchedulerStall => HealthCause.SchedulerStall,
                TelemetryCause.CpuPressure or TelemetryCause.CpuQueuePressure or
                    TelemetryCause.ForegroundProcessPressure => HealthCause.CpuPressure,
                TelemetryCause.MemoryPressure or TelemetryCause.CommitPressure or
                    TelemetryCause.LowMemory => HealthCause.MemoryPressure,
                _ => HealthCause.ResourcePressure,
            };
        }

        // Keep the last cause visible during the short stress-clear hysteresis
        // window instead of claiming that recovery made the cause certain.
        if (cause == HealthCause.Healthy && _stressed)
        {
            cause = previousCause != HealthCause.Healthy
                ? previousCause
                : delayMs >= StressClearDelayMs
                    ? HealthCause.SchedulerStall
                    : HealthCause.ResourcePressure;
        }

        Volatile.Write(ref _schedulerStall, schedulerStall);
        Volatile.Write(ref _cpuPressure, cpuPressure);
        Volatile.Write(ref _memoryPressure, memoryPressure);
        Volatile.Write(ref _likelyFrameDrop, likelyFrameDrop);
        Volatile.Write(ref _stressScore, Math.Clamp(score, 0, 1));
        Volatile.Write(ref _healthCause, (int)cause);
        string causeReason = telemetryStress && telemetryClassification is not null
            ? telemetryClassification.Reason
            : CauseText(cause);
        Volatile.Write(ref _healthReason, causeReason);

        if (_stressed != wasStressed || cause != previousCause)
        {
            Log.Info($"Health: {(_stressed ? "STRESSED" : "healthy")} — {causeReason} " +
                     $"(stall {delayMs} ms, CPU {cpu:0}%, RAM {mem}%, score {score:0.00})");
        }
    }

    private bool UpdateCpuPressure(double cpu, bool sampleValid, int threshold)
    {
        if (!sampleValid) return _cpuPressureActive;

        if (_cpuPressureActive)
        {
            if (cpu <= Math.Max(0, threshold - CpuHysteresisPercent))
            {
                if (++_cpuClearSamples >= PressureClearSamples)
                {
                    _cpuPressureActive = false;
                    _cpuClearSamples = 0;
                }
            }
            else
            {
                _cpuClearSamples = 0;
            }

            return _cpuPressureActive;
        }

        _cpuClearSamples = 0;
        if (cpu >= threshold)
        {
            if (++_cpuHotSamples >= PressureAssertSamples)
            {
                _cpuPressureActive = true;
                _cpuHotSamples = 0;
            }
        }
        else
        {
            _cpuHotSamples = 0;
        }

        return _cpuPressureActive;
    }

    private bool UpdateMemoryPressure(uint mem, bool sampleValid, uint threshold)
    {
        if (!sampleValid) return _memoryPressureActive;

        if (_memoryPressureActive)
        {
            uint clearThreshold = threshold > MemoryHysteresisPercent
                ? threshold - MemoryHysteresisPercent
                : 0;
            if (mem <= clearThreshold)
            {
                if (++_memoryClearSamples >= PressureClearSamples)
                {
                    _memoryPressureActive = false;
                    _memoryClearSamples = 0;
                }
            }
            else
            {
                _memoryClearSamples = 0;
            }

            return _memoryPressureActive;
        }

        _memoryClearSamples = 0;
        if (mem >= threshold)
        {
            if (++_memoryHotSamples >= PressureAssertSamples)
            {
                _memoryPressureActive = true;
                _memoryHotSamples = 0;
            }
        }
        else
        {
            _memoryHotSamples = 0;
        }

        return _memoryPressureActive;
    }

    private bool UpdateJitterWindow(bool jitter)
    {
        int oldest = (_jitterMask >> (FrameDropWindowSize - 1)) & 1;
        _jitterMask = ((_jitterMask << 1) | (jitter ? 1 : 0)) & ((1 << FrameDropWindowSize) - 1);
        _jitterCount = Math.Max(0, _jitterCount - oldest) + (jitter ? 1 : 0);
        return _jitterCount >= FrameDropSamplesRequired;
    }

    private double ComputeStressScore(
        long delayMs,
        double cpu,
        uint mem,
        bool likelyFrameDrop,
        bool cpuPressure,
        bool memoryPressure)
    {
        double stallF = Math.Clamp(delayMs / Math.Max(8000.0, HardStallThresholdMs), 0, 1);
        double cpuF = Normalize(cpu, CpuBaselinePercent, CpuStressThreshold);
        double memF = Normalize(mem, MemoryBaselinePercent, MemoryStressThreshold);
        double score = 0.55 * stallF + 0.25 * cpuF + 0.20 * memF;

        if (likelyFrameDrop) score = Math.Max(score, 0.35);
        if (cpuPressure && memoryPressure) score = Math.Max(score, 0.45);
        return Math.Clamp(score, 0, 1);
    }

    private bool TryClaimHardStall(long now, long delayMs, bool hardStall)
    {
        bool stable = !Volatile.Read(ref _schedulerStall) &&
                      !Volatile.Read(ref _cpuPressure) &&
                      !Volatile.Read(ref _memoryPressure) &&
                      !Volatile.Read(ref _likelyFrameDrop) &&
                      delayMs < StressClearDelayMs &&
                      Volatile.Read(ref _stressScore) < StressClearScore;

        if (!hardStall)
        {
            if (!Volatile.Read(ref _hardStallArmed))
            {
                if (stable) _hardStallRecoverySamples++;
                else _hardStallRecoverySamples = 0;

                if (_hardStallRecoverySamples >= HardStallHealthySamples &&
                    now >= Volatile.Read(ref _hardStallCooldownUntilTick))
                {
                    Volatile.Write(ref _hardStallArmed, true);
                    _hardStallSuppressionLogged = false;
                    Log.Info("Hard-stall auto-recovery re-armed after a stable recovery window");
                }
            }

            return false;
        }

        _hardStallRecoverySamples = 0;
        if (Volatile.Read(ref _hardStallArmed) && now >= Volatile.Read(ref _hardStallCooldownUntilTick))
        {
            Volatile.Write(ref _hardStallArmed, false);
            Volatile.Write(ref _hardStallCooldownUntilTick, now + HardStallCooldownMs);
            Volatile.Write(ref _lastHardStallAtTick, now);
            Interlocked.Increment(ref _hardStallCount);
            return true;
        }

        if (!_hardStallSuppressionLogged)
        {
            Log.Warn($"HARD STALL still present ({delayMs} ms) — auto-unfreeze suppressed by episode gate/cooldown");
            _hardStallSuppressionLogged = true;
        }

        return false;
    }

    private static double Normalize(double value, double baseline, double ceiling)
    {
        double range = Math.Max(1.0, ceiling - baseline);
        return Math.Clamp((value - baseline) / range, 0, 1);
    }

    private int StallThresholdMs => Math.Max(1, _config.StallThresholdMs);
    private int HardStallThresholdMs => Math.Max(1, _config.HardStallMs);
    private int CpuStressThreshold => Math.Clamp(_config.CpuStressPercent, 1, 100);
    private uint MemoryStressThreshold => (uint)Math.Clamp(_config.MemStressPercent, 1, 100);

    private int FrameDropJitterThresholdMs =>
        (int)Math.Clamp(StallThresholdMs / 4L, FrameDropJitterMinMs, FrameDropJitterMaxMs);

    private static string CauseText(HealthCause cause) => cause switch
    {
        HealthCause.SchedulerStall => "scheduler stall",
        HealthCause.CpuPressure => "CPU pressure",
        HealthCause.MemoryPressure => "RAM pressure",
        HealthCause.ResourcePressure => "likely frame-drop/resource pressure",
        _ => "healthy",
    };

    private static string ShortCauseText(HealthCause cause) => cause switch
    {
        HealthCause.SchedulerStall => "scheduler stall",
        HealthCause.CpuPressure => "CPU pressure",
        HealthCause.MemoryPressure => "RAM pressure",
        HealthCause.ResourcePressure => "resource pressure",
        _ => "health signal",
    };

    public void Dispose()
    {
        _stop = true;
        try { _telemetry.Dispose(); } catch { }
        Thread? thread = _thread;
        if (thread is not null && !ReferenceEquals(thread, Thread.CurrentThread))
            thread.Join(2000);
        _thread = null;
    }
}
