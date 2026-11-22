using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using Windows.Devices.Radios;
using Windows.Foundation.Metadata;

namespace vRemoter.Windows.SystemSupport;

/// <summary>
/// Probes true Windows OS build and runtime capabilities across Windows 10 (1809+) and Windows 11 (21H2–24H2).
/// </summary>
public static class WindowsVersionInfo
{
    private const int DwmwaUseImmersiveDarkModeOld = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    public static Version OsVersion { get; } = ReadRtlVersion();
    public static int BuildNumber => OsVersion.Build;
    public static bool IsWindows11 => BuildNumber >= 22000;
    public static bool SupportsDarkTitleBarV20 => BuildNumber >= 18985;
    public static bool SupportsDarkTitleBarV19 => BuildNumber >= 17763;
    public static Architecture ProcessArchitecture => RuntimeInformation.ProcessArchitecture;
    public static bool IsArm64 => ProcessArchitecture == Architecture.Arm64;

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public static string FriendlySummary =>
        $"Windows {(IsWindows11 ? "11" : "10")} (Build {BuildNumber}, {ProcessArchitecture}, {(IsElevated ? "Admin" : "User")})";

    public static void ApplyDarkWindowFrame(Window window)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useDark = 1;
            if (SupportsDarkTitleBarV20)
            {
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
            }
            else if (SupportsDarkTitleBarV19)
            {
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref useDark, sizeof(int));
            }

            if (IsWindows11)
            {
                int round = DwmwcpRound;
                _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
            }
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply();
        }
        else
        {
            window.SourceInitialized += (_, _) => Apply();
        }
    }

    public static async Task<(bool Supported, bool RadioOn)> CheckBluetoothRadioAsync()
    {
        try
        {
            if (!ApiInformation.IsTypePresent("Windows.Devices.Radios.Radio"))
            {
                return (false, false);
            }
            var radios = await Radio.GetRadiosAsync();
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (bt is null) return (false, false);
            return (true, bt.State == RadioState.On);
        }
        catch
        {
            return (false, false);
        }
    }

    private static Version ReadRtlVersion()
    {
        try
        {
            var info = new RtlOsVersionInfoEx { OSVersionInfoSize = (uint)Marshal.SizeOf<RtlOsVersionInfoEx>() };
            if (RtlGetVersion(ref info) == 0)
            {
                return new Version((int)info.MajorVersion, (int)info.MinorVersion, (int)info.BuildNumber);
            }
        }
        catch
        {
        }
        return Environment.OSVersion.Version;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RtlOsVersionInfoEx
    {
        public uint OSVersionInfoSize;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint PlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string CSDVersion;
    }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern int RtlGetVersion(ref RtlOsVersionInfoEx versionInfo);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}

