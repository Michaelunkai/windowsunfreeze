# Thaw — Complete Unfreeze Methods Reference

**Every single technique used to unfreeze a stuck/slow Windows PC, documented for future
sessions.** Each entry explains *what* it does, *why* it helps, the *exact API + signatures*,
*where it lives in the code*, *admin requirements*, and *caveats*.

Project layout: `Thaw/` — `Unfreezer.cs` (engine), `Watchdog.cs` (detection),
`KeyboardHook.cs` (Alt+F4 plus panic/slowdown/frame-drop chords), `RescueHotkeyMonitor.cs` (helper fallback), `Native.cs` (all P/Invoke), `AppContext.cs` (tray + wiring),
`Config.cs` (settings), `Icons.cs` (artwork).

---

## 0. Recovery surfaces and sequence

Normal tray recovery uses the slowdown/system profile. Alt+F4 force-runs every configured
display, DWM, system, memory/cache, Explorer, optional temporary power, desktop-refresh,
foreground-probe, and resource-diagnostic tier concurrently. The complete graph is queued
into a 24-worker pre-warmed pool, so the diagnostic tail is not silently dropped.
Ctrl+Alt+S selects system recovery directly;
Ctrl+Alt+G selects display recovery; the panic chord also combines both profiles.
An enabled automatic trigger uses a diagnostics-only safe profile.
Strong shell recovery is a separate, confirmation-gated call to `RestartExplorerNow()`.

When enabled, the normal/emergency sequence is:

| # | Method | File / function | Admin? |
|---|---|---|---|
| 1 | Own process/thread → bounded Above Normal/Highest recovery priority | `Unfreezer.Run` | no |
| 2 | Profile selection + watchdog diagnostics | `AppContext` / `Unfreezer.Run` | no |
| 3 | GPU driver reset for frame-drop or panic profile (Ctrl+Shift+Win+B) | `Unfreezer.ResetGpuDriver` | SendInput may fail |
| 4 | Panic-only optional DWM rescue after independent hung + stall evidence | `Unfreezer.IsScreenStillFrozen` / `RestartDwm` | opt-in; disruptive |
| 5 | Best-effort working-set tuning for eligible processes | `Unfreezer.TrimWorkingSet` | partial (per-process ACL) |
| 6 | Optional standby + modified page list purge | `Unfreezer.PurgeStandbyList` / `PurgeModifiedPageList` | admin; system-wide |
| 7 | Optional system file cache flush | `Unfreezer.FlushFileCache` | admin; system-wide |
| 8 | Normal-priority foreground/shell → Above Normal temporarily | `Unfreezer.BoostTemporarily` | partial; restored in `finally` |
| 9 | Optional temporary High Performance plan with exact end-of-run restore | `Unfreezer.TryBoostPowerPlanWithJournal` (Alt+F4 only) | admin; machine-wide; restore can be lost on crash |
| 10 | Optional Explorer restart (shell/taskbar) | `Unfreezer.RestartExplorer` | intentional process termination |
| 11 | Tray icon re-registration (after Explorer restart) | `AppContext.ReAddTrayIcon` | — |
| 12 | Bounded desktop/theme refresh broadcast | `Native.TryBroadcastDesktopRefresh` | no; hung recipients are timed out |
| 13 | Foreground `WM_NULL` responsiveness probe | `NativeDiagnostics.TryProbeWindow` | read-only |
| 14 | Resource-cause receipt (commit, pagefile, disk, queue, DPC/ISR, network, GPU, thermal, frames, I/O) | `Unfreezer.BuildResourceDiagnostic` | read-only |
| 15 | Recent event correlation and all present-device problem enumeration | `NativeDiagnostics.CorrelateRecentEvents` / `EnumerateDeviceProblems` | read-only; bounded |

The force profile dispatches independent actions concurrently; Explorer restart, when
enabled, is still restricted to the exact interactive shell process. A completion event and
the log are the evidence of an attempted sequence; a trigger line alone does not prove that
Windows accepted every operation. AppContext
also records a bounded aggregate incident receipt plus the engine's per-action phase/outcome/
elapsed/detail and rollback receipts, and shows immediate capture feedback before the worker
starts.

---

## 1. Bounded recovery priority

**Why:** when the PC is slammed, the recovery worker still needs CPU time. Thaw uses
Above Normal for its process only when it began at Normal and `ThreadPriority.Highest`
for the bounded worker; it never uses Real-Time or Time-Critical scheduling.

