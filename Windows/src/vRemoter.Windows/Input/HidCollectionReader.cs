using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using vRemoter.Windows.SystemSupport;
using static vRemoter.Windows.Input.HidNativeInterop;

namespace vRemoter.Windows.Input;

public readonly record struct HidUsagePair(ushort UsagePage, ushort Usage);

public sealed record HidInputReport(
    ushort CollectionUsagePage,
    ushort CollectionUsage,
    byte ReportId,
    byte[] Payload,
    byte[] RawPacket,
    HidUsagePair[] ParsedUsages);

/// <summary>
/// Reads raw HID input reports from non-exclusive HID Top-Level Collections
/// (Consumer, System, Vendor) via SetupDi + HidD + overlapped Win32 ReadFile.
/// </summary>
public sealed class HidCollectionReader : IDisposable
{
    private readonly record struct CollectionInfo(
        ushort UsagePage,
        ushort Usage,
        int InputReportByteLength,
        ushort InputBtnCaps = 0,
        ushort InputValCaps = 0);

    private sealed class OpenCollection(
        string path,
        ushort usagePage,
        ushort usage,
        int inputReportLength,
        SafeFileHandle handle,
        IntPtr preparsedData,
        IntPtr stopEvent)
    {
        public string Path { get; } = path;
        public ushort UsagePage { get; } = usagePage;
        public ushort Usage { get; } = usage;
        public int InputReportLength { get; } = inputReportLength;
        public SafeFileHandle Handle { get; } = handle;
        public IntPtr PreparsedData { get; set; } = preparsedData;
        public IntPtr StopEvent { get; set; } = stopEvent;
        public volatile bool Stopped;
    }

    private readonly ushort _vendorId;
    private readonly ushort _productId;
    private readonly string _logTag;
    private readonly object _sync = new();
    private readonly Dictionary<string, OpenCollection> _openStreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedOpenFailures = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _rescanTimer;
    private bool _started;
    private bool _connected;

    public event Action<bool>? ConnectionChanged;
    public event Action<HidInputReport>? InputReportReceived;

    public HidCollectionReader(ushort vendorId, ushort productId, string logTag)
    {
        _vendorId = vendorId;
        _productId = productId;
        _logTag = logTag;
    }

    public bool IsConnected
    {
        get
        {
            lock (_sync) { return _connected; }
        }
    }

    public bool HasOpenStream(ushort usagePage, ushort usage)
    {
        lock (_sync)
        {
            return _openStreams.Values.Any(s => s.UsagePage == usagePage && s.Usage == usage);
        }
    }

    public bool HasAnyOpenStream
    {
        get
        {
            lock (_sync) { return _openStreams.Count > 0; }
        }
    }

    public string DescribeStreams()
    {
        lock (_sync)
        {
            if (_openStreams.Count == 0) return "openStreams=0";
            var items = _openStreams.Values.Select(s => $"UP=0x{s.UsagePage:X4}/U=0x{s.Usage:X4}(len={s.InputReportLength})");
            return $"openStreams={_openStreams.Count} [{string.Join(", ", items)}]";
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }
        RefreshDevices();
        _rescanTimer = new Timer(_ => RefreshDevices(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_started) return;
            _started = false;
        }
        _rescanTimer?.Dispose();
        _rescanTimer = null;
        CloseAll();
    }

    public void RefreshDevices()
    {
        lock (_sync)
        {
            if (!_started) return;
        }

        var discovered = EnumerateMatchingDevices();
        bool anyPresent = discovered.Count > 0;
        bool connectionChanged = false;

        lock (_sync)
        {
            if (!_started) return;

            var stale = _openStreams.Keys.Where(k => !discovered.ContainsKey(k)).ToList();
            foreach (string key in stale)
            {
                CloseCollectionLocked(key);
                _loggedOpenFailures.Remove(key);
            }

            foreach (var (path, info) in discovered)
            {
                if (info.InputReportByteLength <= 0 || _openStreams.ContainsKey(path)) continue;
                TryOpenCollectionLocked(path, info);
            }

            if (_connected != anyPresent)
            {
                _connected = anyPresent;
                connectionChanged = true;
            }
        }

        if (connectionChanged)
        {
            ConnectionChanged?.Invoke(anyPresent);
            LogService.Write($"[{_logTag}] HID {(anyPresent ? "connected" : "disconnected")} VID=0x{_vendorId:X4} PID=0x{_productId:X4} collections={discovered.Count}");
        }
    }

