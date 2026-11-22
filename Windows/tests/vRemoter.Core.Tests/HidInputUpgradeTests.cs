using vRemoter.Core.Input;
using Xunit;

namespace vRemoter.Core.Tests;

public sealed class HidInputUpgradeTests
{
    [Theory]
    [InlineData(@"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&000218d1_PID&9450_REV&0100_aabbccddeeff#{4d1e55b2-f16f-11cf-88cb-001111000030}", 0x18D1, 0x9450)]
    [InlineData(@"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&000118d1_PID&9450_REV&0100#{4d1e55b2-f16f-11cf-88cb-001111000030}", 0x18D1, 0x9450)]
    [InlineData(@"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&00021D5A_PID&C081_REV&0001_112233445566\8&12345&0&0010", 0x1D5A, 0xC081)]
    [InlineData(@"\\?\HID#VID_18D1&PID_9450&Col01#7&23456&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 0x18D1, 0x9450)]
    [InlineData(@"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}&VID&0218d1_PID&9450", 0x18D1, 0x9450)]
    public void TryParseVidPid_ParsesAllWindowsBluetoothAndUsbFormats(string path, ushort expectedVid, ushort expectedPid)
    {
        Assert.True(HidDevicePathParser.TryParseVidPid(path, out ushort vid, out ushort pid));
        Assert.Equal(expectedVid, vid);
        Assert.Equal(expectedPid, pid);
    }

    [Fact]
    public void TryParseBluetoothAddress_ExtractsMacFromBthLePath()
    {
        const string path = @"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&000218D1_PID&9450_REV&0100_A1B2C3D4E5F6\8&1a2b3c&0&0010";
        Assert.True(HidDevicePathParser.TryParseBluetoothAddress(path, out ulong mac));
        Assert.Equal(0xA1B2C3D4E5F6UL, mac);
    }

    [Fact]
    public void HidReportMapParser_DecodesChromecastArrayAndBitfieldReports()
    {
        // Consumer Control (0x0C, 0x01) Report ID 1 with 1-based array of Consumer usages:
        // Index 1 = Power (0x30), Index 2 = Menu (0x40), Index 3 = Menu Up (0x42), Index 4 = Menu Down (0x43)
        byte[] descriptor =
        [
            0x05, 0x0C,       // Usage Page (Consumer)
            0x09, 0x01,       // Usage (Consumer Control)
            0xA1, 0x01,       // Collection (Application)
            0x85, 0x01,       //   Report ID (1)
            0x15, 0x01,       //   Logical Minimum (1)
            0x25, 0x04,       //   Logical Maximum (4)
            0x75, 0x08,       //   Report Size (8)
            0x95, 0x02,       //   Report Count (2)
            0x09, 0x30,       //   Usage (Power - index 1)
            0x09, 0x40,       //   Usage (Menu - index 2)
            0x09, 0x42,       //   Usage (Menu Up - index 3)
            0x09, 0x43,       //   Usage (Menu Down - index 4)
            0x81, 0x00,       //   Input (Data, Array, Absolute)
            0xC0              // End Collection
        ];

        var parser = new HidReportMapParser(descriptor);
        Assert.True(parser.HasReportIds);

        // Press index 3 (Menu Up = 0x0042)
        var usages = parser.DecodeActiveUsages(0x01, [0x03, 0x00]);
        Assert.Single(usages);
        Assert.Equal(new ParsedHidUsage(0x000C, 0x0042), usages[0]);

        // Also handles payload that still includes leading Report ID 0x01
        var withPrefix = parser.DecodeActiveUsages(0x01, [0x01, 0x04, 0x00]);
        Assert.Single(withPrefix);
        Assert.Equal(new ParsedHidUsage(0x000C, 0x0043), withPrefix[0]);

        // Release
        Assert.Empty(parser.DecodeActiveUsages(0x01, [0x00, 0x00]));
    }

    [Fact]
    public void HidReportMapParser_DecodesBitfieldVariableReport()
    {
        // Consumer Control (0x0C, 0x01) Report ID 2 with 4-bit bitmap:
        // Bit 0 = Vol Up (0xE9), Bit 1 = Vol Down (0xEA), Bit 2 = Mute (0xE2), Bit 3 = AC Back (0x0224)
        byte[] descriptor =
        [
            0x05, 0x0C,       // Usage Page (Consumer)
            0x09, 0x01,       // Usage (Consumer Control)
            0xA1, 0x01,       // Collection (Application)
            0x85, 0x02,       //   Report ID (2)
            0x15, 0x00,       //   Logical Minimum (0)
            0x25, 0x01,       //   Logical Maximum (1)
            0x75, 0x01,       //   Report Size (1)
            0x95, 0x04,       //   Report Count (4)
            0x09, 0xE9,       //   Usage (Volume Increment)
            0x09, 0xEA,       //   Usage (Volume Decrement)
            0x09, 0xE2,       //   Usage (Mute)
            0x0A, 0x24, 0x02, //   Usage (AC Back)
            0x81, 0x02,       //   Input (Data, Variable, Absolute)
            0x75, 0x04,       //   Report Size (4)
            0x95, 0x01,       //   Report Count (1)
            0x81, 0x03,       //   Input (Constant)
            0xC0              // End Collection
        ];

        var parser = new HidReportMapParser(descriptor);
        var usages = parser.DecodeActiveUsages(0x02, [0x08]); // Bit 3 = AC Back (0x0224)
        Assert.Single(usages);
        Assert.Equal(new ParsedHidUsage(0x000C, 0x0224), usages[0]);
    }

    [Fact]
    public void KeyCorrelationEngine_DistinguishesRemoteFromMainKeyboard()
    {
        long now = 1000;
        var engine = new KeyCorrelationEngine(windowMs: 80, clockMs: () => now);

        // Main PC keyboard presses Up arrow (0x26) -> must return false (pass through)
        engine.RecordRawEvent(0x26, isDown: true, isTargetRemote: false);
        Assert.Equal(false, engine.TryDecideIsRemote(0x26, isDown: true));
        engine.RecordRawEvent(0x26, isDown: false, isTargetRemote: false);
        Assert.Equal(false, engine.TryDecideIsRemote(0x26, isDown: false));

        // Target remote presses Up arrow (0x26) -> must return true (suppress native)
        now += 10;
        engine.RecordRawEvent(0x26, isDown: true, isTargetRemote: true);
        Assert.Equal(true, engine.TryDecideIsRemote(0x26, isDown: true));
    }

    [Fact]
    public void WudfHidTapStubBuilder_BuildsValidX64PageAndDecodesRingEntries()
    {
        byte[] prologue = [0x4C, 0x8B, 0xD1, 0xB8, 0x07, 0x00, 0x00, 0x00, 0xF6, 0x04, 0x25, 0x08, 0x03, 0xFE, 0x7F, 0x01];
        Assert.True(WudfHidTapStubBuilder.TryExtractSyscallId(prologue, out uint syscallId));
        Assert.Equal(0x0007U, syscallId);

        byte[] page = WudfHidTapStubBuilder.BuildHookPage(syscallId, suppressEnabled: true, prologue, out int retOffset);
        Assert.Equal(WudfHidTapStubBuilder.PageSize, page.Length);
        Assert.InRange(retOffset, 150, WudfHidTapStubBuilder.OffMagic - 1);
        Assert.Equal(0xC3, page[retOffset]);

        // Every rel32 conditional jump (0F 84 / 0F 85) inside the stub must target retOffset exactly.
        int jumpCount = 0;
        for (int i = 0; i <= retOffset - 6; i++)
        {
            if (page[i] == 0x0F && (page[i + 1] == 0x84 || page[i + 1] == 0x85))
            {
                int disp = BitConverter.ToInt32(page, i + 2);
                int target = i + 6 + disp;
                if (target == retOffset)
                {
                    jumpCount++;
                }
            }
        }
        Assert.Equal(7, jumpCount);

        var diag = WudfHidTapStubBuilder.ReadDiagnostics(page.AsSpan(WudfHidTapStubBuilder.OffMagic, 0x40));
        Assert.Equal(WudfHidTapStubBuilder.MagicSignature, diag.Magic);
        Assert.Equal(1U, diag.SuppressEnabled);
        Assert.Equal(0x0007U, diag.SyscallId);

        // Verify 8-byte relative jump patch round-trip
        long ntAddr = 0x7FFA_1234_0000L;
        long pageAddr = 0x7FFA_0034_0000L;
        byte[] jmp = WudfHidTapStubBuilder.BuildRelJmp8(ntAddr, pageAddr);
        Assert.Equal(8, jmp.Length);
        Assert.Equal(0xE9, jmp[0]);
        int rel32 = BitConverter.ToInt32(jmp, 1);
        Assert.Equal(pageAddr, ntAddr + 5 + rel32);

        // Verify 3-byte report ring entry decoding ([0x02, 0x9E, 0x01] = Power 0x019E, seq = 0x2A)
        uint packed = 0x02U | (0x9EU << 8) | (0x01U << 16) | (0x2AU << 24);
        var decoded = WudfHidTapStubBuilder.DecodeRingEntry(packed);
        Assert.Equal(0x02, decoded.ReportId);
        Assert.Equal(0x019E, decoded.Usage);
        Assert.Equal(0x2A, decoded.Sequence);
        Assert.Equal(new byte[] { 0x9E, 0x01 }, decoded.ToPayload());
    }
}

