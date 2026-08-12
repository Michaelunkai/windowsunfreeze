# Thaw — Complete Unfreeze Methods Reference

**Every single technique used to unfreeze a stuck/slow Windows PC, documented for future
sessions.** Each entry explains *what* it does, *why* it helps, the *exact API + signatures*,
*where it lives in the code*, *admin requirements*, and *caveats*.

Project layout: `Thaw/` — `Unfreezer.cs` (engine), `Watchdog.cs` (detection),
`KeyboardHook.cs` (Alt+F4), `Native.cs` (all P/Invoke), `AppContext.cs` (tray + wiring),
`Config.cs` (settings), `Icons.cs` (artwork).

---

## 0. The full unfreeze sequence (order matters)

Every Alt+F4 (or panic hotkey / tray double-click / auto-stall) runs `Unfreezer.Run()` in
this exact order:

| # | Method | File / function | Admin? |
|---|---|---|---|
| 1 | Own process → High priority + thread → Time-Critical | `Unfreezer.Run` | no |
| 2 | Timer resolution → 1 ms (`timeBeginPeriod`) | `Unfreezer.Run` | no |
| 3 | GPU driver reset (Ctrl+Shift+Win+B) | `Unfreezer.ResetGpuDriver` | no |
| 4 | DWM soft-freeze rescue (screen still frozen after 1.2 s) | `Unfreezer.IsScreenStillFrozen` / `RestartDwm` | yes |
| 5 | `EmptyWorkingSet` on every process | `Unfreezer.TrimWorkingSet` | partial (per-process ACL) |
| 6 | Standby + modified page list purge | `Unfreezer.PurgeStandbyList` / `PurgeModifiedPageList` | yes |
| 7 | System file cache flush | `Unfreezer.FlushFileCache` | yes |
| 8 | Foreground app + shell → High priority | `Unfreezer.BoostToHigh` | partial |
| 9 | High Performance power plan (restored after N s) | `Unfreezer.BoostPowerPlan` | yes |
| 10 | Force-restart explorer.exe (shell/taskbar) | `Unfreezer.RestartExplorer` | no (elevated = elevated shell) |
| 11 | Tray icon re-registration (after explorer restart) | `AppContext.ReAddTrayIcon` | — |

Methods 3–4 happen **before** memory work (display recovery first); explorer restart happens
**last** (shell comes back with a clean, RAM-rich system).

---

## 1. Own-process priority + Time-Critical thread

**Why:** when the PC is slammed, the unfreezer itself must be scheduled before it can help
anything else. High class for the process, Time-Critical for the worker thread.

```csharp
Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;   // 0x80 HIGH_PRIORITY_CLASS
SetThreadPriority(GetCurrentThread(), 15);                               // THREAD_PRIORITY_TIME_CRITICAL
```

Restored to Normal afterwards in the `finally` block.

- File: `Unfreezer.cs` → `Run()` start / `finally`.
- P/Invoke: `kernel32.dll SetThreadPriority(HANDLE, int)` (local), managed `Process.PriorityClass`.

---

## 2. High-resolution timer (`timeBeginPeriod`)

**Why:** Windows normally runs at 15.6 ms timer granularity. Under 1 ms the OS wakes more
often, which makes the UI feel dramatically smoother and lets a stuck foreground app recover
faster. The system-wide timer resolution is changed by whoever requests the finest period.

```c
timeBeginPeriod(1);   // winmm.dll
timeEndPeriod(1);     // always in finally
```

- File: `Unfreezer.cs` → `Run()`.
- P/Invoke: `winmm.dll timeBeginPeriod/timeEndPeriod(uint)`.
- Note: period is system-global while held; we hold it only for the duration of the unfreeze.

---

## 3. GPU driver reset (Ctrl+Shift+Win+B)

**Why:** the single most common cause of "screen stuck / stutter but PC responsive" is a hung
or wedged graphics driver (TDR). Windows' own hidden hotkey **Ctrl+Shift+Win+B** tells the
kernel to reload the display driver. Thaw injects it with `SendInput`.

