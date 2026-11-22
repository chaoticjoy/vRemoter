using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Input;

/// <summary>
/// Reads raw HID input reports from non-exclusive HID Top-Level Collections
/// (Consumer, System, Vendor) via SetupDi + HidD + CreateFileW async streams.
/// </summary>
public sealed class HidCollectionReader : IDisposable
{
    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;

    private sealed class OpenCollection(string path, SafeFileHandle handle, FileStream stream, CancellationTokenSource cts)
    {
        public string Path { get; } = path;
        public SafeFileHandle Handle { get; } = handle;
        public FileStream Stream { get; } = stream;
        public CancellationTokenSource Cts { get; } = cts;
    }

    private readonly ushort _vendorId;
    private readonly ushort _productId;
    private readonly string _logTag;
    private readonly object _sync = new();
    private readonly Dictionary<string, OpenCollection> _openStreams = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _rescanTimer;
    private bool _started;
    private bool _connected;

    public event Action<bool>? ConnectionChanged;
    public event Action<byte, byte[]>? InputReportReceived;

    public HidCollectionReader(ushort vendorId, ushort productId, string logTag)
    {
        _vendorId = vendorId;
        _productId = productId;
        _logTag = logTag;
    }

    public bool IsConnected => _connected;

    public void Start()
    {
        if (_started) return;
        _started = true;
        RefreshDevices();
        _rescanTimer = new Timer(_ => RefreshDevices(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    public void Stop()
    {
        _started = false;
        _rescanTimer?.Dispose();
        _rescanTimer = null;
        CloseAll();
    }

    public void RefreshDevices()
    {
        if (!_started) return;

        var discovered = EnumerateMatchingDevices();
        bool anyPresent = discovered.Count > 0;

        lock (_sync)
        {
            var stale = _openStreams.Keys.Where(k => !discovered.ContainsKey(k)).ToList();
            foreach (string key in stale)
            {
                CloseCollectionLocked(key);
            }

            foreach (var (path, reportLen) in discovered)
            {
                if (_openStreams.ContainsKey(path)) continue;
                TryOpenCollectionLocked(path, reportLen);
            }
        }

        if (_connected != anyPresent)
        {
            _connected = anyPresent;
            ConnectionChanged?.Invoke(anyPresent);
            LogService.Write($"[{_logTag}] HID {(anyPresent ? "connected" : "disconnected")} VID=0x{_vendorId:X4} PID=0x{_productId:X4}");
        }
    }

    private void TryOpenCollectionLocked(string devicePath, int inputReportByteLength)
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
            handle.Dispose();
            return; // Expected for exclusive kbdhid.sys / mouhid.sys TLCs; RawInputMonitor covers those.
        }

        try
        {
            var stream = new FileStream(handle, FileAccess.Read, Math.Max(64, inputReportByteLength), isAsync: true);
            var cts = new CancellationTokenSource();
            var entry = new OpenCollection(devicePath, handle, stream, cts);
            _openStreams[devicePath] = entry;
            _ = Task.Run(() => ReadLoopAsync(entry, Math.Max(64, inputReportByteLength)));
        }
        catch (Exception ex)
        {
            handle.Dispose();
            LogService.Write($"[{_logTag}] open stream warning: {ex.Message}");
        }
    }

    private async Task ReadLoopAsync(OpenCollection entry, int bufferSize)
    {
        byte[] buffer = new byte[bufferSize];
        while (!entry.Cts.IsCancellationRequested && _started)
        {
            try
            {
                int read = await entry.Stream.ReadAsync(buffer, 0, buffer.Length, entry.Cts.Token);
                if (read <= 0) break;

                byte reportId = buffer[0];
                byte[] payload = new byte[read - 1];
                Buffer.BlockCopy(buffer, 1, payload, 0, payload.Length);
                InputReportReceived?.Invoke(reportId, payload);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }
        }

        lock (_sync)
        {
            CloseCollectionLocked(entry.Path);
        }
    }

    private Dictionary<string, int> EnumerateMatchingDevices()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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

                    if (TryInspectAttributes(path, out ushort vid, out ushort pid, out int reportLen) &&
                        vid == _vendorId && pid == _productId)
                    {
                        result[path] = reportLen;
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

    private static bool TryInspectAttributes(string path, out ushort vid, out ushort pid, out int inputReportLength)
    {
        vid = pid = 0;
        inputReportLength = 64;

        using var handle = CreateFileW(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(handle, ref attrs)) return false;

        vid = attrs.VendorID;
        pid = attrs.ProductID;

        if (HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            try
            {
                if (HidP_GetCaps(preparsed, out var caps) >= 0)
                {
                    inputReportLength = Math.Max(8, (int)caps.InputReportByteLength);
                }
            }
            finally
            {
                _ = HidD_FreePreparsedData(preparsed);
            }
        }
        return true;
    }

    private void CloseCollectionLocked(string path)
    {
        if (!_openStreams.Remove(path, out var entry)) return;
        try { entry.Cts.Cancel(); } catch { }
        try { entry.Stream.Dispose(); } catch { }
        try { entry.Handle.Dispose(); } catch { }
        try { entry.Cts.Dispose(); } catch { }
    }

    private void CloseAll()
    {
        lock (_sync)
        {
            foreach (string key in _openStreams.Keys.ToList())
            {
                CloseCollectionLocked(key);
            }
        }
        if (_connected)
        {
            _connected = false;
            ConnectionChanged?.Invoke(false);
        }
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, ref uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}

