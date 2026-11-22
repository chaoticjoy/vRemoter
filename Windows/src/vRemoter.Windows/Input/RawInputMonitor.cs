using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Interop;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Input;

/// <summary>
/// Registers background Raw Input (RIDEV_INPUTSINK) on a message-only window so
/// keyboard/consumer/system events from VID/PID-matched remotes are observed
/// even when kbdhid.sys holds exclusive CreateFile access.
/// </summary>
public sealed class RawInputMonitor : IDisposable
{
    private const int WmInput = 0x00FF;
    private const int WmDeviceChange = 0x0219;
    private const int RimTypeKeyboard = 1;
    private const int RimTypeHid = 2;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidiDeviceInfo = 0x2000000B;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;
    private const ushort RiKeyBreak = 0x0001;
    private static readonly IntPtr HwndMessage = new(-3);

    private static readonly Regex VidRegex = new(@"VID(?:_|&(?:[0-9A-Fa-f]{2})?(?=[0-9A-Fa-f]{4}))([0-9A-Fa-f]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PidRegex = new(@"PID(?:_|&)([0-9A-Fa-f]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly record struct DeviceIdentity(ushort Vid, ushort Pid, ushort UsagePage, ushort Usage);

    private readonly Dictionary<IntPtr, DeviceIdentity> _deviceCache = [];
    private HwndSource? _hwndSource;
    private bool _started;

    public event Action<ushort, ushort, ushort, bool, ushort>? KeyboardReportReceived;
    public event Action<ushort, ushort, ushort, ushort, byte[]>? HidReportReceived;
    public event Action? DeviceTopologyChanged;

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
            new() { UsUsagePage = 0x0C, UsUsage = 0x01, DwFlags = RidevInputSink, HwndTarget = hwnd },
            new() { UsUsagePage = 0x01, UsUsage = 0x80, DwFlags = RidevInputSink, HwndTarget = hwnd }
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
        _deviceCache.Clear();
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
        else if (msg == WmDeviceChange)
        {
            _deviceCache.Clear();
            DeviceTopologyChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    private void ProcessRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(hRawInput, RidInput, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
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
            if (!TryGetIdentity(header.HDevice, out var identity))
            {
                return;
            }

            if (header.DwType == RimTypeKeyboard)
            {
                var kb = Marshal.PtrToStructure<RAWKEYBOARD>(IntPtr.Add(buffer, (int)headerSize));
                bool isDown = (kb.Flags & RiKeyBreak) == 0;
                KeyboardReportReceived?.Invoke(identity.Vid, identity.Pid, kb.VKey, isDown, kb.MakeCode);
            }
            else if (header.DwType == RimTypeHid)
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
                    HidReportReceived?.Invoke(identity.Vid, identity.Pid, identity.UsagePage, identity.Usage, report);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private bool TryGetIdentity(IntPtr hDevice, out DeviceIdentity identity)
    {
        if (hDevice == IntPtr.Zero)
        {
            identity = default;
            return false;
        }
        if (_deviceCache.TryGetValue(hDevice, out identity))
        {
            return identity.Vid != 0;
        }

        uint size = (uint)Marshal.SizeOf<RID_DEVICE_INFO>();
        var info = new RID_DEVICE_INFO { CbSize = size };
        if (GetRawInputDeviceInfoW(hDevice, RidiDeviceInfo, ref info, ref size) != unchecked((uint)-1) &&
            info.DwType == RimTypeHid &&
            info.Hid.DwVendorId != 0)
        {
            identity = new DeviceIdentity(
                (ushort)info.Hid.DwVendorId,
                (ushort)info.Hid.DwProductId,
                info.Hid.UsUsagePage,
                info.Hid.UsUsage);
            _deviceCache[hDevice] = identity;
            return true;
        }

        // For RIM_TYPEKEYBOARD (e.g. BLE HOGP keyboard collection), Windows does not
        // populate RID_DEVICE_INFO_HID; resolve VID/PID from RIDI_DEVICENAME instead.
        uint charCount = 0;
        if (GetRawInputDeviceInfoCharW(hDevice, RidiDeviceName, null, ref charCount) != unchecked((uint)-1) &&
            charCount > 0)
        {
            var sb = new StringBuilder((int)charCount);
            if (GetRawInputDeviceInfoCharW(hDevice, RidiDeviceName, sb, ref charCount) != unchecked((uint)-1))
            {
                string devName = sb.ToString();
                var vm = VidRegex.Match(devName);
                var pm = PidRegex.Match(devName);
                if (vm.Success && pm.Success &&
                    ushort.TryParse(vm.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort vid) &&
                    ushort.TryParse(pm.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort pid))
                {
                    identity = new DeviceIdentity(vid, pid, 0x0001, 0x0006);
                    _deviceCache[hDevice] = identity;
                    return true;
                }
            }
        }

        _deviceCache[hDevice] = default;
        identity = default;
        return false;
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
}
