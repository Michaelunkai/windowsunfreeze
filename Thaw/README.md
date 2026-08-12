# Thaw — Instant Unfreezer ❄⚡

**Thaw** lives in your system tray and turns **Alt+F4** into an instant, maximum-performance
unfreeze for your whole PC — without closing, pausing, suspending or interrupting a single
process.

When the PC starts to slow down, stutter or lock up, just press **Alt+F4** and Thaw
immediately, all in one shot:
1. **Resets the GPU driver** (Ctrl+Shift+Win+B) — unsticks a frozen/stuttering display,
2. **soft-freeze rescue**: if the screen is *still* frozen ~1 s after the reset (the
   compositor window is hung or the system is still stalled), **restarts dwm.exe** so
   Windows respawns a fresh desktop compositor,
3. trims the working set of **every** process (frees physical RAM instantly),
4. purges the **standby + modified memory lists** and flushes the **system file cache** (admin),
5. boosts the **foreground app** and the shell (explorer/dwm) to High priority,
6. switches to the **High Performance power plan** (admin, restored 2 min later),
7. **force-restarts explorer.exe** (shell/taskbar) — the classic freeze fix,
8. raises its own priority to Time-Critical and tightens the **timer resolution** to 1 ms.

No application except `explorer.exe` is ever terminated — your games, downloads and
browser sessions keep running. Explorer restarts in ~2 s and reopens your File Explorer
windows.

> ⚠️ **Alt+F4 now means "unfreeze", not "close window", while Thaw is running.**
> That's the whole point. To close a window, use the **X** button (or change the
> Alt+F4 behaviour in the tray menu → "Alt+F4 behaviour").

---

## Quick start

1. Run **`Thaw\publish\Thaw.exe`** — no install, no console window, nothing but a tray icon.
2. Right-click the tray icon → **Start with Windows** if you want it always on duty.
3. Done. Press **Alt+F4** whenever the PC feels stuck.

