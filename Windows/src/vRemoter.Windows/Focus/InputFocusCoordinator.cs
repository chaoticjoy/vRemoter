using System.Collections.Concurrent;
using System.IO;
using vRemoter.Core.Focus;
using vRemoter.Core.Localization;
using vRemoter.Windows.Input;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Focus;

/// <summary>
/// Orchestrates Windows UIA3 auto-focus on window switch, post-launch focus,
/// pre-voice focus checks, and 30-second interactive input binding on a dedicated STA worker.
/// </summary>
public sealed class InputFocusCoordinator : IDisposable
{
    private sealed record PendingFocusJob(
        string AppIdentifier,
        DateTimeOffset Deadline,
        long InitialStamp,
        bool AllowSingleFallback,
        bool AllowOverrideOtherEditor,
        bool AllowOverrideNonSearchEditor,
        bool IsPostLaunch)
    {
        public int ScanAttempts { get; set; }
        public int ConfirmAttempts { get; set; }
    }

    private readonly InputFocusStore _store;
    private readonly WinUiaFocusBackend _backend = new();
    private readonly WinEventForegroundHook _fgHook = new();
    private readonly LowLevelKeyboardGuard _keyboardGuard;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _workerThread;
    private readonly Dictionary<string, (int Pid, UiaNode Window, UiaNode Element)> _sessionCache = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _pollTimer;
    private PendingFocusJob? _pendingJob;
    private (string AppId, DateTimeOffset ExpiresAt)? _postLaunchExpected;
    private InputFocusBindingSession<UiaNode>? _bindingSession;
    private bool _started;
    private bool _disposed;

    public InputFocusCoordinator(InputFocusStore store, LowLevelKeyboardGuard keyboardGuard)
    {
        _store = store;
        _keyboardGuard = keyboardGuard;
        _fgHook.ForegroundWindowChanged += (_, _) => OnForegroundChanged();

        _workerThread = new Thread(RunWorkerLoop)
        {
            IsBackground = true,
            Name = "vRemoter-UIA-Worker"
        };
        _workerThread.SetApartmentState(ApartmentState.STA);
        _workerThread.Start();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _fgHook.Start();
        LogService.Write("[INPUT-FOCUS] UIA3 coordinator started");
    }

    public void Stop()
    {
        _started = false;
        _fgHook.Stop();
        Enqueue(() =>
        {
            StopTimer();
            _pendingJob = null;
            _postLaunchExpected = null;
            _bindingSession = null;
        });
    }

