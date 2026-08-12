using System.Text.Json;

namespace Thaw;

/// <summary>User configuration, stored as JSON in %APPDATA%\Thaw\config.json.</summary>
internal sealed class Config
{
    /// <summary>
    /// "always"  — Alt+F4 ALWAYS unfreezes, instantly and without touching any process.
    ///             Normal window closing via Alt+F4 is disabled while Thaw runs (the X button still works).
    /// "stressed"— Alt+F4 unfreezes only while the system is slow/stuck; passes through when healthy.
    /// "off"     — Alt+F4 is never intercepted (only the panic hotkey unfreezes).
    /// </summary>
    public string AltF4Mode { get; set; } = "always";

    /// <summary>Panic hotkey that always unfreezes, e.g. "Ctrl+Alt+U".</summary>
    public string PanicHotkey { get; set; } = "Ctrl+Alt+U";

    public bool AutoUnfreezeOnStall { get; set; } = true;
    public bool ShowBalloons { get; set; } = true;
    public bool DebugLog { get; set; } = false;

    /// <summary>Scheduling delay (ms) above which the system is considered stressed.</summary>
    public int StallThresholdMs { get; set; } = 2500;

    /// <summary>Hard stall (ms) that triggers the automatic unfreeze.</summary>
    public int HardStallMs { get; set; } = 6000;

    /// <summary>CPU% contributing full stress weight at or above this value.</summary>
    public int CpuStressPercent { get; set; } = 90;

    /// <summary>RAM% contributing full stress weight at or above this value.</summary>
    public int MemStressPercent { get; set; } = 92;

    /// <summary>Switches to the High Performance power plan on unfreeze (admin only).</summary>
    public bool PowerPlanBoost { get; set; } = true;

    /// <summary>Restore the previous power plan after this many seconds (0 = leave High Performance active).</summary>
    public int PowerPlanRestoreAfterSeconds { get; set; } = 120;

    /// <summary>Force-restarts explorer.exe (shell/taskbar) as part of every unfreeze.
    /// This is the equivalent of `Stop-Process -Name explorer -Force; Start-Process explorer.exe`.</summary>
    public bool RestartExplorerOnUnfreeze { get; set; } = true;

    /// <summary>Sends Ctrl+Shift+Win+B (the Windows graphics-driver reset) as part of every unfreeze.
    /// The screen may blank for ~1 second.</summary>
    public bool ResetGpuDriver { get; set; } = true;

    /// <summary>Rescue step: if the screen is still frozen about 1 s after the GPU reset
    /// (compositor hung or an ongoing stall), restart dwm.exe so Windows respawns a
    /// fresh desktop compositor. The screen goes black for ~1–2 s.</summary>
    public bool RestartDwmOnFrozenScreen { get; set; } = true;

    // ---------- parsed hotkey ----------

    public (bool Alt, bool Ctrl, bool Shift, int Vk) GetPanicHotkey()
    {
        bool alt = false, ctrl = false, shift = false;
        int vk = 0x55; // default 'U'
        try
        {
            string s = PanicHotkey?.Trim() ?? "";
            string[] parts = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string part in parts)
            {
                switch (part.ToLowerInvariant())
                {
                    case "alt": alt = true; break;
                    case "ctrl": ctrl = true; break;
                    case "shift": shift = true; break;
                    default:
                        vk = ParseKey(part);
                        break;
                }
            }
        }
        catch
        {
            // Fall back to defaults.
        }
        return (alt, ctrl, shift, vk);
    }

    private static int ParseKey(string token)
    {
        if (token.Length == 1 && char.IsAsciiLetter(token[0])) return char.ToUpperInvariant(token[0]);
        if (token.Length == 1 && char.IsAsciiDigit(token[0])) return token[0];
        if (token.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(token.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
            return 0x70 + fn - 1;
        return 0x55;
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
                if (cfg is not null) return cfg;
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
        try
        {
            string json = JsonSerializer.Serialize(cfg, JsonOpts);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warn("Config save failed: " + ex.Message);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