```csharp
Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
Thread.CurrentThread.Priority = ThreadPriority.Highest;
```

Restored to Normal afterwards in the `finally` block.

- File: `Unfreezer.cs` → `Run()` start / `finally`.
- Implementation uses managed `Process.PriorityClass` / `Thread.Priority` and restores both.

---

## 2. Timer resolution deliberately unchanged

Thaw does not call `timeBeginPeriod` during recovery. A finer periodic timer can increase
scheduler wakeups and power use and does not repair a frame-rate or capacity bottleneck.

---

## 3. GPU driver reset (Ctrl+Shift+Win+B)

**Why:** Windows exposes **Ctrl+Shift+Win+B** as a graphics-driver reset shortcut that may
help when the display is stuck while the rest of the session is alive. Thaw attempts the
shortcut with `SendInput`; this is an input request, not proof that the driver accepted it.
Windows, desktop integrity, focus, or another hook can reject or alter the attempt.

```csharp
// order: Ctrl down, Shift down, Win down, B down, then all up
Key(0x11, false); Key(0x10, false); Key(0x5B, false); Key(0x42, false);
Key(0x42, true);  Key(0x5B, true);  Key(0x10, true);  Key(0x11, true);
```

`Key()` = one `SendInput` `INPUT` with `type=1` (keyboard), `KEYBDINPUT.wVk`, `dwFlags`
(0 = down, 2 = KEYEVENTF_KEYUP).

- File: `Unfreezer.cs` → `ResetGpuDriver()` / `Key()`.
- P/Invoke: `user32.dll SendInput(uint, INPUT[], int)` (see `Native.INPUT/INPUTUNION/KEYBDINPUT`).
- Caveat: screen may blank ~1 s, the request may do nothing, and the result is not directly
  observable from user mode. Config: `ResetGpuDriver` (keep opt-in while diagnosing).

---

## 4. DWM soft-freeze rescue (restart the compositor)

**Why:** if the desktop compositor (dwm.exe) appears hung, the screen can stay frozen after
a display reset. When explicitly configured, Thaw waits 1.2 s, checks the compositor, and
may terminate dwm.exe so Windows can respawn it. This is a disruptive, heuristic-gated
operation, not a guaranteed diagnosis or repair.

**Detection — two signals:**
1. `IsHungAppWindow(dwmWindow)` — the OS's own hung check. The window is found by
   class name `DwmNotificationWindow` (classic) or `Dwm` (Windows 11 24H2+), falling back to
   enumerating any top-level window owned by the dwm PID via `EnumWindows` +
   `GetWindowThreadProcessId`.
2. Watchdog stall: `Watchdog.LastStallMs >= Config.StallThresholdMs` (latest 1 s sample still
   shows the scheduler frozen).

**Restart:**
```csharp
foreach (var p in Process.GetProcessesByName("dwm")) { p.Kill(); p.WaitForExit(3000); }
// poll up to 5 s for the system to respawn dwm
```

- File: `Unfreezer.cs` → `IsScreenStillFrozen()` / `IsDwmHung()` / `RestartDwm()`.
- P/Invoke: `user32.dll FindWindow, EnumWindows, GetWindowThreadProcessId, IsHungAppWindow`.
- Caveat: display can go black ~1–2 s, and a heuristic can be wrong. Only fires when enabled
  by `RestartDwmOnFrozenScreen` and the configured check reports a possible freeze.

---

## 5. Working-set tuning for eligible processes

**Why:** during memory-pressure freezes, physical RAM can be under pressure and the system
may thrash the pagefile. Where the engine has permission, working-set tuning can give the
system temporary breathing room. It is not a guarantee of freed memory or a substitute for
finding the process or workload causing pressure. Nothing is closed by this step; pages can
fault back in on demand.

```csharp
IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, pid);
EmptyWorkingSet(h);
CloseHandle(h);
```

- File: `Unfreezer.cs` → `TrimWorkingSet()` (called for eligible process candidates).
- P/Invoke: `kernel32.dll OpenProcess`, `psapi.dll EmptyWorkingSet`, `kernel32.dll CloseHandle`.
- Skipped: own process, PID 0/4, and `DoNotTrim` set = {System, Idle, Registry,
  Secure System, Memory Compression}. Access-denied processes are silently skipped.
- Caveat: freed pages fault back in when used — this is the point.

---

## 6. Standby list purge

**Why:** Windows caches files/executables in the "standby" memory list. Under RAM pressure
this cache can compete with active work. An explicitly enabled purge requests that Windows
reclaim the list; it is system-wide and may not be available or beneficial.

