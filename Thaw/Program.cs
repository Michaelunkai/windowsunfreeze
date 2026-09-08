using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Thaw;

internal static class Program
{
    private const string MutexName = "Thaw.InstantUnfreezer.SingleInstance";
    private const string RescueBrokerMutexName = "Thaw.InstantUnfreezer.RescueBroker";
    private const string RestartCommandLine = "--restart";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegisterApplicationRestart(string? commandLine, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int UnregisterApplicationRestart();

    [STAThread]
    private static int Main(string[] args)
    {
        if (HasAnyArg(args, "--help", "-h", "/?"))
        {
            Console.WriteLine(
                "Thaw — Instant Unfreezer\n" +
                "Usage: Thaw.exe [options]\n" +
                "  (no args)   run in the system tray\n" +
                "  --emit-icon <path>  write the app .ico and exit\n" +
                "  --emit-png <dir>    write PNG previews of the icon and exit\n" +
                "  --elevated  skip the single-instance check (used for admin restart)\n" +
                "  --smoke     start, verify core subsystems, exit after a few seconds\n" +
                "  --test-hook [altf4|panic]  drive the real hook callback (recovery actions run)\n" +
                 "  --diagnose  read-only environment/config diagnostics; no recovery actions\n" +
                 "  --self-test  bounded icon/config/recovery-graph checks; no recovery actions\n" +
                 "  --watchdog --parent-pid <pid>  Thaw-only crash relaunch helper\n" +
                 "  --rescue-broker --parent-pid <pid>  Thaw-only rescue broker\n" +
                 "  --rescue-fallback  run one headless bounded force-all recovery\n");
            return 0;
        }

        if (TryGetArg(args, "--emit-icon", out string? iconPath))
        {
            Icons.EmitIconFile(iconPath!);
            Console.WriteLine("Icon written to " + iconPath);
            return 0;
        }

        if (TryGetArg(args, "--emit-png", out string? pngDir))
        {
            Icons.EmitPngPreview(pngDir!);
            Console.WriteLine("PNG previews written to " + pngDir);
            return 0;
        }

        if (HasAnyArg(args, "--diagnose", "--self-test"))
            return RunDiagnostics(HasArg(args, "--self-test"));

        if (HasArg(args, "--rescue-broker"))
            return RunRescueBroker(args);

        if (HasArg(args, "--watchdog"))
            return RunThawWatchdog(args);

        if (HasArg(args, "--rescue-fallback"))
            return RunRescueFallback();

        bool elevatedRestart = HasArg(args, "--elevated");

        if (elevatedRestart)
        {
            // The UAC hand-off is intentionally allowed to overlap for a very short
            // interval while the unelevated instance exits. Normal launches remain
            // strictly single-instance below.
            return RunApp(args);
        }

        if (!TryAcquireMutex(MutexName, out Mutex? mutex))
            return 0; // another Thaw instance is already running
        using Mutex heldMutex = mutex!;
        return RunApp(args);
    }

    private static int RunApp(string[] args)
    {
        ApplicationConfiguration.Initialize();

        Config startupConfig = Config.Load(Config.DefaultPath());
        ConfigureWindowsApplicationRestart(startupConfig, args);
        TryStartThawOnlyHelper(startupConfig, args);

        if (HasArg(args, "--smoke"))
        {
            using var ctx = new AppContext();
            PumpMessagesFor(TimeSpan.FromSeconds(5));
            Log.Info("SMOKE OK — hook+watchdog+tray initialized");
            return 0;
        }

        if (HasArg(args, "--test-hook"))
        {
            string which = TryGetArg(args, "--test-hook", out string? requested)
                && !string.IsNullOrWhiteSpace(requested)
                && !requested.StartsWith("-", StringComparison.Ordinal)
                ? requested.Trim().ToLowerInvariant()
                : "altf4";
            if (which is not ("altf4" or "panic"))
            {
                Console.Error.WriteLine("--test-hook expects altf4 or panic.");
                return 2;
            }
            using var ctx = new AppContext();
            PumpMessagesFor(TimeSpan.FromMilliseconds(2500));
            ctx.RunHookTest(which);
            PumpMessagesFor(TimeSpan.FromSeconds(13)); // allow posted completion receipts and incident history to run
            Log.Info("HOOK TEST DONE (" + which + ")");
            return 0;
        }

        using var app = new AppContext();
        Application.Run(app);
        return 0;
    }

    /// <summary>
    /// Read-only diagnostics for support scripts. This deliberately avoids
    /// Config.DefaultPath (which creates the app-data directory) and does not
    /// construct AppContext, KeyboardHook, Watchdog, or Unfreezer.
    /// </summary>
    private static int RunDiagnostics(bool selfTest)
    {
        int failures = 0;
        Console.WriteLine("Thaw diagnostics (read-only)");
        Console.WriteLine("Version: " + (typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"));
        Console.WriteLine("OS: " + RuntimeInformation.OSDescription);
        Console.WriteLine("Architecture: " + RuntimeInformation.OSArchitecture);
        Console.WriteLine("Process: " + Environment.ProcessId + " | 64-bit: " + Environment.Is64BitProcess);

        bool elevated = false;
        try
        {
            elevated = Native.IsElevated();
            Console.WriteLine("Privileges: " + (elevated ? "Administrator" : "standard user"));
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("Privileges: ERROR — " + ex.GetType().Name + ": " + ex.Message);
        }

        string configPath = ConfigPathWithoutCreating();
        Console.WriteLine("Config path: " + configPath);
        Config config = new();
        if (!File.Exists(configPath))
        {
            Console.WriteLine("Config: absent (built-in defaults will be used)");
        }
        else
        {
            try
            {
                string json = File.ReadAllText(configPath);
                config = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                }) ?? new Config();
                Console.WriteLine("Config: readable JSON");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("Config: ERROR — " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        try
        {
            var panic = config.GetPanicHotkey();
            string modifiers = string.Join("+", new[]
            {
                panic.Alt ? "Alt" : null,
                panic.Ctrl ? "Ctrl" : null,
                panic.Shift ? "Shift" : null,
            }.Where(s => s is not null));
            if (modifiers.Length == 0) modifiers = "none";
            Console.WriteLine($"Hotkeys: Alt+F4 mode={config.AltF4Mode}; panic={config.PanicHotkey}; parsed={modifiers}+VK 0x{panic.Vk:X2}");
            foreach (RecoveryHotkeyBinding binding in config.GetRecoveryHotkeys())
                Console.WriteLine($"Binding: {binding.Action}={binding.Chord.Text} ({binding.Mode})");
            IReadOnlyList<HotkeyValidationIssue> issues = config.ValidateHotkeys();
            Console.WriteLine(issues.Count == 0
                ? "Hotkey validation: PASS"
                : $"Hotkey validation: {issues.Count} correction(s) would be applied");
            IReadOnlyList<ConfigValidationIssue> policyIssues = config.ValidateSettings();
            Console.WriteLine(policyIssues.Count == 0
                ? "Config policy validation: PASS"
                : $"Config policy validation: {policyIssues.Count} bounded correction(s) would be applied");
            Console.WriteLine($"Watchdog: auto-unfreeze={config.AutoUnfreezeOnStall}; stress threshold={config.StallThresholdMs} ms; hard stall={config.HardStallMs} ms");
            Console.WriteLine($"Recovery options: GPU reset={config.ResetGpuDriver}; DWM rescue={config.RestartDwmOnFrozenScreen}; Explorer restart={config.RestartExplorerOnUnfreeze}; temporary power-plan boost={config.PowerPlanBoost}");
            Console.WriteLine($"Feedback: overlay={config.ShowCaptureOverlay}; immediate balloon={config.ShowImmediateCaptureFeedback}; sound={config.PlayRecoverySound}; overlay duration={config.GetCaptureOverlayDurationMs()} ms");
            Console.WriteLine($"Incident history: enabled={config.KeepIncidentHistory}; limit={config.GetIncidentHistoryLimit()}; export={config.EnableSupportBundleExport}");
            Console.WriteLine($"Lifecycle: Windows restart={config.EnableWindowsApplicationRestart}; Thaw watchdog={config.ThawWatchdogMode}; rescue broker={config.RescueBrokerMode}");
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("Hotkey/config checks: ERROR — " + ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            var memory = Native.GetMemoryStatus();
            Console.WriteLine($"Live read-only sample: RAM load={memory.dwMemoryLoad}%");
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("Live read-only sample: ERROR — " + ex.GetType().Name + ": " + ex.Message);
        }

        if (selfTest)
        {
            try
            {
                using Icon normal = Icons.CreateTrayIcon(alert: false);
                using Icon alert = Icons.CreateTrayIcon(alert: true);
                Console.WriteLine("Self-test: PASS — normal and alert tray icons render in memory");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("Self-test: ERROR — icon rendering failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                var defaults = new Config();
                IReadOnlyList<RecoveryHotkeyBinding> bindings = defaults.GetRecoveryHotkeys();
                bool unique = bindings.Select(b => b.Chord.Signature).Distinct(StringComparer.OrdinalIgnoreCase).Count() == bindings.Count;
                bool safeDefaults = defaults.AltF4Mode == Config.DefaultAltF4Mode &&
                                    !defaults.AutoUnfreezeOnStall &&
                                    !defaults.PowerPlanBoost &&
                                    !defaults.RestartExplorerOnUnfreeze &&
                                    !defaults.RestartDwmOnFrozenScreen;
                if (bindings.Count != 3 || !unique || defaults.ValidateHotkeys().Count != 0 || !safeDefaults)
                    throw new InvalidOperationException("default hotkey or safety policy invariant failed");
                if (!defaults.IsCompatibilitySafe || defaults.GetCaptureOverlayDurationMs() != 1200 ||
                    defaults.GetIncidentHistoryLimit() != 100 || defaults.GetWatchdogHeartbeatSeconds() != 15 ||
                    defaults.ShouldRunThawWatchdog || !defaults.ShouldRunRescueBroker)
                    throw new InvalidOperationException("default lifecycle/feedback policy invariant failed");
                IReadOnlyList<RecoveryHotkeyBinding> fallbackBindings = defaults.GetAlwaysFallbackHotkeys();
                if (fallbackBindings.Count != 3 ||
                    !fallbackBindings.Any(binding => binding.Action == RecoveryHotkeyAction.AltF4) ||
                    fallbackBindings.Select(binding => binding.Chord.Signature)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() != fallbackBindings.Count)
                    throw new InvalidOperationException("registered fallback hotkey invariant failed");
                Console.WriteLine("Self-test: PASS — three unique recovery chords, compatibility defaults, and bounded lifecycle policy validated");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("Self-test: ERROR — config/hotkey invariants failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                int actualInputSize = Marshal.SizeOf<Native.INPUT>();
                int expectedInputSize = Environment.Is64BitProcess ? 40 : 28;
                if (actualInputSize != expectedInputSize)
                    throw new InvalidOperationException($"native INPUT size is {actualInputSize}, expected {expectedInputSize}");
                Console.WriteLine($"Self-test: PASS — native INPUT layout is {actualInputSize} bytes");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("Self-test: ERROR — native input layout invalid: " + ex.Message);
            }

            try
            {
                bool captured = NativeDiagnostics.TryGetDwmTiming(out NativeDiagnostics.DwmTimingSample timing);
                if (!captured ||
                    !timing.Supported || timing.RefreshRateHz <= 0)
                    throw new InvalidOperationException(
                        $"DWM composition timing is unavailable (HRESULT=0x{timing.HResult:X8}, " +
                        $"failure={timing.Failure ?? "none"}, native-size={Marshal.SizeOf<Native.DWM_TIMING_INFO>()})");
                Thread.Sleep(100);
                if (!NativeDiagnostics.TryGetDwmTiming(out NativeDiagnostics.DwmTimingSample timingAfter))
                    throw new InvalidOperationException("DWM second composition timing sample failed");
                NativeDiagnostics.DwmProgressComparison progress =
                    NativeDiagnostics.CompareDwmProgress(timing, timingAfter, 100);
                if (!progress.Progressed)
                    throw new InvalidOperationException("DWM timing counters did not advance across two samples");
                Console.WriteLine($"Self-test: PASS — DWM composition timing {timing.RefreshRateHz:0.##} Hz, " +
                                  $"composed={progress.CompositionFramesDelta}, vblank={progress.VBlankProgressed}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("Self-test: ERROR — DWM timing probe failed: " + ex.Message);
            }

            try
            {
                using var broker = new RescueBroker();
                if (!broker.IsAvailable || string.IsNullOrWhiteSpace(broker.EventName))
                    throw new InvalidOperationException("per-user named rescue event unavailable");

                string selfTestEvent = RescueBroker.EventNamePrefix + "self-test-" + Guid.NewGuid().ToString("N");
                using var channel = new RescueBroker(selfTestEvent);
                if (!channel.IsAvailable || !channel.TrySignalFast("self-test") ||
                    !channel.Wait(0, out RescueSignal signal) || signal.Sequence == 0 ||
                    !channel.TryAcknowledgeFast(signal.Sequence) || !channel.WaitForAcknowledgement(0))
                    throw new InvalidOperationException("rescue signal/acknowledgement channel did not round-trip");
                if (!channel.TryAcknowledgeFast() || channel.WaitForAcknowledgement(0))
                    throw new InvalidOperationException("duplicate rescue acknowledgement produced a stale pulse");
                if (!channel.TrySignalFast("self-test-older", out long olderSequence) ||
                    !channel.Wait(0, out RescueSignal olderSignal) ||
                    olderSignal.Sequence != olderSequence ||
                    !channel.TrySignalFast("self-test-newer", out long newerSequence) ||
                    !channel.Wait(0, out RescueSignal newerSignal) ||
                    newerSignal.Sequence != newerSequence || newerSequence <= olderSequence ||
                    !channel.TryAcknowledgeFast(newerSequence) ||
                    !channel.WaitForAcknowledgement(0) ||
                    !channel.TryAcknowledgeFast(olderSequence) ||
                    channel.WaitForAcknowledgement(0))
                    throw new InvalidOperationException("out-of-order rescue acknowledgement was not sequence-gated");
                Console.WriteLine("Self-test: PASS — per-user rescue event and acknowledgement channel round-trip");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("Self-test: ERROR — rescue event probe failed: " + ex.Message);
            }

            try
            {
                var altF4 = Unfreezer.GetRecoveryProfile(TriggerReason.Hotkey);
                if (!altF4.Display || !altF4.System || !altF4.Memory)
                    throw new InvalidOperationException("Alt+F4 does not enable every recovery tier");
                if (!Unfreezer.IsForceAll(TriggerReason.Hotkey) || Unfreezer.IsForceAll(TriggerReason.Panic))
                    throw new InvalidOperationException("Alt+F4 is not the unique force-all trigger");
                if (!Unfreezer.DefersExpensivePreDispatchProbes(TriggerReason.Hotkey) ||
                    Unfreezer.DefersExpensivePreDispatchProbes(TriggerReason.Panic))
                    throw new InvalidOperationException("force-all pre-dispatch probe deferral invariant failed");
                if (Unfreezer.DisplayProbeDelayMs >= Unfreezer.AltF4ActivationDeadlineMs)
                    throw new InvalidOperationException("display activation barrier is not sub-second");
                Console.WriteLine($"Self-test: PASS — Alt+F4 force-all enables every tier with a {Unfreezer.DisplayProbeDelayMs} ms display barrier");

                IReadOnlyList<string> forceActions = RecoveryCoordinator.SelectCauseDirectedActions(
                    TriggerReason.Hotkey,
                    HealthCause.ResourcePressure,
                    forceAll: true,
                    memoryPressure: true,
                    foregroundHung: true,
                    dwmHung: true,
                    explorerHung: true);
                string[] requiredActions =
                {
                    "desktop-refresh", "dwm-mmcss", "foreground-probe", "resource-diagnostics",
                    "audio-diagnostic", "dns-refresh", "memory-cache",
                };
                if (forceActions.Count > RecoveryCoordinator.MaxActionWorkers ||
                    requiredActions.Any(name => !forceActions.Contains(name, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        $"force-all action graph exceeds the worker capacity or omits a required action ({forceActions.Count}/{RecoveryCoordinator.MaxActionWorkers})");
                Console.WriteLine($"Self-test: PASS — Alt+F4 action graph contains {forceActions.Count} actions within {RecoveryCoordinator.MaxActionWorkers} pre-warmed workers");

                IReadOnlyList<string> automaticActions = RecoveryCoordinator.SelectCauseDirectedActions(
                    TriggerReason.Auto,
                    HealthCause.ResourcePressure,
                    forceAll: false,
                    memoryPressure: true,
                    foregroundHung: true,
                    dwmHung: true,
                    explorerHung: true);
                if (automaticActions.Any(name => name is not "resource-diagnostics" and not "foreground-probe"))
                    throw new InvalidOperationException("automatic recovery selected a mutating action");
                Console.WriteLine("Self-test: PASS — automatic stall recovery is diagnostics-only");

                var rollbackJournal = new RollbackJournal();
                bool rollbackAttempted = false;
                string rollbackDetail = string.Empty;
                rollbackJournal.RecordResult("self-test-restore", () =>
                {
                    rollbackAttempted = true;
                    return false;
                });
                rollbackJournal.RollbackAll((_, started, detail) =>
                {
                    if (!started) rollbackDetail = detail;
                });
                if (!rollbackAttempted || !rollbackDetail.StartsWith("rollback failed", StringComparison.Ordinal))
                    throw new InvalidOperationException("rollback failure receipt invariant failed");
                Console.WriteLine("Self-test: PASS — failed native restore is surfaced in rollback receipts");

                using var coordinator = new RecoveryCoordinator();
                var noOpActions = Enumerable.Range(0, RecoveryCoordinator.MaxActionWorkers)
                    .Select(index => new RecoveryActionSpec(
                        "self-test-action-" + index,
                        500,
                        _ => RecoveryActionResult.Unchanged("self-test")))
                    .ToArray();
                var dispatch = coordinator.Dispatch(
                    Guid.NewGuid(),
                    TriggerReason.Manual,
                    HealthCause.Healthy,
                    forceAll: true,
                    Environment.TickCount64 + 2_000,
                    new RollbackJournal(),
                    noOpActions);
                bool dispatchComplete = coordinator.Wait(dispatch, Environment.TickCount64 + 1_500);
                int completedActions = dispatch.Receipts.Count(receipt =>
                    receipt.Phase == RecoveryReceiptPhase.Completed);
                if (!dispatchComplete || !dispatch.IsDrained || completedActions != noOpActions.Length)
                    throw new InvalidOperationException(
                        $"pre-warmed dispatch did not complete every action ({completedActions}/{noOpActions.Length})");
                Console.WriteLine($"Self-test: PASS — {completedActions} concurrent action slots completed and drained");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("Self-test: ERROR — Alt+F4 combined profile invalid: " + ex.Message);
            }
        }

        Console.WriteLine("Safety: no hook installed, no GPU reset sent, no process enumerated/terminated, no cache purged, and no power plan changed.");
        Console.WriteLine(failures == 0 ? "Result: PASS" : $"Result: FAIL ({failures} check(s))");
        return failures == 0 ? 0 : 2;
    }

    private static bool TryAcquireMutex(string name, out Mutex? mutex)
    {
        mutex = null;
        try
        {
            mutex = new Mutex(true, name, out bool createdNew);
            if (createdNew) return true;
            mutex.Dispose();
            mutex = null;
            return false;
        }
        catch (AbandonedMutexException)
        {
            // The caller owns an abandoned mutex after this exception. Treat it as
            // a clean recovery path rather than starting a duplicate instance.
            return mutex is not null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unable to acquire Thaw mutex '{name}': {ex.Message}");
            mutex?.Dispose();
            mutex = null;
            return false;
        }
    }

    private static void ConfigureWindowsApplicationRestart(Config config, string[] args)
    {
        if (HasAnyArg(args, "--watchdog", "--rescue-broker")) return;
        try
        {
            int hr = config.EnableWindowsApplicationRestart
                ? RegisterApplicationRestart(RestartCommandLine, 0)
                : UnregisterApplicationRestart();
            Log.Info($"Windows Application Restart: enabled={config.EnableWindowsApplicationRestart}, hresult=0x{hr:X8}");
        }
        catch (Exception ex)
        {
            // Older Windows versions and non-Windows test hosts may not expose the
            // API. Registration is best effort and never blocks tray startup.
            Log.Warn("Windows Application Restart unavailable: " + ex.Message);
        }
    }

    private static void TryStartThawOnlyHelper(Config config, string[] args)
    {
        if (HasAnyArg(args,
                "--broker-child", "--watchdog-child", "--watchdog", "--rescue-broker",
                "--smoke", "--test-hook"))
            return;

        string? helperMode = config.ShouldRunRescueBroker
            ? "--rescue-broker"
            : config.ShouldRunThawWatchdog
                ? "--watchdog"
                : null;
        if (helperMode is null) return;

        string? exe = GetThawExecutablePath();
        if (string.IsNullOrWhiteSpace(exe))
        {
            Log.Warn("Thaw-only helper not started: the Thaw executable path is unavailable");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add(helperMode);
            psi.ArgumentList.Add("--parent-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("--broker-child");
            Process.Start(psi);
            Log.Info($"Thaw-only helper started: mode={helperMode}, parent={Environment.ProcessId}");
        }
        catch (Exception ex)
        {
            Log.Warn("Thaw-only helper start failed: " + ex.Message);
        }
    }

    private static int RunRescueBroker(string[] args) => RunThawOnlyMonitor(args, rescueBroker: true);

    private static int RunThawWatchdog(string[] args) => RunThawOnlyMonitor(args, rescueBroker: false);

    /// <summary>
    /// Headless recovery invoked only by the separate rescue helper after the
    /// main process failed to acknowledge a captured always-on emergency chord.
    /// It never creates a tray, hook, helper, or second single-instance owner.
    /// </summary>
    private static int RunRescueFallback()
    {
        Config config = new();
        try { config = Config.Load(ConfigPathWithoutCreating()); }
        catch (Exception ex) { Console.Error.WriteLine("Rescue fallback config load failed: " + ex.Message); }

        try
        {
            try { Log.Init(config.DebugLog); } catch { }
            using var watchdog = new Watchdog(config);
            using var unfreezer = new Unfreezer(config, watchdog);
            using var completed = new ManualResetEventSlim(false);
            UnfreezeStats? result = null;
            unfreezer.Completed += stats =>
            {
                result = stats;
                completed.Set();
            };

            Console.WriteLine("Thaw rescue fallback: starting bounded force-all recovery.");
            if (!unfreezer.Trigger(TriggerReason.Hotkey))
            {
                Console.Error.WriteLine("Thaw rescue fallback: full recovery handoff unavailable.");
                return RunMinimalRescueFallback("full recovery handoff unavailable");
            }

            if (!completed.Wait(Unfreezer.RecoveryBudgetMs + 5_000))
            {
                Console.Error.WriteLine("Thaw rescue fallback: bounded recovery did not report completion.");
                // A full-engine worker can still be stranded by an OS/native
                // boundary even though its own action deadlines have elapsed.
                // Do not let that terminal wait become the end of rescue: submit
                // the allocation-light graphics reset before the helper exits.
                return RunMinimalRescueFallback("full recovery completion timeout");
            }

            if (result is null)
                return RunMinimalRescueFallback("full recovery returned no receipt");

            Console.WriteLine("Thaw rescue fallback: " + result.Summary);
            if (result.OverallOutcome == RecoveryOutcome.Worse)
                return RunMinimalRescueFallback("full recovery reported measured degradation");
            return 0;
        }
        catch (Exception ex)
        {
            try { Log.Error("Thaw rescue fallback full engine failed", ex); } catch { }
            return RunMinimalRescueFallback("full engine startup failed");
        }
    }

    private static int RunMinimalRescueFallback(string reason)
    {
        try
        {
            Console.WriteLine("Thaw rescue fallback: " + reason + "; submitting minimal graphics reset.");
            bool accepted = Unfreezer.TryEmergencyDisplayReset();
            Console.WriteLine("Thaw rescue fallback: minimal graphics reset " +
                              (accepted ? "accepted." : "unavailable."));
            return accepted ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Thaw rescue fallback: minimal graphics reset failed: " + ex.Message);
            return 3;
        }
    }

    private sealed class RescueFallbackState
    {
        internal long LastRequestTick = long.MinValue;
        internal int FallbackInFlight;
    }

    private static int RunThawOnlyMonitor(string[] args, bool rescueBroker)
    {
        if (!TryGetIntArg(args, "--parent-pid", out int parentPid) || parentPid <= 0 || parentPid == Environment.ProcessId)
        {
            Console.Error.WriteLine("A valid --parent-pid is required for a Thaw-only helper.");
            return 2;
        }

        string mutexName = rescueBroker ? RescueBrokerMutexName : RescueBrokerMutexName + ".Watchdog";
        if (!TryAcquireMutex(mutexName, out Mutex? mutex)) return 0;
        using (Mutex heldMutex = mutex!)
        {
            Process? parent = null;
            try
            {
                parent = Process.GetProcessById(parentPid);
                if (!IsThawProcess(parent))
                {
                    Console.Error.WriteLine("Refusing to monitor a process that is not Thaw.exe.");
                    return 3;
                }

                if (rescueBroker)
                    return RunRescueBrokerMonitor(parent);

                Console.WriteLine($"Thaw-only {(rescueBroker ? "rescue broker" : "watchdog")} monitoring PID {parentPid}.");
                parent.WaitForExit();
                int exitCode = 0;
                try { exitCode = parent.ExitCode; } catch { exitCode = -1; }
                if (exitCode == 0)
                {
                    Console.WriteLine("Parent exited cleanly; no relaunch requested.");
                    return 0;
                }

                Config config = Config.Load(ConfigPathWithoutCreating());
                bool relaunchAllowed = rescueBroker ? config.ShouldRunRescueBroker : config.ShouldRunThawWatchdog;
                if (!relaunchAllowed)
                {
                    Console.WriteLine("Relaunch policy is off; no process was started.");
                    return 0;
                }

                Thread.Sleep(config.GetWatchdogRelaunchDelaySeconds() * 1000);
                return RelaunchThawOnly();
            }
            catch (ArgumentException)
            {
                Console.WriteLine("Parent already exited; no relaunch requested.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Thaw-only helper failed: {ex.Message}");
                return 3;
            }
            finally
            {
                parent?.Dispose();
            }
        }
    }

    private static int RunRescueBrokerMonitor(Process parent)
    {
        Config config = Config.Load(ConfigPathWithoutCreating());
        try { Log.Init(config.DebugLog); } catch { }

        using var broker = new RescueBroker();
        broker.DrainAcknowledgements();
        var state = new RescueFallbackState();
        RescueHotkeyMonitor? hotkeys = null;
        Action<RecoveryHotkeyBinding> rescueHotkeyHandler = binding =>
            HandleRescueRequest(parent, broker, state, "registered:" + binding.Chord.Text);
        long lastHotkeyMonitorRestartTick = Environment.TickCount64;
        try
        {
            hotkeys = new RescueHotkeyMonitor(config, rescueHotkeyHandler);
        }
        catch (Exception ex)
        {
            // Keep the named-event listener alive even if the optional
            // RegisterHotKey thread cannot be created on this host.
            Console.Error.WriteLine("Rescue registered-hotkey path unavailable: " + ex.Message);
        }

        Console.WriteLine($"Thaw-only rescue broker monitoring PID {parent.Id}; " +
                          $"registered hotkeys={hotkeys?.RegisteredCount ?? 0}; event={broker.IsAvailable}.");

        try
        {
            while (true)
            {
                bool exited;
                try { exited = parent.WaitForExit(100); }
                catch { exited = true; }
                if (exited) break;

                long now = Environment.TickCount64;
                if ((hotkeys is null || !hotkeys.IsAlive) &&
                    now - lastHotkeyMonitorRestartTick >= 2_000)
                {
                    lastHotkeyMonitorRestartTick = now;
                    try { hotkeys?.Dispose(); } catch { }
                    try
                    {
                        hotkeys = new RescueHotkeyMonitor(
                            Config.Load(ConfigPathWithoutCreating()), rescueHotkeyHandler);
                        Console.WriteLine("Rescue hotkey monitor restarted after thread exit.");
                    }
                    catch (Exception ex)
                    {
                        hotkeys = null;
                        Console.Error.WriteLine("Rescue hotkey monitor restart failed: " + ex.Message);
                    }
                }

                try { hotkeys?.RefreshConfigurationIfChanged(); } catch { }

                if (broker.Wait(100, out RescueSignal signal))
                    HandleRescueRequest(parent, broker, state, "event:" + signal.Reason);
            }
        }
        finally
        {
            try { hotkeys?.Dispose(); } catch { }
        }

        int exitCode = 0;
        try { exitCode = parent.ExitCode; } catch { exitCode = -1; }
        if (exitCode == 0)
        {
            Console.WriteLine("Parent exited cleanly; no relaunch requested.");
            return 0;
        }

        bool relaunchAllowed = false;
        try { relaunchAllowed = Config.Load(ConfigPathWithoutCreating()).ShouldRunRescueBroker; }
        catch { }
        if (!relaunchAllowed)
        {
            Console.WriteLine("Rescue-broker relaunch policy is off; no process was started.");
            return 0;
        }

        Thread.Sleep(config.GetWatchdogRelaunchDelaySeconds() * 1000);
        return RelaunchThawOnly();
    }

    private static void HandleRescueRequest(
        Process parent,
        RescueBroker broker,
        RescueFallbackState state,
        string source)
    {
        long now = Environment.TickCount64;
        while (true)
        {
            long previous = Interlocked.Read(ref state.LastRequestTick);
            if (previous != long.MinValue && now - previous < 2_000)
                return;
            if (Interlocked.CompareExchange(ref state.LastRequestTick, now, previous) == previous)
                break;
        }

        // The normal hook acknowledges as soon as its request is in the
        // pre-warmed dispatch ring. Do not start a duplicate recovery when that
        // in-process path is alive; only an absent acknowledgement escalates.
        if (broker.WaitForAcknowledgement(1_500))
        {
            Console.WriteLine("Rescue request acknowledged by main process (" + source + ").");
            return;
        }

        try
        {
            if (parent.HasExited)
            {
                int exitCode;
                try { exitCode = parent.ExitCode; }
                catch { return; }
                if (exitCode == 0)
                {
                    // A cleanly closed parent cannot have a live recovery run
                    // that needs rescue escalation. The monitor's normal
                    // shutdown path remains responsible for clean exits.
                    return;
                }

                Console.WriteLine("Main Thaw process exited with code " + exitCode +
                                  "; escalating the captured rescue request immediately.");
            }
        }
        catch { return; }

        if (Interlocked.CompareExchange(ref state.FallbackInFlight, 1, 0) != 0)
            return;

        string? exe = GetThawExecutablePath();
        if (string.IsNullOrWhiteSpace(exe))
        {
            Interlocked.Exchange(ref state.FallbackInFlight, 0);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("--rescue-fallback");
            Process? child = Process.Start(psi);
            if (child is null)
            {
                Interlocked.Exchange(ref state.FallbackInFlight, 0);
                return;
            }

            child.EnableRaisingEvents = true;
            child.Exited += (_, _) =>
            {
                try { child.Dispose(); } catch { }
                Interlocked.Exchange(ref state.FallbackInFlight, 0);
            };
            Console.WriteLine("Rescue fallback started after missing acknowledgement (" + source + ").");
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref state.FallbackInFlight, 0);
            Console.Error.WriteLine("Rescue fallback start failed: " + ex.Message);
        }
    }

    private static int RelaunchThawOnly()
    {
        string? exe = GetThawExecutablePath();
        if (string.IsNullOrWhiteSpace(exe)) return 3;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("--restart");
            psi.ArgumentList.Add("--broker-child");
            Process.Start(psi);
            Console.WriteLine("Relaunched Thaw.exe only.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Thaw-only relaunch failed: " + ex.Message);
            return 3;
        }
    }

    private static bool IsThawProcess(Process process)
    {
        string? expected = GetThawExecutablePath();
        if (string.IsNullOrWhiteSpace(expected)) return false;
        try
        {
            string? actual = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(actual) &&
                   string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? GetThawExecutablePath()
    {
        string? current = null;
        try
        {
            current = Environment.ProcessPath;
            if (IsThawExecutablePath(current)) return current;
        }
        catch { }

        try
        {
            string candidate = Path.Combine(System.AppContext.BaseDirectory, "Thaw.exe");
            if (File.Exists(candidate)) return candidate;
        }
        catch { }

        return current;
    }

    private static bool IsThawExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string name = Path.GetFileNameWithoutExtension(path);
        return string.Equals(name, "Thaw", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Thaw-Portable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetIntArg(string[] args, string name, out int value)
    {
        value = 0;
        return TryGetArg(args, name, out string? raw) && int.TryParse(raw, out value);
    }

    private static string ConfigPathWithoutCreating()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Thaw");
        return Path.Combine(dir, "config.json");
    }

    private static bool HasArg(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static void PumpMessagesFor(TimeSpan duration)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            Application.DoEvents();
            Thread.Sleep(25);
        }
    }

    private static bool HasAnyArg(string[] args, params string[] names) =>
        names.Any(name => HasArg(args, name));

    private static bool TryGetArg(string[] args, string name, out string? value)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length)
        {
            value = args[i + 1];
            return true;
        }
        value = null;
        return false;
    }
}
