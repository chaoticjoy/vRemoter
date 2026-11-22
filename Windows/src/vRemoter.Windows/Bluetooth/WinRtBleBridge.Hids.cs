using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using vRemoter.Core.Input;
using vRemoter.Windows.Input;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Bluetooth;

public sealed partial class WinRtBleBridge
{
    private static readonly Guid HidsReportMapCharUuid = Guid.Parse("00002a4b-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HidsReportCharUuid = Guid.Parse("00002a4d-0000-1000-8000-00805f9b34fb");
    private static readonly Guid ReportReferenceDescUuid = Guid.Parse("00002908-0000-1000-8000-00805f9b34fb");

    private sealed record HidsSubscription(GattCharacteristic Characteristic, byte ReportId);

    private readonly List<HidsSubscription> _hidsSubscriptions = [];
    private Win32BleGattHidsReader? _win32HidsReader;
    private GattDeviceService? _hidsService;
    private HidReportMapParser? _winRtReportMapParser;
    private int _hidsRxLogCount;

    public event Action<bool>? HidsConnectionChanged;
    public event Action<byte, byte[], ParsedHidUsage[]>? HidsReportReceived;

    private async Task TryAttachHidsReportMonitorAsync(BluetoothLEDevice bleDevice)
    {
        try
        {
            var pairing = bleDevice.DeviceInformation.Pairing;
            var allSvcs = await bleDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            string svcList = allSvcs.Status == GattCommunicationStatus.Success
                ? string.Join(",", allSvcs.Services.Select(s => s.Uuid.ToString()[..8]))
                : allSvcs.Status.ToString();
            LogService.Write($"[{_logTag}] BLE pairing paired={pairing.IsPaired} prot={pairing.ProtectionLevel} svcs=[{svcList}]");
        }
        catch (Exception ex)
        {
            LogService.Write($"[{_logTag}] BLE service inventory warning: {ex.Message}");
        }

        // 1. Ensure hidbthle.inf (mshidumdf) is bound on 0x1812 so Windows Bluetooth + WUDFHost.exe work normally.
        _win32HidsReader ??= CreateWin32HidsReader();
        bool win32Subscribed = await Task.Run(() => _win32HidsReader.TryAttach());
        if (win32Subscribed)
        {
            HidsConnectionChanged?.Invoke(true);
        }

        // 2. Also attempt WinRT GattDeviceService subscription (without early return on OpenAsync)
        // in case cached/uncached WinRT characteristic handles are accessible after bthleenum takeover.
        try
        {
            var servicesResult = await bleDevice.GetGattServicesForUuidAsync(
                GattServiceUuids.HumanInterfaceDevice,
                BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            {
                servicesResult = await bleDevice.GetGattServicesForUuidAsync(
                    GattServiceUuids.HumanInterfaceDevice,
                    BluetoothCacheMode.Cached);
            }
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            {
                LogService.Write($"[{_logTag}] WinRT HIDS 0x1812 unavailable: {servicesResult.Status}");
                return;
            }

            _hidsService = servicesResult.Services[0];
            try
            {
                var openStatus = await _hidsService.OpenAsync(GattSharingMode.SharedReadOnly);
                LogService.Write($"[{_logTag}] WinRT HIDS 0x1812 OpenAsync(SharedReadOnly)={openStatus}");
            }
            catch (Exception openEx)
            {
                LogService.Write($"[{_logTag}] WinRT HIDS 0x1812 OpenAsync warning: {openEx.Message}");
            }

            var charsResult = await _hidsService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
            {
                charsResult = await _hidsService.GetCharacteristicsAsync(BluetoothCacheMode.Cached);
            }
            if (charsResult.Status != GattCommunicationStatus.Success)
            {
                LogService.Write($"[{_logTag}] WinRT HIDS 0x1812 GetCharacteristics={charsResult.Status}");
                return;
            }

            foreach (var ch in charsResult.Characteristics)
            {
                if (ch.Uuid == HidsReportMapCharUuid)
                {
                    await TryReadWinRtReportMapAsync(ch);
                }
            }

            int subscribedCount = 0;
            foreach (var ch in charsResult.Characteristics)
            {
                if (ch.Uuid != HidsReportCharUuid) continue;
                bool canNotify = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify);
                bool canIndicate = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate);
                if (!canNotify && !canIndicate) continue;

                var (reportId, reportType) = await TryReadReportReferenceAsync(ch);
                if (reportType is not null && reportType != 1)
                {
                    continue; // Only Input reports (type == 1)
                }

                ch.ValueChanged += OnHidsCharacteristicValueChanged;
                lock (_hidsSubscriptions)
                {
                    _hidsSubscriptions.Add(new HidsSubscription(ch, reportId));
                }

                var mode = canNotify
                    ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                    : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
                var cccdStatus = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(mode);
                LogService.Write($"[{_logTag}] WinRT HIDS 0x2A4D handle=0x{ch.AttributeHandle:X4} reportId=0x{reportId:X2} cccd={cccdStatus}");
                if (cccdStatus == GattCommunicationStatus.Success)
                {
                    subscribedCount++;
                }
            }

            if (subscribedCount > 0)
            {
                HidsConnectionChanged?.Invoke(true);
            }
        }
        catch (Exception ex)
        {
            LogService.Write($"[{_logTag}] WinRT HIDS 0x1812 probe error: {ex.Message}");
        }
    }

