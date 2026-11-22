using System.Security.Cryptography;
using System.Text;
using vRemoter.Core.Localization;

namespace vRemoter.Core.Focus;

public sealed record InputFocusLocator(int Version, string Build, string Signature)
{
    public bool IsValidFormat =>
        (Version == 1 || Version == 2)
        && IsSha256Hex(Build)
        && IsSha256Hex(Signature);

    public static string Sha256Hex(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string? BuildDigest(string appIdentifier, string? version, string? shortVersion)
    {
        string v = version ?? string.Empty;
        string sv = shortVersion ?? string.Empty;
        if (string.IsNullOrEmpty(appIdentifier) || (string.IsNullOrEmpty(v) && string.IsNullOrEmpty(sv)))
        {
            return null;
        }
        return Sha256Hex($"{appIdentifier}|{v}|{sv}");
    }

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64) return false;
        foreach (char c in value)
        {
            bool isDigit = c is >= '0' and <= '9';
            bool isLowerHex = c is >= 'a' and <= 'f';
            if (!isDigit && !isLowerHex) return false;
        }
        return true;
    }
}

public enum InputFocusContextState
{
    Already,
    Editing,
    Blocked,
    Clear,
    Unavailable
}

public enum InputFocusResultStatus
{
    Waiting,
    Loading,
    Restored,
    Already,
    Editing,
    Blocked,
    Changed,
    ManualNeeded,
    NeedsBinding,
    Rebind,
    Unavailable,
    Confirming,
    Unconfirmed
}

public static class InputFocusResultStatusExtensions
{
    public static bool NeedsUserAction(this InputFocusResultStatus status) => status is
        InputFocusResultStatus.ManualNeeded or
        InputFocusResultStatus.NeedsBinding or
        InputFocusResultStatus.Rebind or
        InputFocusResultStatus.Unavailable;

    public static string ShortDescription(this InputFocusResultStatus status) => status switch
    {
        InputFocusResultStatus.Waiting => L10n.Text("已就绪：切到该应用时自动聚焦输入框", "Ready: focuses input when switching to this app"),
        InputFocusResultStatus.Loading => L10n.Text("正在等待应用窗口就绪…", "Waiting for app window…"),
        InputFocusResultStatus.Restored => L10n.Text("已将输入焦点切入输入框", "Focused the input box"),
        InputFocusResultStatus.Already => L10n.Text("输入框已有焦点", "Input box is already focused"),
        InputFocusResultStatus.Editing => L10n.Text("正在其他编辑框操作，保持现状", "Editing elsewhere; kept current focus"),
        InputFocusResultStatus.Blocked => L10n.Text("当前有菜单或弹窗，未抢占焦点", "Menu or dialog is open; skipped"),
        InputFocusResultStatus.Changed => L10n.Text("检测到已切换或操作，已让位", "User acted or switched away"),
        InputFocusResultStatus.ManualNeeded => L10n.Text("未能自动唯一识别输入框，请点「指定输入框」", "Could not auto-identify input; bind it manually"),
        InputFocusResultStatus.NeedsBinding => L10n.Text("尚未指定输入框，请点「指定输入框」", "No input bound yet"),
        InputFocusResultStatus.Rebind => L10n.Text("输入框结构已变化，请「重新指定」", "Input changed (app updated?); rebind required"),
        InputFocusResultStatus.Unavailable => L10n.Text("无法读取窗口，请检查权限或以管理员模式运行", "Unavailable; check UIA access or elevated mode"),
        InputFocusResultStatus.Confirming => L10n.Text("已请求聚焦，等待确认", "Focus requested, confirming"),
        InputFocusResultStatus.Unconfirmed => L10n.Text("已请求聚焦，暂未收到确认", "Focus requested, unconfirmed"),
        _ => status.ToString()
    };
}

public enum InputFocusMode
{
    Auto,
    Manual
}

public sealed record InputFocusAppEntry
{
    public string AppIdentifier { get; init; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? AppPath { get; set; }
    public bool Enabled { get; set; } = true;
    public InputFocusMode Mode { get; set; } = InputFocusMode.Auto;
    public InputFocusLocator? Locator { get; set; }
}

public sealed record RemoteAppLaunchConfig(
    string AppIdentifier,
    string AppPath,
    string Name,
    bool AutoFocusInput);

public enum FocusCaptureStatus
{
    Waiting,
    WindowUnavailable,
    FocusUnavailable,
    NotEditor,
    Captured
}

public static class FocusCaptureStatusExtensions
{
    public static string GetMessage(this FocusCaptureStatus status) => status switch
    {
        FocusCaptureStatus.Waiting => L10n.Text(
            "请切到目标应用并点击输入框，然后返回点击「确认指定」。",
            "Switch to the target app, click its input box, then return and click Confirm."),
        FocusCaptureStatus.WindowUnavailable => L10n.Text(
            "暂时读不到目标应用窗口，请保持窗口打开。",
            "Target window is not readable yet; keep it open."),
        FocusCaptureStatus.FocusUnavailable => L10n.Text(
            "请点击目标应用的输入框并停留 1 秒。",
            "Click inside the target input box and wait a moment."),
        FocusCaptureStatus.NotEditor => L10n.Text(
            "当前选中的控件不是文本输入框，请点击真正的输入区域。",
            "The focused control is not a text input; click inside the input box."),
        FocusCaptureStatus.Captured => L10n.Text(
            "已捕获输入框！确认无误后点击「确认指定」。",
            "Input box captured! Click Confirm Binding to save."),
        _ => string.Empty
    };
}

public sealed record InputFocusBindingState(
    string? SelectingAppId = null,
    string? SelectingAppName = null,
    bool CandidateReady = false,
    FocusCaptureStatus CaptureStatus = FocusCaptureStatus.Waiting,
    int SecondsLeft = 0)
{
    public bool IsSelecting => !string.IsNullOrEmpty(SelectingAppId);
}

