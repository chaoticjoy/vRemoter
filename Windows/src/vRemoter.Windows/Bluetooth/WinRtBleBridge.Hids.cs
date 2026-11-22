using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Bluetooth;

public sealed partial class WinRtBleBridge
{
    private static readonly Guid HidsReportCharUuid = Guid.Parse("00002a4d-0000-1000-8000-00805f9b34fb");
    private static readonly Guid ReportReferenceDescUuid = Guid.Parse("00002908-0000-1000-8000-00805f9b34fb");

    private sealed record HidsSubscription(GattCharacteristic Characteristic, byte ReportId);

    private readonly List<HidsSubscription> _hidsSubscriptions = [];
    private GattDeviceService? _hidsService;
    private int _hidsRxLogCount;

    public event Action<byte, byte[]>? HidsReportReceived;

    private async Task TryAttachHidsReportMonitorAsync(BluetoothLEDevice bleDevice)
    {
        try
        {
            var servicesResult = await bleDevice.GetGattServicesForUuidAsync(
                GattServiceUuids.HumanInterfaceDevice,
                BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            {
                LogService.Write($"[{_logTag}] HIDS 0x1812 unavailable: {servicesResult.Status}");
                return;
            }

            _hidsService = servicesResult.Services[0];
            var openStatus = await _hidsService.OpenAsync(GattSharingMode.SharedReadOnly);
            if (openStatus is not (GattOpenStatus.Success or GattOpenStatus.AlreadyOpened))
            {
                LogService.Write($"[{_logTag}] HIDS 0x1812 OpenAsync(SharedReadOnly)={openStatus}");
                return;
            }

            var charsResult = await _hidsService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success)
            {
                LogService.Write($"[{_logTag}] HIDS 0x1812 GetCharacteristics={charsResult.Status}");
                return;
            }

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
                LogService.Write($"[{_logTag}] HIDS 0x2A4D handle=0x{ch.AttributeHandle:X4} reportId=0x{reportId:X2} cccd={cccdStatus}");
            }
        }
        catch (Exception ex)
        {
            LogService.Write($"[{_logTag}] HIDS 0x1812 probe error: {ex.Message}");
        }
    }

    private static async Task<(byte ReportId, byte? ReportType)> TryReadReportReferenceAsync(GattCharacteristic ch)
    {
        try
        {
            var descs = await ch.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
            if (descs.Status != GattCommunicationStatus.Success) return (0x01, null);

            var refDesc = descs.Descriptors.FirstOrDefault(d => d.Uuid == ReportReferenceDescUuid);
            if (refDesc is null) return (0x01, null);

            var read = await refDesc.ReadValueAsync(BluetoothCacheMode.Uncached);
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
            LogService.Write($"[{_logTag}] HIDS rx id=0x{reportId:X2} len={payload.Length} hex={BitConverter.ToString(payload)}");
        }

        HidsReportReceived?.Invoke(reportId, payload);
    }

    private void CleanupHidsLocked()
    {
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

        try { _hidsService?.Dispose(); } catch { }
        _hidsService = null;
    }
}
