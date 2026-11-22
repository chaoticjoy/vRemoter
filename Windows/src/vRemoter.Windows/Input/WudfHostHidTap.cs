using System.Buffers.Binary;
using vRemoter.Core.Input;
using vRemoter.Windows.SystemSupport;
using static vRemoter.Windows.Input.WudfHostNativeInterop;

namespace vRemoter.Windows.Input;

/// <summary>
/// Native x64 zero-thread lock-free interceptor for WUDFHost.exe (IOCTL 0x80018483).
/// Ported from Tilkmilk/vibe-mote's keys.py + tap.js in pure C#/Win32 without Frida or Python.
/// Reads 3-byte HOGP Consumer Control reports ([0x02, usage_lo, usage_hi]) directly from
/// WUDFHost.exe's NtDeviceIoControlFile output buffer before the UMDF driver drops them.
/// </summary>
public sealed class WudfHostHidTap : IDisposable
{
    private sealed class AttachedHost(int pid, IntPtr processHandle, IntPtr pageAddress, uint initialSeq, string sourceNode) : IDisposable
    {
        public int Pid { get; } = pid;
        public IntPtr ProcessHandle { get; } = processHandle;
        public IntPtr PageAddress { get; } = pageAddress;
        public string SourceNode { get; } = sourceNode;
        public uint LastReadSeq { get; set; } = initialSeq;
        public ulong LastLoggedTotals { get; set; }
        public ulong LastLoggedIoctls { get; set; }
        public DateTimeOffset LastDiagLogAt { get; set; } = DateTimeOffset.MinValue;

        public void Dispose()
        {
            if (ProcessHandle != IntPtr.Zero)
            {
                _ = CloseHandle(ProcessHandle);
            }
        }
    }

    private readonly ushort _vendorId;
    private readonly ushort _productId;
    private readonly string _logTag;
    private readonly object _sync = new();
    private readonly Dictionary<int, AttachedHost> _hosts = [];

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private bool _suppressEnabled;
    private bool _isConnected;
    private bool _loggedNotElevated;
    private int _rxLogCount;

    public event Action<bool>? ConnectionChanged;
    public event Action<byte, byte[]>? ReportReceived;

    public bool IsConnected
    {
        get { lock (_sync) return _isConnected; }
    }

