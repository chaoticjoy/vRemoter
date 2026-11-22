using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation.Metadata;
using vRemoter.Core.Protocol;
using vRemoter.Core.Session;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Bluetooth;

/// <summary>
/// WinRT GATT ATVV bridge for one remote (Chromecast or X6) on Windows 10/11.
/// </summary>
public sealed partial class WinRtBleBridge : IDisposable
{
    private readonly string _logTag;
    private readonly string _recordingPrefix;
    private readonly bool _resetSessionOnConnect;
    private readonly BleDeviceWatcher _watcher;
    private readonly AtvvProtocol _protocol = new();
    private readonly SemaphoreSlim _gattLock = new(1, 1);

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattDeviceService? _atvvService;
    private GattCharacteristic? _commandChar;
    private GattCharacteristic? _audioChar;
    private GattCharacteristic? _controlChar;

    private string? _connectedDeviceId;
    private bool _isStarted;
    private bool _isConnected;
    private bool _isStreaming;
    private bool _hasResetSessionForConnection;
    private byte _streamId;

    private DateTimeOffset _lastStartSearchAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMicOpenAt = DateTimeOffset.MinValue;
    private Timer? _keepAliveTimer;
    private Timer? _closeTimeoutTimer;
    private WavRecorder? _wavRecorder;

    private long _levelSumSq;
    private int _levelCount;
    private int _levelPeak;
    private DateTimeOffset _levelLogAt = DateTimeOffset.MinValue;
    private int _streamFrameCount;
    private int _streamSampleCount;
    private int _streamPeak;
    private long _streamSumSq;
    private int _streamClippedCount;
    private int _pendingSyncCount;
    private int _streamSyncCount;

    public event Action<bool>? ConnectionChanged;
    public event Action<bool, int>? StreamingChanged;
    public event Action<byte, int>? AudioStarted;
    public event Action<byte>? AudioStopped;
    public event Action? FirstAudioFrame;
    public event Action<ushort>? MicrophoneOpenFailed;
    public event Action<double, int>? LevelUpdated;
    public event Action<short[], int>? PcmDecoded;

    public WinRtBleBridge(string nameHint, string savedAddressFilename, string recordingPrefix, string logTag, bool resetSessionOnConnect)
    {
        _recordingPrefix = recordingPrefix;
        _logTag = logTag;
        _resetSessionOnConnect = resetSessionOnConnect;
        _watcher = new BleDeviceWatcher(nameHint, savedAddressFilename, logTag);
        _watcher.ConnectedCandidateFound += OnCandidateFound;
        _watcher.DeviceDisconnected += OnWatcherDisconnected;
    }

    public bool IsConnected => _isConnected;
    public bool IsStreaming => _isStreaming;

    public void Start()
    {
        if (_isStarted) return;
        _isStarted = true;
        _watcher.Start();
    }

    public void Stop()
    {
        _isStarted = false;
        _watcher.Stop();
        StopKeepAliveTimer();
        CancelCloseTimeout();

        if (_isStreaming)
        {
            try
            {
                byte[] cmd = _protocol.MicCloseCommand(_streamId);
                _ = WriteCommandAsync(cmd);
            }
            catch { }
            FinishAudioStreamLocally(0xFD);
        }
        DisconnectGatt();
    }

    public void SetRecordingEnabled(bool enabled)
    {
        if (enabled && _isStreaming && _wavRecorder is null)
        {
            _wavRecorder = WavRecorder.CreateNext(_recordingPrefix);
        }
        else if (!enabled && _wavRecorder is not null)
        {
            _wavRecorder.Dispose();
            _wavRecorder = null;
        }
    }

    public void ClearRecordings()
    {
        _wavRecorder?.Dispose();
        _wavRecorder = null;
        AppStorage.ClearFiles(AppStorage.Shared.RecordingsDirectory);
    }

    public RemoteMicrophoneOpenResult OpenMicrophone(bool bypassDebounce = false)
    {
        if (_isStreaming) return RemoteMicrophoneOpenResult.AlreadyStreaming;
        if (_device is null || _commandChar is null)
        {
            return RemoteMicrophoneOpenResult.RetryAfter(0.15);
        }

        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _lastMicOpenAt;
        if (!bypassDebounce && elapsed < TimeSpan.FromSeconds(0.35))
        {
            return RemoteMicrophoneOpenResult.RetryAfter(Math.Max(0.01, 0.35 - elapsed.TotalSeconds));
        }

        try
        {
            _protocol.PrepareForAudioStream();
            _pendingSyncCount = 0;
            byte[] cmd = _protocol.MicOpenCommand();
            _lastMicOpenAt = now;
            _ = WriteCommandAsync(cmd);
            LogService.Write($"[{_logTag}] TX micOpen requested by gesture");
            return RemoteMicrophoneOpenResult.Sent;
        }
        catch (Exception ex)
        {
            LogService.Write($"[{_logTag}] micOpen error: {ex.Message}");
            return RemoteMicrophoneOpenResult.Failed(ex.Message);
        }
    }

    public void CloseMicrophone(bool force = false)
    {
        if (!force && !_isStreaming) return;
        if (!_isStreaming)
        {
            LevelUpdated?.Invoke(-120, 0);
        }
        StopKeepAliveTimer();
        try
        {
            byte[] cmd = _protocol.MicCloseCommand(_streamId);
            _ = WriteCommandAsync(cmd);
            LogService.Write($"[{_logTag}] TX micClose requested by gesture");
            ScheduleCloseTimeout();
        }
        catch (Exception ex)
        {
            LogService.Write($"[{_logTag}] micClose error: {ex.Message}");
        }
    }

