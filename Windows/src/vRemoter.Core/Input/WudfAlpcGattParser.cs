using System.Buffers.Binary;
using vRemoter.Core.Mapping;

namespace vRemoter.Core.Input;

/// <summary>
/// Extracts raw BLE GATT Characteristic ValueChanged IBuffer payloads from WinRT DCOM/ALPC
/// RPC packets arriving inside WUDFHost.exe (Windows 10/11 Microsoft.Bluetooth.Profiles.HidOverGatt.dll)
/// and resolves Chromecast / Consumer / Keyboard HID reports into RemoteButtonDefinition IDs.
/// </summary>
public static class WudfAlpcGattParser
{
    private static ReadOnlySpan<byte> MeowCustomPrefix =>
        [0x4D, 0x45, 0x4F, 0x57, 0x04, 0x00, 0x00, 0x00]; // "MEOW" + OBJREF_CUSTOM

    private static ReadOnlySpan<byte> IBufferAndBufferClsid =>
    [
        // IID_IBuffer {905a0fe0-bc53-11df-8c49-001e4fc686da}
        0xE0, 0x0F, 0x5A, 0x90, 0x53, 0xBC, 0xDF, 0x11, 0x8C, 0x49, 0x00, 0x1E, 0x4F, 0xC6, 0x86, 0xDA,
        // CLSID Windows.Storage.Streams.Buffer
        0x34, 0x93, 0xC2, 0xF6, 0xDC, 0x47, 0x97, 0x43, 0x91, 0x50, 0xF5, 0x49, 0xCF, 0x1D, 0x48, 0x61
    ];

    private static ReadOnlySpan<byte> BtBaseUuidTail =>
        [0x00, 0x00, 0x00, 0x10, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB];

    public static bool TryExtractValueChangedReport(
        in WudfTapAlpcEntry entry,
        out byte[] payload,
        out ushort charUuidShort)
    {
        payload = [];
        charUuidShort = 0;

        if (entry.Direction != 1 || entry.NtStatus != 0 || entry.Payload.Length < 64)
        {
            return false;
        }

        ReadOnlySpan<byte> span = entry.Payload;
        int searchStart = 0;
        while (searchStart <= span.Length - 56)
        {
            int relMeow = span[searchStart..].IndexOf(MeowCustomPrefix);
            if (relMeow < 0)
            {
                return false;
            }

            int meowOffset = searchStart + relMeow;
            int iidOffset = meowOffset + 8;
            if (iidOffset + 48 <= span.Length &&
                span.Slice(iidOffset, IBufferAndBufferClsid.Length).SequenceEqual(IBufferAndBufferClsid))
            {
                uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(iidOffset + 40, 4));
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(iidOffset + 44, 4));
                if (length is >= 1 and <= 64 &&
                    capacity >= length &&
                    iidOffset + 48 + (int)length <= span.Length)
                {
                    if (TryFindPrecedingBtUuid16(span[..meowOffset], out ushort foundUuid) &&
                        foundUuid is not (0x2A4D or 0x2A22 or 0x2A33))
                    {
                        return false; // Ignore non-HID GATT notifications (e.g., 0x2A19 Battery)
                    }

                    charUuidShort = foundUuid != 0 ? foundUuid : (ushort)0x2A4D;
                    payload = span.Slice(iidOffset + 48, (int)length).ToArray();
                    return true;
                }
            }

            searchStart = meowOffset + 4;
        }

        return false;
    }

    private static bool TryFindPrecedingBtUuid16(ReadOnlySpan<byte> prefix, out ushort uuid16)
    {
        uuid16 = 0;
        int minStart = Math.Max(0, prefix.Length - 160);
        int idx = prefix.Length - 16;
        while (idx >= minStart)
        {
            if (prefix[idx + 2] == 0x00 &&
                prefix[idx + 3] == 0x00 &&
                prefix.Slice(idx + 4, BtBaseUuidTail.Length).SequenceEqual(BtBaseUuidTail))
            {
                uuid16 = BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(idx, 2));
                return true;
            }
            idx--;
        }
        return false;
    }
}

public static class ChromecastButtonDecoder
{
    public static string? ResolveButtonId(
        ushort collectionUsagePage,
        ushort collectionUsage,
        byte reportId,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<ParsedHidUsage> parsedUsages)
    {
        foreach (var pair in parsedUsages)
        {
            if (TryMapHidUsageToButtonId(pair.UsagePage, pair.Usage, out string? mapped))
            {
                return mapped;
            }
        }

        if (payload.Length == 0 || IsAllZero(payload)) return null;

        // 1. Direct Chromecast 1-based button index in Report ID 0x01 (e.g. [0x07] or 8-byte [0x07,0,0,0,0,0,0,0])
        if (reportId is 0x01 or 0x00 && IsSingleLeadingByte(payload))
        {
            string candidate = payload[0].ToString("X2");
            if (RemoteProfiles.ChromecastButtons.Any(b => b.Id == candidate))
            {
                return candidate;
            }
        }

        // 2. Standard 16-bit Consumer Control usage (e.g. 0x0041..0x0224, 0x019E, 0x0189)
        if (collectionUsagePage is 0x0C or 0x00)
        {
            ushort consumerUsage = payload.Length >= 2
                ? (ushort)(payload[0] | (payload[1] << 8))
                : payload[0];
            if (TryMapConsumerUsage(consumerUsage, out string? consumerId))
            {
                return consumerId;
            }
        }

        // 3. Standard USB/BLE 8-byte Boot Keyboard report: [modifiers, reserved, usage0, ...]
        if (payload.Length >= 3 && payload[0] == 0x00 && payload[1] == 0x00 && payload[2] != 0x00 &&
            TryMapKeyboardUsage(payload[2], out string? bootKeyId))
        {
            return bootKeyId;
        }

        // 4. System Control Power
        if (collectionUsagePage == 0x01 && collectionUsage == 0x80 && payload[0] == 0x01)
        {
            return "01";
        }

        return null;
    }

    public static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            if (b != 0) return false;
        }
        return true;
    }

    private static bool IsSingleLeadingByte(ReadOnlySpan<byte> payload)
    {
        for (int i = 1; i < payload.Length; i++)
        {
            if (payload[i] != 0) return false;
        }
        return true;
    }

    public static bool TryMapHidUsageToButtonId(ushort usagePage, ushort usage, out string? buttonId)
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

    public static bool TryMapConsumerUsage(ushort usage, out string? buttonId)
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
            0x0030 or 0x0031 or 0x0032 or 0x019E => "01", // Power / Reset / Sleep / AL Terminal Lock
            0x0077 => "0E", // YouTube
            0x0078 => "0F", // Netflix
            0x006A or 0x0082 or 0x00B8 or 0x0189 or 0x019D => "11", // Input / Source
            _ => null
        };
        return buttonId is not null;
    }

    public static bool TryMapKeyboardUsage(ushort usage, out string? buttonId)
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
}