    public WudfHostHidTap(ushort vendorId, ushort productId, bool suppressEnabled, string logTag)
    {
        _vendorId = vendorId;
        _productId = productId;
        _suppressEnabled = suppressEnabled;
        _logTag = logTag;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _pollTask = Task.Factory.StartNew(() => PollLoop(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_sync)
        {
            cts = _cts;
            task = _pollTask;
            _cts = null;
            _pollTask = null;
        }
        if (cts is not null)
        {
            cts.Cancel();
            try { task?.Wait(500); } catch { }
            cts.Dispose();
        }

        bool wasConnected;
        lock (_sync)
        {
            foreach (var host in _hosts.Values)
            {
                WriteSuppressFlag(host, enabled: false);
                host.Dispose();
            }
            _hosts.Clear();
            wasConnected = _isConnected;
            _isConnected = false;
        }
        if (wasConnected) ConnectionChanged?.Invoke(false);
    }

    public void SetSuppressEnabled(bool enabled)
    {
        lock (_sync)
        {
            _suppressEnabled = enabled;
            foreach (var host in _hosts.Values)
            {
                WriteSuppressFlag(host, enabled);
            }
        }
    }

    private void PollLoop(CancellationToken ct)
    {
        if (WindowsVersionInfo.IsElevated)
        {
            TryEnableDebugPrivilege();
        }

        var lastScan = DateTimeOffset.MinValue;
        byte[] headerBuf = new byte[0x40];
        byte[] ringSlotBuf = new byte[4];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (now - lastScan >= TimeSpan.FromSeconds(2.0))
                {
                    lastScan = now;
                    RefreshAttachedHosts();
                }

                List<AttachedHost> snapshot;
                lock (_sync)
                {
                    snapshot = [.. _hosts.Values];
                }

                bool anyAlive = false;
                foreach (var host in snapshot)
                {
                    if (!ReadProcessMemory(
                            host.ProcessHandle,
                            IntPtr.Add(host.PageAddress, WudfHidTapStubBuilder.OffMagic),
                            headerBuf,
                            headerBuf.Length,
                            out int bytesRead) ||
                        bytesRead < 0x38)
                    {
                        RemoveHost(host.Pid);
                        continue;
                    }

                    var diag = WudfHidTapStubBuilder.ReadDiagnostics(headerBuf);
                    if (diag.Magic != WudfHidTapStubBuilder.MagicSignature)
                    {
                        RemoveHost(host.Pid);
                        continue;
                    }

                    anyAlive = true;
                    uint committed = diag.CommittedSeq;
                    uint delta = committed - host.LastReadSeq;
                    if (delta > 0)
                    {
                        if (delta > WudfHidTapStubBuilder.RingCapacity)
                        {
                            host.LastReadSeq = committed - WudfHidTapStubBuilder.RingCapacity;
                        }

                        while (host.LastReadSeq != committed)
                        {
                            uint slotIndex = host.LastReadSeq & 0xFF;
                            IntPtr slotPtr = IntPtr.Add(
                                host.PageAddress,
                                WudfHidTapStubBuilder.OffRingBuffer + (int)(slotIndex * 4));
                            host.LastReadSeq++;

                            if (!ReadProcessMemory(host.ProcessHandle, slotPtr, ringSlotBuf, 4, out int r) || r != 4)
                            {
                                break;
                            }

                            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(ringSlotBuf);
                            var report = WudfHidTapStubBuilder.DecodeRingEntry(packed);
                            if (report.ReportId != 0x02 && report.ReportId != 0x01)
                            {
                                continue;
                            }

                            if (_rxLogCount < 24)
                            {
                                _rxLogCount++;
                                LogService.Write($"[{_logTag}] WUDF rx pid={host.Pid} id=0x{report.ReportId:X2} usage=0x{report.Usage:X4} hex={report.ReportId:X2}-{report.UsageLo:X2}-{report.UsageHi:X2}");
                            }
                            ReportReceived?.Invoke(report.ReportId, report.ToPayload());
                        }
                    }

                    if ((diag.IoctlCalls != host.LastLoggedIoctls ||
                         (diag.TotalCalls != host.LastLoggedTotals && host.LastLoggedTotals == 0)) &&
                        now - host.LastDiagLogAt >= TimeSpan.FromSeconds(10))
                    {
                        host.LastLoggedTotals = diag.TotalCalls;
                        host.LastLoggedIoctls = diag.IoctlCalls;
                        host.LastDiagLogAt = now;
                        LogService.Write($"[{_logTag}] WUDF stats pid={host.Pid} total={diag.TotalCalls} ioctl80018483={diag.IoctlCalls} reports3={diag.Report3Calls} lastLen={diag.LastOutLen} lastStatus=0x{diag.LastNtStatus:X8}");
                    }
                }

                UpdateConnectedState(anyAlive);
            }
            catch (Exception ex)
            {
                LogService.Write($"[{_logTag}] WUDF poll error: {ex.Message}");
            }

            if (ct.WaitHandle.WaitOne(8))
            {
                break;
            }
        }
    }

    private void RefreshAttachedHosts()
    {
        if (!WindowsVersionInfo.IsElevated)
        {
            if (!_loggedNotElevated)
            {
                _loggedNotElevated = true;
                LogService.Write($"[{_logTag}] WUDF tap skipped (process not elevated)");
            }
            return;
        }

        var candidates = DiscoverCandidateWudfHosts(_vendorId, _productId);
        foreach (var (pid, sourceNode) in candidates)
        {
            lock (_sync)
            {
                if (_hosts.ContainsKey(pid)) continue;
            }
            TryAttachToHost(pid, sourceNode);
        }
    }

    private void TryAttachToHost(int pid, string sourceNode)
    {
        IntPtr hModNtdll = GetModuleHandleW("ntdll.dll");
        IntPtr ntAddr = hModNtdll != IntPtr.Zero ? GetProcAddress(hModNtdll, "NtDeviceIoControlFile") : IntPtr.Zero;
        if (ntAddr == IntPtr.Zero) return;

        // Read clean syscall ID from our own process's ntdll!NtDeviceIoControlFile in case
        // the target process already holds an older hooked prologue.
        byte[] localPrologue = new byte[16];
        System.Runtime.InteropServices.Marshal.Copy(ntAddr, localPrologue, 0, localPrologue.Length);
        _ = WudfHidTapStubBuilder.TryExtractSyscallId(localPrologue, out uint localSyscallId);

        IntPtr hProc = OpenProcess(ProcessAccessFlags, bInheritHandle: false, pid);
        if (hProc == IntPtr.Zero) return;

        try
        {
            byte[] prologue = new byte[16];
            if (!ReadProcessMemory(hProc, ntAddr, prologue, prologue.Length, out int read) || read < 8)
            {
                _ = CloseHandle(hProc);
                return;
            }

            uint syscallId = localSyscallId;

            // Case A: Already hooked -> reuse only if it matches current v2 MagicSignature; otherwise upgrade atomically.
            if (prologue[0] == 0xE9)
            {
                int rel32 = BinaryPrimitives.ReadInt32LittleEndian(prologue.AsSpan(1, 4));
                IntPtr existingPage = new(ntAddr.ToInt64() + 5 + rel32);
                byte[] hdr = new byte[0x40];
                if (ReadProcessMemory(hProc, IntPtr.Add(existingPage, WudfHidTapStubBuilder.OffMagic), hdr, hdr.Length, out int hr) &&
                    hr >= 0x38)
                {
                    var diag = WudfHidTapStubBuilder.ReadDiagnostics(hdr);
                    if (diag.Magic == WudfHidTapStubBuilder.MagicSignature)
                    {
                        var reused = new AttachedHost(pid, hProc, existingPage, diag.CommittedSeq, sourceNode);
                        WriteSuppressFlag(reused, _suppressEnabled);
                        lock (_sync) { _hosts[pid] = reused; }
                        LogService.Write($"[{_logTag}] WUDF attached (reused) pid={pid} page=0x{existingPage.ToInt64():X} src={sourceNode}");
                        return;
                    }

                    if (syscallId == 0 && diag.SyscallId != 0)
                    {
                        syscallId = diag.SyscallId;
                    }
                }
            }
            else if (!WudfHidTapStubBuilder.TryExtractSyscallId(prologue, out syscallId) && localSyscallId != 0)
            {
                syscallId = localSyscallId;
            }

            if (syscallId == 0)
            {
                _ = CloseHandle(hProc);
                return;
            }

            IntPtr pageAddr = AllocateNearAddress(hProc, ntAddr.ToInt64());
            if (pageAddr == IntPtr.Zero)
            {
                _ = CloseHandle(hProc);
                return;
            }

            byte[] pageBytes = WudfHidTapStubBuilder.BuildHookPage(syscallId, _suppressEnabled, prologue);
            if (!WriteProcessMemory(hProc, pageAddr, pageBytes, pageBytes.Length, out int written) || written != pageBytes.Length)
            {
                _ = CloseHandle(hProc);
                return;
            }

            _ = FlushInstructionCache(hProc, pageAddr, (UIntPtr)pageBytes.Length);

            byte[] jmpPatch = WudfHidTapStubBuilder.BuildRelJmp8(ntAddr.ToInt64(), pageAddr.ToInt64());
            if (!VirtualProtectEx(hProc, ntAddr, (UIntPtr)8, PageExecuteReadWrite, out uint oldProtect))
            {
                _ = CloseHandle(hProc);
                return;
            }

            bool patched = WriteProcessMemory(hProc, ntAddr, jmpPatch, jmpPatch.Length, out int patchWritten) &&
                           patchWritten == jmpPatch.Length;
            _ = FlushInstructionCache(hProc, ntAddr, (UIntPtr)8);
            _ = VirtualProtectEx(hProc, ntAddr, (UIntPtr)8, oldProtect, out _);

            if (!patched)
            {
                _ = CloseHandle(hProc);
                return;
            }

            var host = new AttachedHost(pid, hProc, pageAddr, 0, sourceNode);
            lock (_sync) { _hosts[pid] = host; }
            LogService.Write($"[{_logTag}] WUDF attached{(prologue[0] == 0xE9 ? " (upgraded)" : string.Empty)} pid={pid} syscall=0x{syscallId:X} page=0x{pageAddr.ToInt64():X} src={sourceNode}");
        }
        catch (Exception ex)
        {
            _ = CloseHandle(hProc);
            LogService.Write($"[{_logTag}] WUDF attach error pid={pid}: {ex.Message}");
        }
    }

    private static void WriteSuppressFlag(AttachedHost host, bool enabled)
    {
        byte[] val = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(val, enabled ? 1U : 0U);
        _ = WriteProcessMemory(
            host.ProcessHandle,
            IntPtr.Add(host.PageAddress, WudfHidTapStubBuilder.OffSuppressEnabled),
            val,
            4,
            out _);
    }

    private void RemoveHost(int pid)
    {
        AttachedHost? removed;
        lock (_sync)
        {
            if (!_hosts.Remove(pid, out removed)) return;
        }
        removed.Dispose();
    }

    private void UpdateConnectedState(bool connected)
    {
        bool changed = false;
        lock (_sync)
        {
            if (_isConnected != connected)
            {
                _isConnected = connected;
                changed = true;
            }
        }
        if (changed) ConnectionChanged?.Invoke(connected);
    }

    public void Dispose() => Stop();
}
