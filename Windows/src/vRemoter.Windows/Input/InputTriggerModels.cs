using System.Runtime.InteropServices;
using System.Text.Json;
using vRemoter.Core.Localization;
using vRemoter.Core.Mapping;
using vRemoter.Core.Session;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Input;

public enum InputTriggerPreset
{
    RightAlt,
    LeftAlt,
    LeftControl,
    RightControl,
    LeftShift,
    RightShift,
    LeftWin,
    RightWin,
    CtrlWin,
    CtrlWinShift,
    Custom
}

public sealed record TriggerKeyChord(ushort[] VirtualKeys, string Label)
{
    public static TriggerKeyChord FromPreset(InputTriggerPreset preset, TriggerKeyChord? custom = null) => preset switch
    {
        InputTriggerPreset.LeftAlt => new([0xA4], L10n.Text("左 Alt", "Left Alt")),
        InputTriggerPreset.RightAlt => new([0xA5], L10n.Text("右 Alt (豆包默认)", "Right Alt (Doubao)")),
        InputTriggerPreset.LeftControl => new([0xA2], L10n.Text("左 Ctrl", "Left Ctrl")),
        InputTriggerPreset.RightControl => new([0xA3], L10n.Text("右 Ctrl", "Right Ctrl")),
        InputTriggerPreset.LeftShift => new([0xA0], L10n.Text("左 Shift", "Left Shift")),
        InputTriggerPreset.RightShift => new([0xA1], L10n.Text("右 Shift", "Right Shift")),
        InputTriggerPreset.LeftWin => new([0x5B], L10n.Text("左 Win", "Left Win")),
        InputTriggerPreset.RightWin => new([0x5C], L10n.Text("右 Win", "Right Win")),
        InputTriggerPreset.CtrlWin => new([0xA2, 0x5B], L10n.Text("Ctrl + Win (微信默认按住)", "Ctrl + Win (WeType Hold)")),
        InputTriggerPreset.CtrlWinShift => new([0xA2, 0x5B, 0xA0], L10n.Text("Ctrl + Win + Shift (微信点按)", "Ctrl + Win + Shift (WeType Tap)")),
        InputTriggerPreset.Custom when custom is not null && custom.VirtualKeys.Length > 0 => custom,
        _ => new([0xA5], L10n.Text("右 Alt", "Right Alt"))
    };
}

public static class InputTriggerSettings
{
    private const string CustomChordKey = "inputTriggerCustomChord";

    public static IReadOnlyList<InputTriggerPreset> Presets { get; } =
    [
        InputTriggerPreset.RightAlt,
        InputTriggerPreset.LeftAlt,
        InputTriggerPreset.LeftControl,
        InputTriggerPreset.RightControl,
        InputTriggerPreset.LeftShift,
        InputTriggerPreset.RightShift,
        InputTriggerPreset.LeftWin,
        InputTriggerPreset.RightWin,
        InputTriggerPreset.CtrlWin,
        InputTriggerPreset.CtrlWinShift,
        InputTriggerPreset.Custom
    ];

    public static InputTriggerPreset CurrentPreset
    {
        get
        {
            string? raw = AppStorage.Shared.GetString(AppStorage.InputTriggerKeyKey);
            return Enum.TryParse<InputTriggerPreset>(raw, ignoreCase: true, out var preset)
                ? preset
                : InputTriggerPreset.RightAlt;
        }
        set => AppStorage.Shared.SetString(AppStorage.InputTriggerKeyKey, value.ToString());
    }

