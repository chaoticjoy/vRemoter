namespace vRemoter.Core.Focus;

public static class InputFocusDiscovery
{
    private const int MaxDepth = 64;
    private const int MaxDiscoverNodes = 5000;
    private static readonly TimeSpan DiscoverTimeout = TimeSpan.FromSeconds(0.85);

    public static readonly HashSet<string> ChatIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompt-textarea",
        "chat-input",
        "chat-input-textarea",
        "composer-input",
        "message-input",
        "chat-composer",
        "ai-input"
    };

    public static readonly string[] ChatHintPhrases =
    [
        "message input",
        "ask anything",
        "ask codex",
        "ask a question",
        "how can i help",
        "message chatgpt",
        "message claude",
        "reply to claude",
        "message deepseek",
        "type a message",
        "输入消息",
        "请输入消息",
        "发消息",
        "发送消息",
        "输入问题",
        "随便问",
        "和 kimi 聊聊",
        "向 deepseek 发送消息",
        "向 kimi 提问",
        "向豆包提问"
    ];

    public static readonly string[] ExcludedWords =
    [
        "search",
        "rename",
        "password",
        "filter",
        "find in",
        "搜索",
        "重命名",
        "密码",
        "查找"
    ];

    public static bool IsChatMetadataMatch(
        string? identifier,
        string? domIdentifier,
        string? placeholder,
        string? descriptionText)
    {
        string id = (identifier ?? string.Empty).Trim().ToLowerInvariant();
        string dom = (domIdentifier ?? string.Empty).Trim().ToLowerInvariant();
        string ph = (placeholder ?? string.Empty).Trim().ToLowerInvariant();
        string desc = (descriptionText ?? string.Empty).Trim().ToLowerInvariant();

        string combined = $"{id} {dom} {ph} {desc}";
        if (ExcludedWords.Any(w => combined.Contains(w, StringComparison.Ordinal)))
        {
            return false;
        }
        if (ChatIdentifiers.Contains(id) || ChatIdentifiers.Contains(dom))
        {
            return true;
        }
        return ChatHintPhrases.Any(phrase =>
            ph == phrase || desc == phrase ||
            ph.Contains(phrase, StringComparison.Ordinal) ||
            desc.Contains(phrase, StringComparison.Ordinal));
    }

    public static bool HasExcludedMetadata<TNode>(IUiTreeBackend<TNode> backend, TNode node)
        where TNode : class
    {
        string combined = string.Join(" ",
            backend.GetIdentifier(node) ?? string.Empty,
            backend.GetDomIdentifier(node) ?? string.Empty,
            backend.GetPlaceholder(node) ?? string.Empty,
            backend.GetDescriptionText(node) ?? string.Empty).ToLowerInvariant();
        return ExcludedWords.Any(w => combined.Contains(w, StringComparison.Ordinal));
    }

    public static bool IsProtectedTransientInput<TNode>(IUiTreeBackend<TNode> backend, TNode node)
        where TNode : class
    {
        string role = backend.GetRole(node) ?? string.Empty;
        string subrole = backend.GetSubrole(node) ?? string.Empty;
        if (InputFocusRoles.SearchRoles.Contains(role) || InputFocusRoles.BlockedSubroles.Contains(subrole))
        {
            return true;
        }
        return HasExcludedMetadata(backend, node);
    }

    public static TNode? DiscoverTarget<TNode>(
        IUiTreeBackend<TNode> backend,
        TNode window,
        bool allowSingleFallback,
        Func<bool>? cancelled = null)
        where TNode : class
    {
        if (backend.IsModal(window)) return null;

        var pending = new Stack<(TNode Node, int Depth)>();
        pending.Push((window, 0));
        var highConfidence = new List<TNode>();
        var textAreas = new List<TNode>();
        var otherInputs = new List<TNode>();
        var deadline = DateTimeOffset.UtcNow + DiscoverTimeout;
        int visited = 0;

        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (visited >= MaxDiscoverNodes || DateTimeOffset.UtcNow >= deadline || cancelled?.Invoke() == true)
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
                    InputFocusLocatorBuilder.IsVisible(backend, node, window))
                {
                    if (IsChatMetadataMatch(
                        backend.GetIdentifier(node),
                        backend.GetDomIdentifier(node),
                        backend.GetPlaceholder(node),
                        backend.GetDescriptionText(node)))
                    {
                        highConfidence.Add(node);
                    }
                    else if (!HasExcludedMetadata(backend, node))
                    {
                        if (InputFocusRoles.MultiLineTextRoles.Contains(role))
                        {
                            textAreas.Add(node);
                        }
                        else
                        {
                            otherInputs.Add(node);
                        }
                    }
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
                if (depth >= MaxDepth || visited + pending.Count + children.Count > MaxDiscoverNodes)
                {
                    return null;
                }
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    pending.Push((children[i], depth + 1));
                }
            }
        }

        if (cancelled?.Invoke() == true || DateTimeOffset.UtcNow >= deadline)
        {
            return null;
        }
        if (highConfidence.Count == 1) return highConfidence[0];
        if (highConfidence.Count > 1) return null;

        if (allowSingleFallback)
        {
            if (textAreas.Count == 1) return textAreas[0];
            if (textAreas.Count == 0 && otherInputs.Count == 1) return otherInputs[0];
        }
        return null;
    }

    public static InputFocusContextState FocusContext<TNode>(
        IUiTreeBackend<TNode> backend,
        TNode? focused,
        TNode window,
        TNode? target)
        where TNode : class
    {
        var current = focused;
        for (int i = 0; i < MaxDepth; i++)
        {
            if (current is null) return InputFocusContextState.Unavailable;
            string? role = backend.GetRole(current);
            if (string.IsNullOrEmpty(role)) return InputFocusContextState.Unavailable;

            string subrole = backend.GetSubrole(current) ?? string.Empty;
            if (InputFocusRoles.BlockedRoles.Contains(role) || InputFocusRoles.BlockedSubroles.Contains(subrole))
            {
                return InputFocusContextState.Blocked;
            }
            if (target is not null && backend.AreEqual(current, target))
            {
                return InputFocusContextState.Already;
            }
            if (InputFocusRoles.EditableRoles.Contains(role))
            {
                return InputFocusContextState.Editing;
            }
            if (backend.AreEqual(current, window))
            {
                return InputFocusContextState.Clear;
            }
            current = backend.GetParent(current);
        }
        return InputFocusContextState.Unavailable;
    }

    public static InputFocusResultStatus Restore<TNode>(
        IUiTreeBackend<TNode> backend,
        TNode window,
        TNode target,
        TNode? focusedElement,
        bool allowOverrideOtherEditor,
        bool allowOverrideNonSearchEditor,
        Func<bool> isSameWindowAndFrontmost,
        Func<bool> didUserInteract,
        Func<bool> cancelled)
        where TNode : class
    {
        if (cancelled() || !isSameWindowAndFrontmost() || didUserInteract())
        {
            return InputFocusResultStatus.Changed;
        }
        if (backend.IsModal(window))
        {
            return InputFocusResultStatus.Blocked;
        }

        string? role = backend.GetRole(target);
        if (string.IsNullOrEmpty(role) ||
            !InputFocusRoles.EditableRoles.Contains(role) ||
            !backend.IsEnabled(target) ||
            !backend.IsFocusedSettable(target) ||
            !InputFocusLocatorBuilder.IsAttached(backend, target, window))
        {
            return InputFocusResultStatus.Rebind;
        }

        if (focusedElement is not null)
        {
            var state = FocusContext(backend, focusedElement, window, target);
            switch (state)
            {
                case InputFocusContextState.Already:
                    return InputFocusResultStatus.Already;
                case InputFocusContextState.Blocked:
                    return InputFocusResultStatus.Blocked;
                case InputFocusContextState.Editing:
                    bool shouldProtect = IsProtectedTransientInput(backend, focusedElement);
                    if (!allowOverrideOtherEditor && (shouldProtect || !allowOverrideNonSearchEditor))
                    {
                        return InputFocusResultStatus.Editing;
                    }
                    break;
                case InputFocusContextState.Unavailable:
                    if (!allowOverrideOtherEditor && !allowOverrideNonSearchEditor)
                    {
                        return InputFocusResultStatus.Unavailable;
                    }
                    break;
            }
        }

        if (cancelled() || !isSameWindowAndFrontmost() || didUserInteract())
        {
            return InputFocusResultStatus.Changed;
        }

        _ = backend.SetFocused(target);
        return backend.IsFocused(target)
            ? InputFocusResultStatus.Restored
            : InputFocusResultStatus.Confirming;
    }
}

