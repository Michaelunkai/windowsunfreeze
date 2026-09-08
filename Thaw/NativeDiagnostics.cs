using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Thaw;

/// <summary>
/// Optional, best-effort Windows diagnostics.  Every entry point is read-only
/// unless its name explicitly says temporary/set, and every temporary setter
/// returns a scope which restores the captured state when disposed.
///
/// This file deliberately uses only APIs shipped with Windows and the .NET
/// Windows desktop runtime.  APIs added after the minimum supported Windows
/// version are guarded and all native availability/permission failures are
/// converted to a false result rather than escaping into recovery code.
/// </summary>
internal static class NativeDiagnostics
{
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorSuccess = 0;
    private const int ErrorCancelled = 1223;
    private const int ErrorNoMoreItems = Native.ERROR_NO_MORE_ITEMS;
    private const int MaxCpuSetIds = 4096;
    private const int MaxEventRecords = 256;
    private const int MaxDevicePropertyBytes = 1024 * 1024;
    private const int MaxWctNodes = (int)Native.WCT_MAX_NODE_COUNT;

    // ---------- DWM composition timing and progress ----------

    internal sealed record DwmTimingSample
    {
        internal bool Supported { get; init; }
        internal int HResult { get; init; }
        internal DateTimeOffset CapturedAtUtc { get; init; }
        internal double RefreshRateHz { get; init; }
        internal double ComposeRateHz { get; init; }
        internal ulong QpcRefreshPeriod { get; init; }
        internal ulong QpcVBlank { get; init; }
        internal ulong QpcCompose { get; init; }
        internal ulong QpcFrameDisplayed { get; init; }
        internal ulong CRefresh { get; init; }
        internal ulong CFramesDisplayed { get; init; }
        internal ulong CFramesComplete { get; init; }
        internal ulong CFramesPending { get; init; }
        internal ulong CFramesAvailable { get; init; }
        internal ulong CFramesDropped { get; init; }
        internal ulong CFramesMissed { get; init; }
        internal ulong CFramesLate { get; init; }
        internal uint CFramesOutstanding { get; init; }
        internal ulong CFrame { get; init; }
        internal ulong CFrameDisplayed { get; init; }
        internal ulong CFrameComplete { get; init; }
        internal ulong CFramePending { get; init; }
        internal string? Failure { get; init; }
    }

    internal sealed record DwmProgressComparison
    {
        internal bool Supported { get; init; }
        internal bool Cancelled { get; init; }
        internal bool Progressed { get; init; }
        internal bool Worsened { get; init; }
        internal int ErrorCode { get; init; }
        internal DwmTimingSample? Before { get; init; }
        internal DwmTimingSample? After { get; init; }
        internal long ElapsedMilliseconds { get; init; }
        internal ulong FramesDisplayedDelta { get; init; }
        internal ulong FramesCompleteDelta { get; init; }
        internal ulong FramesDroppedDelta { get; init; }
        internal ulong FramesMissedDelta { get; init; }
        internal ulong FramesPendingDelta { get; init; }
        internal ulong CompositionFramesDelta { get; init; }
        internal ulong LateFramesDelta { get; init; }
        internal bool VBlankProgressed { get; init; }
    }

    internal static bool TryGetDwmTiming(out DwmTimingSample sample, IntPtr hwnd = default)
    {
        sample = new DwmTimingSample
        {
            Supported = false,
            CapturedAtUtc = DateTimeOffset.UtcNow,
        };

        if (!OperatingSystem.IsWindowsVersionAtLeast(6)) return false;

        try
        {
            var native = new Native.DWM_TIMING_INFO
            {
                cbSize = (uint)Marshal.SizeOf<Native.DWM_TIMING_INFO>(),
            };
            int hr = Native.DwmGetCompositionTimingInfo(hwnd, ref native);
            sample = new DwmTimingSample
            {
                Supported = hr >= 0,
                HResult = hr,
                CapturedAtUtc = DateTimeOffset.UtcNow,
                RefreshRateHz = Ratio(native.rateRefresh.Numerator, native.rateRefresh.Denominator),
                ComposeRateHz = Ratio(native.rateCompose.Numerator, native.rateCompose.Denominator),
                QpcRefreshPeriod = native.qpcRefreshPeriod,
                QpcVBlank = native.qpcVBlank,
                QpcCompose = native.qpcCompose,
                QpcFrameDisplayed = native.qpcFrameDisplayed,
                CRefresh = native.cRefresh,
                CFramesDisplayed = native.cFramesDisplayed,
                CFramesComplete = native.cFramesComplete,
                CFramesPending = native.cFramesPending,
                CFramesAvailable = native.cFramesAvailable,
                CFramesDropped = native.cFramesDropped,
                CFramesMissed = native.cFramesMissed,
                CFramesLate = native.cFramesLate,
                CFramesOutstanding = native.cFramesOutstanding,
                CFrame = native.cFrame,
                CFrameDisplayed = native.cFrameDisplayed,
                CFrameComplete = native.cFrameComplete,
                CFramePending = native.cFramePending,
                Failure = hr < 0 ? $"HRESULT 0x{hr:X8}" : null,
            };
            return hr >= 0;
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            sample = sample with { Failure = ex.GetType().Name };
            return false;
        }
    }

    internal static bool TryEnableDwmMmcss(out IDisposable? revertScope)
    {
        revertScope = null;
        if (!TrySetDwmMmcss(true)) return false;

        // DwmEnableMMCSS has no query API.  The scope therefore restores the
        // documented default (disabled), rather than claiming to know a prior
        // third-party setting.  Callers that already know the prior state may
        // use TrySetDwmMmcss(..., restoreValue) below.
        revertScope = new ActionScope(() => TrySetDwmMmcss(false));
        return true;
    }

