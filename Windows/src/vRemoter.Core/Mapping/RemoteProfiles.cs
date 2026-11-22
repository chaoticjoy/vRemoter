using vRemoter.Core.Localization;

namespace vRemoter.Core.Mapping;

public enum SupportedRemoteId
{
    Chromecast,
    X6
}

public static class SupportedRemoteIdExtensions
{
    public static string StorageKey(this SupportedRemoteId id) => id switch
    {
        SupportedRemoteId.Chromecast => "chromecast",
        SupportedRemoteId.X6 => "x6",
        _ => id.ToString().ToLowerInvariant()
    };

    public static string GetTitle(this SupportedRemoteId id) => id switch
    {
        SupportedRemoteId.Chromecast => "Chromecast Voice Remote",
        SupportedRemoteId.X6 => "X6 Remote",
        _ => id.ToString()
    };

    public static string GetSignature(this SupportedRemoteId id) => id switch
    {
        SupportedRemoteId.Chromecast => "18D1 · 9450",
        SupportedRemoteId.X6 => "1D5A · C081",
        _ => string.Empty
    };
}

public sealed record RemoteButtonDefinition(
    string Id,
    string ZhTitle,
    string EnTitle,
    string Glyph,
    RemoteMappingTarget DefaultTarget,
    bool VoiceControlled = false,
    bool Remappable = true)
{
    public string Title => L10n.Text(ZhTitle, EnTitle);
}

public static class RemoteProfiles
{
    public static readonly IReadOnlyList<RemoteButtonDefinition> ChromecastButtons =
    [
        new("03", "方向上", "Up", "↑", RemoteMappingTarget.ArrowUp),
        new("04", "方向下", "Down", "↓", RemoteMappingTarget.ArrowDown),
        new("05", "方向左", "Left", "←", RemoteMappingTarget.ArrowLeft),
        new("06", "方向右", "Right", "→", RemoteMappingTarget.ArrowRight),
        new("07", "确认", "Select", "●", RemoteMappingTarget.ReturnKey),
        new("0B", "返回", "Back", "‹", RemoteMappingTarget.Escape),
        new("0A", "Home", "Home", "⌂", RemoteMappingTarget.ShowDesktop),
        new("0E", "YouTube", "YouTube", "▶", RemoteMappingTarget.Disabled),
        new("voice", "语音", "Voice", "🎙", RemoteMappingTarget.DoubaoVoice, VoiceControlled: true),
        new("08", "静音", "Mute", "🔇", RemoteMappingTarget.Mute),
        new("0F", "Netflix", "Netflix", "N", RemoteMappingTarget.Disabled),
        new("01", "电源", "Power", "⏻", RemoteMappingTarget.Disabled),
        new("11", "信源", "Input", "⧉", RemoteMappingTarget.Disabled),
        new("0C", "音量＋", "Volume Up", "🔊", RemoteMappingTarget.VolumeUp),
        new("0D", "音量－", "Volume Down", "🔉", RemoteMappingTarget.VolumeDown)
    ];

    public static readonly IReadOnlyList<RemoteButtonDefinition> X6Buttons =
    [
        new("mouseMode", "鼠标模式", "Mouse Mode", "⌖", RemoteMappingTarget.Disabled, Remappable: false),
        new("k2A", "Delete", "Delete", "⌫", RemoteMappingTarget.DeleteBackward),
        new("cE2", "静音", "Mute", "🔇", RemoteMappingTarget.Mute),
        new("c224", "返回", "Back", "‹", RemoteMappingTarget.Escape),
        new("k65", "菜单", "Menu", "☰", RemoteMappingTarget.Disabled),
        new("c196", "浏览器/搜索", "Browser / Search", "🔍", RemoteMappingTarget.Spotlight),
        new("k52", "方向上", "Up", "↑", RemoteMappingTarget.ArrowUp),
        new("k51", "方向下", "Down", "↓", RemoteMappingTarget.ArrowDown),
        new("k50", "方向左", "Left", "←", RemoteMappingTarget.ArrowLeft),
        new("k4F", "方向右", "Right", "→", RemoteMappingTarget.ArrowRight),
        new("k28", "OK", "OK", "●", RemoteMappingTarget.ReturnKey),
        new("k4B", "PG+", "PG+", "⤒", RemoteMappingTarget.PageUp),
        new("k4E", "PG−", "PG−", "⤓", RemoteMappingTarget.PageDown),
        new("voice", "语音", "Voice", "🎙", RemoteMappingTarget.DoubaoVoice, VoiceControlled: true),
        new("cE9", "音量＋", "Volume Up", "🔊", RemoteMappingTarget.VolumeUp),
        new("cEA", "音量－", "Volume Down", "🔉", RemoteMappingTarget.VolumeDown),
        new("s01", "电源", "Power", "⏻", RemoteMappingTarget.Disabled)
    ];

    public static IReadOnlyList<RemoteButtonDefinition> GetButtons(SupportedRemoteId remote) => remote switch
    {
        SupportedRemoteId.Chromecast => ChromecastButtons,
        SupportedRemoteId.X6 => X6Buttons,
        _ => ChromecastButtons
    };
}

