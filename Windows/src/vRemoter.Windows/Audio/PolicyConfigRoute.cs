using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using vRemoter.Core.Session;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Audio;

/// <summary>
/// Switches the Windows default capture endpoint via undocumented IPolicyConfig COM
/// with multi-IID fallback across Windows 10 (1809–22H2) and Windows 11 (21H2–24H2).
/// Used for WeType (微信输入法), which follows the system default microphone.
/// </summary>
public sealed class PolicyConfigRoute
{
    private static readonly Guid ClsidPolicyConfigClient = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    private static readonly Guid[] CandidateIids =
    [
        new("f8679f50-850a-41cf-9c72-430f290290c8"), // IPolicyConfig (Win7-Win11)
        new("ca286fc5-91fd-42c3-8e9b-caafa66242e3"), // IPolicyConfigRedstone (Win10/Win11)
        new("568b9108-44bf-40b4-9006-86afe5b5a620")  // IPolicyConfigVista fallback
    ];

    private string? _previousDefaultCaptureId;
    private string? _routedVirtualCaptureId;

    public void Apply(VoiceInputMethodKind method)
    {
        if (method == VoiceInputMethodKind.WeType)
        {
            RouteToVirtualMic();
        }
        else
        {
            Restore();
        }
    }

    public void RouteToVirtualMic()
    {
        var route = VirtualAudioDetector.DetectRoute(AppStorage.Shared.PreferredVirtualAudioDevice);
        if (route is null)
        {
            LogService.Write("[MIC-ROUTE] virtual capture endpoint unavailable");
            return;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? currentDefault = null;
            try
            {
                currentDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
            catch { }

            if (currentDefault is not null &&
                string.Equals(currentDefault.ID, route.CaptureDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Write($"[MIC-ROUTE] system default capture already {route.CaptureFriendlyName}");
                return;
            }

            if (_previousDefaultCaptureId is null && currentDefault is not null)
            {
                _previousDefaultCaptureId = currentDefault.ID;
            }

            if (SetDefaultCaptureEndpoint(route.CaptureDeviceId))
            {
                _routedVirtualCaptureId = route.CaptureDeviceId;
                LogService.Write($"[MIC-ROUTE] system capture -> {route.CaptureFriendlyName}");
            }
        }
        catch (Exception ex)
        {
            LogService.Write($"[MIC-ROUTE] route failed: {ex.Message}");
        }
    }

    public void Restore()
    {
        string? prev = _previousDefaultCaptureId;
        string? routed = _routedVirtualCaptureId;
        _previousDefaultCaptureId = null;
        _routedVirtualCaptureId = null;
        if (string.IsNullOrEmpty(prev) || string.IsNullOrEmpty(routed)) return;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            if (!string.Equals(current.ID, routed, StringComparison.OrdinalIgnoreCase))
            {
                return; // User switched mic manually; leave their choice intact.
            }

            var target = enumerator.GetDevice(prev);
            if (target.State == DeviceState.Active && SetDefaultCaptureEndpoint(prev))
            {
                LogService.Write($"[MIC-ROUTE] system capture restored -> {target.FriendlyName}");
            }
        }
        catch (Exception ex)
        {
            LogService.Write($"[MIC-ROUTE] restore failed: {ex.Message}");
        }
    }

    private static bool SetDefaultCaptureEndpoint(string deviceId)
    {
        var type = Type.GetTypeFromCLSID(ClsidPolicyConfigClient, throwOnError: false);
        if (type is null) return false;

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            if (instance is null) return false;

            IntPtr punk = Marshal.GetIUnknownForObject(instance);
            try
            {
                foreach (var iid in CandidateIids)
                {
                    Guid g = iid;
                    if (Marshal.QueryInterface(punk, ref g, out IntPtr iface) == 0 && iface != IntPtr.Zero)
                    {
                        try
                        {
                            // IPolicyConfig::SetDefaultEndpoint is slot 13 in the vtable (3 IUnknown + 10 prior methods).
                            IntPtr vtable = Marshal.ReadIntPtr(iface);
                            IntPtr methodPtr = Marshal.ReadIntPtr(vtable, 13 * IntPtr.Size);
                            var fn = Marshal.GetDelegateForFunctionPointer<SetDefaultEndpointDelegate>(methodPtr);
                            int hr0 = fn(iface, deviceId, 0); // eConsole
                            int hr1 = fn(iface, deviceId, 1); // eMultimedia
                            int hr2 = fn(iface, deviceId, 2); // eCommunications
                            if (hr0 == 0 && hr1 == 0 && hr2 == 0)
                            {
                                return true;
                            }
                            LogService.Write($"[MIC-ROUTE] IPolicyConfig({iid}) returned hr={hr0:X},{hr1:X},{hr2:X}");
                        }
                        finally
                        {
                            Marshal.Release(iface);
                        }
                    }
                }
            }
            finally
            {
                Marshal.Release(punk);
            }
        }
        catch (Exception ex)
        {
            LogService.Write($"[MIC-ROUTE] SetDefaultCaptureEndpoint error: {ex.Message}");
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.ReleaseComObject(instance);
            }
        }
        return false;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetDefaultEndpointDelegate(
        IntPtr pThis,
        [MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId,
        int role);
}