    private void OnCandidateFound(MatchedBleRemote candidate)
    {
        if (!_isStarted) return;
        if (_connectedDeviceId == candidate.DeviceId && _isConnected) return;
        _ = Task.Run(() => ConnectAsync(candidate));
    }

    private void OnWatcherDisconnected(string deviceId)
    {
        if (_connectedDeviceId != deviceId) return;
        LogService.Write($"[{_logTag}] BLE disconnected by OS watcher");
        FinishAudioStreamLocally(0xFC);
        DisconnectGatt();
    }

    private async Task ConnectAsync(MatchedBleRemote candidate)
    {
        await _gattLock.WaitAsync();
        try
        {
            if (!_isStarted || (_connectedDeviceId == candidate.DeviceId && _isConnected)) return;
            DisconnectGattLocked();

            var bleDevice = await BluetoothLEDevice.FromIdAsync(candidate.DeviceId);
            if (bleDevice is null)
            {
                LogService.Write($"[{_logTag}] FromIdAsync returned null for {candidate.Name}");
                return;
            }

            _device = bleDevice;
            _connectedDeviceId = candidate.DeviceId;
            _hasResetSessionForConnection = false;
            if (bleDevice.BluetoothAddress != 0)
            {
                _watcher.SaveAddress(bleDevice.BluetoothAddress);
            }
            bleDevice.ConnectionStatusChanged += OnBleConnectionStatusChanged;

            if (ApiInformation.IsTypePresent("Windows.Devices.Bluetooth.GenericAttributeProfile.GattSession"))
            {
                try
                {
                    _session = await GattSession.FromDeviceIdAsync(bleDevice.BluetoothDeviceId);
                    if (_session is not null) _session.MaintainConnection = true;
                }
                catch (Exception ex)
                {
                    LogService.Write($"[{_logTag}] GattSession warning: {ex.Message}");
                }
            }

            var servicesResult = await bleDevice.GetGattServicesForUuidAsync(AtvvProtocol.ServiceUuid, BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            {
                LogService.Write($"[{_logTag}] ATVV service discovery failed: {servicesResult.Status}");
                DisconnectGattLocked();
                return;
            }

            _atvvService = servicesResult.Services[0];
            var charsResult = await _atvvService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success)
            {
                LogService.Write($"[{_logTag}] ATVV char discovery failed: {charsResult.Status}");
                DisconnectGattLocked();
                return;
            }

            foreach (var ch in charsResult.Characteristics)
            {
                if (ch.Uuid == AtvvProtocol.TxUuid)
                {
                    _commandChar = ch;
                    if (ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) ||
                        ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                    {
                        await SubscribeAsync(ch, OnCommandValueChanged);
                    }
                }
                else if (ch.Uuid == AtvvProtocol.RxUuid)
                {
                    _audioChar = ch;
                    await SubscribeAsync(ch, OnAudioValueChanged);
                }
                else if (ch.Uuid == AtvvProtocol.ControlUuid)
                {
                    _controlChar = ch;
                    await SubscribeAsync(ch, OnControlValueChanged);
                }
            }

            if (_commandChar is null || _audioChar is null || _controlChar is null)
            {
                LogService.Write($"[{_logTag}] incomplete ATVV characteristics");
                DisconnectGattLocked();
                return;
            }

            _isConnected = true;
            ConnectionChanged?.Invoke(true);
            LogService.Write($"[{_logTag}] connected & ATVV subscribed ({candidate.Name})");
            await WriteCommandCoreAsync(_commandChar, AtvvProtocol.GetCapabilitiesCommand);
            LogService.Write($"[{_logTag}] TX getCapabilities");
        }
        catch (Exception ex)
        {
            LogService.Write($"[{_logTag}] connect exception: {ex.Message}");
            DisconnectGattLocked();
        }
        finally
        {
            _gattLock.Release();
        }
    }

    private static async Task SubscribeAsync(GattCharacteristic ch, Windows.Foundation.TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler)
    {
        ch.ValueChanged += handler;
        var mode = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        _ = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(mode);
    }

    private void OnBleConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
        LogService.Write($"[{_logTag}] ConnectionStatus -> Disconnected");
        FinishAudioStreamLocally(0xFC);
        DisconnectGatt();
    }

    private void DisconnectGatt()
    {
        _gattLock.Wait();
        try { DisconnectGattLocked(); }
        finally { _gattLock.Release(); }
    }

    private void DisconnectGattLocked()
    {
        bool wasConnected = _isConnected;
        _isConnected = false;
        _connectedDeviceId = null;

        if (_audioChar is not null) _audioChar.ValueChanged -= OnAudioValueChanged;
        if (_controlChar is not null) _controlChar.ValueChanged -= OnControlValueChanged;
        if (_commandChar is not null) _commandChar.ValueChanged -= OnCommandValueChanged;
        _audioChar = null;
        _controlChar = null;
        _commandChar = null;

        try { _atvvService?.Dispose(); } catch { }
        _atvvService = null;
        try { _session?.Dispose(); } catch { }
        _session = null;
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnBleConnectionStatusChanged;
            try { _device.Dispose(); } catch { }
            _device = null;
        }
        if (wasConnected) ConnectionChanged?.Invoke(false);
    }

    public void Dispose()
    {
        Stop();
        _watcher.Dispose();
        _gattLock.Dispose();
    }
}
