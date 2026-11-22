using System.Buffers.Binary;

namespace vRemoter.Core.Input;

public readonly record struct WudfTapReport(byte ReportId, byte UsageLo, byte UsageHi, byte Sequence)
{
    public ushort Usage => (ushort)(UsageLo | (UsageHi << 8));
    public byte[] ToPayload() => [UsageLo, UsageHi];
    public byte[] ToRawPacket() => [ReportId, UsageLo, UsageHi];
}

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
    uint SyscallId);

/// <summary>
/// Builds the position-independent 4KB x64 page and decodes the lock-free ring buffer
/// used by WudfHostHidTap to intercept IOCTL 0x80018483 (3-byte HOGP Consumer Control reports
/// [0x02, usage_lo, usage_hi]) inside WUDFHost.exe on Windows 10/11.
/// </summary>
public static class WudfHidTapStubBuilder
{
    public const int PageSize = 4096;
    public const uint ReadIoctlCode = 0x80018483;
    public const ulong MagicSignature = 0x324644574D455256UL; // "VREMWDF2"

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
    public const int OffOriginalBytes = 0x238;
    public const int OffRingBuffer = 0x300;
    public const int RingCapacity = 256;

    public static byte[] BuildHookPage(uint syscallId, bool suppressEnabled, ReadOnlySpan<byte> originalEntryBytes)
    {
        return BuildHookPage(syscallId, suppressEnabled, originalEntryBytes, out _);
    }

