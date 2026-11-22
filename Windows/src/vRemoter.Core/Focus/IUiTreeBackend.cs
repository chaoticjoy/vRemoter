namespace vRemoter.Core.Focus;

public readonly record struct UiRect(double X, double Y, double Width, double Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public UiRect Intersect(UiRect other)
    {
        double left = Math.Max(X, other.X);
        double top = Math.Max(Y, other.Y);
        double right = Math.Min(X + Width, other.X + other.Width);
        double bottom = Math.Min(Y + Height, other.Y + other.Height);
        if (right <= left || bottom <= top)
        {
            return default;
        }
        return new UiRect(left, top, right - left, bottom - top);
    }
}

public sealed record FocusAppInfo(
    string AppIdentifier,
    int ProcessId,
    string Name,
    string? AppPath,
    string? BuildHash);

public static class InputFocusRoles
{
    public static readonly HashSet<string> EditableRoles =
    [
        "AXTextArea",
        "AXTextField",
        "AXSearchField",
        "AXComboBox",
        "Edit",
        "Document"
    ];

    public static readonly HashSet<string> MultiLineTextRoles =
    [
        "AXTextArea",
        "Document"
    ];

    public static readonly HashSet<string> SearchRoles =
    [
        "AXSearchField"
    ];

    public static readonly HashSet<string> BlockedRoles =
    [
        "AXSheet",
        "AXDialog",
        "AXMenu",
        "AXMenuItem",
        "AXMenuBar",
        "Menu",
        "MenuItem",
        "MenuBar"
    ];

    public static readonly HashSet<string> BlockedSubroles =
    [
        "AXSecureTextField",
        "AXDialog",
        "AXSystemDialog",
        "Password"
    ];

    public static readonly HashSet<string> SkipDescendRoles =
    [
        "AXStaticText",
        "AXImage",
        "AXMenuBar",
        "AXScrollBar",
        "AXSplitter",
        "AXValueIndicator",
        "Text",
        "Image",
        "MenuBar",
        "ScrollBar"
    ];
}

/// <summary>
/// Platform-agnostic UI tree abstraction (implemented by WinUiaFocusBackend on Windows).
/// </summary>
public interface IUiTreeBackend<TNode> where TNode : class
{
    bool AreEqual(TNode? left, TNode? right);
    string? GetRole(TNode node);
    string? GetSubrole(TNode node);
    string? GetIdentifier(TNode node);
    string? GetDomIdentifier(TNode node);
    string? GetPlaceholder(TNode node);
    string? GetDescriptionText(TNode node);
    bool IsEnabled(TNode node);
    bool IsHidden(TNode node);
    bool IsFocused(TNode node);
    bool IsFocusedSettable(TNode node);
    bool IsModal(TNode window);
    UiRect? GetRect(TNode node);
    TNode? GetParent(TNode node);
    IReadOnlyList<TNode>? GetChildren(TNode node);
    bool SetFocused(TNode node);
}

