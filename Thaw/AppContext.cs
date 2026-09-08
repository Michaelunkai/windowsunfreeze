using System.Diagnostics;
using System.IO.Compression;
using System.Media;
using System.Text;
using System.Text.Json;

namespace Thaw;

/// <summary>
/// Owns the tray icon, the hidden host form (message pump), the keyboard hook,
/// the watchdog and the unfreezer. Lives for the whole process.
/// </summary>
internal sealed class AppContext : ApplicationContext
{
    private readonly Config _config;
    private readonly Watchdog _watchdog;
    private readonly Unfreezer _unfreezer;
    private readonly KeyboardHook _hook;
    private readonly NotifyIcon _tray;
    private readonly Icon _iconNormal;
    private readonly Icon _iconAlert;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _recoveryStatusItem;
    private readonly ToolStripMenuItem _unfreezeItem;
    private readonly ToolStripMenuItem _strongRecoveryItem;
    private readonly ToolStripMenuItem _emergencyRecoveryItem;
    private readonly ToolStripMenuItem _panicItem;
    private readonly ToolStripMenuItem _adminItem;
    private readonly ToolStripMenuItem _historyItem;
    private readonly ToolStripMenuItem _exportBundleItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly string _configPath;
    private readonly SynchronizationContext _ui;
    private readonly bool _isElevated;

    // The engine is deliberately asynchronous. Keep a small UI-side guard so a
    // held/repeated key, a double-click, and an auto trigger cannot queue a storm
    // of equally expensive recovery passes. The panic action still bypasses the
    // cooldown (but cannot overlap an already-running pass).
    private const long RecoveryCooldownMs = 3500;
    private const long RecoveryWatchdogTimeoutMs = 60_000;
    private const long ShellRecoveryCooldownMs = 6000;
    private const long ShellRecoveryWatchdogTimeoutMs = 30_000;
    private int _recoveryInProgress;
    private int _shellRecoveryInProgress;
    private long _lastRecoveryRequestTick = long.MinValue;
    private long _lastShellRecoveryRequestTick = long.MinValue;
    private int _lastRecoveryReason = (int)TriggerReason.Manual;
    private string _lastRecoveryOutcome = "Ready — no recovery has run yet.";
    private readonly IncidentHistoryStore _incidentHistory;
    private FeedbackOverlay? _captureOverlay;
    private long _lastCaptureFeedbackTick = long.MinValue;

