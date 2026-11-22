using System.Diagnostics;
using System.Runtime.InteropServices;
using vRemoter.Core.Input;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Input;

/// <summary>
/// Global WH_KEYBOARD_LL hook with "MIRB" synthetic pass-through,
/// 1.5s X6 Search suppression gate, per-device KeyCorrelationEngine check
/// (ported from 10RemoteHub's win_hid_suppress.py), and physical trigger observation.
/// </summary>
public sealed class LowLevelKeyboardGuard : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private const ushort VkBrowserSearch = 0xAA;
    private const ushort VkBrowserBack = 0xA6;
    private const ushort VkBrowserHome = 0xAC;
    private const ushort VkApps = 0x5D;
    private const ushort VkVolumeMute = 0xAD;
    private const ushort VkVolumeDown = 0xAE;
    private const ushort VkVolumeUp = 0xAF;
    private const ushort VkSleep = 0x5F;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelKeyboardProc _proc;
    private readonly HashSet<ushort> _physicalKeysDown = [];
    private readonly HashSet<ushort> _suppressedSystemKeys = [];

    private IntPtr _hookHandle;
    private long _searchSuppressUntilTick;
    private long _remoteSuppressUntilTick;
    private bool _chordTriggeredDown;
    private long _inputActivityStamp;

    public KeyCorrelationEngine Correlation { get; } = new();
    public bool IsHookActive => _hookHandle != IntPtr.Zero;
    public long InputActivityStamp => Interlocked.Read(ref _inputActivityStamp);

    public event Action<bool>? TriggerDownObserved;
    public event Action<bool>? TriggerUpObserved;

    public LowLevelKeyboardGuard()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero) return;
        using var proc = Process.GetCurrentProcess();
        using var mod = proc.MainModule;
        IntPtr hMod = mod is not null ? GetModuleHandleW(mod.ModuleName) : IntPtr.Zero;
        _hookHandle = SetWindowsHookExW(WhKeyboardLl, _proc, hMod, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            LogService.Write($"[KEY-HOOK] SetWindowsHookEx(WH_KEYBOARD_LL) failed Win32={err}");
        }
        else
        {
            LogService.Write("[KEY-HOOK] WH_KEYBOARD_LL active");
        }
    }

    public void Stop()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _physicalKeysDown.Clear();
        _suppressedSystemKeys.Clear();
        Correlation.Clear();
        _chordTriggeredDown = false;
    }

    public void ArmSearchSuppression(int milliseconds = 1500)
    {
        long candidate = Environment.TickCount64 + milliseconds;
        long current = Interlocked.Read(ref _searchSuppressUntilTick);
        if (candidate > current)
        {
            Interlocked.Exchange(ref _searchSuppressUntilTick, candidate);
        }
    }

    /// <summary>
    /// Arms short suppression for remote-mapped keys so Windows does not
    /// double-execute native volume/back/search/apps/navigation actions.
    /// </summary>
    public void ArmRemoteKeySuppression(ushort vk, int milliseconds = 350)
    {
        lock (_suppressedSystemKeys)
        {
            _suppressedSystemKeys.Add(vk);
        }
        Interlocked.Exchange(ref _remoteSuppressUntilTick, Environment.TickCount64 + milliseconds);
    }

    public void TriggerConfigurationChanged()
    {
        _physicalKeysDown.Clear();
        _chordTriggeredDown = false;
        LogService.Write($"[INPUT-TRIGGER] listening for {InputTriggerSettings.ActiveChord.Label}");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if (kb.DwExtraInfo == Win32SendInput.SyntheticMarker)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        Interlocked.Increment(ref _inputActivityStamp);

        int msg = wParam.ToInt32();
        bool isDown = msg is WmKeyDown or WmSysKeyDown;
        bool isUp = msg is WmKeyUp or WmSysKeyUp;
        if (!isDown && !isUp)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        ushort vk = (ushort)kb.VkCode;
        var activeChord = InputTriggerSettings.ActiveChord;

        if (Environment.TickCount64 <= Interlocked.Read(ref _searchSuppressUntilTick))
        {
            bool isSearchKey = vk == VkBrowserSearch;
            bool isDelayedModifier = InputTriggerSettings.IsFamilyMatch(vk, activeChord) &&
                                     !_physicalKeysDown.Contains(vk);
            if (isSearchKey || isDelayedModifier)
            {
                return (IntPtr)1;
            }
        }

        if (Environment.TickCount64 <= Interlocked.Read(ref _remoteSuppressUntilTick) &&
            IsRemappableRemoteKey(vk))
        {
            // Never suppress if KeyCorrelationEngine proves the key came from the main PC keyboard!
            bool? isRemote = Correlation.TryDecideIsRemote(vk, isDown);
            if (isRemote != false)
            {
                bool suppress;
                lock (_suppressedSystemKeys)
                {
                    suppress = _suppressedSystemKeys.Contains(vk);
                    if (isUp) _suppressedSystemKeys.Remove(vk);
                }
                if (suppress)
                {
                    return (IntPtr)1;
                }
            }
        }

        ObservePhysicalTrigger(vk, isDown, activeChord);
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void ObservePhysicalTrigger(ushort vk, bool isDown, TriggerKeyChord chord)
    {
        if (isDown)
        {
            _physicalKeysDown.Add(vk);
            if (!_chordTriggeredDown && AllChordKeysDown(chord))
            {
                _chordTriggeredDown = true;
                Task.Run(() => TriggerDownObserved?.Invoke(false));
            }
        }
        else
        {
            _physicalKeysDown.Remove(vk);
            if (_chordTriggeredDown && !AllChordKeysDown(chord))
            {
                _chordTriggeredDown = false;
                Task.Run(() => TriggerUpObserved?.Invoke(false));
            }
        }
    }

    private bool AllChordKeysDown(TriggerKeyChord chord)
    {
        if (chord.VirtualKeys.Length == 0) return false;
        foreach (ushort required in chord.VirtualKeys)
        {
            if (!_physicalKeysDown.Contains(required))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsRemappableRemoteKey(ushort vk) => vk is
        VkBrowserSearch or VkBrowserBack or VkBrowserHome or
        VkApps or VkVolumeMute or VkVolumeDown or VkVolumeUp or VkSleep or
        0x08 or 0x0D or 0x1B or 0x21 or 0x22 or 0x25 or 0x26 or 0x27 or 0x28;

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
