using vRemoter.Core.Localization;

namespace vRemoter.Core.Mapping;

public enum RemoteMappingTarget
{
    Disabled,
    DoubaoVoice,
    ArrowUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    ReturnKey,
    Escape,
    DeleteBackward,
    Tab,
    Space,
    Home,
    End,
    PageUp,
    PageDown,
    VolumeUp,
    VolumeDown,
    Mute,
    PlayPause,
    ShowDesktop,
    Spotlight,
    CommandC,
    CommandV,
    CommandZ,
    LockScreen,
    Sleep,
    AppSwitcher,
    FocusCurrentInput,
    OpenApp,
    Custom
}

public static class RemoteMappingTargetExtensions
{
    public static string GetTitle(this RemoteMappingTarget target, string voiceInputShortName = "豆包") => target switch
    {
        RemoteMappingTarget.Disabled => L10n.Text("禁用", "Disabled"),
        RemoteMappingTarget.DoubaoVoice => L10n.Text($"{voiceInputShortName}语音输入", $"{voiceInputShortName} Voice Input"),
        RemoteMappingTarget.ArrowUp => L10n.Text("方向上", "Up Arrow"),
        RemoteMappingTarget.ArrowDown => L10n.Text("方向下", "Down Arrow"),
        RemoteMappingTarget.ArrowLeft => L10n.Text("方向左", "Left Arrow"),
        RemoteMappingTarget.ArrowRight => L10n.Text("方向右", "Right Arrow"),
        RemoteMappingTarget.ReturnKey => "Enter",
        RemoteMappingTarget.Escape => "Escape",
        RemoteMappingTarget.DeleteBackward => "Backspace",
        RemoteMappingTarget.Tab => "Tab",
        RemoteMappingTarget.Space => L10n.Text("空格", "Space"),
        RemoteMappingTarget.Home => "Home",
        RemoteMappingTarget.End => "End",
        RemoteMappingTarget.PageUp => "Page Up",
        RemoteMappingTarget.PageDown => "Page Down",
        RemoteMappingTarget.VolumeUp => L10n.Text("系统音量＋", "System Volume Up"),
        RemoteMappingTarget.VolumeDown => L10n.Text("系统音量－", "System Volume Down"),
        RemoteMappingTarget.Mute => L10n.Text("系统静音", "System Mute"),
        RemoteMappingTarget.PlayPause => L10n.Text("播放/暂停", "Play / Pause"),
        RemoteMappingTarget.ShowDesktop => L10n.Text("显示桌面 (Win+D)", "Show Desktop (Win+D)"),
        RemoteMappingTarget.Spotlight => L10n.Text("系统搜索 (Win+S)", "Windows Search (Win+S)"),
        RemoteMappingTarget.CommandC => L10n.Text("复制 (Ctrl+C)", "Copy (Ctrl+C)"),
        RemoteMappingTarget.CommandV => L10n.Text("粘贴 (Ctrl+V)", "Paste (Ctrl+V)"),
        RemoteMappingTarget.CommandZ => L10n.Text("撤销 (Ctrl+Z)", "Undo (Ctrl+Z)"),
        RemoteMappingTarget.LockScreen => L10n.Text("锁屏 (Win+L)", "Lock Screen"),
        RemoteMappingTarget.Sleep => L10n.Text("睡眠", "Sleep"),
        RemoteMappingTarget.AppSwitcher => L10n.Text("切换应用", "App Switcher"),
        RemoteMappingTarget.FocusCurrentInput => L10n.Text("聚焦当前输入框", "Focus Input Box"),
        RemoteMappingTarget.OpenApp => L10n.Text("打开指定应用…", "Open App…"),
        RemoteMappingTarget.Custom => L10n.Text("录制任意按键…", "Record a key…"),
        _ => target.ToString()
    };

    public static string ToStorageValue(this RemoteMappingTarget target) => target switch
    {
        RemoteMappingTarget.ReturnKey => "returnKey",
        RemoteMappingTarget.DeleteBackward => "deleteBackward",
        RemoteMappingTarget.DoubaoVoice => "doubaoVoice",
        RemoteMappingTarget.ShowDesktop => "showDesktop",
        RemoteMappingTarget.CommandC => "commandC",
        RemoteMappingTarget.CommandV => "commandV",
        RemoteMappingTarget.CommandZ => "commandZ",
        RemoteMappingTarget.LockScreen => "lockScreen",
        RemoteMappingTarget.AppSwitcher => "appSwitcher",
        RemoteMappingTarget.FocusCurrentInput => "focusCurrentInput",
        RemoteMappingTarget.OpenApp => "openApp",
        _ => char.ToLowerInvariant(target.ToString()[0]) + target.ToString()[1..]
    };

    public static bool TryParseStorageValue(string? raw, out RemoteMappingTarget target)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            target = RemoteMappingTarget.Disabled;
            return false;
        }
        return Enum.TryParse(raw, ignoreCase: true, out target);
    }
}

public sealed record RemoteCustomShortcut(
    ushort VirtualKey,
    bool Ctrl,
    bool Shift,
    bool Alt,
    bool Win,
    string Label);

