using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Thaw;

/// <summary>Win32 / Native API interop used by Thaw.</summary>
internal static class Native
{
    // ---------- user32 ----------

    internal const int WH_KEYBOARD_LL = 13;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;
    internal const int WM_HOTKEY = 0x0312;
    internal const int VK_MENU = 0x12;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_SHIFT = 0x10;
    internal const int VK_F4 = 0x73;

    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;

    internal delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsHungAppWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetShellWindow();

    // Bounded broadcasts are used only as a transient desktop/UI refresh
    // nudge. SMTO_ABORTIFHUNG prevents one unresponsive window from holding
    // the recovery worker indefinitely.
    internal static readonly IntPtr HWND_BROADCAST = new(-1);
    internal const uint WM_SETTINGCHANGE = 0x001A;
    internal const uint WM_THEMECHANGED = 0x031A;

    // Shell notification refresh is deliberately used instead of stopping the
    // shell: it asks the existing Explorer process to refresh cached associations
    // and icons without dropping open windows.
    internal const uint SHCNE_ASSOCCHANGED = 0x08000000;
    internal const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    internal static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    internal static bool TryBroadcastDesktopRefresh(out int deliveredMessages)
    {
        deliveredMessages = 0;
        try
        {
            const uint flags = SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT;
            foreach (uint message in new[] { WM_SETTINGCHANGE, WM_THEMECHANGED })
            {
                IntPtr result = SendMessageTimeout(
                    HWND_BROADCAST,
                    message,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    flags,
                    100,
                    out _);
                if (result != IntPtr.Zero) deliveredMessages++;
            }
            return deliveredMessages > 0;
        }
        catch (Exception ex)
        {
            Log.Debug("Desktop refresh broadcast unavailable: " + ex.Message);
            return false;
        }
    }

    // ---------- Desktop Window Manager diagnostics ----------

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct UNSIGNED_RATIO
    {
        internal uint Numerator;
        internal uint Denominator;
    }

    // DWM_TIMING_INFO is intentionally kept in the native field order.  The
    // structure is versioned by cbSize; Windows only fills fields it knows.
    // All DWM_FRAME_COUNT and QPC_TIME values are ULONGLONG in dwmapi.h.
    // dwmapi.h wraps this definition in pshpack1.h / poppack.h.  Using the
    // CLR's default alignment makes the x64 structure 320 bytes instead of
    // the native 292 bytes and DWM returns MILERR_MISMATCHED_SIZE.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct DWM_TIMING_INFO
    {
        internal uint cbSize;
        internal UNSIGNED_RATIO rateRefresh;
        internal ulong qpcRefreshPeriod;
        internal UNSIGNED_RATIO rateCompose;
        internal ulong qpcVBlank;
        internal ulong cRefresh;
        internal uint cDXRefresh;
        internal ulong qpcCompose;
        internal ulong cFrame;
        internal uint cDXPresent;
        internal ulong cRefreshFrame;
        internal ulong cFrameSubmitted;
        internal uint cDXPresentSubmitted;
        internal ulong cFrameConfirmed;
        internal uint cDXPresentConfirmed;
        internal ulong cRefreshConfirmed;
        internal uint cDXRefreshConfirmed;
        internal ulong cFramesLate;
        internal uint cFramesOutstanding;
        internal ulong cFrameDisplayed;
        internal ulong qpcFrameDisplayed;
        internal ulong cRefreshFrameDisplayed;
        internal ulong cFrameComplete;
        internal ulong qpcFrameComplete;
        internal ulong cFramePending;
        internal ulong qpcFramePending;
        internal ulong cFramesDisplayed;
        internal ulong cFramesComplete;
        internal ulong cFramesPending;
        internal ulong cFramesAvailable;
        internal ulong cFramesDropped;
        internal ulong cFramesMissed;
        internal ulong cRefreshNextDisplayed;
        internal ulong cRefreshNextPresented;
        internal ulong cRefreshesDisplayed;
        internal ulong cRefreshesPresented;
        internal ulong cRefreshStarted;
        internal ulong cPixelsReceived;
        internal ulong cPixelsDrawn;
        internal ulong cBuffersEmpty;
    }