```csharp
// order: Ctrl down, Shift down, Win down, B down, then all up
Key(0x11, false); Key(0x10, false); Key(0x5B, false); Key(0x42, false);
Key(0x42, true);  Key(0x5B, true);  Key(0x10, true);  Key(0x11, true);
```

`Key()` = one `SendInput` `INPUT` with `type=1` (keyboard), `KEYBDINPUT.wVk`, `dwFlags`
(0 = down, 2 = KEYEVENTF_KEYUP).

- File: `Unfreezer.cs` → `ResetGpuDriver()` / `Key()`.
- P/Invoke: `user32.dll SendInput(uint, INPUT[], int)` (see `Native.INPUT/INPUTUNION/KEYBDINPUT`).
- Caveat: screen blanks ~1 s. Config: `ResetGpuDriver`.

---

## 4. DWM soft-freeze rescue (restart the compositor)

**Why:** if the desktop compositor (dwm.exe) hangs, the screen stays frozen even after the
GPU reset. Thaw waits 1.2 s after the reset, checks whether the compositor is unresponsive,
and if so terminates dwm.exe — Windows respawns a fresh compositor automatically.

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
- Caveat: display goes black ~1–2 s. Only fires when the screen is genuinely still frozen
  (healthy screens produce no false positives — verified: `hung=False`).
- Config: `RestartDwmOnFrozenScreen`.

---

## 5. `EmptyWorkingSet` on every process (instant RAM)

**Why:** during memory-pressure freezes, physical RAM is exhausted and the system thrashes the
pagefile. Trimming every process's working set (the classic RAMMap/CleanMem/EmptyStandbyList
technique) forces pages out of physical RAM instantly, giving the system breathing room.
Nothing is closed; apps page their data back on demand.

```csharp
IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, pid);
EmptyWorkingSet(h);
CloseHandle(h);
```

- File: `Unfreezer.cs` → `TrimWorkingSet()` (called for every process).
- P/Invoke: `kernel32.dll OpenProcess`, `psapi.dll EmptyWorkingSet`, `kernel32.dll CloseHandle`.
- Skipped: own process, PID 0/4, and `DoNotTrim` set = {System, Idle, Registry,
  Secure System, Memory Compression}. Access-denied processes are silently skipped.
- Caveat: freed pages fault back in when used — this is the point.

---

## 6. Standby list purge

**Why:** Windows caches files/executables in the "standby" memory list. Under RAM pressure
this cache can starve running apps. Purging the standby list returns that memory to
applications instantly.

```csharp
NtSetSystemInformation(SystemMemoryListInformation /*0x50*/, &MemoryPurgeStandbyList /*1*/, 4);
```

- File: `Unfreezer.cs` → `PurgeStandbyList()`.
- P/Invoke: `ntdll.dll NtSetSystemInformation(int, IntPtr, int)`.
- Requires: **admin** + enabling `SeProfileSingleProcessPrivilege`
  (`Native.EnablePrivilege` → `OpenProcessToken` + `LookupPrivilegeValue` +
  `AdjustTokenPrivileges`).
- Status: logged as `Standby list purge: NTSTATUS 0x00000000` on success.

---

## 7. Modified page list purge

**Why:** dirty pages waiting to be written to disk. Purging forces those writes out now,
freeing more RAM and reducing later disk stalls. Slightly disk-heavy for a moment.

```csharp
NtSetSystemInformation(SystemMemoryListInformation /*0x50*/, &MemoryPurgeModifiedPageList /*2*/, 4);
```

- File: `Unfreezer.cs` → `PurgeModifiedPageList()`.
- Same requirements as standby purge (admin + `SeProfileSingleProcessPrivilege`).

---

## 8. System file cache flush

**Why:** the system file cache working set can hold gigabytes. Flushing it to its minimum
releases that RAM for running apps.

```c
SetSystemFileCacheSize((SIZE_T)-1, (SIZE_T)-1, 0);   // -1,-1,0 = flush to minimum
```

