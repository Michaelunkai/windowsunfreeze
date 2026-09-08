using System.Text.Json;

namespace Thaw;

/// <summary>Recovery operation requested by a global recovery binding.</summary>
internal enum RecoveryHotkeyAction
{
    AltF4,
    Panic,
    Slowness,
    FrameDrop,
}

/// <summary>When a recovery binding is allowed to run.</summary>
internal enum RecoveryHotkeyMode
{
    Off,
    Always,
    Stressed,
}

/// <summary>Validated keyboard chord. The hook compares the complete modifier state.</summary>
internal readonly record struct RecoveryHotkeyChord(
    bool Alt,
    bool Ctrl,
    bool Shift,
    bool Win,
    int Vk,
    string Text)
{
    public int ModifierCount => (Alt ? 1 : 0) + (Ctrl ? 1 : 0) + (Shift ? 1 : 0) + (Win ? 1 : 0);

    public string Signature => $"{(Alt ? 'A' : '-')}{(Ctrl ? 'C' : '-')}" +
                               $"{(Shift ? 'S' : '-')}{(Win ? 'W' : '-')}:{Vk:X2}";
}

/// <summary>One effective, validated recovery binding used by <see cref="KeyboardHook"/>.</summary>
internal sealed record RecoveryHotkeyBinding(
    RecoveryHotkeyAction Action,
    RecoveryHotkeyMode Mode,
    string SettingName,
    RecoveryHotkeyChord Chord);

/// <summary>Non-fatal configuration validation diagnostic.</summary>
internal sealed record HotkeyValidationIssue(string SettingName, string Message, string EffectiveBinding);

/// <summary>Non-fatal configuration-policy diagnostic.</summary>
internal sealed record ConfigValidationIssue(string SettingName, string Message, string EffectiveValue);

/// <summary>User configuration, stored as JSON in %APPDATA%\Thaw\config.json.</summary>
internal sealed class Config
{
    internal const string DefaultAltF4Mode = "always";
    internal const string DefaultPanicHotkey = "Ctrl+Alt+U";
    internal const string DefaultSlownessHotkey = "Ctrl+Alt+S";
    internal const string DefaultFrameDropHotkey = "Ctrl+Alt+G";
    internal const string DefaultWatchdogMode = "off";
    internal const string DefaultRescueBrokerMode = "relaunch";

    /// <summary>
    /// "always"  — Alt+F4 ALWAYS runs the full combined recovery profile.
    ///             Normal window closing via Alt+F4 is disabled while Thaw runs (the X button still works).
    /// "stressed"— Alt+F4 unfreezes only while the system is slow/stuck; passes through when healthy.
    /// "off"     — Alt+F4 is never intercepted (only the panic hotkey unfreezes).
    /// </summary>
    // Alt+F4 is Thaw's primary, full-recovery chord. Use the window X button to close.
    public string AltF4Mode { get; set; } = DefaultAltF4Mode;

    /// <summary>Panic hotkey that always unfreezes. Keep this a two-modifier chord.</summary>
    public string PanicHotkey { get; set; } = DefaultPanicHotkey;

    /// <summary>Recovery chord for a slow or generally sluggish system.</summary>
    public string SlownessHotkey { get; set; } = DefaultSlownessHotkey;

    /// <summary>
    /// Controls the slowness chord. "always" is useful during a known bad session;
    /// "stressed" lets AppContext gate it with the watchdog; "off" leaves it untouched.
    /// </summary>
    public string SlownessHotkeyMode { get; set; } = "stressed";

    /// <summary>Recovery chord for display stutter/frame drops.</summary>
    public string FrameDropHotkey { get; set; } = DefaultFrameDropHotkey;

    /// <summary>Controls the frame-drop recovery chord ("always", "stressed", or "off").</summary>
    public string FrameDropHotkeyMode { get; set; } = "always";

