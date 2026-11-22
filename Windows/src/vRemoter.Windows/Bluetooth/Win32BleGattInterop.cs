using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace vRemoter.Windows.Bluetooth;

/// <summary>
/// Win32 BluetoothAPIs.dll (bluetoothleapis.h / bthledef.h) P/Invoke structures
/// for direct kernel/BthLEEnum GATT service, characteristic, descriptor, and
/// notification registration without WinRT UWP service UUID restrictions.
/// </summary>
internal static class Win32BleGattInterop
{
    public static readonly Guid GattServiceInterfaceGuid = new("6e3bb679-4372-40c8-9eaa-4509df260cd8");
    public static readonly Guid HidsServiceGuid = new("00001812-0000-1000-8000-00805f9b34fb");

    public const ushort UuidHidsService = 0x1812;
    public const ushort UuidReportMap = 0x2A4B;
    public const ushort UuidHidReport = 0x2A4D;
    public const ushort UuidBootKeyboardInput = 0x2A22;
    public const ushort UuidCccd = 0x2902;
    public const ushort UuidReportReference = 0x2908;

    public const int DescriptorTypeClientCharacteristicConfiguration = 3;
    public const int CharacteristicValueChangedEvent = 0;

    public const uint BluetoothGattFlagNone = 0x00000000;
    public const uint BluetoothGattFlagConnectionEncrypted = 0x00000001;
    public const uint BluetoothGattFlagForceReadFromDevice = 0x00000004;

