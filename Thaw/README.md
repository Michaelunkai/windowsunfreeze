# Thaw — Instant Unfreezer ❄⚡

Version **1.4.9**

**Thaw** lives in your system tray and provides keyboard-first recovery for a PC that is
slow, stuttering, dropping frames, or temporarily unresponsive. It does not promise to
repair a kernel deadlock or failing hardware; every recovery step is best-effort and
should be reviewed before it is enabled.

The tray exposes three deliberately different actions:

1. **Normal recovery** — runs the slowdown/system profile: bounded diagnostics, a temporary
   Above Normal responsiveness boost, and pressure-gated memory/cache work. It does not
   reset the display or change the active power plan.
2. **Strong shell recovery** — restarts `explorer.exe` only, after a confirmation warning.
   The taskbar, desktop icons, and File Explorer windows can disappear briefly.
3. **Emergency recovery** — the same sequence as the panic hotkey and bypasses the small
   duplicate-request cooldown. It still cannot overlap an already-running recovery.

> ⚠️ **Alt+F4 may mean “recover” rather than “close window” while its mode is `always`.**
> Use the window’s **X** button, choose `stressed`/`off`, or use the tray’s hotkey help
> surface when normal close behavior is needed.

---

## Download

- **Recommended:** [Download the Windows installer](https://github.com/Michaelunkai/windowsunfreeze/releases/latest/download/Thaw-Setup.exe)
- **No installation:** [Download the portable executable](https://github.com/Michaelunkai/windowsunfreeze/releases/latest/download/Thaw-Portable.exe)
- Verify either download with [`SHA256SUMS.txt`](https://github.com/Michaelunkai/windowsunfreeze/releases/latest/download/SHA256SUMS.txt).

The installer is self-contained for 64-bit Windows 10/11, adds Start-menu and uninstall
entries, offers an optional desktop shortcut, and does not automatically enable Start with
Windows. The binaries are currently unsigned, so Windows SmartScreen may ask for confirmation.

## Quick start

1. Install **`Thaw-Setup.exe`**, or run **`Thaw-Portable.exe`** without installing.
2. Right-click the tray icon and read **Hotkeys & safety help…** before enabling a
   recovery action.
3. Use **Start with Windows** only if you want Thaw always on duty. Run as Administrator
   only when you have reviewed the system-level actions that need it.

| Action | Effect |
|---|---|
| **Alt+F4** | Force-runs the full bounded recovery graph concurrently: display, foreground, memory, shell, power/network cache, desktop refresh, and diagnostics |
| **Ctrl+Alt+U** | Emergency recovery; always intercepted by the hook |
| **Ctrl+Alt+S** | Slowdown/system recovery when the configured mode allows it |
| **Ctrl+Alt+G** | Frame-drop/display recovery; requests the Windows graphics reset |
| **Double-click tray icon** | Normal configured recovery |
| Tray menu → **Normal recovery** | Configured recovery sequence |
| Tray menu → **Strong shell recovery** | Confirmed Explorer-only restart |

The tray icon tells you the state of the machine in real time (hover for CPU/RAM):

- ❄ **Blue ice + golden bolt** — monitoring, currently healthy.
- 🔴 **Red ring + red bolt** — the watchdog has detected a stall or heavy load.
  The tray status shows whether automatic recovery is enabled.
- The tray menu also shows **ready**, **cooldown**, or **recovery in progress**, plus
  whether the process is a standard user or Administrator.

After every recovery a balloon reports the evidence result, e.g.:

> **Thaw ⚡ Unfrozen** — Recovery verified in 879 ms · trimmed eligible processes · boosted 6 apps · +563 MB RAM

When a configured recovery chord is captured, Thaw first shows a short non-activating
overlay and (when enabled) an immediate balloon. An optional Windows notification sound is
off by default. This acknowledgement means only that the hook captured the chord; it is not
proof that a GPU reset, cache operation, or other Windows request completed.

The tray also exposes bounded **Incident history** and **Export support bundle** commands.
Receipts contain aggregate duration, action counts, diagnostics, and budget status. The ZIP
contains Thaw's log/config/receipts and read-only diagnostics; it does not capture arbitrary
application memory or process dumps.

---

## How it works

- **Keyboard hook** (`KeyboardHook.cs`) — a global low-level `WH_KEYBOARD_LL` hook
  observes Alt+F4 plus configurable panic, slowdown, and frame-drop chords. Depending on `AltF4Mode`, Alt+F4
  is either swallowed and converted into a recovery request or passed through unchanged.
  The panic hotkey is always intercepted. A hook callback is not proof that recovery
  completed; check the log and the tray result.
- **Hook resilience** — the hook owns a dedicated highest-priority native message thread,
  250/500 ms heartbeats, bounded 3/5-second renewal, a preallocated dispatch ring, and a
  per-user rescue event so WinForms work cannot delay Alt+F4 capture. Version 1.4 adds a
  second independent low-level hook, lock-free callback capture, a reserved emergency
  dispatch slot, callback/drop counters for the rare saturated path, and an independent
  supervisor that recreates a hook thread if its native message loop ever exits. The
  dispatch workers also isolate delivery/queue exceptions and retry their loops without
  clearing already-captured requests; the final worker is an independent pre-warmed
  last-resort slot. Each event subscriber is isolated individually, and AltGr text input
  is excluded from Ctrl+Alt recovery matching. Recovery requests are handed to a pre-warmed
  high-priority worker, with bounded recreation only if that worker itself exits. A capture
  carries the exact successfully-published rescue sequence through dispatch; failed event
  pulses cannot acknowledge an older request. Both dispatch workers poll their queues when a
  wake pulse fails, and the hook message loops perform bounded health ticks even if a timer
  stops delivering messages.
- **Rescue fallback** — when `RescueBrokerMode` is `relaunch`, a Thaw-only helper watches
  the per-user signal and acknowledgement events. It also registers always-on panic,
  frame-drop, and Alt+F4 (when `AltF4Mode` is `always`) chords with `RegisterHotKey`; the
  main process keeps a matching in-process fallback as well. If the main process does not
  acknowledge a capture, the helper starts one headless bounded force-all recovery. This
  is an emergency fallback, not a second normal recovery loop, and it never runs arbitrary
  commands. Its registered-hotkey message loop re-registers after a recoverable
  message/queue failure, retries rejected registrations, renews them periodically, and
  refreshes its registrations after a live config change; bounded message-queue polling keeps
  those health checks alive if `SetTimer` fails. The parent helper recreates the monitor thread
  if it exits.
- **Watchdog** (`Watchdog.cs`) — samples scheduling delay, CPU, and RAM each second.
  It drives the alert icon and can request automatic recovery after a hard stall when
  `AutoUnfreezeOnStall` is enabled. Automatic recovery is deliberately diagnostics-only;
  explicit shortcuts retain the configured mutation tiers. A failed native/probe sample is
  isolated and retried instead of terminating the detector, and a watchdog trigger is not
  a diagnosis.
- **Telemetry and verification** — a bounded one-Hz ring records scheduler, CPU, RAM,
  commit, disk, DPC/ISR, network, GPU-engine, QoS, foreground-process, and real DWM
  composition/frame evidence. Alt+F4 also starts bounded DWM-frame, foreground WM_NULL and
  wait-chain, desktop-refresh, resource-cause, recent-event, and all-device diagnostics
  alongside recovery and records rollback receipts, including native restore failures.
  The expanded graph is sized so these actions are queued rather than silently dropped.
- **Tray guard** (`AppContext.cs`) — shows health, privilege level, cooldown, and
  in-progress state. It prevents duplicate normal requests while allowing the emergency
  request to bypass the short cooldown. It cannot cancel an engine pass already running.
- **Recovery engine** (`Unfreezer.cs`) — accepts requests through a pre-warmed recovery
  worker and applies only the steps enabled by the current configuration. The major surfaces
  are intentionally documented separately. The Alt+F4 force-all path decides its profile before
  expensive foreground/DWM/Explorer probes and queues the recovery graph first; synchronous
  trigger/diagnostic logging is written only after that handoff, as is the tray acceptance log;
  optional sound and UI feedback are posted asynchronously after handoff, and individual
  pre-dispatch probe failures are isolated so they cannot cancel recovery. Evidence probes
  run independently within their own deadlines, and a wait-boundary fault still closes the
  batch while preserving the active-run fence until late workers drain. Failed runs publish a
  terminal unverified result so the tray can release its retry guard immediately, even when
  optional UI marshaling is unavailable.

  | Surface | What it attempts | Why it may be disruptive |
  |---|---|---|
  | Display | Sends Ctrl+Shift+Win+B through Windows `SendInput`; a screen blank is possible. | SendInput can be blocked or fail; a reset is not guaranteed. |
  | DWM | Checks whether the current-session System32 `dwm.exe` appears hung, then asks Windows to respawn that exact validated process when configured. | The display can go black for 1–2 seconds; detection is imperfect. |
  | Memory/cache | Best-effort working-set and system-list/cache operations for eligible resources. | Pages may fault back in; system-wide cache operations need Administrator. |
  | Priority | Temporarily raises Thaw and selected foreground/shell work from Normal to Above Normal, then restores it. | Scheduling priority cannot fix I/O, GPU, thermal, or hardware limits. |
  | Power | Optional Alt+F4-only temporary High Performance selection with exact end-of-run rollback. | It is machine-wide and is not proof of repair. |
  | Explorer | Optionally restarts `explorer.exe`, the taskbar/shell process. | The shell disappears briefly; this is the only intentional process termination. |

The shortcut also records resource evidence for disk latency/queue, commit and pagefile
pressure, DPC/ISR load, network errors/retransmits, GPU utilization, thermal/frequency
limits, low-memory notifications, foreground responsiveness/I/O, and device/event faults.
These are diagnostic branches because blindly restarting a service, adapter, driver, or
storage device during a freeze can make the session worse.

Normal tray recovery uses the slowdown/system profile; Alt+F4 and emergency recovery combine
the display and system profiles in one run. Strong shell recovery invokes only the Explorer operation after a confirmation. Read the tray safety help and inspect
`%APPDATA%\Thaw\config.json` before using global actions.

## Configuration

Config file: **`%APPDATA%\Thaw\config.json`** (create it on first run / open via
tray menu → *Open Config File*; apply changes via tray menu → *Reload Config*).
Start with a conservative profile and enable one disruptive surface at a time. In
particular, keep automatic recovery off until a manual run and the log have been reviewed.

```jsonc
{
  "AltF4Mode": "always",          // "always" | "stressed" | "off"
  "PanicHotkey": "Ctrl+Alt+U",    // always requests emergency recovery
  "SlownessHotkey": "Ctrl+Alt+S",
  "SlownessHotkeyMode": "stressed",
  "FrameDropHotkey": "Ctrl+Alt+G",
  "FrameDropHotkeyMode": "always",
  "HotkeyDebounceMs": 750,
  "AutoUnfreezeOnStall": false,   // opt in only after manual recovery is trusted
  "ShowBalloons": true,           // completion balloons
  "DebugLog": false,              // verbose key/hook logging to %LOCALAPPDATA%\Thaw\log.txt
  "ShowCaptureOverlay": true,     // immediate non-activating capture acknowledgement
  "ShowImmediateCaptureFeedback": true, // immediate capture balloon (if ShowBalloons=true)
  "PlayRecoverySound": false,     // optional Windows notification sound
  "CaptureOverlayDurationMs": 1200,
  "KeepIncidentHistory": true,    // bounded aggregate receipts in %LOCALAPPDATA%\Thaw
  "IncidentHistoryLimit": 100,
  "EnableSupportBundleExport": true,
  "EnableDetailedActionReporting": true, // used when the engine exposes ActionProgress
  "EnableWindowsApplicationRestart": true, // Windows may relaunch Thaw after a crash
  "ThawWatchdogMode": "off",      // "off" | "relaunch"; Thaw.exe only
  "RescueBrokerMode": "relaunch", // default crash monitor; Thaw.exe only
  "WatchdogHeartbeatSeconds": 15,
  "WatchdogRelaunchDelaySeconds": 3,
  "StallThresholdMs": 2500,       // scheduling delay that flags the system as stressed
  "HardStallMs": 6000,            // stall that triggers the automatic unfreeze
  "CpuStressPercent": 90,         // CPU% that counts as fully stressed
  "MemStressPercent": 92,         // RAM% that counts as fully stressed
  "PowerPlanBoost": false,        // explicit Alt+F4 opt-in: temporary High Performance
  "PowerPlanRestoreAfterSeconds": 120, // bounded compatibility setting; current engine restores at run end
  "RestartExplorerOnUnfreeze": false,  // Alt+F4: force restart Explorer
  "ResetGpuDriver": true,              // used by explicit frame-drop/panic profiles
  "RestartDwmOnFrozenScreen": false    // Alt+F4: force restart DWM
}
```

- **`"always"`** — Alt+F4 always requests full combined recovery. Window closing via Alt+F4
  is disabled; use the X button. Choose this only after reviewing the configured sequence.
- **`"stressed"`** — Alt+F4 requests full combined recovery only while the watchdog detects the system is slow/stuck;
  when healthy it passes through and closes windows normally.
- **`"off"`** — Alt+F4 is never intercepted; only the panic hotkey unfreezes.

`AltF4Mode` controls only the key binding; it does not disable actions enabled in the
configured recovery sequence. The panic key remains the emergency path. The normal tray
action is asynchronous and reports cooldown/in-progress state instead of queueing duplicates.

## Running as administrator

The tray reports **Administrator** or **standard user** explicitly. An elevated token makes
system-level memory-list/cache operations available; it does not make a
display reset, DWM rescue, or Explorer restart safe, guaranteed, or required. If Thaw is
not elevated, those system-level steps are skipped and the result is logged.

Use tray menu → **Restart as Administrator** only after reviewing the current configuration.
The UAC restart is optional and remains under your control.

## Read-only diagnostics and test hooks

These modes help distinguish wiring/configuration problems from a failed recovery:

```text
Thaw.exe --diagnose
Thaw.exe --self-test
```

`--diagnose` reads the environment, privilege level, config, hotkey parsing, and a
  RAM-load sample. `--self-test` adds in-memory normal/alert icon rendering, DWM timing,
  a private rescue signal/acknowledgement round-trip, and the complete Alt+F4 action graph. Both modes
avoid the tray, keyboard hook, GPU/DWM actions, process termination, cache purge, and power
plan changes; they do not prove that a live hotkey can recover a freeze.

`--smoke` starts the tray, hook, watchdog, and icon for a short lifecycle check.
`--test-hook altf4` or `--test-hook panic` drives the real callback and
therefore **does run configured recovery actions**. Use it only with a disposable/reviewed
configuration and confirm the log afterward. It is not a read-only test.

`--watchdog --parent-pid <pid>`, `--rescue-broker --parent-pid <pid>`, and
`--rescue-fallback` are internal,
  Thaw-only helper modes. They refuse to monitor or relaunch a process whose executable path
  is not the current `Thaw.exe`, and they relaunch only after a non-zero parent exit when the
  matching config mode is `relaunch`. They do not restart Explorer, services, drivers, or
  arbitrary commands. The legacy watchdog is off by default. The Thaw-only rescue broker
  defaults to `relaunch` and monitors only the exact running `Thaw.exe` process. The rescue
  fallback performs one bounded force-all pass without creating a tray or acquiring the
  normal single-instance mutex.

## Logs

Everything Thaw does is written to **`%LOCALAPPDATA%\Thaw\log.txt`** (rotated at 1 MB).
This is the primary evidence for what was attempted and what completed; a trigger line alone
does not prove that Windows accepted a SendInput reset or that Explorer/DWM restarted:

```
[INF] Unfreeze triggered (Hotkey)
[INF] Unfreeze done: Recovery verified in 2656 ms · trimmed 2/187 processes · boosted 1 apps · +3100 MB RAM
[INF] GPU reset input accepted (8/8 events)
[INF] Standby list purge: NTSTATUS 0x00000000
[INF] System file cache flush: True
```

Aggregate incident receipts are stored in `%LOCALAPPDATA%\Thaw\incidents.jsonl` when
`KeepIncidentHistory` is enabled. The current engine exposes `Completed(UnfreezeStats)` and
therefore records aggregate duration, action counts, bounded diagnostics, and budget status.
When `EnableDetailedActionReporting` is enabled, the same receipt also persists the engine's
bounded `ProgressReceipts`: action name, phase (started/completed/timeout/skip/rollback),
outcome, elapsed milliseconds, and detail. These are evidence for the reported request and
post-probe result; Thaw does not infer per-action proof from an aggregate completion line.

## Honest limits (important)

- **A truly hard lock** (kernel panic, driver deadlock, power failure) cannot be fixed by any
  user-mode application — nothing can run while the CPU is frozen. Thaw covers every
  "slow / stuttering / unresponsive-but-recovering" state, but it cannot promise recovery
  for every cause.
- **GPU driver TDR** (display hangs) and **disk hardware** failures are outside an app's reach.
- Memory/cache recovery is best-effort. Pages can fault back in, and system-wide cache
  operations may be unavailable or disruptive; do not treat freed memory as proof of repair.
- **Explorer restart is intentional and requested** — it is the one process Thaw may restart.
  It takes ~2 s and can reopen File Explorer windows, but that behavior belongs to Windows.
  No other application is intentionally terminated, suspended, or paused.
- The display reset uses Windows `SendInput`; it may be rejected, have no visible effect,
  or blank the screen for ~1 second. A log entry records an attempt, not driver success.
- The DWM rescue fires only when configured and its heuristic says the screen is still
  frozen; the heuristic can be wrong and the display can go black ~1–2 s.
- **Power plan is opt-in and temporary, not a performance guarantee.** With
  `PowerPlanBoost=true`, an Alt+F4 force-all pass records the active scheme, selects High
  Performance for the bounded pass, and restores the exact prior scheme at run end. A
  crash, power loss, killed helper, or failed `powercfg.exe` call can still prevent
  restoration; the default is false. Thaw never claims that a power-plan change fixed the
  underlying stall, and it does not apply this setting to normal tray recovery.
- Explorer relaunch first uses the interactive user's token and falls back to the current
  user shell launch only when that token path is unavailable.

## Building from source

Build the verified self-contained portable executable and installer with:

```powershell
.\build-release.ps1
```

This requires a .NET 10 SDK and Inno Setup 6. The script prefers the project-local SDK,
publishes a self-contained `win-x64` single file, compiles the installer, and writes:

```text
artifacts\release\Thaw-Setup.exe
artifacts\release\Thaw-Portable.exe
artifacts\release\SHA256SUMS.txt
```

## Project layout

```
Thaw/
├── Thaw.csproj          # WinForms app, WinExe (no console), .NET 10
├── Program.cs           # entry point, CLI flags, single-instance mutex, Thaw-only helpers
├── AppContext.cs        # tray icon + feedback overlay + history/export + wiring
├── KeyboardHook.cs      # global Alt+F4 / panic / slowdown / frame-drop hook
├── Watchdog.cs          # stress detector (stall/CPU/RAM) + auto-unfreeze
├── Unfreezer.cs         # recovery engine; optional Explorer restart is the only kill
├── Config.cs            # %APPDATA%\Thaw\config.json
├── Icons.cs             # GDI+ multi-size ice-shield/lightning artwork, ICO writer
├── Autostart.cs         # "Start with Windows" registry toggle
├── Log.cs               # rotating file log
├── Native.cs            # Win32/ntdll/powrprof P/Invoke
├── NativeDiagnostics.cs # bounded native diagnostics and verification helpers
├── RecoveryCoordinator.cs # concurrent workers, deadlines, receipts, rollback
├── RescueBroker.cs      # per-user rescue event and Thaw-only relaunch helper
├── RescueHotkeyMonitor.cs # helper-side RegisterHotKey emergency receiver
├── Telemetry.cs         # bounded system, foreground, GPU, network, and DWM evidence
├── build-release.ps1    # reproducible portable + installer release build
├── installer/Thaw.iss  # Inno Setup installer definition
├── app.manifest         # asInvoker, PerMonitorV2 DPI
└── Assets/Thaw.ico      # multi-size app icon (generated by --emit-icon)
```
