using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using vRemoter.Core.Input;

namespace vRemoter.Windows.Input;

internal static class WudfHostNativeInterop
{
    private const string BthLeEnumPath = @"SYSTEM\CurrentControlSet\Enum\BTHLEDevice";
    private const string HidServicePrefix = "{00001812-0000-1000-8000-00805f9b34fb}";
    private const string WudfDiagSubKey = @"Device Parameters\WUDFDiagnosticInfo";

    internal const uint ProcessAccessFlags =
        0x0008 | // PROCESS_VM_OPERATION
        0x0010 | // PROCESS_VM_READ
        0x0020 | // PROCESS_VM_WRITE
        0x0400;  // PROCESS_QUERY_INFORMATION

    internal const uint MemCommit = 0x1000;
    internal const uint MemReserve = 0x2000;
    internal const uint MemFree = 0x10000;
    internal const uint PageExecuteReadWrite = 0x40;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    internal static List<(int Pid, string SourceNode, bool IsHidOverGatt)> DiscoverCandidateWudfHosts(ushort vendorId, ushort productId)
    {
        var list = new List<(int Pid, string SourceNode, bool IsHidOverGatt)>();
        var seenPids = new HashSet<int>();
        string vidHex = vendorId.ToString("x4");
        string pidHex = productId.ToString("x4");

        // 1. Exact registry lookup from HKLM\SYSTEM\CurrentControlSet\Enum\BTHLEDevice\{00001812-...}\...\WUDFDiagnosticInfo\HostPid
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(BthLeEnumPath);
            if (root is not null)
            {
                foreach (string svcName in root.GetSubKeyNames())
                {
                    if (!svcName.StartsWith(HidServicePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    bool matchesTarget = svcName.Contains(vidHex, StringComparison.OrdinalIgnoreCase) &&
                                         svcName.Contains(pidHex, StringComparison.OrdinalIgnoreCase);

                    try
                    {
                        using var svcKey = root.OpenSubKey(svcName);
                        if (svcKey is null) continue;
                        foreach (string instName in svcKey.GetSubKeyNames())
                        {
                            try
                            {
                                using var diagKey = svcKey.OpenSubKey($@"{instName}\{WudfDiagSubKey}");
                                object? raw = diagKey?.GetValue("HostPid");
                                int hostPid = raw is int iv ? iv : (raw is uint uv ? (int)uv : 0);
                                if (hostPid > 0 && seenPids.Add(hostPid))
                                {
                                    bool isHog = matchesTarget || HasHidOverGattModule(hostPid, out _);
                                    if (matchesTarget) list.Insert(0, (hostPid, svcName, true));
                                    else list.Add((hostPid, svcName, isHog));
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
        }
        catch
        {
        }

        // 2. Fallback: include all running WUDFHost.exe processes and inspect loaded modules
        //    so we pinpoint the exact process hosting Microsoft.Bluetooth.Profiles.HidOverGatt.dll.
        try
        {
            foreach (var proc in Process.GetProcessesByName("WUDFHost"))
            {
                using (proc)
                {
                    if (proc.Id > 0 && seenPids.Add(proc.Id))
                    {
                        bool isHog = HasHidOverGattModule(proc.Id, out string modTag);
                        string label = string.IsNullOrEmpty(modTag) ? "WUDFHost.exe" : modTag;
                        if (isHog) list.Insert(0, (proc.Id, label, true));
                        else list.Add((proc.Id, label, false));
                    }
                }
            }
        }
        catch
        {
        }

        return list;
    }

    internal static bool HasHidOverGattModule(int pid, out string moduleSummary)
    {
        moduleSummary = string.Empty;
        IntPtr snap = CreateToolhelp32Snapshot(0x00000008 /* TH32CS_SNAPMODULE */ | 0x00000010 /* TH32CS_SNAPMODULE32 */, (uint)pid);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
        {
            return false;
        }
        try
        {
            var me = new MODULEENTRY32W { DwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
            if (!Module32FirstW(snap, ref me)) return false;
            var interesting = new List<string>();
            do
            {
                string name = me.SzModule ?? string.Empty;
                if (name.Contains("HidOverGatt", StringComparison.OrdinalIgnoreCase))
                {
                    interesting.Insert(0, name);
                }
                else if (name.Contains("Bth", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Hid", StringComparison.OrdinalIgnoreCase))
                {
                    interesting.Add(name);
                }
            }
            while (Module32NextW(snap, ref me));

            if (interesting.Count > 0)
            {
                moduleSummary = string.Join(",", interesting.Distinct().Take(3));
            }
            return interesting.Any(n => n.Contains("HidOverGatt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _ = CloseHandle(snap);
        }
    }

    internal static IntPtr AllocateNearAddress(IntPtr hProcess, long targetAddr)
    {
        const long maxDistance = 0x7000_0000L; // ±1.75 GB
        long minAddr = Math.Max(0x10000L, targetAddr - maxDistance);
        long maxAddr = targetAddr + maxDistance;

        for (long cur = targetAddr - 0x0200_0000L; cur >= minAddr; cur -= 0x10000L)
        {
            IntPtr p = VirtualAllocEx(hProcess, new IntPtr(cur & ~0xFFFFL), (UIntPtr)WudfHidTapStubBuilder.PageSize, MemCommit | MemReserve, PageExecuteReadWrite);
            if (p != IntPtr.Zero && Math.Abs(p.ToInt64() - targetAddr) <= maxDistance)
            {
                return p;
            }
        }

        long scan = minAddr;
        while (scan < maxAddr &&
               VirtualQueryEx(hProcess, new IntPtr(scan), out var mbi, (UIntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != UIntPtr.Zero)
        {
            if (mbi.State == MemFree && (long)mbi.RegionSize >= 0x10000)
            {
                long candidate = (mbi.BaseAddress.ToInt64() + 0xFFFFL) & ~0xFFFFL;
                if (candidate >= minAddr && candidate + WudfHidTapStubBuilder.PageSize <= maxAddr)
                {
                    IntPtr p = VirtualAllocEx(hProcess, new IntPtr(candidate), (UIntPtr)WudfHidTapStubBuilder.PageSize, MemCommit | MemReserve, PageExecuteReadWrite);
                    if (p != IntPtr.Zero) return p;
                }
            }
            long next = mbi.BaseAddress.ToInt64() + Math.Max(0x10000L, (long)mbi.RegionSize);
            if (next <= scan) break;
            scan = next;
        }
        return IntPtr.Zero;
    }

    internal static void TryEnableDebugPrivilege()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out IntPtr hToken))
        {
            return;
        }
        try
        {
            if (LookupPrivilegeValueW(null, "SeDebugPrivilege", out LUID luid))
            {
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                _ = AdjustTokenPrivileges(hToken, bDisableAllPrivileges: false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally
        {
            _ = CloseHandle(hToken);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MODULEENTRY32W
    {
        public uint DwSize;
        public uint Th32ModuleID;
        public uint Th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr ModBaseAddr;
        public uint ModBaseSize;
        public IntPtr HModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string SzModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    internal static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern UIntPtr VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool bDisableAllPrivileges, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);
}

