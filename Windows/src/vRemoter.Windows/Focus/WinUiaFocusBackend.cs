using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using vRemoter.Core.Focus;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Focus;

public sealed class UiaNode
{
    public AutomationElement Element { get; }
    public int[] RuntimeId { get; }

    public UiaNode(AutomationElement element)
    {
        Element = element;
        try { RuntimeId = element.GetRuntimeId() ?? []; }
        catch { RuntimeId = []; }
    }
}

/// <summary>
/// Windows UI Automation backend implementing <see cref="IFocusSessionBackend{UiaNode}"/>
/// using CacheRequest batch property prefetching and Electron/Chromium WM_GETOBJECT wakeup.
/// </summary>
public sealed class WinUiaFocusBackend : IFocusSessionBackend<UiaNode>
{
    private const int WmGetObject = 0x003D;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly IntPtr ObjidClient = new(-4);

    private static readonly CacheRequest SubtreeCacheRequest = CreateCacheRequest();

    private static CacheRequest CreateCacheRequest()
    {
        var req = new CacheRequest
        {
            AutomationElementMode = AutomationElementMode.Full,
            TreeScope = TreeScope.Element | TreeScope.Children
        };
        req.Add(AutomationElement.ControlTypeProperty);
        req.Add(AutomationElement.LocalizedControlTypeProperty);
        req.Add(AutomationElement.AutomationIdProperty);
        req.Add(AutomationElement.ClassNameProperty);
        req.Add(AutomationElement.NameProperty);
        req.Add(AutomationElement.HelpTextProperty);
        req.Add(AutomationElement.IsEnabledProperty);
        req.Add(AutomationElement.IsOffscreenProperty);
        req.Add(AutomationElement.IsKeyboardFocusableProperty);
        req.Add(AutomationElement.HasKeyboardFocusProperty);
        req.Add(AutomationElement.IsPasswordProperty);
        req.Add(AutomationElement.BoundingRectangleProperty);
        req.Add(WindowPattern.Pattern);
        req.Add(WindowPattern.IsModalProperty);
        return req;
    }

    public bool AreEqual(UiaNode? left, UiaNode? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (left.RuntimeId.Length > 0 && right.RuntimeId.Length > 0)
        {
            return left.RuntimeId.SequenceEqual(right.RuntimeId);
        }
        try { return Automation.Compare(left.Element, right.Element); }
        catch { return false; }
    }

    public FocusAppInfo? FrontmostApp()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;