    public AppContext()
    {
        _configPath = Config.DefaultPath();
        _config = Config.Load(_configPath);
        Log.Init(_config.DebugLog);
        _incidentHistory = new IncidentHistoryStore(_config.GetIncidentHistoryLimit());
        foreach (ConfigValidationIssue issue in _config.ValidateSettings())
            Log.Warn($"Config policy: {issue.SettingName}: {issue.Message}; effective={issue.EffectiveValue}");
        _isElevated = Native.IsElevated();

        // Artwork.
        _iconNormal = Icons.CreateTrayIcon(alert: false);
        _iconAlert = Icons.CreateTrayIcon(alert: true);

        // No host window needed: the message loop comes from Application.Run(context),
        // NotifyIcon owns its own hidden window, and both low-level hook message loops
        // run independently of the WinForms UI thread.

        _watchdog = new Watchdog(_config);
        _unfreezer = new Unfreezer(_config, _watchdog);

        _hook = new KeyboardHook(
            _config,
            shouldAllowStressed: () => _watchdog.Stressed || _watchdog.RecentStall);
        _hook.RecoveryActionRequested += OnRecoveryHotkeyRequested;

        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _watchdog.HardStallDetected += () =>
        {
            if (_config.AutoUnfreezeOnStall) RequestUnfreeze(TriggerReason.Auto);
        };
        _unfreezer.Completed += stats =>
        {
            // Release the retry fence before optional UI marshaling. If the
            // message loop is shutting down, a failed Post must not suppress
            // the next emergency shortcut for the stale-guard interval.
            Volatile.Write(ref _recoveryInProgress, 0);
            try { _ui.Post(_ => OnUnfreezeCompleted(stats), null); }
            catch (Exception ex)
            {
                _lastRecoveryOutcome = "Recovery completed; tray update unavailable.";
                Log.Error("Recovery completion UI post failed", ex);
            }
        };
        _unfreezer.ExplorerRestartedNow += () =>
        {
            MarkShellRecoveryCompleted();
            try { _ui.Post(_ => ReAddTrayIcon(), null); }
            catch (Exception ex) { Log.Error("Explorer completion UI post failed", ex); }
        };

        // Tray.
        _tray = new NotifyIcon
        {
            Icon = _iconNormal,
            Text = "Thaw — starting…",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => RequestUnfreeze(TriggerReason.Manual);

        var menu = BuildMenu();
        _menu = menu.Menu;
        _statusItem = menu.Status;
        _recoveryStatusItem = menu.RecoveryStatus;
        _unfreezeItem = menu.Unfreeze;
        _strongRecoveryItem = menu.StrongRecovery;
        _emergencyRecoveryItem = menu.EmergencyRecovery;
        _panicItem = menu.Panic;
        _adminItem = menu.Admin;
        _historyItem = menu.History;
        _exportBundleItem = menu.ExportBundle;
        _autostartItem = menu.Autostart;
        _tray.ContextMenuStrip = _menu;

        _uiTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _uiTimer.Tick += (_, _) => RefreshTray();
        _uiTimer.Start();

        Application.ApplicationExit += (_, _) => Cleanup();
        ThreadExit += (_, _) => Cleanup();

        _watchdog.Start();

        Log.Info("Thaw running. Alt+F4 mode: " + _config.AltF4Mode + " | panic: " + _config.PanicHotkey +
                 " | slow: " + _config.SlownessHotkey + " (" + _config.SlownessHotkeyMode + ")" +
                 " | frame: " + _config.FrameDropHotkey + " (" + _config.FrameDropHotkeyMode + ")" +
                 " | elevated: " + _isElevated + " | autostart: " + Autostart.IsEnabled());
        RefreshTray();
    }

    /// <summary>Dev/test: drives the hook's real callback with synthetic input.</summary>
    public void RunHookTest(string which)
    {
        if (which == "panic")
            _hook.SimulatePanicForTest();
        else
            _hook.SimulateAltF4ForTest();
    }

    // ------------------------------------------------------------------
    // Tray UI
    // ------------------------------------------------------------------

    private (ContextMenuStrip Menu, ToolStripMenuItem Status, ToolStripMenuItem RecoveryStatus,
        ToolStripMenuItem Unfreeze, ToolStripMenuItem StrongRecovery, ToolStripMenuItem EmergencyRecovery,
        ToolStripMenuItem Panic, ToolStripMenuItem Admin, ToolStripMenuItem History,
        ToolStripMenuItem ExportBundle, ToolStripMenuItem Autostart) BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var unfreezeItem = new ToolStripMenuItem("⚡ Normal recovery — configured sequence")
        {
            Font = new Font(menu.Font, FontStyle.Bold),
            ToolTipText = "Runs the configured bounded display, memory, priority, shell, and resource recovery steps.",
        };
        unfreezeItem.Click += (_, _) => RequestUnfreeze(TriggerReason.Manual);

        var strongRecoveryItem = new ToolStripMenuItem("🧯 Strong recovery — restart Explorer shell")
        {
            ToolTipText = "Restarts explorer.exe; the taskbar and File Explorer windows briefly disappear.",
        };
        strongRecoveryItem.Click += (_, _) => ConfirmShellRecovery();

        var emergencyRecoveryItem = new ToolStripMenuItem("🚨 Emergency recovery — panic sequence")
        {
            ToolTipText = "Same recovery sequence as the panic hotkey; bypasses the normal cooldown.",
        };
        emergencyRecoveryItem.Click += (_, _) => RequestUnfreeze(TriggerReason.Panic, bypassCooldown: true);

        var statusItem = new ToolStripMenuItem("Status: …") { Enabled = false };
        var recoveryStatusItem = new ToolStripMenuItem("Recovery: ready") { Enabled = false };
        var adminItem = new ToolStripMenuItem("Privileges: checking…") { Enabled = false };

        var altF4Mode = new ToolStripMenuItem("Alt+F4 behaviour");
        var modeAlways = new ToolStripMenuItem("Always unfreeze (recommended)");
        var modeStressed = new ToolStripMenuItem("Unfreeze only when stressed");
        var modeOff = new ToolStripMenuItem("Disabled (panic hotkey still works)");
        string current = _config.AltF4Mode;
        modeAlways.Checked = current == "always";
        modeStressed.Checked = current != "always" && current != "off";
        modeOff.Checked = current == "off";
        modeAlways.Click += (_, _) => SetAltF4Mode("always", modeStressed, modeAlways, modeOff);
        modeStressed.Click += (_, _) => SetAltF4Mode("stressed", modeStressed, modeAlways, modeOff);
        modeOff.Click += (_, _) => SetAltF4Mode("off", modeStressed, modeAlways, modeOff);
        altF4Mode.DropDownItems.AddRange(new ToolStripItem[] { modeStressed, modeAlways, modeOff });

        var panicItem = new ToolStripMenuItem($"Panic hotkey: {_config.PanicHotkey} (always works)") { Enabled = false };
        var slownessItem = new ToolStripMenuItem($"Slowdown hotkey: {_config.SlownessHotkey} ({_config.SlownessHotkeyMode})") { Enabled = false };
        var frameDropItem = new ToolStripMenuItem($"Frame-drop hotkey: {_config.FrameDropHotkey} ({_config.FrameDropHotkeyMode})") { Enabled = false };

        var autostartItem = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsEnabled() };
        autostartItem.Click += (_, _) =>
        {
            autostartItem.Checked = !autostartItem.Checked;
            Autostart.SetEnabled(autostartItem.Checked);
            Log.Info("Autostart -> " + autostartItem.Checked);
        };

        var openConfig = new ToolStripMenuItem("Open Config File");
        openConfig.Click += (_, _) => OpenConfig();

