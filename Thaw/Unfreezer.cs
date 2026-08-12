using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Thaw;

internal enum TriggerReason { Hotkey, Panic, Manual, Auto }

internal sealed record UnfreezeStats(
    int ProcessesTuned, int AppsBoosted, long FreedMb, long DurationMs, TriggerReason Reason,
    bool GpuReset, bool DwmRestarted, bool ExplorerRestarted)
{
    public string Summary
    {
        get
        {
            var parts = new List<string>
            {
                $"Unfrozen in {DurationMs} ms",
                $"tuned {ProcessesTuned} processes",
                $"boosted {AppsBoosted} apps",
                $"+{FreedMb} MB RAM",
            };
            if (GpuReset) parts.Add("GPU reset");
            if (DwmRestarted) parts.Add("dwm restarted (screen was still frozen)");
            if (ExplorerRestarted) parts.Add("explorer restarted");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// The unfreeze engine. Runs on its own high-priority thread and applies every
/// safe, non-destructive trick to break a freeze:
///  1. Own process/thread priority raised + 1 ms timer resolution.
///  2. GPU driver reset (Ctrl+Shift+Win+B) — unsticks a frozen/stuttering display.
///  3. Soft-freeze rescue: if the screen is still frozen ~1 s later (dwm hung or ongoing
///     stall), restart dwm.exe so Windows respawns a fresh desktop compositor.
///  4. EmptyWorkingSet on every process → frees physical RAM instantly.
///  5. Purge standby + modified memory lists + flush the system file cache (admin only).
///  6. Boost the foreground app and the shell (explorer/dwm) to High priority.
///  7. Switch to the High Performance power plan (admin only), restored later.
///  8. Force-restart explorer.exe (shell/taskbar) — the classic freeze fix.
/// Nothing else is ever closed, suspended, paused or interrupted.
/// </summary>
internal sealed class Unfreezer
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const int THREAD_PRIORITY_TIME_CRITICAL = 15;

    private static readonly Guid HighPerformanceGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private static readonly HashSet<string> DoNotTrim = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Secure System", "Memory Compression",
    };

    private static readonly HashSet<string> ShellApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
    };

    private readonly Config _config;
    private readonly Watchdog _watchdog;
    private int _active;
    private Guid _previousPowerPlan;
    private bool _powerPlanSaved;

    public event Action? ExplorerRestartedNow;

    public event Action<UnfreezeStats>? Completed;

    public Unfreezer(Config config, Watchdog watchdog)
    {
        _config = config;
        _watchdog = watchdog;
    }

    public void Trigger(TriggerReason reason)
    {
        if (Interlocked.Exchange(ref _active, 1) != 0)
        {
            Log.Debug("Unfreeze already running, skipping duplicate trigger");
            return;
        }

        var thread = new Thread(() => Run(reason))
        {
            IsBackground = true,
            Name = "Thaw.Unfreezer",
            Priority = ThreadPriority.Highest,
        };
        thread.Start();
    }

    private void Run(TriggerReason reason)
    {
        Log.Info($"Unfreeze triggered ({reason})");
        var sw = Stopwatch.StartNew();

        bool timerPeriod = false;
        ProcessPriorityClass oldPriority = ProcessPriorityClass.Normal;
        ThreadPriority oldThreadPriority = ThreadPriority.Normal;

        try
        {
            // 1. Make sure WE can execute even under load.
            oldPriority = Process.GetCurrentProcess().PriorityClass;
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            try
            {
                oldThreadPriority = Thread.CurrentThread.Priority;
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
            catch { /* not fatal */ }
            try { SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL); } catch { /* not fatal */ }
            try
            {
                Native.timeBeginPeriod(1);
                timerPeriod = true;
            }
            catch { /* not fatal */ }

            ulong availBefore = Native.GetMemoryStatus().ullAvailPhys;
            bool gpuReset = false, explorerRestarted = false;
            int tuned = 0, boosted = 0;
            uint fgPid = Native.GetForegroundPid();
            int selfId = Environment.ProcessId;

            // 2. GPU driver reset first — recovers a frozen/stuttering display quickly.
            bool dwmRestarted = false;
            if (_config.ResetGpuDriver)
            {
                ResetGpuDriver();
                gpuReset = true;

                // 3. Soft-freeze rescue: still frozen a second after the reset? Restart DWM.
                if (_config.RestartDwmOnFrozenScreen && IsScreenStillFrozen())
                {
                    RestartDwm();
                    dwmRestarted = true;
                }
            }

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string name = p.ProcessName;
                    int pid = p.Id;
                    if (pid == selfId || pid == 0 || pid == 4) continue;
                    if (DoNotTrim.Contains(name)) continue;

                    if (TrimWorkingSet(pid)) tuned++;

                    bool isForeground = pid == fgPid;
                    bool isShell = ShellApps.Contains(name);
                    if (isForeground || isShell)
                    {
                        if (BoostToHigh(p, pid)) boosted++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"Process {p.Id} skipped: {ex.Message}");
                }
                finally
                {
                    p.Dispose();
                }
            }

            // 3. Deep memory ops (only meaningful with an elevated token).
            if (Native.IsElevated())
            {
                PurgeStandbyList();
                PurgeModifiedPageList();
                FlushFileCache();
            }

            // 4. Performance extras.
            if (_config.PowerPlanBoost && Native.IsElevated())
                BoostPowerPlan();

            // 5. Force-restart the shell last — the classic fix for a frozen taskbar/desktop.
            if (_config.RestartExplorerOnUnfreeze)
            {
                RestartExplorer();
                explorerRestarted = true;
                try { ExplorerRestartedNow?.Invoke(); } catch { }
            }

            ulong availAfter = Native.GetMemoryStatus().ullAvailPhys;
            long freedMb = availAfter > availBefore ? (long)((availAfter - availBefore) / (1024 * 1024)) : 0;

            var stats = new UnfreezeStats(tuned, boosted, freedMb, sw.ElapsedMilliseconds, reason, gpuReset, dwmRestarted, explorerRestarted);
            Log.Info("Unfreeze done: " + stats.Summary);
            try { Completed?.Invoke(stats); } catch (Exception ex) { Log.Error("Completed handler error", ex); }
        }
        catch (Exception ex)
        {
            Log.Error("Unfreeze failed", ex);
        }
        finally
        {
            if (timerPeriod) { try { Native.timeEndPeriod(1); } catch { } }
            try { Thread.CurrentThread.Priority = oldThreadPriority; } catch { }
            try { Process.GetCurrentProcess().PriorityClass = oldPriority; } catch { }
            Interlocked.Exchange(ref _active, 0);
        }
    }

    private static bool TrimWorkingSet(int pid)
    {
        IntPtr h = Native.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, (uint)pid);
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

    private static bool BoostToHigh(Process p, int pid)
    {
        try
        {
            IntPtr h = Native.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION, false, (uint)pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                return SetPriorityClass(h, 0x00000080); // HIGH_PRIORITY_CLASS
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the screen still appears frozen ~1 s after the GPU reset:
    /// the desktop compositor window is unresponsive (IsHungAppWindow) or the
    /// watchdog's latest sample still shows a scheduling stall.
    /// </summary>
    private bool IsScreenStillFrozen()
    {
        Thread.Sleep(1200); // give the GPU reset a moment to bring the display back
        try
        {
            bool hung = IsDwmHung();
            if (hung)
            {
                Log.Info("DWM compositor window is not responding — screen still frozen");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("DWM responsiveness check failed: " + ex.Message);
        }

        // Secondary signal: the system itself is still stalled right now.
        if (_watchdog.LastStallMs >= _config.StallThresholdMs)
        {
            Log.Info($"System still stalled ({_watchdog.LastStallMs} ms) — screen still frozen");
            return true;
        }
        return false;
    }

    /// <summary>
    /// True if the desktop compositor's window is unresponsive. Tries both known DWM
    /// window classes (pre- and post-Win11), then falls back to enumerating top-level
    /// windows owned by the dwm process.
    /// </summary>
    private static bool IsDwmHung()
    {
        IntPtr wnd = Native.FindWindow("DwmNotificationWindow", null);
        if (wnd == IntPtr.Zero) wnd = Native.FindWindow("Dwm", null);
        if (wnd != IntPtr.Zero)
        {
            Log.Debug($"DWM check: hwnd=0x{wnd.ToInt64():X} hung={Native.IsHungAppWindow(wnd)}");
            return Native.IsHungAppWindow(wnd);
        }

        // Fallback: any top-level window owned by the dwm process.
        uint dwmPid = 0;
        try
        {
            using var p = Process.GetProcessesByName("dwm").FirstOrDefault();
            if (p is not null) dwmPid = (uint)p.Id;
        }
        catch { }
        if (dwmPid == 0) return false;

        bool found = false, hung = false;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == dwmPid)
            {
                found = true;
                if (Native.IsHungAppWindow(h)) { hung = true; return false; }
            }
            return true;
        }, IntPtr.Zero);
        Log.Debug($"DWM check (enum): found={found} hung={hung}");
        return found && hung;
    }

    /// <summary>
    /// Restarts the desktop compositor: terminates dwm.exe and lets Windows respawn it.
    /// The screen goes black for ~1–2 s, then the compositor comes back fresh.
    /// </summary>
    private static void RestartDwm()
    {
        Log.Info("Restarting dwm.exe (desktop compositor)");
        foreach (var p in Process.GetProcessesByName("dwm"))
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                Log.Warn("dwm kill: " + ex.Message);
            }
            finally
            {
                p.Dispose();
            }
        }

        // Windows respawns dwm automatically; wait until it is back.
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(500);
            try
            {
                if (Process.GetProcessesByName("dwm").Length > 0)
                {
                    Log.Info("dwm respawned by the system");
                    return;
                }
            }
            catch { /* process list race */ }
        }
        Log.Warn("dwm did not respawn within 5 s");
    }

    /// <summary>Public entry for the tray menu "Restart Explorer" item.</summary>
    public void RestartExplorerNow()
    {
        var t = new Thread(() =>
        {
            RestartExplorer();
            try { ExplorerRestartedNow?.Invoke(); } catch { }
        })
        { IsBackground = true, Name = "Thaw.ExplorerRestart" };
        t.Start();
    }

    /// <summary>Ctrl+Shift+Win+B — the documented Windows graphics-driver reset.</summary>
    private static void ResetGpuDriver()
    {
        try
        {
            Key(0x11, up: false); // Ctrl
            Key(0x10, up: false); // Shift
            Key(0x5B, up: false); // Win
            Key(0x42, up: false); // B
            Key(0x42, up: true);
            Key(0x5B, up: true);
            Key(0x10, up: true);
            Key(0x11, up: true);
            Log.Info("GPU driver reset sent (Ctrl+Shift+Win+B)");
        }
        catch (Exception ex)
        {
            Log.Error("GPU reset failed", ex);
        }
    }

    private static void Key(ushort vk, bool up)
    {
        var input = new Native.INPUT { type = 1 };
        input.U.ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
    }

    private void PurgeModifiedPageList()
    {
        try
        {
            Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
            IntPtr ptr = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(ptr, 2); // MemoryPurgeModifiedPageList
                int status = Native.NtSetSystemInformation(Native.SystemMemoryListInformation, ptr, 4);
                Log.Info($"Modified page list purge: NTSTATUS 0x{status:X8}");
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Modified purge unavailable: " + ex.Message);
        }
    }

    private void PurgeStandbyList()
    {
        try
        {
            Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
            IntPtr ptr = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(ptr, Native.MemoryPurgeStandbyList);
                int status = Native.NtSetSystemInformation(Native.SystemMemoryListInformation, ptr, 4);
                Log.Info($"Standby list purge: NTSTATUS 0x{status:X8}");
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Standby purge unavailable: " + ex.Message);
        }
    }

    private void FlushFileCache()
    {
        try
        {
            Native.EnablePrivilege("SeIncreaseQuotaPrivilege");
            bool ok = Native.SetSystemFileCacheSize((IntPtr)(-1L), (IntPtr)(-1L), 0);
            Log.Info($"System file cache flush: {ok}");
        }
        catch (Exception ex)
        {
            Log.Debug("File cache flush unavailable: " + ex.Message);
        }
    }

    /// <summary>
    /// Force-restarts explorer.exe: kills it, waits, then relaunches it under the
    /// interactive user's token (so it does not come back elevated). Falls back to a
    /// plain start. This is the equivalent of:
    ///   Stop-Process -Name explorer -Force; Start-Sleep 2; Start-Process "$env:windir\explorer.exe"
    /// </summary>
    private void RestartExplorer()
    {
        bool anyKilled = false;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try
            {
                p.Kill();
                if (p.WaitForExit(3000)) anyKilled = true;
            }
            catch (Exception ex)
            {
                Log.Warn("explorer kill: " + ex.Message);
            }
            finally
            {
                p.Dispose();
            }
        }
        Log.Info("explorer killed: " + anyKilled);
        Thread.Sleep(2000);

        if (!TryStartExplorerAsInteractiveUser())
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    UseShellExecute = true,
                });
                Log.Info("explorer started (fallback)");
            }
            catch (Exception ex)
            {
                Log.Error("explorer start failed", ex);
            }
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
                bool ok = Native.CreateProcessAsUser(token, exe, null, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref si, out _);
                if (!ok)
                    Log.Debug("CreateProcessAsUser failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                else
                    Log.Info("explorer started as interactive user");
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

    private void BoostPowerPlan()
    {
        try
        {
            if (!_powerPlanSaved &&
                Native.PowerGetActiveScheme(IntPtr.Zero, out IntPtr guidPtr) == 0)
            {
                _previousPowerPlan = Marshal.PtrToStructure<Guid>(guidPtr);
                Native.LocalFree(guidPtr);
                _powerPlanSaved = true;
            }

            Guid hp = HighPerformanceGuid;
            uint status = Native.PowerSetActiveScheme(IntPtr.Zero, ref hp);
            Log.Info($"Power plan -> High Performance: 0x{status:X8}");

            if (_config.PowerPlanRestoreAfterSeconds > 0)
            {
                var t = new Thread(() =>
                {
                    Thread.Sleep(_config.PowerPlanRestoreAfterSeconds * 1000);
                    if (_powerPlanSaved)
                    {
                        var prev = _previousPowerPlan;
                        Native.PowerSetActiveScheme(IntPtr.Zero, ref prev);
                        Log.Info("Power plan restored to previous scheme");
                    }
                })
                { IsBackground = true, Name = "Thaw.PowerRestore" };
                t.Start();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Power plan boost failed", ex);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    [DllImport("kernel32.dll")]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);
}
