using vRemoter.Core.Localization;

namespace vRemoter.Core.Session;

public enum VoiceRemoteId
{
    X6,
    Chromecast
}

public enum VoiceInputMethodKind
{
    Doubao,
    WeType
}

public enum InputTriggerMode
{
    Toggle,
    Hold
}

public static class InputTriggerModeExtensions
{
    public static string GetTitle(this InputTriggerMode mode) => mode switch
    {
        InputTriggerMode.Toggle => L10n.Text("点按切换", "Tap to toggle"),
        InputTriggerMode.Hold => L10n.Text("按住说话", "Hold to talk"),
        _ => mode.ToString()
    };

    public static string ToStorageValue(this InputTriggerMode mode) =>
        mode == InputTriggerMode.Hold ? "hold" : "toggle";

    public static InputTriggerMode ParseStorageValue(string? raw) =>
        string.Equals(raw, "hold", StringComparison.OrdinalIgnoreCase)
            ? InputTriggerMode.Hold
            : InputTriggerMode.Toggle;
}

public sealed record RemoteMicrophoneOpenResult
{
    public enum ResultKind
    {
        Sent,
        AlreadyStreaming,
        RetryAfter,
        Unavailable,
        Failed
    }

    public ResultKind Kind { get; }
    public TimeSpan Delay { get; }
    public string? ErrorMessage { get; }

    private RemoteMicrophoneOpenResult(ResultKind kind, TimeSpan delay = default, string? errorMessage = null)
    {
        Kind = kind;
        Delay = delay;
        ErrorMessage = errorMessage;
    }

    public static readonly RemoteMicrophoneOpenResult Sent = new(ResultKind.Sent);
    public static readonly RemoteMicrophoneOpenResult AlreadyStreaming = new(ResultKind.AlreadyStreaming);
    public static readonly RemoteMicrophoneOpenResult Unavailable = new(ResultKind.Unavailable);
    public static RemoteMicrophoneOpenResult RetryAfter(TimeSpan delay) => new(ResultKind.RetryAfter, delay);
    public static RemoteMicrophoneOpenResult RetryAfter(double seconds) => RetryAfter(TimeSpan.FromSeconds(seconds));
    public static RemoteMicrophoneOpenResult Failed(string message) => new(ResultKind.Failed, default, message);
}

public enum VoiceCaptureState
{
    Unavailable,
    Inactive,
    Active
}

public sealed record VoiceCaptureSnapshot(
    VoiceCaptureState State,
    int? ProcessId,
    string[] InputDeviceIds,
    string[] InputDeviceNames,
    bool CaptureObservable = true)
{
    public bool IsRecording => State == VoiceCaptureState.Active;

    public string DeviceSummary
    {
        get
        {
            if (!CaptureObservable)
            {
                return L10n.Text("兼容模式", "Compatibility mode");
            }
            return InputDeviceNames.Length == 0
                ? L10n.Text("无", "None")
                : string.Join("、", InputDeviceNames);
        }
    }
}

public interface IVoiceCaptureStateProvider
{
    event Action<VoiceCaptureSnapshot>? SnapshotChanged;
    bool CanObserveRecording { get; }
    void Start();
    void Stop();
    VoiceCaptureSnapshot SnapshotNow();
}

public interface IVoiceSessionScheduler
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

public sealed class ThreadPoolVoiceSessionScheduler : IVoiceSessionScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var effectiveDelay = delay < TimeSpan.FromMilliseconds(1)
            ? TimeSpan.FromMilliseconds(1)
            : delay;
        var cancelled = 0;
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref cancelled, 1) == 0)
            {
                timer?.Dispose();
                callback();
            }
        }, null, effectiveDelay, Timeout.InfiniteTimeSpan);

        return new CancellationTokenHandle(() =>
        {
            if (Interlocked.Exchange(ref cancelled, 1) == 0)
            {
                timer?.Dispose();
            }
        });
    }

    private sealed class CancellationTokenHandle(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

public sealed record VoiceSessionDebugSnapshot(
    string Phase,
    ulong Generation,
    VoiceRemoteId? SessionRemote,
    IReadOnlySet<VoiceRemoteId> StreamingRemotes,
    VoiceRemoteId? OpenRequestedRemote);