```csharp
NtSetSystemInformation(SystemMemoryListInformation /*0x50*/, &MemoryPurgeStandbyList /*1*/, 4);
```

- File: `Unfreezer.cs` → `PurgeStandbyList()`.
- P/Invoke: `ntdll.dll NtSetSystemInformation(int, IntPtr, int)`.
- Requires: **admin** + enabling `SeProfileSingleProcessPrivilege`
  (`Native.EnablePrivilege` → `OpenProcessToken` + `LookupPrivilegeValue` +
  `AdjustTokenPrivileges`).
- Safety: keep disabled while diagnosing unless the user has explicitly opted into a
  machine-wide memory operation.
- Status: logged as `Standby list purge: NTSTATUS 0x00000000` on success.

---

## 7. Modified page list purge

**Why:** dirty pages waiting to be written to disk can contribute to memory pressure. An
explicitly enabled purge asks Windows to process that list; it can be disk-heavy and is not
guaranteed to reduce stalls.

```csharp
NtSetSystemInformation(SystemMemoryListInformation /*0x50*/, &MemoryPurgeModifiedPageList /*2*/, 4);
```

- File: `Unfreezer.cs` → `PurgeModifiedPageList()`.
- Same requirements as standby purge (admin + `SeProfileSingleProcessPrivilege`); keep it
  opt-in because it is system-wide.

---

## 8. System file cache flush

**Why:** the system file cache can hold substantial memory. An explicitly enabled flush asks
Windows to reduce it; applications may need to reload data and the result is workload-
dependent.

```c
SetSystemFileCacheSize((SIZE_T)-1, (SIZE_T)-1, 0);   // -1,-1,0 = flush to minimum
```

- File: `Unfreezer.cs` → `FlushFileCache()`.
- P/Invoke: `kernel32.dll SetSystemFileCacheSize(IntPtr, IntPtr, uint)`.
- Requires: **admin** + enabling `SeIncreaseQuotaPrivilege`.
- Safety: this is a machine-wide cache operation; keep it disabled unless explicitly
  authorized and verify the return value in the log.

---

## 9. Foreground app + shell priority boost

**Why:** priority is relative. A temporary boost to the foreground app plus selected shell
components may improve visible responsiveness, but it can also add scheduler pressure and
does not fix the underlying workload.

```csharp
SetPriorityClass(hProcess, 0x00008000);   // ABOVE_NORMAL_PRIORITY_CLASS
```

Boosted: `GetForegroundWindow()` → `GetWindowThreadProcessId` → that PID; plus `ShellApps` =
{explorer, dwm, ShellExperienceHost, StartMenuExperienceHost, SearchHost}.

- File: `Unfreezer.cs` → `BoostTemporarily()`; only Normal-priority targets are changed and every handle/class is restored.
- P/Invoke: `kernel32.dll OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION)`,
  `kernel32.dll SetPriorityClass`.
- Safety: this is best-effort and should be evaluated with the current workload; a priority
  boost is not evidence that the machine has more capacity.

---

## 10. Power plan safety boundary

The default profile leaves the active power plan untouched. When `PowerPlanBoost` is enabled,
the **Alt+F4 force profile only** may temporarily select Windows' High Performance scheme
(`SCHEME_MIN`) before the force-run. The previously active scheme is captured in the rollback
journal and restored at the end of the bounded pass. The operation is machine-wide, requires
the relevant Windows permission, and is not proof that a recovery action succeeded. A process
crash, power loss, killed helper, or `powercfg` failure can still prevent the restore; inspect
the log and current Windows power-plan state when this matters.
Other triggers record `safety-policy-no-persistent-changes` and do not change the plan.

---

## 11. Explorer restart (shell/taskbar)

**Why:** a hung shell can make the desktop, taskbar, and File Explorer appear frozen.
When explicitly enabled or selected through the confirmed strong action, restarting
Explorer asks Windows to recreate that shell surface. It is not a general process or
hardware recovery.

Equivalent to the reference command:
```powershell
Stop-Process -Name explorer -Force; Start-Sleep 2; Start-Process "$env:windir\explorer.exe"
```

