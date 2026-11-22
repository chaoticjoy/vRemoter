using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using vRemoter.Core.Input;
using vRemoter.Windows.Input;
using vRemoter.Windows.SystemSupport;
using static vRemoter.Windows.Bluetooth.Win32BleGattInterop;
using static vRemoter.Windows.Input.HidNativeInterop;

namespace vRemoter.Windows.Bluetooth;

/// <summary>
/// Direct Win32 BluetoothAPIs.dll reader for BLE HOGP Service 0x1812 (HIDS).
/// Opens the BTHLEDevice 0x1812 GATT service interface via CreateFileW, reads
/// the 0x2A4B Report Map, subscribes 0x2902 CCCD on 0x2A4D Input Reports, and
/// registers a native CharacteristicValueChangedEvent callback.
/// If Windows' hidbthle.inf (mshidumdf) blocks 0x1812 with AccessDenied, automatically
/// switches that remote's 0x1812 devnode to bthleenum.inf ("Bluetooth LE Generic Attribute Service").
/// </summary>
public sealed class Win32BleGattHidsReader : IDisposable
{
    private readonly ushort _vendorId;
    private readonly ushort _productId;
    private readonly string _savedAddressPath;
    private readonly string _logTag;
    private readonly object _sync = new();
    private readonly PfnBluetoothGattEventCallback _callbackDelegate;
    private readonly Dictionary<ushort, byte> _handleToReportId = [];

    private SafeFileHandle? _serviceHandle;
    private IntPtr _eventHandle;
    private HidReportMapParser? _reportMapParser;
    private bool _attemptedDriverTakeover;
    private int _rxLogCount;

    public bool IsSubscribed
    {
        get
        {
            lock (_sync) { return _eventHandle != IntPtr.Zero; }
        }
    }

    public HidReportMapParser? ReportMapParser
    {
        get
        {
            lock (_sync) { return _reportMapParser; }
        }
    }

    public event Action<bool>? SubscriptionChanged;
    public event Action<byte, byte[], ParsedHidUsage[]>? ReportReceived;

    public Win32BleGattHidsReader(
        ushort vendorId,
        ushort productId,
        string savedAddressFilename,
        string logTag)
    {
        _vendorId = vendorId;
        _productId = productId;
        _savedAddressPath = Path.Combine(AppStorage.Shared.BleCacheDirectory, savedAddressFilename);
        _logTag = logTag;
        _callbackDelegate = OnGattValueChanged;
    }

