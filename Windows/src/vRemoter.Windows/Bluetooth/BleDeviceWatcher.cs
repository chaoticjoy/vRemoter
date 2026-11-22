using System.Globalization;
using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Bluetooth;

public sealed record MatchedBleRemote(string DeviceId, string Name, ulong BluetoothAddress, bool IsConnected);

/// <summary>
/// Watches paired Bluetooth LE AssociationEndpoints so HID-connected remotes
/// (which stop advertising) are still discovered and reconnected reliably on Win10/11.
/// </summary>
public sealed class BleDeviceWatcher : IDisposable
{
    private const string PropDeviceAddress = "System.Devices.Aep.DeviceAddress";
    private const string PropIsConnected = "System.Devices.Aep.IsConnected";

    private readonly string _nameHint;
    private readonly string _savedAddressPath;
    private readonly string _logTag;
    private readonly object _sync = new();

    private DeviceWatcher? _watcher;
    private ulong? _savedAddress;
    private readonly Dictionary<string, MatchedBleRemote> _known = new(StringComparer.Ordinal);
    private bool _started;

    public event Action<MatchedBleRemote>? ConnectedCandidateFound;
    public event Action<MatchedBleRemote>? PairedCandidateFound;
    public event Action<string>? DeviceDisconnected;

    public ulong SavedAddress
    {
        get { lock (_sync) return _savedAddress ?? 0; }
    }

    public BleDeviceWatcher(string nameHint, string savedAddressFilename, string logTag)
    {
        _nameHint = nameHint;
        _savedAddressPath = Path.Combine(AppStorage.Shared.BleCacheDirectory, savedAddressFilename);
        _logTag = logTag;
        LoadSavedAddress();
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }

        if (!ApiInformation.IsTypePresent("Windows.Devices.Bluetooth.BluetoothLEDevice"))
        {
            LogService.Write($"[{_logTag}] WinRT BluetoothLEDevice unavailable on this OS");
            return;
        }

        try
        {
            string aqs = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            string[] props = [PropDeviceAddress, PropIsConnected];
            _watcher = DeviceInformation.CreateWatcher(aqs, props, DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += OnAdded;
            _watcher.Updated += OnUpdated;
            _watcher.Removed += OnRemoved;
            _watcher.EnumerationCompleted += OnEnumerationCompleted;
            _watcher.Start();
            LogService.Write($"[{_logTag}] paired BLE AEP watcher started (hint={_nameHint})");
        }
        catch (Exception ex)
        {
            LogService.Write($"[{_logTag}] failed to start BLE watcher: {ex.Message}");
        }
    }

    public void Stop()
    {
        DeviceWatcher? watcher;
        lock (_sync)
        {
            _started = false;
            watcher = _watcher;
            _watcher = null;
            _known.Clear();
        }
        if (watcher is not null)
        {
            try
            {
                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                {
                    watcher.Stop();
                }
            }
            catch
            {
            }
        }
    }

    public void SaveAddress(ulong address)
    {
        lock (_sync)
        {
            _savedAddress = address;
        }
        try
        {
            AppStorage.EnsureDirectory(Path.GetDirectoryName(_savedAddressPath)!);
            File.WriteAllText(_savedAddressPath, address.ToString("X12", CultureInfo.InvariantCulture));
        }
        catch
        {
        }
    }

    public IReadOnlyList<MatchedBleRemote> GetConnectedCandidates()
    {
        lock (_sync)
        {
            return _known.Values.Where(x => x.IsConnected).ToList();
        }
    }

    public IReadOnlyList<MatchedBleRemote> GetPairedCandidates()
    {
        lock (_sync)
        {
            return _known.Values.ToList();
        }
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        ProcessDevice(info.Id, info.Name, info.Properties);
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        ProcessUpdate(update.Id, update.Properties);
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        bool removed;
        lock (_sync)
        {
            removed = _known.Remove(update.Id);
        }
        if (removed)
        {
            DeviceDisconnected?.Invoke(update.Id);
        }
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        foreach (var candidate in GetPairedCandidates())
        {
            PairedCandidateFound?.Invoke(candidate);
            if (candidate.IsConnected)
            {
                ConnectedCandidateFound?.Invoke(candidate);
            }
        }
    }

    private void ProcessDevice(string id, string name, IReadOnlyDictionary<string, object> props)
    {
        ulong addr = ParseAddress(props);
        bool isConnected = props.TryGetValue(PropIsConnected, out var c) && c is true;
        if (!Matches(name, addr)) return;

        var entry = new MatchedBleRemote(id, name, addr, isConnected);
        lock (_sync)
        {
            _known[id] = entry;
        }
        PairedCandidateFound?.Invoke(entry);
        if (isConnected)
        {
            ConnectedCandidateFound?.Invoke(entry);
        }
    }

    private void ProcessUpdate(string id, IReadOnlyDictionary<string, object> props)
    {
        MatchedBleRemote? updated = null;
        bool becameDisconnected = false;

        lock (_sync)
        {
            if (!_known.TryGetValue(id, out var existing)) return;
            bool isConnected = props.TryGetValue(PropIsConnected, out var c) && c is bool b
                ? b
                : existing.IsConnected;
            ulong addr = ParseAddress(props);
            if (addr == 0) addr = existing.BluetoothAddress;

            updated = existing with { IsConnected = isConnected, BluetoothAddress = addr };
            _known[id] = updated;
            becameDisconnected = existing.IsConnected && !isConnected;
        }

        if (updated is not null && updated.IsConnected)
        {
            ConnectedCandidateFound?.Invoke(updated);
        }
        else if (becameDisconnected)
        {
            DeviceDisconnected?.Invoke(id);
        }
    }

    private bool Matches(string? name, ulong address)
    {
        if (!string.IsNullOrEmpty(name) &&
            name.Contains(_nameHint, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        lock (_sync)
        {
            return _savedAddress is { } saved && saved != 0 && saved == address;
        }
    }

    private static ulong ParseAddress(IReadOnlyDictionary<string, object> props)
    {
        if (!props.TryGetValue(PropDeviceAddress, out var raw) || raw is null) return 0;
        string cleaned = raw.ToString()!.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return ulong.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong val)
            ? val
            : 0;
    }

    private void LoadSavedAddress()
    {
        try
        {
            if (!File.Exists(_savedAddressPath)) return;
            string hex = File.ReadAllText(_savedAddressPath).Trim();
            if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong addr))
            {
                _savedAddress = addr;
            }
        }
        catch
        {
        }
    }

    public void Dispose() => Stop();
}