    internal static bool TrySetDwmMmcss(bool enabled)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6)) return false;
        try
        {
            return Native.DwmEnableMMCSS(enabled) >= 0;
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            return false;
        }
    }

    internal static bool TrySetDwmMmcss(bool enabled, bool restoreValue, out IDisposable? revertScope)
    {
        revertScope = null;
        if (!TrySetDwmMmcss(enabled)) return false;
        revertScope = new ActionScope(() => TrySetDwmMmcss(restoreValue));
        return true;
    }

    internal static async Task<DwmProgressComparison> MeasureDwmProgressAsync(
        TimeSpan interval,
        IntPtr hwnd = default,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        if (!TryGetDwmTiming(out DwmTimingSample before, hwnd))
        {
            return new DwmProgressComparison
            {
                Supported = false,
                Before = before,
                ErrorCode = before.HResult,
            };
        }

        TimeSpan bounded = Clamp(interval, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5));
        try
        {
            await Task.Delay(bounded, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new DwmProgressComparison
            {
                Supported = true,
                Cancelled = true,
                Before = before,
                ElapsedMilliseconds = started.ElapsedMilliseconds,
            };
        }

        if (!TryGetDwmTiming(out DwmTimingSample after, hwnd))
        {
            return new DwmProgressComparison
            {
                Supported = false,
                Before = before,
                After = after,
                ErrorCode = after.HResult,
                ElapsedMilliseconds = started.ElapsedMilliseconds,
            };
        }

        return CompareDwmProgress(before, after, started.ElapsedMilliseconds);
    }

    internal static DwmProgressComparison CompareDwmProgress(
        DwmTimingSample before,
        DwmTimingSample after,
        long elapsedMilliseconds = 0)
    {
        ulong displayed = Delta(after.CFramesDisplayed, before.CFramesDisplayed);
        ulong complete = Delta(after.CFramesComplete, before.CFramesComplete);
        ulong dropped = Delta(after.CFramesDropped, before.CFramesDropped);
        ulong missed = Delta(after.CFramesMissed, before.CFramesMissed);
        ulong pending = Delta(after.CFramesPending, before.CFramesPending);
        ulong composition = Delta(after.CFrame, before.CFrame);
        ulong late = Delta(after.CFramesLate, before.CFramesLate);
        bool vblankProgressed = after.QpcVBlank != before.QpcVBlank || after.CRefresh != before.CRefresh;
        return new DwmProgressComparison
        {
            Supported = before.Supported && after.Supported,
            Before = before,
            After = after,
            ElapsedMilliseconds = elapsedMilliseconds,
            FramesDisplayedDelta = displayed,
            FramesCompleteDelta = complete,
            FramesDroppedDelta = dropped,
            FramesMissedDelta = missed,
            FramesPendingDelta = pending,
            CompositionFramesDelta = composition,
            LateFramesDelta = late,
            VBlankProgressed = vblankProgressed,
            Progressed = displayed > 0 || complete > 0 || composition > 0 || vblankProgressed,
            Worsened = dropped > 0 || missed > 0 || late > 0,
            ErrorCode = after.HResult,
        };
    }

    // ---------- low-memory resource notifications ----------

    internal sealed record LowMemoryNotification
    {
        internal bool Supported { get; init; }
        internal bool LowMemory { get; init; }
        internal bool HighMemory { get; init; }
        internal bool LowQuerySucceeded { get; init; }
        internal bool HighQuerySucceeded { get; init; }
        internal int ErrorCode { get; init; }
    }

    internal static bool TryGetLowMemoryNotification(out LowMemoryNotification notification)
    {
        notification = new LowMemoryNotification();
        if (!OperatingSystem.IsWindowsVersionAtLeast(6)) return false;

        IntPtr lowHandle = IntPtr.Zero;
        IntPtr highHandle = IntPtr.Zero;
        try
        {
            lowHandle = Native.CreateMemoryResourceNotification(
                Native.MEMORY_RESOURCE_NOTIFICATION_TYPE.LowMemoryResourceNotification);
            highHandle = Native.CreateMemoryResourceNotification(
                Native.MEMORY_RESOURCE_NOTIFICATION_TYPE.HighMemoryResourceNotification);
            int createError = lowHandle != IntPtr.Zero && highHandle != IntPtr.Zero
                ? ErrorSuccess
                : Marshal.GetLastWin32Error();

            bool low = false;
            bool high = false;
            bool lowOk = lowHandle != IntPtr.Zero && Native.QueryMemoryResourceNotification(lowHandle, out low);
            int lowError = lowOk ? ErrorSuccess : Marshal.GetLastWin32Error();
            bool highOk = highHandle != IntPtr.Zero && Native.QueryMemoryResourceNotification(highHandle, out high);
            int highError = highOk ? ErrorSuccess : Marshal.GetLastWin32Error();
            notification = new LowMemoryNotification
            {
                Supported = lowOk || highOk,
                LowMemory = low,
                HighMemory = high,
                LowQuerySucceeded = lowOk,
                HighQuerySucceeded = highOk,
                ErrorCode = lowOk ? (highOk ? createError : highError) : lowError,
            };
            return notification.Supported;
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            notification = notification with { ErrorCode = ErrorCode(ex) };
            return false;
        }
        finally
        {
            CloseHandle(lowHandle);
            CloseHandle(highHandle);
        }
    }

    // ---------- bounded window responsiveness ----------

    internal sealed record WindowMessageProbe
    {
        internal bool Completed { get; init; }
        internal bool TimedOut { get; init; }
        internal int ErrorCode { get; init; }
        internal IntPtr Result { get; init; }
        internal long ElapsedMilliseconds { get; init; }
    }

    internal static bool TryProbeWindow(IntPtr hwnd, TimeSpan timeout, out WindowMessageProbe probe)
    {
        probe = new WindowMessageProbe();
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(6)) return false;

        TimeSpan bounded = Clamp(timeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5));
        uint timeoutMs = (uint)Math.Clamp((long)bounded.TotalMilliseconds, 1L, 5000L);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            IntPtr result;
            IntPtr callResult = Native.SendMessageTimeout(
                hwnd,
                Native.WM_NULL,
                IntPtr.Zero,
                IntPtr.Zero,
                Native.SMTO_ABORTIFHUNG | Native.SMTO_ERRORONEXIT,
                timeoutMs,
                out result);
            int error = callResult != IntPtr.Zero ? ErrorSuccess : Marshal.GetLastWin32Error();
            probe = new WindowMessageProbe
            {
                Completed = callResult != IntPtr.Zero,
                TimedOut = callResult == IntPtr.Zero && error == Native.ERROR_TIMEOUT,
                ErrorCode = error,
                Result = result,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
            return probe.Completed;
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            probe = new WindowMessageProbe
            {
                ErrorCode = ErrorCode(ex),
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
            return false;
        }
    }

    // ---------- process I/O and QoS ----------

    internal sealed record ProcessIoSnapshot
    {
        internal bool Supported { get; init; }
        internal uint ProcessId { get; init; }
        internal DateTimeOffset CapturedAtUtc { get; init; }
        internal ulong ReadOperationCount { get; init; }
        internal ulong WriteOperationCount { get; init; }
        internal ulong OtherOperationCount { get; init; }
        internal ulong ReadTransferCount { get; init; }
        internal ulong WriteTransferCount { get; init; }
        internal ulong OtherTransferCount { get; init; }
        internal int ErrorCode { get; init; }
    }

    internal static bool TryGetProcessIo(uint processId, out ProcessIoSnapshot snapshot)
    {
        snapshot = new ProcessIoSnapshot
        {
            ProcessId = processId,
            CapturedAtUtc = DateTimeOffset.UtcNow,
        };
        if (processId == 0 || !OperatingSystem.IsWindowsVersionAtLeast(6)) return false;

        IntPtr process = IntPtr.Zero;
        try
        {
            process = OpenProcessForQuery(processId);
            if (process == IntPtr.Zero)
            {
                snapshot = snapshot with { ErrorCode = Marshal.GetLastWin32Error() };
                return false;
            }

            if (!Native.GetProcessIoCounters(process, out Native.IO_COUNTERS counters))
            {
                snapshot = snapshot with { ErrorCode = Marshal.GetLastWin32Error() };
                return false;
            }

            snapshot = new ProcessIoSnapshot
            {
                Supported = true,
                ProcessId = processId,
                CapturedAtUtc = DateTimeOffset.UtcNow,
                ReadOperationCount = counters.ReadOperationCount,
                WriteOperationCount = counters.WriteOperationCount,
                OtherOperationCount = counters.OtherOperationCount,
                ReadTransferCount = counters.ReadTransferCount,
                WriteTransferCount = counters.WriteTransferCount,
                OtherTransferCount = counters.OtherTransferCount,
            };
            return true;
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            snapshot = snapshot with { ErrorCode = ErrorCode(ex) };
            return false;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    internal readonly record struct ProcessPowerThrottlingSnapshot(
        bool Supported,
        uint Version,
        uint ControlMask,
        uint StateMask,
        int ErrorCode = 0);

    internal static bool TryGetProcessPowerThrottling(
        uint processId,
        out ProcessPowerThrottlingSnapshot snapshot)
    {
        snapshot = default;
        if (processId == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0)) return false;

        IntPtr process = IntPtr.Zero;
        try
        {
            process = OpenProcessForQuery(processId);
            if (process == IntPtr.Zero)
            {
                snapshot = snapshot with { ErrorCode = Marshal.GetLastWin32Error() };
                return false;
            }
            return TryGetPowerState(process, out snapshot);
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            snapshot = snapshot with { ErrorCode = ErrorCode(ex) };
            return false;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    internal static bool TrySetProcessPowerThrottling(
        uint processId,
        ProcessPowerThrottlingSnapshot state)
    {
        if (processId == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0)) return false;
        IntPtr process = IntPtr.Zero;
        try
        {
            process = OpenProcessForInformation(processId);
            return process != IntPtr.Zero && TrySetPowerState(process, state, out _);
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            return false;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    internal static bool TryTemporarilySetProcessPowerThrottling(
        uint processId,
        ProcessPowerThrottlingSnapshot desired,
        out IDisposable? revertScope)
    {
        revertScope = null;
        if (processId == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0)) return false;

        IntPtr process = IntPtr.Zero;
        try
        {
            process = OpenProcessForInformation(processId);
            if (process == IntPtr.Zero || !TryGetPowerState(process, out ProcessPowerThrottlingSnapshot previous))
                return false;
            if (!TrySetPowerState(process, desired, out _)) return false;

            revertScope = new ProcessPowerScope(process, previous);
            process = IntPtr.Zero;
            return true;
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            return false;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool TryGetPowerState(IntPtr process, out ProcessPowerThrottlingSnapshot snapshot)
    {
        snapshot = default;
        int size = Marshal.SizeOf<Native.PROCESS_POWER_THROTTLING_STATE>();
        IntPtr memory = IntPtr.Zero;
        try
        {
            memory = Marshal.AllocHGlobal(size);
            if (!Native.GetProcessInformation(
                    process,
                    Native.PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                    memory,
                    (uint)size))
            {
                snapshot = snapshot with { ErrorCode = Marshal.GetLastWin32Error() };
                return false;
            }
            var native = Marshal.PtrToStructure<Native.PROCESS_POWER_THROTTLING_STATE>(memory);
            snapshot = new ProcessPowerThrottlingSnapshot(
                true,
                native.Version,
                native.ControlMask,
                native.StateMask);
            return true;
        }
        finally
        {
            if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
        }
    }

    private static bool TrySetPowerState(
        IntPtr process,
        ProcessPowerThrottlingSnapshot state,
        out int errorCode)
    {
        errorCode = 0;
        int size = Marshal.SizeOf<Native.PROCESS_POWER_THROTTLING_STATE>();
        IntPtr memory = IntPtr.Zero;
        try
        {
            memory = Marshal.AllocHGlobal(size);
            var native = new Native.PROCESS_POWER_THROTTLING_STATE
            {
                Version = state.Version == 0 ? Native.PROCESS_POWER_THROTTLING_CURRENT_VERSION : state.Version,
                ControlMask = state.ControlMask,
                StateMask = state.StateMask,
            };
            Marshal.StructureToPtr(native, memory, false);
            if (Native.SetProcessInformation(
                    process,
                    Native.PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                    memory,
                    (uint)size))
                return true;
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }
        finally
        {
            if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
        }
    }

    private sealed class ProcessPowerScope : IDisposable
    {
        private IntPtr _process;
        private readonly ProcessPowerThrottlingSnapshot _previous;

        internal ProcessPowerScope(IntPtr process, ProcessPowerThrottlingSnapshot previous)
        {
            _process = process;
            _previous = previous;
        }

        public void Dispose()
        {
            IntPtr process = Interlocked.Exchange(ref _process, IntPtr.Zero);
            if (process == IntPtr.Zero) return;
            try { _ = TrySetPowerState(process, _previous, out _); }
            catch { /* A process can exit before rollback. */ }
            finally { CloseHandle(process); }
        }
    }

    // ---------- CPU Sets ----------

    internal sealed record CpuSetInfo
    {
        internal uint Id { get; init; }
        internal ushort Group { get; init; }
        internal byte LogicalProcessorIndex { get; init; }
        internal byte CoreIndex { get; init; }
        internal byte LastLevelCacheIndex { get; init; }
        internal byte NumaNodeIndex { get; init; }
        internal byte EfficiencyClass { get; init; }
        internal byte Flags { get; init; }
        internal uint SchedulingClass { get; init; }
        internal uint AllocationTag { get; init; }
        internal bool Allocated => (Flags & 0x01) != 0;
        internal bool Parked => (Flags & 0x02) != 0;
    }

    internal static bool TryGetProcessDefaultCpuSets(uint processId, out uint[] cpuSetIds)
    {
        cpuSetIds = Array.Empty<uint>();
        if (processId == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0)) return false;
        IntPtr process = IntPtr.Zero;
        try
        {
            process = OpenProcessForQuery(processId);
            return process != IntPtr.Zero && TryReadProcessCpuSets(process, out cpuSetIds);
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            return false;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    internal static bool TrySetProcessDefaultCpuSets(uint processId, IReadOnlyList<uint> cpuSetIds)
    {
        if (processId == 0 || cpuSetIds is null || cpuSetIds.Count > MaxCpuSetIds ||
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0)) return false;
        IntPtr process = IntPtr.Zero;
        try
        {
            process = OpenProcessForInformation(processId);
            return process != IntPtr.Zero && TrySetProcessCpuSets(process, cpuSetIds);
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            return false;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    internal static bool TryTemporarilySetProcessDefaultCpuSets(
        uint processId,
        IReadOnlyList<uint> desiredCpuSetIds,
        out IDisposable? revertScope)
    {
        revertScope = null;
        if (processId == 0 || desiredCpuSetIds is null || desiredCpuSetIds.Count > MaxCpuSetIds ||
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0)) return false;

        IntPtr process = IntPtr.Zero;
        try
        {
            process = OpenProcessForInformation(processId);
            if (process == IntPtr.Zero || !TryReadProcessCpuSets(process, out uint[] previous)) return false;
            if (!TrySetProcessCpuSets(process, desiredCpuSetIds)) return false;

            revertScope = new CpuSetScope(process, previous);
            process = IntPtr.Zero;
            return true;
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            return false;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    internal static bool TryGetSystemCpuSets(out IReadOnlyList<CpuSetInfo> cpuSets)
    {
        cpuSets = Array.Empty<CpuSetInfo>();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0)) return false;

        IntPtr memory = IntPtr.Zero;
        try
        {
            uint required = 0;
            _ = Native.GetSystemCpuSetInformation(IntPtr.Zero, 0, out required, IntPtr.Zero, 0);
            int firstError = Marshal.GetLastWin32Error();
            if (required == 0 && firstError != ErrorInsufficientBuffer) return false;
            if (required == 0 || required > 1024 * 1024) return false;

            memory = Marshal.AllocHGlobal(checked((int)required));
            if (!Native.GetSystemCpuSetInformation(memory, required, out uint returned, IntPtr.Zero, 0))
                return false;

            int available = (int)Math.Min(returned, required);
            var list = new List<CpuSetInfo>();
            int offset = 0;
            while (offset + 8 <= available)
            {
                int size = Marshal.ReadInt32(memory, offset);
                int type = Marshal.ReadInt32(memory, offset + 4);
                if (size < 8 || size > available - offset) break;
                if (type == 0 && size >= 20)
                {
                    list.Add(new CpuSetInfo
                    {
                        Id = unchecked((uint)Marshal.ReadInt32(memory, offset + 8)),
                        Group = unchecked((ushort)Marshal.ReadInt16(memory, offset + 12)),
                        LogicalProcessorIndex = Marshal.ReadByte(memory, offset + 14),
                        CoreIndex = Marshal.ReadByte(memory, offset + 15),
                        LastLevelCacheIndex = Marshal.ReadByte(memory, offset + 16),
                        NumaNodeIndex = Marshal.ReadByte(memory, offset + 17),
                        EfficiencyClass = Marshal.ReadByte(memory, offset + 18),
                        Flags = Marshal.ReadByte(memory, offset + 19),
                        SchedulingClass = size >= 24
                            ? unchecked((uint)Marshal.ReadInt32(memory, offset + 20))
                            : 0,
                        AllocationTag = size >= 28
                            ? unchecked((uint)Marshal.ReadInt32(memory, offset + 24))
                            : 0,
                    });
                }
                offset += size;
            }

            cpuSets = list;
            return true;
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            return false;
        }
        finally
        {
            if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
        }
    }

    private static bool TryReadProcessCpuSets(IntPtr process, out uint[] ids)
    {
        ids = Array.Empty<uint>();
        uint required = 0;
        _ = Native.GetProcessDefaultCpuSets(process, IntPtr.Zero, 0, out required);
        int firstError = Marshal.GetLastWin32Error();
        if (required == 0)
            return firstError == ErrorSuccess || firstError == ErrorInsufficientBuffer;
        if (required > MaxCpuSetIds) return false;

        IntPtr memory = IntPtr.Zero;
        try
        {
            memory = Marshal.AllocHGlobal(checked((int)required * sizeof(int)));
            if (!Native.GetProcessDefaultCpuSets(process, memory, required, out uint returned)) return false;
            int count = (int)Math.Min(returned == 0 ? required : returned, required);
            int[] raw = new int[count];
            Marshal.Copy(memory, raw, 0, count);
            ids = raw.Select(value => unchecked((uint)value)).ToArray();
            return true;
        }
        finally
        {
            if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
        }
    }

    private static bool TrySetProcessCpuSets(IntPtr process, IReadOnlyList<uint> ids)
    {
        if (ids.Count > MaxCpuSetIds) return false;
        if (ids.Count == 0)
        {
            return Native.SetProcessDefaultCpuSets(process, IntPtr.Zero, 0);
        }

        IntPtr memory = IntPtr.Zero;
        try
        {
            memory = Marshal.AllocHGlobal(checked(ids.Count * sizeof(int)));
            int[] raw = ids.Select(value => unchecked((int)value)).ToArray();
            Marshal.Copy(raw, 0, memory, raw.Length);
            return Native.SetProcessDefaultCpuSets(process, memory, (uint)raw.Length);
        }
        finally
        {
            if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
        }
    }

    private sealed class CpuSetScope : IDisposable
    {
        private IntPtr _process;
        private readonly uint[] _previous;

        internal CpuSetScope(IntPtr process, uint[] previous)
        {
            _process = process;
            _previous = previous;
        }

        public void Dispose()
        {
            IntPtr process = Interlocked.Exchange(ref _process, IntPtr.Zero);
            if (process == IntPtr.Zero) return;
            try { _ = TrySetProcessCpuSets(process, _previous); }
            catch { /* The target may have exited before rollback. */ }
            finally { CloseHandle(process); }
        }
    }

    // ---------- MMCSS worker registration ----------

    internal sealed class MmcssWorkerRegistration : IDisposable
    {
        private IntPtr _handle;

        internal MmcssWorkerRegistration(IntPtr handle, uint taskIndex)
        {
            _handle = handle;
            TaskIndex = taskIndex;
        }

        internal uint TaskIndex { get; }

        internal bool TrySetPriority(int priority)
        {
            IntPtr handle = Volatile.Read(ref _handle);
            if (handle == IntPtr.Zero || priority < -8 || priority > 8) return false;
            try { return Native.AvSetMmThreadPriority(handle, priority); }
            catch (Exception ex) when (IsNativeUnavailable(ex)) { return false; }
        }

        public void Dispose()
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle == IntPtr.Zero) return;
            try { _ = Native.AvRevertMmThreadCharacteristics(handle); }
            catch { /* Revert is best effort if the thread/process is exiting. */ }
        }
    }

    internal static bool TryRegisterMmcssWorker(
        string taskName,
        out MmcssWorkerRegistration? registration)
    {
        registration = null;
        if (string.IsNullOrWhiteSpace(taskName) || taskName.Length > 256 ||
            !OperatingSystem.IsWindowsVersionAtLeast(6)) return false;
        try
        {
            IntPtr handle = Native.AvSetMmThreadCharacteristics(taskName, out uint taskIndex);
            if (handle == IntPtr.Zero) return false;
            registration = new MmcssWorkerRegistration(handle, taskIndex);
            return true;
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            return false;
        }
    }

    // ---------- bounded/cancellable Wait Chain Traversal ----------

    internal readonly record struct WaitChainNode(
        int ObjectType,
        int ObjectStatus,
        uint ProcessId,
        uint ThreadId,
        uint WaitTime,
        uint ContextSwitches,
        string? ObjectName);

    internal sealed record WaitChainResult
    {
        internal bool Supported { get; init; }
        internal bool Succeeded { get; init; }
        internal bool TimedOut { get; init; }
        internal bool Cancelled { get; init; }
        internal bool IsCycle { get; init; }
        internal int ErrorCode { get; init; }
        internal IReadOnlyList<WaitChainNode> Nodes { get; init; } = Array.Empty<WaitChainNode>();
    }

    internal static async Task<WaitChainResult> GetWaitChainAsync(
        uint threadId,
        TimeSpan timeout,
        bool includeOutOfProcess = false,
        CancellationToken cancellationToken = default)
    {
        if (threadId == 0 || !OperatingSystem.IsWindowsVersionAtLeast(6))
            return new WaitChainResult { Supported = false };
        if (cancellationToken.IsCancellationRequested)
            return new WaitChainResult { Supported = true, Cancelled = true, ErrorCode = ErrorCancelled };

        TimeSpan bounded = Clamp(timeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5));
        WctSessionLease? lease = null;
        try
        {
            IntPtr session = Native.OpenThreadWaitChainSession(Native.WCT_SYNC_OPEN_SESSION, IntPtr.Zero);
            if (session == IntPtr.Zero)
            {
                return new WaitChainResult
                {
                    Supported = false,
                    ErrorCode = Marshal.GetLastWin32Error(),
                };
            }

            lease = new WctSessionLease(session);
            using CancellationTokenRegistration cancellation = cancellationToken.Register(lease.Close);
            Task<WaitChainNativeResult> operation = Task.Run(
                () => ReadWaitChain(lease.Handle, threadId, includeOutOfProcess),
                CancellationToken.None);
            Task delay = Task.Delay(bounded, cancellationToken);
            Task completed = await Task.WhenAny(operation, delay).ConfigureAwait(false);
            if (completed != operation)
            {
                bool cancelled = cancellationToken.IsCancellationRequested;
                lease.Close();
                return new WaitChainResult
                {
                    Supported = true,
                    TimedOut = !cancelled,
                    Cancelled = cancelled,
                    ErrorCode = cancelled ? ErrorCancelled : Native.ERROR_TIMEOUT,
                };
            }

            WaitChainNativeResult result = await operation.ConfigureAwait(false);
            return new WaitChainResult
            {
                Supported = true,
                Succeeded = result.Succeeded,
                IsCycle = result.IsCycle,
                ErrorCode = result.ErrorCode,
                Nodes = result.Nodes,
            };
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            return new WaitChainResult { Supported = false, ErrorCode = ErrorCode(ex) };
        }
        finally
        {
            lease?.Close();
        }
    }

    private readonly record struct WaitChainNativeResult(
        bool Succeeded,
        bool IsCycle,
        int ErrorCode,
        IReadOnlyList<WaitChainNode> Nodes);

    private static WaitChainNativeResult ReadWaitChain(
        IntPtr session,
        uint threadId,
        bool includeOutOfProcess)
    {
        if (session == IntPtr.Zero)
            return new WaitChainNativeResult(false, false, ErrorInvalidHandle, Array.Empty<WaitChainNode>());

        int bytes = checked(MaxWctNodes * Native.WCT_NODE_INFO_SIZE);
        IntPtr nodeMemory = IntPtr.Zero;
        try
        {
            nodeMemory = Marshal.AllocHGlobal(bytes);
            uint nodeCount = Native.WCT_MAX_NODE_COUNT;
            uint flags = includeOutOfProcess ? Native.WCT_OUT_OF_PROC_FLAG : 0;
            bool cycle;
            bool ok = Native.GetThreadWaitChain(
                session,
                IntPtr.Zero,
                flags,
                threadId,
                ref nodeCount,
                nodeMemory,
                out cycle);
            int error = ok ? ErrorSuccess : Marshal.GetLastWin32Error();
            int count = (int)Math.Min(nodeCount, Native.WCT_MAX_NODE_COUNT);
            var nodes = new List<WaitChainNode>(count);
            for (int index = 0; index < count; index++)
            {
                IntPtr node = IntPtr.Add(nodeMemory, index * Native.WCT_NODE_INFO_SIZE);
                int type = Marshal.ReadInt32(node, 0);
                int status = Marshal.ReadInt32(node, 4);
                uint processId = unchecked((uint)Marshal.ReadInt32(node, 8));
                uint chainThreadId = unchecked((uint)Marshal.ReadInt32(node, 12));
                uint waitTime = unchecked((uint)Marshal.ReadInt32(node, 16));
                uint contextSwitches = unchecked((uint)Marshal.ReadInt32(node, 20));
                string? objectName = type is >= 1 and <= 5
                    ? ReadNativeString(node, 8, 128)
                    : null;
                nodes.Add(new WaitChainNode(
                    type,
                    status,
                    processId,
                    chainThreadId,
                    waitTime,
                    contextSwitches,
                    objectName));
            }
            return new WaitChainNativeResult(ok, cycle, error, nodes);
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            return new WaitChainNativeResult(false, false, ErrorCode(ex), Array.Empty<WaitChainNode>());
        }
        finally
        {
            if (nodeMemory != IntPtr.Zero) Marshal.FreeHGlobal(nodeMemory);
        }
    }

    private sealed class WctSessionLease
    {
        private IntPtr _handle;

        internal WctSessionLease(IntPtr handle) { _handle = handle; }

        internal IntPtr Handle => Volatile.Read(ref _handle);

        internal void Close()
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle == IntPtr.Zero) return;
            try { Native.CloseThreadWaitChainSession(handle); }
            catch { }
        }
    }

    // ---------- application restart and recovery registration ----------

    internal static bool TryRegisterApplicationRestart(string? commandLine, uint flags = 0)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6)) return false;
        try { return Native.RegisterApplicationRestart(commandLine, flags) >= 0; }
        catch (Exception ex) when (IsNativeUnavailable(ex)) { return false; }
    }

    internal static bool TryUnregisterApplicationRestart()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6)) return false;
        try { return Native.UnregisterApplicationRestart() >= 0; }
        catch (Exception ex) when (IsNativeUnavailable(ex)) { return false; }
    }

    internal sealed class ApplicationRecoveryRegistration : IDisposable
    {
        private readonly object _gate = new();
        private readonly Func<CancellationToken, bool> _callback;
        private readonly Native.ApplicationRecoveryCallback _nativeCallback;
        private GCHandle _selfHandle;
        private bool _disposed;

        private ApplicationRecoveryRegistration(Func<CancellationToken, bool> callback)
        {
            _callback = callback;
            _nativeCallback = InvokeCallback;
        }

        internal static bool TryRegister(
            Func<CancellationToken, bool> callback,
            TimeSpan pingInterval,
            out ApplicationRecoveryRegistration? registration)
        {
            registration = null;
            if (callback is null || !OperatingSystem.IsWindowsVersionAtLeast(6)) return false;

            var candidate = new ApplicationRecoveryRegistration(callback);
            try
            {
                candidate._selfHandle = GCHandle.Alloc(candidate, GCHandleType.Normal);
                uint interval = (uint)Math.Clamp((long)pingInterval.TotalMilliseconds, 500L, 60000L);
                int result = Native.RegisterApplicationRecoveryCallback(
                    candidate._nativeCallback,
                    GCHandle.ToIntPtr(candidate._selfHandle),
                    interval,
                    0);
                if (result < 0)
                {
                    candidate._selfHandle.Free();
                    return false;
                }
                registration = candidate;
                return true;
            }
            catch (Exception ex) when (IsNativeUnavailable(ex))
            {
                if (candidate._selfHandle.IsAllocated) candidate._selfHandle.Free();
                return false;
            }
        }

        private static uint InvokeCallback(IntPtr parameter)
        {
            if (parameter == IntPtr.Zero) return 1;
            try
            {
                var handle = GCHandle.FromIntPtr(parameter);
                if (handle.Target is not ApplicationRecoveryRegistration registration)
                    return 1;
                return registration.RunCallback() ? 0u : 1u;
            }
            catch
            {
                return 1;
            }
        }

        private bool RunCallback()
        {
            lock (_gate)
            {
                if (_disposed) return false;
            }

            bool success = false;
            try
            {
                _ = Native.ApplicationRecoveryInProgress();
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                success = _callback(cancellation.Token);
                return success;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { Native.ApplicationRecoveryFinished(success); }
                catch { }
            }
        }

        internal bool Ping()
        {
            try { return Native.ApplicationRecoveryInProgress(); }
            catch (Exception ex) when (IsNativeUnavailable(ex)) { return false; }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            try { _ = Native.UnregisterApplicationRecoveryCallback(); }
            catch { }
            if (_selfHandle.IsAllocated) _selfHandle.Free();
        }
    }

    internal static bool TryRegisterApplicationRecovery(
        Func<CancellationToken, bool> callback,
        TimeSpan pingInterval,
        out ApplicationRecoveryRegistration? registration)
    {
        return ApplicationRecoveryRegistration.TryRegister(callback, pingInterval, out registration);
    }

    // ---------- bounded recent event-log correlation ----------

    internal sealed record RecentEventCorrelation
    {
        internal string Channel { get; init; } = string.Empty;
        internal string ProviderName { get; init; } = string.Empty;
        internal int EventId { get; init; }
        internal byte? Level { get; init; }
        internal DateTimeOffset? TimeCreatedUtc { get; init; }
        internal bool Relevant { get; init; }
    }

    internal static IReadOnlyList<RecentEventCorrelation> CorrelateRecentEvents(
        TimeSpan lookback,
        TimeSpan budget,
        IEnumerable<string>? channels = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<RecentEventCorrelation>();
        if (!OperatingSystem.IsWindowsVersionAtLeast(6)) return result;

        long lookbackMs = Math.Clamp((long)lookback.TotalMilliseconds, 1L, 86_400_000L);
        TimeSpan boundedBudget = Clamp(budget, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5));
        string[] requestedChannels = (channels ?? new[] { "System", "Application" })
            .Where(channel => !string.IsNullOrWhiteSpace(channel))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        if (requestedChannels.Length == 0) return result;

        var stopwatch = Stopwatch.StartNew();
        string query = $"*[System[TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]";
        foreach (string channel in requestedChannels)
        {
            if (stopwatch.Elapsed >= boundedBudget || cancellationToken.IsCancellationRequested) break;
            try
            {
                var eventQuery = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                    channel,
                    System.Diagnostics.Eventing.Reader.PathType.LogName,
                    query)
                {
                    ReverseDirection = true,
                };
                using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(eventQuery);
                while (result.Count < MaxEventRecords && stopwatch.Elapsed < boundedBudget &&
                       !cancellationToken.IsCancellationRequested)
                {
                    TimeSpan remaining = boundedBudget - stopwatch.Elapsed;
                    if (remaining <= TimeSpan.Zero) break;
                    System.Diagnostics.Eventing.Reader.EventRecord? record =
                        reader.ReadEvent(TimeSpan.FromMilliseconds(Math.Min(100, Math.Max(1, remaining.TotalMilliseconds))));
                    if (record is null) break;
                    using (record)
                    {
                        string provider = record.ProviderName ?? string.Empty;
                        result.Add(new RecentEventCorrelation
                        {
                            Channel = channel,
                            ProviderName = provider,
                            EventId = record.Id,
                            Level = record.Level is null ? null : record.Level.Value,
                            TimeCreatedUtc = record.TimeCreated is null
                                ? null
                                : new DateTimeOffset(DateTime.SpecifyKind(record.TimeCreated.Value, DateTimeKind.Utc)),
                            Relevant = IsRelevantEvent(provider),
                        });
                    }
                }
            }
            catch (Exception ex) when (IsEventLogUnavailable(ex))
            {
                // Missing channels, access denied and an unavailable event
                // service are diagnostic misses, not recovery failures.
            }
        }

        return result;
    }

    // ---------- device/display problem enumeration ----------

    internal sealed record DeviceProblem
    {
        internal bool IsDisplay { get; init; }
        internal uint DevInst { get; init; }
        internal uint Status { get; init; }
        internal uint ProblemCode { get; init; }
        internal string? DeviceDescription { get; init; }
        internal string? FriendlyName { get; init; }
        internal string? Manufacturer { get; init; }
        internal string? HardwareId { get; init; }
        internal string? InstanceId { get; init; }
        internal string? DriverKey { get; init; }
        internal string? Location { get; init; }
    }

    internal static IReadOnlyList<DeviceProblem> EnumerateDeviceProblems(
        bool displayOnly = false,
        bool includeHealthy = false,
        TimeSpan? budget = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<DeviceProblem>();
        if (!OperatingSystem.IsWindowsVersionAtLeast(6)) return result;

        Stopwatch? stopwatch = budget.HasValue ? Stopwatch.StartNew() : null;
        TimeSpan boundedBudget = budget.HasValue
            ? Clamp(budget.Value, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5))
            : Timeout.InfiniteTimeSpan;

        IntPtr deviceSet = IntPtr.Zero;
        try
        {
            if (displayOnly)
            {
                Guid displayClass = Native.GUID_DEVCLASS_DISPLAY;
                deviceSet = Native.SetupDiGetClassDevs(
                    ref displayClass,
                    null,
                    IntPtr.Zero,
                    Native.DIGCF_PRESENT);
            }
            else
            {
                deviceSet = Native.SetupDiGetClassDevsAll(
                    IntPtr.Zero,
                    null,
                    IntPtr.Zero,
                    Native.DIGCF_PRESENT | Native.DIGCF_ALLCLASSES);
            }

            if (deviceSet == IntPtr.Zero || deviceSet == new IntPtr(-1)) return result;
            uint index = 0;
            while (index < 8192 && result.Count < 128 &&
                   !cancellationToken.IsCancellationRequested &&
                   (stopwatch is null || stopwatch.Elapsed < boundedBudget))
            {
                var data = new Native.SP_DEVINFO_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<Native.SP_DEVINFO_DATA>(),
                };
                if (!Native.SetupDiEnumDeviceInfo(deviceSet, index++, ref data))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems) break;
                    continue;
                }

                uint status = 0;
                uint problem = 0;
                _ = Native.CM_Get_DevNode_Status(out status, out problem, data.DevInst, 0);
                if (!includeHealthy && problem == 0 && (status & Native.DN_HAS_PROBLEM) == 0) continue;

                result.Add(new DeviceProblem
                {
                    IsDisplay = displayOnly || data.ClassGuid == Native.GUID_DEVCLASS_DISPLAY,
                    DevInst = data.DevInst,
                    Status = status,
                    ProblemCode = problem,
                    DeviceDescription = ReadDeviceProperty(deviceSet, ref data, Native.SPDRP_DEVICEDESC),
                    FriendlyName = ReadDeviceProperty(deviceSet, ref data, Native.SPDRP_FRIENDLYNAME),
                    Manufacturer = ReadDeviceProperty(deviceSet, ref data, Native.SPDRP_MFG),
                    HardwareId = ReadDeviceProperty(deviceSet, ref data, Native.SPDRP_HARDWAREID),
                    DriverKey = ReadDeviceProperty(deviceSet, ref data, Native.SPDRP_DRIVER),
                    Location = ReadDeviceProperty(deviceSet, ref data, Native.SPDRP_LOCATION_INFORMATION),
                    InstanceId = ReadDeviceInstanceId(deviceSet, ref data),
                });
            }
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            // SetupAPI is optional and often denied in restricted sessions.
        }
        finally
        {
            if (deviceSet != IntPtr.Zero && deviceSet != new IntPtr(-1))
            {
                try { _ = Native.SetupDiDestroyDeviceInfoList(deviceSet); }
                catch { }
            }
        }

        return result;
    }

    // ---------- helpers ----------

    private static double Ratio(uint numerator, uint denominator)
        => denominator == 0 ? 0 : (double)numerator / denominator;

    private static ulong Delta(ulong newer, ulong older)
        => newer >= older ? newer - older : 0;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum)
        => value < minimum ? minimum : value > maximum ? maximum : value;

    private static bool IsRelevantEvent(string provider)
    {
        string text = provider.ToLowerInvariant();
        return text.Contains("display", StringComparison.Ordinal) ||
               text.Contains("dxg", StringComparison.Ordinal) ||
               text.Contains("kernel-power", StringComparison.Ordinal) ||
               text.Contains("whea", StringComparison.Ordinal) ||
               text.Contains("disk", StringComparison.Ordinal) ||
               text.Contains("stor", StringComparison.Ordinal) ||
               text.Contains("application hang", StringComparison.Ordinal) ||
               text.Contains("wer", StringComparison.Ordinal) ||
               text.Contains("resource-exhaustion", StringComparison.Ordinal);
    }

    private static bool IsEventLogUnavailable(Exception ex)
        => ex is System.Diagnostics.Eventing.Reader.EventLogException ||
           ex is UnauthorizedAccessException ||
           ex is InvalidOperationException ||
           ex is NotSupportedException;

    private static IntPtr OpenProcessForQuery(uint processId)
    {
        IntPtr handle = Native.OpenProcess(
            Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            processId);
        if (handle != IntPtr.Zero) return handle;
        return Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
    }

    private static IntPtr OpenProcessForInformation(uint processId)
        => Native.OpenProcess(
            Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_QUOTA,
            false,
            processId);

    private static void CloseHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { _ = Native.CloseHandle(handle); }
        catch { }
    }

    private static string? ReadNativeString(IntPtr pointer, int offset, int maxChars)
    {
        try
        {
            string? value = Marshal.PtrToStringUni(IntPtr.Add(pointer, offset), maxChars);
            return string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('\0');
        }
        catch { return null; }
    }

    private static string? ReadDeviceProperty(
        IntPtr deviceSet,
        ref Native.SP_DEVINFO_DATA data,
        uint property)
    {
        uint type;
        uint required;
        IntPtr memory = IntPtr.Zero;
        try
        {
            uint size = 512;
            memory = Marshal.AllocHGlobal((int)size);
            if (!Native.SetupDiGetDeviceRegistryProperty(
                    deviceSet,
                    ref data,
                    property,
                    out type,
                    memory,
                    size,
                    out required))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorInsufficientBuffer || required == 0 || required > MaxDevicePropertyBytes)
                    return null;
                Marshal.FreeHGlobal(memory);
                memory = Marshal.AllocHGlobal((int)required);
                size = required;
                if (!Native.SetupDiGetDeviceRegistryProperty(
                        deviceSet,
                        ref data,
                        property,
                        out type,
                        memory,
                        size,
                        out required)) return null;
            }

            int bytes = (int)Math.Min(required == 0 ? size : required, size);
            if (bytes <= 0) return null;
            string value = Marshal.PtrToStringUni(memory, Math.Max(0, bytes / 2)) ?? string.Empty;
            return value.Replace('\0', ';').Trim(';', ' ', '\r', '\n');
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            return null;
        }
        finally
        {
            if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
        }
    }

    private static string? ReadDeviceInstanceId(
        IntPtr deviceSet,
        ref Native.SP_DEVINFO_DATA data)
    {
        uint required = 0;
        _ = Native.SetupDiGetDeviceInstanceId(
            deviceSet,
            ref data,
            IntPtr.Zero,
            0,
            out required);
        if (required == 0 || required > 32 * 1024) return null;

        IntPtr memory = IntPtr.Zero;
        try
        {
            memory = Marshal.AllocHGlobal(checked((int)required * 2));
            if (!Native.SetupDiGetDeviceInstanceId(
                    deviceSet,
                    ref data,
                    memory,
                    required,
                    out _)) return null;
            return Marshal.PtrToStringUni(memory);
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            return null;
        }
        finally
        {
            if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
        }
    }

    private static bool IsNativeUnavailable(Exception ex)
        => ex is DllNotFoundException ||
           ex is EntryPointNotFoundException ||
           ex is BadImageFormatException ||
           ex is MarshalDirectiveException ||
           ex is PlatformNotSupportedException ||
           ex is NotSupportedException ||
           ex is UnauthorizedAccessException;

    private static int ErrorCode(Exception ex)
        => ex is System.ComponentModel.Win32Exception win32
            ? win32.NativeErrorCode
            : Marshal.GetLastWin32Error();

    private sealed class ActionScope : IDisposable
    {
        private Action? _action;

        internal ActionScope(Action action) { _action = action; }

        public void Dispose()
        {
            Action? action = Interlocked.Exchange(ref _action, null);
            if (action is null) return;
            try { action(); }
            catch { }
        }
    }
}
