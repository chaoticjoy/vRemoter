using vRemoter.Core.Localization;

namespace vRemoter.Core.Session;

/// <summary>
/// Single voice-session state machine for all supported remotes (Chromecast / X6).
/// Coordinates input-method trigger synthesis, physical keyboard trigger edges,
/// and independent ATVV remote audio transports.
/// </summary>
public sealed partial class VoiceSessionCoordinator
{
    private enum SessionPhase { Closed, Opening, Open, Closing }
    private enum SessionOwner { None, Remote, Keyboard }

    private sealed record PhysicalVoiceGesture(
        DateTimeOffset BeganAt,
        bool RecognitionWasOpen,
        ulong Generation);

    private static readonly TimeSpan RemoteHoldThreshold = TimeSpan.FromSeconds(0.55);
    private static readonly TimeSpan OpenConfirmationTimeout = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan FirstAudioFrameTimeout = TimeSpan.FromSeconds(0.3);
    private static readonly TimeSpan CloseVerificationTimeout = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan CompatibilityCloseDelay = TimeSpan.FromSeconds(0.10);
    private const int MaximumOpenAttempts = 3;

    private readonly object _sync = new();
    private readonly IVoiceCaptureStateProvider _captureState;
    private readonly Action<bool> _setRemoteRouteActive;
    private readonly Action<bool> _sendTrigger;
    private readonly Action _releaseTrigger;
    private readonly Func<InputTriggerMode> _triggerMode;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<(string ShortName, string Title)> _inputMethodNames;
    private readonly VoiceSessionWatchdogs _watchdogs;

    private SessionPhase _phase = SessionPhase.Closed;
    private SessionOwner _owner = SessionOwner.None;
    private ulong _generation;
    private bool _routeActive;
    private VoiceRemoteId? _sessionRemote;
    private readonly HashSet<VoiceRemoteId> _streamingRemotes = [];
    private VoiceRemoteId? _openRequestedRemote;
    private int _openRequestedAttempt = 1;
    private (VoiceRemoteId Remote, int Attempt)? _reopenAfterStop;
    private readonly Dictionary<VoiceRemoteId, PhysicalVoiceGesture> _physicalGestures = [];

    public Func<VoiceRemoteId?>? PreferredRemoteProvider { get; set; }
    public Func<VoiceRemoteId, bool, RemoteMicrophoneOpenResult>? OnMicrophoneOpenRequested { get; set; }
    public Action? OnMicrophoneCloseRequested { get; set; }
    public Action<VoiceRemoteId>? OnRemoteMicrophoneCloseRequested { get; set; }
    public Action<string>? OnStateChanged { get; set; }
    public Action<string>? OnLog { get; set; }

    public VoiceSessionCoordinator(
        IVoiceCaptureStateProvider captureState,
        Action<bool> setRemoteRouteActive,
        Action<bool> sendTrigger,
        Action releaseTrigger,
        Func<InputTriggerMode> triggerMode,
        Func<(string ShortName, string Title)>? inputMethodNames = null,
        IVoiceSessionScheduler? scheduler = null,
        Func<DateTimeOffset>? now = null)
    {
        _captureState = captureState;
        _setRemoteRouteActive = setRemoteRouteActive;
        _sendTrigger = sendTrigger;
        _releaseTrigger = releaseTrigger;
        _triggerMode = triggerMode;
        _inputMethodNames = inputMethodNames ?? (() => ("豆包", "豆包输入法"));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _watchdogs = new VoiceSessionWatchdogs(scheduler ?? new ThreadPoolVoiceSessionScheduler());
    }

    private bool CompatibilityMode => !_captureState.CanObserveRecording;

    public VoiceSessionDebugSnapshot DebugSnapshot
    {
        get
        {
            lock (_sync)
            {
                return new VoiceSessionDebugSnapshot(
                    _phase.ToString().ToLowerInvariant(),
                    _generation,
                    _sessionRemote,
                    _streamingRemotes.ToHashSet(),
                    _openRequestedRemote);
            }
        }
    }

    public void Start()
    {
        _captureState.SnapshotChanged += HandleCaptureSnapshot;
        _captureState.Start();
    }

    public void Stop()
    {
        ForceClose();
        _captureState.SnapshotChanged -= HandleCaptureSnapshot;
        _captureState.Stop();
    }

