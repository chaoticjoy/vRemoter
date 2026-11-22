using System.Runtime.InteropServices;
using System.Text;
using vRemoter.Core.Input;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Input;

public sealed record BleHidsDevNodeInfo(
    string InstanceId,
    string ParentInstanceId,
    ushort VendorId,
    ushort ProductId,
    ulong BluetoothAddress,
    string Service,
    string DriverInf,
    uint Status,
    uint ProblemCode)
{
    public bool IsHidBthLeBound =>
        string.Equals(Service, "mshidumdf", StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrEmpty(DriverInf) || DriverInf.Contains("hidbthle", StringComparison.OrdinalIgnoreCase));

    public bool IsGenericGattBound =>
        !string.Equals(Service, "mshidumdf", StringComparison.OrdinalIgnoreCase) &&
        !DriverInf.Contains("hidbthle", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Read-only Windows cfgmgr32 topology resolver (ported from 10RemoteHub's windows_probe_metadata.py).
/// Walks parent devnodes to resolve VID/PID for RawInput/HID interfaces and inspects
/// BTHLEDEVICE 0x1812 devnodes for driver binding and PnP problem codes.
/// </summary>
public static class CfgMgrDeviceResolver
{
    private const string HidsGattUuid = "00001812-0000-1000-8000-00805F9B34FB";
    private const uint CmGetIdListFilterEnumerator = 0x00000001;
    private const uint CmGetIdListFilterPresent = 0x00000100;
    private const uint CmLocateDevNodePhantom = 0x00000001;
    private const uint DevPropTypeString = 0x00000012;

    private static readonly DEVPROPKEY PKeyDeviceInstanceId = new(
        new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    private static readonly DEVPROPKEY PKeyDeviceService = new(
        new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 6);

    private static readonly DEVPROPKEY PKeyDeviceDriverInfPath = new(
        new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), 5);

    public static bool TryResolveInterfaceVidPid(string? deviceInterfacePath, out ushort vid, out ushort pid)
    {
        if (HidDevicePathParser.TryParseVidPid(deviceInterfacePath, out vid, out pid))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(deviceInterfacePath))
        {
            return false;
        }

        try
        {
            string instanceId = GetInterfaceStringProperty(deviceInterfacePath, PKeyDeviceInstanceId);
            if (HidDevicePathParser.TryParseVidPid(instanceId, out vid, out pid))
            {
                return true;
            }
            if (string.IsNullOrEmpty(instanceId) || CM_Locate_DevNodeW(out uint node, instanceId, 0) != 0)
            {
                return false;
            }

            for (int depth = 0; depth < 16 && node != 0; depth++)
            {
                string currentId = GetDevNodeInstanceId(node);
                if (HidDevicePathParser.TryParseVidPid(currentId, out vid, out pid))
                {
                    return true;
                }
                if (CM_Get_Parent(out uint parent, node, 0) != 0)
                {
                    break;
                }
                node = parent;
            }
        }
        catch
        {
        }

        vid = 0;
        pid = 0;
        return false;
    }

    public static IReadOnlyList<BleHidsDevNodeInfo> FindBleHidsDevNodes(
        ushort targetVid,
        ushort targetPid,
        ulong savedBluetoothAddress = 0,
        bool includeNonPresent = false)
    {
        var results = new List<BleHidsDevNodeInfo>();
        try
        {
            uint flags = includeNonPresent
                ? CmGetIdListFilterEnumerator
                : CmGetIdListFilterEnumerator | CmGetIdListFilterPresent;
            if (CM_Get_Device_ID_List_SizeW(out uint charCount, "BTHLEDEVICE", flags) != 0 ||
                charCount == 0 || charCount > 262_144)
            {
                return results;
            }

            char[] buffer = new char[charCount];
            if (CM_Get_Device_ID_ListW("BTHLEDEVICE", buffer, charCount, flags) != 0)
            {
                return results;
            }

            uint locateFlags = includeNonPresent ? CmLocateDevNodePhantom : 0;
            string all = new(buffer);
            foreach (string rawInstance in all.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!rawInstance.Contains(HidsGattUuid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (CM_Locate_DevNodeW(out uint node, rawInstance, locateFlags) != 0 || node == 0)
                {
                    continue;
                }

                string parentInstanceId = string.Empty;
                if (CM_Get_Parent(out uint immediateParent, node, 0) == 0 && immediateParent != 0)
                {
                    parentInstanceId = GetDevNodeInstanceId(immediateParent);
                }

                ushort vid = 0;
                ushort pid = 0;
                if (!HidDevicePathParser.TryParseVidPid(rawInstance, out vid, out pid))
                {
                    uint cur = node;
                    for (int depth = 0; depth < 8 && cur != 0; depth++)
                    {
                        if (CM_Get_Parent(out uint parent, cur, 0) != 0) break;
                        cur = parent;
                        if (HidDevicePathParser.TryParseVidPid(GetDevNodeInstanceId(cur), out vid, out pid))
                        {
                            break;
                        }
                    }
                }

                _ = HidDevicePathParser.TryParseBluetoothAddress(rawInstance, out ulong mac);
                bool matchesVidPid = vid == targetVid && pid == targetPid;
                bool matchesMac = savedBluetoothAddress != 0 && mac == savedBluetoothAddress;
                if (!matchesVidPid && !matchesMac)
                {
                    continue;
                }

                _ = CM_Get_DevNode_Status(out uint status, out uint problem, node, 0);
                string service = GetDevNodeStringProperty(node, PKeyDeviceService);
                string inf = GetDevNodeStringProperty(node, PKeyDeviceDriverInfPath);

                results.Add(new BleHidsDevNodeInfo(
                    rawInstance,
                    parentInstanceId,
                    vid != 0 ? vid : targetVid,
                    pid != 0 ? pid : targetPid,
                    mac,
                    service,
                    inf,
                    status,
                    problem));
            }
        }
        catch (Exception ex)
        {
            LogService.Write($"[CFGMGR] FindBleHidsDevNodes error: {ex.Message}");
        }
        return results;
    }

    private static string GetDevNodeInstanceId(uint devInst)
    {
        var sb = new StringBuilder(512);
        return CM_Get_Device_IDW(devInst, sb, (uint)sb.Capacity, 0) == 0 ? sb.ToString() : string.Empty;
    }

    private static string GetInterfaceStringProperty(string path, DEVPROPKEY key)
    {
        uint size = 1024;
        byte[] buf = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(path, ref key, out uint propType, buf, ref size, 0) != 0 ||
            propType != DevPropTypeString || size < 2)
        {
            return string.Empty;
        }
        return Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
    }

    private static string GetDevNodeStringProperty(uint devInst, DEVPROPKEY key)
    {
        uint size = 1024;
        byte[] buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out uint propType, buf, ref size, 0) != 0 ||
            propType != DevPropTypeString || size < 2)
        {
            return string.Empty;
        }
        return Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY(Guid fmtid, uint pid)
    {
        public Guid Fmtid = fmtid;
        public uint Pid = pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint dnDevInst, StringBuilder buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string pszFilter, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(
        uint dnDevInst,
        ref DEVPROPKEY propertyKey,
        out uint propertyType,
        [Out] byte[] propertyBuffer,
        ref uint propertyBufferSize,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_PropertyW(
        string pszDeviceInterface,
        ref DEVPROPKEY propertyKey,
        out uint propertyType,
        [Out] byte[] propertyBuffer,
        ref uint propertyBufferSize,
        uint ulFlags);
}

