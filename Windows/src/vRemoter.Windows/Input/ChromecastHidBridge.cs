using vRemoter.Core.Focus;
using vRemoter.Core.Input;
using vRemoter.Core.Mapping;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Input;

/// <summary>
/// Multi-pipeline input bridge for Google Chromecast Voice Remote (VID 0x18D1, PID 0x9450).
/// Simultaneously accepts reports from:
/// 1. WudfHostHidTap (native x64 IOCTL 0x80018483 3-byte [0x02, usage_lo, usage_hi] interceptor in WUDFHost.exe)
/// 2. Win32 / WinRT BLE GATT HIDS 0x1812 (0x2A4D Input Reports + 0x2A4B Report Map)
/// 3. RawInputMonitor (WM_INPUT Keyboard + Consumer + RIDI_PREPARSEDDATA HidP_GetUsages)
/// 4. HidCollectionReader (Overlapped ReadFile + HidP_GetUsagesEx / HidP_GetUsages)
/// Deduplicates at the button-state level so no pipeline blocks another.
/// </summary>
public sealed class ChromecastHidBridge : IDisposable
{
    public const ushort VendorId = 0x18D1;
    public const ushort ProductId = 0x9450;

    private readonly HidCollectionReader _reader;
    private readonly WudfHostHidTap _wudfTap;
    private readonly RemoteMappingStore _mappings;
    private readonly LowLevelKeyboardGuard _keyboardGuard;
    private readonly Action<RemoteMappingTarget, RemoteCustomShortcut?, RemoteAppLaunchConfig?, bool> _dispatchAction;
    private readonly object _stateSync = new();
    private readonly HashSet<string> _loggedUnmapped = [];

    private bool _remappingEnabled;
    private bool _bleHidsConnected;
    private bool _loggedRemappingOff;
    private string? _lastButtonId;

    public event Action<bool>? ConnectionChanged;
    public event Action? ActivityObserved;
    public bool IsConnected => _reader.IsConnected || _wudfTap.IsConnected || _bleHidsConnected;

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
        _reader.ConnectionChanged += _ => ConnectionChanged?.Invoke(IsConnected);
        _reader.InputReportReceived += OnHidReport;

        _wudfTap = new WudfHostHidTap(VendorId, ProductId, _remappingEnabled, "CAST-HID");
        _wudfTap.ConnectionChanged += _ => ConnectionChanged?.Invoke(IsConnected);
        _wudfTap.ReportReceived += OnWudfHidReport;

