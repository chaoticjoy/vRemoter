using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using vRemoter.Windows.Input;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Bluetooth;

/// <summary>
/// Switches a target BLE remote's BTHLEDevice\{00001812-...} (HOGP HIDS) devnode
/// from hidbthle.inf (mshidumdf) to Microsoft's built-in signed bthleenum.inf
/// ("Bluetooth LE Generic Attribute Service", BTHLEDevice\GenericATTService)
/// via SetupAPI + DiInstallDevice — ported from 10RemoteHub's win_usb_driver.py.
/// This releases mshidumdf's exclusive lock on 0x1812 so user-space GATT readers
/// can subscribe to 0x2A4D button notifications identically to the 0xAB5E0001 voice service.
/// </summary>
public static class BleHidsDriverTakeover
{
    private const uint DiodInheritClassDrvs = 0x00000002;
    private const uint DiEnumSingleInf = 0x00010000;
    private const uint DiFlagsExAllowExcludedDrvs = 0x00000800;
    private const uint SpditClassDriver = 1;
    private const uint SpditCompatDriver = 2;

    public static bool EnsureGenericGattOnHids(
        ushort vendorId,
        ushort productId,
        ulong savedBluetoothAddress,
        string logTag)
    {
        var nodes = CfgMgrDeviceResolver.FindBleHidsDevNodes(vendorId, productId, savedBluetoothAddress);
        if (nodes.Count == 0)
        {
            LogService.Write($"[{logTag}] no BTHLE 0x1812 devnode found for VID=0x{vendorId:X4} PID=0x{productId:X4}");
            return false;
        }

        bool anyReady = false;
        foreach (var node in nodes)
        {
            LogService.Write($"[{logTag}] BTHLE 0x1812 node={node.InstanceId} svc='{node.Service}' inf='{node.DriverInf}' status=0x{node.Status:X8} problem={node.ProblemCode}");
            if (node.IsGenericGattBound && node.ProblemCode == 0)
            {
                anyReady = true;
                continue;
            }

            if (!WindowsVersionInfo.IsElevated)
            {
                LogService.Write($"[{logTag}] skip bthleenum.inf takeover (process not elevated)");
                continue;
            }

            if (TryBindDevNodeToInf(node.InstanceId, "bthleenum.inf", MatchGenericGattDriver, logTag))
            {
                anyReady = true;
            }
        }
        return anyReady;
    }

    public static bool RestoreHidBthLeOnHids(
        ushort vendorId,
        ushort productId,
        ulong savedBluetoothAddress,
        string logTag)
    {
        if (!WindowsVersionInfo.IsElevated) return false;
        var nodes = CfgMgrDeviceResolver.FindBleHidsDevNodes(vendorId, productId, savedBluetoothAddress);
        bool ok = false;
        foreach (var node in nodes)
        {
            if (TryBindDevNodeToInf(node.InstanceId, "hidbthle.inf", MatchHidBthLeDriver, logTag))
            {
                ok = true;
            }
        }
        return ok;
    }

    private static bool MatchGenericGattDriver(string section, string hwid, string description) =>
        hwid.Contains("GENERICATTSERVICE", StringComparison.OrdinalIgnoreCase) ||
        hwid.Contains("GATTSERVICE", StringComparison.OrdinalIgnoreCase) ||
        section.Contains("GattService", StringComparison.OrdinalIgnoreCase) ||
        section.Contains("GenericATT", StringComparison.OrdinalIgnoreCase) ||
        description.Contains("Generic Attribute", StringComparison.OrdinalIgnoreCase);

    private static bool MatchHidBthLeDriver(string section, string hwid, string description) =>
        hwid.Contains("00001812", StringComparison.OrdinalIgnoreCase) ||
        section.Contains("HidBthLE", StringComparison.OrdinalIgnoreCase);

    private static bool TryBindDevNodeToInf(
        string instanceId,
        string infFileName,
        Func<string, string, string, bool> predicate,
        string logTag)
    {
        string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(sysRoot)) sysRoot = @"C:\Windows";
        string infPath = Path.Combine(sysRoot, "INF", infFileName);
        if (!File.Exists(infPath))
        {
            LogService.Write($"[{logTag}] inbox INF not found: {infPath}");
            return false;
        }

