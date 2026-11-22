using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Input;

public sealed record SwitcherWindowItem(IntPtr Handle, string Title, string ProcessName, int ProcessId);

/// <summary>
/// Built-in dark overlay app switcher (UIPI-immune across Win10/11),
/// cycling through top-level desktop windows with a 1-second auto-commit timer.
/// </summary>
public sealed class AppSwitcherSession : IDisposable
{
    private const int SwRestore = 9;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int DwmwaCloaked = 14;
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(1.0);

    private readonly object _sync = new();
    private List<SwitcherWindowItem> _items = [];
    private int _selectedIndex;
    private Timer? _commitTimer;
    private OverlayAppSwitcherWindow? _overlay;

    public bool IsActive { get; private set; }

    public void TapSwitcher()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!IsActive)
            {
                _items = EnumerateSwitchableWindows();
                if (_items.Count == 0) return;

                IsActive = true;
                _selectedIndex = _items.Count > 1 ? 1 : 0;
                EnsureOverlay().UpdateItems(_items, _selectedIndex);
                _overlay?.Show();
            }
            else if (_items.Count > 0)
            {
                _selectedIndex = (_selectedIndex + 1) % _items.Count;
                _overlay?.UpdateItems(_items, _selectedIndex);
            }
            ScheduleCommit();
        });
    }

    public void RedirectArrow(bool isRight)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!IsActive || _items.Count == 0) return;
            _selectedIndex = isRight
                ? (_selectedIndex + 1) % _items.Count
                : (_selectedIndex - 1 + _items.Count) % _items.Count;
            _overlay?.UpdateItems(_items, _selectedIndex);
            ScheduleCommit();
        });
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _commitTimer?.Dispose();
            _commitTimer = null;
            IsActive = false;
        }
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _overlay?.Hide();
        });
    }

    private void CommitSelection()
    {
        SwitcherWindowItem? chosen = null;
        lock (_sync)
        {
            _commitTimer?.Dispose();
            _commitTimer = null;
            if (!IsActive) return;
            IsActive = false;
            if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
            {
                chosen = _items[_selectedIndex];
            }
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _overlay?.Hide();
            if (chosen is not null)
            {
                BringWindowToForeground(chosen.Handle);
            }
        });
    }

    private void ScheduleCommit()
    {
        lock (_sync)
        {
            _commitTimer?.Dispose();
            _commitTimer = new Timer(_ => CommitSelection(), null, IdleInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private OverlayAppSwitcherWindow EnsureOverlay()
    {
        _overlay ??= new OverlayAppSwitcherWindow();
        return _overlay;
    }

    public static void BringWindowToForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;

        if (IsIconic(hwnd))
        {
            _ = ShowWindow(hwnd, SwRestore);
        }

        IntPtr foreground = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint fgThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);

        bool attachedFg = false;
        bool attachedTarget = false;
        try
        {
            if (fgThread != 0 && fgThread != currentThread)
            {
                attachedFg = AttachThreadInput(currentThread, fgThread, true);
            }
            if (targetThread != 0 && targetThread != currentThread)
            {
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);
            }
            _ = BringWindowToTop(hwnd);
            _ = SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedTarget) _ = AttachThreadInput(currentThread, targetThread, false);
            if (attachedFg) _ = AttachThreadInput(currentThread, fgThread, false);
        }
    }

    public static List<SwitcherWindowItem> EnumerateSwitchableWindows()
    {
        var results = new List<SwitcherWindowItem>();
        int selfPid = Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;

            int exStyle = GetWindowLongW(hwnd, GwlExStyle);
            if ((exStyle & WsExToolWindow) != 0) return true;

            int cloaked = 0;
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            int len = GetWindowTextLengthW(hwnd);
            if (len <= 0) return true;

            var sb = new StringBuilder(len + 1);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if ((int)pid == selfPid) return true;

            string procName = string.Empty;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                procName = p.ProcessName;
            }
            catch { }

            if (procName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                procName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            results.Add(new SwitcherWindowItem(hwnd, title, procName, (int)pid));
            return results.Count < 14;
        }, IntPtr.Zero);

        return results;
    }

    public void Dispose()
    {
        Cancel();
        Application.Current?.Dispatcher.BeginInvoke(() => _overlay?.Close());
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}

