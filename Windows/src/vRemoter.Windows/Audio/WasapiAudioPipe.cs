using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Audio;

/// <summary>
/// Captures the PC physical microphone via WASAPI, mixes with remote ATVV PCM,
/// and streams continuously to the virtual audio cable input endpoint.
/// </summary>
public sealed class WasapiAudioPipe : IDisposable, IMMNotificationClient
{
    private readonly object _sync = new();
    private readonly DualMicMixer _mixer = new();

    private MMDeviceEnumerator? _enumerator;
    private WasapiOut? _renderOut;
    private WasapiCapture? _pcCapture;
    private VirtualAudioRoute? _currentRoute;

    private bool _pcInputEnabled = AppStorage.Shared.PcInputEnabled;
    private bool _remoteInputEnabled = AppStorage.Shared.RemoteInputEnabled;
    private bool _mixActive;
    private DateTimeOffset _lastLevelAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public event Action<double>? PcLevelUpdated;
    public event Action<bool>? RouteChanged;
    public event Action<VirtualAudioRoute?>? VirtualRouteAvailabilityChanged;

    public bool IsPcInputEnabled => _pcInputEnabled;
    public bool IsRemoteInputEnabled => _remoteInputEnabled;
    public VirtualAudioRoute? CurrentRoute => _currentRoute;
    public bool IsOutputDeviceAvailable => _currentRoute is not null;

    public WasapiAudioPipe()
    {
        _mixer.SetInputEnabled(_pcInputEnabled, _remoteInputEnabled);
    }

