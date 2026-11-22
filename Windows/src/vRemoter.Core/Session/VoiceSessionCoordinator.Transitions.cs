using vRemoter.Core.Localization;

namespace vRemoter.Core.Session;

public sealed partial class VoiceSessionCoordinator
{
    private void HandleCaptureSnapshot(VoiceCaptureSnapshot snapshot)
    {
        var pending = new List<Action>();
        lock (_sync)
        {
            switch (snapshot.State)
            {
                case VoiceCaptureState.Active:
                    switch (_phase)
                    {
                        case SessionPhase.Opening:
                            ConfirmOpenLocked(snapshot, pending);
                            break;
                        case SessionPhase.Open:
                            ActivateRemoteRouteLocked(pending);
                            PublishLocked(L10n.Text($"豆包录音中 · {snapshot.DeviceSummary}", $"Doubao recording · {snapshot.DeviceSummary}"), pending);
                            break;
                        case SessionPhase.Closing:
                            Log("[VOICE-SESSION] active snapshot ignored while closing");
                            break;
                        case SessionPhase.Closed:
                            AdoptExternalActivationLocked(snapshot, pending);
                            break;
                    }
                    break;

                case VoiceCaptureState.Inactive:
                    switch (_phase)
                    {
                        case SessionPhase.Opening:
                            break;
                        case SessionPhase.Open:
                        case SessionPhase.Closing:
                            FinalizeClosedLocked("Doubao inactive", pending);
                            break;
                        case SessionPhase.Closed:
                            if (_routeActive || _streamingRemotes.Count > 0)
                            {
                                CloseRouteAndTransportsLocked(pending);
                            }
                            break;
                    }
                    PublishLocked(L10n.Text("豆包已就绪", "Doubao ready"), pending);
                    break;

                case VoiceCaptureState.Unavailable:
                    if (CompatibilityMode && snapshot.ProcessId is not null)
                    {
                        if (_phase == SessionPhase.Closed)
                        {
                            PublishLocked(L10n.Text("豆包已就绪", "Doubao ready"), pending);
                        }
                        break;
                    }
                    if (_phase != SessionPhase.Opening)
                    {
                        FinalizeClosedLocked("Doubao unavailable", pending);
                    }
                    PublishLocked(L10n.Text("等待豆包输入法", "Waiting for Doubao"), pending);
                    break;
            }
        }
        ExecutePending(pending);
    }

    private void ConfirmOpenLocked(VoiceCaptureSnapshot snapshot, List<Action> pending)
    {
        _watchdogs.CancelStartVerification();
        _phase = SessionPhase.Open;
        ActivateRemoteRouteLocked(pending);
        if (_sessionRemote is { } remote &&
            !_streamingRemotes.Contains(remote) &&
            _openRequestedRemote != remote)
        {
            RequestRemoteTransportOpenLocked(remote, bypassDebounce: false, attempt: 1, pending);
        }
        PublishLocked(L10n.Text($"豆包录音中 · {snapshot.DeviceSummary}", $"Doubao recording · {snapshot.DeviceSummary}"), pending);
    }

    private void ConfirmOpenWithoutObservationLocked(List<Action> pending)
    {
        if (!CompatibilityMode || _phase != SessionPhase.Opening)
        {
            return;
        }
        var snapshot = _captureState.SnapshotNow();
        if (snapshot.ProcessId is null)
        {
            return;
        }
        Log("[VOICE-SESSION] compatibility mode: input method running, open");
        ConfirmOpenLocked(snapshot, pending);
    }

    private void AdoptExternalActivationLocked(VoiceCaptureSnapshot snapshot, List<Action> pending)
    {
        _generation++;
        _watchdogs.CancelAll();
        _phase = SessionPhase.Open;
        _owner = SessionOwner.Keyboard;
        _sessionRemote = PreferredRemoteProvider?.Invoke();
        ActivateRemoteRouteLocked(pending);
        Log($"[VOICE-SESSION] external activation adopted device={snapshot.DeviceSummary}");
        if (_sessionRemote is { } remote)
        {
            RequestRemoteTransportOpenLocked(remote, bypassDebounce: false, attempt: 1, pending);
        }
        PublishLocked(L10n.Text($"豆包录音中 · {snapshot.DeviceSummary}", $"Doubao recording · {snapshot.DeviceSummary}"), pending);
    }

