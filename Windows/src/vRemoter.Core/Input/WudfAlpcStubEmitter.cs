using System.Buffers.Binary;

namespace vRemoter.Core.Input;

public readonly record struct WudfTapAlpcEntry(
    uint NtStatus,
    uint Flags,
    ushort PortHandleLo,
    ushort DataLength,
    ushort TotalLength,
    byte MsgType,
    byte Direction,
    byte[] Payload);

/// <summary>
/// Emits the x64 NtAlpcSendWaitReceivePort hook stub (Capture Send & Receive ALPC messages
/// into the 32x128-byte trace ring at OffAlpcTraceBuffer) and decodes captured entries.
/// </summary>
public static class WudfAlpcStubEmitter
{
    public const int SlotSize = 128;
    public const int SlotCount = 32;
    public const int MaxCapturedPayload = 112;

    public static void EmitAlpcStub(
        byte[] page,
        int stubOffset,
        int stubLimit,
        int offAlpcCalls,
        int offWriteSeq,
        int offCommittedSeq,
        int offTraceBuffer,
        uint alpcSyscallId)
    {
        int pos = stubOffset;

        void Emit(params byte[] bytes)
        {
            bytes.CopyTo(page, pos);
            pos += bytes.Length;
        }

        void EmitU32(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(pos, 4), v);
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

        int EmitRel32Placeholder(params byte[] opcode)
        {
            opcode.CopyTo(page, pos);
            pos += opcode.Length;
            int fixup = pos;
            pos += 4;
            return fixup;
        }

        void PatchRel32(int fixupPos, int targetOffset)
        {
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(fixupPos, 4), targetOffset - (fixupPos + 4));
        }

        // lock inc qword ptr [rip + offAlpcCalls]
        EmitRipRel(offAlpcCalls, 0xF0, 0x48, 0xFF, 0x05);

        // Save volatile register args into caller's 32-byte home space
        Emit(0x48, 0x89, 0x4C, 0x24, 0x08); // mov [rsp+0x08], rcx (PortHandle)
        Emit(0x89, 0x54, 0x24, 0x10);       // mov [rsp+0x10], edx (Flags)
        Emit(0x4C, 0x89, 0x44, 0x24, 0x18); // mov [rsp+0x18], r8  (SendMessage)
        Emit(0x4C, 0x89, 0x4C, 0x24, 0x20); // mov [rsp+0x20], r9  (SendAttributes)

        // Pre-syscall capture of SendMessage (r8) before kernel possibly reuses buffer for Reply
        Emit(0x4D, 0x85, 0xC0);             // test r8, r8
        int jzDoSyscall = EmitRel32Placeholder(0x0F, 0x84);
        Emit(0x41, 0xB9, 0xFF, 0xFF, 0xFF, 0xFF); // mov r9d, 0xFFFFFFFF
        Emit(0x31, 0xD2);                   // xor edx, edx (dir = 0 Send)
        int callSend = EmitRel32Placeholder(0xE8);

        int doSyscallOff = pos;
        PatchRel32(jzDoSyscall, doSyscallOff);
        Emit(0x48, 0x8B, 0x4C, 0x24, 0x08); // mov rcx, [rsp+0x08]
        Emit(0x8B, 0x54, 0x24, 0x10);       // mov edx, [rsp+0x10]
        Emit(0x4C, 0x8B, 0x44, 0x24, 0x18); // mov r8,  [rsp+0x18]
        Emit(0x4C, 0x8B, 0x4C, 0x24, 0x20); // mov r9,  [rsp+0x20]
        Emit(0x4C, 0x8B, 0xD1);             // mov r10, rcx
        Emit(0xB8);
        EmitU32(alpcSyscallId);
        Emit(0x0F, 0x05);                   // syscall

        // Save NTSTATUS in [rsp+0x18]; only capture ReceiveMessage ([rsp+0x28]) if eax == 0
        Emit(0x89, 0x44, 0x24, 0x18);       // mov [rsp+0x18], eax
        Emit(0x85, 0xC0);                   // test eax, eax
        int jnzRet1 = EmitRel32Placeholder(0x0F, 0x85);
        Emit(0x4C, 0x8B, 0x44, 0x24, 0x28); // mov r8, [rsp+0x28] (ReceiveMessage)
        Emit(0x4D, 0x85, 0xC0);             // test r8, r8
        int jzRet2 = EmitRel32Placeholder(0x0F, 0x84);
        Emit(0x41, 0x89, 0xC1);             // mov r9d, eax (0)
        Emit(0xBA, 0x01, 0x00, 0x00, 0x00); // mov edx, 1 (dir = 1 Recv)
        int callRecv = EmitRel32Placeholder(0xE8);

        int retOff = pos;
        PatchRel32(jnzRet1, retOff);
        PatchRel32(jzRet2, retOff);
        Emit(0x8B, 0x44, 0x24, 0x18);       // mov eax, [rsp+0x18]
        Emit(0xC3);                         // ret

        // --- RecordAlpcMsg(r8=msg, r9d=ntStatus, edx=dir) ---
        // Note: `call` shifts rsp by -8, so caller's [rsp+0x08/0x10] are at [rsp+0x10/0x18].
        int helperOff = pos;
        PatchRel32(callSend, helperOff);
        PatchRel32(callRecv, helperOff);