- File: `Unfreezer.cs` → `FlushFileCache()`.
- P/Invoke: `kernel32.dll SetSystemFileCacheSize(IntPtr, IntPtr, uint)`.
- Requires: **admin** + enabling `SeIncreaseQuotaPrivilege`.

---

## 9. Foreground app + shell priority boost

**Why:** priority is relative — boosting everything is a no-op. Boosting the app you're
actually using plus the shell (explorer/dwm/Start menu/Search) makes the visible system
responsive while background processes keep their share.

```csharp
SetPriorityClass(hProcess, 0x00000080);   // HIGH_PRIORITY_CLASS
```

Boosted: `GetForegroundWindow()` → `GetWindowThreadProcessId` → that PID; plus `ShellApps` =
{explorer, dwm, ShellExperienceHost, StartMenuExperienceHost, SearchHost}.

- File: `Unfreezer.cs` → `BoostToHigh()`.
- P/Invoke: `kernel32.dll OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION)`,
  `kernel32.dll SetPriorityClass`.

---

## 10. High Performance power plan

**Why:** balanced/eco power plans throttle CPU frequency and C-states. Forcing High
Performance removes throttling so everything runs at full speed (especially under short
bursts where the governor lags).

```csharp
PowerGetActiveScheme(IntPtr.Zero, out guidPtr);            // save current plan
PowerSetActiveScheme(IntPtr.Zero, ref HighPerformanceGuid);// 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c
// after PowerPlanRestoreAfterSeconds (default 120 s, 0 = keep): restore saved plan
```

- File: `Unfreezer.cs` → `BoostPowerPlan()` (restore on a background thread).
- P/Invoke: `powrprof.dll PowerGetActiveScheme / PowerSetActiveScheme`, `LocalFree` the GUID.
- Requires: **admin**. Config: `PowerPlanBoost` (default on), `PowerPlanRestoreAfterSeconds`.

---

## 11. Explorer restart (shell/taskbar)

**Why:** a hung shell is one of the most common "whole desktop frozen" states. Force-restarting
explorer re-creates the taskbar, start menu, desktop icons and File Explorer windows.

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
  usually falls back to `Process.Start` — explorer then runs elevated, exactly like running
  the user's PowerShell from an admin prompt.
- Config: `RestartExplorerOnUnfreeze` (default on). Manual menu item: "Restart Explorer Now".
- explorer is the **only** process ever terminated by Thaw.

---

## 12. Tray icon re-registration after explorer restart

**Why:** the notification area belongs to explorer. After a shell restart the tray icon must
be re-added (`Shell_NotifyIcon NIM_ADD`). Thaw toggles `NotifyIcon.Visible` on the UI thread
via `SynchronizationContext.Post` right after explorer comes back.

- File: `AppContext.cs` → `ReAddTrayIcon()`; wired from `Unfreezer.ExplorerRestartedNow`.

---

## Watchdog — how "stressed / frozen" is detected

`Watchdog.cs`, a Highest-priority background thread sampling every 1 s:

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
**Hard stall** (delay ≥ `HardStallMs`, 6000) fires `HardStallDetected` → automatic unfreeze
(if `AutoUnfreezeOnStall`), then a 2 s breather. `RecentStall` = stall within the last 30 s.

- P/Invoke: `kernel32.dll GetSystemTimes`, `kernel32.dll GlobalMemoryStatusEx`
  (`Native.MEMORYSTATUSEX`).

---

## Keyboard hook — how Alt+F4 is captured

`KeyboardHook.cs` — a global `WH_KEYBOARD_LL` low-level hook installed with
`SetWindowsHookEx(13, proc, GetModuleHandle(null), 0)`; callbacks arrive on the app's own
message loop thread (`Application.Run`). Every key event updates a modifier-state tracker
(handles **both** generic and left/right VKs: VK_MENU 0x12 / VK_LMENU 0xA4 / VK_RMENU 0xA5,
VK_CONTROL 0x11 / VK_LCONTROL 0xA2 / VK_RCONTROL 0xA3, VK_SHIFT 0x10 / 0xA0 / 0xA1 — real
keyboards send the left/right codes; the generic-only bug was caught and fixed).