        var reload = new ToolStripMenuItem("Reload Config");
        reload.Click += (_, _) =>
        {
            var fresh = Config.Load(_configPath);
            _config.AltF4Mode = fresh.AltF4Mode;
            _config.PanicHotkey = fresh.PanicHotkey;
            _config.SlownessHotkey = fresh.SlownessHotkey;
            _config.SlownessHotkeyMode = fresh.SlownessHotkeyMode;
            _config.FrameDropHotkey = fresh.FrameDropHotkey;
            _config.FrameDropHotkeyMode = fresh.FrameDropHotkeyMode;
            _config.HotkeyDebounceMs = fresh.HotkeyDebounceMs;
            _config.AutoUnfreezeOnStall = fresh.AutoUnfreezeOnStall;
            _config.ShowBalloons = fresh.ShowBalloons;
            _config.StallThresholdMs = fresh.StallThresholdMs;
            _config.HardStallMs = fresh.HardStallMs;
            _config.CpuStressPercent = fresh.CpuStressPercent;
            _config.MemStressPercent = fresh.MemStressPercent;
            _config.PowerPlanBoost = fresh.PowerPlanBoost;
            _config.PowerPlanRestoreAfterSeconds = fresh.PowerPlanRestoreAfterSeconds;
            _config.RestartExplorerOnUnfreeze = fresh.RestartExplorerOnUnfreeze;
            _config.ResetGpuDriver = fresh.ResetGpuDriver;
            _config.RestartDwmOnFrozenScreen = fresh.RestartDwmOnFrozenScreen;
            _config.ShowCaptureOverlay = fresh.ShowCaptureOverlay;
            _config.ShowImmediateCaptureFeedback = fresh.ShowImmediateCaptureFeedback;
            _config.PlayRecoverySound = fresh.PlayRecoverySound;
            _config.CaptureOverlayDurationMs = fresh.CaptureOverlayDurationMs;
            _config.KeepIncidentHistory = fresh.KeepIncidentHistory;
            _config.IncidentHistoryLimit = fresh.IncidentHistoryLimit;
            _config.EnableSupportBundleExport = fresh.EnableSupportBundleExport;
            _config.EnableDetailedActionReporting = fresh.EnableDetailedActionReporting;
            _config.EnableWindowsApplicationRestart = fresh.EnableWindowsApplicationRestart;
            _config.ThawWatchdogMode = fresh.ThawWatchdogMode;
            _config.RescueBrokerMode = fresh.RescueBrokerMode;
            _config.WatchdogHeartbeatSeconds = fresh.WatchdogHeartbeatSeconds;
            _config.WatchdogRelaunchDelaySeconds = fresh.WatchdogRelaunchDelaySeconds;
            _incidentHistory.SetLimit(_config.GetIncidentHistoryLimit());
            IReadOnlyList<HotkeyValidationIssue> issues = _hook.ReloadConfiguration(_config);
            _panicItem.Text = $"Panic hotkey: {_config.PanicHotkey} (always works)";
            slownessItem.Text = $"Slowdown hotkey: {_config.SlownessHotkey} ({_config.SlownessHotkeyMode})";
            frameDropItem.Text = $"Frame-drop hotkey: {_config.FrameDropHotkey} ({_config.FrameDropHotkeyMode})";
            Log.Info("Config reloaded");
            string reloadMessage = issues.Count == 0
                ? "Configuration and hotkeys reloaded."
                : $"Configuration reloaded with {issues.Count} hotkey correction(s); inspect the log.";
            _tray.ShowBalloonTip(3000, "Thaw", reloadMessage, issues.Count == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
            foreach (ConfigValidationIssue issue in _config.ValidateSettings())
                Log.Warn($"Config policy after reload: {issue.SettingName}: {issue.Message}; effective={issue.EffectiveValue}");
        };

        var history = new ToolStripMenuItem("Incident history…")
        {
            ToolTipText = "Review bounded aggregate recovery receipts; no action is performed.",
        };
        history.Click += (_, _) => ShowIncidentHistory();

        var exportBundle = new ToolStripMenuItem("Export support bundle…")
        {
            ToolTipText = "Write a ZIP containing Thaw's log, config, diagnostics, and incident receipts.",
        };
        exportBundle.Click += (_, _) => ExportSupportBundle();

        var elevate = new ToolStripMenuItem("Restart as Administrator");
        elevate.Enabled = !_isElevated;
        elevate.Click += (_, _) => RestartElevated();

        var hotkeyHelp = new ToolStripMenuItem("Hotkeys & safety help…");
        hotkeyHelp.Click += (_, _) => ShowHotkeyHelp();

        var about = new ToolStripMenuItem("About Thaw");
        about.Click += (_, _) =>
        {
            string msg =
                "Thaw — Instant Unfreezer\n\n" +
                "Bounded, evidence-gated recovery profiles:\n" +
                "• slowdown: temporary Above Normal responsiveness and pressure-gated memory/cache work\n" +
                "• frame drops: requests the Windows graphics-driver reset only\n" +
                "• panic: combines display and system recovery; disruptive steps stay separately gated\n" +
                "• automatic stalls: diagnostics-only safe profile\n\n" +
                $"• Panic: {_config.PanicHotkey}\n" +
                $"• Slowdown: {_config.SlownessHotkey} ({_config.SlownessHotkeyMode})\n" +
                $"• Frame drops: {_config.FrameDropHotkey} ({_config.FrameDropHotkeyMode})\n" +
                "• Double-click tray icon to unfreeze\n" +
                $"• Automatic stall response: {(_config.AutoUnfreezeOnStall ? "enabled" : "disabled")}\n\n" +
                "Log: " + Log.LogPath + "\n" +
                "Config: " + _configPath;
            MessageBox.Show(msg, "About Thaw", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        var exit = new ToolStripMenuItem("Exit Thaw");
        exit.Click += (_, _) => ExitThread();

        menu.Items.Add(unfreezeItem);
        menu.Items.Add(strongRecoveryItem);
        menu.Items.Add(emergencyRecoveryItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(statusItem);
        menu.Items.Add(recoveryStatusItem);
        menu.Items.Add(adminItem);
        menu.Items.Add(altF4Mode);
        menu.Items.Add(panicItem);
        menu.Items.Add(slownessItem);
        menu.Items.Add(frameDropItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autostartItem);
        menu.Items.Add(openConfig);
        menu.Items.Add(reload);
        menu.Items.Add(elevate);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(history);
        menu.Items.Add(exportBundle);
        menu.Items.Add(hotkeyHelp);
        menu.Items.Add(about);
        menu.Items.Add(exit);

        return (menu, statusItem, recoveryStatusItem, unfreezeItem, strongRecoveryItem,
            emergencyRecoveryItem, panicItem, adminItem, history, exportBundle, autostartItem);
    }

    private void SetAltF4Mode(string mode, ToolStripMenuItem stressed, ToolStripMenuItem always, ToolStripMenuItem off)
    {
        _config.AltF4Mode = mode;
        Config.Save(_configPath, _config);
        stressed.Checked = mode == "stressed";
        always.Checked = mode == "always";
        off.Checked = mode == "off";
        Log.Info("Alt+F4 mode -> " + mode);
        RefreshTray();
    }

    /// <summary>
    /// Requests one asynchronous recovery pass. The engine already rejects an
    /// overlapping pass; this UI-side guard additionally makes repeated keys and
    /// tray clicks visible and keeps them from creating misleading duplicate work.
    /// </summary>
    private void RequestUnfreeze(TriggerReason reason, bool bypassCooldown = false)
    {
        long now = Environment.TickCount64;
        if (Interlocked.CompareExchange(ref _recoveryInProgress, 1, 0) != 0)
        {
            Log.Debug($"Recovery request ({reason}) ignored: recovery already in progress");
            return;
        }

        long last = Interlocked.Read(ref _lastRecoveryRequestTick);
        if (!bypassCooldown && last != long.MinValue && now - last < RecoveryCooldownMs)
        {
            Interlocked.Exchange(ref _recoveryInProgress, 0);
            Log.Debug($"Recovery request ({reason}) ignored: cooldown {RecoveryCooldownMs - (now - last)} ms remaining");
            return;
        }

        Interlocked.Exchange(ref _lastRecoveryRequestTick, now);
        Volatile.Write(ref _lastRecoveryReason, (int)reason);
        bool accepted;
        try
        {
            accepted = _unfreezer.Trigger(reason);
        }
        catch (Exception ex)
        {
            // A startup/resource race must release the UI fence immediately;
            // otherwise every later shortcut would be suppressed until the
            // 60-second stale-guard timeout.
            Interlocked.Exchange(ref _recoveryInProgress, 0);
            _lastRecoveryOutcome = "Recovery could not start; retry is available.";
            Log.Error($"Recovery request ({reason}) failed before handoff", ex);
            return;
        }
        // The recovery worker is pre-warmed and must receive the request before
        // synchronous log I/O can contend with the shortcut handoff.
        Log.Info($"Recovery requested ({reason}) — normal/cooldown guard accepted");
        if (!accepted)
        {
            // A previous action may still be draining after its owner deadline.
            // Do not leave the UI guard claiming a recovery is in progress when
            // this request was deliberately refused by the engine fence.
            Interlocked.Exchange(ref _recoveryInProgress, 0);
            _lastRecoveryOutcome = "Recovery deferred while a previous bounded action drains.";
            Log.Debug($"Recovery request ({reason}) was deferred by the engine fence");
        }
    }

    private void OnRecoveryHotkeyRequested(object? sender, RecoveryHotkeyEventArgs e)
    {
        // Handoff comes first. UI marshaling must never delay a captured shortcut
        // or prevent the pre-warmed recovery worker from receiving it.
        switch (e.Action)
        {
            case RecoveryHotkeyAction.Panic:
                RequestUnfreeze(TriggerReason.Panic, bypassCooldown: true);
                break;
            case RecoveryHotkeyAction.Slowness:
                RequestUnfreeze(TriggerReason.Slowness);
                break;
            case RecoveryHotkeyAction.FrameDrop:
                RequestUnfreeze(TriggerReason.FrameDrop);
                break;
            case RecoveryHotkeyAction.AltF4:
                RequestUnfreeze(TriggerReason.Hotkey);
                break;
        }

        // This is only a capture acknowledgement, not proof that Windows accepted
        // every recovery operation. It is posted after the engine handoff.
        ShowImmediateCaptureFeedback(e.Action);
    }

    private void ShowImmediateCaptureFeedback(RecoveryHotkeyAction action)
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastCaptureFeedbackTick);
        if (previous != long.MinValue && now - previous < 150)
            return;
        Interlocked.Exchange(ref _lastCaptureFeedbackTick, now);

        string label = action switch
        {
            RecoveryHotkeyAction.AltF4 => "Alt+F4 captured — recovery starting",
            RecoveryHotkeyAction.Panic => "Panic hotkey captured — recovery starting",
            RecoveryHotkeyAction.Slowness => "Slowdown hotkey captured — recovery starting",
            RecoveryHotkeyAction.FrameDrop => "Frame-drop hotkey captured — recovery starting",
            _ => "Recovery captured — starting",
        };

        bool playSound = _config.PlayRecoverySound;
        bool showBalloon = _config.ShowImmediateCaptureFeedback && _config.ShowBalloons;
        bool showOverlay = _config.ShowCaptureOverlay;
        try
        {
            _ui.Post(_ =>
            {
                try
                {
                    if (playSound)
                    {
                        try { SystemSounds.Asterisk.Play(); }
                        catch (Exception ex) { Log.Debug("Recovery sound unavailable: " + ex.Message); }
                    }
                    if (showBalloon)
                        _tray.ShowBalloonTip(1500, "Thaw", label, ToolTipIcon.Info);
                    if (showOverlay)
                        ShowCaptureOverlay(label);
                }
                catch (Exception ex)
                {
                    Log.Debug("Immediate recovery feedback failed: " + ex.Message);
                }
            }, null);
        }
        catch (Exception ex)
        {
            // Feedback is optional; never let a dead UI context prevent the
            // dispatch worker from acknowledging the already-accepted rescue.
            Log.Error("Immediate recovery feedback post failed", ex);
        }
    }

    private void ShowCaptureOverlay(string text)
    {
        _captureOverlay?.Close();
        _captureOverlay?.Dispose();
        _captureOverlay = new FeedbackOverlay(text, _config.GetCaptureOverlayDurationMs());
        _captureOverlay.FormClosed += (_, _) =>
        {
            if (_captureOverlay is not null && _captureOverlay.IsDisposed)
                _captureOverlay = null;
        };
        _captureOverlay.Show();
    }

    private void ConfirmShellRecovery()
    {
        if (Volatile.Read(ref _recoveryInProgress) != 0 || Volatile.Read(ref _shellRecoveryInProgress) != 0)
        {
            Log.Debug("Strong recovery ignored: another recovery is already in progress");
            return;
        }

        var choice = MessageBox.Show(
            "Strong recovery restarts explorer.exe. The taskbar, desktop icons, and File Explorer windows may disappear briefly.\n\nContinue?",
            "Thaw — confirm strong recovery",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (choice != DialogResult.Yes) return;

        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastShellRecoveryRequestTick);
        if (last != long.MinValue && now - last < ShellRecoveryCooldownMs)
        {
            Log.Debug($"Strong recovery ignored: cooldown {ShellRecoveryCooldownMs - (now - last)} ms remaining");
            return;
        }

        if (Interlocked.CompareExchange(ref _shellRecoveryInProgress, 1, 0) != 0)
            return;

        Interlocked.Exchange(ref _lastShellRecoveryRequestTick, now);
        Log.Warn("Strong recovery requested from tray: restarting explorer.exe");
        _unfreezer.RestartExplorerNow();
    }

    private void MarkShellRecoveryCompleted()
    {
        if (Interlocked.Exchange(ref _shellRecoveryInProgress, 0) != 0)
            Log.Info("Strong recovery completed: explorer restart returned");
    }

    private void ShowHotkeyHelp()
    {
        string modeText = _config.AltF4Mode switch
        {
            "always" => "always FORCE-RUNS every configured recovery action (it will not close the focused window)",
            "stressed" => "force-runs every configured recovery action only while Thaw detects stress; otherwise it passes through",
            "off" => "passes through; use the panic hotkey for recovery",
            _ => "uses the configured mode",
        };

        string msg =
            "Thaw controls\n\n" +
            $"• Alt+F4: {modeText}.\n" +
            $"• {_config.PanicHotkey}: emergency recovery; always intercepted.\n" +
            $"• {_config.SlownessHotkey}: slowdown recovery ({_config.SlownessHotkeyMode}).\n" +
            $"• {_config.FrameDropHotkey}: frame-drop/display recovery ({_config.FrameDropHotkeyMode}).\n" +
            "• Double-click the tray icon: normal recovery.\n" +
            "• Right-click: choose normal, strong shell, or emergency recovery.\n\n" +
             "• A non-activating overlay and optional immediate balloon acknowledge capture before the worker starts.\n" +
             "• Optional sound is disabled by default and can be enabled in config.json.\n" +
             "• Capture redundancy uses two independent low-level hooks; the Thaw-only rescue helper adds a named event and registered-hotkey fallback.\n" +
             "• Incident history and support-bundle export contain aggregate receipts; they never dump other apps.\n\n" +
            "Safety notes\n\n" +
            "• The slowdown chord never resets the display; the frame-drop chord never trims processes or restarts the shell.\n" +
            "• Alt+F4 force-runs display, DWM, foreground, memory/cache, shell, desktop-refresh, power/network cache, and resource diagnostics together.\n" +
            "• Expect the screen, taskbar, and desktop shell to disappear briefly on every Alt+F4 press.\n" +
            "• Strong recovery restarts explorer.exe only; confirm the warning before it runs.\n" +
            "• Thaw does not intentionally close, suspend, or pause other applications.\n" +
            "• Network, audio, storage, thermal, and driver faults are diagnosed; services, adapters, and devices are not blindly restarted.\n" +
            "• A kernel/driver deadlock or power failure cannot be repaired by a user-mode app.\n\n" +
            $"Current privilege: {(_isElevated ? "Administrator (system-level steps available)" : "standard user (system-level steps skipped)")}.";
        MessageBox.Show(msg, "Thaw — hotkeys & safety", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Re-registers the tray icon after explorer restarts (the shell owns the notification area).</summary>
    private void ReAddTrayIcon()
    {
        try
        {
            _tray.Visible = false;
            _tray.Icon = _watchdog.Stressed ? _iconAlert : _iconNormal;
            _tray.Visible = true;
            Log.Info("Tray icon re-registered after explorer restart");
        }
        catch (Exception ex)
        {
            Log.Debug("Tray re-register failed: " + ex.Message);
        }
    }

    private string GetRecoveryStateText(long now)
    {
        if (Volatile.Read(ref _recoveryInProgress) != 0)
        {
            long started = Interlocked.Read(ref _lastRecoveryRequestTick);
            if (started != long.MinValue && now - started >= RecoveryWatchdogTimeoutMs &&
                Interlocked.CompareExchange(ref _recoveryInProgress, 0, 1) == 1)
            {
                _lastRecoveryOutcome = "No completion was reported after 60 s; inspect the log before retrying.";
                Log.Warn("Recovery UI guard timed out after 60 s; engine completion was not observed");
            }
            else
            {
                string reason = (TriggerReason)Volatile.Read(ref _lastRecoveryReason) switch
                {
                    TriggerReason.Hotkey => "Alt+F4",
                    TriggerReason.Panic => "panic hotkey",
                    TriggerReason.Slowness => "slowdown hotkey",
                    TriggerReason.FrameDrop => "frame-drop hotkey",
                    TriggerReason.Auto => "automatic stall response",
                    _ => "tray",
                };
                return $"in progress ({reason}) — new requests wait";
            }
        }

        if (Volatile.Read(ref _shellRecoveryInProgress) != 0)
        {
            long started = Interlocked.Read(ref _lastShellRecoveryRequestTick);
            if (started != long.MinValue && now - started >= ShellRecoveryWatchdogTimeoutMs &&
                Interlocked.CompareExchange(ref _shellRecoveryInProgress, 0, 1) == 1)
            {
                _lastRecoveryOutcome = "Explorer restart has not reported completion; inspect the log.";
                Log.Warn("Strong recovery UI guard timed out after 30 s");
            }
            else
            {
                return "strong shell recovery in progress — taskbar may be restarting";
            }
        }

        long recoveryRemaining = GetCooldownRemaining(Interlocked.Read(ref _lastRecoveryRequestTick), now, RecoveryCooldownMs);
        long shellRemaining = GetCooldownRemaining(Interlocked.Read(ref _lastShellRecoveryRequestTick), now, ShellRecoveryCooldownMs);
        long remaining = Math.Max(recoveryRemaining, shellRemaining);
        if (remaining > 0)
            return $"cooldown — retry in {(remaining + 999) / 1000} s";

        return "ready — " + Shorten(_lastRecoveryOutcome, 96);
    }

    private static long GetCooldownRemaining(long last, long now, long cooldown)
    {
        if (last == long.MinValue) return 0;
        long elapsed = now - last;
        return elapsed >= cooldown ? 0 : cooldown - elapsed;
    }

    private static string Shorten(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..Math.Max(0, maxLength - 1)] + "…";
    }

    private void RefreshTray()
    {
        try
        {
            long now = Environment.TickCount64;
            bool recoveryBusy = Volatile.Read(ref _recoveryInProgress) != 0;
            bool shellBusy = Volatile.Read(ref _shellRecoveryInProgress) != 0;
            long recoveryCooldown = GetCooldownRemaining(Interlocked.Read(ref _lastRecoveryRequestTick), now, RecoveryCooldownMs);
            long shellCooldown = GetCooldownRemaining(Interlocked.Read(ref _lastShellRecoveryRequestTick), now, ShellRecoveryCooldownMs);

            _tray.Icon = _watchdog.Stressed ? _iconAlert : _iconNormal;
            string trayState = recoveryBusy || shellBusy
                ? "recovery in progress"
                : _watchdog.Stressed ? "stressed" : "ready";
            string trayText = $"Thaw | {trayState} | CPU {_watchdog.CpuPercent:0}% RAM {_watchdog.MemPercent}% | {(_isElevated ? "admin" : "standard")}";
            _tray.Text = Shorten(trayText, 63);
            _statusItem.Text = "Status: " + _watchdog.StatusText;
            _recoveryStatusItem.Text = "Recovery: " + GetRecoveryStateText(now);
            _adminItem.Text = _isElevated
                ? "Privileges: Administrator (system-level steps available)"
                : "Privileges: Standard user (system-level steps skipped)";
            _adminItem.ToolTipText = _isElevated
                ? "Standby/modified-list purge, file-cache flush, and power-plan changes are available."
                : "Run as Administrator to enable standby/modified-list purge, file-cache flush, and power-plan changes.";
            _historyItem.Enabled = _config.KeepIncidentHistory;
            _historyItem.ToolTipText = _config.KeepIncidentHistory
                ? "Review bounded aggregate recovery receipts."
                : "Incident history is disabled in config.json.";
            _exportBundleItem.Enabled = _config.EnableSupportBundleExport;
            _exportBundleItem.ToolTipText = _config.EnableSupportBundleExport
                ? "Export log, config, diagnostics, and incident receipts to a ZIP."
                : "Support-bundle export is disabled in config.json.";
            _unfreezeItem.Text = recoveryBusy
                ? "⏳ Normal recovery in progress…"
                : recoveryCooldown > 0
                    ? $"⏱ Normal recovery — retry in {(recoveryCooldown + 999) / 1000} s"
                    : "⚡ Normal recovery — configured sequence";
            _strongRecoveryItem.Text = shellBusy
                ? "⏳ Strong recovery in progress…"
                : shellCooldown > 0
                    ? $"⏱ Strong recovery — retry in {(shellCooldown + 999) / 1000} s"
                    : "🧯 Strong recovery — restart Explorer shell";
            _unfreezeItem.Enabled = !recoveryBusy && !shellBusy && recoveryCooldown == 0;
            _strongRecoveryItem.Enabled = !recoveryBusy && !shellBusy && shellCooldown == 0;
            _emergencyRecoveryItem.Enabled = !recoveryBusy && !shellBusy;
        }
        catch
        {
            // Tray may not be ready during startup; ignore.
        }
    }

    private void OnUnfreezeCompleted(UnfreezeStats stats)
    {
        Volatile.Write(ref _recoveryInProgress, 0);
        string proof = string.IsNullOrWhiteSpace(stats.Diagnostics)
            ? "proof=aggregate completion only"
            : "proof=" + Shorten(stats.Diagnostics, 220);
        if (_config.EnableDetailedActionReporting && stats.ProgressReceipts.Count > 0)
        {
            string actionSummary = BuildActionReceiptSummary(stats.ProgressReceipts);
            proof += " · " + actionSummary;
            Log.Info("Recovery action receipts: " + actionSummary);
        }
        _lastRecoveryOutcome = stats.Summary + " · " + proof;
        if (_config.KeepIncidentHistory)
            _incidentHistory.Append(stats, _config.EnableDetailedActionReporting);
        Log.Info("Recovery UI state: completed");
        try
        {
            if (_config.ShowBalloons)
            {
                string title = stats.OverallOutcome switch
                {
                    RecoveryOutcome.Recovered => "Thaw ⚡ Recovery verified",
                    RecoveryOutcome.Unchanged => "Thaw ⚡ Recovery unchanged",
                    RecoveryOutcome.Worse => "Thaw ⚠ Recovery warning",
                    _ => "Thaw ⚡ Recovery report",
                };
                ToolTipIcon icon = stats.OverallOutcome == RecoveryOutcome.Worse
                    ? ToolTipIcon.Warning
                    : ToolTipIcon.Info;
                _tray.ShowBalloonTip(5000, title, Shorten(stats.Summary + "\n" + proof, 700), icon);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Balloon failed: " + ex.Message);
        }
        RefreshTray();
    }

    private void ShowIncidentHistory()
    {
        try
        {
            IReadOnlyList<IncidentReceipt> receipts = _incidentHistory.ReadRecent(20);
            if (receipts.Count == 0)
            {
                MessageBox.Show("No recovery incidents have been recorded yet.", "Thaw — incident history",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var lines = new StringBuilder("Recent recovery incidents\n\n");
            foreach (IncidentReceipt receipt in receipts)
            {
                lines.Append(receipt.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                lines.Append("  ").Append(receipt.Reason);
                lines.Append("  ").Append(receipt.DurationMs).Append(" ms");
                lines.Append("  outcome=").Append(receipt.OverallOutcome);
                lines.Append("  ").Append(receipt.Summary).AppendLine();
                if (receipt.ProgressReceipts.Count > 0)
                {
                    lines.Append("  actions: ")
                        .Append(Shorten(BuildActionReceiptSummary(receipt.ProgressReceipts), 900))
                        .AppendLine();
                }
            }
            lines.Append("\nThe history is bounded and contains aggregate receipts plus the engine's ");
            lines.Append("per-action phase/outcome/elapsed/detail records, including rollback phases when emitted.");
            MessageBox.Show(lines.ToString(), "Thaw — incident history", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Warn("Incident history read failed: " + ex.Message);
        }
    }

    private void ExportSupportBundle()
    {
        if (!_config.EnableSupportBundleExport)
        {
            MessageBox.Show("Support-bundle export is disabled in config.json.", "Thaw",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Export Thaw support bundle",
            Filter = "ZIP archive (*.zip)|*.zip|All files (*.*)|*.*",
            DefaultExt = "zip",
            AddExtension = true,
            FileName = "thaw-support-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        string temporaryPath = Path.Combine(Path.GetTempPath(), "thaw-support-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                AddTextEntry(archive, "manifest.txt",
                    "Thaw support bundle\n" +
                    "Created UTC: " + DateTimeOffset.UtcNow.ToString("O") + "\n" +
                    "Version: " + (typeof(AppContext).Assembly.GetName().Version?.ToString() ?? "unknown") + "\n" +
                    "This bundle contains no process dumps and no arbitrary application data.\n");
                AddTextEntry(archive, "diagnostics.txt", BuildSupportDiagnostics());
                AddFileEntry(archive, Log.LogPath, "log.txt");
                AddFileEntry(archive, _configPath, "config.json");
                AddFileEntry(archive, _incidentHistory.Path, "incidents.jsonl");
            }

            File.Copy(temporaryPath, dialog.FileName, overwrite: true);
            Log.Info("Support bundle exported: " + dialog.FileName);
            MessageBox.Show("Support bundle exported.\n\n" + dialog.FileName, "Thaw",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error("Support bundle export failed", ex);
            MessageBox.Show("Support bundle export failed:\n\n" + ex.Message, "Thaw",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private string BuildSupportDiagnostics()
    {
        var text = new StringBuilder();
        text.AppendLine("Thaw read-only support diagnostics");
        text.AppendLine("Timestamp UTC: " + DateTimeOffset.UtcNow.ToString("O"));
        text.AppendLine("Process: " + Environment.ProcessId);
        text.AppendLine("Elevated: " + _isElevated);
        text.AppendLine("Config: " + _configPath);
        text.AppendLine("History: " + _incidentHistory.Path);
        text.AppendLine("Watchdog: " + _watchdog.StatusText);
        text.AppendLine("CPU: " + _watchdog.CpuPercent.ToString("0.0") + "%");
        text.AppendLine("RAM: " + _watchdog.MemPercent + "%");
        text.AppendLine("Last stall: " + _watchdog.LastStallMs + " ms");
        text.AppendLine("Hook primary installed: " + _hook.IsInstalled);
        text.AppendLine("Hook emergency installed: " + _hook.IsEmergencyInstalled);
        text.AppendLine("Hook capture path available: " + _hook.HasCapturePath);
        text.AppendLine("Recovery request worker alive: " + _unfreezer.RecoveryWorkerAlive);
        text.AppendLine("Hook registered fallback count: " + _hook.RegisteredFallbackHotkeyCount);
        text.AppendLine("Hook captures/drops/callback errors: " + _hook.CaptureCount + "/" +
                         _hook.DroppedDispatchCount + "/" + _hook.CallbackErrorCount);
        KeyboardCaptureReceipt lastCapture = _hook.LastCapture;
        text.AppendLine("Hook last capture: " + (lastCapture.Sequence == 0
            ? "none"
            : lastCapture.Sequence + " " + lastCapture.Hotkey + " " + lastCapture.CapturedAtUtc.ToString("O")));
        text.AppendLine("Rescue event/ack available: " + _hook.RescueBroker.IsAvailable + "/" +
                         _hook.RescueBroker.IsAcknowledgementAvailable);
        TelemetrySnapshot? telemetry = _watchdog.LatestTelemetry;
        if (telemetry is null)
        {
            text.AppendLine("Telemetry: unavailable");
        }
        else
        {
            text.AppendLine("Telemetry cause: " + telemetry.Classification.Cause +
                             " score=" + telemetry.Classification.Score.ToString("0.00") +
                             " reason=" + telemetry.Classification.Reason);
            text.AppendLine("Commit/pagefile: " + FormatSupportMetric(telemetry.CommitPercent) +
                             "%/" + FormatSupportMetric(telemetry.PageFilePercent) + "%");
            text.AppendLine("Disk/queue: " + FormatSupportMetric(telemetry.DiskLatencyMilliseconds) +
                             " ms/" + FormatSupportMetric(telemetry.DiskQueueLength));
            text.AppendLine("DPC/ISR/network: " + FormatSupportMetric(telemetry.DpcTimePercent) +
                             "%/" + FormatSupportMetric(telemetry.InterruptTimePercent) +
                             "%/" + FormatSupportMetric(telemetry.NetworkRetransmitsPerSecond) + " retransmits/s");
            text.AppendLine("GPU/thermal/frequency: " + FormatSupportMetric(telemetry.GpuEngineUtilizationPercent) +
                             "%/" + FormatSupportMetric(telemetry.ThermalCelsius) +
                             " C/" + FormatSupportMetric(telemetry.EffectiveFrequencyPercent) + "%");
            if (telemetry.ForegroundProcess is { IsValid: true } foreground)
                text.AppendLine("Foreground: " + foreground.ProcessId + "/" + foreground.Name +
                                 " responding=" + (foreground.RespondingSampleValid ? foreground.Responding.ToString() : "n/a") +
                                 " io=" + (foreground.IoSampleValid ? "sampled" : "n/a"));
        }
        text.AppendLine("Recovery state: " + GetRecoveryStateText(Environment.TickCount64));
        text.AppendLine("Detailed action API: Completed(UnfreezeStats) includes bounded ActionOutcomes and ProgressReceipts.");
        text.AppendLine("Per-action fields: phase, outcome, elapsed milliseconds, detail, and rollback phases when emitted.");
        text.AppendLine("No process dump, service mutation, or device mutation was performed by export.");
        return text.ToString();
    }

    private static string FormatSupportMetric(double value) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? "n/a"
            : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string BuildActionReceiptSummary(IReadOnlyList<RecoveryProgressReceipt> receipts)
    {
        if (receipts.Count == 0) return "actions=none";
        IEnumerable<string> entries = receipts.TakeLast(16).Select(receipt =>
        {
            string detail = (receipt.Detail ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
            detail = Shorten(detail, 64);
            return string.IsNullOrWhiteSpace(detail)
                ? $"{receipt.Action}:{receipt.Phase}/{receipt.Outcome}@{receipt.ElapsedMs}ms"
                : $"{receipt.Action}:{receipt.Phase}/{receipt.Outcome}@{receipt.ElapsedMs}ms({detail})";
        });
        return "actions=" + string.Join(",", entries);
    }

    private static void AddTextEntry(ZipArchive archive, string name, string text)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using StreamWriter writer = new(entry.Open(), Encoding.UTF8);
        writer.Write(text);
    }

    private static void AddFileEntry(ZipArchive archive, string path, string name)
    {
        if (!File.Exists(path)) return;
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using FileStream source = File.OpenRead(path);
        using Stream destination = entry.Open();
        source.CopyTo(destination);
    }

    private sealed record IncidentReceipt(
        DateTimeOffset TimestampUtc,
        TriggerReason Reason,
        long DurationMs,
        string Summary,
        string Diagnostics,
        int ActionsSucceeded,
        int ActionsSkipped,
        bool BudgetExpired,
        bool DetailedReportingRequested)
    {
        public RecoveryOutcome OverallOutcome { get; init; } = RecoveryOutcome.Unverified;
        public IReadOnlyDictionary<string, RecoveryOutcome> ActionOutcomes { get; init; } =
            new Dictionary<string, RecoveryOutcome>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<RecoveryProgressReceipt> ProgressReceipts { get; init; } =
            Array.Empty<RecoveryProgressReceipt>();
    }

    /// <summary>
    /// Small JSONL journal owned by Thaw. It is deliberately bounded, append-only
    /// during normal operation, and never contains process command lines or dumps.
    /// </summary>
    private sealed class IncidentHistoryStore
    {
        private readonly object _gate = new();
        private int _limit;

        public IncidentHistoryStore(int limit)
        {
            _limit = Math.Clamp(limit, 10, 500);
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Thaw");
            Path = System.IO.Path.Combine(dir, "incidents.jsonl");
        }

        public string Path { get; }

        public void SetLimit(int limit)
        {
            lock (_gate) _limit = Math.Clamp(limit, 10, 500);
        }

        public void Append(UnfreezeStats stats, bool detailedReportingRequested)
        {
            var receipt = new IncidentReceipt(
                DateTimeOffset.UtcNow,
                stats.Reason,
                stats.DurationMs,
                stats.Summary,
                stats.Diagnostics,
                stats.ActionsSucceeded,
                stats.ActionsSkipped,
                stats.BudgetExpired,
                detailedReportingRequested)
            {
                OverallOutcome = stats.OverallOutcome,
                ActionOutcomes = detailedReportingRequested
                    ? new Dictionary<string, RecoveryOutcome>(stats.ActionOutcomes, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, RecoveryOutcome>(StringComparer.OrdinalIgnoreCase),
                ProgressReceipts = detailedReportingRequested
                    ? stats.ProgressReceipts.ToArray()
                    : Array.Empty<RecoveryProgressReceipt>(),
            };
            try
            {
                lock (_gate)
                {
                    string? dir = System.IO.Path.GetDirectoryName(Path);
                    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(Path, JsonSerializer.Serialize(receipt) + Environment.NewLine, Encoding.UTF8);
                    TrimLocked();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Incident history append failed: " + ex.Message);
            }
        }

        public IReadOnlyList<IncidentReceipt> ReadRecent(int count)
        {
            var result = new List<IncidentReceipt>();
            try
            {
                lock (_gate)
                {
                    if (!File.Exists(Path)) return result;
                    foreach (string line in File.ReadLines(Path).TakeLast(Math.Clamp(count, 1, 100)))
                    {
                        try
                        {
                            IncidentReceipt? receipt = JsonSerializer.Deserialize<IncidentReceipt>(line);
                            if (receipt is not null) result.Add(receipt);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Incident history read failed: " + ex.Message);
            }
            return result;
        }

        private void TrimLocked()
        {
            string[] lines = File.ReadLines(Path).TakeLast(_limit).ToArray();
            string temporaryPath = Path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllLines(temporaryPath, lines, Encoding.UTF8);
                File.Move(temporaryPath, Path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private sealed class FeedbackOverlay : Form
    {
        private readonly System.Windows.Forms.Timer _timer;

        public FeedbackOverlay(string text, int durationMs)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(28, 33, 42);
            ForeColor = Color.White;
            Opacity = 0.92;
            Width = 460;
            Height = 74;
            Padding = new Padding(14, 8, 14, 8);

            var label = new Label
            {
                Dock = DockStyle.Fill,
                Text = text,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                AutoEllipsis = true,
            };
            Controls.Add(label);

            _timer = new System.Windows.Forms.Timer { Interval = Math.Clamp(durationMs, 250, 5_000) };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                Close();
            };
            Shown += (_, _) =>
            {
                Screen? screen = Screen.PrimaryScreen;
                Rectangle area = screen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
                Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + 48);
                _timer.Start();
            };
            FormClosed += (_, _) => _timer.Dispose();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExNoActivate = 0x08000000;
                const int WsExToolWindow = 0x00000080;
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExNoActivate | WsExToolWindow;
                return cp;
            }
        }
    }

    private void OpenConfig()
    {
        try
        {
            Config.Save(_configPath, _config); // ensure the file exists with current settings
            Process.Start(new ProcessStartInfo { FileName = _configPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("OpenConfig failed", ex);
        }
    }

    private void RestartElevated()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? "Thaw.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--elevated",
            };
            Process.Start(psi);
            Log.Info("Requesting elevated restart");
            ExitThread();
        }
        catch
        {
            Log.Info("Elevated restart cancelled by user");
        }
    }

    // ------------------------------------------------------------------
    // Shutdown
    // ------------------------------------------------------------------

    private void Cleanup()
    {
        try
        {
            _uiTimer?.Stop();
            _captureOverlay?.Close();
            _captureOverlay?.Dispose();
            _hook?.Dispose();
            _unfreezer?.Dispose();
            _watchdog?.Dispose();
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Icon = null;
                _tray.Dispose();
            }
            _iconNormal?.Dispose();
            _iconAlert?.Dispose();
            Log.Info("Thaw exited cleanly");
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
