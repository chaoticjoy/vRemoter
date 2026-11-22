namespace vRemoter.Core.Session;

/// <summary>
/// Manages cancellable, generation-scoped timers for <see cref="VoiceSessionCoordinator"/>.
/// </summary>
internal sealed class VoiceSessionWatchdogs
{
    private readonly IVoiceSessionScheduler _scheduler;

    private IDisposable? _routeClose;
    private IDisposable? _startVerification;
    private IDisposable? _closeVerification;
    private IDisposable? _openRetry;
    private IDisposable? _openConfirmation;
    private IDisposable? _firstFrame;

    public VoiceRemoteId? FirstFrameRemote { get; private set; }

    public VoiceSessionWatchdogs(IVoiceSessionScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public void ScheduleRouteClose(TimeSpan delay, Action action)
    {
        Cancel(ref _routeClose);
        _routeClose = _scheduler.Schedule(delay, action);
    }

    public void CancelRouteClose() => Cancel(ref _routeClose);

    public void ScheduleStartVerification(TimeSpan delay, Action action)
    {
        Cancel(ref _startVerification);
        _startVerification = _scheduler.Schedule(delay, action);
    }

    public void CancelStartVerification() => Cancel(ref _startVerification);

    public void ScheduleCloseVerification(TimeSpan delay, Action action)
    {
        Cancel(ref _closeVerification);
        _closeVerification = _scheduler.Schedule(delay, action);
    }

    public void CancelCloseVerification() => Cancel(ref _closeVerification);

    public void ScheduleOpenRetry(TimeSpan delay, Action action)
    {
        Cancel(ref _openRetry);
        var effective = delay < TimeSpan.FromMilliseconds(10)
            ? TimeSpan.FromMilliseconds(10)
            : delay;
        _openRetry = _scheduler.Schedule(effective, action);
    }

    public void CancelOpenRetry() => Cancel(ref _openRetry);

    public void ScheduleOpenConfirmation(TimeSpan delay, Action action)
    {
        Cancel(ref _openConfirmation);
        _openConfirmation = _scheduler.Schedule(delay, action);
    }

    public void CancelOpenConfirmation() => Cancel(ref _openConfirmation);

    public void ScheduleFirstFrame(VoiceRemoteId remote, TimeSpan delay, Action action)
    {
        Cancel(ref _firstFrame);
        FirstFrameRemote = remote;
        _firstFrame = _scheduler.Schedule(delay, action);
    }

    public void CancelFirstFrame(VoiceRemoteId? onlyForRemote = null)
    {
        if (onlyForRemote is not null && FirstFrameRemote != onlyForRemote)
        {
            return;
        }
        Cancel(ref _firstFrame);
        FirstFrameRemote = null;
    }

    public void ScheduleReconcile(TimeSpan delay, Action action)
    {
        // Fire-and-forget generation-guarded reconcile callback.
        _scheduler.Schedule(delay, action);
    }

    public void CancelAll()
    {
        Cancel(ref _routeClose);
        Cancel(ref _startVerification);
        Cancel(ref _closeVerification);
        Cancel(ref _openRetry);
        Cancel(ref _openConfirmation);
        CancelFirstFrame();
    }

    private static void Cancel(ref IDisposable? handle)
    {
        handle?.Dispose();
        handle = null;
    }
}

