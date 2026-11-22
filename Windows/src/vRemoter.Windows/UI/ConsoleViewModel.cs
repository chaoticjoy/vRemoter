using System.ComponentModel;
using System.Runtime.CompilerServices;
using vRemoter.Core.Localization;
using vRemoter.Core.Mapping;
using vRemoter.Core.Session;
using vRemoter.Windows.Audio;
using vRemoter.Windows.Input;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.UI;

public enum ConsolePage
{
    Mixer,
    Mapping,
    Focus
}

public sealed class ConsoleViewModel : INotifyPropertyChanged
{
    private ConsolePage _selectedPage = ConsolePage.Mixer;
    private string _status = L10n.Text("启动中", "Starting");
    private bool _hidConnected;
    private bool _bleConnected;
    private bool _remoteStreaming;
    private bool _pcInputEnabled = AppStorage.Shared.PcInputEnabled;
    private bool _remoteInputEnabled = AppStorage.Shared.RemoteInputEnabled;
    private bool _imeIsRecording;
    private string _imeInput = "--";
    private bool _driverAvailable;
    private string _driverName = string.Empty;
    private double _pcLevelDb = -120.0;
    private double _remoteLevelDb = -120.0;
    private bool _bluetoothReady = true;
    private bool _keyboardHookReady;
    private bool _isElevated = WindowsVersionInfo.IsElevated;
    private bool _x6Connected;
    private bool _chromecastConnected;
    private InputTriggerPreset _triggerPreset = InputTriggerSettings.CurrentPreset;
    private InputTriggerMode _triggerMode = AppStorage.Shared.InputTriggerMode;
    private VoiceInputMethodKind _voiceInputMethod = AppStorage.Shared.VoiceInputMethod;
    private bool _showMicHint = !AppStorage.Shared.GetBool(AppStorage.CompletedMicTestKey, false);

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? StopMicrophoneRequested;
    public event Action? RestartAppRequested;
    public event Action<bool>? RestartAsAdminRequested;
    public event Action<bool>? PcInputEnabledChanged;
    public event Action<bool>? RemoteInputEnabledChanged;
    public event Action? InputTriggerChanged;
    public event Action<VoiceInputMethodKind>? VoiceInputMethodChanged;
    public event Action<SupportedRemoteId, bool>? RemoteMappingEnabledChanged;
    public event Action? RefreshAudioRequested;

    public ConsolePage SelectedPage
    {
        get => _selectedPage;
        set => SetField(ref _selectedPage, value);
    }

    public string Status { get => _status; set => SetField(ref _status, value); }
    public bool HidConnected { get => _hidConnected; set { if (SetField(ref _hidConnected, value)) NotifyDerived(); } }
    public bool BleConnected { get => _bleConnected; set { if (SetField(ref _bleConnected, value)) NotifyDerived(); } }
    public bool RemoteStreaming { get => _remoteStreaming; set { if (SetField(ref _remoteStreaming, value)) NotifyDerived(); } }
    public bool PcInputEnabled { get => _pcInputEnabled; set => SetField(ref _pcInputEnabled, value); }
    public bool RemoteInputEnabled { get => _remoteInputEnabled; set => SetField(ref _remoteInputEnabled, value); }
    public bool ImeIsRecording { get => _imeIsRecording; set { if (SetField(ref _imeIsRecording, value)) NotifyDerived(); } }
    public string ImeInput { get => _imeInput; set { if (SetField(ref _imeInput, value)) NotifyDerived(); } }
    public bool DriverAvailable { get => _driverAvailable; set { if (SetField(ref _driverAvailable, value)) NotifyDerived(); } }
    public string DriverName { get => _driverName; set => SetField(ref _driverName, value); }
    public double PcLevelDb { get => _pcLevelDb; set => SetField(ref _pcLevelDb, value); }
    public double RemoteLevelDb { get => _remoteLevelDb; set => SetField(ref _remoteLevelDb, value); }
    public bool BluetoothReady { get => _bluetoothReady; set { if (SetField(ref _bluetoothReady, value)) NotifyDerived(); } }
    public bool KeyboardHookReady { get => _keyboardHookReady; set { if (SetField(ref _keyboardHookReady, value)) NotifyDerived(); } }
    public bool IsElevated { get => _isElevated; set => SetField(ref _isElevated, value); }
    public bool X6Connected { get => _x6Connected; set => SetField(ref _x6Connected, value); }
    public bool ChromecastConnected { get => _chromecastConnected; set => SetField(ref _chromecastConnected, value); }
    public bool ShowMicHint { get => _showMicHint; set => SetField(ref _showMicHint, value); }