```csharp
foreach (var p in Process.GetProcessesByName("explorer")) { p.Kill(); p.WaitForExit(3000); }
Thread.Sleep(2000);
// preferred: start under the interactive user's token (normal integrity)
WTSGetActiveConsoleSessionId() → WTSQueryUserToken(session, out token)
    → CreateProcessAsUser(token, "...\\explorer.exe", ..., STARTUPINFO{ lpDesktop = "winsta0\\default" })
// fallback: Process.Start(explorer.exe, UseShellExecute = true)  ← same as the PS reference
```

- File: `Unfreezer.cs` → `RestartExplorer()` / `TryStartExplorerAsInteractiveUser()`.
- P/Invoke: `kernel32.dll WTSGetActiveConsoleSessionId`, `wtsapi32.dll WTSQueryUserToken`,
  `advapi32.dll CreateProcessAsUser` (see `Native.STARTUPINFO/PROCESS_INFORMATION`).
- Note: `WTSQueryUserToken` needs SYSTEM (`SeTcbPrivilege`), so from a plain elevated Thaw it
  may fall back to `Process.Start`; verify the resulting Explorer integrity level if it
  matters.
- Config: `RestartExplorerOnUnfreeze` (recommended default off). Tray action: confirmed
  "Strong recovery — restart Explorer shell".
- Explorer is the **only** process intentionally terminated by this operation.

---

## 12. Tray icon re-registration after explorer restart

**Why:** the notification area belongs to explorer. After a shell restart the tray icon must
be re-added (`Shell_NotifyIcon NIM_ADD`). Thaw toggles `NotifyIcon.Visible` on the UI thread
via `SynchronizationContext.Post` right after explorer comes back.

- File: `AppContext.cs` → `ReAddTrayIcon()`; wired from `Unfreezer.ExplorerRestartedNow`.

---

## Watchdog — how "stressed / frozen" is detected

`Watchdog.cs`, a Highest-priority background thread sampling every 1 s. A single
native/probe exception is rate-limited in the log and isolated to that sample so the
detector continues on the next interval:

1. **Scheduling delay (the freeze detector).** `Environment.TickCount64` before/after
   `Thread.Sleep(1000)`. If the machine froze, the thread wakes late:
   `delayMs = wallMs - 1000`. Any delay > 0 means the OS could not schedule this thread.
2. **CPU%** via `GetSystemTimes` deltas (idle/kernel/user).
3. **RAM%** via `GlobalMemoryStatusEx.dwMemoryLoad`.

**Stress score** (0..1): `0.55·stallF + 0.25·cpuF + 0.20·memF`, where stallF =
clamp(delay/8000), cpuF = clamp((cpu−60)/(CpuStressPercent−60)), memF =
clamp((mem−70)/(MemStressPercent−70)).

**Stressed** when score > 0.40 **or** delay ≥ `StallThresholdMs` (2500). Hysteresis: stays
stressed until 5 consecutive healthy samples with score < 0.25 and delay < 1200 ms.
**Hard stall** (delay ≥ `HardStallMs`, 6000) fires `HardStallDetected`; the tray requests
automatic recovery only when `AutoUnfreezeOnStall` is explicitly enabled (recommended
default off), then the watchdog takes a 2 s breather. `RecentStall` = stall within the last
30 s. A hard-stall signal is not a root-cause diagnosis.

- P/Invoke: `kernel32.dll GetSystemTimes`, `kernel32.dll GlobalMemoryStatusEx`
  (`Native.MEMORYSTATUSEX`).

---

## Keyboard hook — how Alt+F4 is captured

`KeyboardHook.cs` — a global `WH_KEYBOARD_LL` low-level hook installed with
`SetWindowsHookEx(13, proc, GetModuleHandle(null), 0)` on a dedicated highest-priority
native message-loop thread independent of `Application.Run`. A 250 ms heartbeat renews the
hook when needed, while a preallocated dispatch ring and per-user rescue event preserve the
sub-second activation path. Every key event updates a modifier-state tracker
(handles **both** generic and left/right VKs: VK_MENU 0x12 / VK_LMENU 0xA4 / VK_RMENU 0xA5,
VK_CONTROL 0x11 / VK_LCONTROL 0xA2 / VK_RCONTROL 0xA3, VK_SHIFT 0x10 / 0xA0 / 0xA1 — real
keyboards send the left/right codes; the generic-only bug was caught and fixed).

**Alt+F4:** on F4 (0x73) keydown with Alt (`LLKHF_ALTDOWN` = flags bit 0x20, or tracked
state), `_shouldInterceptAltF4()` decides by `AltF4Mode`:
- `always` → always swallow (explicit compatibility mode; window closing via Alt+F4 disabled),
- `stressed` → swallow only while `Watchdog.Stressed || Watchdog.RecentStall`,
- `off` → never swallow.