    public const int HrMoreData = unchecked((int)0x800700EA);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void PfnBluetoothGattEventCallback(
        int eventType,
        IntPtr eventOutParameter,
        IntPtr context);

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct BTH_LE_UUID
    {
        [FieldOffset(0)] public byte IsShortUuid;
        [FieldOffset(4)] public ushort ShortUuid;
        [FieldOffset(4)] public Guid LongUuid;

        public bool MatchesShort(ushort expected)
        {
            if (IsShortUuid != 0)
            {
                return ShortUuid == expected;
            }
            byte[] bytes = LongUuid.ToByteArray();
            uint prefix = BitConverter.ToUInt32(bytes, 0);
            return prefix == expected;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BTH_LE_GATT_SERVICE
    {
        public BTH_LE_UUID ServiceUuid;
        public ushort AttributeHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BTH_LE_GATT_CHARACTERISTIC
    {
        public ushort ServiceHandle;
        public BTH_LE_UUID CharacteristicUuid;
        public ushort AttributeHandle;
        public ushort CharacteristicValueHandle;
        public byte IsBroadcastable;
        public byte IsReadable;
        public byte IsWritable;
        public byte IsWritableWithoutResponse;
        public byte IsSignedWritable;
        public byte IsNotifiable;
        public byte IsIndicatable;
        public byte HasExtendedProperties;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BTH_LE_GATT_DESCRIPTOR
    {
        public ushort ServiceHandle;
        public ushort CharacteristicHandle;
        public int DescriptorType;
        public BTH_LE_UUID DescriptorUuid;
        public ushort AttributeHandle;
    }

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    public struct BTH_LE_GATT_DESCRIPTOR_VALUE
    {
        [FieldOffset(0)] public int DescriptorType;
        [FieldOffset(4)] public BTH_LE_UUID DescriptorUuid;
        [FieldOffset(24)] public byte IsSubscribeToNotification;
        [FieldOffset(25)] public byte IsSubscribeToIndication;
        [FieldOffset(72)] public uint DataSize;
        [FieldOffset(76)] public byte Data0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLUETOOTH_GATT_VALUE_CHANGED_EVENT
    {
        public ushort ChangedAttributeHandle;
        public UIntPtr CharacteristicValueDataSize;
        public IntPtr CharacteristicValue;
    }

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    public static extern int BluetoothGATTGetServices(
        SafeFileHandle hDevice,
        ushort servicesBufferCount,
        [Out] BTH_LE_GATT_SERVICE[]? servicesBuffer,
        out ushort servicesBufferActual,
        uint flags);

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    public static extern int BluetoothGATTGetCharacteristics(
        SafeFileHandle hDevice,
        IntPtr service,
        ushort characteristicsBufferCount,
        [Out] BTH_LE_GATT_CHARACTERISTIC[]? characteristicsBuffer,
        out ushort characteristicsBufferActual,
        uint flags);

    [DllImport("BluetoothAPIs.dll", EntryPoint = "BluetoothGATTGetCharacteristics", SetLastError = true)]
    public static extern int BluetoothGATTGetCharacteristics(
        SafeFileHandle hDevice,
        ref BTH_LE_GATT_SERVICE service,
        ushort characteristicsBufferCount,
        [Out] BTH_LE_GATT_CHARACTERISTIC[]? characteristicsBuffer,
        out ushort characteristicsBufferActual,
        uint flags);

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    public static extern int BluetoothGATTGetCharacteristicValue(
        SafeFileHandle hDevice,
        ref BTH_LE_GATT_CHARACTERISTIC characteristic,
        uint characteristicValueDataSize,
        IntPtr characteristicValue,
        out ushort characteristicValueSizeRequired,
        uint flags);

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    public static extern int BluetoothGATTGetDescriptors(
        SafeFileHandle hDevice,
        ref BTH_LE_GATT_CHARACTERISTIC characteristic,
        ushort descriptorsBufferCount,
        [Out] BTH_LE_GATT_DESCRIPTOR[]? descriptorsBuffer,
        out ushort descriptorsBufferActual,
        uint flags);

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    public static extern int BluetoothGATTGetDescriptorValue(
        SafeFileHandle hDevice,
        ref BTH_LE_GATT_DESCRIPTOR descriptor,
        uint descriptorValueDataSize,
        IntPtr descriptorValue,
        out ushort descriptorValueSizeRequired,
        uint flags);

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    public static extern int BluetoothGATTSetDescriptorValue(
        SafeFileHandle hDevice,
        ref BTH_LE_GATT_DESCRIPTOR descriptor,
        ref BTH_LE_GATT_DESCRIPTOR_VALUE descriptorValue,
        uint flags);

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    public static extern int BluetoothGATTRegisterEvent(
        SafeFileHandle hService,
        int eventType,
        IntPtr eventParameterIn,
        PfnBluetoothGattEventCallback callback,
        IntPtr callbackContext,
        out IntPtr eventHandle,
        uint flags);

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    public static extern int BluetoothGATTUnregisterEvent(
        IntPtr eventHandle,
        uint flags);

    public static BTH_LE_GATT_CHARACTERISTIC[] QueryHidsCharacteristics(SafeFileHandle handle, out int hr)
    {
        hr = BluetoothGATTGetCharacteristics(handle, IntPtr.Zero, 0, null, out ushort count, BluetoothGattFlagNone);
        if (count > 0)
        {
            var buf = new BTH_LE_GATT_CHARACTERISTIC[count];
            hr = BluetoothGATTGetCharacteristics(handle, IntPtr.Zero, count, buf, out ushort actual, BluetoothGattFlagNone);
            if (hr == 0 && actual > 0) return buf[..actual];
        }

        _ = BluetoothGATTGetServices(handle, 0, null, out ushort svcCount, BluetoothGattFlagNone);
        if (svcCount == 0) return [];
        var svcs = new BTH_LE_GATT_SERVICE[svcCount];
        if (BluetoothGATTGetServices(handle, svcCount, svcs, out ushort svcActual, BluetoothGattFlagNone) != 0)
        {
            return [];
        }

        for (int i = 0; i < svcActual; i++)
        {
            var svc = svcs[i];
            if (!svc.ServiceUuid.MatchesShort(UuidHidsService) && svcActual > 1) continue;
            hr = BluetoothGATTGetCharacteristics(handle, ref svc, 0, null, out count, BluetoothGattFlagNone);
            if (count == 0) continue;
            var buf = new BTH_LE_GATT_CHARACTERISTIC[count];
            hr = BluetoothGATTGetCharacteristics(handle, ref svc, count, buf, out ushort actual, BluetoothGattFlagNone);
            if (hr == 0 && actual > 0) return buf[..actual];
        }
        return [];
    }

    public static byte[]? TryReadCharacteristicBytes(SafeFileHandle handle, ref BTH_LE_GATT_CHARACTERISTIC ch)
    {
        _ = BluetoothGATTGetCharacteristicValue(handle, ref ch, 0, IntPtr.Zero, out ushort req, BluetoothGattFlagNone);
        if (req <= 4) return null;
        IntPtr buf = Marshal.AllocHGlobal(req);
        try
        {
            int hr = BluetoothGATTGetCharacteristicValue(handle, ref ch, req, buf, out _, BluetoothGattFlagNone);
            if (hr != 0)
            {
                hr = BluetoothGATTGetCharacteristicValue(handle, ref ch, req, buf, out _, BluetoothGattFlagForceReadFromDevice);
            }
            if (hr != 0) return null;
            int dataSize = Marshal.ReadInt32(buf, 0);
            if (dataSize <= 0 || dataSize + 4 > req) return null;
            byte[] data = new byte[dataSize];
            Marshal.Copy(IntPtr.Add(buf, 4), data, 0, dataSize);
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public static byte[]? TryReadDescriptorBytes(SafeFileHandle handle, ref BTH_LE_GATT_DESCRIPTOR desc)
    {
        _ = BluetoothGATTGetDescriptorValue(handle, ref desc, 0, IntPtr.Zero, out ushort req, BluetoothGattFlagNone);
        if (req <= 76) return null;
        IntPtr buf = Marshal.AllocHGlobal(req);
        try
        {
            if (BluetoothGATTGetDescriptorValue(handle, ref desc, req, buf, out _, BluetoothGattFlagNone) != 0)
            {
                return null;
            }
            int dataSize = Marshal.ReadInt32(buf, 72);
            if (dataSize <= 0 || 76 + dataSize > req) return null;
            byte[] data = new byte[dataSize];
            Marshal.Copy(IntPtr.Add(buf, 76), data, 0, dataSize);
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
