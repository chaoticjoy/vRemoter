using vRemoter.Core.Session;
using Xunit;

namespace vRemoter.Core.Tests;

public sealed class VoiceSessionCoordinatorTests
{
    private sealed class FakeCaptureState : IVoiceCaptureStateProvider
    {
        private VoiceCaptureSnapshot _snapshot;
        public event Action<VoiceCaptureSnapshot>? SnapshotChanged;
        public bool CanObserveRecording { get; }

        public FakeCaptureState(VoiceCaptureState state = VoiceCaptureState.Inactive)
        {
            CanObserveRecording = true;
            _snapshot = MakeSnapshot(state);
        }

        public FakeCaptureState(bool compatibilityWithImeRunning)
        {
            CanObserveRecording = false;
            _snapshot = new VoiceCaptureSnapshot(
                VoiceCaptureState.Unavailable,
                compatibilityWithImeRunning ? 123 : null,
                [],
                [],
                CaptureObservable: false);
        }

        public void Start() { }
        public void Stop() { }
        public VoiceCaptureSnapshot SnapshotNow() => _snapshot;

        public void EmitCurrent() => SnapshotChanged?.Invoke(_snapshot);

        public void Emit(VoiceCaptureState state)
        {
            _snapshot = MakeSnapshot(state);
            SnapshotChanged?.Invoke(_snapshot);
        }

        private static VoiceCaptureSnapshot MakeSnapshot(VoiceCaptureState state) => new(
            state,
            123,
            state == VoiceCaptureState.Active ? ["dev-1"] : [],
            state == VoiceCaptureState.Active ? ["CABLE Output"] : []);
    }

    private sealed class VirtualScheduler : IVoiceSessionScheduler
    {
        private sealed class TaskItem(DateTimeOffset due, long seq, Action action)
        {
            public DateTimeOffset Due { get; } = due;
            public long Seq { get; } = seq;
            public Action Action { get; } = action;
            public bool Cancelled { get; set; }
        }

        private readonly List<TaskItem> _items = [];
        private long _seq;
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.FromUnixTimeSeconds(1_000);

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var item = new TaskItem(Now + delay, Interlocked.Increment(ref _seq), callback);
            _items.Add(item);
            return new Handle(item);
        }

        public void Advance(TimeSpan delta)
        {
            var target = Now + delta;
            while (true)
            {
                var next = _items
                    .Where(x => !x.Cancelled && x.Due <= target)
                    .OrderBy(x => x.Due)
                    .ThenBy(x => x.Seq)
                    .FirstOrDefault();
                if (next is null) break;
                _items.Remove(next);
                Now = next.Due;
                next.Action();
            }
            Now = target;
        }