    [DllImport("dwmapi.dll", SetLastError = false)]
    internal static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO timingInfo);

    [DllImport("dwmapi.dll", SetLastError = false)]
    internal static extern int DwmEnableMMCSS([MarshalAs(UnmanagedType.Bool)] bool enable);

    // ---------- bounded window and memory probes ----------

    internal const uint WM_NULL = 0x0000;
    internal const uint SMTO_BLOCK = 0x0001;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;
    internal const uint SMTO_ERRORONEXIT = 0x0020;
    internal const int ERROR_TIMEOUT = 1460;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    internal enum MEMORY_RESOURCE_NOTIFICATION_TYPE
    {
        LowMemoryResourceNotification = 0,
        HighMemoryResourceNotification = 1,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateMemoryResourceNotification(MEMORY_RESOURCE_NOTIFICATION_TYPE notificationType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryMemoryResourceNotification(
        IntPtr resourceNotificationHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool resourceState);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetEvent(IntPtr handle);

    // ---------- process accounting and quality of service ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessIoCounters(IntPtr processHandle, out IO_COUNTERS counters);

    internal enum PROCESS_INFORMATION_CLASS
    {
        ProcessPowerThrottling = 9,
    }

    internal const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    internal const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x00000001;
    internal const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE
    {
        internal uint Version;
        internal uint ControlMask;
        internal uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetProcessInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessInformation(
        IntPtr processHandle,
        PROCESS_INFORMATION_CLASS processInformationClass,
        IntPtr processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetProcessInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessInformation(
        IntPtr processHandle,
        PROCESS_INFORMATION_CLASS processInformationClass,
        IntPtr processInformation,
        uint processInformationSize);

    // ---------- CPU Sets (Windows 10+, best effort) ----------

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessDefaultCpuSets(
        IntPtr processHandle,
        IntPtr cpuSetIds,
        uint cpuSetIdCount,
        out uint requiredIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessDefaultCpuSets(
        IntPtr processHandle,
        IntPtr cpuSetIds,
        uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetThreadSelectedCpuSets(
        IntPtr threadHandle,
        IntPtr cpuSetIds,
        uint cpuSetIdCount,
        out uint requiredIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetThreadSelectedCpuSets(
        IntPtr threadHandle,
        IntPtr cpuSetIds,
        uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemCpuSetInformation(
        IntPtr cpuSetInformation,
        uint bufferLength,
        out uint returnedLength,
        IntPtr processHandle,
        uint flags);

    // ---------- MMCSS worker registration ----------

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW", SetLastError = true)]
    internal static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AvSetMmThreadPriority(IntPtr avrtHandle, int priority);

    // ---------- Wait Chain Traversal (diagnostic only) ----------

    internal const uint WCT_SYNC_OPEN_SESSION = 0x00000000;
    internal const uint WCT_OUT_OF_PROC_FLAG = 0x00000001;
    internal const uint WCT_OUT_OF_PROC_COM_FLAG = 0x00000002;
    internal const uint WCT_OUT_OF_PROC_CS_FLAG = 0x00000004;
    internal const uint WCT_MAX_NODE_COUNT = 16;
    internal const int ERROR_IO_PENDING = 997;
    internal const int ERROR_MORE_DATA = 234;
    internal const int ERROR_NO_MORE_ITEMS = 259;
    internal const int ERROR_NOT_SUPPORTED = 50;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_OBJECT_NOT_FOUND = 441;

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern void CloseThreadWaitChainSession(IntPtr wctSessionHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetThreadWaitChain(
        IntPtr wctSessionHandle,
        IntPtr context,
        uint flags,
        uint threadId,
        ref uint nodeCount,
        IntPtr nodeInfoArray,
        [MarshalAs(UnmanagedType.Bool)] out bool isCycle);

    // The largest member of WAITCHAIN_NODE_INFO is LockObject.  Keeping the
    // native size here lets the managed wrapper inspect the fixed-size array
    // without introducing unsafe code or a third-party interop package.
    internal const int WCT_NODE_INFO_SIZE = 280;

    // ---------- application restart/recovery ----------

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint ApplicationRecoveryCallback(IntPtr parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int RegisterApplicationRestart(string? commandLine, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int UnregisterApplicationRestart();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int RegisterApplicationRecoveryCallback(
        ApplicationRecoveryCallback callback,
        IntPtr parameter,
        uint pingInterval,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int UnregisterApplicationRecoveryCallback();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ApplicationRecoveryInProgress();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void ApplicationRecoveryFinished([MarshalAs(UnmanagedType.Bool)] bool success);

    // ---------- SetupAPI / Configuration Manager (read-only enumeration) ----------

    internal const uint DIGCF_PRESENT = 0x00000002;
    internal const uint DIGCF_ALLCLASSES = 0x00000004;
    internal const uint SPDRP_DEVICEDESC = 0x00000000;
    internal const uint SPDRP_HARDWAREID = 0x00000001;
    internal const uint SPDRP_MFG = 0x0000000B;
    internal const uint SPDRP_FRIENDLYNAME = 0x0000000C;
    internal const uint SPDRP_LOCATION_INFORMATION = 0x0000000D;
    internal const uint SPDRP_DRIVER = 0x00000009;
    internal const uint DN_HAS_PROBLEM = 0x00000400;
    internal static readonly Guid GUID_DEVCLASS_DISPLAY = new("4d36e968-e325-11ce-bfc1-08002be10318");

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVINFO_DATA
    {
        internal uint cbSize;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevsAll(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        IntPtr propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        IntPtr deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", SetLastError = false)]
    internal static extern int CM_Get_DevNode_Status(
        out uint status,
        out uint problemNumber,
        uint devInst,
        uint flags);

    // ---------- kernel32 ----------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_SET_QUOTA = 0x0100;
    internal const uint PROCESS_SET_INFORMATION = 0x0200;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint HIGH_PRIORITY_CLASS = 0x00000080;
    internal const uint REALTIME_PRIORITY_CLASS = 0x00000100;
    internal const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    internal const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetSystemFileCacheSize(IntPtr MinimumFileCacheSize, IntPtr MaximumFileCacheSize, uint Flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    internal static MEMORYSTATUSEX GetMemoryStatus()
    {
        var ms = new MEMORYSTATUSEX();
        ms.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        if (!GlobalMemoryStatusEx(ref ms)) ms.dwMemoryLoad = 0;
        return ms;
    }

    // ---------- psapi ----------

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyWorkingSet(IntPtr hProcess);

    // ---------- ntdll ----------

    internal const int SystemMemoryListInformation = 0x50;
    internal const int MemoryPurgeStandbyList = 1;

    [DllImport("ntdll.dll")]
    internal static extern int NtSetSystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength);

    // ---------- user32 input injection ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // INPUT's native union is sized by its largest member. On 64-bit Windows,
    // MOUSEINPUT is 32 bytes while KEYBDINPUT is only 24. Omitting the mouse
    // member shrinks managed INPUT to 32 bytes instead of the required 40 and
    // makes SendInput reject the entire request with ERROR_INVALID_PARAMETER.
    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type; // 1 = keyboard
        public INPUTUNION U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // Resolver-cache flush leaves existing sockets and authentication sessions
    // intact.  It is only invoked for an explicit, user-triggered recovery.
    [DllImport("dnsapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DnsFlushResolverCache();

    // ---------- terminal services (launch explorer as the interactive user) ----------

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    // ---------- advapi32 ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    internal const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    internal const int TOKEN_QUERY = 0x00000008;
    internal const int TOKEN_ADJUST_PRIVILEGES = 0x00000020;
    internal const int TokenPrivileges = 3;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out long lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    /// <summary>True when the current process runs with an elevated (administrator) token.</summary>
    internal static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Log.Debug("Elevation probe unavailable: " + ex.Message);
            return false;
        }
    }

    /// <summary>Enables a privilege (e.g. SeIncreaseQuotaPrivilege) on the current process token.</summary>
    internal static bool EnablePrivilege(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES, out IntPtr token))
            return false;

        try
        {
            if (!LookupPrivilegeValue(null, privilege, out long luid))
                return false;

            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                return false;
            // AdjustTokenPrivileges can return TRUE while setting
            // ERROR_NOT_ALL_ASSIGNED when the token lacks the privilege.
            return Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ---------- helpers ----------

    internal static uint GetForegroundPid()
    {
        uint pid = 0;
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero) GetWindowThreadProcessId(hwnd, out pid);
        return pid;
    }
}