        rawInput.HidReportReceived += OnRawHidReport;
        rawInput.KeyboardReportReceived += OnRawKeyboard;
        rawInput.DeviceTopologyChanged += _reader.RefreshDevices;
    }

    public void Start()
    {
        _reader.Start();
        _wudfTap.Start();
    }

    public void Stop()
    {
        lock (_stateSync)
        {
            _lastButtonId = null;
            _bleHidsConnected = false;
        }
        _wudfTap.Stop();
        _reader.Stop();
    }

    public void SetRemappingEnabled(bool enabled)
    {
        _remappingEnabled = enabled;
        _loggedRemappingOff = false;
        _wudfTap.SetSuppressEnabled(enabled);
        LogService.Write($"[CAST-HID] remapping={(enabled ? "ON" : "OFF")} {_reader.DescribeStreams()} wudf={_wudfTap.IsConnected} bleHids={_bleHidsConnected}");
    }

    private void OnWudfHidReport(byte reportId, byte[] payload)
    {
        ActivityObserved?.Invoke();
        byte[] raw = [reportId, .. payload];
        OnHidReport(new HidInputReport(0x000C, 0x0001, reportId, payload, raw, []));
    }

    public void OnBleHidsConnectionChanged(bool connected)
    {
        _bleHidsConnected = connected;
        ConnectionChanged?.Invoke(IsConnected);
    }

    public void OnBleHidsReport(byte reportId, byte[] payload, ParsedHidUsage[] parsedUsages)
    {
        var pairs = parsedUsages.Length > 0
            ? parsedUsages.Select(u => new HidUsagePair(u.UsagePage, u.Usage)).ToArray()
            : [];

        // Strip duplicate leading reportId byte if over-the-air payload already includes it
        byte[] normalizedPayload = payload;
        if (payload.Length >= 2 && payload[0] == reportId &&
            RemoteProfiles.ChromecastButtons.Any(b => b.Id == payload[1].ToString("X2")))
        {
            normalizedPayload = payload[1..];
        }

        byte[] raw = [reportId, .. normalizedPayload];
        OnHidReport(new HidInputReport(0x000C, 0x0001, reportId, normalizedPayload, raw, pairs));
    }

    private void OnRawHidReport(
        ushort vid,
        ushort pid,
        ushort usagePage,
        ushort usage,
        byte[] raw,
        HidUsagePair[] parsedUsages)
    {
        if (vid != VendorId || pid != ProductId || raw.Length == 0) return;

        byte reportId = raw[0];
        byte[] payload = raw.Length > 1 ? raw[1..] : raw;
        OnHidReport(new HidInputReport(usagePage, usage, reportId, payload, raw, parsedUsages));
    }

    private void OnRawKeyboard(ushort vid, ushort pid, ushort vkey, bool isDown, ushort makeCode)
    {
        if (vid != VendorId || pid != ProductId) return;

        if (!TryMapVirtualKeyToButtonId(vkey, out string? buttonId) || buttonId is null)
        {
            bool shouldLog = false;
            if (isDown)
            {
                lock (_stateSync) { shouldLog = _loggedUnmapped.Add($"vk:{vkey:X2}"); }
            }
            if (shouldLog)
            {
                LogService.Write($"[CAST-HID] unmapped keyboard vkey=0x{vkey:X2} make=0x{makeCode:X2}");
            }
            return;
        }

        if (!_remappingEnabled) return;
        var button = RemoteProfiles.ChromecastButtons.FirstOrDefault(b => b.Id == buttonId);
        if (button is null || button.VoiceControlled || !button.Remappable) return;

        var target = _mappings.GetTarget(button, SupportedRemoteId.Chromecast);
        if (Win32SendInput.TryGetSimpleVirtualKey(target, out ushort mappedVk) && mappedVk == vkey)
        {
            return;
        }

        if (isDown)
        {
            _keyboardGuard.ArmRemoteKeySuppression(vkey);
        }

        DispatchMappedButton(button, target, isDown);
    }

    private void OnHidReport(HidInputReport report)
    {
        string? buttonId = ResolveButtonId(report);
        if (buttonId is null)
        {
            if (IsAllZero(report.Payload) && report.ParsedUsages.Length == 0)
            {
                string? released;
                lock (_stateSync)
                {
                    released = _lastButtonId;
                    _lastButtonId = null;
                }
                if (released is not null)
                {
                    ApplyButton(released, isDown: false);
                }
            }
            else
            {
                bool shouldLog;
                lock (_stateSync)
                {
                    shouldLog = _loggedUnmapped.Add($"hid:{report.CollectionUsagePage:X4}:{report.ReportId:X2}:{BitConverter.ToString(report.Payload)}");
                }
                if (shouldLog)
                {
                    LogService.Write($"[CAST-HID] unmapped report UP=0x{report.CollectionUsagePage:X4} id=0x{report.ReportId:X2} hex={BitConverter.ToString(report.Payload)}");
                }
            }
            return;
        }

        string? previous = null;
        lock (_stateSync)
        {
            if (_lastButtonId == buttonId) return;
            previous = _lastButtonId;
            _lastButtonId = buttonId;
        }
        if (previous is not null)
        {
            ApplyButton(previous, isDown: false);
        }
        ApplyButton(buttonId, isDown: true);
    }

    private static string? ResolveButtonId(HidInputReport report)
    {
        ParsedHidUsage[] parsed = report.ParsedUsages.Length > 0
            ? report.ParsedUsages.Select(u => new ParsedHidUsage(u.UsagePage, u.Usage)).ToArray()
            : [];
        return ChromecastButtonDecoder.ResolveButtonId(
            report.CollectionUsagePage,
            report.CollectionUsage,
            report.ReportId,
            report.Payload,
            parsed);
    }

    private void ApplyButton(string buttonId, bool isDown)
    {
        if (!_remappingEnabled)
        {
            if (isDown && !_loggedRemappingOff)
            {
                _loggedRemappingOff = true;
                LogService.Write($"[CAST-HID] button {buttonId} received while remapping=OFF (enable mapping switch in UI)");
            }
            return;
        }
        var button = RemoteProfiles.ChromecastButtons.FirstOrDefault(b => b.Id == buttonId);
        if (button is null || button.VoiceControlled || !button.Remappable) return;

        var target = _mappings.GetTarget(button, SupportedRemoteId.Chromecast);
        if (isDown && TryGetNativeSystemVk(buttonId, out ushort nativeVk) && !MatchesNativeVk(target, nativeVk))
        {
            _keyboardGuard.ArmRemoteKeySuppression(nativeVk);
        }

        DispatchMappedButton(button, target, isDown);
    }

    private void DispatchMappedButton(RemoteButtonDefinition button, RemoteMappingTarget target, bool isDown)
    {
        var custom = target == RemoteMappingTarget.Custom ? _mappings.GetCustomShortcut(button, SupportedRemoteId.Chromecast) : null;
        var appCfg = target == RemoteMappingTarget.OpenApp ? _mappings.GetAppLaunchConfig(button, SupportedRemoteId.Chromecast) : null;
        _dispatchAction(target, custom, appCfg, isDown);
        LogService.Write($"[CAST-MAP] {button.Title} {(isDown ? "DOWN" : "UP")} -> {_mappings.GetTargetTitle(button, SupportedRemoteId.Chromecast)}");
    }

    private static bool IsAllZero(byte[] data) => ChromecastButtonDecoder.IsAllZero(data);

    private static bool TryMapVirtualKeyToButtonId(ushort vkey, out string? buttonId)
    {
        buttonId = vkey switch
        {
            0x26 => "03", // VK_UP
            0x28 => "04", // VK_DOWN
            0x25 => "05", // VK_LEFT
            0x27 => "06", // VK_RIGHT
            0x0D => "07", // VK_RETURN
            0x1B or 0xA6 or 0x08 => "0B", // VK_ESCAPE / VK_BROWSER_BACK / VK_BACK
            0xAC or 0x24 => "0A", // VK_BROWSER_HOME / VK_HOME
            0xAD => "08", // VK_VOLUME_MUTE
            0xAF => "0C", // VK_VOLUME_UP
            0xAE => "0D", // VK_VOLUME_DOWN
            0x5F => "01", // VK_SLEEP
            _ => null
        };
        return buttonId is not null;
    }

    private static bool TryGetNativeSystemVk(string buttonId, out ushort vk)
    {
        vk = buttonId switch
        {
            "03" => 0x26,
            "04" => 0x28,
            "05" => 0x25,
            "06" => 0x27,
            "07" => 0x0D,
            "08" => 0xAD,
            "0C" => 0xAF,
            "0D" => 0xAE,
            "0A" => 0xAC,
            "0B" => 0xA6,
            "01" => 0x5F,
            _ => 0
        };
        return vk != 0;
    }

    private static bool MatchesNativeVk(RemoteMappingTarget target, ushort vk) => (target, vk) switch
    {
        (RemoteMappingTarget.ArrowUp, 0x26) => true,
        (RemoteMappingTarget.ArrowDown, 0x28) => true,
        (RemoteMappingTarget.ArrowLeft, 0x25) => true,
        (RemoteMappingTarget.ArrowRight, 0x27) => true,
        (RemoteMappingTarget.ReturnKey, 0x0D) => true,
        (RemoteMappingTarget.Mute, 0xAD) => true,
        (RemoteMappingTarget.VolumeUp, 0xAF) => true,
        (RemoteMappingTarget.VolumeDown, 0xAE) => true,
        _ => false
    };

    public void Dispose() => Stop();
}