        return GetAppInfoByPid((int)pid);
    }

    public void PrepareEnhancedUi(FocusAppInfo app)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        _ = SendMessageTimeoutW(hwnd, WmGetObject, IntPtr.Zero, ObjidClient, SmtoAbortIfHung, 120, out _);
    }

    public UiaNode? FocusedWindow(FocusAppInfo app)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if ((int)pid != app.ProcessId) return null;

        try
        {
            using (SubtreeCacheRequest.Activate())
            {
                var el = AutomationElement.FromHandle(hwnd);
                return el is not null ? new UiaNode(el) : null;
            }
        }
        catch
        {
            return null;
        }
    }

    public UiaNode? FocusedElement(FocusAppInfo app)
    {
        try
        {
            using (SubtreeCacheRequest.Activate())
            {
                var focused = AutomationElement.FocusedElement;
                if (focused is null) return null;
                if (focused.Current.ProcessId != app.ProcessId) return null;
                return new UiaNode(focused);
            }
        }
        catch
        {
            return null;
        }
    }

    public string? GetRole(UiaNode node)
    {
        try
        {
            var ct = ReadControlType(node.Element);
            if (ct == ControlType.Edit)
            {
                var rect = GetRect(node);
                return rect is { Height: >= 40 } ? "AXTextArea" : "Edit";
            }
            if (ct == ControlType.Document) return "Document";
            if (ct == ControlType.ComboBox) return "AXComboBox";
            if (ct == ControlType.Menu) return "Menu";
            if (ct == ControlType.MenuItem) return "MenuItem";
            if (ct == ControlType.MenuBar) return "MenuBar";
            if (ct == ControlType.Text) return "Text";
            if (ct == ControlType.Image) return "Image";
            if (ct == ControlType.ScrollBar) return "ScrollBar";
            if (ct == ControlType.Window) return "AXWindow";
            return ct?.ProgrammaticName ?? "Group";
        }
        catch
        {
            return null;
        }
    }

    public string? GetSubrole(UiaNode node)
    {
        try
        {
            bool isPassword = (bool)(ReadProp(node.Element, AutomationElement.IsPasswordProperty) ?? false);
            return isPassword ? "Password" : null;
        }
        catch
        {
            return null;
        }
    }

    public string? GetIdentifier(UiaNode node) =>
        ReadProp(node.Element, AutomationElement.AutomationIdProperty) as string;

    public string? GetDomIdentifier(UiaNode node) =>
        ReadProp(node.Element, AutomationElement.ClassNameProperty) as string;

    public string? GetPlaceholder(UiaNode node) =>
        ReadProp(node.Element, AutomationElement.HelpTextProperty) as string;

    public string? GetDescriptionText(UiaNode node) =>
        ReadProp(node.Element, AutomationElement.NameProperty) as string;

    public bool IsEnabled(UiaNode node)
    {
        try { return (bool)(ReadProp(node.Element, AutomationElement.IsEnabledProperty) ?? false); }
        catch { return false; }
    }

    public bool IsHidden(UiaNode node)
    {
        try { return (bool)(ReadProp(node.Element, AutomationElement.IsOffscreenProperty) ?? false); }
        catch { return true; }
    }

    public bool IsFocused(UiaNode node)
    {
        try { return (bool)(ReadProp(node.Element, AutomationElement.HasKeyboardFocusProperty) ?? false); }
        catch { return false; }
    }

    public bool IsFocusedSettable(UiaNode node)
    {
        try { return (bool)(ReadProp(node.Element, AutomationElement.IsKeyboardFocusableProperty) ?? false); }
        catch { return false; }
    }

    public bool IsModal(UiaNode window)
    {
        try
        {
            if (window.Element.TryGetCachedPattern(WindowPattern.Pattern, out object? cachedObj) &&
                cachedObj is WindowPattern cachedPattern)
            {
                return cachedPattern.Cached.IsModal;
            }
            if (window.Element.TryGetCurrentPattern(WindowPattern.Pattern, out object? currObj) &&
                currObj is WindowPattern currPattern)
            {
                return currPattern.Current.IsModal;
            }
        }
        catch { }
        return false;
    }

    public UiRect? GetRect(UiaNode node)
    {
        try
        {
            if (ReadProp(node.Element, AutomationElement.BoundingRectangleProperty) is Rect r && !r.IsEmpty)
            {
                return new UiRect(r.X, r.Y, r.Width, r.Height);
            }
        }
        catch { }
        return null;
    }

    public UiaNode? GetParent(UiaNode node)
    {
        try
        {
            var parent = TreeWalker.ControlViewWalker.GetParent(node.Element);
            return parent is not null ? new UiaNode(parent) : null;
        }
        catch { return null; }
    }

    public IReadOnlyList<UiaNode>? GetChildren(UiaNode node)
    {
        try
        {
            using (SubtreeCacheRequest.Activate())
            {
                var children = node.Element.FindAll(TreeScope.Children, Condition.TrueCondition);
                var list = new List<UiaNode>(children.Count);
                foreach (AutomationElement child in children)
                {
                    list.Add(new UiaNode(child));
                }
                return list;
            }
        }
        catch
        {
            return null;
        }
    }

    public bool SetFocused(UiaNode node)
    {
        try
        {
            node.Element.SetFocus();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static List<FocusAppInfo> GetRunningAppsForSelection()
    {
        var seen = new Dictionary<string, FocusAppInfo>(StringComparer.OrdinalIgnoreCase);
        int selfPid = Environment.ProcessId;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id == selfPid || proc.MainWindowHandle == IntPtr.Zero) continue;
                string? exePath = proc.MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) continue;

                string appId = Path.GetFileName(exePath);
                if (seen.ContainsKey(appId)) continue;

                var info = CreateAppInfo(exePath, proc.Id, proc.MainWindowTitle);
                if (info is not null) seen[appId] = info;
            }
            catch { }
            finally { proc.Dispose(); }
        }

        return seen.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static FocusAppInfo? GetAppInfoFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return CreateAppInfo(path, 0, null);
    }

    private static FocusAppInfo? GetAppInfoByPid(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            string? exePath = proc.MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return null;
            return CreateAppInfo(exePath, pid, proc.MainWindowTitle);
        }
        catch
        {
            return null;
        }
    }

    private static FocusAppInfo? CreateAppInfo(string exePath, int pid, string? fallbackTitle)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            string appId = Path.GetFileName(exePath);
            string name = !string.IsNullOrWhiteSpace(fvi.ProductName)
                ? fvi.ProductName.Trim()
                : Path.GetFileNameWithoutExtension(exePath);
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(fallbackTitle))
            {
                name = fallbackTitle;
            }
            string? build = InputFocusLocator.BuildDigest(appId, fvi.FileVersion, fvi.ProductVersion);
            return new FocusAppInfo(appId, pid, name, exePath, build);
        }
        catch
        {
            return null;
        }
    }

    private static ControlType? ReadControlType(AutomationElement element) =>
        ReadProp(element, AutomationElement.ControlTypeProperty) as ControlType;

    private static object? ReadProp(AutomationElement element, AutomationProperty property)
    {
        try
        {
            object? val = element.GetCachedPropertyValue(property, ignoreDefaultValue: true);
            if (val != AutomationElement.NotSupported && val is not null)
            {
                return val;
            }
        }
        catch { }

        try
        {
            object? val = element.GetCurrentPropertyValue(property, ignoreDefaultValue: true);
            return val == AutomationElement.NotSupported ? null : val;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}

