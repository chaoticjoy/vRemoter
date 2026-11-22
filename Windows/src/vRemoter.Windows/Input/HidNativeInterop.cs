using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace vRemoter.Windows.Input;

internal static class HidNativeInterop
{
    public const uint DigcfPresent = 0x02;
    public const uint DigcfDeviceInterface = 0x10;
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareReadWrite = 0x00000003;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000;

    public const int ErrorIoPending = 997;
    public const int ErrorOperationAborted = 995;
    public const int ErrorDeviceNotConnected = 1167;
    public const uint WaitObject0 = 0;
    public const uint Infinite = 0xFFFFFFFF;
    public const int HidPInput = 0;
    public const int HidPStatusSuccess = 0x00110000;

    public static HidUsagePair[] ParseHidUsages(IntPtr preparsed, int expectedInputLength, byte[] packet)
    {
        if (preparsed == IntPtr.Zero || packet.Length == 0)
        {
            return [];
        }

        byte[] report = packet;
        if (expectedInputLength > packet.Length && expectedInputLength <= 512)
        {
            report = new byte[expectedInputLength];
            Buffer.BlockCopy(packet, 0, report, 0, packet.Length);
        }

        var results = new List<HidUsagePair>(4);

        var usageAndPages = new USAGE_AND_PAGE[16];
        uint exLen = (uint)usageAndPages.Length;
        int exStatus = HidP_GetUsagesEx(HidPInput, 0, usageAndPages, ref exLen, preparsed, report, (uint)report.Length);
        if (exStatus >= 0 && exLen > 0)
        {
            int count = (int)Math.Min(exLen, (uint)usageAndPages.Length);
            for (int i = 0; i < count; i++)
            {
                if (usageAndPages[i].Usage != 0)
                {
                    var pair = new HidUsagePair(usageAndPages[i].UsagePage, usageAndPages[i].Usage);
                    if (!results.Contains(pair)) results.Add(pair);
                }
            }
        }

        // Also query page-specific HidP_GetUsages (as in 10RemoteHub's windows_hid.py)
        ReadOnlySpan<ushort> pages = [0x000C, 0x0007, 0x0001];
        var pageUsages = new ushort[16];
        foreach (ushort page in pages)
        {
            uint len = (uint)pageUsages.Length;
            int status = HidP_GetUsages(HidPInput, page, 0, pageUsages, ref len, preparsed, report, (uint)report.Length);
            if ((status == HidPStatusSuccess || status >= 0) && len > 0)
            {
                int count = (int)Math.Min(len, (uint)pageUsages.Length);
                for (int i = 0; i < count; i++)
                {
                    if (pageUsages[i] != 0)
                    {
                        var pair = new HidUsagePair(page, pageUsages[i]);
                        if (!results.Contains(pair)) results.Add(pair);
                    }
                }
            }
        }

        return [.. results];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct USAGE_AND_PAGE
    {
        public ushort Usage;
        public ushort UsagePage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OVERLAPPED
    {
        public UIntPtr Internal;
        public UIntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr HEvent;
    }

    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsagesEx(
        int reportType,
        ushort linkCollection,
        [Out] USAGE_AND_PAGE[] buttonList,
        ref uint usageLength,
        IntPtr preparsedData,
        [In] byte[] report,
        uint reportLength);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsages(
        int reportType,
        ushort usagePage,
        ushort linkCollection,
        [Out] ushort[] usageList,
        ref uint usageLength,
        IntPtr preparsedData,
        [In] byte[] report,
        uint reportLength);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        ref uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(
        SafeFileHandle hFile,
        IntPtr lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateEventW(
        IntPtr lpEventAttributes,
        bool bManualReset,
        bool bInitialState,
        string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ResetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForMultipleObjects(
        uint nCount,
        [In] IntPtr[] lpHandles,
        bool bWaitAll,
        uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}