    public InputTriggerPreset TriggerPreset
    {
        get => _triggerPreset;
        set
        {
            if (!SetField(ref _triggerPreset, value)) return;
            Win32SendInput.ReleaseHeldTrigger();
            InputTriggerSettings.CurrentPreset = value;
            InputTriggerChanged?.Invoke();
        }
    }

    public InputTriggerMode TriggerMode
    {
        get => _triggerMode;
        set
        {
            if (!SetField(ref _triggerMode, value)) return;
            Win32SendInput.ReleaseHeldTrigger();
            AppStorage.Shared.InputTriggerMode = value;
        }
    }

    public VoiceInputMethodKind VoiceInputMethod
    {
        get => _voiceInputMethod;
        set
        {
            if (!SetField(ref _voiceInputMethod, value)) return;
            AppStorage.Shared.VoiceInputMethod = value;
            VoiceInputMethodChanged?.Invoke(value);
            NotifyDerived();
        }
    }

    public bool ImeUsesVirtualMic =>
        _imeInput.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
        _imeInput.Contains("vRemoteDr", StringComparison.OrdinalIgnoreCase) ||
        _imeInput.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
        _imeInput.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase);

    public string HeaderStatusText
    {
        get
        {
            if (RemoteStreaming) return L10n.Text("录音中", "Recording");
            if (!DriverAvailable || !KeyboardHookReady) return L10n.Text("需要处理", "Action needed");
            if (HidConnected && BleConnected) return L10n.Text("正在运行", "Running");
            return L10n.Text("正在连接", "Connecting");
        }
    }

    public void SetPcInput(bool enabled)
    {
        PcInputEnabled = enabled;
        PcInputEnabledChanged?.Invoke(enabled);
    }

    public void SetRemoteInput(bool enabled)
    {
        RemoteInputEnabled = enabled;
        RemoteInputEnabledChanged?.Invoke(enabled);
    }

    public void NotifyRemoteMappingEnabled(SupportedRemoteId remote, bool enabled) =>
        RemoteMappingEnabledChanged?.Invoke(remote, enabled);

    public void ObserveRemoteLevel(double db)
    {
        RemoteLevelDb = db;
        if (ShowMicHint && db > -100.0)
        {
            AppStorage.Shared.SetBool(AppStorage.CompletedMicTestKey, true);
            ShowMicHint = false;
        }
    }

    public void RequestStopMicrophone() => StopMicrophoneRequested?.Invoke();
    public void RequestRestartApp() => RestartAppRequested?.Invoke();
    public void RequestRestartAsAdmin() => RestartAsAdminRequested?.Invoke(true);
    public void RequestRefreshAudio() => RefreshAudioRequested?.Invoke();

    public async Task RefreshSystemStatusAsync(LowLevelKeyboardGuard guard)
    {
        KeyboardHookReady = guard.IsHookActive;
        IsElevated = WindowsVersionInfo.IsElevated;
        var (_, radioOn) = await WindowsVersionInfo.CheckBluetoothRadioAsync();
        BluetoothReady = radioOn;
        var route = VirtualAudioDetector.DetectRoute(AppStorage.Shared.PreferredVirtualAudioDevice);
        DriverAvailable = route is not null;
        DriverName = route?.CaptureFriendlyName ?? string.Empty;
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(ImeUsesVirtualMic));
        OnPropertyChanged(nameof(HeaderStatusText));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

