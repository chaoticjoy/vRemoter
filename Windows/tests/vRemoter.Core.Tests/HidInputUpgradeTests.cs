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

        byte[] page = WudfHidTapStubBuilder.BuildHookPage(
            syscallId,
            suppressEnabled: true,
            prologue,
            out int retOffset,
            captureAny3Reports: true,
            readSyscallId: 0x0006U,
            alpcSyscallId: 0x007DU,
            writeSyscallId: 0x0008U,
            ioCompSyscallId: 0x0026U);
        Assert.Equal(WudfHidTapStubBuilder.PageSize, page.Length);
        Assert.InRange(retOffset, 150, WudfHidTapStubBuilder.OffReadStub - 1);
        Assert.Equal(0xC3, page[retOffset]);

        // Verify ALPC stub populated and decodable
        Assert.Equal(0xF0, page[WudfHidTapStubBuilder.OffAlpcStub]);
        var slot = new byte[WudfAlpcStubEmitter.SlotSize];
        BitConverter.GetBytes(0x00000000U).CopyTo(slot, 0x00);
        BitConverter.GetBytes(0x00020000U).CopyTo(slot, 0x04);
        BitConverter.GetBytes((ushort)0x1234).CopyTo(slot, 0x08);
        BitConverter.GetBytes((ushort)4).CopyTo(slot, 0x0A);
        BitConverter.GetBytes((ushort)(0x28 + 4)).CopyTo(slot, 0x0C);
        slot[0x0E] = 0x02;
        slot[0x0F] = 0x01;
        slot[0x10] = 0xAA;
        slot[0x11] = 0x02;
        slot[0x12] = 0x42;
        slot[0x13] = 0x00;
        var alpcEntry = WudfAlpcStubEmitter.DecodeAlpcEntry(slot);
        Assert.Equal(0x1234, alpcEntry.PortHandleLo);
        Assert.Equal((byte)1, alpcEntry.Direction);
        Assert.Equal(new byte[] { 0xAA, 0x02, 0x42, 0x00 }, alpcEntry.Payload);

        // Every rel32 conditional jump (0F 84 / 0F 85) inside the stub that targets retOffset must be intact,
        // and total backward jumps from the main path must be 0.
        int jumpCount = 0;
        for (int i = 0; i <= retOffset - 6; i++)
        {
            if ((page[i] == 0x0F && (page[i + 1] == 0x84 || page[i + 1] == 0x85)))
            {
                int disp = BitConverter.ToInt32(page, i + 2);
                int target = i + 6 + disp;
                if (target == retOffset)
                {
                    jumpCount++;
                }
            }
            else if (page[i] == 0xE9)
            {
                int disp = BitConverter.ToInt32(page, i + 1);
                int target = i + 5 + disp;
                if (target == retOffset)
                {
                    jumpCount++;
                }
            }
        }
        Assert.True(jumpCount >= 7, $"Expected >= 7 jumps to ret, got {jumpCount}");

        var diag = WudfHidTapStubBuilder.ReadDiagnostics(page.AsSpan(WudfHidTapStubBuilder.OffMagic, 0x70));
        Assert.Equal(WudfHidTapStubBuilder.MagicSignature, diag.Magic);
        Assert.Equal(1U, diag.SuppressEnabled);
        Assert.Equal(0x0007U, diag.SyscallId);
        Assert.Equal(1U, diag.CaptureAny3Reports);
        Assert.Equal(0UL, diag.ReadCalls);
        Assert.Equal(0UL, diag.AlpcCalls);

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

    [Fact]
    public void WudfAlpcGattParser_DecodesCapturedWin10AlpcPacketsAndMapsButtons()
    {
        // Exact 456-byte port=0x0598 ALPC ReceiveMessage captured from Windows 10 Build 19045 WUDFHost.exe (pid=6900)
        const string selectHex =
            "00-00-00-00-00-00-00-00-01-00-00-00-32-01-00-00-08-00-00-00-03-00-00-00-00-00-00-00-00-00-00-00-" +
            "00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-05-84-00-00-F4-1A-00-00-EF-28-4D-20-ED-1C-CD-B9-" +
            "05-00-07-00-01-00-00-00-00-00-00-00-E6-35-AF-DE-21-2E-CC-4B-9C-35-E5-91-7A-D4-E5-46-00-00-00-00-" +
            "00-00-00-00-00-00-00-00-9E-5F-10-BC-E6-75-00-00-3C-6E-14-BC-E6-75-DD-01-6B-0D-B0-A9-A4-56-EC-41-" +
            "83-4F-07-7E-3D-91-CA-5D-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-" +
            "00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-02-00-" +
            "00-00-00-00-94-00-00-00-00-00-00-00-94-00-00-00-4D-45-4F-57-04-00-00-00-7C-02-8E-58-66-5A-7A-4C-" +
            "8D-B4-29-40-47-36-9C-53-65-1F-22-15-8D-AC-47-4B-9E-31-EC-32-8B-14-AE-E9-00-00-00-00-CA-01-00-00-" +
            "4D-45-4F-57-01-00-00-00-7C-02-8E-58-66-5A-7A-4C-8D-B4-29-40-47-36-9C-53-80-0E-00-00-05-00-00-00-" +
            "BB-E1-A5-BB-A7-4C-EB-7A-2E-6B-1C-6E-C8-4C-CC-84-27-00-00-00-98-1A-FF-FF-C5-2C-FD-C7-39-62-2E-45-" +
            "00-00-00-00-1C-00-00-00-28-00-2B-00-4D-2A-00-00-00-00-00-10-80-00-00-80-5F-9B-34-FB-12-00-00-00-" +
            "28-00-29-00-00-00-02-00-00-00-00-00-40-00-00-00-00-00-00-00-40-00-00-00-4D-45-4F-57-04-00-00-00-" +
            "E0-0F-5A-90-53-BC-DF-11-8C-49-00-1E-4F-C6-86-DA-34-93-C2-F6-DC-47-97-43-91-50-F5-49-CF-1D-48-61-" +
            "00-00-00-00-10-00-00-00-08-00-00-00-08-00-00-00-07-00-00-00-00-00-00-00-00-00-00-00-F8-17-2D-BD-8D-7A-DD-01";
        byte[] selectBytes = Convert.FromHexString(selectHex.Replace("-", string.Empty));
        var entry = new WudfTapAlpcEntry(0, 0, 0x0598, 456, 496, 1, 1, selectBytes);

        Assert.True(WudfAlpcGattParser.TryExtractValueChangedReport(in entry, out byte[] payload, out ushort charUuid));
        Assert.Equal((ushort)0x2A4D, charUuid);
        Assert.Equal(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0 }, payload);
        Assert.Equal("07", ChromecastButtonDecoder.ResolveButtonId(0x000C, 0x0001, 0x01, payload, []));

        // Verify all observed button IDs (Up=03, Left=05, Right=06, Select=07, Back=0B, Vol+=0C, Vol-=0D, Release=00)
        byte[] testPayload = (byte[])payload.Clone();
        foreach (string expectedId in new[] { "01", "03", "04", "05", "06", "07", "08", "0A", "0B", "0C", "0D", "0E", "0F", "11" })
        {
            testPayload[0] = Convert.ToByte(expectedId, 16);
            Assert.Equal(expectedId, ChromecastButtonDecoder.ResolveButtonId(0x000C, 0x0001, 0x01, testPayload, []));
        }

        // Release packet -> null button ID + IsAllZero == true
        testPayload[0] = 0x00;
        Assert.Null(ChromecastButtonDecoder.ResolveButtonId(0x000C, 0x0001, 0x01, testPayload, []));
        Assert.True(ChromecastButtonDecoder.IsAllZero(testPayload));

        // Consumer usage fallback (Power 0x019E -> "01", Input 0x0189 -> "11")
        Assert.Equal("01", ChromecastButtonDecoder.ResolveButtonId(0x000C, 0x0001, 0x02, [0x9E, 0x01], []));
        Assert.Equal("11", ChromecastButtonDecoder.ResolveButtonId(0x000C, 0x0001, 0x02, [0x89, 0x01], []));

        // Non-HID ALPC timer message (port 0x0258, 88 bytes) must be ignored
        var timerEntry = new WudfTapAlpcEntry(0, 0, 0x0258, 88, 128, 1, 1, new byte[88]);
        Assert.False(WudfAlpcGattParser.TryExtractValueChangedReport(in timerEntry, out _, out _));
    }
}