        Emit(0x49, 0x81, 0xF8, 0x00, 0x00, 0x01, 0x00); // cmp r8, 0x10000
        int jbSkip1 = EmitRel32Placeholder(0x0F, 0x82);
        Emit(0x41, 0x0F, 0xB7, 0x08);       // movzx ecx, word ptr [r8]     (DataLength)
        Emit(0x41, 0x0F, 0xB7, 0x40, 0x02); // movzx eax, word ptr [r8 + 2] (TotalLength)
        Emit(0x83, 0xF8, 0x28);             // cmp eax, 0x28
        int jbSkip2 = EmitRel32Placeholder(0x0F, 0x82);
        Emit(0x83, 0xE8, 0x28);             // sub eax, 0x28
        Emit(0x39, 0xC1);                   // cmp ecx, eax
        Emit(0x76, 0x02);                   // jbe +2
        Emit(0x89, 0xC1);                   // mov ecx, eax
        Emit(0x85, 0xC9);                   // test ecx, ecx
        int jzSkip3 = EmitRel32Placeholder(0x0F, 0x84);
        Emit(0x83, 0xF9, 0x70);             // cmp ecx, 112
        Emit(0x76, 0x05);                   // jbe +5
        Emit(0xB9, 0x70, 0x00, 0x00, 0x00); // mov ecx, 112

        // r10 = offTraceBuffer + ((xadd(offWriteSeq, 1) & 31) << 7)
        Emit(0x41, 0xBA, 0x01, 0x00, 0x00, 0x00); // mov r10d, 1
        EmitRipRel(offWriteSeq, 0xF0, 0x44, 0x0F, 0xC1, 0x15);
        Emit(0x41, 0x83, 0xE2, 0x1F);       // and r10d, 31
        Emit(0x49, 0xC1, 0xE2, 0x07);       // shl r10, 7
        EmitRipRel(offTraceBuffer, 0x4C, 0x8D, 0x1D);
        Emit(0x4D, 0x01, 0xDA);             // add r10, r11

        // Populate 16-byte entry header
        Emit(0x45, 0x89, 0x0A);             // mov [r10], r9d
        Emit(0x8B, 0x44, 0x24, 0x18);       // mov eax, [rsp+0x18] (Flags)
        Emit(0x41, 0x89, 0x42, 0x04);       // mov [r10+4], eax
        Emit(0x66, 0x8B, 0x44, 0x24, 0x10); // mov ax, [rsp+0x10]  (PortHandleLo)
        Emit(0x66, 0x41, 0x89, 0x42, 0x08); // mov [r10+8], ax
        Emit(0x66, 0x41, 0x8B, 0x00);       // mov ax, [r8]        (DataLength)
        Emit(0x66, 0x41, 0x89, 0x42, 0x0A); // mov [r10+10], ax
        Emit(0x66, 0x41, 0x8B, 0x40, 0x02); // mov ax, [r8+2]      (TotalLength)
        Emit(0x66, 0x41, 0x89, 0x42, 0x0C); // mov [r10+12], ax
        Emit(0x41, 0x8A, 0x40, 0x04);       // mov al, [r8+4]      (MsgType)
        Emit(0x41, 0x88, 0x42, 0x0E);       // mov [r10+14], al
        Emit(0x41, 0x88, 0x52, 0x0F);       // mov [r10+15], dl    (Direction)

        // Copy ecx bytes from [r8 + 0x28] to [r10 + 0x10]
        Emit(0x49, 0x83, 0xC0, 0x28);       // add r8, 0x28
        Emit(0x49, 0x83, 0xC2, 0x10);       // add r10, 0x10
        int loopStart = pos;
        Emit(0x41, 0x8A, 0x00);             // mov al, [r8]
        Emit(0x41, 0x88, 0x02);             // mov [r10], al
        Emit(0x49, 0xFF, 0xC0);             // inc r8
        Emit(0x49, 0xFF, 0xC2);             // inc r10
        Emit(0xFF, 0xC9);                   // dec ecx
        sbyte relLoop = (sbyte)(loopStart - (pos + 2));
        Emit(0x75, unchecked((byte)relLoop)); // jnz loopStart

        EmitRipRel(offCommittedSeq, 0xF0, 0xFF, 0x05); // lock inc dword ptr [rip + offCommittedSeq]

        int helperRet = pos;
        PatchRel32(jbSkip1, helperRet);
        PatchRel32(jbSkip2, helperRet);
        PatchRel32(jzSkip3, helperRet);
        Emit(0xC3); // ret

        if (pos > stubLimit)
        {
            throw new InvalidOperationException($"ALPC hook stub overflowed region: {pos} > {stubLimit}");
        }
    }

    public static WudfTapAlpcEntry DecodeAlpcEntry(ReadOnlySpan<byte> slot128)
    {
        uint status = BinaryPrimitives.ReadUInt32LittleEndian(slot128.Slice(0x00, 4));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(slot128.Slice(0x04, 4));
        ushort portLo = BinaryPrimitives.ReadUInt16LittleEndian(slot128.Slice(0x08, 2));
        ushort dataLen = BinaryPrimitives.ReadUInt16LittleEndian(slot128.Slice(0x0A, 2));
        ushort totalLen = BinaryPrimitives.ReadUInt16LittleEndian(slot128.Slice(0x0C, 2));
        byte msgType = slot128[0x0E];
        byte dir = slot128[0x0F];
        int availPayload = totalLen >= 0x28 ? Math.Min(dataLen, totalLen - 0x28) : dataLen;
        int copyLen = Math.Clamp(availPayload, 0, MaxCapturedPayload);
        byte[] payload = copyLen > 0 ? slot128.Slice(0x10, copyLen).ToArray() : [];
        return new WudfTapAlpcEntry(status, flags, portLo, dataLen, totalLen, msgType, dir, payload);
    }
}