        IntPtr devInfoSet = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            // Step 1: Build global class driver list from the single inbox INF so the opened
            // devnode inherits all driver models across classes via DIOD_INHERIT_CLASSDRVS.
            var setParams = new SP_DEVINSTALL_PARAMS_W
            {
                CbSize = (uint)Marshal.SizeOf<SP_DEVINSTALL_PARAMS_W>()
            };
            if (SetupDiGetDeviceInstallParamsW(devInfoSet, IntPtr.Zero, ref setParams))
            {
                setParams.Flags |= DiEnumSingleInf;
                setParams.FlagsEx |= DiFlagsExAllowExcludedDrvs;
                setParams.DriverPath = infPath;
                _ = SetupDiSetDeviceInstallParamsW(devInfoSet, IntPtr.Zero, ref setParams);
                _ = SetupDiBuildDriverInfoList(devInfoSet, IntPtr.Zero, SpditClassDriver);
            }

            var devData = new SP_DEVINFO_DATA
            {
                CbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
            };
            if (!SetupDiOpenDeviceInfoW(devInfoSet, instanceId, IntPtr.Zero, DiodInheritClassDrvs, ref devData))
            {
                int err = Marshal.GetLastWin32Error();
                LogService.Write($"[{logTag}] SetupDiOpenDeviceInfoW failed Win32={err}");
                return false;
            }

            // Also build compatible driver list directly on the devnode for the single INF.
            var devParams = new SP_DEVINSTALL_PARAMS_W
            {
                CbSize = (uint)Marshal.SizeOf<SP_DEVINSTALL_PARAMS_W>()
            };
            if (SetupDiGetDeviceInstallParamsW(devInfoSet, ref devData, ref devParams))
            {
                devParams.Flags |= DiEnumSingleInf;
                devParams.FlagsEx |= DiFlagsExAllowExcludedDrvs;
                devParams.DriverPath = infPath;
                _ = SetupDiSetDeviceInstallParamsW(devInfoSet, ref devData, ref devParams);
                _ = SetupDiBuildDriverInfoList(devInfoSet, ref devData, SpditCompatDriver);
            }

            if (!TrySelectDriver(devInfoSet, ref devData, SpditCompatDriver, predicate, logTag, out var chosen) &&
                !TrySelectDriver(devInfoSet, ref devData, SpditClassDriver, predicate, logTag, out chosen))
            {
                LogService.Write($"[{logTag}] no matching driver entry found in {infFileName}");
                return false;
            }

            if (!SetupDiSetSelectedDriverW(devInfoSet, ref devData, ref chosen))
            {
                int err = Marshal.GetLastWin32Error();
                LogService.Write($"[{logTag}] SetupDiSetSelectedDriverW failed Win32={err}");
                return false;
            }

            if (!DiInstallDevice(IntPtr.Zero, devInfoSet, ref devData, ref chosen, 0, out bool needReboot))
            {
                int err = Marshal.GetLastWin32Error();
                LogService.Write($"[{logTag}] DiInstallDevice({infFileName}) failed Win32={err}");
                return false;
            }

