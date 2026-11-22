namespace vRemoter.Core.Focus;

public static class InputFocusLocatorBuilder
{
    private const int MaxDepth = 64;
    private const int MaxResolveNodes = 5000;
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(1.5);

    public static bool IsAttached<TNode>(IUiTreeBackend<TNode> backend, TNode node, TNode window)
        where TNode : class
    {
        TNode? current = node;
        for (int i = 0; i < MaxDepth; i++)
        {
            if (current is null) return false;
            if (backend.AreEqual(current, window)) return true;
            current = backend.GetParent(current);
        }
        return false;
    }

    public static bool IsVisible<TNode>(IUiTreeBackend<TNode> backend, TNode node, TNode window)
        where TNode : class
    {
        if (backend.IsHidden(node)) return false;
        if (backend.GetRect(node) is not { } elementRect ||
            backend.GetRect(window) is not { } windowRect)
        {
            return false;
        }
        if (elementRect.Width < 16 || elementRect.Height < 12)
        {
            return false;
        }
        var intersection = elementRect.Intersect(windowRect);
        return !intersection.IsEmpty && intersection.Width >= 8 && intersection.Height >= 8;
    }

    public static string? Signature<TNode>(
        IUiTreeBackend<TNode> backend,
        TNode target,
        TNode window,
        bool includeSurroundings = false,
        Func<bool>? cancelled = null)
        where TNode : class
    {
        string? Metadata(TNode node)
        {
            string? role = backend.GetRole(node);
            if (string.IsNullOrEmpty(role) || InputFocusRoles.BlockedRoles.Contains(role))
            {
                return null;
            }
            string subrole = backend.GetSubrole(node) ?? string.Empty;
            if (InputFocusRoles.BlockedSubroles.Contains(subrole))
            {
                return null;
            }
            string ident = backend.GetIdentifier(node) ?? string.Empty;
            string domId = backend.GetDomIdentifier(node) ?? string.Empty;
            string desc = InputFocusRoles.EditableRoles.Contains(role)
                ? (backend.GetDescriptionText(node) ?? string.Empty)
                : string.Empty;
            return $"{role}|{subrole}|{ident}|{domId}|{desc}";
        }

        var chain = new List<string>();
        var surroundings = new List<List<string>>();
        TNode? current = target;

        for (int depth = 0; depth < MaxDepth; depth++)
        {
            if (cancelled?.Invoke() == true) return null;
            if (current is null || Metadata(current) is not { } meta) return null;

            chain.Add(meta);
            if (backend.AreEqual(current, window))
            {
                if (includeSurroundings)
                {
                    string surr = string.Join("\n", surroundings.Select(s => string.Join(",", s)));
                    return InputFocusLocator.Sha256Hex($"{string.Join("\n", chain)}\n---\n{surr}");
                }
                return InputFocusLocator.Sha256Hex(string.Join("\n", chain));
            }

            if (includeSurroundings && depth is >= 1 and <= 3)
            {
                var children = backend.GetChildren(current);
                if (children is not null && children.Count <= 64)
                {
                    var childMeta = new List<string>(children.Count);
                    foreach (var child in children)
                    {
                        if (Metadata(child) is not { } item) return null;
                        childMeta.Add(item);
                    }
                    surroundings.Add(childMeta);
                }
            }
            current = backend.GetParent(current);
        }
        return null;
    }

    public static InputFocusLocator? Describe<TNode>(
        IUiTreeBackend<TNode> backend,
        TNode window,
        TNode target,
        string? build)
        where TNode : class
    {
        if (string.IsNullOrEmpty(build)) return null;
        string? sig = Signature(backend, target, window, includeSurroundings: false);
        if (sig is null) return null;

        var locator = new InputFocusLocator(2, build, sig);
        var resolved = Resolve(backend, window, locator, build);
        return resolved is not null && backend.AreEqual(resolved, target) ? locator : null;
    }

    public static TNode? Resolve<TNode>(
        IUiTreeBackend<TNode> backend,
        TNode window,
        InputFocusLocator locator,
        string? build,
        Func<bool>? cancelled = null)
        where TNode : class
    {
        if (!locator.IsValidFormat ||
            string.IsNullOrEmpty(build) ||
            !string.Equals(locator.Build, build, StringComparison.Ordinal) ||
            backend.IsModal(window))
        {
            return null;
        }

        var pending = new Stack<(TNode Node, int Depth)>();
        pending.Push((window, 0));
        var matches = new List<TNode>();
        var deadline = DateTimeOffset.UtcNow + ResolveTimeout;
        int visited = 0;

        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (visited >= MaxResolveNodes || DateTimeOffset.UtcNow >= deadline || cancelled?.Invoke() == true)
            {
                return null;
            }
            visited++;

            string? role = backend.GetRole(node);
            if (string.IsNullOrEmpty(role) || InputFocusRoles.BlockedRoles.Contains(role))
            {
                return null;
            }

            if (InputFocusRoles.EditableRoles.Contains(role))
            {
                string subrole = backend.GetSubrole(node) ?? string.Empty;
                if (!InputFocusRoles.BlockedSubroles.Contains(subrole) &&
                    backend.IsEnabled(node) &&
                    backend.IsFocusedSettable(node) &&
                    IsVisible(backend, node, window) &&
                    Signature(backend, node, window, includeSurroundings: locator.Version == 1, cancelled) == locator.Signature)
                {
                    matches.Add(node);
                    if (matches.Count > 1) return null;
                }
                continue;
            }

            if (InputFocusRoles.SkipDescendRoles.Contains(role))
            {
                continue;
            }

            var children = backend.GetChildren(node);
            if (children is null) return null;

            if (children.Count > 0)
            {
                if (depth >= MaxDepth || visited + pending.Count + children.Count > MaxResolveNodes)
                {
                    return null;
                }
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    pending.Push((children[i], depth + 1));
                }
            }
        }

        if (cancelled?.Invoke() == true || DateTimeOffset.UtcNow >= deadline || matches.Count != 1)
        {
            return null;
        }
        return matches[0];
    }
}

