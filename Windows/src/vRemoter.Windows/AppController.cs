using System.Reflection;
using System.Windows;
using vRemoter.Core.Focus;
using vRemoter.Core.Localization;
using vRemoter.Core.Mapping;
using vRemoter.Core.Session;
using vRemoter.Windows.Audio;
using vRemoter.Windows.Bluetooth;
using vRemoter.Windows.Focus;
using vRemoter.Windows.Input;
using vRemoter.Windows.SystemSupport;
using vRemoter.Windows.UI;
using vRemoter.Windows.UI.Views;

namespace vRemoter.Windows;

/// <summary>
/// Central Windows application controller wiring BLE, HID, RawInput, LLHook,
/// WASAPI audio pipe, IME capture monitor, UIA3 focus, tray, and console UI.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly ConsoleViewModel _vm;
    private readonly RemoteMappingStore _mappingStore;
    private readonly InputFocusStore _focusStore;
    private readonly WasapiAudioPipe _audioPipe;
    private readonly WasapiCaptureMonitor _captureMonitor;
    private readonly PolicyConfigRoute _policyRoute;
    private readonly LowLevelKeyboardGuard _keyboardGuard;
    private readonly RawInputMonitor _rawInput;
    private readonly AppSwitcherSession _appSwitcher;
    private readonly InputFocusCoordinator _focusCoordinator;
    private readonly AppLaunchService _appLaunchService;
    private readonly VoiceSessionCoordinator _session;
    private readonly X6HidBridge _x6Hid;
    private readonly ChromecastHidBridge _chromecastHid;
    private readonly WinRtBleBridge _x6Ble;
    private readonly WinRtBleBridge _chromecastBle;
    private readonly TrayMenuController _tray;
    private readonly MainConsoleWindow _consoleWindow;
    private readonly UpdateWindow _updateWindow;
    private readonly Timer _statusPollTimer;

    private bool _x6HidConnected;
    private bool _x6BleConnected;
    private bool _x6Streaming;
    private bool _chromecastHidConnected;
    private bool _chromecastBleConnected;
    private bool _chromecastStreaming;
    private VoiceRemoteId? _lastVoiceRemote;

    public AppController()
    {
        AppStorage.Shared.Prepare();
        LogService.SetEnabled(AppStorage.Shared.LoggingEnabled);
        string ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.4.0";
        LogService.Write($"[APP] ===== vRemoter Windows {ver} started ({WindowsVersionInfo.FriendlySummary}) =====");
        LaunchAtLogin.RepairPathIfNeeded();

        _vm = new ConsoleViewModel();
        _mappingStore = new RemoteMappingStore(AppStorage.Shared);
        _focusStore = new InputFocusStore();
        _audioPipe = new WasapiAudioPipe();
        _captureMonitor = new WasapiCaptureMonitor();
        _policyRoute = new PolicyConfigRoute();
        _keyboardGuard = new LowLevelKeyboardGuard();
        _rawInput = new RawInputMonitor();
        _appSwitcher = new AppSwitcherSession();
        _focusCoordinator = new InputFocusCoordinator(_focusStore, _keyboardGuard);
        _appLaunchService = new AppLaunchService(_focusCoordinator);

        _session = new VoiceSessionCoordinator(
            _captureMonitor,
            active => _audioPipe.SetRemoteActive(active),
            open =>
            {
                if (open)
                {
                    _focusCoordinator.EnsureFocusedForVoiceIfNeeded();
                    Win32SendInput.BeginRecognition();
                }
                else
                {
                    Win32SendInput.EndRecognition();
                }
            },
            Win32SendInput.ReleaseHeldTrigger,
            () => AppStorage.Shared.InputTriggerMode,
            () =>
            {
                var method = AppStorage.Shared.VoiceInputMethod;
                return (VoiceInputMethodProfiles.GetShortName(method), VoiceInputMethodProfiles.GetTitle(method));
            })
        {
            OnLog = LogService.Write
        };

        _x6Hid = new X6HidBridge(_mappingStore, _rawInput, _keyboardGuard, DispatchMappedAction);
        _chromecastHid = new ChromecastHidBridge(_mappingStore, _rawInput, _keyboardGuard, DispatchMappedAction);
        _x6Ble = new WinRtBleBridge("X6-Remote", "x6-ble.txt", "x6-voice", "X6-BLE", resetSessionOnConnect: true);
        _chromecastBle = new WinRtBleBridge("Chromecast Remote", "chromecast-ble.txt", "chromecast-voice", "CAST-BLE", resetSessionOnConnect: true, enableHidsFallback: true);

        _tray = new TrayMenuController();
        _consoleWindow = new MainConsoleWindow(_vm, _mappingStore, _focusStore, _focusCoordinator);
        _updateWindow = new UpdateWindow();
        _statusPollTimer = new Timer(_ => _ = RefreshSystemDiagnosticsAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        WireEvents();
    }

    public void Start()
    {
        _policyRoute.Apply(AppStorage.Shared.VoiceInputMethod);
        _session.Start();
        _focusCoordinator.Start();
        _rawInput.Start();
        _keyboardGuard.Start();
        _x6Hid.Start();
        _x6Ble.Start();
        _chromecastHid.Start();
        _chromecastBle.Start();
        _audioPipe.Start();

        _statusPollTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(2));
        UpdateStatus();
        ShowConsole();

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            await _updateWindow.CheckAutomaticallyAsync();
        });
    }

    public void ShowConsole()
    {
        _consoleWindow.Show();
        if (_consoleWindow.WindowState == WindowState.Minimized)
        {
            _consoleWindow.WindowState = WindowState.Normal;
        }
        _consoleWindow.Activate();
    }

    private void WireEvents()
    {
        _tray.OpenConsoleRequested += () => Application.Current.Dispatcher.BeginInvoke(ShowConsole);
        _tray.StopMicrophoneRequested += StopMicrophoneNow;
        _tray.RestartRequested += () => RestartAppNow(asAdmin: WindowsVersionInfo.IsElevated);
        _tray.OpenUpdatesRequested += () => Application.Current.Dispatcher.BeginInvoke(() => { _updateWindow.Show(); _updateWindow.Activate(); });
        _tray.RecordingToggled += enabled =>
        {
            _x6Ble.SetRecordingEnabled(enabled);
            _chromecastBle.SetRecordingEnabled(enabled);
        };
        _tray.ClearRecordingsRequested += () => _x6Ble.ClearRecordings();
        _tray.QuitRequested += QuitApplication;

        _vm.StopMicrophoneRequested += StopMicrophoneNow;
        _vm.RestartAppRequested += () => RestartAppNow(asAdmin: WindowsVersionInfo.IsElevated);
        _vm.RestartAsAdminRequested += RestartAppNow;
        _vm.PcInputEnabledChanged += enabled => { _audioPipe.SetInputEnabled(enabled, _audioPipe.IsRemoteInputEnabled); UpdateStatus(); };
        _vm.RemoteInputEnabledChanged += enabled =>
        {
            _audioPipe.SetInputEnabled(_audioPipe.IsPcInputEnabled, enabled);
            if (!enabled)
            {
                _x6Ble.CloseMicrophone(force: true);
                _chromecastBle.CloseMicrophone(force: true);
                _vm.RemoteLevelDb = -120;
            }
            else if (_captureMonitor.SnapshotNow().IsRecording)
            {
                OpenPreferredRemoteMicrophone();
            }
            UpdateStatus();
        };
        _vm.InputTriggerChanged += _keyboardGuard.TriggerConfigurationChanged;
        _vm.VoiceInputMethodChanged += method =>
        {
            LogService.Write($"[VOICE-IME] switched to {method}");
            _policyRoute.Apply(method);
            _captureMonitor.TargetDidChange();
            UpdateStatus();
        };
        _vm.RemoteMappingEnabledChanged += (remote, enabled) =>
        {
            if (remote == SupportedRemoteId.X6) _x6Hid.SetRemappingEnabled(enabled);
            else _chromecastHid.SetRemappingEnabled(enabled);
        };
        _vm.RefreshAudioRequested += () => { _audioPipe.RefreshEndpoints(); _ = RefreshSystemDiagnosticsAsync(); };

        _audioPipe.PcLevelUpdated += db => _vm.PcLevelDb = db;
        _audioPipe.RouteChanged += _ => UpdateStatus();
        _audioPipe.VirtualRouteAvailabilityChanged += route =>
        {
            _vm.DriverAvailable = route is not null;
            _vm.DriverName = route?.CaptureFriendlyName ?? string.Empty;
        };

        _keyboardGuard.TriggerDownObserved += _session.TriggerDownObserved;
        _keyboardGuard.TriggerUpObserved += _session.TriggerUpObserved;

        _x6Hid.ConnectionChanged += connected => { _x6HidConnected = connected; UpdateStatus(); };
        _x6Hid.NativeSearchEdge += () => _keyboardGuard.ArmSearchSuppression();
        _x6Hid.ShortPress += _session.RemoteHidShortPress;
        _x6Hid.LongPressEnded += _session.RemoteHidLongPressEnded;

        _chromecastHid.ConnectionChanged += connected => { _chromecastHidConnected = connected; UpdateStatus(); };
        _chromecastBle.HidsReportReceived += _chromecastHid.OnBleHidsReport;

        WireBleEvents(_x6Ble, VoiceRemoteId.X6, false);
        WireBleEvents(_chromecastBle, VoiceRemoteId.Chromecast, true);

        _session.PreferredRemoteProvider = PreferredVoiceRemote;
        _session.OnMicrophoneCloseRequested = () => { _x6Ble.CloseMicrophone(true); _chromecastBle.CloseMicrophone(true); };
        _session.OnRemoteMicrophoneCloseRequested = remote =>
        {
            if (remote == VoiceRemoteId.X6) _x6Ble.CloseMicrophone(true);
            else _chromecastBle.CloseMicrophone(true);
        };
        _session.OnMicrophoneOpenRequested = (remote, bypass) =>
        {
            if (!_audioPipe.IsRemoteInputEnabled) return RemoteMicrophoneOpenResult.Unavailable;
            return remote == VoiceRemoteId.X6
                ? (_x6BleConnected ? _x6Ble.OpenMicrophone(bypass) : RemoteMicrophoneOpenResult.Unavailable)
                : (_chromecastBleConnected ? _chromecastBle.OpenMicrophone(bypass) : RemoteMicrophoneOpenResult.Unavailable);
        };
        _session.OnStateChanged += _ => UpdateStatus();
        _captureMonitor.SnapshotChanged += _ => UpdateStatus();
    }

    private void WireBleEvents(WinRtBleBridge bridge, VoiceRemoteId remote, bool supportsHoldGesture)
    {
        bridge.ConnectionChanged += connected =>
        {
            if (remote == VoiceRemoteId.X6) _x6BleConnected = connected;
            else _chromecastBleConnected = connected;
            if (!connected)
            {
                if (remote == VoiceRemoteId.X6) _x6Streaming = false;
                else _chromecastStreaming = false;
                _session.RemoteDisconnected(remote);
            }
            UpdateStatus();
        };
        bridge.StreamingChanged += (streaming, _) =>
        {
            if (remote == VoiceRemoteId.X6) _x6Streaming = streaming;
            else _chromecastStreaming = streaming;
            UpdateStatus();
        };
        bridge.AudioStarted += (reason, _) =>
        {
            _lastVoiceRemote = remote;
            _session.RemoteAudioStarted(remote, reason, supportsHoldGesture);
        };
        bridge.AudioStopped += reason => _session.RemoteAudioStopped(remote, reason);
        bridge.FirstAudioFrame += () => _session.RemoteAudioFrameReceived(remote);
        bridge.MicrophoneOpenFailed += code => _session.RemoteMicrophoneOpenFailed(remote, code);
        bridge.PcmDecoded += (samples, sr) => _audioPipe.FeedRemotePcm(samples, sr);
        bridge.LevelUpdated += (db, _) =>
        {
            if (_audioPipe.IsRemoteInputEnabled) _vm.ObserveRemoteLevel(db);
        };
    }

    private void DispatchMappedAction(
        RemoteMappingTarget target,
        RemoteCustomShortcut? custom,
        RemoteAppLaunchConfig? appConfig,
        bool isDown)
    {
        if (_appSwitcher.IsActive && target is RemoteMappingTarget.ArrowLeft or RemoteMappingTarget.ArrowRight)
        {
            if (isDown) _appSwitcher.RedirectArrow(target == RemoteMappingTarget.ArrowRight);
            return;
        }

        Win32SendInput.DispatchMappingTarget(
            target,
            custom,
            isDown,
            onAppSwitcher: _ => _appSwitcher.TapSwitcher(),
            onFocusCurrentInput: _focusCoordinator.FocusFrontmostNow,
            onOpenApp: () => { if (appConfig is not null) _appLaunchService.LaunchOrActivate(appConfig); });
    }

    private VoiceRemoteId? PreferredVoiceRemote()
    {
        if (_lastVoiceRemote == VoiceRemoteId.X6 && _x6BleConnected) return VoiceRemoteId.X6;
        if (_lastVoiceRemote == VoiceRemoteId.Chromecast && _chromecastBleConnected) return VoiceRemoteId.Chromecast;
        if (_x6BleConnected) return VoiceRemoteId.X6;
        if (_chromecastBleConnected) return VoiceRemoteId.Chromecast;
        return null;
    }

    private void OpenPreferredRemoteMicrophone()
    {
        if (PreferredVoiceRemote() is { } remote)
        {
            _ = remote == VoiceRemoteId.X6 ? _x6Ble.OpenMicrophone() : _chromecastBle.OpenMicrophone();
        }
    }

    private void StopMicrophoneNow()
    {
        _session.ForceClose();
        _x6Ble.CloseMicrophone(force: true);
        _chromecastBle.CloseMicrophone(force: true);
        _audioPipe.SetRemoteActive(false);
        UpdateStatus();
    }

    private void RestartAppNow(bool asAdmin)
    {
        StopMicrophoneNow();
        if (LaunchAtLogin.RestartProcess(asAdmin))
        {
            QuitApplication();
        }
    }

    private void QuitApplication()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _consoleWindow.AllowRealClose = true;
            _updateWindow.AllowRealClose = true;
            Application.Current.Shutdown(0);
        });
    }

    private async Task RefreshSystemDiagnosticsAsync()
    {
        await _vm.RefreshSystemStatusAsync(_keyboardGuard);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        bool anyHid = _x6HidConnected || _chromecastHidConnected;
        bool anyBle = _x6BleConnected || _chromecastBleConnected;
        bool streaming = _x6Streaming || _chromecastStreaming;
        var snap = _captureMonitor.SnapshotNow();

        _vm.HidConnected = anyHid;
        _vm.BleConnected = anyBle;
        _vm.RemoteStreaming = streaming;
        _vm.X6Connected = _x6HidConnected || _x6BleConnected;
        _vm.ChromecastConnected = _chromecastHidConnected || _chromecastBleConnected;
        _vm.ImeIsRecording = snap.IsRecording;
        _vm.ImeInput = snap.DeviceSummary;

        string status = (snap.IsRecording && !_vm.ImeUsesVirtualMic)
            ? L10n.Text($"{VoiceInputMethodProfiles.GetShortName(AppStorage.Shared.VoiceInputMethod)}输入设备错误", "Incorrect IME input")
            : streaming
                ? L10n.Text("遥控器录音中", "Remote recording")
                : (anyHid && anyBle)
                    ? L10n.Text("已就绪", "Ready")
                    : (anyHid || anyBle)
                        ? L10n.Text("正在连接语音服务", "Connecting voice service")
                        : L10n.Text("等待遥控器", "Waiting for remote");

        _vm.Status = status;
        _tray.UpdateStatus(status);
    }

    public void Dispose()
    {
        _statusPollTimer.Dispose();
        _x6Hid.Dispose();
        _chromecastHid.Dispose();
        _keyboardGuard.Dispose();
        _rawInput.Dispose();
        _focusCoordinator.Dispose();
        _session.Stop();
        _x6Ble.Dispose();
        _chromecastBle.Dispose();
        _audioPipe.Dispose();
        _captureMonitor.Dispose();
        Win32SendInput.ReleaseHeldTrigger();
        _policyRoute.Restore();
        _appSwitcher.Dispose();
        _tray.Dispose();
    }
}

