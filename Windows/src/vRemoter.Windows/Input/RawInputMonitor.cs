using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using vRemoter.Core.Input;
using vRemoter.Windows.SystemSupport;
using static vRemoter.Windows.Input.HidNativeInterop;

namespace vRemoter.Windows.Input;

/// <summary>
/// Registers background Raw Input (RIDEV_INPUTSINK | RIDEV_DEVNOTIFY) on a message-only window.
/// Ported with 10RemoteHub's windows_input.py + windows_hid.py + windows_probe_metadata.py enhancements:
/// - Accurately resolves Bluetooth LE VID&amp;0002xxxx_PID&amp;xxxx and cfgmgr32 parent devnode VID/PID
/// - Parses RIDI_PREPARSEDDATA (0x20000005) via HidP_GetUsagesEx / HidP_GetUsages
/// - Feeds KeyCorrelationEngine for deterministic per-device WH_KEYBOARD_LL suppression
/// </summary>
public sealed class RawInputMonitor : IDisposable
{
    private const int WmInputDeviceChange = 0x00FE;
    private const int WmInput = 0x00FF;
    private const int WmDeviceChange = 0x0219;
    private const int RimTypeKeyboard = 1;
    private const int RimTypeHid = 2;
    private const uint RidInput = 0x10000003;
    private const uint RidiPreparsedData = 0x20000005;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidiDeviceInfo = 0x2000000B;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;
    private const ushort RiKeyBreak = 0x0001;
    private static readonly IntPtr HwndMessage = new(-3);

    private sealed class CachedDevice(
        ushort vid,
        ushort pid,
        ushort usagePage,
        ushort usage,
        int inputReportLength,
        IntPtr preparsedData) : IDisposable
    {
        public ushort Vid { get; } = vid;
        public ushort Pid { get; } = pid;
        public ushort UsagePage { get; } = usagePage;
        public ushort Usage { get; } = usage;
        public int InputReportLength { get; } = inputReportLength;
        public IntPtr PreparsedData { get; private set; } = preparsedData;

        public void Dispose()
        {
            if (PreparsedData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(PreparsedData);
                PreparsedData = IntPtr.Zero;
            }
        }
    }

    private readonly Dictionary<IntPtr, CachedDevice> _deviceCache = [];
    private readonly KeyCorrelationEngine? _correlation;
    private HwndSource? _hwndSource;
    private bool _started;
    private int _remoteRxLogCount;

    public event Action<ushort, ushort, ushort, bool, ushort>? KeyboardReportReceived;
    public event Action<ushort, ushort, ushort, ushort, byte[], HidUsagePair[]>? HidReportReceived;
    public event Action? DeviceTopologyChanged;

    public RawInputMonitor(KeyCorrelationEngine? correlation = null)
    {
        _correlation = correlation;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        var parameters = new HwndSourceParameters("vRemoterRawInputSink")
        {
            ParentWindow = HwndMessage,
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        var hwnd = _hwndSource.Handle;
        RAWINPUTDEVICE[] devices =
        [
            new() { UsUsagePage = 0x01, UsUsage = 0x06, DwFlags = RidevInputSink | RidevDevNotify, HwndTarget = hwnd },
            new() { UsUsagePage = 0x0C, UsUsage = 0x01, DwFlags = RidevInputSink | RidevDevNotify, HwndTarget = hwnd },
            new() { UsUsagePage = 0x01, UsUsage = 0x80, DwFlags = RidevInputSink | RidevDevNotify, HwndTarget = hwnd }
        ];

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            int err = Marshal.GetLastWin32Error();
            LogService.Write($"[RAWINPUT] RegisterRawInputDevices failed Win32={err}");
        }
        else
        {
            LogService.Write("[RAWINPUT] background monitor started (Keyboard + Consumer + System)");
        }
    }

