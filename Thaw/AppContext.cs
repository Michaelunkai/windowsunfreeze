using System.Diagnostics;

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
    private readonly ToolStripMenuItem _unfreezeItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly string _configPath;
    private readonly SynchronizationContext _ui;

    public AppContext()
    {
        _configPath = Config.DefaultPath();
        _config = Config.Load(_configPath);
        Log.Init(_config.DebugLog);

        // Artwork.
        _iconNormal = Icons.CreateTrayIcon(alert: false);
        _iconAlert = Icons.CreateTrayIcon(alert: true);

        // No host window needed: the message loop comes from Application.Run(context),
        // NotifyIcon owns its own hidden window, and the low-level hook is delivered
        // to this thread's message queue.

        _watchdog = new Watchdog(_config);
        _unfreezer = new Unfreezer(_config, _watchdog);

        _hook = new KeyboardHook(
            shouldInterceptAltF4: ShouldInterceptAltF4,
            onAltF4Intercepted: () => _unfreezer.Trigger(TriggerReason.Hotkey),
            onPanic: () => _unfreezer.Trigger(TriggerReason.Panic),
            _config);

        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _watchdog.HardStallDetected += () =>
        {
            if (_config.AutoUnfreezeOnStall) _unfreezer.Trigger(TriggerReason.Auto);
        };
        _unfreezer.Completed += stats => _ui.Post(_ => OnUnfreezeCompleted(stats), null);
        _unfreezer.ExplorerRestartedNow += () => _ui.Post(_ => ReAddTrayIcon(), null);

        // Tray.
        _tray = new NotifyIcon
        {
            Icon = _iconNormal,
            Text = "Thaw — starting…",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => _unfreezer.Trigger(TriggerReason.Manual);

        var menu = BuildMenu();
        _menu = menu.Menu;
        _statusItem = menu.Status;
        _unfreezeItem = menu.Unfreeze;
        _autostartItem = menu.Autostart;
        _tray.ContextMenuStrip = _menu;

        _uiTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _uiTimer.Tick += (_, _) => RefreshTray();
        _uiTimer.Start();

        Application.ApplicationExit += (_, _) => Cleanup();
        ThreadExit += (_, _) => Cleanup();

        _watchdog.Start();

        Log.Info("Thaw running. Alt+F4 mode: " + _config.AltF4Mode + " | panic: " + _config.PanicHotkey +
                 " | elevated: " + Native.IsElevated() + " | autostart: " + Autostart.IsEnabled());
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

    private bool ShouldInterceptAltF4()
    {
        return _config.AltF4Mode switch
        {
            "always" => true,
            "off" => false,
            _ => _watchdog.Stressed || _watchdog.RecentStall,
        };
    }

    // ------------------------------------------------------------------
    // Tray UI
    // ------------------------------------------------------------------

    private (ContextMenuStrip Menu, ToolStripMenuItem Status, ToolStripMenuItem Unfreeze, ToolStripMenuItem Autostart) BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var unfreezeItem = new ToolStripMenuItem("⚡ Unfreeze Now")
        {
            Font = new Font(menu.Font, FontStyle.Bold),
        };
        unfreezeItem.Click += (_, _) => _unfreezer.Trigger(TriggerReason.Manual);

        var statusItem = new ToolStripMenuItem("Status: …") { Enabled = false };

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

        var autostartItem = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsEnabled() };
        autostartItem.Click += (_, _) =>
        {
            autostartItem.Checked = !autostartItem.Checked;
            Autostart.SetEnabled(autostartItem.Checked);
            Log.Info("Autostart -> " + autostartItem.Checked);
        };

        var openConfig = new ToolStripMenuItem("Open Config File");
        openConfig.Click += (_, _) => OpenConfig();

        var restartExplorer = new ToolStripMenuItem("Restart Explorer Now");
        restartExplorer.Click += (_, _) => _unfreezer.RestartExplorerNow();

        var reload = new ToolStripMenuItem("Reload Config");
        reload.Click += (_, _) =>
        {
            var fresh = Config.Load(_configPath);
            _config.AltF4Mode = fresh.AltF4Mode;
            _config.PanicHotkey = fresh.PanicHotkey;
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
            Log.Info("Config reloaded");
            _tray.ShowBalloonTip(2500, "Thaw", "Configuration reloaded.", ToolTipIcon.Info);
        };

        var elevate = new ToolStripMenuItem("Restart as Administrator");
        elevate.Enabled = !Native.IsElevated();
        elevate.Click += (_, _) => RestartElevated();

        var about = new ToolStripMenuItem("About Thaw");
        about.Click += (_, _) =>
        {
            string msg =
                "Thaw — Instant Unfreezer\n\n" +
                "Alt+F4 instantly unfreezes the PC:\n" +
                "• trims RAM of every process (working sets + standby/modified lists + file cache)\n" +
                "• resets the GPU driver (Ctrl+Shift+Win+B)\n" +
                "• boosts foreground app + shell to High priority\n" +
                "• switches to the High Performance power plan (restored later)\n" +
                "• restarts explorer.exe (shell/taskbar)\n\n" +
                $"• Panic hotkey {_config.PanicHotkey}: always unfreezes\n" +
                "• Double-click tray icon to unfreeze\n" +
                "• Auto-unfreeze after hard stalls\n\n" +
                "Log: " + Log.LogPath + "\n" +
                "Config: " + _configPath;
            MessageBox.Show(msg, "About Thaw", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        var exit = new ToolStripMenuItem("Exit Thaw");
        exit.Click += (_, _) => ExitThread();

        menu.Items.Add(unfreezeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(statusItem);
        menu.Items.Add(altF4Mode);
        menu.Items.Add(panicItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autostartItem);
        menu.Items.Add(restartExplorer);
        menu.Items.Add(openConfig);
        menu.Items.Add(reload);
        menu.Items.Add(elevate);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(about);
        menu.Items.Add(exit);

        return (menu, statusItem, unfreezeItem, autostartItem);
    }

    private void SetAltF4Mode(string mode, ToolStripMenuItem stressed, ToolStripMenuItem always, ToolStripMenuItem off)
    {
        _config.AltF4Mode = mode;
        Config.Save(_configPath, _config);
        stressed.Checked = mode == "stressed";
        always.Checked = mode == "always";
        off.Checked = mode == "off";
        Log.Info("Alt+F4 mode -> " + mode);
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

    private void RefreshTray()
    {
        try
        {
            _tray.Icon = _watchdog.Stressed ? _iconAlert : _iconNormal;
            _tray.Text = "Thaw — " + _watchdog.StatusText;
            _statusItem.Text = "Status: " + _watchdog.StatusText;
        }
        catch
        {
            // Tray may not be ready during startup; ignore.
        }
    }

    private void OnUnfreezeCompleted(UnfreezeStats stats)
    {
        try
        {
            if (_config.ShowBalloons)
                _tray.ShowBalloonTip(5000, "Thaw ⚡ Unfrozen", stats.Summary, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Debug("Balloon failed: " + ex.Message);
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
            _hook?.Dispose();
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