            LogService.Write($"[{logTag}] switched 0x1812 devnode to {infFileName} ('{chosen.Description}', reboot={needReboot})");
            RestartDeviceViaPnpUtil(instanceId);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Write($"[{logTag}] driver switch error: {ex.Message}");
            return false;
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(devInfoSet);
        }
    }

    private static bool TrySelectDriver(
        IntPtr devInfoSet,
        ref SP_DEVINFO_DATA devData,
        uint driverType,
        Func<string, string, string, bool> predicate,
        string logTag,
        out SP_DRVINFO_DATA_W selected)
    {
        selected = default;
        uint drvSize = (uint)Marshal.SizeOf<SP_DRVINFO_DATA_W>();
        for (uint idx = 0; ; idx++)
        {
            var drv = new SP_DRVINFO_DATA_W { CbSize = drvSize };
            if (!SetupDiEnumDriverInfoW(devInfoSet, ref devData, driverType, idx, ref drv))
            {
                break;
            }

            GetDriverSectionAndHardwareId(devInfoSet, ref devData, ref drv, out string section, out string hwid);
            if (predicate(section, hwid, drv.Description ?? string.Empty))
            {
                selected = drv;
                return true;
            }
        }
        return false;
    }

    private static void GetDriverSectionAndHardwareId(
        IntPtr devInfoSet,
        ref SP_DEVINFO_DATA devData,
        ref SP_DRVINFO_DATA_W drv,
        out string section,
        out string hwid)
    {
        section = string.Empty;
        hwid = string.Empty;
        uint required = 0;
        _ = SetupDiGetDriverInfoDetailW(devInfoSet, ref devData, ref drv, IntPtr.Zero, 0, out required);
        int baseSize = Marshal.SizeOf<SP_DRVINFO_DETAIL_DATA_W>();
        int allocSize = Math.Max((int)required, baseSize + 512);

        IntPtr buf = Marshal.AllocHGlobal(allocSize);
        try
        {
            Marshal.WriteInt32(buf, baseSize);
            if (!SetupDiGetDriverInfoDetailW(devInfoSet, ref devData, ref drv, buf, (uint)allocSize, out required))
            {
                return;
            }

            var detail = Marshal.PtrToStructure<SP_DRVINFO_DETAIL_DATA_W>(buf);
            section = detail.SectionName ?? string.Empty;
            int hwidOffset = (int)Marshal.OffsetOf<SP_DRVINFO_DETAIL_DATA_W>(nameof(SP_DRVINFO_DETAIL_DATA_W.HardwareID));
            if (required > hwidOffset)
            {
                int charLen = ((int)required - hwidOffset) / 2;
                hwid = Marshal.PtrToStringUni(IntPtr.Add(buf, hwidOffset), charLen).Replace('\0', ' ').Trim();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void RestartDeviceViaPnpUtil(string instanceId)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("pnputil.exe")
            {
                ArgumentList = { "/restart-device", instanceId },
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(4000);
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DEVINSTALL_PARAMS_W
    {
        public uint CbSize;
        public uint Flags;
        public uint FlagsEx;
        public IntPtr HwndParent;
        public IntPtr InstallMsgHandler;
        public IntPtr InstallMsgHandlerContext;
        public IntPtr FileQueue;
        public UIntPtr ClassInstallReserved;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DriverPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint DwLowDateTime;
        public uint DwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DRVINFO_DATA_W
    {
        public uint CbSize;
        public uint DriverType;
        public UIntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string MfgName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProviderName;
        public FILETIME DriverDate;
        public ulong DriverVersion;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DRVINFO_DETAIL_DATA_W
    {
        public uint CbSize;
        public FILETIME InfDate;
        public uint CompatIDsOffset;
        public uint CompatIDsLength;
        public UIntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string SectionName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string InfFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string DrvDescription;
        public char HardwareID;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiOpenDeviceInfoW(
        IntPtr deviceInfoSet,
        string deviceInstanceId,
        IntPtr hwndParent,
        uint openFlags,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstallParamsW(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref SP_DEVINSTALL_PARAMS_W deviceInstallParams);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstallParamsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstallParamsW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_DEVINSTALL_PARAMS_W deviceInstallParams);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceInstallParamsW(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref SP_DEVINSTALL_PARAMS_W deviceInstallParams);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiSetDeviceInstallParamsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceInstallParamsW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_DEVINSTALL_PARAMS_W deviceInstallParams);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiBuildDriverInfoList(IntPtr deviceInfoSet, IntPtr deviceInfoData, uint driverType);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiBuildDriverInfoList", SetLastError = true)]
    private static extern bool SetupDiBuildDriverInfoList(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint driverType);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDriverInfoW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint driverType,
        uint memberIndex,
        ref SP_DRVINFO_DATA_W driverInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDriverInfoDetailW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_DRVINFO_DATA_W driverInfoData,
        IntPtr driverInfoDetailData,
        uint driverInfoDetailDataSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetSelectedDriverW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_DRVINFO_DATA_W driverInfoData);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DiInstallDevice(
        IntPtr hwndParent,
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_DRVINFO_DATA_W driverInfoData,
        uint flags,
        out bool needReboot);
}