    public static TriggerKeyChord? CustomChord
    {
        get
        {
            string? json = AppStorage.Shared.GetString(CustomChordKey);
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<TriggerKeyChord>(json); }
            catch { return null; }
        }
        set => AppStorage.Shared.SetString(CustomChordKey, value is null ? null : JsonSerializer.Serialize(value));
    }

    public static TriggerKeyChord ActiveChord => TriggerKeyChord.FromPreset(CurrentPreset, CustomChord);

    public static string GetPresetTitle(InputTriggerPreset preset) => preset switch
    {
        InputTriggerPreset.RightAlt => L10n.Text("右 Alt (豆包默认)", "Right Alt (Doubao Default)"),
        InputTriggerPreset.LeftAlt => L10n.Text("左 Alt", "Left Alt"),
        InputTriggerPreset.LeftControl => L10n.Text("左 Ctrl", "Left Ctrl"),
        InputTriggerPreset.RightControl => L10n.Text("右 Ctrl", "Right Ctrl"),
        InputTriggerPreset.LeftShift => L10n.Text("左 Shift", "Left Shift"),
        InputTriggerPreset.RightShift => L10n.Text("右 Shift", "Right Shift"),
        InputTriggerPreset.LeftWin => L10n.Text("左 Win", "Left Win"),
        InputTriggerPreset.RightWin => L10n.Text("右 Win", "Right Win"),
        InputTriggerPreset.CtrlWin => L10n.Text("Ctrl + Win (微信输入法按住)", "Ctrl + Win (WeType Hold)"),
        InputTriggerPreset.CtrlWinShift => L10n.Text("Ctrl + Win + Shift (微信输入法点按)", "Ctrl + Win + Shift (WeType Tap)"),
        InputTriggerPreset.Custom => CustomChord is { } c
            ? L10n.Text($"自定义 ({c.Label})", $"Custom ({c.Label})")
            : L10n.Text("录制自定义触发键…", "Record Custom Trigger…"),
        _ => preset.ToString()
    };

    public static bool IsFamilyMatch(ushort vk, TriggerKeyChord chord)
    {
        foreach (ushort k in chord.VirtualKeys)
        {
            if (NormalizeSide(vk) == NormalizeSide(k)) return true;
        }
        return false;
    }

    public static ushort NormalizeSide(ushort vk) => vk switch
    {
        0xA4 or 0xA5 or 0x12 => 0x12, // Alt
        0xA2 or 0xA3 or 0x11 => 0x11, // Ctrl
        0xA0 or 0xA1 or 0x10 => 0x10, // Shift
        0x5B or 0x5C => 0x5B,         // Win
        _ => vk
    };

    public static string FormatVirtualKeyName(ushort vk) => vk switch
    {
        0xA4 => "LAlt",
        0xA5 => "RAlt",
        0x12 => "Alt",
        0xA2 => "LCtrl",
        0xA3 => "RCtrl",
        0x11 => "Ctrl",
        0xA0 => "LShift",
        0xA1 => "RShift",
        0x10 => "Shift",
        0x5B => "LWin",
        0x5C => "RWin",
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0x08 => "Backspace",
        0x14 => "CapsLock",
        0x2C => "PrintScreen",
        0x2D => "Insert",
        0x2E => "Delete",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
        0x6A => "Num*",
        0x6B => "Num+",
        0x6D => "Num-",
        0x6E => "Num.",
        0x6F => "Num/",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => FormatMappedCharOrHex(vk)
    };

    private static string FormatMappedCharOrHex(ushort vk)
    {
        uint ch = MapVirtualKeyW(vk, 2) & 0xFFFF;
        if (ch >= 0x21 && ch <= 0x7E)
        {
            return ((char)ch).ToString();
        }
        return $"0x{vk:X2}";
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
}

public static class VoiceInputMethodProfiles
{
    private const string ExtraDoubaoProcessesKey = "vRemoter.doubaoProcessNames";
    private const string ExtraWeTypeProcessesKey = "vRemoter.wetypeProcessNames";

    private static readonly string[] DefaultDoubaoProcesses =
        ["DoubaoIme", "DoubaoVoice", "DoubaoInput", "Doubao"];

    private static readonly string[] DefaultWeTypeProcesses =
        ["WeType", "WeTypeVoice", "WeTypeService", "wxime"];

    public static string GetShortName(VoiceInputMethodKind kind) => kind switch
    {
        VoiceInputMethodKind.Doubao => L10n.Text("豆包", "Doubao"),
        VoiceInputMethodKind.WeType => L10n.Text("微信输入法", "WeType"),
        _ => kind.ToString()
    };

    public static string GetTitle(VoiceInputMethodKind kind) => kind switch
    {
        VoiceInputMethodKind.Doubao => L10n.Text("豆包输入法", "Doubao Input Method"),
        VoiceInputMethodKind.WeType => L10n.Text("微信输入法", "WeType"),
        _ => kind.ToString()
    };

    public static IReadOnlyList<string> GetProcessNames(VoiceInputMethodKind kind)
    {
        string[] defaults = kind == VoiceInputMethodKind.WeType ? DefaultWeTypeProcesses : DefaultDoubaoProcesses;
        string key = kind == VoiceInputMethodKind.WeType ? ExtraWeTypeProcessesKey : ExtraDoubaoProcessesKey;
        string? extra = AppStorage.Shared.GetString(key);
        if (string.IsNullOrWhiteSpace(extra)) return defaults;

        var custom = extra.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return defaults.Concat(custom).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