    /// <summary>
    /// Minimum interval between accepted presses of the same binding. Key repeats are
    /// suppressed separately while the key is held. Values outside 50..10,000 ms are clamped.
    /// </summary>
    public int HotkeyDebounceMs { get; set; } = 750;

    // Automatic recovery is opt-in. A scheduler delay alone is not evidence that
    // display, shell, memory, or network mutation is appropriate.
    public bool AutoUnfreezeOnStall { get; set; } = false;
    public bool ShowBalloons { get; set; } = true;
    public bool DebugLog { get; set; } = false;

    /// <summary>Shows a short, non-activating confirmation as soon as a recovery chord is captured.</summary>
    public bool ShowCaptureOverlay { get; set; } = true;

    /// <summary>Shows the immediate capture balloon. Completion balloons remain controlled by ShowBalloons.</summary>
    public bool ShowImmediateCaptureFeedback { get; set; } = true;

    /// <summary>Plays the standard Windows notification sound when a recovery chord is captured.</summary>
    public bool PlayRecoverySound { get; set; } = false;

    /// <summary>Duration of the capture overlay in milliseconds; values are clamped to 250..5000.</summary>
    public int CaptureOverlayDurationMs { get; set; } = 1200;

    /// <summary>Retains bounded aggregate recovery receipts under %LOCALAPPDATA%\Thaw.</summary>
    public bool KeepIncidentHistory { get; set; } = true;

    /// <summary>Maximum number of incident receipts retained by Thaw.</summary>
    public int IncidentHistoryLimit { get; set; } = 100;

    /// <summary>Enables the tray command that writes a support bundle; no bundle is exported automatically.</summary>
    public bool EnableSupportBundleExport { get; set; } = true;

    /// <summary>Requests per-action receipts when the engine exposes that optional API.</summary>
    public bool EnableDetailedActionReporting { get; set; } = true;

    /// <summary>Registers Windows Application Restart for Thaw itself; this never restarts another process.</summary>
    public bool EnableWindowsApplicationRestart { get; set; } = true;

    /// <summary>Thaw-only helper mode: "off" or "relaunch". It never targets arbitrary commands.</summary>
    public string ThawWatchdogMode { get; set; } = DefaultWatchdogMode;

    /// <summary>Out-of-process Thaw-only rescue broker mode: "off" or "relaunch".</summary>
    public string RescueBrokerMode { get; set; } = DefaultRescueBrokerMode;

    /// <summary>Helper heartbeat interval in seconds; values are clamped to 5..120.</summary>
    public int WatchdogHeartbeatSeconds { get; set; } = 15;

    /// <summary>Delay before a failed Thaw-only helper relaunch; values are clamped to 1..30.</summary>
    public int WatchdogRelaunchDelaySeconds { get; set; } = 3;

    /// <summary>Scheduling delay (ms) above which the system is considered stressed.</summary>
    public int StallThresholdMs { get; set; } = 2500;

    /// <summary>Hard stall (ms) that triggers the automatic unfreeze.</summary>
    public int HardStallMs { get; set; } = 6000;

    /// <summary>CPU% contributing full stress weight at or above this value.</summary>
    public int CpuStressPercent { get; set; } = 90;

    /// <summary>RAM% contributing full stress weight at or above this value.</summary>
    public int MemStressPercent { get; set; } = 92;

    /// <summary>
    /// Alt+F4 force-all temporarily selects High Performance when explicitly enabled. The
    /// exact prior plan is restored at the end of the bounded run; a crash or power loss can
    /// still prevent rollback, so the conservative default is false.
    /// </summary>
    public bool PowerPlanBoost { get; set; } = false;

    /// <summary>Restore the previous power plan after this many seconds (0 = no automatic restore).</summary>
    public int PowerPlanRestoreAfterSeconds { get; set; } = 120;

    /// <summary>Permit Explorer restart. Alt+F4 force-all restarts it unconditionally.</summary>
    public bool RestartExplorerOnUnfreeze { get; set; } = false;