    private Win32BleGattHidsReader CreateWin32HidsReader()
    {
        var reader = new Win32BleGattHidsReader(
            ChromecastHidBridge.VendorId,
            ChromecastHidBridge.ProductId,
            "chromecast-ble.txt",
            _logTag);
        reader.SubscriptionChanged += connected => HidsConnectionChanged?.Invoke(connected);
        reader.ReportReceived += (reportId, payload, parsed) => HidsReportReceived?.Invoke(reportId, payload, parsed);
        return reader;
    }

    private async Task TryReadWinRtReportMapAsync(GattCharacteristic reportMapChar)
    {
        try
        {
            var read = await reportMapChar.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status != GattCommunicationStatus.Success || read.Value is null)
            {
                read = await reportMapChar.ReadValueAsync(BluetoothCacheMode.Cached);
            }
            if (read.Status == GattCommunicationStatus.Success && read.Value is not null)
            {
                byte[] mapBytes = read.Value.ToArray();
                if (mapBytes.Length > 0)
                {
                    _winRtReportMapParser = new HidReportMapParser(mapBytes);
                    LogService.Write($"[{_logTag}] WinRT 0x2A4B ReportMap len={mapBytes.Length}");
                }
            }
        }
        catch
        {
        }
    }

    private static async Task<(byte ReportId, byte? ReportType)> TryReadReportReferenceAsync(GattCharacteristic ch)
    {
        try
        {
            var descs = await ch.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
            if (descs.Status != GattCommunicationStatus.Success)
            {
                descs = await ch.GetDescriptorsAsync(BluetoothCacheMode.Cached);
            }
            if (descs.Status != GattCommunicationStatus.Success) return (0x01, null);

            var refDesc = descs.Descriptors.FirstOrDefault(d => d.Uuid == ReportReferenceDescUuid);
            if (refDesc is null) return (0x01, null);

            var read = await refDesc.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status != GattCommunicationStatus.Success || read.Value is null)
            {
                read = await refDesc.ReadValueAsync(BluetoothCacheMode.Cached);
            }
            if (read.Status != GattCommunicationStatus.Success || read.Value is null) return (0x01, null);

            byte[] bytes = read.Value.ToArray();
            return bytes.Length >= 2 ? (bytes[0], bytes[1]) : (0x01, null);
        }
        catch
        {
            return (0x01, null);
        }
    }

    private void OnHidsCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        byte[] payload = args.CharacteristicValue.ToArray();
        byte reportId;
        lock (_hidsSubscriptions)
        {
            reportId = _hidsSubscriptions.FirstOrDefault(s => s.Characteristic.AttributeHandle == sender.AttributeHandle)?.ReportId ?? 0x01;
        }

        if (_hidsRxLogCount < 12)
        {
            _hidsRxLogCount++;
            LogService.Write($"[{_logTag}] WinRT HIDS rx id=0x{reportId:X2} len={payload.Length} hex={BitConverter.ToString(payload)}");
        }

        var parser = _winRtReportMapParser ?? _win32HidsReader?.ReportMapParser;
        var parsed = parser is not null ? parser.DecodeActiveUsages(reportId, payload) : [];
        HidsReportReceived?.Invoke(reportId, payload, parsed);
    }

    private void CleanupHidsLocked()
    {
        _win32HidsReader?.Detach();

        List<HidsSubscription> snapshot;
        lock (_hidsSubscriptions)
        {
            snapshot = [.. _hidsSubscriptions];
            _hidsSubscriptions.Clear();
        }
        foreach (var sub in snapshot)
        {
            try { sub.Characteristic.ValueChanged -= OnHidsCharacteristicValueChanged; } catch { }
        }
        _hidsRxLogCount = 0;
        _winRtReportMapParser = null;

        try { _hidsService?.Dispose(); } catch { }
        _hidsService = null;
    }
}