    public void RemoteAudioStarted(
        VoiceRemoteId remote,
        byte reason,
        bool supportsPhysicalHoldGesture = false)
    {
        var snapshot = _captureState.SnapshotNow();
        var pendingCallbacks = new List<Action>();

        lock (_sync)
        {
            bool inserted = _streamingRemotes.Add(remote);
            int? hostRequestedAttempt = _openRequestedRemote == remote ? _openRequestedAttempt : null;
            if (_openRequestedRemote == remote)
            {
                _openRequestedRemote = null;
                _watchdogs.CancelOpenConfirmation();
            }

            if (_phase == SessionPhase.Closing)
            {
                Log($"[VOICE-SESSION] late AUDIO_START while closing remote={remote} reason=0x{reason:x2}");
                CloseAllRemoteTransportsLocked(pendingCallbacks);
                goto Dispatch;
            }

            if (supportsPhysicalHoldGesture && reason == 0x03)
            {
                bool recognitionWasOpen = _phase == SessionPhase.Open || snapshot.IsRecording;
                if (!_physicalGestures.ContainsKey(remote))
                {
                    _physicalGestures[remote] = new PhysicalVoiceGesture(_now(), recognitionWasOpen, _generation);
                }
                Log($"[VOICE-SESSION] physical ATVV DOWN remote={remote} recognitionWasOpen={recognitionWasOpen}");
                if (recognitionWasOpen)
                {
                    _sessionRemote = remote;
                    ActivateRemoteRouteLocked(pendingCallbacks);
                    PublishLocked(L10n.Text("遥控器按住录音中", "Remote hold recording"), pendingCallbacks);
                    goto Dispatch;
                }
            }

            if (!inserted)
            {
                Log($"[VOICE-SESSION] duplicate AUDIO_START ignored remote={remote} reason=0x{reason:x2}");
                goto Dispatch;
            }

            ActivateRemoteRouteLocked(pendingCallbacks);
            Log($"[VOICE-SESSION] AUDIO_START remote={remote} reason=0x{reason:x2} phase={_phase} owner={_owner} state={snapshot.State}");

            switch (_phase)
            {
                case SessionPhase.Opening:
                case SessionPhase.Open:
                    PublishLocked(L10n.Text("豆包录音中 · 遥控器已接入", "Doubao recording · Remote connected"), pendingCallbacks);
                    if (remote == VoiceRemoteId.Chromecast && reason == 0x00 && hostRequestedAttempt is int attempt)
                    {
                        ScheduleFirstFrameWatchdogLocked(remote, attempt);
                    }
                    break;

                case SessionPhase.Closed:
                    if (remote == VoiceRemoteId.Chromecast && reason == 0x00)
                    {
                        Log($"[VOICE-SESSION] stale host AUDIO_START ignored remote={remote}");
                        CloseAllRemoteTransportsLocked(pendingCallbacks);
                        break;
                    }
                    BeginOpeningLocked(SessionOwner.Remote, remote, sendTrigger: true, requestTransportImmediately: false, pendingCallbacks);
                    if (supportsPhysicalHoldGesture && reason == 0x03 &&
                        _physicalGestures.TryGetValue(remote, out var gesture))
                    {
                        _physicalGestures[remote] = gesture with { Generation = _generation };
                    }
                    break;
            }
        }

    Dispatch:
        ExecutePending(pendingCallbacks);
    }

    public void RemoteAudioFrameReceived(VoiceRemoteId remote)
    {
        lock (_sync)
        {
            _watchdogs.CancelFirstFrame(remote);
        }
    }

    public void RemoteAudioStopped(VoiceRemoteId remote, byte reason)
    {
        var pendingCallbacks = new List<Action>();
        lock (_sync)
        {
            _watchdogs.CancelFirstFrame(remote);
            _streamingRemotes.Remove(remote);
            if (_openRequestedRemote == remote)
            {
                _openRequestedRemote = null;
                _watchdogs.CancelOpenConfirmation();
            }

            if (reason == 0x02 && _physicalGestures.Remove(remote, out var gesture))
            {
                var duration = _now() - gesture.BeganAt;
                bool wasHold = duration >= RemoteHoldThreshold;
                Log($"[VOICE-SESSION] physical ATVV UP remote={remote} duration={duration.TotalSeconds:F3}s kind={(wasHold ? "HOLD" : "TAP")} wasOpen={gesture.RecognitionWasOpen}");

                if (gesture.Generation != _generation)
                {
                    Log($"[VOICE-SESSION] stale physical release ignored remote={remote}");
                    goto Dispatch;
                }

                if (wasHold || gesture.RecognitionWasOpen)
                {
                    CloseRecognitionFromRemoteLocked(wasHold ? "ATVV HOLD release" : "ATVV TAP toggle", pendingCallbacks);
                    goto Dispatch;
                }

                if (_phase is SessionPhase.Opening or SessionPhase.Open)
                {
                    _sessionRemote = remote;
                    RequestRemoteTransportOpenLocked(remote, bypassDebounce: true, attempt: 1, pendingCallbacks);
                    PublishLocked(L10n.Text("豆包录音中 · 遥控器持续收音", "Doubao recording · Remote streaming"), pendingCallbacks);
                }
                goto Dispatch;
            }

            Log($"[VOICE-SESSION] AUDIO_STOP remote={remote} reason=0x{reason:x2} phase={_phase}");
            if (_reopenAfterStop is { } pending && pending.Remote == remote)
            {
                _reopenAfterStop = null;
                if (_phase is SessionPhase.Opening or SessionPhase.Open)
                {
                    RequestRemoteTransportOpenLocked(remote, bypassDebounce: true, attempt: pending.Attempt, pendingCallbacks);
                }
                goto Dispatch;
            }

            if (_phase is SessionPhase.Open or SessionPhase.Opening)
            {
                PublishLocked(L10n.Text("豆包仍开启 · 遥控器音频已停止", "Doubao still active · Remote audio stopped"), pendingCallbacks);
            }
            else if (_streamingRemotes.Count == 0)
            {
                ScheduleRouteCloseLocked(TimeSpan.FromSeconds(0.08));
            }
        }

    Dispatch:
        ExecutePending(pendingCallbacks);
    }