    private void TryOpenCollectionLocked(string devicePath, CollectionInfo info)
    {
        var handle = CreateFileW(
            devicePath,
            GenericRead,
            FileShareReadWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (_loggedOpenFailures.Add(devicePath))
            {
                LogService.Write($"[{_logTag}] collection UP=0x{info.UsagePage:X4} U=0x{info.Usage:X4} len={info.InputReportByteLength} -> RawInput fallback (Win32={err})");
            }
            return;
        }

        IntPtr preparsed = IntPtr.Zero;
        if (!HidD_GetPreparsedData(handle, out preparsed))
        {
            preparsed = IntPtr.Zero;
        }

        IntPtr stopEvent = CreateEventW(IntPtr.Zero, bManualReset: true, bInitialState: false, null);
        if (stopEvent == IntPtr.Zero)
        {
            if (preparsed != IntPtr.Zero) _ = HidD_FreePreparsedData(preparsed);
            handle.Dispose();
            return;
        }

        int reportLen = info.InputReportByteLength > 0 ? info.InputReportByteLength : 64;
        var entry = new OpenCollection(
            devicePath,
            info.UsagePage,
            info.Usage,
            reportLen,
            handle,
            preparsed,
            stopEvent);
        _openStreams[devicePath] = entry;
        LogService.Write($"[{_logTag}] opened stream UP=0x{info.UsagePage:X4} U=0x{info.Usage:X4} reportLen={reportLen} btnCaps={info.InputBtnCaps} valCaps={info.InputValCaps}");
        _ = Task.Factory.StartNew(() => ReadLoop(entry), TaskCreationOptions.LongRunning);
    }

    private void ReadLoop(OpenCollection entry)
    {
        int readLen = entry.InputReportLength > 0 ? entry.InputReportLength : 64;
        IntPtr ioEvent = CreateEventW(IntPtr.Zero, bManualReset: true, bInitialState: false, null);
        if (ioEvent == IntPtr.Zero)
        {
            RemoveAndDispose(entry);
            return;
        }

        IntPtr readBuf = Marshal.AllocHGlobal(readLen);
        IntPtr overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<OVERLAPPED>());
        IntPtr[] waitHandles = [ioEvent, entry.StopEvent];

        try
        {
            while (!entry.Stopped)
            {
                _ = ResetEvent(ioEvent);
                var ov = new OVERLAPPED { HEvent = ioEvent };
                Marshal.StructureToPtr(ov, overlappedPtr, fDeleteOld: false);

                bool ok = ReadFile(entry.Handle, readBuf, (uint)readLen, out uint bytesRead, overlappedPtr);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ErrorIoPending)
                    {
                        if (!entry.Stopped && err != ErrorOperationAborted && err != ErrorDeviceNotConnected)
                        {
                            LogService.Write($"[{_logTag}] ReadFile stopped UP=0x{entry.UsagePage:X4} U=0x{entry.Usage:X4} Win32={err}");
                        }
                        break;
                    }

                    uint wait = WaitForMultipleObjects(2, waitHandles, bWaitAll: false, Infinite);
                    if (wait != WaitObject0 || entry.Stopped)
                    {
                        _ = CancelIoEx(entry.Handle, overlappedPtr);
                        _ = GetOverlappedResult(entry.Handle, overlappedPtr, out _, bWait: true);
                        break;
                    }

                    if (!GetOverlappedResult(entry.Handle, overlappedPtr, out bytesRead, bWait: false))
                    {
                        int ovErr = Marshal.GetLastWin32Error();
                        if (!entry.Stopped && ovErr != ErrorOperationAborted && ovErr != ErrorDeviceNotConnected)
                        {
                            LogService.Write($"[{_logTag}] GetOverlappedResult stopped UP=0x{entry.UsagePage:X4} U=0x{entry.Usage:X4} Win32={ovErr}");
                        }
                        break;
                    }
                }

                if (bytesRead == 0) continue;
                int count = (int)Math.Min(bytesRead, (uint)readLen);
                byte[] packet = new byte[count];
                Marshal.Copy(readBuf, packet, 0, count);

                byte reportId = packet[0];
                byte[] payload = count > 1 ? packet[1..] : [];
                var parsed = ParseHidUsages(entry.PreparsedData, entry.InputReportLength, packet);