    /// <summary>Sends Ctrl+Shift+Win+B (the Windows graphics-driver reset) as part of every unfreeze.
    /// The screen may blank for ~1 second.</summary>
    public bool ResetGpuDriver { get; set; } = true;

    /// <summary>Permit DWM restart. Alt+F4 force-all restarts it unconditionally;
    /// other profiles retain the frozen-screen evidence check.</summary>
    public bool RestartDwmOnFrozenScreen { get; set; } = false;

    // ---------- parsed hotkeys ----------

    /// <summary>
    /// Returns the effective validated recovery bindings. Invalid chords are replaced by
    /// safe defaults; duplicate chords keep the first action and disable later collisions.
    /// </summary>
    internal IReadOnlyList<RecoveryHotkeyBinding> GetRecoveryHotkeys()
    {
        return BuildRecoveryHotkeys(null);
    }

    /// <summary>
    /// Returns the always-on bindings that can use RegisterHotKey as a fallback
    /// when a low-level hook path is unavailable. Alt+F4 is included only when
    /// its explicit mode is "always"; RegisterHotKey cannot guarantee the same
    /// close-suppression contract as WH_KEYBOARD_LL, but it can still wake the
    /// recovery path when both low-level hooks are unavailable.
    /// </summary>
    internal IReadOnlyList<RecoveryHotkeyBinding> GetAlwaysFallbackHotkeys()
    {
        IReadOnlyList<RecoveryHotkeyBinding> configured = GetRecoveryHotkeys();
        var result = new List<RecoveryHotkeyBinding>(configured.Count + 1);
        if (string.Equals(AltF4Mode?.Trim(), "always", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(new RecoveryHotkeyBinding(
                RecoveryHotkeyAction.AltF4,
                RecoveryHotkeyMode.Always,
                nameof(AltF4Mode),
                new RecoveryHotkeyChord(true, false, false, false, 0x73, "Alt+F4")));
        }

        foreach (RecoveryHotkeyBinding binding in configured)
        {
            if (binding.Mode == RecoveryHotkeyMode.Always)
                result.Add(binding);
        }
        return result;
    }

    /// <summary>Returns validation diagnostics without mutating the user's raw JSON values.</summary>
    internal IReadOnlyList<HotkeyValidationIssue> ValidateHotkeys()
    {
        var issues = new List<HotkeyValidationIssue>();
        _ = BuildRecoveryHotkeys(issues);
        if (AltF4Mode?.Trim().ToLowerInvariant() is not ("always" or "stressed" or "off"))
        {
            issues.Add(new HotkeyValidationIssue(
                nameof(AltF4Mode),
                "must be always, stressed, or off; using off",
                "off"));
        }
        if (HotkeyDebounceMs is < 50 or > 10_000)
        {
            issues.Add(new HotkeyValidationIssue(
                nameof(HotkeyDebounceMs),
                "must be between 50 and 10,000 ms; value was clamped",
                GetHotkeyDebounceMs().ToString()));
        }
        return issues;
    }

    internal int GetHotkeyDebounceMs()
    {
        return Math.Clamp(HotkeyDebounceMs, 50, 10_000);
    }

    internal int GetCaptureOverlayDurationMs()
    {
        return Math.Clamp(CaptureOverlayDurationMs, 250, 5_000);
    }

    internal int GetIncidentHistoryLimit()
    {
        return Math.Clamp(IncidentHistoryLimit, 10, 500);
    }

    internal int GetWatchdogHeartbeatSeconds()
    {
        return Math.Clamp(WatchdogHeartbeatSeconds, 5, 120);
    }

    internal int GetWatchdogRelaunchDelaySeconds()
    {
        return Math.Clamp(WatchdogRelaunchDelaySeconds, 1, 30);
    }

    internal bool ShouldRunThawWatchdog =>
        string.Equals(ThawWatchdogMode?.Trim(), "relaunch", StringComparison.OrdinalIgnoreCase);

    internal bool ShouldRunRescueBroker =>
        string.Equals(RescueBrokerMode?.Trim(), "relaunch", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reports unsafe or ambiguous values without changing the user's JSON. Runtime
    /// accessors clamp bounded values and mode checks fail closed to "off".
    /// </summary>
    internal IReadOnlyList<ConfigValidationIssue> ValidateSettings()
    {
        var issues = new List<ConfigValidationIssue>();
        if (CaptureOverlayDurationMs is < 250 or > 5_000)
            issues.Add(new ConfigValidationIssue(nameof(CaptureOverlayDurationMs), "must be between 250 and 5,000 ms; value will be clamped", GetCaptureOverlayDurationMs().ToString()));
        if (IncidentHistoryLimit is < 10 or > 500)
            issues.Add(new ConfigValidationIssue(nameof(IncidentHistoryLimit), "must be between 10 and 500; value will be clamped", GetIncidentHistoryLimit().ToString()));
        if (WatchdogHeartbeatSeconds is < 5 or > 120)
            issues.Add(new ConfigValidationIssue(nameof(WatchdogHeartbeatSeconds), "must be between 5 and 120 seconds; value will be clamped", GetWatchdogHeartbeatSeconds().ToString()));
        if (WatchdogRelaunchDelaySeconds is < 1 or > 30)
            issues.Add(new ConfigValidationIssue(nameof(WatchdogRelaunchDelaySeconds), "must be between 1 and 30 seconds; value will be clamped", GetWatchdogRelaunchDelaySeconds().ToString()));
        if (!IsHelperMode(ThawWatchdogMode))
            issues.Add(new ConfigValidationIssue(nameof(ThawWatchdogMode), "must be off or relaunch; using off", "off"));
        if (!IsHelperMode(RescueBrokerMode))
            issues.Add(new ConfigValidationIssue(nameof(RescueBrokerMode), "must be off or relaunch; using off", "off"));
        if (PowerPlanRestoreAfterSeconds < 0 || PowerPlanRestoreAfterSeconds > 86_400)
            issues.Add(new ConfigValidationIssue(nameof(PowerPlanRestoreAfterSeconds), "must be between 0 and 86,400 seconds; value will be bounded", Math.Clamp(PowerPlanRestoreAfterSeconds, 0, 86_400).ToString()));
        return issues;
    }

    internal bool IsCompatibilitySafe => ValidateHotkeys().Count == 0 && ValidateSettings().Count == 0;

    private static bool IsHelperMode(string? value)
    {
        return string.Equals(value?.Trim(), "off", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value?.Trim(), "relaunch", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureCompatibilityDefaults()
    {
        AltF4Mode ??= DefaultAltF4Mode;
        PanicHotkey ??= DefaultPanicHotkey;
        SlownessHotkey ??= DefaultSlownessHotkey;
        SlownessHotkeyMode ??= "stressed";
        FrameDropHotkey ??= DefaultFrameDropHotkey;
        FrameDropHotkeyMode ??= "always";
        ThawWatchdogMode ??= DefaultWatchdogMode;
        RescueBrokerMode ??= DefaultRescueBrokerMode;
    }

    public (bool Alt, bool Ctrl, bool Shift, int Vk) GetPanicHotkey()
    {
        RecoveryHotkeyChord chord = BuildRecoveryHotkeys(null)
            .First(binding => binding.Action == RecoveryHotkeyAction.Panic).Chord;
        return (chord.Alt, chord.Ctrl, chord.Shift, chord.Vk);
    }

    internal RecoveryHotkeyChord GetPanicChord()
    {
        return BuildRecoveryHotkeys(null)
            .First(binding => binding.Action == RecoveryHotkeyAction.Panic).Chord;
    }

    private IReadOnlyList<RecoveryHotkeyBinding> BuildRecoveryHotkeys(List<HotkeyValidationIssue>? issues)
    {
        var result = new List<RecoveryHotkeyBinding>(3);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddBinding(result, used, issues, RecoveryHotkeyAction.Panic, RecoveryHotkeyMode.Always,
            nameof(PanicHotkey), PanicHotkey, DefaultPanicHotkey);
        AddBinding(result, used, issues, RecoveryHotkeyAction.Slowness,
            ParseMode(SlownessHotkeyMode, nameof(SlownessHotkeyMode), issues),
            nameof(SlownessHotkey), SlownessHotkey, DefaultSlownessHotkey);
        AddBinding(result, used, issues, RecoveryHotkeyAction.FrameDrop,
            ParseMode(FrameDropHotkeyMode, nameof(FrameDropHotkeyMode), issues),
            nameof(FrameDropHotkey), FrameDropHotkey, DefaultFrameDropHotkey);

        return result;
    }

    private static void AddBinding(
        List<RecoveryHotkeyBinding> result,
        HashSet<string> used,
        List<HotkeyValidationIssue>? issues,
        RecoveryHotkeyAction action,
        RecoveryHotkeyMode mode,
        string settingName,
        string? raw,
        string defaultText)
    {
        string effectiveText = raw?.Trim() ?? string.Empty;
        if (!TryParseHotkey(effectiveText, out RecoveryHotkeyChord chord, out string error))
        {
            effectiveText = defaultText;
            if (!TryParseHotkey(effectiveText, out chord, out _))
                return; // Defensive: constants are validated by the same parser.
            issues?.Add(new HotkeyValidationIssue(
                settingName,
                error,
                defaultText));
        }

        if (mode != RecoveryHotkeyMode.Off && !used.Add(chord.Signature))
        {
            issues?.Add(new HotkeyValidationIssue(
                settingName,
                "duplicates another recovery chord and was disabled",
                "off"));
            return;
        }

        // An explicitly disabled mode remains represented. This lets the hook keep one
        // deterministic binding table and makes a later config reload straightforward;
        // disabled chords do not reserve a key for collision purposes.
        result.Add(new RecoveryHotkeyBinding(action, mode, settingName, chord));
    }

    private static RecoveryHotkeyMode ParseMode(
        string? raw,
        string settingName,
        List<HotkeyValidationIssue>? issues)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "always": return RecoveryHotkeyMode.Always;
            case "stressed": return RecoveryHotkeyMode.Stressed;
            case "off": return RecoveryHotkeyMode.Off;
            default:
                issues?.Add(new HotkeyValidationIssue(
                    settingName,
                    "must be always, stressed, or off; using off",
                    "off"));
                return RecoveryHotkeyMode.Off;
        }
    }

    /// <summary>
    /// Parses only chords with at least two modifiers. This intentionally rejects plain
    /// letters/function keys and common one-modifier shortcuts so Thaw never steals normal
    /// application input. Ctrl+Alt is the default emergency shape because it is reachable
    /// with one hand and is rarely claimed by applications.
    /// </summary>
    internal static bool TryParseHotkey(string? value, out RecoveryHotkeyChord chord, out string error)
    {
        chord = default;
        error = "invalid hotkey";
        string[] parts = (value ?? string.Empty)
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            error = "a recovery chord must contain at least two modifiers and a key";
            return false;
        }

        bool alt = false, ctrl = false, shift = false, win = false;
        int vk = 0;
        bool keySeen = false;
        foreach (string part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "alt":
                case "menu":
                    if (alt) { error = "duplicate Alt modifier"; return false; }
                    alt = true;
                    continue;
                case "ctrl":
                case "control":
                    if (ctrl) { error = "duplicate Ctrl modifier"; return false; }
                    ctrl = true;
                    continue;
                case "shift":
                    if (shift) { error = "duplicate Shift modifier"; return false; }
                    shift = true;
                    continue;
                case "win":
                case "windows":
                case "meta":
                case "super":
                    if (win) { error = "duplicate Win modifier"; return false; }
                    win = true;
                    continue;
            }

            if (keySeen)
            {
                error = "a recovery chord may contain exactly one non-modifier key";
                return false;
            }
            if (!TryParseKey(part, out vk))
            {
                error = $"unknown key '{part}'";
                return false;
            }
            keySeen = true;
        }

        int modifierCount = (alt ? 1 : 0) + (ctrl ? 1 : 0) + (shift ? 1 : 0) + (win ? 1 : 0);
        if (!keySeen)
        {
            error = "a recovery chord must include a key";
            return false;
        }
        if (modifierCount < 2)
        {
            error = "a recovery chord must contain at least two modifiers";
            return false;
        }
        if (IsReservedChord(alt, ctrl, shift, win, vk))
        {
            error = "this chord is reserved by Windows or is a common application shortcut";
            return false;
        }

        string canonical = FormatHotkey(alt, ctrl, shift, win, vk);
        chord = new RecoveryHotkeyChord(alt, ctrl, shift, win, vk, canonical);
        return true;
    }

