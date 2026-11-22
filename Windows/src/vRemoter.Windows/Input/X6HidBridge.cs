using vRemoter.Core.Focus;
using vRemoter.Core.Mapping;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Input;

/// <summary>
/// HID + RawInput bridge for Shenzhen Xingwei X6 BLE remote (VID 0x1D5A, PID 0xC081).
/// Combines Consumer/System/Vendor HID streams with RawInput keyboard tracking for 0xAA.
/// </summary>
public sealed class X6HidBridge : IDisposable
{
    public const ushort VendorId = 0x1D5A;
    public const ushort ProductId = 0xC081;
    private const byte VoiceDataReportId = 0x5A;
    private const ushort VkBrowserSearch = 0xAA;

    private readonly HidCollectionReader _reader;
    private readonly RemoteMappingStore _mappings;
    private readonly LowLevelKeyboardGuard _keyboardGuard;
    private readonly Action<RemoteMappingTarget, RemoteCustomShortcut?, RemoteAppLaunchConfig?, bool> _dispatchAction;

    private bool _remappingEnabled;
    private ushort _lastConsumerUsage;
    private byte _lastSystemUsage;
    private bool _voiceGestureActive;
    private bool _longVoiceKeyActive;
    private bool _longPressConfirmed;
    private Timer? _longPressConfirmTimer;
    private Timer? _deferredReleaseTimer;

    public event Action<bool>? ConnectionChanged;
    public event Action? NativeSearchEdge;
    public event Action? PhysicalVoiceDown;
    public event Action? ShortPress;
    public event Action? LongPressBegan;
    public event Action? LongPressEnded;

    public bool IsConnected => _reader.IsConnected;

    public X6HidBridge(
        RemoteMappingStore mappings,
        RawInputMonitor rawInput,
        LowLevelKeyboardGuard keyboardGuard,
        Action<RemoteMappingTarget, RemoteCustomShortcut?, RemoteAppLaunchConfig?, bool> dispatchAction)
    {
        _mappings = mappings;
        _keyboardGuard = keyboardGuard;
        _dispatchAction = dispatchAction;
        _remappingEnabled = mappings.IsEnabled(SupportedRemoteId.X6);

        _reader = new HidCollectionReader(VendorId, ProductId, "X6-HID");
        _reader.ConnectionChanged += connected => ConnectionChanged?.Invoke(connected);
        _reader.InputReportReceived += OnHidReport;
        rawInput.KeyboardReportReceived += OnRawKeyboard;
        rawInput.DeviceTopologyChanged += _reader.RefreshDevices;
    }

    public void Start() => _reader.Start();

    public void Stop()
    {
        CancelTimers();
        if (_voiceGestureActive)
        {
            bool wasLong = _longPressConfirmed;
            ResetVoiceGestureState();
            if (wasLong) LongPressEnded?.Invoke();
        }
        _lastConsumerUsage = 0;
        _lastSystemUsage = 0;
        _reader.Stop();
    }

    public void SetRemappingEnabled(bool enabled)
    {
        _remappingEnabled = enabled;
        LogService.Write($"[X6-HID] remapping={(enabled ? "ON" : "OFF")}");
    }

    private void OnHidReport(byte reportId, byte[] payload)
    {
        switch (reportId)
        {
            case 0x02:
                HandleConsumerReport(payload);
                break;
            case 0x03:
                HandleSystemReport(payload);
                break;
            case VoiceDataReportId:
                // Vendor voice report diagnostic telemetry available if needed.
                break;
        }
    }

    private void OnRawKeyboard(ushort vid, ushort pid, ushort vkey, bool isDown, ushort makeCode)
    {
        if (vid != VendorId || pid != ProductId) return;

        if (vkey == VkBrowserSearch)
        {
            HandleLongVoiceKeyEdge(isDown);
            return;
        }

        if (!_remappingEnabled) return;
        if (TryMapRawVKeyToX6ButtonId(vkey, out string? buttonId) && buttonId is not null)
        {
            var button = RemoteProfiles.X6Buttons.FirstOrDefault(b => b.Id == buttonId);
            if (button is null || button.VoiceControlled || !button.Remappable) return;

            var target = _mappings.GetTarget(button, SupportedRemoteId.X6);
            // Skip re-dispatch when target equals the native key already delivered by kbdhid.sys.
            if (Win32SendInput.TryGetSimpleVirtualKey(target, out ushort mappedVk) && mappedVk == vkey)
            {
                return;
            }
            if (vkey == 0x5D) // VK_APPS (Menu)
            {
                _keyboardGuard.ArmRemoteKeySuppression(0x5D);
            }
            DispatchButton(button, target, isDown);
        }
    }

    private void HandleConsumerReport(byte[] data)
    {
        if (data.Length < 2) return;
        ushort usage = (ushort)(data[0] | (data[1] << 8));
        if (usage == _lastConsumerUsage) return;

        ushort previous = _lastConsumerUsage;
        _lastConsumerUsage = usage;

        if (_remappingEnabled)
        {
            if (previous != 0 && previous != 0x0221)
            {
                ApplyById("c" + previous.ToString("X"), isDown: false);
            }
            if (usage != 0 && usage != 0x0221)
            {
                ApplyById("c" + usage.ToString("X"), isDown: true);
            }
        }

        if (usage == 0x0221)
        {
            NativeSearchEdge?.Invoke();
            BeginVoiceGestureIfNeeded("consumer-search");
        }
        else if (usage == 0 && _voiceGestureActive && !_longVoiceKeyActive)
        {
            NativeSearchEdge?.Invoke();
            ScheduleDeferredVoiceRelease();
        }
    }