                InputReportReceived?.Invoke(new HidInputReport(
                    entry.UsagePage,
                    entry.Usage,
                    reportId,
                    payload,
                    packet,
                    parsed));
            }
        }
        catch (Exception ex)
        {
            if (!entry.Stopped)
            {
                LogService.Write($"[{_logTag}] ReadLoop error UP=0x{entry.UsagePage:X4}: {ex.Message}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(overlappedPtr);
            Marshal.FreeHGlobal(readBuf);
            _ = CloseHandle(ioEvent);
            RemoveAndDispose(entry);
        }
    }

    private Dictionary<string, CollectionInfo> EnumerateMatchingDevices()
    {
        var result = new Dictionary<string, CollectionInfo>(StringComparer.OrdinalIgnoreCase);
        HidD_GetHidGuid(out Guid hidGuid);

        IntPtr devInfo = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (devInfo == new IntPtr(-1)) return result;

        try
        {
            var ifaceData = new SP_DEVICE_INTERFACE_DATA { CbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint idx = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, idx, ref ifaceData); idx++)
            {
                uint required = 0;
                _ = SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifaceData, IntPtr.Zero, 0, ref required, IntPtr.Zero);
                if (required == 0) continue;

                IntPtr detailBuf = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifaceData, detailBuf, required, ref required, IntPtr.Zero))
                    {
                        continue;
                    }
                    string? path = Marshal.PtrToStringUni(IntPtr.Add(detailBuf, 4));
                    if (string.IsNullOrEmpty(path)) continue;

                    if (TryInspectAttributes(path, out ushort vid, out ushort pid, out var info) &&
                        vid == _vendorId && pid == _productId)
                    {
                        result[path] = info;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(devInfo);
        }

        return result;
    }

    private static bool TryInspectAttributes(string path, out ushort vid, out ushort pid, out CollectionInfo info)
    {
        vid = pid = 0;
        info = default;

        using var handle = CreateFileW(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (HidD_GetAttributes(handle, ref attrs))
            {
                vid = attrs.VendorID;
                pid = attrs.ProductID;
            }
            if ((vid == 0 || pid == 0) && !CfgMgrDeviceResolver.TryResolveInterfaceVidPid(path, out vid, out pid))
            {
                return false;
            }

            if (HidD_GetPreparsedData(handle, out IntPtr preparsed))
            {
                try
                {
                    if (HidP_GetCaps(preparsed, out var caps) >= 0)
                    {
                        info = new CollectionInfo(
                            caps.UsagePage,
                            caps.Usage,
                            caps.InputReportByteLength,
                            caps.NumberInputButtonCaps,
                            caps.NumberInputValueCaps);
                        return true;
                    }
                }
                finally
                {
                    _ = HidD_FreePreparsedData(preparsed);
                }
            }
            info = new CollectionInfo(0, 0, 64);
            return true;
        }

        if (CfgMgrDeviceResolver.TryResolveInterfaceVidPid(path, out vid, out pid))
        {
            info = new CollectionInfo(0, 0, 0);
            return true;
        }
        return false;
    }

    private void RemoveAndDispose(OpenCollection entry)
    {
        lock (_sync)
        {
            if (_openStreams.TryGetValue(entry.Path, out var current) && ReferenceEquals(current, entry))
            {
                _openStreams.Remove(entry.Path);
            }
        }
        entry.Stopped = true;
        try { entry.Handle.Dispose(); } catch { }
        if (entry.PreparsedData != IntPtr.Zero)
        {
            try { _ = HidD_FreePreparsedData(entry.PreparsedData); } catch { }
            entry.PreparsedData = IntPtr.Zero;
        }
        if (entry.StopEvent != IntPtr.Zero)
        {
            try { _ = CloseHandle(entry.StopEvent); } catch { }
            entry.StopEvent = IntPtr.Zero;
        }
    }

    private void CloseCollectionLocked(string path)
    {
        if (!_openStreams.Remove(path, out var entry)) return;
        entry.Stopped = true;
        try { if (entry.StopEvent != IntPtr.Zero) _ = SetEvent(entry.StopEvent); } catch { }
        try { _ = CancelIoEx(entry.Handle, IntPtr.Zero); } catch { }
    }

    private void CloseAll()
    {
        bool wasConnected;
        lock (_sync)
        {
            foreach (string key in _openStreams.Keys.ToList())
            {
                CloseCollectionLocked(key);
            }
            _loggedOpenFailures.Clear();
            wasConnected = _connected;
            _connected = false;
        }
        if (wasConnected)
        {
            ConnectionChanged?.Invoke(false);
        }
    }

    public void Dispose() => Stop();
}