Swallowing = return `(IntPtr)1` from the hook proc (the keystroke never reaches any window),
then request recovery. **Alt+F4** and **panic** (default Ctrl+Alt+U) request the combined profile;
**slowdown** (Ctrl+Alt+S) selects system recovery and **frame drop** (Ctrl+Alt+G) selects
display-only recovery. Held repeats, injected input, duplicate chords, and presses inside
the configurable debounce window are rejected. Config reload rebuilds the live binding table.

- P/Invoke: `user32.dll SetWindowsHookEx/UnhookWindowsHookEx/CallNextHookEx/GetAsyncKeyState`,
  `KBDLLHOOKSTRUCT` (vkCode, scanCode, flags, time, dwExtraInfo).
- Note: LL hooks receive input across integrity levels, so Alt+F4 is caught even when an
  elevated window is focused.

---

## Capture-path hardening (1.4.0)

The keybind path now has several independent boundaries so a busy UI thread or a damaged
single hook does not erase the user's emergency request:

1. **Two independent low-level hooks plus a supervisor** — the primary and emergency
   `WH_KEYBOARD_LL` hooks use separate native message-loop threads. Each has its own handle,
   heartbeat, and bounded reinstall path; an independent supervisor recreates a hook thread
   if its message loop exits, while the newer hook gets the first opportunity and the primary
   remains a fallback.
2. **Lock-free callback edge** — callbacks use per-hook key state, atomic configuration
   references, a concurrent debounce table, and no synchronous file logging or recovery work.
   Callback faults are counted and passed through to Windows.
3. **Reserved emergency dispatch slot** — a 32-slot pre-warmed dispatch ring is backed by
   one reserved slot for the case where cleanup briefly owns the normal queue. A capture is
   never allowed to run the recovery engine synchronously; if an extreme burst fills both
   paths, the dropped count is surfaced instead of hiding the loss.
4. **Named signal plus acknowledgement** — the capture edge pulses a per-user `Local\\`
   event and the dispatch worker pulses a paired acknowledgement event only after it has
   delivered the request. Sequence gating is committed only after the native pulse succeeds,
   so a transient event-handle failure remains retryable and duplicate acknowledgements do not
   leave stale fallback pulses.
5. **Out-of-process registered-hotkey fallback** — the Thaw-only rescue helper owns
   `RegisterHotKey` registrations for always-on panic/frame-drop chords. If the main process
   does not acknowledge a request within the bounded window, it starts one headless,
   force-all recovery. `Alt+F4` remains low-level-hook-only because `RegisterHotKey` cannot
   guarantee close suppression.
6. **Capture telemetry** — support diagnostics expose primary/emergency installation,
   registered fallback count, available capture path, capture count, dispatch drops, callback
   faults, and the most recent captured chord.

These are still user-mode recovery paths. If Windows cannot schedule either process, the
input stack, kernel, power source, or hardware has failed, no executable can react.

---

## Privilege enabling (admin helpers)

`Native.EnablePrivilege(name)` — `OpenProcessToken(TOKEN_QUERY|TOKEN_ADJUST_PRIVILEGES)` →
`LookupPrivilegeValue` → `AdjustTokenPrivileges(SE_PRIVILEGE_ENABLED)`.

Used for: `SeProfileSingleProcessPrivilege` (standby/modified purge),
`SeIncreaseQuotaPrivilege` (file cache flush). `Native.IsElevated()` uses
`WindowsIdentity` + `WindowsPrincipal.IsInRole(Administrator)`; deep memory ops run only when
elevated, everything else degrades gracefully. Tray menu → "Restart as Administrator"
re-launches with `--elevated` (skips the single-instance mutex).

---

## Config reference (`%APPDATA%\Thaw\config.json`)