    public void RequestPostLaunchFocus(string appIdentifier)
    {
        Enqueue(() =>
        {
            _postLaunchExpected = (appIdentifier, DateTimeOffset.UtcNow.AddSeconds(7.0));
            var front = _backend.FrontmostApp();
            if (front is not null && string.Equals(front.AppIdentifier, appIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                StartFocusJob(
                    appIdentifier,
                    TimeSpan.FromSeconds(5.0),
                    allowSingleFallback: true,
                    allowOverrideOtherEditor: false,
                    _store.GetSnapshot().PullFromOtherEditorsOnSwitch,
                    isPostLaunch: true);
            }
            else
            {
                EnsureTimerRunning();
            }
        });
    }

    public void FocusFrontmostNow()
    {
        Enqueue(() =>
        {
            var front = _backend.FrontmostApp();
            if (front is null) return;

            string selfExe = Path.GetFileName(LaunchAtLogin.ExecutablePath);
            if (string.Equals(front.AppIdentifier, selfExe, StringComparison.OrdinalIgnoreCase))
            {
                _store.SetFeedback(L10n.Text(
                    "当前前台为 vRemoter 本身，请先切换到目标应用",
                    "Frontmost app is vRemoter; switch to target app first"));
                return;
            }

            _store.GetSnapshot().Apps.TryGetValue(front.AppIdentifier, out var entry);
            var status = FocusAttemptHelper.AttemptFocus(
                _backend, _store, _keyboardGuard, _sessionCache,
                front,
                entry,
                _keyboardGuard.InputActivityStamp,
                allowSingleFallback: true,
                allowOverrideOtherEditor: true,
                allowOverrideNonSearchEditor: true,
                updateStoreState: entry is not null);
            LogService.Write($"[INPUT-FOCUS] manual-trigger {front.AppIdentifier} -> {status}");
            _store.SetFeedback($"{front.Name}: {status.ShortDescription()}");
        });
    }

    public void EnsureFocusedForVoiceIfNeeded()
    {
        Enqueue(() =>
        {
            var snap = _store.GetSnapshot();
            if (!snap.FocusBeforeVoiceEnabled) return;
            var front = _backend.FrontmostApp();
            if (front is null || !snap.Apps.TryGetValue(front.AppIdentifier, out var entry) || !entry.Enabled)
            {
                return;
            }

            var status = FocusAttemptHelper.AttemptFocus(
                _backend, _store, _keyboardGuard, _sessionCache,
                front,
                entry,
                _keyboardGuard.InputActivityStamp,
                allowSingleFallback: true,
                allowOverrideOtherEditor: false,
                allowOverrideNonSearchEditor: snap.PullFromOtherEditorsOnSwitch,
                updateStoreState: true);
            LogService.Write($"[INPUT-FOCUS] pre-voice {front.AppIdentifier} -> {status}");
        });
    }

    public void BeginBinding(string appIdentifier, string appName)
    {
        Enqueue(() =>
        {
            _pendingJob = null;
            _bindingSession = new InputFocusBindingSession<UiaNode>(
                appIdentifier,
                appName,
                DateTimeOffset.UtcNow.AddSeconds(30.0));
            _store.SetBindingState(new InputFocusBindingState(
                appIdentifier,
                appName,
                CandidateReady: false,
                FocusCaptureStatus.Waiting,
                SecondsLeft: 30));
            EnsureTimerRunning();
        });
    }

    public void ConfirmBinding()
    {
        Enqueue(() =>
        {
            var session = _bindingSession;
            if (session is null) return;

            if (session.DraftApp is not { } app ||
                session.DraftWindow is not { } window ||
                session.DraftElement is not { } element ||
                !string.Equals(app.AppIdentifier, session.AppIdentifier, StringComparison.OrdinalIgnoreCase) ||
                !InputFocusLocatorBuilder.IsAttached(_backend, element, window))
            {
                _store.SetFeedback(L10n.Text(
                    "尚未捕获到有效输入框，请重试",
                    "No valid input captured yet; please retry"));
                return;
            }

            var locator = InputFocusLocatorBuilder.Describe(_backend, window, element, app.BuildHash);
            _sessionCache[session.AppIdentifier] = (app.ProcessId, window, element);
            _bindingSession = null;
            StopTimerIfIdle();

            _store.SaveLocator(locator, session.AppIdentifier);
            _store.SetBindingState(new InputFocusBindingState());
            LogService.Write($"[INPUT-FOCUS] bound {session.AppIdentifier} locator={locator is not null}");
            _store.SetFeedback(locator is not null
                ? L10n.Text("已保存输入框结构指纹，重启后自动恢复", "Saved input locator; persists across restarts")
                : L10n.Text("本次已绑定输入框（当前会话内有效）", "Input bound for current session"));
        });
    }

    public void CancelBinding()
    {
        Enqueue(() =>
        {
            _bindingSession = null;
            StopTimerIfIdle();
            _store.SetBindingState(new InputFocusBindingState());
        });
    }

    private void OnForegroundChanged()
    {
        Enqueue(() =>
        {
            var front = _backend.FrontmostApp();
            if (front is null) return;

            var snap = _store.GetSnapshot();
            if (_postLaunchExpected is { } expected)
            {
                if (string.Equals(expected.AppId, front.AppIdentifier, StringComparison.OrdinalIgnoreCase) &&
                    DateTimeOffset.UtcNow < expected.ExpiresAt)
                {
                    _postLaunchExpected = null;
                    StartFocusJob(
                        front.AppIdentifier,
                        TimeSpan.FromSeconds(5.0),
                        allowSingleFallback: true,
                        allowOverrideOtherEditor: false,
                        snap.PullFromOtherEditorsOnSwitch,
                        isPostLaunch: true);
                    return;
                }
                if (!string.Equals(expected.AppId, front.AppIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    _postLaunchExpected = null;
                }
            }

            if (!snap.AutoFocusOnSwitchEnabled ||
                !snap.Apps.TryGetValue(front.AppIdentifier, out var entry) ||
                !entry.Enabled)
            {
                _pendingJob = null;
                return;
            }

            StartFocusJob(
                front.AppIdentifier,
                TimeSpan.FromSeconds(3.0),
                allowSingleFallback: true,
                allowOverrideOtherEditor: false,
                snap.PullFromOtherEditorsOnSwitch,
                isPostLaunch: false);
        });
    }

    private void StartFocusJob(
        string appId,
        TimeSpan timeout,
        bool allowSingleFallback,
        bool allowOverrideOtherEditor,
        bool allowOverrideNonSearchEditor,
        bool isPostLaunch)
    {
        if (_bindingSession is not null) return;
        _pendingJob = new PendingFocusJob(
            appId,
            DateTimeOffset.UtcNow + timeout,
            _keyboardGuard.InputActivityStamp,
            allowSingleFallback,
            allowOverrideOtherEditor,
            allowOverrideNonSearchEditor,
            isPostLaunch);
        EnsureTimerRunning();
        TickOnWorker();
    }

    private void EnsureTimerRunning()
    {
        _pollTimer ??= new Timer(_ => Enqueue(TickOnWorker), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    private void StopTimer()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private void StopTimerIfIdle()
    {
        if (_pendingJob is null && _bindingSession is null && _postLaunchExpected is null)
        {
            StopTimer();
        }
    }

    private void TickOnWorker()
    {
        if (_postLaunchExpected is { } expected && DateTimeOffset.UtcNow >= expected.ExpiresAt)
        {
            _postLaunchExpected = null;
        }

        if (_bindingSession is { } session)
        {
            var state = session.Tick(_backend);
            _store.SetBindingState(state);
            if (state.SecondsLeft <= 0)
            {
                _bindingSession = null;
            }
            StopTimerIfIdle();
            return;
        }

        var job = _pendingJob;
        if (job is null)
        {
            StopTimerIfIdle();
            return;
        }

        if (DateTimeOffset.UtcNow >= job.Deadline)
        {
            _pendingJob = null;
            StopTimerIfIdle();
            return;
        }

        var front = _backend.FrontmostApp();
        if (front is null || !string.Equals(front.AppIdentifier, job.AppIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            _pendingJob = null;
            StopTimerIfIdle();
            return;
        }

        if (_keyboardGuard.InputActivityStamp != job.InitialStamp)
        {
            LogService.Write($"[INPUT-FOCUS] switch-focus aborted due to user input on {job.AppIdentifier}");
            _pendingJob = null;
            StopTimerIfIdle();
            if (_store.GetSnapshot().Apps.ContainsKey(job.AppIdentifier))
            {
                _store.UpdateState(InputFocusResultStatus.Changed, job.AppIdentifier);
            }
            return;
        }

        _store.GetSnapshot().Apps.TryGetValue(job.AppIdentifier, out var entry);
        var status = FocusAttemptHelper.AttemptFocus(
            _backend,
            _store,
            _keyboardGuard,
            _sessionCache,
            front,
            entry,
            job.InitialStamp,
            job.AllowSingleFallback,
            job.AllowOverrideOtherEditor,
            job.AllowOverrideNonSearchEditor,
            updateStoreState: entry is not null);

        if (status == InputFocusResultStatus.Confirming)
        {
            job.ConfirmAttempts++;
            if (job.ConfirmAttempts >= 4)
            {
                if (entry is not null) _store.UpdateState(InputFocusResultStatus.Unconfirmed, job.AppIdentifier);
                LogService.Write($"[INPUT-FOCUS] switch-focus {job.AppIdentifier} -> {InputFocusResultStatus.Unconfirmed}");
                _pendingJob = null;
                StopTimerIfIdle();
            }
            return;
        }

        if (status is InputFocusResultStatus.Loading or InputFocusResultStatus.ManualNeeded or
            InputFocusResultStatus.NeedsBinding or InputFocusResultStatus.Rebind or InputFocusResultStatus.Unavailable)
        {
            job.ScanAttempts++;
            int maxAttempts = job.IsPostLaunch ? 16 : 8;
            if (job.ScanAttempts >= maxAttempts || status == InputFocusResultStatus.NeedsBinding)
            {
                LogService.Write($"[INPUT-FOCUS] switch-focus gave up {job.AppIdentifier} -> {status}");
                _pendingJob = null;
                StopTimerIfIdle();
            }
        }
        else
        {
            LogService.Write($"[INPUT-FOCUS] switch-focus {job.AppIdentifier} -> {status}");
            _pendingJob = null;
            StopTimerIfIdle();
        }
    }

    private void Enqueue(Action work)
    {
        if (_disposed) return;
        try { _queue.Add(work); } catch { }
    }

    private void RunWorkerLoop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { LogService.Write($"[INPUT-FOCUS] worker error: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _fgHook.Dispose();
        _queue.CompleteAdding();
    }
}
