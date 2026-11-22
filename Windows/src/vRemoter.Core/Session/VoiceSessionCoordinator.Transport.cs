using vRemoter.Core.Localization;

namespace vRemoter.Core.Session;

public sealed partial class VoiceSessionCoordinator
{
    private void RequestRemoteTransportOpenLocked(
        VoiceRemoteId remote,
        bool bypassDebounce,
        int attempt,
        List<Action> pending)
    {
        if (_phase is not (SessionPhase.Opening or SessionPhase.Open) ||
            _streamingRemotes.Contains(remote) ||
            _openRequestedRemote == remote)
        {
            return;
        }

        var requester = OnMicrophoneOpenRequested;
        if (requester is null)
        {
            Log($"[VOICE-SESSION] MIC_OPEN unavailable remote={remote}");
            return;
        }

        var result = requester(remote, bypassDebounce);
        switch (result.Kind)
        {
            case RemoteMicrophoneOpenResult.ResultKind.Sent:
                _openRequestedRemote = remote;
                _openRequestedAttempt = attempt;
                ScheduleOpenConfirmationLocked(remote, attempt, _generation);
                Log($"[VOICE-SESSION] MIC_OPEN sent remote={remote} attempt={attempt}");
                break;
            case RemoteMicrophoneOpenResult.ResultKind.AlreadyStreaming:
                _streamingRemotes.Add(remote);
                _openRequestedRemote = null;
                break;
            case RemoteMicrophoneOpenResult.ResultKind.RetryAfter:
                ScheduleOpenRetryLocked(remote, result.Delay, attempt + 1);
                break;
            case RemoteMicrophoneOpenResult.ResultKind.Failed:
                Log($"[VOICE-SESSION] MIC_OPEN failed remote={remote} message={result.ErrorMessage}");
                ScheduleOpenRetryLocked(remote, TimeSpan.FromSeconds(0.20), attempt + 1);
                break;
        }
    }

    private void ScheduleOpenRetryLocked(VoiceRemoteId remote, TimeSpan delay, int attempt)
    {
        if (attempt > MaximumOpenAttempts)
        {
            Log($"[VOICE-SESSION] MIC_OPEN retries exhausted remote={remote}");
            return;
        }

        ulong expectedGen = _generation;
        _watchdogs.ScheduleOpenRetry(delay, () =>
        {
            var pending = new List<Action>();
            lock (_sync)
            {
                if (_generation != expectedGen || _phase is not (SessionPhase.Opening or SessionPhase.Open))
                {
                    return;
                }
                RequestRemoteTransportOpenLocked(remote, bypassDebounce: true, attempt, pending);
            }
            ExecutePending(pending);
        });
    }

    private void ScheduleOpenConfirmationLocked(VoiceRemoteId remote, int attempt, ulong expectedGen)
    {
        _watchdogs.ScheduleOpenConfirmation(OpenConfirmationTimeout, () =>
        {
            lock (_sync)
            {
                if (_generation != expectedGen ||
                    _openRequestedRemote != remote ||
                    _streamingRemotes.Contains(remote) ||
                    _phase is not (SessionPhase.Opening or SessionPhase.Open))
                {
                    return;
                }
                _openRequestedRemote = null;
                Log($"[VOICE-SESSION] MIC_OPEN confirmation timeout remote={remote} attempt={attempt}");
                ScheduleOpenRetryLocked(remote, TimeSpan.FromSeconds(0.05), attempt + 1);
            }
        });
    }

    private void ScheduleFirstFrameWatchdogLocked(VoiceRemoteId remote, int attempt)
    {
        ulong expectedGen = _generation;
        _watchdogs.ScheduleFirstFrame(remote, FirstAudioFrameTimeout, () =>
        {
            Action<VoiceRemoteId>? closeRequested = null;
            lock (_sync)
            {
                if (_generation != expectedGen ||
                    !_streamingRemotes.Contains(remote) ||
                    _phase is not (SessionPhase.Opening or SessionPhase.Open))
                {
                    return;
                }
                _watchdogs.CancelFirstFrame();
                if (attempt >= MaximumOpenAttempts)
                {
                    Log($"[VOICE-SESSION] MIC_OPEN acked but no audio; retries exhausted remote={remote}");
                    return;
                }
                Log($"[VOICE-SESSION] MIC_OPEN acked but no audio; reopening remote={remote} attempt={attempt + 1}");
                _reopenAfterStop = (remote, attempt + 1);
                closeRequested = OnRemoteMicrophoneCloseRequested;
            }
            closeRequested?.Invoke(remote);
        });
    }

