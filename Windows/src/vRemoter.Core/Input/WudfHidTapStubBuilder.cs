using System.Buffers.Binary;

namespace vRemoter.Core.Input;

public readonly record struct WudfTapReport(byte ReportId, byte UsageLo, byte UsageHi, byte Sequence)
{
    public ushort Usage => (ushort)(UsageLo | (UsageHi << 8));
    public byte[] ToPayload() => [UsageLo, UsageHi];
    public byte[] ToRawPacket() => [ReportId, UsageLo, UsageHi];
}

public readonly record struct WudfTapTraceEntry(uint IoControlCode, uint NtStatus, uint OutLen, uint SampleBytes);

public readonly record struct WudfTapDiagnostics(
    ulong Magic,
    ulong TotalCalls,
    ulong IoctlCalls,
    ulong Report3Calls,
    uint WriteSeq,
    uint CommittedSeq,
    uint LastOutLen,
    uint SuppressEnabled,
    uint LastNtStatus,
    uint SyscallId,
    uint LastIoctlCode,
    uint LastMatchedIoctlCode,
    uint TraceSeq,
    uint CaptureAny3Reports);

/// <summary>
/// Builds the position-independent 4KB x64 page and decodes the lock-free ring buffers
/// used by WudfHostHidTap to intercept IOCTL 0x80018483 (and fallback 3-byte HOGP reports)
/// inside WUDFHost.exe on Windows 10/11.
/// </summary>
public static class WudfHidTapStubBuilder
{
    public const int PageSize = 4096;
    public const uint ReadIoctlCode = 0x80018483;
    public const ulong MagicSignature = 0x334644574D455256UL; // "VREMWDF3"

    public const int OffCode = 0x000;
    public const int OffMagic = 0x200;
    public const int OffTotalCalls = 0x208;
    public const int OffIoctlCalls = 0x210;
    public const int OffReport3Calls = 0x218;
    public const int OffWriteSeq = 0x220;
    public const int OffCommittedSeq = 0x224;
    public const int OffLastOutLen = 0x228;
    public const int OffSuppressEnabled = 0x22C;
    public const int OffLastNtStatus = 0x230;
    public const int OffSyscallId = 0x234;
    public const int OffLastIoctlCode = 0x238;
    public const int OffLastMatchedIoctlCode = 0x23C;
    public const int OffTraceSeq = 0x240;
    public const int OffCaptureAny3Reports = 0x244;
    public const int OffOriginalBytes = 0x248;

    public const int OffRingBuffer = 0x300;
    public const int RingCapacity = 256; // 256 * 4 = 1024 bytes (0x300..0x6FF)

    public const int OffTraceBuffer = 0x700;
    public const int TraceCapacity = 64; // 64 * 16 = 1024 bytes (0x700..0xAFF)

    public static byte[] BuildHookPage(
        uint syscallId,
        bool suppressEnabled,
        ReadOnlySpan<byte> originalEntryBytes,
        bool captureAny3Reports = false)
    {
        return BuildHookPage(syscallId, suppressEnabled, originalEntryBytes, out _, captureAny3Reports);
    }