    private void HandleSystemReport(byte[] data)
    {
        if (data.Length == 0) return;
        byte usage = data[0];
        if (usage == _lastSystemUsage) return;

        if (_remappingEnabled && _lastSystemUsage != 0)
        {
            ApplyById("s" + _lastSystemUsage.ToString("X2"), isDown: false);
        }
        _lastSystemUsage = usage;
        if (_remappingEnabled && usage != 0)
        {
            ApplyById("s" + usage.ToString("X2"), isDown: true);
        }
    }

    private void HandleLongVoiceKeyEdge(bool voiceHeld)
    {
        if (voiceHeld == _longVoiceKeyActive) return;
        _longVoiceKeyActive = voiceHeld;

        if (voiceHeld)
        {
            CancelDeferredRelease();
            BeginVoiceGestureIfNeeded("keyboard-0xAA");
            _longPressConfirmed = false;
            _longPressConfirmTimer?.Dispose();
            _longPressConfirmTimer = new Timer(_ =>
            {
                if (!_voiceGestureActive || !_longVoiceKeyActive) return;
                _longPressConfirmed = true;
                LogService.Write("[X6] physical voice HOLD confirmed");
                LongPressBegan?.Invoke();
            }, null, TimeSpan.FromMilliseconds(280), Timeout.InfiniteTimeSpan);
        }
        else if (_voiceGestureActive)
        {
            _longPressConfirmTimer?.Dispose();
            _longPressConfirmTimer = null;
            bool wasLong = _longPressConfirmed;
            ResetVoiceGestureState();
            if (wasLong)
            {
                LogService.Write("[X6] voice LONG UP source=keyboard-0xAA");
                LongPressEnded?.Invoke();
            }
            else
            {
                LogService.Write("[X6] voice UP source=keyboard-0xAA");
                ShortPress?.Invoke();
            }
        }
    }

    private void BeginVoiceGestureIfNeeded(string source)
    {
        if (_voiceGestureActive) return;
        CancelDeferredRelease();
        _voiceGestureActive = true;
        LogService.Write($"[X6] voice DOWN source={source}");
        PhysicalVoiceDown?.Invoke();
    }

    private void ScheduleDeferredVoiceRelease()
    {
        CancelDeferredRelease();
        _deferredReleaseTimer = new Timer(_ =>
        {
            if (_longVoiceKeyActive || !_voiceGestureActive) return;
            ResetVoiceGestureState();
            LogService.Write("[X6] voice UP source=consumer-search");
            ShortPress?.Invoke();
        }, null, TimeSpan.FromMilliseconds(120), Timeout.InfiniteTimeSpan);
    }

    private void ApplyById(string buttonId, bool isDown)
    {
        var button = RemoteProfiles.X6Buttons.FirstOrDefault(b => b.Id == buttonId);
        if (button is null || button.VoiceControlled || !button.Remappable) return;

        var target = _mappings.GetTarget(button, SupportedRemoteId.X6);
        if (TryGetNativeConsumerVk(buttonId, out ushort nativeVk))
        {
            _keyboardGuard.ArmRemoteKeySuppression(nativeVk);
        }
        DispatchButton(button, target, isDown);
    }

    private void DispatchButton(RemoteButtonDefinition button, RemoteMappingTarget target, bool isDown)
    {
        var custom = target == RemoteMappingTarget.Custom ? _mappings.GetCustomShortcut(button, SupportedRemoteId.X6) : null;
        var appCfg = target == RemoteMappingTarget.OpenApp ? _mappings.GetAppLaunchConfig(button, SupportedRemoteId.X6) : null;
        _dispatchAction(target, custom, appCfg, isDown);
        LogService.Write($"[X6-MAP] {button.Title} {(isDown ? "DOWN" : "UP")} -> {_mappings.GetTargetTitle(button, SupportedRemoteId.X6)}");
    }

    private static bool TryMapRawVKeyToX6ButtonId(ushort vkey, out string? buttonId)
    {
        buttonId = vkey switch
        {
            0x08 => "k2A", // Backspace / Delete
            0x5D => "k65", // Menu / Apps
            0x26 => "k52", // Up
            0x28 => "k51", // Down
            0x25 => "k50", // Left
            0x27 => "k4F", // Right
            0x0D => "k28", // Enter / OK
            0x21 => "k4B", // Page Up
            0x22 => "k4E", // Page Down
            _ => null
        };
        return buttonId is not null;
    }

    private static bool TryGetNativeConsumerVk(string buttonId, out ushort vk)
    {
        vk = buttonId switch
        {
            "cE2" => 0xAD,   // Mute
            "c224" => 0xA6,  // Browser Back
            "c196" => 0xAA,  // Browser Search
            "cE9" => 0xAF,   // Volume Up
            "cEA" => 0xAE,   // Volume Down
            "s01" => 0x5F,   // Sleep
            _ => 0
        };
        return vk != 0;
    }

    private void ResetVoiceGestureState()
    {
        _voiceGestureActive = false;
        _longVoiceKeyActive = false;
        _longPressConfirmed = false;
        CancelTimers();
    }

    private void CancelDeferredRelease()
    {
        _deferredReleaseTimer?.Dispose();
        _deferredReleaseTimer = null;
    }

    private void CancelTimers()
    {
        CancelDeferredRelease();
        _longPressConfirmTimer?.Dispose();
        _longPressConfirmTimer = null;
    }

    public void Dispose() => Stop();
}

