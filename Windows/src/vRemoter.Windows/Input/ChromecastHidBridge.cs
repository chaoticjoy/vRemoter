using vRemoter.Core.Focus;
using vRemoter.Core.Input;
using vRemoter.Core.Mapping;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Input;

/// <summary>
/// Multi-pipeline input bridge for Google Chromecast Voice Remote (VID 0x18D1, PID 0x9450).
/// Simultaneously accepts reports from:
/// 1. Win32 / WinRT BLE GATT HIDS 0x1812 (0x2A4D Input Reports + 0x2A4B Report Map)
/// 2. RawInputMonitor (WM_INPUT Keyboard + Consumer + RIDI_PREPARSEDDATA HidP_GetUsages)
/// 3. HidCollectionReader (Overlapped ReadFile + HidP_GetUsagesEx / HidP_GetUsages)
/// Deduplicates at the button-state level so no pipeline blocks another.
/// </summary>
public sealed class ChromecastHidBridge : IDisposable
{
    public const ushort VendorId = 0x18D1;
    public const ushort ProductId = 0x9450;

    private readonly HidCollectionReader _reader;
    private readonly RemoteMappingStore _mappings;
    private readonly LowLevelKeyboardGuard _keyboardGuard;
    private readonly Action<RemoteMappingTarget, RemoteCustomShortcut?, RemoteAppLaunchConfig?, bool> _dispatchAction;
    private readonly object _stateSync = new();
    private readonly HashSet<string> _loggedUnmapped = [];

    private bool _remappingEnabled;
    private bool _bleHidsConnected;
    private string? _lastButtonId;

    public event Action<bool>? ConnectionChanged;
    public bool IsConnected => _reader.IsConnected || _bleHidsConnected;

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
        rawInput.HidReportReceived += OnRawHidReport;
        rawInput.KeyboardReportReceived += OnRawKeyboard;
        rawInput.DeviceTopologyChanged += _reader.RefreshDevices;
    }

    public void Start() => _reader.Start();

    public void Stop()
    {
        lock (_stateSync)
        {
            _lastButtonId = null;
            _bleHidsConnected = false;
        }
        _reader.Stop();
    }

    public void SetRemappingEnabled(bool enabled)
    {
        _remappingEnabled = enabled;
        LogService.Write($"[CAST-HID] remapping={(enabled ? "ON" : "OFF")} {_reader.DescribeStreams()} bleHids={_bleHidsConnected}");
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
        foreach (var pair in report.ParsedUsages)
        {
            if (TryMapHidUsageToButtonId(pair.UsagePage, pair.Usage, out string? mapped))
            {
                return mapped;
            }
        }

        byte[] payload = report.Payload;
        if (payload.Length == 0 || IsAllZero(payload)) return null;

        // 1. Direct Chromecast 1-based button index in Report ID 0x01 (matches macOS ChromecastRemoteHIDBridge.swift)
        if ((report.ReportId == 0x01 || report.ReportId == 0x00) &&
            (payload.Length == 1 || payload.Skip(1).All(b => b == 0)))
        {
            string candidate = payload[0].ToString("X2");
            if (RemoteProfiles.ChromecastButtons.Any(b => b.Id == candidate))
            {
                return candidate;
            }
        }

        // 2. Standard 16-bit Consumer Control usage
        if (report.CollectionUsagePage is 0x0C or 0x00)
        {
            ushort consumerUsage = payload.Length >= 2
                ? (ushort)(payload[0] | (payload[1] << 8))
                : payload[0];
            if (TryMapConsumerUsage(consumerUsage, out string? consumerId))
            {
                return consumerId;
            }
        }

        // 3. System Control Power
        if (report.CollectionUsagePage == 0x01 && report.CollectionUsage == 0x80 && payload[0] == 0x01)
        {
            return "01";
        }

        return null;
    }

    private void ApplyButton(string buttonId, bool isDown)
    {
        if (!_remappingEnabled) return;
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

    private static bool IsAllZero(byte[] data)
    {
        foreach (byte b in data)
        {
            if (b != 0) return false;
        }
        return true;
    }

    private static bool TryMapHidUsageToButtonId(ushort usagePage, ushort usage, out string? buttonId)
    {
        buttonId = usagePage switch
        {
            0x0C => TryMapConsumerUsage(usage, out string? cid) ? cid : null,
            0x07 => TryMapKeyboardUsage(usage, out string? kid) ? kid : null,
            0x01 when usage is 0x81 or 0x82 => "01",
            _ => null
        };
        return buttonId is not null;
    }

    private static bool TryMapConsumerUsage(ushort usage, out string? buttonId)
    {
        buttonId = usage switch
        {
            0x0042 => "03", // Menu Up
            0x0043 => "04", // Menu Down
            0x0044 => "05", // Menu Left
            0x0045 => "06", // Menu Right
            0x0041 => "07", // Menu Pick / Select
            0x0040 or 0x0046 or 0x0224 => "0B", // Menu / Escape / AC Back
            0x0223 or 0x009A => "0A", // AC Home / Media Select Home
            0x00E2 => "08", // Mute
            0x00E9 => "0C", // Volume Up
            0x00EA => "0D", // Volume Down
            0x0030 or 0x0031 or 0x0032 => "01", // Power / Reset / Sleep
            0x0077 => "0E", // YouTube
            0x0078 => "0F", // Netflix
            0x006A or 0x0082 or 0x00B8 or 0x019D => "11", // Input / Source
            _ => null
        };
        return buttonId is not null;
    }

    private static bool TryMapKeyboardUsage(ushort usage, out string? buttonId)
    {
        buttonId = usage switch
        {
            0x52 => "03", // Up
            0x51 => "04", // Down
            0x50 => "05", // Left
            0x4F => "06", // Right
            0x28 or 0x58 => "07", // Enter
            0x29 or 0x2A or 0xF1 => "0B", // Esc / Backspace / Back
            0x4A => "0A", // Home
            0x7F => "08", // Mute
            0x80 => "0C", // Vol Up
            0x81 => "0D", // Vol Down
            0x66 => "01", // Power
            _ => null
        };
        return buttonId is not null;
    }

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