    public void Start()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
        }
        catch (Exception ex)
        {
            LogService.Write($"[AUDIO] MMDeviceEnumerator failed: {ex.Message}");
        }

        RefreshEndpoints();
    }

    public void RefreshEndpoints()
    {
        var route = VirtualAudioDetector.DetectRoute(AppStorage.Shared.PreferredVirtualAudioDevice);
        lock (_sync)
        {
            if (_disposed) return;
            if (!Equals(_currentRoute?.RenderDeviceId, route?.RenderDeviceId))
            {
                StopRenderLocked();
                _currentRoute = route;
                if (route is not null)
                {
                    StartRenderLocked(route);
                }
                else
                {
                    LogService.Write("[AUDIO] Virtual audio cable not found (VB-Cable not installed yet)");
                }
            }
        }
        VirtualRouteAvailabilityChanged?.Invoke(route);

        if (_pcInputEnabled)
        {
            EnsurePcCaptureStarted();
        }
    }

    public void FeedRemotePcm(short[] samples, int sampleRate) =>
        _mixer.FeedRemotePcm(samples, sampleRate);

    public void SetRemoteActive(bool active)
    {
        bool changed;
        lock (_sync)
        {
            changed = _mixActive != active;
            _mixActive = active;
            _mixer.SetMixActive(active);
        }
        if (!changed) return;

        LogService.Write(active
            ? "[AUDIO] Dual-mic mix ON (Remote + PC Mic, 120ms aligned)"
            : "[AUDIO] Dual-mic mix OFF");
        RouteChanged?.Invoke(active);
    }

    public void SetInputEnabled(bool pcEnabled, bool remoteEnabled)
    {
        bool pcChanged;
        bool remoteChanged;
        bool active;
        lock (_sync)
        {
            pcChanged = _pcInputEnabled != pcEnabled;
            remoteChanged = _remoteInputEnabled != remoteEnabled;
            _pcInputEnabled = pcEnabled;
            _remoteInputEnabled = remoteEnabled;
            active = _mixActive;
            _mixer.SetInputEnabled(pcEnabled, remoteEnabled);
        }

        AppStorage.Shared.PcInputEnabled = pcEnabled;
        AppStorage.Shared.RemoteInputEnabled = remoteEnabled;

        if (pcChanged)
        {
            if (pcEnabled)
            {
                EnsurePcCaptureStarted();
            }
            else
            {
                StopPcCapture();
                PcLevelUpdated?.Invoke(-120);
            }
        }

        if (pcChanged || remoteChanged)
        {
            LogService.Write($"[AUDIO] Input selection updated · PC={(pcEnabled ? "ON" : "OFF")} Remote={(remoteEnabled ? "ON" : "OFF")}");
            RouteChanged?.Invoke(active);
        }
    }

    private void StartRenderLocked(VirtualAudioRoute route)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(route.RenderDeviceId);
            var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 40);
            output.Init(_mixer);
            output.Play();
            _renderOut = output;
            LogService.Write($"[AUDIO] WASAPI render started: {route.RenderFriendlyName} (48kHz stereo)");
        }
        catch (Exception ex)
        {
            LogService.Write($"[AUDIO] WASAPI render failed ({route.RenderFriendlyName}): {ex.Message}");
            StopRenderLocked();
        }
    }

    private void StopRenderLocked()
    {
        if (_renderOut is null) return;
        try { _renderOut.Stop(); } catch { }
        try { _renderOut.Dispose(); } catch { }
        _renderOut = null;
    }

    private void EnsurePcCaptureStarted()
    {
        lock (_sync)
        {
            if (_disposed || _pcCapture is not null) return;
            try
            {
                var micDevice = FindPhysicalMicrophone();
                if (micDevice is null)
                {
                    LogService.Write("[AUDIO] No physical PC microphone available (skipping virtual devices)");
                    return;
                }

                var capture = new WasapiCapture(micDevice);
                capture.DataAvailable += OnPcDataAvailable;
                capture.RecordingStopped += OnPcRecordingStopped;
                capture.StartRecording();
                _pcCapture = capture;
                LogService.Write($"[AUDIO] PC microphone capture started: {micDevice.FriendlyName} ({capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels}ch, {capture.WaveFormat.Encoding})");
            }
            catch (Exception ex)
            {
                LogService.Write($"[AUDIO] PC microphone capture failed: {ex.Message}");
                StopPcCaptureLocked();
            }
        }
    }

    private MMDevice? FindPhysicalMicrophone()
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            if (!VirtualAudioDetector.IsVirtualCaptureDevice(def))
            {
                return def;
            }
        }
        catch
        {
        }

        var candidates = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Where(d => !VirtualAudioDetector.IsVirtualCaptureDevice(d))
            .ToList();

        return candidates.FirstOrDefault(d =>
                d.FriendlyName.Contains("Microphone", StringComparison.OrdinalIgnoreCase) ||
                d.FriendlyName.Contains("麦克风", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    private void OnPcDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || sender is not WasapiCapture cap) return;
        float[] mono = ExtractMonoFloats(e.Buffer, e.BytesRecorded, cap.WaveFormat);
        if (mono.Length == 0) return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastLevelAt >= TimeSpan.FromMilliseconds(100))
        {
            double sumSq = 0;
            for (int i = 0; i < mono.Length; i++) sumSq += mono[i] * mono[i];
            double rms = Math.Sqrt(sumSq / mono.Length);
            double db = rms > 0 ? 20.0 * Math.Log10(rms) : -120.0;
            PcLevelUpdated?.Invoke(db);
            _lastLevelAt = now;
        }

        _mixer.FeedPcMono(mono, cap.WaveFormat.SampleRate);
    }

    private static float[] ExtractMonoFloats(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        int channels = Math.Max(1, format.Channels);
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            int totalSamples = bytesRecorded / 4;
            int frames = totalSamples / channels;
            var mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    sum += BitConverter.ToSingle(buffer, (f * channels + c) * 4);
                }
                mono[f] = sum / channels;
            }
            return mono;
        }

        if (format.BitsPerSample == 16)
        {
            int totalSamples = bytesRecorded / 2;
            int frames = totalSamples / channels;
            var mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    short s = BitConverter.ToInt16(buffer, (f * channels + c) * 2);
                    sum += s / 32768.0f;
                }
                mono[f] = sum / channels;
            }
            return mono;
        }

        return [];
    }

    private void OnPcRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            LogService.Write($"[AUDIO] PC capture stopped with error: {e.Exception.Message}");
        }
    }

    private void StopPcCapture()
    {
        lock (_sync) { StopPcCaptureLocked(); }
    }

    private void StopPcCaptureLocked()
    {
        if (_pcCapture is null) return;
        _pcCapture.DataAvailable -= OnPcDataAvailable;
        _pcCapture.RecordingStopped -= OnPcRecordingStopped;
        try { _pcCapture.StopRecording(); } catch { }
        try { _pcCapture.Dispose(); } catch { }
        _pcCapture = null;
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => ScheduleTopologyRefresh();
    public void OnDeviceAdded(string pwstrDeviceId) => ScheduleTopologyRefresh();
    public void OnDeviceRemoved(string deviceId) => ScheduleTopologyRefresh();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Capture && _pcInputEnabled)
        {
            _ = Task.Run(() =>
            {
                StopPcCapture();
                EnsurePcCaptureStarted();
            });
        }
    }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    private void ScheduleTopologyRefresh() =>
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            RefreshEndpoints();
        });

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            StopPcCaptureLocked();
            StopRenderLocked();
            try
            {
                _enumerator?.UnregisterEndpointNotificationCallback(this);
                _enumerator?.Dispose();
            }
            catch { }
            _enumerator = null;
        }
    }
}

