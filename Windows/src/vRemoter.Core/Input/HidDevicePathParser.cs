using System.Globalization;
using System.Text.RegularExpressions;

namespace vRemoter.Core.Input;

/// <summary>
/// Parses Vendor ID, Product ID, and Bluetooth MAC address from Windows
/// RawInput (RIDI_DEVICENAME), SetupDi HID, and BTHLEDevice instance paths.
/// Supports USB/HID style (VID_18D1&amp;PID_9450) and Bluetooth PnP ID style
/// (VID&amp;000218d1_PID&amp;9450 / VID&amp;000118d1_PID&amp;9450 / VID&amp;0218d1_PID&amp;9450).
/// </summary>
public static class HidDevicePathParser
{
    // Matches Bluetooth PnP ID format: VID&000218d1_PID&9450 or VID&0118d1&PID&9450
    // where 0{0,3}[12] is the 1-to-4 hex digit Vendor ID Source (1=Bluetooth SIG, 2=USB IF)
    // immediately followed by the 4-hex-digit Vendor ID.
    private static readonly Regex BtPnpVidPidRegex = new(
        @"VID&0{0,3}[12]([0-9A-Fa-f]{4})[_&]PID[_&]([0-9A-Fa-f]{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches standard USB/HID format: VID_18D1&PID_9450 or VID&18D1_PID&9450
    private static readonly Regex StandardVidPidRegex = new(
        @"VID[_&]([0-9A-Fa-f]{4})[_&]PID[_&]([0-9A-Fa-f]{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Fallback independent VID / PID patterns where VID takes the last 4 hex digits before separator
    private static readonly Regex FallbackVidRegex = new(
        @"VID[_&][0-9A-Fa-f]{0,4}?([0-9A-Fa-f]{4})(?=[_&#\\]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FallbackPidRegex = new(
        @"PID[_&]([0-9A-Fa-f]{4})(?=[_&#\\]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BtMacRegex = new(
        @"(?:_|&|DEV_)([0-9A-Fa-f]{12})(?=[_&#\\]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryParseVidPid(string? pathOrInstanceId, out ushort vid, out ushort pid)
    {
        vid = 0;
        pid = 0;
        if (string.IsNullOrWhiteSpace(pathOrInstanceId))
        {
            return false;
        }

        var btMatch = BtPnpVidPidRegex.Match(pathOrInstanceId);
        if (btMatch.Success &&
            ushort.TryParse(btMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) &&
            ushort.TryParse(btMatch.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid))
        {
            return vid != 0 || pid != 0;
        }

        var stdMatch = StandardVidPidRegex.Match(pathOrInstanceId);
        if (stdMatch.Success &&
            ushort.TryParse(stdMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) &&
            ushort.TryParse(stdMatch.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid))
        {
            return vid != 0 || pid != 0;
        }

        var fbVid = FallbackVidRegex.Match(pathOrInstanceId);
        var fbPid = FallbackPidRegex.Match(pathOrInstanceId);
        if (fbVid.Success && fbPid.Success &&
            ushort.TryParse(fbVid.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) &&
            ushort.TryParse(fbPid.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid))
        {
            return vid != 0 || pid != 0;
        }

        vid = 0;
        pid = 0;
        return false;
    }

    public static bool TryParseBluetoothAddress(string? pathOrInstanceId, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(pathOrInstanceId))
        {
            return false;
        }

        var match = BtMacRegex.Match(pathOrInstanceId);
        if (!match.Success)
        {
            return false;
        }

        return ulong.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address) &&
               address != 0;
    }
}

