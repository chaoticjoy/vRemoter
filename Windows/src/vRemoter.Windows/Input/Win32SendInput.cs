using System.Runtime.InteropServices;
using vRemoter.Core.Mapping;
using vRemoter.Core.Session;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Input;

public static class Win32SendInput
{
    public static readonly IntPtr SyntheticMarker = new(0x4D495242); // "MIRB"

    private const uint InputKeyboard = 1;
    private const uint KeyEventFExtendedKey = 0x0001;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint KeyEventFScancode = 0x0008;
    private const uint MapVkToVsc = 0;

    private static readonly object TriggerSync = new();
    private static TriggerKeyChord? _heldTrigger;

    public static void BeginRecognition()
    {
        var chord = InputTriggerSettings.ActiveChord;
        if (AppStorage.Shared.InputTriggerMode == InputTriggerMode.Toggle)
        {
            TapChord(chord);
            return;
        }

        lock (TriggerSync)
        {
            if (_heldTrigger is not null) return;
            _heldTrigger = chord;
        }
        SendChord(chord.VirtualKeys, isDown: true);
        LogService.Write($"[KEY] synthetic hold DOWN ({chord.Label})");
    }

    public static void EndRecognition()
    {
        if (AppStorage.Shared.InputTriggerMode == InputTriggerMode.Toggle)
        {
            TapChord(InputTriggerSettings.ActiveChord);
            return;
        }
        ReleaseHeldTrigger();
    }

    public static void ReleaseHeldTrigger()
    {
        TriggerKeyChord? held;
        lock (TriggerSync)
        {
            held = _heldTrigger;
            _heldTrigger = null;
        }
        if (held is null) return;
        SendChord(held.VirtualKeys.Reverse().ToArray(), isDown: false);
        LogService.Write($"[KEY] synthetic hold UP ({held.Label})");
    }

    public static void TapChord(TriggerKeyChord chord)
    {
        SendChord(chord.VirtualKeys, isDown: true);
        LogService.Write($"[KEY] synthetic tap DOWN ({chord.Label})");
        _ = Task.Run(async () =>
        {
            await Task.Delay(60);
            SendChord(chord.VirtualKeys.Reverse().ToArray(), isDown: false);
            LogService.Write($"[KEY] synthetic tap UP ({chord.Label})");
        });
    }

