namespace Thaw;

/// <summary>
/// Watches system health every second from a high-priority background thread:
///  - scheduling delay (how long the machine actually froze before this thread ran again)
///  - CPU utilization (GetSystemTimes)
///  - RAM pressure (GlobalMemoryStatusEx)
/// Produces a stress flag with hysteresis and fires an event on hard stalls
/// (used for the automatic unfreeze).
/// </summary>
internal sealed class Watchdog : IDisposable
{
    private readonly Config _config;
    private Thread? _thread;
    private volatile bool _stop;

    // Sampled values (updated by the watchdog thread, read by the UI thread).
    private volatile bool _stressed;
    private double _cpuPercent;
    private uint _memPercent;
    private long _lastStallMs;
    private long _lastStallAtTick;

    private long _prevIdle, _prevKernel, _prevUser;
    private int _healthySamples;

    public event Action? HardStallDetected;

    public Watchdog(Config config) => _config = config;

    public bool Stressed => _stressed;

    /// <summary>True when a stall happened within the last 30 s (used for the Alt+F4 decision).</summary>
    public bool RecentStall =>
        Environment.TickCount64 - Volatile.Read(ref _lastStallAtTick) < 30_000;

    public double CpuPercent => Volatile.Read(ref _cpuPercent);
    public uint MemPercent => Volatile.Read(ref _memPercent);
    public long LastStallMs => Volatile.Read(ref _lastStallMs);

    public string StatusText
    {
        get
        {
            string state = _stressed
                ? "STRESSED — unfreeze now!"
                : "healthy — Alt+F4 = unfreeze";
            return $"CPU {_cpuPercent:0}% · RAM {_memPercent}% · {state}";
        }
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Thaw.Watchdog",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    private void Loop()
    {
        Native.GetSystemTimes(out long idle, out long kernel, out long user);
        _prevIdle = idle; _prevKernel = kernel; _prevUser = user;

        while (!_stop)
        {
            long t0 = Environment.TickCount64;
            Thread.Sleep(1000);
            long wallMs = Environment.TickCount64 - t0;
            long delayMs = Math.Max(0, wallMs - 1000);

            // CPU %
            if (Native.GetSystemTimes(out long idle2, out long kernel2, out long user2))
            {
                long idleD = idle2 - _prevIdle;
                long kernD = kernel2 - _prevKernel;
                long userD = user2 - _prevUser;
                long total = kernD + userD;
                Volatile.Write(ref _cpuPercent, total > 0 ? Math.Clamp(100.0 * (1.0 - (double)idleD / total), 0, 100) : 0);
                _prevIdle = idle2; _prevKernel = kernel2; _prevUser = user2;
            }

            // RAM %
            var mem = Native.GetMemoryStatus();
            Volatile.Write(ref _memPercent, mem.dwMemoryLoad);

            Volatile.Write(ref _lastStallMs, delayMs);
            if (delayMs > 800)
                Volatile.Write(ref _lastStallAtTick, Environment.TickCount64);

            bool hardStall = delayMs >= _config.HardStallMs;
            bool stressedNow = ComputeStress(delayMs, Volatile.Read(ref _cpuPercent), Volatile.Read(ref _memPercent));

            if (stressedNow) _healthySamples = 0;
            else _healthySamples++;

            bool prev = _stressed;
            _stressed = stressedNow || hardStall;
            if (_stressed != prev)
                Log.Info($"Stress state: {(_stressed ? "STRESSED" : "healthy")} (stall {delayMs} ms, CPU {Volatile.Read(ref _cpuPercent):0}%, RAM {Volatile.Read(ref _memPercent)}%)");

            if (hardStall && !_stop)
            {
                Log.Warn($"HARD STALL detected: {delayMs} ms — auto-unfreeze");
                try { HardStallDetected?.Invoke(); }
                catch (Exception ex) { Log.Error("HardStall handler error", ex); }
                _healthySamples = 0;
            }

            // small breather after a stall so recovery can happen
            if (hardStall) Thread.Sleep(2000);
        }
    }

    /// <summary>Blends stall, CPU and RAM into 0..1 and applies hysteresis.</summary>
    private bool ComputeStress(long delayMs, double cpu, uint mem)
    {
        double stallF = Math.Clamp(delayMs / 8000.0, 0, 1);
        double cpuF = Math.Clamp((cpu - 60.0) / (_config.CpuStressPercent - 60.0), 0, 1);
        double memF = Math.Clamp((mem - 70.0) / (_config.MemStressPercent - 70.0), 0, 1);
        double score = 0.55 * stallF + 0.25 * cpuF + 0.20 * memF;

        if (_stressed)
        {
            // Hysteresis: stay stressed until clearly healthy for several samples.
            if (_healthySamples >= 5 && score < 0.25 && delayMs < 1200) return false;
            return true;
        }

        return score > 0.40 || delayMs >= _config.StallThresholdMs;
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(2000);
        _thread = null;
    }
}
