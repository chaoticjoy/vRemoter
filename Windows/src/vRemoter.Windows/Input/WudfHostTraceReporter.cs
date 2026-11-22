using System.Buffers.Binary;
using vRemoter.Core.Input;
using vRemoter.Windows.SystemSupport;
using static vRemoter.Windows.Input.WudfHostNativeInterop;

namespace vRemoter.Windows.Input;

internal static class WudfHostTraceReporter
{
    public static void DrainAndLog(
        IntPtr processHandle,
        IntPtr pageAddress,
        int pid,
        bool isHidOverGatt,
        string logTag,
        in WudfTapDiagnostics diag,
        WudfHostHidTap.HostTraceState state)
    {
        if (isHidOverGatt && state.IoctlLogCount < 40 && diag.TraceSeq != state.LastTraceSeq)
        {
            DrainIoctlTraces(processHandle, pageAddress, pid, logTag, diag.TraceSeq, state);
        }

        if (isHidOverGatt && state.AlpcLogCount < 120 && diag.AlpcTraceCommittedSeq != state.LastAlpcTraceSeq)
        {
            DrainAlpcTraces(processHandle, pageAddress, pid, logTag, diag.AlpcTraceCommittedSeq, state);
        }

        var now = DateTimeOffset.UtcNow;
        bool changed =
            diag.IoctlCalls != state.LastLoggedIoctls ||
            diag.TotalCalls != state.LastLoggedTotals ||
            (isHidOverGatt && (
                diag.ReadCalls != state.LastLoggedReads ||
                diag.AlpcCalls != state.LastLoggedAlpc ||
                diag.WriteCalls != state.LastLoggedWrites ||
                diag.IoCompCalls != state.LastLoggedIoComps));

        if (changed && now - state.LastDiagLogAt >= TimeSpan.FromSeconds(5))
        {
            state.LastLoggedTotals = diag.TotalCalls;
            state.LastLoggedIoctls = diag.IoctlCalls;
            state.LastLoggedReads = diag.ReadCalls;
            state.LastLoggedAlpc = diag.AlpcCalls;
            state.LastLoggedWrites = diag.WriteCalls;
            state.LastLoggedIoComps = diag.IoCompCalls;
            state.LastDiagLogAt = now;
            LogService.Write(
                $"[{logTag}] WUDF stats pid={pid}(hog={isHidOverGatt}) ioctlTotal={diag.TotalCalls} ioctl={diag.IoctlCalls} " +
                $"reports3={diag.Report3Calls} read={diag.ReadCalls} write={diag.WriteCalls} iocp={diag.IoCompCalls} " +
                $"alpc={diag.AlpcCalls} alpcCap={diag.AlpcTraceCommittedSeq} lastIoctl=0x{diag.LastIoctlCode:X8} " +
                $"lastLen={diag.LastOutLen} lastStatus=0x{diag.LastNtStatus:X8}");
        }
    }

    private static void DrainIoctlTraces(
        IntPtr processHandle,
        IntPtr pageAddress,
        int pid,
        string logTag,
        uint traceSeq,
        WudfHostHidTap.HostTraceState state)
    {
        uint delta = traceSeq - state.LastTraceSeq;
        if (delta > WudfHidTapStubBuilder.TraceCapacity)
        {
            state.LastTraceSeq = traceSeq - WudfHidTapStubBuilder.TraceCapacity;
        }

        Span<byte> buf = stackalloc byte[16];
        byte[] managed = new byte[16];
        while (state.LastTraceSeq != traceSeq && state.IoctlLogCount < 40)
        {
            uint idx = state.LastTraceSeq & (WudfHidTapStubBuilder.TraceCapacity - 1);
            IntPtr ptr = IntPtr.Add(pageAddress, WudfHidTapStubBuilder.OffTraceBuffer + (int)(idx * 16));
            state.LastTraceSeq++;
            if (!ReadProcessMemory(processHandle, ptr, managed, 16, out int read) || read != 16)
            {
                break;
            }
            managed.CopyTo(buf);
            var te = WudfHidTapStubBuilder.DecodeTraceEntry(buf);
            state.IoctlLogCount++;
            LogService.Write(
                $"[{logTag}] WUDF trace pid={pid} ioctl=0x{te.IoControlCode:X8} status=0x{te.NtStatus:X8} " +
                $"outLen={te.OutLen} sample=0x{te.SampleBytes:X8}");
        }
    }

    private static void DrainAlpcTraces(
        IntPtr processHandle,
        IntPtr pageAddress,
        int pid,
        string logTag,
        uint committedSeq,
        WudfHostHidTap.HostTraceState state)
    {
        uint delta = committedSeq - state.LastAlpcTraceSeq;
        if (delta > WudfAlpcStubEmitter.SlotCount)
        {
            state.LastAlpcTraceSeq = committedSeq - WudfAlpcStubEmitter.SlotCount;
        }

        byte[] slotBuf = new byte[WudfAlpcStubEmitter.SlotSize];
        while (state.LastAlpcTraceSeq != committedSeq && state.AlpcLogCount < 120)
        {
            uint idx = state.LastAlpcTraceSeq & (WudfAlpcStubEmitter.SlotCount - 1);
            IntPtr ptr = IntPtr.Add(pageAddress, WudfHidTapStubBuilder.OffAlpcTraceBuffer + (int)(idx * WudfAlpcStubEmitter.SlotSize));
            if (!ReadProcessMemory(processHandle, ptr, slotBuf, slotBuf.Length, out int read) || read != slotBuf.Length)
            {
                break;
            }

            ushort totalLen = BinaryPrimitives.ReadUInt16LittleEndian(slotBuf.AsSpan(0x0C, 2));
            if (totalLen < 0x28)
            {
                break; // Concurrent slot still finalizing; retry next 8ms tick
            }

            state.LastAlpcTraceSeq++;
            var entry = WudfAlpcStubEmitter.DecodeAlpcEntry(slotBuf);
            string hex = entry.Payload.Length > 0 ? BitConverter.ToString(entry.Payload) : string.Empty;
            // Ignore per-message CallId/MsgId prefix (first 24 bytes) when deduplicating large RPC packets
            // so repeating timer/idle headers collapse cleanly while any argument change always logs.
            string bodyHex = entry.Payload.Length > 24 ? BitConverter.ToString(entry.Payload, 24) : hex;
            string dedupKey = $"{entry.Direction}:{entry.PortHandleLo:X4}:{entry.DataLength}:{bodyHex}";
            int seenCount = state.AlpcSeenCounts.TryGetValue(dedupKey, out int prev) ? prev + 1 : 1;
            state.AlpcSeenCounts[dedupKey] = seenCount;
            if (seenCount > 3)
            {
                continue;
            }

            state.AlpcLogCount++;
            char dir = entry.Direction == 0 ? 'S' : 'R';
            LogService.Write(
                $"[{logTag}] WUDF alpc pid={pid} dir={dir} port=0x{entry.PortHandleLo:X4} fl=0x{entry.Flags:X} " +
                $"st=0x{entry.NtStatus:X8} dlen={entry.DataLength} tlen={entry.TotalLength} type=0x{entry.MsgType:X2} " +
                $"hex={hex}");
        }
    }
}