```jsonc
{
  "AltF4Mode": "always",                 // "always" | "stressed" | "off"
  "PanicHotkey": "Ctrl+Alt+U",           // e.g. "Alt+F4", "Ctrl+Shift+U", "F12"
  "SlownessHotkey": "Ctrl+Alt+S",
  "SlownessHotkeyMode": "stressed",
  "FrameDropHotkey": "Ctrl+Alt+G",
  "FrameDropHotkeyMode": "always",
  "HotkeyDebounceMs": 750,
  "AutoUnfreezeOnStall": false,          // opt in: auto recovery on hard stall
  "ShowBalloons": true,                  // unfreeze result balloons
  "DebugLog": false,                     // verbose key/hook/DWM logging
  "StallThresholdMs": 2500,              // delay that flags the system as stressed
  "HardStallMs": 6000,                   // stall that triggers auto-unfreeze
  "CpuStressPercent": 90,                // CPU% counting as fully stressed
  "MemStressPercent": 92,                // RAM% counting as fully stressed
  "PowerPlanBoost": false,               // Alt+F4 only: temporary High Performance selection
  "PowerPlanRestoreAfterSeconds": 120,   // compatibility setting; current bounded runs restore the exact prior plan at run end
  "ShowCaptureOverlay": true,            // non-activating immediate capture acknowledgement
  "ShowImmediateCaptureFeedback": true,  // immediate balloon before recovery work starts
  "PlayRecoverySound": false,            // optional system acknowledgement sound
  "CaptureOverlayDurationMs": 1200,      // bounded to 250..5000 ms
  "KeepIncidentHistory": true,           // bounded aggregate receipts in %LOCALAPPDATA%\Thaw
  "IncidentHistoryLimit": 100,            // bounded to 10..500 receipts
  "EnableSupportBundleExport": true,     // user-selected ZIP of diagnostics/log/config/receipts
  "EnableDetailedActionReporting": true,  // persist bounded per-action phase/outcome/detail receipts
  "EnableWindowsApplicationRestart": true,
  "ThawWatchdogMode": "off",            // "off" | "relaunch"; Thaw-only child monitor
   "RescueBrokerMode": "relaunch",       // default; Thaw-only capture/crash fallback monitor
  "WatchdogHeartbeatSeconds": 15,        // reserved helper heartbeat cadence (bounded)
  "WatchdogRelaunchDelaySeconds": 3,     // delay before an eligible nonzero-exit relaunch
  "RestartExplorerOnUnfreeze": false,    // permit restart only if Explorer is measured hung
  "ResetGpuDriver": true,                // explicit frame-drop/panic profiles
  "RestartDwmOnFrozenScreen": false      // opt in: detection-gated DWM restart
}
```

Treat this as a conservative reference profile. Existing config values remain user-owned;
inspect and change them deliberately, then use **Reload Config**. The tray shows whether
system-level operations are available under the current token.

---

## What Thaw deliberately does NOT do (and why)

- **Never suspends/pauses any process** (removed — `NtSuspendProcess` was dropped by request).
- **Never kills arbitrary application/service processes.** Explorer can be restarted only
  by the confirmed shell action or when enabled and measured hung.
- **No dwm.exe kill except the explicit panic profile**, with configuration plus independent
  DWM-hung and scheduler-stall evidence; the heuristic is not infallible.
- **No automatic service, adapter, audio, storage, input, or PnP-device changes**; these
  surfaces are diagnosed and left for an explicit, informed decision.
- **No GPU overclocking / no registry writes** (except the "Start with Windows" Run key).

## Hard limits (physics, not Thaw)

- A total kernel panic / hardware deadlock / power failure cannot be fixed by **any**
  user-mode application — nothing can run while the CPU itself is frozen.
- GPU *hardware* failure and dying disks are outside software's reach.
- Everything Thaw can safely attempt or classify: slow/stutter/unresponsive-but-recovering —
  screen (GPU reset + optional DWM rescue), shell (Explorer refresh/restart), RAM
  (pressure-gated trim/cache work), foreground/background scheduling, desktop refresh, and
  bounded evidence for disk, commit, DPC/ISR, network, GPU, thermal, device, and deadlock
  causes. Timer resolution is left unchanged. If explicitly enabled, only an Alt+F4 force
  capture may temporarily select High Performance; that machine-wide change can outlive a
  crash or power loss and is not a repair guarantee.

## Verification log (`%LOCALAPPDATA%\Thaw\log.txt`)

Every method logs its outcome — the fastest way to confirm a fix worked:

```
[INF] Unfreeze triggered (Hotkey)
[INF] GPU reset input accepted (8/8 events)
[DBG] DWM check: hwnd=0x10048 hung=False
[INF] Power-plan boost skipped by safety policy; active plan left unchanged
[INF] Standby list purge: NTSTATUS 0x00000000
[INF] System file cache flush: True
[INF] explorer killed: True
[INF] explorer started (fallback)
[INF] Unfreeze done: ... · trimmed 2/187 processes · boosted 1 apps · +864 MB RAM · GPU reset
[INF] Tray icon re-registered after explorer restart
```