    private void BeginOpeningWithoutRemoteLocked(List<Action> pending)
    {
        _generation++;
        _watchdogs.CancelAll();
        _phase = SessionPhase.Opening;
        _owner = SessionOwner.Keyboard;
        _sessionRemote = null;
        ActivateRemoteRouteLocked(pending);
        ScheduleStartVerificationLocked(_generation);
        PublishLocked(L10n.Text("豆包正在启动 · 等待遥控器", "Doubao starting · Waiting for remote"), pending);
        ConfirmOpenWithoutObservationLocked(pending);
    }

    private void BeginOpeningLocked(
        SessionOwner owner,
        VoiceRemoteId remote,
        bool sendTrigger,
        bool requestTransportImmediately,
        List<Action> pending)
    {
        _generation++;
        _watchdogs.CancelAll();
        _phase = SessionPhase.Opening;
        _owner = owner;
        _sessionRemote = remote;
        ActivateRemoteRouteLocked(pending);
        if (sendTrigger)
        {
            pending.Add(() => _sendTrigger(true));
        }
        if (requestTransportImmediately)
        {
            RequestRemoteTransportOpenLocked(remote, bypassDebounce: false, attempt: 1, pending);
        }
        ScheduleStartVerificationLocked(_generation);
        PublishLocked(owner == SessionOwner.Keyboard
            ? L10n.Text("豆包正在启动 · 遥控器已预热", "Doubao starting · Remote warming up")
            : L10n.Text("遥控器已启动 · 正在打开豆包", "Remote started · Opening Doubao"), pending);
        ConfirmOpenWithoutObservationLocked(pending);
    }

    private void CloseRecognitionFromRemoteLocked(string reason, List<Action> pending)
    {
        if (_phase is not (SessionPhase.Opening or SessionPhase.Open))
        {
            Log($"[VOICE-SESSION] {reason} ignored phase={_phase}");
            return;
        }
        BeginClosingLocked(sendTrigger: true, reason, pending);
    }

    private void BeginClosingLocked(bool sendTrigger, string reason, List<Action> pending)
    {
        if (_phase is SessionPhase.Closed or SessionPhase.Closing)
        {
            return;
        }

        _generation++;
        _watchdogs.CancelAll();
        _phase = SessionPhase.Closing;
        _owner = SessionOwner.None;
        _openRequestedRemote = null;
        if (sendTrigger)
        {
            pending.Add(() => _sendTrigger(false));
        }
        ScheduleRouteCloseLocked(TimeSpan.FromSeconds(0.10));
        ScheduleCloseVerificationLocked(_generation);
        PublishLocked(L10n.Text("遥控器已结束 · 正在关闭豆包", "Remote ended · Closing Doubao"), pending);
        Log($"[VOICE-SESSION] begin closing reason={reason} trigger={(sendTrigger ? "synthetic" : "physical")}");
    }

    private void FinalizeClosedLocked(string reason, List<Action> pending)
    {
        _generation++;
        _watchdogs.CancelAll();
        _phase = SessionPhase.Closed;
        _owner = SessionOwner.None;
        _sessionRemote = null;
        _physicalGestures.Clear();
        pending.Add(_releaseTrigger);
        CloseRouteAndTransportsLocked(pending);
        Log($"[VOICE-SESSION] closed reason={reason}");
    }

    private void PublishLocked(string status, List<Action> pending)
    {
        var (shortName, title) = _inputMethodNames();
        string text = status
            .Replace("豆包输入法", title, StringComparison.Ordinal)
            .Replace("豆包", shortName, StringComparison.Ordinal);
        if (CompatibilityMode && !text.Contains("兼容模式", StringComparison.Ordinal))
        {
            text += L10n.Text(" · 兼容模式", " · Compat");
        }
        if (OnStateChanged is { } cb)
        {
            pending.Add(() => cb(text));
        }
    }

    private void Log(string message) => OnLog?.Invoke(message);

    private static void ExecutePending(List<Action> pending)
    {
        foreach (var action in pending)
        {
            action();
        }
    }
}