    private void ScheduleStartVerificationLocked(ulong expectedGen)
    {
        _watchdogs.ScheduleStartVerification(TimeSpan.FromSeconds(1.0), () =>
        {
            var pending = new List<Action>();
            lock (_sync)
            {
                if (_generation != expectedGen || _phase != SessionPhase.Opening) return;
            }
            var snapshot = _captureState.SnapshotNow();
            if (snapshot.IsRecording)
            {
                HandleCaptureSnapshot(snapshot);
                return;
            }
            lock (_sync)
            {
                if (_generation != expectedGen || _phase != SessionPhase.Opening) return;
                FinalizeClosedLocked("opening timeout", pending);
                PublishLocked(L10n.Text("豆包未启动 · 遥控器自动关闭", "Doubao did not start · Closed"), pending);
            }
            ExecutePending(pending);
        });
    }

    private void ScheduleCloseVerificationLocked(ulong expectedGen)
    {
        var delay = CompatibilityMode ? CompatibilityCloseDelay : CloseVerificationTimeout;
        _watchdogs.ScheduleCloseVerification(delay, () =>
        {
            var pending = new List<Action>();
            var snapshot = _captureState.SnapshotNow();
            lock (_sync)
            {
                if (_generation != expectedGen || _phase != SessionPhase.Closing) return;
                if (snapshot.IsRecording)
                {
                    _phase = SessionPhase.Open;
                    _owner = SessionOwner.Keyboard;
                    _sessionRemote = PreferredRemoteProvider?.Invoke();
                    ActivateRemoteRouteLocked(pending);
                    if (_sessionRemote is { } remote)
                    {
                        RequestRemoteTransportOpenLocked(remote, bypassDebounce: false, attempt: 1, pending);
                    }
                    PublishLocked(L10n.Text("豆包仍在录音 · 请再次关闭", "Doubao still recording · Press again to close"), pending);
                }
                else
                {
                    FinalizeClosedLocked("close verification", pending);
                    PublishLocked(L10n.Text("豆包已就绪", "Doubao ready"), pending);
                }
            }
            ExecutePending(pending);
        });
    }

    private void ScheduleReconcileLocked(TimeSpan delay)
    {
        ulong expectedGen = _generation;
        _watchdogs.ScheduleReconcile(delay, () =>
        {
            lock (_sync)
            {
                if (_generation != expectedGen) return;
            }
            HandleCaptureSnapshot(_captureState.SnapshotNow());
        });
    }

    private void ActivateRemoteRouteLocked(List<Action> pending)
    {
        _watchdogs.CancelRouteClose();
        if (_routeActive) return;
        _routeActive = true;
        pending.Add(() => _setRemoteRouteActive(true));
        Log($"[VOICE-SESSION] audio route ON phase={_phase} owner={_owner}");
    }

    private void ScheduleRouteCloseLocked(TimeSpan delay)
    {
        ulong expectedGen = _generation;
        _watchdogs.ScheduleRouteClose(delay, () =>
        {
            var pending = new List<Action>();
            lock (_sync)
            {
                if (_generation != expectedGen) return;
                CloseRouteAndTransportsLocked(pending);
            }
            ExecutePending(pending);
        });
    }

    private void CloseAllRemoteTransportsLocked(List<Action> pending)
    {
        if (OnMicrophoneCloseRequested is { } cb)
        {
            pending.Add(cb);
        }
        _streamingRemotes.Clear();
        _openRequestedRemote = null;
        _watchdogs.CancelOpenRetry();
        _watchdogs.CancelOpenConfirmation();
        _watchdogs.CancelFirstFrame();
        _reopenAfterStop = null;
    }

    private void CloseRouteAndTransportsLocked(List<Action> pending)
    {
        bool hadTransport = _streamingRemotes.Count > 0 || _openRequestedRemote is not null;
        _routeActive = false;
        pending.Add(() => _setRemoteRouteActive(false));
        if (hadTransport)
        {
            CloseAllRemoteTransportsLocked(pending);
        }
        Log("[VOICE-SESSION] audio route OFF");
    }
}

