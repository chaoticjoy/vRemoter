using System.Runtime.InteropServices;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Focus;

/// <summary>
/// Listens for global foreground window changes via SetWinEventHook(EVENT_SYSTEM_FOREGROUND).
/// Must be started and stopped on a thread with an active message loop (WPF Dispatcher).
/// </summary>
public sealed class WinEventForegroundHook : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    private readonly WinEventDelegate _proc;
    private IntPtr _hookHandle;

    public event Action<IntPtr, int>? ForegroundWindowChanged;

    public WinEventForegroundHook()
    {
        _proc = OnWinEvent;
    }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero) return;
        _hookHandle = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _proc,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);

        if (_hookHandle == IntPtr.Zero)
        {
            LogService.Write($"[FOCUS-HOOK] SetWinEventHook failed Win32={Marshal.GetLastWin32Error()}");
        }
        else
        {
            LogService.Write("[FOCUS-HOOK] EVENT_SYSTEM_FOREGROUND hook active");
        }
    }

    public void Stop()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != 0)
        {
            ForegroundWindowChanged?.Invoke(hwnd, (int)pid);
        }
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}