        private sealed class Handle(TaskItem item) : IDisposable
        {
            public void Dispose() => item.Cancelled = true;
        }
    }

    private sealed class Harness
    {
        public FakeCaptureState Capture { get; }
        public VirtualScheduler Scheduler { get; } = new();
        public VoiceSessionCoordinator Coordinator { get; }
        public int TriggerTapCount { get; private set; }
        public List<bool> TriggerEdges { get; } = [];
        public int ReleaseCount { get; private set; }
        public List<bool> RouteChanges { get; } = [];
        public List<(VoiceRemoteId Remote, bool Bypass)> OpenRequests { get; } = [];
        public int CloseRequestCount { get; private set; }
        public List<VoiceRemoteId> RemoteCloseRequests { get; } = [];
        public Queue<RemoteMicrophoneOpenResult> OpenResults { get; } = new();
        public VoiceRemoteId? PreferredRemote { get; set; } = VoiceRemoteId.Chromecast;
        public InputTriggerMode Mode { get; set; } = InputTriggerMode.Toggle;

        public Harness(VoiceCaptureState initialState = VoiceCaptureState.Inactive)
            : this(new FakeCaptureState(initialState)) { }

        public Harness(bool compatibilityWithImeRunning, InputTriggerMode mode = InputTriggerMode.Toggle)
            : this(new FakeCaptureState(compatibilityWithImeRunning))
        {
            Mode = mode;
        }

        private Harness(FakeCaptureState capture)
        {
            Capture = capture;
            Coordinator = new VoiceSessionCoordinator(
                capture,
                active => RouteChanges.Add(active),
                opening =>
                {
                    TriggerTapCount++;
                    TriggerEdges.Add(opening);
                },
                () => ReleaseCount++,
                () => Mode,
                scheduler: Scheduler,
                now: () => Scheduler.Now)
            {
                PreferredRemoteProvider = () => PreferredRemote,
                OnMicrophoneOpenRequested = (remote, bypass) =>
                {
                    OpenRequests.Add((remote, bypass));
                    return OpenResults.Count > 0 ? OpenResults.Dequeue() : RemoteMicrophoneOpenResult.Sent;
                },
                OnMicrophoneCloseRequested = () => CloseRequestCount++,
                OnRemoteMicrophoneCloseRequested = remote => RemoteCloseRequests.Add(remote)
            };
            Coordinator.Start();
        }

        public void Advance(double seconds) => Scheduler.Advance(TimeSpan.FromSeconds(seconds));
        public void BeginChromecastPress() =>
            Coordinator.RemoteAudioStarted(VoiceRemoteId.Chromecast, 0x03, supportsPhysicalHoldGesture: true);
        public void EndChromecastPress() =>
            Coordinator.RemoteAudioStopped(VoiceRemoteId.Chromecast, 0x02);
    }

    [Fact]
    public void ActiveBeforeReleaseWaitsThenRecoversPersistentStream()
    {
        var h = new Harness();
        h.BeginChromecastPress();
        h.Capture.Emit(VoiceCaptureState.Active);
        Assert.Empty(h.OpenRequests);

        h.Advance(0.18);
        h.EndChromecastPress();

        Assert.Equal(1, h.TriggerTapCount);
        Assert.Single(h.OpenRequests);
        Assert.Equal((VoiceRemoteId.Chromecast, true), h.OpenRequests[0]);
        Assert.Equal("open", h.Coordinator.DebugSnapshot.Phase);
    }

    [Fact]
    public void StopBeforeActiveCompletesOpeningWithoutDuplicateMicOpen()
    {
        var h = new Harness();
        h.BeginChromecastPress();
        h.Advance(0.12);
        h.EndChromecastPress();
        Assert.Single(h.OpenRequests);

        h.Capture.Emit(VoiceCaptureState.Active);
        Assert.Single(h.OpenRequests);
        Assert.Equal("open", h.Coordinator.DebugSnapshot.Phase);
    }

    [Fact]
    public void SecondTapClosesAndTriggerEdgesReleaseHeldTrigger()
    {
        var h = new Harness();
        h.BeginChromecastPress();
        h.Advance(0.15);
        h.EndChromecastPress();
        h.Capture.Emit(VoiceCaptureState.Active);
        h.Coordinator.RemoteAudioStarted(VoiceRemoteId.Chromecast, 0x00);

        h.BeginChromecastPress();
        h.Advance(0.16);
        h.EndChromecastPress();

        Assert.Equal([true, false], h.TriggerEdges);
        Assert.Single(h.OpenRequests);
        Assert.Equal("closing", h.Coordinator.DebugSnapshot.Phase);

        h.Capture.Emit(VoiceCaptureState.Inactive);
        Assert.True(h.ReleaseCount >= 1);

        var timeout = new Harness();
        timeout.BeginChromecastPress();
        timeout.Advance(0.15);
        timeout.EndChromecastPress();
        timeout.Coordinator.ForceClose();
        Assert.Equal([true], timeout.TriggerEdges);
        Assert.True(timeout.ReleaseCount >= 1);
    }

    [Fact]
    public void LongPressClosesWithoutPersistentReopen()
    {
        var h = new Harness();
        h.BeginChromecastPress();
        h.Capture.Emit(VoiceCaptureState.Active);
        h.Advance(0.70);
        h.EndChromecastPress();

        Assert.Equal(2, h.TriggerTapCount);
        Assert.Empty(h.OpenRequests);
        Assert.Equal("closing", h.Coordinator.DebugSnapshot.Phase);
    }

    [Fact]
    public void KeyboardOpenRemoteCloseAndCrossClose()
    {
        var h = new Harness();
        h.Coordinator.TriggerDownObserved(false);
        h.Coordinator.TriggerUpObserved(false);
        Assert.Equal(0, h.TriggerTapCount);
        Assert.Equal(VoiceRemoteId.Chromecast, h.OpenRequests[0].Remote);

        h.Capture.Emit(VoiceCaptureState.Active);
        h.Coordinator.RemoteAudioStarted(VoiceRemoteId.Chromecast, 0x00);
        h.BeginChromecastPress();
        h.Advance(0.14);
        h.EndChromecastPress();
        Assert.Equal(1, h.TriggerTapCount);
        Assert.Equal("closing", h.Coordinator.DebugSnapshot.Phase);

        var h2 = new Harness();
        h2.BeginChromecastPress();
        h2.Advance(0.16);
        h2.EndChromecastPress();
        h2.Capture.Emit(VoiceCaptureState.Active);
        h2.Coordinator.RemoteAudioStarted(VoiceRemoteId.Chromecast, 0x00);
        h2.Coordinator.TriggerDownObserved(false);
        h2.Coordinator.TriggerUpObserved(false);
        h2.Capture.Emit(VoiceCaptureState.Active);
        h2.Advance(0.22);
        Assert.Equal(1, h2.TriggerTapCount);
        Assert.Single(h2.OpenRequests);
        Assert.Equal("closing", h2.Coordinator.DebugSnapshot.Phase);
        h2.Capture.Emit(VoiceCaptureState.Inactive);
        Assert.Equal("closed", h2.Coordinator.DebugSnapshot.Phase);
    }

    [Fact]
    public void X6ReasonZeroOpensWhileChromecastReasonZeroWhileClosedIsIgnored()
    {
        var x6 = new Harness();
        x6.Coordinator.RemoteAudioStarted(VoiceRemoteId.X6, 0x00);
        Assert.Equal(1, x6.TriggerTapCount);
        Assert.Equal("opening", x6.Coordinator.DebugSnapshot.Phase);
        Assert.Equal(new HashSet<VoiceRemoteId> { VoiceRemoteId.X6 }, x6.Coordinator.DebugSnapshot.StreamingRemotes);

        var cc = new Harness();
        cc.Coordinator.RemoteAudioStarted(VoiceRemoteId.Chromecast, 0x00);
        Assert.Equal(0, cc.TriggerTapCount);
        Assert.Equal("closed", cc.Coordinator.DebugSnapshot.Phase);
        Assert.Empty(cc.Coordinator.DebugSnapshot.StreamingRemotes);
    }

    [Fact]
    public void IndependentRemoteStopsAndDebouncedRetry()
    {
        var h = new Harness();
        h.Coordinator.RemoteAudioStarted(VoiceRemoteId.X6, 0x00);
        h.BeginChromecastPress();
        Assert.Equal(1, h.TriggerTapCount);
        Assert.Equal(new HashSet<VoiceRemoteId> { VoiceRemoteId.X6, VoiceRemoteId.Chromecast }, h.Coordinator.DebugSnapshot.StreamingRemotes);
        h.Coordinator.RemoteAudioStopped(VoiceRemoteId.X6, 0x00);
        Assert.Equal(new HashSet<VoiceRemoteId> { VoiceRemoteId.Chromecast }, h.Coordinator.DebugSnapshot.StreamingRemotes);

        var retry = new Harness();
        retry.OpenResults.Enqueue(RemoteMicrophoneOpenResult.RetryAfter(0.02));
        retry.OpenResults.Enqueue(RemoteMicrophoneOpenResult.Sent);
        retry.BeginChromecastPress();
        retry.Advance(0.13);
        retry.EndChromecastPress();
        Assert.Null(retry.Coordinator.DebugSnapshot.OpenRequestedRemote);
        retry.Advance(0.08);
        Assert.Equal(2, retry.OpenRequests.Count);
        Assert.Equal(VoiceRemoteId.Chromecast, retry.Coordinator.DebugSnapshot.OpenRequestedRemote);
    }

    [Fact]
    public void SilentAckReopensAndFirstFrameKeepsStream()
    {
        var silent = new Harness();
        silent.BeginChromecastPress();
        silent.Advance(0.09);
        silent.EndChromecastPress();
        silent.Capture.Emit(VoiceCaptureState.Active);
        silent.Coordinator.RemoteAudioStarted(VoiceRemoteId.Chromecast, 0x00);
        Assert.Empty(silent.RemoteCloseRequests);
        silent.Advance(0.95);
        Assert.Equal([VoiceRemoteId.Chromecast], silent.RemoteCloseRequests);
        Assert.Single(silent.OpenRequests);
        silent.Coordinator.RemoteAudioStopped(VoiceRemoteId.Chromecast, 0x00);
        Assert.Equal(2, silent.OpenRequests.Count);
        Assert.Equal("open", silent.Coordinator.DebugSnapshot.Phase);

        var streaming = new Harness();
        streaming.BeginChromecastPress();
        streaming.Advance(0.15);
        streaming.EndChromecastPress();
        streaming.Capture.Emit(VoiceCaptureState.Active);
        streaming.Coordinator.RemoteAudioStarted(VoiceRemoteId.Chromecast, 0x00);
        streaming.Coordinator.RemoteAudioFrameReceived(VoiceRemoteId.Chromecast);
        streaming.Advance(0.95);
        Assert.Empty(streaming.RemoteCloseRequests);
        Assert.Single(streaming.OpenRequests);
    }

    [Fact]
    public void ObservableTimeoutAndCompatibilityModesWork()
    {
        var observable = new Harness();
        observable.BeginChromecastPress();
        observable.Advance(0.15);
        observable.EndChromecastPress();
        observable.Advance(1.1);
        Assert.Equal("closed", observable.Coordinator.DebugSnapshot.Phase);

        var compatTap = new Harness(compatibilityWithImeRunning: true);
        compatTap.BeginChromecastPress();
        Assert.Equal("open", compatTap.Coordinator.DebugSnapshot.Phase);
        compatTap.Advance(0.15);
        compatTap.EndChromecastPress();
        compatTap.Coordinator.RemoteAudioStarted(VoiceRemoteId.Chromecast, 0x00);
        compatTap.Capture.EmitCurrent();
        compatTap.Advance(1.3);
        Assert.Equal("open", compatTap.Coordinator.DebugSnapshot.Phase);
        compatTap.BeginChromecastPress();
        compatTap.Advance(0.15);
        compatTap.EndChromecastPress();
        Assert.Equal([true, false], compatTap.TriggerEdges);
        compatTap.Advance(0.2);
        Assert.Equal("closed", compatTap.Coordinator.DebugSnapshot.Phase);
        Assert.False(compatTap.RouteChanges[^1]);

        var compatHold = new Harness(compatibilityWithImeRunning: true, InputTriggerMode.Hold);
        compatHold.BeginChromecastPress();
        compatHold.Advance(0.80);
        compatHold.Advance(1.1);
        Assert.Equal("open", compatHold.Coordinator.DebugSnapshot.Phase);
        compatHold.EndChromecastPress();
        Assert.Equal([true, false], compatHold.TriggerEdges);
        compatHold.Advance(0.2);
        Assert.Equal("closed", compatHold.Coordinator.DebugSnapshot.Phase);

        var compatKbHold = new Harness(compatibilityWithImeRunning: true, InputTriggerMode.Hold);
        compatKbHold.Coordinator.TriggerDownObserved(false);
        Assert.Equal("open", compatKbHold.Coordinator.DebugSnapshot.Phase);
        compatKbHold.Coordinator.TriggerUpObserved(false);
        compatKbHold.Advance(0.2);
        Assert.Equal("closed", compatKbHold.Coordinator.DebugSnapshot.Phase);

        var compatStopped = new Harness(compatibilityWithImeRunning: false);
        compatStopped.BeginChromecastPress();
        compatStopped.Advance(0.15);
        compatStopped.EndChromecastPress();
        compatStopped.Advance(1.1);
        Assert.Equal("closed", compatStopped.Coordinator.DebugSnapshot.Phase);
    }
}