    public static byte[] BuildHookPage(
        uint syscallId,
        bool suppressEnabled,
        ReadOnlySpan<byte> originalEntryBytes,
        out int retOffset,
        bool captureAny3Reports = false)
    {
        byte[] page = new byte[PageSize];
        int pos = OffCode;
        var retJumps = new List<int>();
        var captureJumps = new List<int>();

        void Emit(params byte[] bytes)
        {
            bytes.CopyTo(page, pos);
            pos += bytes.Length;
        }

        void EmitU32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(pos, 4), value);
            pos += 4;
        }

        void EmitRipRel(int targetOffset, params byte[] opcode)
        {
            opcode.CopyTo(page, pos);
            pos += opcode.Length;
            int disp32 = targetOffset - (pos + 4);
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(pos, 4), disp32);
            pos += 4;
        }

        void EmitJcc(List<int> jumpList, byte nearOpcodeSecondByte)
        {
            page[pos++] = 0x0F;
            page[pos++] = nearOpcodeSecondByte;
            jumpList.Add(pos);
            pos += 4;
        }

        // 1. lock inc qword ptr [rip + OffTotalCalls]
        EmitRipRel(OffTotalCalls, 0xF0, 0x48, 0xFF, 0x05);

        // 2. Execute original NtDeviceIoControlFile syscall: mov r10, rcx; mov eax, syscallId; syscall
        Emit(0x4C, 0x8B, 0xD1);
        Emit(0xB8);
        EmitU32(syscallId);
        Emit(0x0F, 0x05);

        // 3. Always record IoControlCode ([rsp + 0x30]) -> OffLastIoctlCode and check trace mode
        Emit(0x8B, 0x4C, 0x24, 0x30); // mov ecx, dword ptr [rsp + 0x30]
        EmitRipRel(OffLastIoctlCode, 0x89, 0x0D);
        EmitRipRel(OffLastNtStatus, 0x89, 0x05);
        Emit(0x8B, 0x54, 0x24, 0x50); // mov edx, dword ptr [rsp + 0x50] (OutLen)
        EmitRipRel(OffLastOutLen, 0x89, 0x15);

        // 4. If OffCaptureAny3Reports != 0 (HidOverGatt host), write 16-byte entry into OffTraceBuffer[0..63]
        EmitRipRel(OffCaptureAny3Reports, 0x44, 0x8B, 0x1D); // mov r11d, dword ptr [rip + OffCaptureAny3Reports]
        Emit(0x45, 0x85, 0xDB); // test r11d, r11d
        int skipTraceJumpPos;
        page[pos++] = 0x0F;
        page[pos++] = 0x84; // jz skipTrace
        skipTraceJumpPos = pos;
        pos += 4;

        // Reserve trace slot in r10d = lock xadd(OffTraceSeq, 1) & 63
        Emit(0x41, 0xBA, 0x01, 0x00, 0x00, 0x00); // mov r10d, 1
        EmitRipRel(OffTraceSeq, 0xF0, 0x44, 0x0F, 0xC1, 0x15); // lock xadd [rip+OffTraceSeq], r10d
        Emit(0x41, 0x83, 0xE2, 0x3F);             // and r10d, 63
        Emit(0x49, 0xC1, 0xE2, 0x04);             // shl r10, 4 (slot * 16)
        EmitRipRel(OffTraceBuffer, 0x4C, 0x8D, 0x0D); // lea r9, [rip + OffTraceBuffer]
        Emit(0x4D, 0x01, 0xCA);                   // add r10, r9
        Emit(0x41, 0x89, 0x0A);                   // mov dword ptr [r10], ecx (IoControlCode)
        Emit(0x41, 0x89, 0x42, 0x04);             // mov dword ptr [r10 + 4], eax (NtStatus)
        Emit(0x41, 0x89, 0x52, 0x08);             // mov dword ptr [r10 + 8], edx (OutLen)
        Emit(0x41, 0xC7, 0x42, 0x0C, 0, 0, 0, 0); // mov dword ptr [r10 + 12], 0

        // If eax == 0 && OutBuf != null && edx in 1..16, copy min(edx, 4) bytes safely into [r10 + 12]
        Emit(0x85, 0xC0);                         // test eax, eax
        int skipSample1 = EmitPlaceholderJnz(page, ref pos);
        Emit(0x85, 0xD2);                         // test edx, edx
        int skipSample2 = EmitPlaceholderJz(page, ref pos);
        Emit(0x83, 0xFA, 0x10);                   // cmp edx, 16
        int skipSample3 = EmitPlaceholderJa(page, ref pos);
        Emit(0x4C, 0x8B, 0x44, 0x24, 0x48);       // mov r8, [rsp + 0x48]
        Emit(0x4D, 0x85, 0xC0);                   // test r8, r8
        int skipSample4 = EmitPlaceholderJz(page, ref pos);
        Emit(0x45, 0x0F, 0xB6, 0x08);             // movzx r9d, byte ptr [r8]
        Emit(0x83, 0xFA, 0x01);                   // cmp edx, 1
        int storeSample1 = EmitPlaceholderJe(page, ref pos);
        Emit(0x45, 0x0F, 0xB6, 0x58, 0x01);       // movzx r11d, byte ptr [r8 + 1]
        Emit(0x41, 0xC1, 0xE3, 0x08);             // shl r11d, 8
        Emit(0x45, 0x09, 0xD9);                   // or r9d, r11d
        Emit(0x83, 0xFA, 0x02);                   // cmp edx, 2
        int storeSample2 = EmitPlaceholderJe(page, ref pos);
        Emit(0x45, 0x0F, 0xB6, 0x58, 0x02);       // movzx r11d, byte ptr [r8 + 2]
        Emit(0x41, 0xC1, 0xE3, 0x10);             // shl r11d, 16
        Emit(0x45, 0x09, 0xD9);                   // or r9d, r11d
        int storeSampleOff = pos;
        PatchRel32(page, storeSample1, storeSampleOff);
        PatchRel32(page, storeSample2, storeSampleOff);
        Emit(0x45, 0x89, 0x4A, 0x0C);             // mov dword ptr [r10 + 12], r9d

        int traceEnd = pos;
        PatchRel32(page, skipTraceJumpPos, traceEnd);
        PatchRel32(page, skipSample1, traceEnd);
        PatchRel32(page, skipSample2, traceEnd);
        PatchRel32(page, skipSample3, traceEnd);
        PatchRel32(page, skipSample4, traceEnd);

        // 5. Fast match: IoControlCode == 0x80018483?
        Emit(0x81, 0xF9);
        EmitU32(ReadIoctlCode);
        EmitJcc(captureJumps, 0x84); // je .check_report

        // Fallback match for HidOverGatt host (if another IOCTL carries a 3-byte [0x02, ..] or [0x01, ..] report)
        EmitRipRel(OffCaptureAny3Reports, 0x44, 0x8B, 0x1D); // mov r11d, dword ptr [rip + OffCaptureAny3Reports]
        Emit(0x45, 0x85, 0xDB); // test r11d, r11d
        EmitJcc(retJumps, 0x84); // jz .ret
        Emit(0x85, 0xC0);        // test eax, eax
        EmitJcc(retJumps, 0x85); // jnz .ret
        Emit(0x83, 0xFA, 0x03);  // cmp edx, 3
        EmitJcc(retJumps, 0x85); // jne .ret
        Emit(0x4C, 0x8B, 0x44, 0x24, 0x48); // mov r8, [rsp + 0x48]
        Emit(0x4D, 0x85, 0xC0);  // test r8, r8
        EmitJcc(retJumps, 0x84); // jz .ret
        Emit(0x41, 0x80, 0x38, 0x02); // cmp byte ptr [r8], 0x02
        EmitJcc(captureJumps, 0x84);  // je .check_report
        Emit(0x41, 0x80, 0x38, 0x01); // cmp byte ptr [r8], 0x01
        EmitJcc(captureJumps, 0x84);  // je .check_report
        page[pos++] = 0xE9;           // jmp .ret
        retJumps.Add(pos);
        pos += 4;

        // .check_report:
        int captureOffset = pos;
        foreach (int j in captureJumps)
        {
            PatchRel32(page, j, captureOffset);
        }

        // Increment OffIoctlCalls, record OffLastMatchedIoctlCode, require eax == 0 and OutLen (edx) == 3
        EmitRipRel(OffIoctlCalls, 0xF0, 0x48, 0xFF, 0x05);
        EmitRipRel(OffLastMatchedIoctlCode, 0x89, 0x0D);
        Emit(0x85, 0xC0);       // test eax, eax
        EmitJcc(retJumps, 0x85); // jnz .ret
        Emit(0x83, 0xFA, 0x03); // cmp edx, 3
        EmitJcc(retJumps, 0x85); // jne .ret

        // r8 = OutputBuffer ([rsp + 0x48])
        Emit(0x4C, 0x8B, 0x44, 0x24, 0x48);
        Emit(0x4D, 0x85, 0xC0); // test r8, r8
        EmitJcc(retJumps, 0x84); // jz .ret

        // Read 3 bytes: r9d = byte0 | (byte1 << 8) | (byte2 << 16)
        Emit(0x45, 0x0F, 0xB7, 0x48, 0x01); // movzx r9d, word ptr [r8 + 1]
        Emit(0x41, 0xC1, 0xE1, 0x08);       // shl r9d, 8
        Emit(0x41, 0x0F, 0xB6, 0x10);       // movzx edx, byte ptr [r8]
        Emit(0x41, 0x09, 0xD1);             // or r9d, edx

        // Increment OffReport3Calls and reserve ring slot
        EmitRipRel(OffReport3Calls, 0xF0, 0x48, 0xFF, 0x05);
        Emit(0xB9, 0x01, 0x00, 0x00, 0x00);
        EmitRipRel(OffWriteSeq, 0xF0, 0x0F, 0xC1, 0x0D);

        // edx = ecx & 0xFF; pack sequence byte into bits 24..31 of r9d
        Emit(0x89, 0xCA);
        Emit(0x81, 0xE2, 0xFF, 0x00, 0x00, 0x00);
        Emit(0x41, 0x89, 0xD2);             // mov r10d, edx
        Emit(0x41, 0xC1, 0xE2, 0x18);       // shl r10d, 24
        Emit(0x45, 0x09, 0xD1);             // or r9d, r10d

        // RingBuffer[edx] = r9d; lock inc dword ptr [rip + OffCommittedSeq]
        EmitRipRel(OffRingBuffer, 0x4C, 0x8D, 0x15); // lea r10, [rip + OffRingBuffer]
        Emit(0x45, 0x89, 0x0C, 0x92);                // mov dword ptr [r10 + rdx*4], r9d
        EmitRipRel(OffCommittedSeq, 0xF0, 0xFF, 0x05);

        // If reportId == 0x02 && usage != 0 && SuppressEnabled != 0, zero usage bytes in-place
        Emit(0x41, 0x80, 0x38, 0x02);       // cmp byte ptr [r8], 0x02
        EmitJcc(retJumps, 0x85);            // jne .ret
        Emit(0x41, 0x0F, 0xB7, 0x48, 0x01); // movzx ecx, word ptr [r8 + 1]
        Emit(0x85, 0xC9);                   // test ecx, ecx
        EmitJcc(retJumps, 0x84);            // jz .ret
        EmitRipRel(OffSuppressEnabled, 0x8B, 0x0D); // mov ecx, dword ptr [rip + OffSuppressEnabled]
        Emit(0x85, 0xC9);                   // test ecx, ecx
        EmitJcc(retJumps, 0x84);            // jz .ret
        Emit(0x41, 0xC6, 0x40, 0x01, 0x00); // mov byte ptr [r8 + 1], 0
        Emit(0x41, 0xC6, 0x40, 0x02, 0x00); // mov byte ptr [r8 + 2], 0

        retOffset = pos;
        Emit(0xC3); // ret

        if (pos > OffMagic)
        {
            throw new InvalidOperationException($"Hook stub overflowed data header region: {pos} > {OffMagic}");
        }

        foreach (int patchPos in retJumps)
        {
            PatchRel32(page, patchPos, retOffset);
        }

        // Populate data header at OffMagic (0x200)
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(OffMagic, 8), MagicSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(OffSuppressEnabled, 4), suppressEnabled ? 1U : 0U);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(OffSyscallId, 4), syscallId);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(OffCaptureAny3Reports, 4), captureAny3Reports ? 1U : 0U);

        int copyLen = Math.Min(16, originalEntryBytes.Length);
        originalEntryBytes[..copyLen].CopyTo(page.AsSpan(OffOriginalBytes, copyLen));

        return page;
    }

    private static int EmitPlaceholderJnz(byte[] page, ref int pos) => EmitJccRel32(page, ref pos, 0x85);
    private static int EmitPlaceholderJz(byte[] page, ref int pos) => EmitJccRel32(page, ref pos, 0x84);
    private static int EmitPlaceholderJe(byte[] page, ref int pos) => EmitJccRel32(page, ref pos, 0x84);
    private static int EmitPlaceholderJa(byte[] page, ref int pos) => EmitJccRel32(page, ref pos, 0x87);

    private static int EmitJccRel32(byte[] page, ref int pos, byte secondByte)
    {
        page[pos++] = 0x0F;
        page[pos++] = secondByte;
        int fixupPos = pos;
        pos += 4;
        return fixupPos;
    }

    private static void PatchRel32(byte[] page, int dispPos, int targetOffset)
    {
        int disp32 = targetOffset - (dispPos + 4);
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(dispPos, 4), disp32);
    }

    public static bool TryExtractSyscallId(ReadOnlySpan<byte> prologue, out uint syscallId)
    {
        if (prologue.Length >= 8 &&
            prologue[0] == 0x4C &&
            prologue[1] == 0x8B &&
            prologue[2] == 0xD1 &&
            prologue[3] == 0xB8)
        {
            syscallId = BinaryPrimitives.ReadUInt32LittleEndian(prologue.Slice(4, 4));
            return true;
        }
        syscallId = 0;
        return false;
    }

    public static byte[] BuildRelJmp8(long sourceAddress, long targetAddress)
    {
        long rel = targetAddress - (sourceAddress + 5);
        if (rel < int.MinValue || rel > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(targetAddress), "Target is outside ±2GB rel32 range.");
        }

        byte[] patch = [0xE9, 0, 0, 0, 0, 0x90, 0x90, 0x90];
        BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(1, 4), (int)rel);
        return patch;
    }

    public static WudfTapDiagnostics ReadDiagnostics(ReadOnlySpan<byte> headerFrom0x200)
    {
        return new WudfTapDiagnostics(
            BinaryPrimitives.ReadUInt64LittleEndian(headerFrom0x200.Slice(0x00, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(headerFrom0x200.Slice(0x08, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(headerFrom0x200.Slice(0x10, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(headerFrom0x200.Slice(0x18, 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(headerFrom0x200.Slice(0x20, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(headerFrom0x200.Slice(0x24, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(headerFrom0x200.Slice(0x28, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(headerFrom0x200.Slice(0x2C, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(headerFrom0x200.Slice(0x30, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(headerFrom0x200.Slice(0x34, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(headerFrom0x200.Slice(0x38, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(headerFrom0x200.Slice(0x3C, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(headerFrom0x200.Slice(0x40, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(headerFrom0x200.Slice(0x44, 4)));
    }

    public static WudfTapReport DecodeRingEntry(uint packedEntry)
    {
        byte reportId = (byte)(packedEntry & 0xFF);
        byte usageLo = (byte)((packedEntry >> 8) & 0xFF);
        byte usageHi = (byte)((packedEntry >> 16) & 0xFF);
        byte seq = (byte)((packedEntry >> 24) & 0xFF);
        return new WudfTapReport(reportId, usageLo, usageHi, seq);
    }

    public static WudfTapTraceEntry DecodeTraceEntry(ReadOnlySpan<byte> slot16)
    {
        return new WudfTapTraceEntry(
            BinaryPrimitives.ReadUInt32LittleEndian(slot16.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(slot16.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(slot16.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(slot16.Slice(12, 4)));
    }
}