    public static void SendChord(IReadOnlyList<ushort> virtualKeys, bool isDown)
    {
        if (virtualKeys.Count == 0) return;
        var inputs = new INPUT[virtualKeys.Count];
        for (int i = 0; i < virtualKeys.Count; i++)
        {
            inputs[i] = BuildKeyInput(virtualKeys[i], isDown);
        }
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            int err = Marshal.GetLastWin32Error();
            LogService.Write($"[KEY] SendInput returned 0 (Win32={err}; elevated foreground may block via UIPI)");
        }
    }

    public static void SendKey(ushort vk, bool isDown) => SendChord([vk], isDown);

    public static void SendShortcut(ushort vk, bool ctrl, bool shift, bool alt, bool win, bool isDown)
    {
        var keys = new List<ushort>(5);
        if (isDown)
        {
            if (ctrl) keys.Add(0xA2);
            if (shift) keys.Add(0xA0);
            if (alt) keys.Add(0xA4);
            if (win) keys.Add(0x5B);
            keys.Add(vk);
        }
        else
        {
            keys.Add(vk);
            if (win) keys.Add(0x5B);
            if (alt) keys.Add(0xA4);
            if (shift) keys.Add(0xA0);
            if (ctrl) keys.Add(0xA2);
        }
        SendChord(keys, isDown);
    }

    public static void DispatchMappingTarget(
        RemoteMappingTarget target,
        RemoteCustomShortcut? custom,
        bool isDown,
        Action<bool>? onAppSwitcher = null,
        Action? onFocusCurrentInput = null,
        Action? onOpenApp = null)
    {
        if (target is RemoteMappingTarget.Disabled or RemoteMappingTarget.DoubaoVoice) return;

        switch (target)
        {
            case RemoteMappingTarget.LockScreen:
                if (isDown) _ = LockWorkStation();
                return;
            case RemoteMappingTarget.Sleep:
                if (isDown) _ = SetSuspendState(false, false, false);
                return;
            case RemoteMappingTarget.AppSwitcher:
                if (isDown) onAppSwitcher?.Invoke(true);
                return;
            case RemoteMappingTarget.FocusCurrentInput:
                if (isDown) onFocusCurrentInput?.Invoke();
                return;
            case RemoteMappingTarget.OpenApp:
                if (isDown) onOpenApp?.Invoke();
                return;
            case RemoteMappingTarget.Custom:
                if (custom is not null)
                {
                    SendShortcut(custom.VirtualKey, custom.Ctrl, custom.Shift, custom.Alt, custom.Win, isDown);
                }
                return;
            case RemoteMappingTarget.ShowDesktop:
                SendShortcut(0x44, ctrl: false, shift: false, alt: false, win: true, isDown); // Win+D
                return;
            case RemoteMappingTarget.Spotlight:
                SendShortcut(0x53, ctrl: false, shift: false, alt: false, win: true, isDown); // Win+S
                return;
            case RemoteMappingTarget.CommandC:
                SendShortcut(0x43, ctrl: true, shift: false, alt: false, win: false, isDown); // Ctrl+C
                return;
            case RemoteMappingTarget.CommandV:
                SendShortcut(0x56, ctrl: true, shift: false, alt: false, win: false, isDown); // Ctrl+V
                return;
            case RemoteMappingTarget.CommandZ:
                SendShortcut(0x5A, ctrl: true, shift: false, alt: false, win: false, isDown); // Ctrl+Z
                return;
        }

        if (TryGetSimpleVirtualKey(target, out ushort vk))
        {
            SendKey(vk, isDown);
        }
    }

    public static bool TryGetSimpleVirtualKey(RemoteMappingTarget target, out ushort vk)
    {
        vk = target switch
        {
            RemoteMappingTarget.ArrowUp => 0x26,
            RemoteMappingTarget.ArrowDown => 0x28,
            RemoteMappingTarget.ArrowLeft => 0x25,
            RemoteMappingTarget.ArrowRight => 0x27,
            RemoteMappingTarget.ReturnKey => 0x0D,
            RemoteMappingTarget.Escape => 0x1B,
            RemoteMappingTarget.DeleteBackward => 0x08,
            RemoteMappingTarget.Tab => 0x09,
            RemoteMappingTarget.Space => 0x20,
            RemoteMappingTarget.Home => 0x24,
            RemoteMappingTarget.End => 0x23,
            RemoteMappingTarget.PageUp => 0x21,
            RemoteMappingTarget.PageDown => 0x22,
            RemoteMappingTarget.VolumeUp => 0xAF,
            RemoteMappingTarget.VolumeDown => 0xAE,
            RemoteMappingTarget.Mute => 0xAD,
            RemoteMappingTarget.PlayPause => 0xB3,
            _ => 0
        };
        return vk != 0;
    }

    private static INPUT BuildKeyInput(ushort vk, bool isDown)
    {
        ushort scan = (ushort)MapVirtualKeyW(vk, MapVkToVsc);
        uint flags = 0;
        if (!isDown) flags |= KeyEventFKeyUp;
        if (IsExtendedKey(vk)) flags |= KeyEventFExtendedKey;
        if (scan != 0 && vk is not (0x5B or 0x5C))
        {
            // Keep scancode populated for game/IDE/IME compatibility without breaking Win keys.
        }

        return new INPUT
        {
            Type = InputKeyboard,
            Union = new INPUTUNION
            {
                Ki = new KEYBDINPUT
                {
                    WVk = vk,
                    WScan = scan,
                    DwFlags = flags,
                    Time = 0,
                    DwExtraInfo = SyntheticMarker
                }
            }
        };
    }

    private static bool IsExtendedKey(ushort vk) => vk is
        0xA5 or // RAlt
        0xA3 or // RCtrl
        0x5B or 0x5C or // LWin / RWin
        0x21 or 0x22 or 0x23 or 0x24 or // PgUp/PgDn/End/Home
        0x25 or 0x26 or 0x27 or 0x28 or // Arrows
        0x2D or 0x2E or // Ins / Del
        0xAD or 0xAE or 0xAF or 0xB3; // Media

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
        [FieldOffset(0)] public HARDWAREINPUT Hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint UMsg;
        public ushort WParamL;
        public ushort WParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}

