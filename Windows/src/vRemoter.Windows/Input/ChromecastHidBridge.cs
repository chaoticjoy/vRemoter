using vRemoter.Core.Mapping;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Input;

/// <summary>
/// HID bridge for Google Chromecast Voice Remote (VID 0x18D1, PID 0x9450).
/// </summary>
public sealed class ChromecastHidBridge : IDisposable
{
    public const ushort VendorId = 0x18D1;
    public const ushort ProductId = 0x9450;

    private readonly HidCollectionReader _reader;
    private readonly RemoteMappingStore _mappings;
    private readonly LowLevelKeyboardGuard _keyboardGuard;
    private readonly Action<RemoteMappingTarget, RemoteCustomShortcut?, RemoteAppLaunchConfig?, bool> _dispatchAction;

    private bool _remappingEnabled;
    private string? _lastButtonId;

    public event Action<bool>? ConnectionChanged;
    public bool IsConnected => _reader.IsConnected;

    public ChromecastHidBridge(
        RemoteMappingStore mappings,
        RawInputMonitor rawInput,
        LowLevelKeyboardGuard keyboardGuard,
        Action<RemoteMappingTarget, RemoteCustomShortcut?, RemoteAppLaunchConfig?, bool> dispatchAction)
    {
        _mappings = mappings;
        _keyboardGuard = keyboardGuard;
        _dispatchAction = dispatchAction;
        _remappingEnabled = mappings.IsEnabled(SupportedRemoteId.Chromecast);

        _reader = new HidCollectionReader(VendorId, ProductId, "CAST-HID");
        _reader.ConnectionChanged += connected => ConnectionChanged?.Invoke(connected);
        _reader.InputReportReceived += OnHidReport;
        rawInput.HidReportReceived += OnRawHidReport;
        rawInput.DeviceTopologyChanged += _reader.RefreshDevices;
    }

    public void Start() => _reader.Start();
    public void Stop()
    {
        _lastButtonId = null;
        _reader.Stop();
    }

    public void SetRemappingEnabled(bool enabled)
    {
        _remappingEnabled = enabled;
        LogService.Write($"[CAST-HID] remapping={(enabled ? "ON" : "OFF")}");
    }

    private void OnRawHidReport(ushort vid, ushort pid, byte[] raw)
    {
        if (vid != VendorId || pid != ProductId || raw.Length < 2) return;
        // Prefer direct HidCollectionReader when open; fallback only if reader has no stream.
        if (_reader.IsConnected) return;
        OnHidReport(raw[0], raw[1..]);
    }

    private void OnHidReport(byte reportId, byte[] payload)
    {
        if (reportId != 0x01 || payload.Length == 0) return;
        byte usage = payload[0];

        if (usage == 0)
        {
            if (_lastButtonId is null) return;
            string released = _lastButtonId;
            _lastButtonId = null;
            ApplyButton(released, isDown: false);
            return;
        }

        string buttonId = usage.ToString("X2");
        if (_lastButtonId == buttonId) return;
        if (_lastButtonId is not null)
        {
            ApplyButton(_lastButtonId, isDown: false);
        }
        _lastButtonId = buttonId;
        ApplyButton(buttonId, isDown: true);
    }

    private void ApplyButton(string buttonId, bool isDown)
    {
        if (!_remappingEnabled) return;
        var button = RemoteProfiles.ChromecastButtons.FirstOrDefault(b => b.Id == buttonId);
        if (button is null || button.VoiceControlled || !button.Remappable) return;

        var target = _mappings.GetTarget(button, SupportedRemoteId.Chromecast);
        if (TryGetNativeSystemVk(buttonId, out ushort nativeVk) && !MatchesNativeVk(target, nativeVk))
        {
            _keyboardGuard.ArmRemoteKeySuppression(nativeVk);
        }

        var custom = target == RemoteMappingTarget.Custom ? _mappings.GetCustomShortcut(button, SupportedRemoteId.Chromecast) : null;
        var appCfg = target == RemoteMappingTarget.OpenApp ? _mappings.GetAppLaunchConfig(button, SupportedRemoteId.Chromecast) : null;
        _dispatchAction(target, custom, appCfg, isDown);
        LogService.Write($"[CAST-MAP] {button.Title} {(isDown ? "DOWN" : "UP")} -> {_mappings.GetTargetTitle(button, SupportedRemoteId.Chromecast)}");
    }

    private static bool TryGetNativeSystemVk(string buttonId, out ushort vk)
    {
        vk = buttonId switch
        {
            "08" => 0xAD, // Mute
            "0C" => 0xAF, // Volume Up
            "0D" => 0xAE, // Volume Down
            "0A" => 0xAC, // Browser Home
            "0B" => 0xA6, // Browser Back
            _ => 0
        };
        return vk != 0;
    }

    private static bool MatchesNativeVk(RemoteMappingTarget target, ushort vk) => (target, vk) switch
    {
        (RemoteMappingTarget.Mute, 0xAD) => true,
        (RemoteMappingTarget.VolumeUp, 0xAF) => true,
        (RemoteMappingTarget.VolumeDown, 0xAE) => true,
        _ => false
    };

    public void Dispose() => Stop();
}