    public bool TryAttach()
    {
        lock (_sync)
        {
            if (_eventHandle != IntPtr.Zero && _serviceHandle is { IsInvalid: false, IsClosed: false })
            {
                return true;
            }
        }

        ulong savedMac = LoadSavedAddress();
        if (TryOpenAndSubscribe(savedMac))
        {
            return true;
        }

        if (!_attemptedDriverTakeover)
        {
            _attemptedDriverTakeover = true;
            if (BleHidsDriverTakeover.EnsureGenericGattOnHids(_vendorId, _productId, savedMac, _logTag))
            {
                Thread.Sleep(350);
                if (TryOpenAndSubscribe(savedMac))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Detach()
    {
        bool wasSubscribed;
        lock (_sync)
        {
            wasSubscribed = _eventHandle != IntPtr.Zero;
            CleanupLocked();
        }
        if (wasSubscribed)
        {
            SubscriptionChanged?.Invoke(false);
        }
    }

    private bool TryOpenAndSubscribe(ulong savedMac)
    {
        var paths = EnumerateHidsServicePaths(savedMac);
        if (paths.Count == 0)
        {
            return false;
        }

        foreach (string path in paths)
        {
            var handle = CreateFileW(path, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = CreateFileW(path, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            }
            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                handle.Dispose();
                LogService.Write($"[{_logTag}] Win32 CreateFile(0x1812) failed Win32={err}");
                continue;
            }

            if (TrySubscribeOnHandle(handle))
            {
                SubscriptionChanged?.Invoke(true);
                return true;
            }
            handle.Dispose();
        }
        return false;
    }

    private bool TrySubscribeOnHandle(SafeFileHandle handle)
    {
        var chars = QueryHidsCharacteristics(handle, out int hr);
        if (chars.Length == 0)
        {
            LogService.Write($"[{_logTag}] Win32 BluetoothGATTGetCharacteristics count=0 hr=0x{hr:X8}");
            return false;
        }
        ushort actual = (ushort)chars.Length;

        HidReportMapParser? parser = null;
        var notifyChars = new List<BTH_LE_GATT_CHARACTERISTIC>();
        var reportIdMap = new Dictionary<ushort, byte>();

        for (int i = 0; i < actual; i++)
        {
            var ch = chars[i];
            if (ch.CharacteristicUuid.MatchesShort(UuidReportMap))
            {
                byte[]? mapBytes = TryReadCharacteristicBytes(handle, ref ch);
                if (mapBytes is { Length: > 0 })
                {
                    parser = new HidReportMapParser(mapBytes);
                    LogService.Write($"[{_logTag}] Win32 0x2A4B ReportMap len={mapBytes.Length} hex={BitConverter.ToString(mapBytes)}");
                }
            }
            else if (ch.CharacteristicUuid.MatchesShort(UuidHidReport) ||
                     ch.CharacteristicUuid.MatchesShort(UuidBootKeyboardInput))
            {
                if (ch.IsNotifiable == 0 && ch.IsIndicatable == 0) continue;

                var (reportId, reportType) = InspectAndEnableCharacteristicDescriptors(handle, ref ch);
                if (reportType is not null && reportType != 1)
                {
                    continue; // Only Input reports
                }

                notifyChars.Add(ch);
                reportIdMap[ch.CharacteristicValueHandle] = reportId;
                reportIdMap[ch.AttributeHandle] = reportId;
            }
        }

        if (notifyChars.Count == 0)
        {
            LogService.Write($"[{_logTag}] Win32 0x1812 had no notifiable Input Report characteristics");
            return false;
        }

        int charStructSize = Marshal.SizeOf<BTH_LE_GATT_CHARACTERISTIC>();
        int regSize = 4 + (notifyChars.Count * charStructSize);
        IntPtr regPtr = Marshal.AllocHGlobal(regSize);
        try
        {
            Marshal.WriteInt16(regPtr, 0, (short)notifyChars.Count);
            Marshal.WriteInt16(regPtr, 2, 0);
            for (int i = 0; i < notifyChars.Count; i++)
            {
                Marshal.StructureToPtr(notifyChars[i], IntPtr.Add(regPtr, 4 + (i * charStructSize)), fDeleteOld: false);
            }

            hr = BluetoothGATTRegisterEvent(
                handle,
                CharacteristicValueChangedEvent,
                regPtr,
                _callbackDelegate,
                IntPtr.Zero,
                out IntPtr evHandle,
                BluetoothGattFlagNone);

            if (hr != 0 || evHandle == IntPtr.Zero)
            {
                LogService.Write($"[{_logTag}] Win32 BluetoothGATTRegisterEvent failed hr=0x{hr:X8}");
                return false;
            }

            lock (_sync)
            {
                CleanupLocked();
                _serviceHandle = handle;
                _eventHandle = evHandle;
                _reportMapParser = parser;
                foreach (var kv in reportIdMap)
                {
                    _handleToReportId[kv.Key] = kv.Value;
                }
            }

            LogService.Write($"[{_logTag}] Win32 0x1812 GATT HIDS subscribed (reports={notifyChars.Count})");
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(regPtr);
        }
    }

    private (byte ReportId, byte? ReportType) InspectAndEnableCharacteristicDescriptors(
        SafeFileHandle handle,
        ref BTH_LE_GATT_CHARACTERISTIC ch)
    {
        byte reportId = 0x01;
        byte? reportType = null;

        _ = BluetoothGATTGetDescriptors(handle, ref ch, 0, null, out ushort descCount, BluetoothGattFlagNone);
        if (descCount == 0) return (reportId, reportType);

        var descs = new BTH_LE_GATT_DESCRIPTOR[descCount];
        if (BluetoothGATTGetDescriptors(handle, ref ch, descCount, descs, out ushort actual, BluetoothGattFlagNone) != 0)
        {
            return (reportId, reportType);
        }

        for (int i = 0; i < actual; i++)
        {
            var desc = descs[i];
            if (desc.DescriptorUuid.MatchesShort(UuidReportReference))
            {
                byte[]? refBytes = TryReadDescriptorBytes(handle, ref desc);
                if (refBytes is { Length: >= 2 })
                {
                    reportId = refBytes[0];
                    reportType = refBytes[1];
                }
            }
            else if (desc.DescriptorType == DescriptorTypeClientCharacteristicConfiguration ||
                     desc.DescriptorUuid.MatchesShort(UuidCccd))
            {
                if (reportType is not null && reportType != 1) continue;
                var cccd = new BTH_LE_GATT_DESCRIPTOR_VALUE
                {
                    DescriptorType = DescriptorTypeClientCharacteristicConfiguration,
                    DescriptorUuid = desc.DescriptorUuid,
                    IsSubscribeToNotification = (byte)(ch.IsNotifiable != 0 ? 1 : 0),
                    IsSubscribeToIndication = (byte)(ch.IsNotifiable == 0 && ch.IsIndicatable != 0 ? 1 : 0)
                };
                int hr = BluetoothGATTSetDescriptorValue(handle, ref desc, ref cccd, BluetoothGattFlagNone);
                if (hr != 0)
                {
                    hr = BluetoothGATTSetDescriptorValue(handle, ref desc, ref cccd, BluetoothGattFlagConnectionEncrypted);
                }
                LogService.Write($"[{_logTag}] Win32 0x2A4D valHandle=0x{ch.CharacteristicValueHandle:X4} reportId=0x{reportId:X2} CCCD hr=0x{hr:X8}");
            }
        }

        return (reportId, reportType);
    }

    private void OnGattValueChanged(int eventType, IntPtr eventOutParameter, IntPtr context)
    {
        if (eventType != CharacteristicValueChangedEvent || eventOutParameter == IntPtr.Zero) return;
        try
        {
            var ev = Marshal.PtrToStructure<BLUETOOTH_GATT_VALUE_CHANGED_EVENT>(eventOutParameter);
            if (ev.CharacteristicValue == IntPtr.Zero) return;

            int dataSize = Marshal.ReadInt32(ev.CharacteristicValue, 0);
            if (dataSize <= 0 || dataSize > 512) return;

            byte[] payload = new byte[dataSize];
            Marshal.Copy(IntPtr.Add(ev.CharacteristicValue, 4), payload, 0, dataSize);

            byte reportId;
            HidReportMapParser? parser;
            lock (_sync)
            {
                reportId = _handleToReportId.GetValueOrDefault(ev.ChangedAttributeHandle, (byte)0x01);
                parser = _reportMapParser;
            }

            var parsed = parser is not null ? parser.DecodeActiveUsages(reportId, payload) : [];
            if (_rxLogCount < 16)
            {
                _rxLogCount++;
                LogService.Write($"[{_logTag}] Win32 HIDS rx handle=0x{ev.ChangedAttributeHandle:X4} id=0x{reportId:X2} hex={BitConverter.ToString(payload)}");
            }
            ReportReceived?.Invoke(reportId, payload, parsed);
        }
        catch (Exception ex)
        {
            LogService.Write($"[{_logTag}] Win32 HIDS callback error: {ex.Message}");
        }
    }

    private List<string> EnumerateHidsServicePaths(ulong savedMac)
    {
        var paths = new List<string>();
        Guid[] interfaceGuids = [HidsServiceGuid, GattServiceInterfaceGuid];

        foreach (Guid guid in interfaceGuids)
        {
            Guid classGuid = guid;
            IntPtr devInfo = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (devInfo == new IntPtr(-1)) continue;

            try
            {
                var ifaceData = new SP_DEVICE_INTERFACE_DATA { CbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (uint idx = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref classGuid, idx, ref ifaceData); idx++)
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
                        if (string.IsNullOrEmpty(path) || !path.Contains("00001812", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        bool matchesVidPid = CfgMgrDeviceResolver.TryResolveInterfaceVidPid(path, out ushort vid, out ushort pid) &&
                                             vid == _vendorId && pid == _productId;
                        bool matchesMac = savedMac != 0 &&
                                          HidDevicePathParser.TryParseBluetoothAddress(path, out ulong mac) &&
                                          mac == savedMac;

                        if ((matchesVidPid || matchesMac) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                        {
                            paths.Add(path);
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
        }
        return paths;
    }

    private ulong LoadSavedAddress()
    {
        try
        {
            if (!File.Exists(_savedAddressPath)) return 0;
            string hex = File.ReadAllText(_savedAddressPath).Trim();
            return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong addr) ? addr : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void CleanupLocked()
    {
        if (_eventHandle != IntPtr.Zero)
        {
            try { _ = BluetoothGATTUnregisterEvent(_eventHandle, BluetoothGattFlagNone); } catch { }
            _eventHandle = IntPtr.Zero;
        }
        if (_serviceHandle is not null)
        {
            try { _serviceHandle.Dispose(); } catch { }
            _serviceHandle = null;
        }
        _handleToReportId.Clear();
        _reportMapParser = null;
        _rxLogCount = 0;
    }

    public void Dispose() => Detach();
}