**Alt+F4:** on F4 (0x73) keydown with Alt (`LLKHF_ALTDOWN` = flags bit 0x20, or tracked
state), `_shouldInterceptAltF4()` decides by `AltF4Mode`:
- `always` → always swallow (default; window closing via Alt+F4 disabled),
- `stressed` → swallow only while `Watchdog.Stressed || Watchdog.RecentStall`,
- `off` → never swallow.

Swallowing = return `(IntPtr)1` from the hook proc (the keystroke never reaches any window),
then trigger the unfreeze. **Panic hotkey** (default Ctrl+Alt+U) is always swallowed and
always unfreezes. Config: `AltF4Mode`, `PanicHotkey`.

- P/Invoke: `user32.dll SetWindowsHookEx/UnhookWindowsHookEx/CallNextHookEx/GetAsyncKeyState`,
  `KBDLLHOOKSTRUCT` (vkCode, scanCode, flags, time, dwExtraInfo).
- Note: LL hooks receive input across integrity levels, so Alt+F4 is caught even when an
  elevated window is focused.

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
  "AutoUnfreezeOnStall": true,           // auto-unfreeze on hard stall (> HardStallMs)
  "ShowBalloons": true,                  // unfreeze result balloons
  "DebugLog": false,                     // verbose key/hook/DWM logging
  "StallThresholdMs": 2500,              // delay that flags the system as stressed
  "HardStallMs": 6000,                   // stall that triggers auto-unfreeze
  "CpuStressPercent": 90,                // CPU% counting as fully stressed
  "MemStressPercent": 92,                // RAM% counting as fully stressed
  "PowerPlanBoost": true,                // (admin) High Performance power plan on unfreeze
  "PowerPlanRestoreAfterSeconds": 120,   // restore previous plan after N s (0 = keep)
  "RestartExplorerOnUnfreeze": true,     // force-restart explorer.exe on every unfreeze
  "ResetGpuDriver": true,                // send Ctrl+Shift+Win+B on every unfreeze
  "RestartDwmOnFrozenScreen": true       // restart dwm.exe if screen still frozen after reset
}
```

---

## What Thaw deliberately does NOT do (and why)

- **Never suspends/pauses any process** (removed — `NtSuspendProcess` was dropped by request).
- **Never kills any process except explorer.exe** (explicitly requested).
- **No dwm.exe kill unless the screen is actually still frozen** (detection-gated).
- **No service changes** (SysMain/Superfetch etc. are invasive and often counterproductive).
- **No GPU overclocking / no registry writes** (except the "Start with Windows" Run key).

## Hard limits (physics, not Thaw)

- A total kernel panic / hardware deadlock / power failure cannot be fixed by **any**
  user-mode application — nothing can run while the CPU itself is frozen.
- GPU *hardware* failure and dying disks are outside software's reach.
- Everything Thaw covers: slow/stutter/unresponsive-but-recovering — screen (GPU reset +
  DWM rescue), shell (explorer restart), RAM (trim + standby/modified purge + cache flush),
  scheduling (priority + timer + power plan).

## Verification log (`%LOCALAPPDATA%\Thaw\log.txt`)

Every method logs its outcome — the fastest way to confirm a fix worked:

```
[INF] Unfreeze triggered (Hotkey)
[INF] GPU driver reset sent (Ctrl+Shift+Win+B)
[DBG] DWM check: hwnd=0x10048 hung=False
[INF] Power plan -> High Performance: 0x00000000
[INF] Standby list purge: NTSTATUS 0x00000000
[INF] System file cache flush: True
[INF] explorer killed: True
[INF] explorer started (fallback)
[INF] Unfreeze done: ... · tuned 327 processes · boosted 6 apps · +864 MB RAM · GPU reset · explorer restarted
[INF] Tray icon re-registered after explorer restart
```