    public void RemoteDisconnected(VoiceRemoteId remote)
    {
        var pendingCallbacks = new List<Action>();
        lock (_sync)
        {
            _streamingRemotes.Remove(remote);
            _physicalGestures.Remove(remote);
            if (_openRequestedRemote == remote)
            {
                _openRequestedRemote = null;
                _watchdogs.CancelOpenRetry();
                _watchdogs.CancelOpenConfirmation();
            }

            if (_sessionRemote != remote) return;

            _sessionRemote = PreferredRemoteProvider?.Invoke();
            if (_phase is SessionPhase.Opening or SessionPhase.Open && _sessionRemote is { } replacement)
            {
                RequestRemoteTransportOpenLocked(replacement, bypassDebounce: false, attempt: 1, pendingCallbacks);
            }
        }

        ExecutePending(pendingCallbacks);
    }

    public void RemoteMicrophoneOpenFailed(VoiceRemoteId remote, ushort code)
    {
        lock (_sync)
        {
            if (_openRequestedRemote != remote || _phase is not (SessionPhase.Opening or SessionPhase.Open))
            {
                return;
            }
            _openRequestedRemote = null;
            _watchdogs.CancelOpenConfirmation();
            Log($"[VOICE-SESSION] MIC_OPEN failed remote={remote} code=0x{code:x4}");
            ScheduleOpenRetryLocked(remote, TimeSpan.FromSeconds(0.20), attempt: 2);
        }
    }

    public void RemoteHidShortPress()
    {
        var pending = new List<Action>();
        lock (_sync) { CloseRecognitionFromRemoteLocked("HID SHORT", pending); }
        ExecutePending(pending);
    }

    public void RemoteHidLongPressEnded()
    {
        var pending = new List<Action>();
        lock (_sync) { CloseRecognitionFromRemoteLocked("HID LONG release", pending); }
        ExecutePending(pending);
    }

    public void TriggerDownObserved(bool isSynthetic)
    {
        if (isSynthetic) return;
        var snapshot = _captureState.SnapshotNow();
        var pending = new List<Action>();

        lock (_sync)
        {
            bool shouldOpen = _phase switch
            {
                SessionPhase.Closed => !snapshot.IsRecording,
                SessionPhase.Closing => true,
                _ => false
            };

            Log($"[TRIGGER] DOWN source=keyboard action={(shouldOpen ? "open" : "close")} phase={_phase} state={snapshot.State}");
            if (shouldOpen)
            {
                var remote = PreferredRemoteProvider?.Invoke();
                if (remote is null) BeginOpeningWithoutRemoteLocked(pending);
                else BeginOpeningLocked(SessionOwner.Keyboard, remote.Value, sendTrigger: false, requestTransportImmediately: true, pending);
            }
            else
            {
                BeginClosingLocked(sendTrigger: false, "keyboard trigger", pending);
            }
        }

        ExecutePending(pending);
    }

    public void TriggerUpObserved(bool isSynthetic)
    {
        if (isSynthetic) return;
        var pending = new List<Action>();
        lock (_sync)
        {
            Log($"[TRIGGER] UP source=keyboard phase={_phase} owner={_owner}");
            if (CompatibilityMode &&
                _triggerMode() == InputTriggerMode.Hold &&
                _phase is SessionPhase.Opening or SessionPhase.Open)
            {
                BeginClosingLocked(sendTrigger: false, "keyboard hold release", pending);
                goto Dispatch;
            }
            ScheduleReconcileLocked(TimeSpan.FromSeconds(0.03));
            ScheduleReconcileLocked(TimeSpan.FromSeconds(0.15));
        }

    Dispatch:
        ExecutePending(pending);
    }

    public void ForceClose()
    {
        var pending = new List<Action>();
        lock (_sync)
        {
            _generation++;
            _watchdogs.CancelAll();
            _phase = SessionPhase.Closed;
            _owner = SessionOwner.None;
            _sessionRemote = null;
            _physicalGestures.Clear();
            pending.Add(_releaseTrigger);
            CloseRouteAndTransportsLocked(pending);
            PublishLocked(L10n.Text("已手动关闭", "Closed manually"), pending);
        }
        ExecutePending(pending);
    }
}
