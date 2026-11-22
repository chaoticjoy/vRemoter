using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Devices.Radios;
using vRemoter.Windows.Input;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Bluetooth;

/// <summary>
/// Ensures a BLE remote's BTHLEDevice\{00001812-...} (HOGP HIDS) devnode is bound
/// to Microsoft's standard hidbthle.inf (mshidumdf) driver so Windows Bluetooth
/// automatically connects the remote and hosts HidOverGatt.dll in WUDFHost.exe.
/// Also provides non-destructive Bluetooth Radio cycling (ported from vibe-mote's unstick_bluetooth).
/// </summary>
public static class BleHidsDriverTakeover
{
    private const uint DiodInheritClassDrvs = 0x00000002;
    private const uint DiEnumSingleInf = 0x00010000;
    private const uint DiFlagsExAllowExcludedDrvs = 0x00000800;
    private const uint SpditClassDriver = 1;
    private const uint SpditCompatDriver = 2;

    private static DateTimeOffset _lastRadioCycleAt = DateTimeOffset.MinValue;

    public static bool EnsureHidBthLeRestored(
        ushort vendorId,
        ushort productId,
        ulong savedBluetoothAddress,
        string logTag,
        out bool repairedAnyDriver)
    {
        repairedAnyDriver = false;
        var nodes = CfgMgrDeviceResolver.FindBleHidsDevNodes(
            vendorId,
            productId,
            savedBluetoothAddress,
            includeNonPresent: true);
        if (nodes.Count == 0)
        {
            return false;
        }

        bool allHealthy = true;
        foreach (var node in nodes)
        {
            LogService.Write($"[{logTag}] BTHLE 0x1812 node={node.InstanceId} svc='{node.Service}' inf='{node.DriverInf}' status=0x{node.Status:X8} problem={node.ProblemCode}");
            if (node.IsHidBthLeBound && node.ProblemCode == 0)
            {
                continue;
            }

            if (!WindowsVersionInfo.IsElevated)
            {
                LogService.Write($"[{logTag}] 0x1812 needs hidbthle.inf restore (not elevated)");
                allHealthy = false;
                continue;
            }

            if (TryBindDevNodeToInf(node.InstanceId, "hidbthle.inf", MatchHidBthLeDriver, logTag))
            {
                repairedAnyDriver = true;
                if (!string.IsNullOrEmpty(node.ParentInstanceId))
                {
                    RestartDeviceViaPnpUtil(node.ParentInstanceId);
                }
            }
            else
            {
                allHealthy = false;
            }
        }
        return allHealthy;
    }

    public static async Task<bool> TryCycleBluetoothRadioAsync(string logTag, bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastRadioCycleAt < TimeSpan.FromMinutes(5))
        {
            return false;
        }
        _lastRadioCycleAt = now;

        try
        {
            var radios = await Radio.GetRadiosAsync();
            var btRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (btRadio is null)
            {
                return false;
            }

            if (btRadio.State != RadioState.On)
            {
                for (int i = 0; i < 3; i++)
                {
                    _ = await btRadio.SetStateAsync(RadioState.On);
                    await Task.Delay(1200);
                    if (btRadio.State == RadioState.On)
                    {
                        LogService.Write($"[{logTag}] Bluetooth radio was OFF -> turned ON");
                        return true;
                    }
                }
                return false;
            }

            LogService.Write($"[{logTag}] cycling Bluetooth radio OFF/ON to restore BLE+HID link...");
            _ = await btRadio.SetStateAsync(RadioState.Off);
            await Task.Delay(2500);

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                _ = await btRadio.SetStateAsync(RadioState.On);
                await Task.Delay(1800);
                if (btRadio.State == RadioState.On)
                {
                    LogService.Write($"[{logTag}] Bluetooth radio confirmed back ON (attempt={attempt})");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Write($"[{logTag}] Bluetooth radio cycle warning: {ex.Message}");
        }
        return false;
    }

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
