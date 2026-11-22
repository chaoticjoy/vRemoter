using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using vRemoter.Core.Session;
using vRemoter.Windows.Input;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Audio;

/// <summary>
/// Monitors Windows CoreAudio capture sessions across all active recording endpoints
/// via IAudioSessionManager2 to detect real-time Doubao / WeType microphone usage.
/// </summary>
public sealed class WasapiCaptureMonitor : IVoiceCaptureStateProvider, IMMNotificationClient, IDisposable
{
    private sealed class TrackedSession(
        string deviceId,
        string deviceName,
        AudioSessionControl control,
        WasapiCaptureMonitor owner) : IAudioSessionEventsHandler
    {
        public string DeviceId { get; } = deviceId;
        public string DeviceName { get; } = deviceName;
        public AudioSessionControl Control { get; } = control;

        public void OnVolumeChanged(float volume, bool isMuted) { }
        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }

        public void OnStateChanged(AudioSessionState state) => owner.ScheduleEvaluate();

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) =>
            owner.ScheduleRebind();
    }

    private readonly object _sync = new();
    private readonly List<(MMDevice Device, AudioSessionManager Manager)> _managers = [];
    private readonly List<TrackedSession> _tracked = [];

    private MMDeviceEnumerator? _enumerator;
    private Timer? _pollTimer;
    private VoiceCaptureSnapshot? _lastSnapshot;
    private bool _started;

    public event Action<VoiceCaptureSnapshot>? SnapshotChanged;
    public bool CanObserveRecording { get; }

    public WasapiCaptureMonitor()
    {
        bool? overrideCompat = AppStorage.Shared.VoiceCompatibilityOverride;
        CanObserveRecording = overrideCompat.HasValue ? !overrideCompat.Value : true;
        LogService.Write($"[IME-STATE] capture monitor initialized (observable={CanObserveRecording}, {WindowsVersionInfo.FriendlySummary})");
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }

        if (CanObserveRecording)
        {
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _enumerator.RegisterEndpointNotificationCallback(this);
                BindCaptureManagers();
            }
            catch (Exception ex)
            {
                LogService.Write($"[IME-STATE] WASAPI session binding failed: {ex.Message}");
            }
        }

        _pollTimer = new Timer(_ => EvaluateAndPublish(force: false), null,
            TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(1500));
        EvaluateAndPublish(force: true);
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_started) return;
            _started = false;
            _pollTimer?.Dispose();
            _pollTimer = null;
            UnbindAllLocked();
            try
            {
                _enumerator?.UnregisterEndpointNotificationCallback(this);
                _enumerator?.Dispose();
            }
            catch { }
            _enumerator = null;
            _lastSnapshot = null;
        }
    }

    public void TargetDidChange() => EvaluateAndPublish(force: true);

    public VoiceCaptureSnapshot SnapshotNow() => ReadCurrentSnapshot();

    private void BindCaptureManagers()
    {
        lock (_sync)
        {
            UnbindAllLocked();
            if (_enumerator is null) return;

            var captures = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var device in captures)
            {
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.OnSessionCreated += OnSessionCreated;
                    _managers.Add((device, manager));

                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var ctrl = sessions[i];
                        var tracked = new TrackedSession(device.ID, device.FriendlyName, ctrl, this);
                        ctrl.RegisterEventClient(tracked);
                        _tracked.Add(tracked);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Write($"[IME-STATE] failed binding {device.FriendlyName}: {ex.Message}");
                }
            }
        }
    }

    private void OnSessionCreated(object sender, IAudioSessionControl newSession) => ScheduleRebind();

    private void ScheduleRebind() =>
        _ = Task.Run(() =>
        {
            lock (_sync)
            {
                if (!_started || !CanObserveRecording) return;
            }
            BindCaptureManagers();
            EvaluateAndPublish(force: false);
        });

    private void ScheduleEvaluate() =>
        _ = Task.Run(() => EvaluateAndPublish(force: false));

    private void EvaluateAndPublish(bool force)
    {
        lock (_sync)
        {
            if (!_started) return;
        }

        var snapshot = ReadCurrentSnapshot();
        bool changed;
        lock (_sync)
        {
            changed = force || !AreEqual(_lastSnapshot, snapshot);
            if (changed) _lastSnapshot = snapshot;
        }
        if (!changed) return;

        LogService.Write($"[IME-STATE] state={snapshot.State} pid={snapshot.ProcessId?.ToString() ?? "-"} devices={snapshot.DeviceSummary}");
        if (snapshot.IsRecording && !IsUsingVirtualMic(snapshot))
        {
            string name = VoiceInputMethodProfiles.GetShortName(AppStorage.Shared.VoiceInputMethod);
            LogService.Write($"[IME-STATE] ⚠️ {name} is recording on non-virtual mic: {snapshot.DeviceSummary}");
        }
        SnapshotChanged?.Invoke(snapshot);
    }

    private VoiceCaptureSnapshot ReadCurrentSnapshot()
    {
        var method = AppStorage.Shared.VoiceInputMethod;
        var matchingPids = FindMatchingProcessIds(method);

        if (!CanObserveRecording)
        {
            return new VoiceCaptureSnapshot(
                VoiceCaptureState.Unavailable,
                matchingPids.Count > 0 ? matchingPids.First() : null,
                [],
                [],
                CaptureObservable: false);
        }

        if (matchingPids.Count == 0)
        {
            return new VoiceCaptureSnapshot(VoiceCaptureState.Unavailable, null, [], []);
        }

        var activeIds = new List<string>();
        var activeNames = new List<string>();

        lock (_sync)
        {
            foreach (var item in _tracked)
            {
                try
                {
                    int pid = unchecked((int)item.Control.GetProcessID);
                    if (matchingPids.Contains(pid) &&
                        item.Control.State == AudioSessionState.AudioSessionStateActive)
                    {
                        if (!activeIds.Contains(item.DeviceId))
                        {
                            activeIds.Add(item.DeviceId);
                            activeNames.Add(item.DeviceName);
                        }
                    }
                }
                catch
                {
                }
            }
        }

        return new VoiceCaptureSnapshot(
            activeIds.Count > 0 ? VoiceCaptureState.Active : VoiceCaptureState.Inactive,
            matchingPids.First(),
            activeIds.ToArray(),
            activeNames.ToArray());
    }

    private static HashSet<int> FindMatchingProcessIds(VoiceInputMethodKind method)
    {
        var targets = VoiceInputMethodProfiles.GetProcessNames(method);
        var pids = new HashSet<int>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                string name = proc.ProcessName;
                if (targets.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    pids.Add(proc.Id);
                }
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }
        return pids;
    }

    private static bool IsUsingVirtualMic(VoiceCaptureSnapshot snapshot) =>
        snapshot.InputDeviceNames.Any(n =>
            n.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("vRemoteDr", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase));

    private static bool AreEqual(VoiceCaptureSnapshot? a, VoiceCaptureSnapshot b) =>
        a is not null &&
        a.State == b.State &&
        a.ProcessId == b.ProcessId &&
        a.CaptureObservable == b.CaptureObservable &&
        a.InputDeviceIds.SequenceEqual(b.InputDeviceIds) &&
        a.InputDeviceNames.SequenceEqual(b.InputDeviceNames);

    private void UnbindAllLocked()
    {
        foreach (var item in _tracked)
        {
            try { item.Control.UnRegisterEventClient(item); } catch { }
            try { item.Control.Dispose(); } catch { }
        }
        _tracked.Clear();

        foreach (var (device, manager) in _managers)
        {
            try { manager.OnSessionCreated -= OnSessionCreated; } catch { }
            try { device.Dispose(); } catch { }
        }
        _managers.Clear();
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => ScheduleRebind();
    public void OnDeviceAdded(string pwstrDeviceId) => ScheduleRebind();
    public void OnDeviceRemoved(string deviceId) => ScheduleRebind();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose() => Stop();
}