    public void Stop()
    {
        _started = false;
        ClearCache();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmInput)
        {
            ProcessRawInput(lParam);
        }
        else if (msg is WmInputDeviceChange or WmDeviceChange)
        {
            ClearCache();
            DeviceTopologyChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    private void ProcessRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(hRawInput, RidInput, IntPtr.Zero, ref size, headerSize) != 0 ||
            size == 0 || size > 4096)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RidInput, buffer, ref size, headerSize) != size)
            {
                return;
            }

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            var device = GetOrResolveDevice(header.HDevice);
            bool isTargetRemote = IsKnownTargetRemote(device.Vid, device.Pid);

            if (header.DwType == RimTypeKeyboard)
            {
                var kb = Marshal.PtrToStructure<RAWKEYBOARD>(IntPtr.Add(buffer, (int)headerSize));
                if (kb.VKey == 0xFF) return;
                bool isDown = (kb.Flags & RiKeyBreak) == 0;
                _correlation?.RecordRawEvent(kb.VKey, isDown, isTargetRemote);

                if (device.Vid == 0) return;
                if (isTargetRemote && _remoteRxLogCount < 10)
                {
                    _remoteRxLogCount++;
                    LogService.Write($"[RAWINPUT] kb VID=0x{device.Vid:X4} PID=0x{device.Pid:X4} vk=0x{kb.VKey:X2} scan=0x{kb.MakeCode:X2} down={isDown}");
                }
                KeyboardReportReceived?.Invoke(device.Vid, device.Pid, kb.VKey, isDown, kb.MakeCode);
            }
            else if (header.DwType == RimTypeHid && device.Vid != 0)
            {
                var hid = Marshal.PtrToStructure<RAWHID>(IntPtr.Add(buffer, (int)headerSize));
                int itemSize = (int)hid.DwSizeHid;
                int count = (int)hid.DwCount;
                int totalLen = itemSize * count;
                if (itemSize <= 0 || count <= 0 || headerSize + 8 + totalLen > size) return;

                IntPtr dataPtr = IntPtr.Add(buffer, (int)headerSize + 8);
                for (int i = 0; i < count; i++)
                {
                    byte[] report = new byte[itemSize];
                    Marshal.Copy(IntPtr.Add(dataPtr, i * itemSize), report, 0, itemSize);
                    var parsed = ParseHidUsages(device.PreparsedData, device.InputReportLength, report);

                    if (isTargetRemote && _remoteRxLogCount < 10)
                    {
                        _remoteRxLogCount++;
                        LogService.Write($"[RAWINPUT] hid VID=0x{device.Vid:X4} PID=0x{device.Pid:X4} UP=0x{device.UsagePage:X4} U=0x{device.Usage:X4} hex={BitConverter.ToString(report)} usages={parsed.Length}");
                    }
                    HidReportReceived?.Invoke(device.Vid, device.Pid, device.UsagePage, device.Usage, report, parsed);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private CachedDevice GetOrResolveDevice(IntPtr hDevice)
    {
        if (hDevice == IntPtr.Zero)
        {
            return new CachedDevice(0, 0, 0, 0, 0, IntPtr.Zero);
        }
        if (_deviceCache.TryGetValue(hDevice, out var cached))
        {
            return cached;
        }

        ushort vid = 0;
        ushort pid = 0;
        ushort usagePage = 0;
        ushort usage = 0;

        uint infoSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>();
        var info = new RID_DEVICE_INFO { CbSize = infoSize };
        if (GetRawInputDeviceInfoW(hDevice, RidiDeviceInfo, ref info, ref infoSize) != unchecked((uint)-1))
        {
            if (info.DwType == RimTypeHid)
            {
                vid = (ushort)info.Hid.DwVendorId;
                pid = (ushort)info.Hid.DwProductId;
                usagePage = info.Hid.UsUsagePage;
                usage = info.Hid.UsUsage;
            }
            else if (info.DwType == RimTypeKeyboard)
            {
                usagePage = 0x0001;
                usage = 0x0006;
            }
        }

        string devPath = GetDevicePath(hDevice);
        if ((vid == 0 || pid == 0) && !string.IsNullOrEmpty(devPath))
        {
            _ = CfgMgrDeviceResolver.TryResolveInterfaceVidPid(devPath, out vid, out pid);
        }

        IntPtr preparsed = IntPtr.Zero;
        int inputReportLen = 0;
        if (IsKnownTargetRemote(vid, pid))
        {
            preparsed = TryReadPreparsedData(hDevice, ref usagePage, ref usage, out inputReportLen);
            LogService.Write($"[RAWINPUT] matched device VID=0x{vid:X4} PID=0x{pid:X4} UP=0x{usagePage:X4} U=0x{usage:X4} reportLen={inputReportLen}");
        }

        var entry = new CachedDevice(vid, pid, usagePage, usage, inputReportLen, preparsed);
        _deviceCache[hDevice] = entry;
        return entry;
    }

    private static string GetDevicePath(IntPtr hDevice)
    {
        uint charCount = 0;
        if (GetRawInputDeviceInfoCharW(hDevice, RidiDeviceName, null, ref charCount) == unchecked((uint)-1) ||
            charCount == 0 || charCount > 32_768)
        {
            return string.Empty;
        }

        var sb = new StringBuilder((int)charCount + 2);
        return GetRawInputDeviceInfoCharW(hDevice, RidiDeviceName, sb, ref charCount) != unchecked((uint)-1)
            ? sb.ToString()
            : string.Empty;
    }

    private static IntPtr TryReadPreparsedData(
        IntPtr hDevice,
        ref ushort usagePage,
        ref ushort usage,
        out int inputReportLen)
    {
        inputReportLen = 0;
        uint byteCount = 0;
        if (GetRawInputDeviceInfoPtrW(hDevice, RidiPreparsedData, IntPtr.Zero, ref byteCount) == unchecked((uint)-1) ||
            byteCount == 0 || byteCount > 65_536)
        {
            return IntPtr.Zero;
        }

        IntPtr buf = Marshal.AllocHGlobal((int)byteCount);
        if (GetRawInputDeviceInfoPtrW(hDevice, RidiPreparsedData, buf, ref byteCount) == unchecked((uint)-1))
        {
            Marshal.FreeHGlobal(buf);
            return IntPtr.Zero;
        }

        if (HidP_GetCaps(buf, out var caps) >= 0)
        {
            if (usagePage == 0) usagePage = caps.UsagePage;
            if (usage == 0) usage = caps.Usage;
            inputReportLen = caps.InputReportByteLength;
        }
        return buf;
    }

    private static bool IsKnownTargetRemote(ushort vid, ushort pid) =>
        (vid == ChromecastHidBridge.VendorId && pid == ChromecastHidBridge.ProductId) ||
        (vid == X6HidBridge.VendorId && pid == X6HidBridge.ProductId);

    private void ClearCache()
    {
        foreach (var entry in _deviceCache.Values)
        {
            entry.Dispose();
        }
        _deviceCache.Clear();
        _correlation?.Clear();
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsUsagePage;
        public ushort UsUsage;
        public uint DwFlags;
        public IntPtr HwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint DwType;
        public uint DwSize;
        public IntPtr HDevice;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWHID
    {
        public uint DwSizeHid;
        public uint DwCount;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct RID_DEVICE_INFO
    {
        [FieldOffset(0)] public uint CbSize;
        [FieldOffset(4)] public uint DwType;
        [FieldOffset(8)] public RID_DEVICE_INFO_HID Hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO_HID
    {
        public uint DwVendorId;
        public uint DwProductId;
        public uint DwVersionNumber;
        public ushort UsUsagePage;
        public ushort UsUsage;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, ref RID_DEVICE_INFO pData, ref uint pcbSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoCharW(IntPtr hDevice, uint uiCommand, StringBuilder? pData, ref uint pcbSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoPtrW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);
}