    public static byte[] BuildHookPage(
        uint syscallId,
        bool suppressEnabled,
        ReadOnlySpan<byte> originalEntryBytes,
        out int retOffset)
    {
        byte[] page = new byte[PageSize];
        int pos = OffCode;
        var retJumps = new List<int>();

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

        // Near conditional jump rel32 (0F 84 / 0F 85): immune to 127-byte rel8 overflow.
        void EmitJccToRet(byte jccNearSecondByte)
        {
            page[pos++] = 0x0F;
            page[pos++] = jccNearSecondByte;
            retJumps.Add(pos);
            pos += 4;
        }

        // 1. lock inc qword ptr [rip + OffTotalCalls]
        EmitRipRel(OffTotalCalls, 0xF0, 0x48, 0xFF, 0x05);

        // 2. Execute original NtDeviceIoControlFile syscall: mov r10, rcx; mov eax, syscallId; syscall
        Emit(0x4C, 0x8B, 0xD1);
        Emit(0xB8);
        EmitU32(syscallId);
        Emit(0x0F, 0x05);

        // 3. cmp dword ptr [rsp + 0x30], 0x80018483 (IoControlCode)
        Emit(0x81, 0x7C, 0x24, 0x30);
        EmitU32(ReadIoctlCode);
        EmitJccToRet(0x85); // jne .ret

        // 4. Record IOCTL hit, NTSTATUS, and OutputBufferLength ([rsp + 0x50])
        EmitRipRel(OffIoctlCalls, 0xF0, 0x48, 0xFF, 0x05);
        EmitRipRel(OffLastNtStatus, 0x89, 0x05);
        Emit(0x8B, 0x4C, 0x24, 0x50); // mov ecx, dword ptr [rsp + 0x50]
        EmitRipRel(OffLastOutLen, 0x89, 0x0D);

        // 5. Require eax == 0 (STATUS_SUCCESS) and ecx == 3 (3-byte Consumer Control report)
        Emit(0x85, 0xC0);       // test eax, eax
        EmitJccToRet(0x85);     // jnz .ret
        Emit(0x83, 0xF9, 0x03); // cmp ecx, 3
        EmitJccToRet(0x85);     // jne .ret

        // 6. r8 = OutputBuffer ([rsp + 0x48])
        Emit(0x4C, 0x8B, 0x44, 0x24, 0x48);
        Emit(0x4D, 0x85, 0xC0); // test r8, r8
        EmitJccToRet(0x84);     // jz .ret

        // 7. Read 3 bytes: r9d = byte0 | (byte1 << 8) | (byte2 << 16)
        Emit(0x45, 0x0F, 0xB7, 0x48, 0x01); // movzx r9d, word ptr [r8 + 1]
        Emit(0x41, 0xC1, 0xE1, 0x08);       // shl r9d, 8
        Emit(0x41, 0x0F, 0xB6, 0x10);       // movzx edx, byte ptr [r8]
        Emit(0x41, 0x09, 0xD1);             // or r9d, edx

        // 8. lock inc qword ptr [rip + OffReport3Calls]
        EmitRipRel(OffReport3Calls, 0xF0, 0x48, 0xFF, 0x05);

        // 9. Reserve ring slot: ecx = 1; lock xadd dword ptr [rip + OffWriteSeq], ecx
        Emit(0xB9, 0x01, 0x00, 0x00, 0x00);
        EmitRipRel(OffWriteSeq, 0xF0, 0x0F, 0xC1, 0x0D);

        // 10. edx = ecx & 0xFF; pack sequence byte into bits 24..31 of r9d
        Emit(0x89, 0xCA);
        Emit(0x81, 0xE2, 0xFF, 0x00, 0x00, 0x00);
        Emit(0x41, 0x89, 0xD2);             // mov r10d, edx
        Emit(0x41, 0xC1, 0xE2, 0x18);       // shl r10d, 24
        Emit(0x45, 0x09, 0xD1);             // or r9d, r10d

        // 11. RingBuffer[edx] = r9d; lock inc dword ptr [rip + OffCommittedSeq]
        EmitRipRel(OffRingBuffer, 0x4C, 0x8D, 0x15); // lea r10, [rip + OffRingBuffer]
        Emit(0x45, 0x89, 0x0C, 0x92);                // mov dword ptr [r10 + rdx*4], r9d
        EmitRipRel(OffCommittedSeq, 0xF0, 0xFF, 0x05);

        // 12. If reportId == 0x02 && usage != 0 && SuppressEnabled != 0, zero usage bytes in-place
        Emit(0x41, 0x80, 0x38, 0x02);       // cmp byte ptr [r8], 0x02
        EmitJccToRet(0x85);                 // jne .ret
        Emit(0x41, 0x0F, 0xB7, 0x48, 0x01); // movzx ecx, word ptr [r8 + 1]
        Emit(0x85, 0xC9);                   // test ecx, ecx
        EmitJccToRet(0x84);                 // jz .ret
        EmitRipRel(OffSuppressEnabled, 0x8B, 0x0D); // mov ecx, dword ptr [rip + OffSuppressEnabled]
        Emit(0x85, 0xC9);                   // test ecx, ecx
        EmitJccToRet(0x84);                 // jz .ret
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
            int disp32 = retOffset - (patchPos + 4);
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(patchPos, 4), disp32);
        }

        // Populate data header at OffMagic (0x200)
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(OffMagic, 8), MagicSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(OffSuppressEnabled, 4), suppressEnabled ? 1U : 0U);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(OffSyscallId, 4), syscallId);

        int copyLen = Math.Min(16, originalEntryBytes.Length);
        originalEntryBytes[..copyLen].CopyTo(page.AsSpan(OffOriginalBytes, copyLen));

        return page;
    }

    public static bool TryExtractSyscallId(ReadOnlySpan<byte> prologue, out uint syscallId)
    {
        // Standard x64 ntdll syscall stub starts with: 4C 8B D1 (mov r10, rcx) B8 <imm32> (mov eax, id)
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
            BinaryPrimitives.ReadUInt32LittleEndian(headerFrom0x200.Slice(0x34, 4)));
    }

    public static WudfTapReport DecodeRingEntry(uint packedEntry)
    {
        byte reportId = (byte)(packedEntry & 0xFF);
        byte usageLo = (byte)((packedEntry >> 8) & 0xFF);
        byte usageHi = (byte)((packedEntry >> 16) & 0xFF);
        byte seq = (byte)((packedEntry >> 24) & 0xFF);
        return new WudfTapReport(reportId, usageLo, usageHi, seq);
    }
}