| Action | Effect |
|---|---|
| **Alt+F4** | Instant unfreeze (always — that's the point) |
| **Ctrl+Alt+U** | Panic unfreeze — works even if Alt+F4 is disabled |
| **Double-click tray icon** | Unfreeze |
| Tray menu → **⚡ Unfreeze Now** | Unfreeze |

The tray icon tells you the state of the machine in real time (hover for CPU/RAM):

- ❄ **Blue ice + golden bolt** — monitoring, healthy. Alt+F4 will unfreeze.
- 🔴 **Red ring + red bolt** — the watchdog has detected a stall or heavy load.
  Alt+F4 now, or it will auto-unfreeze itself on hard stalls.

After every unfreeze a balloon reports what happened, e.g.:

> **Thaw ⚡ Unfrozen** — Unfrozen in 879 ms · tuned 334 processes · boosted 6 apps · +563 MB RAM

---

## How it works

- **Keyboard hook** (`KeyboardHook.cs`) — a global low-level `WH_KEYBOARD_LL` hook that
  intercepts Alt+F4 before any window sees it. Alt+F4 is swallowed and converted into an
  unfreeze command. `Ctrl+Alt+U` (configurable) is the always-on panic hotkey.
- **Watchdog** (`Watchdog.cs`) — a high-priority thread samples the machine every second:
  scheduling delay (how long the OS actually froze before this thread got to run again),
  CPU% (`GetSystemTimes`) and RAM% (`GlobalMemoryStatusEx`). It drives the alert icon and
  fires the **automatic unfreeze** when a hard stall (>6 s) is detected.
- **Unfreezer** (`Unfreezer.cs`) — the engine, running at Time-Critical priority:
  1. Own priority → High, thread → Time-Critical, `timeBeginPeriod(1)`.
  2. **GPU driver reset** — injects Ctrl+Shift+Win+B via `SendInput` (the documented
     Windows graphics-driver reset; the screen may blank for ~1 s).
  3. **DWM soft-freeze rescue** — ~1 s after the reset, checks whether the compositor
     window (`DwmNotificationWindow` / `Dwm`, or any top-level window of the dwm process)
     is unresponsive (`IsHungAppWindow`) or the watchdog still reports a stall; if so,
     terminates dwm.exe so Windows respawns the compositor (screen goes black ~1–2 s).
  4. `EmptyWorkingSet` on every process → physical RAM is freed instantly.
  4. `NtSetSystemInformation(SystemMemoryListInformation, …)` → purges the standby **and**
     modified page lists + `SetSystemFileCacheSize(-1,-1,0)` (needs admin).
  5. Foreground app + explorer/dwm → High priority class.
  6. High Performance power plan (`PowerSetActiveScheme`, restores the previous plan).
  7. **Explorer restart** — `TerminateProcess` on explorer.exe, 2 s pause, then relaunch
     (equivalent of `Stop-Process explorer -Force; Start-Process explorer.exe`).

## Configuration

Config file: **`%APPDATA%\Thaw\config.json`** (create it on first run / open via
tray menu → *Open Config File*; apply changes via tray menu → *Reload Config*).

```jsonc
{
  "AltF4Mode": "always",          // "always" | "stressed" | "off"
  "PanicHotkey": "Ctrl+Alt+U",    // always unfreezes, e.g. "Alt+F4", "Ctrl+Shift+U", "F12"
  "AutoUnfreezeOnStall": true,    // auto-unfreeze after a hard stall (6 s default)
  "ShowBalloons": true,           // unfreeze result balloons
  "DebugLog": false,              // verbose key/hook logging to %LOCALAPPDATA%\Thaw\log.txt
  "StallThresholdMs": 2500,       // scheduling delay that flags the system as stressed
  "HardStallMs": 6000,            // stall that triggers the automatic unfreeze
  "CpuStressPercent": 90,         // CPU% that counts as fully stressed
  "MemStressPercent": 92,         // RAM% that counts as fully stressed
  "PowerPlanBoost": true,         // (admin) switch to High Performance on unfreeze
  "PowerPlanRestoreAfterSeconds": 120  // restore previous plan after N s (0 = keep)
  "RestartExplorerOnUnfreeze": true,   // force-restart explorer.exe on every unfreeze
  "ResetGpuDriver": true,              // send Ctrl+Shift+Win+B on every unfreeze
  "RestartDwmOnFrozenScreen": true     // restart dwm.exe if screen is still frozen after the GPU reset
}
```

- **`"always"`** — Alt+F4 always unfreezes (recommended, the default). Window closing via
  Alt+F4 is disabled; use the X button.
- **`"stressed"`** — Alt+F4 unfreezes only while the watchdog detects the system is slow/stuck;
  when healthy it passes through and closes windows normally.
- **`"off"`** — Alt+F4 is never intercepted; only the panic hotkey unfreezes.

## Running as administrator (recommended)

The deepest memory tricks (standby-list purge, file-cache flush, power plan) require an
elevated token. If Thaw is not elevated it still works — it just skips those three steps.
To get full power: tray menu → **Restart as Administrator** (one UAC prompt, then it runs
elevated from then on).

## Logs

Everything Thaw does is written to **`%LOCALAPPDATA%\Thaw\log.txt`** (rotated at 1 MB).
This is the best way to confirm it worked after a freeze:

```
[INF] Unfreeze triggered (Hotkey)
[INF] Unfreeze done: Unfrozen in 2656 ms · tuned 327 processes · boosted 6 apps · +3100 MB RAM
[INF] Standby list purge: NTSTATUS 0x00000000
[INF] System file cache flush: True
```

## Honest limits (important)

- **A truly hard lock** (kernel panic, driver deadlock, power failure) cannot be fixed by any
  user-mode application — nothing can run while the CPU is frozen. Thaw covers every
  "slow / stuttering / unresponsive-but-recovering" state, which is what 99% of everyday
  freezes are.
- **GPU driver TDR** (display hangs) and **disk hardware** failures are outside an app's reach.
- `EmptyWorkingSet` briefly trims RAM caches; apps reload their pages on demand. This is the
  same technique RAMMap/EmptyStandbyList/CleanMem use — safe and non-destructive.
- **Explorer restart is intentional and requested** — it is the one process Thaw restarts.
  It takes ~2 s and reopens your File Explorer windows. No other application is ever
  terminated, suspended, or paused.
- The GPU reset may blank the screen for ~1 second — that is the driver being reloaded.
- The DWM rescue only fires when the screen is *still* frozen after the GPU reset — on a
  healthy screen nothing extra happens. When it does fire, the display goes black ~1–2 s
  while the compositor respawns.
- If Thaw runs elevated, the restarted explorer runs elevated (same as running
  `Start-Process explorer.exe` from an admin prompt).

## Building from source

The machine-wide .NET SDKs here are incomplete, so the project pins a **project-local SDK**
(`Thaw\.dotnet`, installed via `dotnet-install.ps1`):

```bash
cd Thaw
./.dotnet/dotnet.exe build -c Release
./.dotnet/dotnet.exe publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Output: **`Thaw\publish\Thaw.exe`** — a single ~480 KB file. Needs the .NET 10 Desktop
Runtime (already installed on this machine). For a fully standalone exe that needs nothing
installed:

```bash
./.dotnet/dotnet.exe publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-standalone
```

## Project layout

```
Thaw/
├── Thaw.csproj          # WinForms app, WinExe (no console), .NET 10
├── Program.cs           # entry point, CLI flags, single-instance mutex
├── AppContext.cs        # tray icon + menu + live status + wiring
├── KeyboardHook.cs      # global Alt+F4 / panic-hotkey hook
├── Watchdog.cs          # stress detector (stall/CPU/RAM) + auto-unfreeze
├── Unfreezer.cs         # the unfreeze engine (never kills/pauses anything)
├── Config.cs            # %APPDATA%\Thaw\config.json
├── Icons.cs             # GDI+ icon artwork (shattered ice + bolt), ICO writer
├── Autostart.cs         # "Start with Windows" registry toggle
├── Log.cs               # rotating file log
├── Native.cs            # Win32/ntdll/powrprof P/Invoke
├── app.manifest         # asInvoker, PerMonitorV2 DPI
└── Assets/Thaw.ico      # multi-size app icon (generated by --emit-icon)
```