    private static bool TryParseKey(string token, out int vk)
    {
        vk = 0;
        if (token.Length == 1 && char.IsAsciiLetter(token[0]))
        {
            vk = char.ToUpperInvariant(token[0]);
            return true;
        }
        if (token.Length == 1 && char.IsAsciiDigit(token[0]))
        {
            vk = token[0];
            return true;
        }
        if (token.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(token.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
        {
            vk = 0x70 + fn - 1;
            return true;
        }

        vk = token.ToLowerInvariant() switch
        {
            "esc" or "escape" => 0x1B,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "space" => 0x20,
            "backspace" or "back" => 0x08,
            "insert" or "ins" => 0x2D,
            "delete" or "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "printscreen" or "prtsc" => 0x2C,
            "pause" => 0x13,
            "apps" or "menu" => 0x5D,
            _ => 0,
        };
        return vk != 0;
    }

    private static bool IsReservedChord(bool alt, bool ctrl, bool shift, bool win, int vk)
    {
        // Alt+F4 is an existing, separately-modeled binding. Do not let a second
        // action compete with it, and keep well-known Windows/application chords intact.
        if (alt && !ctrl && !shift && !win && vk == 0x73) return true;
        if (alt && !ctrl && !shift && !win && vk == 0x09) return true; // Alt+Tab
        if (ctrl && shift && !alt && !win && vk == 0x1B) return true; // Ctrl+Shift+Esc
        if (win && !alt && !ctrl && !shift && vk is 0x4C or 0x52 or 0x44 or 0x47) return true;
        if (win && shift && !alt && !ctrl && vk == 0x53) return true; // Win+Shift+S
        if (ctrl && alt && !shift && !win && vk == 0x2E) return true; // Ctrl+Alt+Del
        return false;
    }

    private static string FormatHotkey(bool alt, bool ctrl, bool shift, bool win, int vk)
    {
        var parts = new List<string>(5);
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        string key = vk switch
        {
            >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1),
            0x1B => "Esc",
            0x09 => "Tab",
            0x0D => "Enter",
            0x20 => "Space",
            0x08 => "Backspace",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            _ when vk >= 0x30 && vk <= 0x39 => ((char)vk).ToString(),
            _ when vk >= 0x41 && vk <= 0x5A => ((char)vk).ToString(),
            _ => $"VK_{vk:X2}",
        };
        parts.Add(key);
        return string.Join('+', parts);
    }

    // ---------- persistence ----------

    internal static string DefaultPath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Thaw");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    internal static Config Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<Config>(json, JsonOpts);
                if (cfg is not null)
                {
                    cfg.EnsureCompatibilityDefaults();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Config load failed, using defaults: " + ex.Message);
        }
        return new Config();
    }

    internal static void Save(string path, Config cfg)
    {
        string? temporaryPath = null;
        try
        {
            cfg.EnsureCompatibilityDefaults();
            string json = JsonSerializer.Serialize(cfg, JsonOpts);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            temporaryPath = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Config save failed: " + ex.Message);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
