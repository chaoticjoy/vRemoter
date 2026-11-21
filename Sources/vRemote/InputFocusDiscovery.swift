import Foundation

enum InputFocusContextState: Equatable {
    case already
    case editing
    case blocked
    case clear
    case unavailable
}

enum InputFocusResultStatus: String, Codable, Equatable {
    case waiting
    case loading
    case restored
    case already
    case editing
    case blocked
    case changed
    case manualNeeded
    case needsBinding
    case rebind
    case unavailable
    case confirming
    case unconfirmed

    var needsUserAction: Bool {
        switch self {
        case .manualNeeded, .needsBinding, .rebind, .unavailable:
            true
        default:
            false
        }
    }

    var shortDescription: String {
        switch self {
        case .waiting:
            L10n.text("已就绪：切到该应用时自动聚焦输入框", "Ready: focuses input when switching to this app")
        case .loading:
            L10n.text("正在等待应用窗口就绪…", "Waiting for app window…")
        case .restored:
            L10n.text("已将输入焦点切入输入框", "Focused the input box")
        case .already:
            L10n.text("输入框已有焦点", "Input box is already focused")
        case .editing:
            L10n.text("正在其他编辑框操作，保持现状", "Editing elsewhere; kept current focus")
        case .blocked:
            L10n.text("当前有菜单或弹窗，未抢占焦点", "Menu or dialog is open; skipped")
        case .changed:
            L10n.text("检测到已切换或操作，已让位", "User acted or switched away")
        case .manualNeeded:
            L10n.text("未能自动唯一识别输入框，请点「指定输入框」", "Could not auto-identify input; bind it manually")
        case .needsBinding:
            L10n.text("尚未指定输入框，请点「指定输入框」", "No input bound yet")
        case .rebind:
            L10n.text("输入框结构已变化，请「重新指定」", "Input changed (app updated?); rebind required")
        case .unavailable:
            L10n.text("无法读取窗口，请检查辅助功能权限", "Unavailable; check Accessibility permission")
        case .confirming:
            L10n.text("已请求聚焦，等待确认", "Focus requested, confirming")
        case .unconfirmed:
            L10n.text("已请求聚焦，暂未收到确认", "Focus requested, unconfirmed")
        }
    }
}

enum InputFocusDiscovery {
    private static let maxDepth = 64
    private static let maxDiscoverNodes = 5000
    private static let discoverTimeout: TimeInterval = 0.85

    static let chatIdentifiers: Set<String> = [
        "prompt-textarea",
        "chat-input",
        "chat-input-textarea",
        "composer-input",
        "message-input",
        "chat-composer",
        "ai-input",
    ]

    static let chatHintPhrases: [String] = [
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
        "向豆包提问",
    ]

    static let excludedWords: [String] = [
        "search",
        "rename",
        "password",
        "filter",
        "find in",
        "搜索",
        "重命名",
        "密码",
        "查找",
    ]

    static func isChatMetadataMatch(
        identifier: String?,
        domIdentifier: String?,
        placeholder: String?,
        descriptionText: String?
    ) -> Bool {
        let id = (identifier ?? "").trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let dom = (domIdentifier ?? "").trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let ph = (placeholder ?? "").trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let desc = (descriptionText ?? "").trimmingCharacters(in: .whitespacesAndNewlines).lowercased()

        let combined = "\(id) \(dom) \(ph) \(desc)"
        if excludedWords.contains(where: { combined.contains($0) }) {
            return false
        }
        if chatIdentifiers.contains(id) || chatIdentifiers.contains(dom) {
            return true
        }
        return chatHintPhrases.contains(where: { phrase in
            ph == phrase || desc == phrase || ph.contains(phrase) || desc.contains(phrase)
        })
    }

    static func hasExcludedMetadata<B: AXTreeBackend>(
        backend: B,
        node: B.Node
    ) -> Bool {
        let combined = [
            backend.identifier(of: node) ?? "",
            backend.domIdentifier(of: node) ?? "",
            backend.placeholder(of: node) ?? "",
            backend.descriptionText(of: node) ?? "",
        ].joined(separator: " ").lowercased()
        return excludedWords.contains(where: { combined.contains($0) })
    }

    static func isProtectedTransientInput<B: AXTreeBackend>(
        backend: B,
        node: B.Node
    ) -> Bool {
        let role = backend.role(of: node) ?? ""
        let subrole = backend.subrole(of: node) ?? ""
        if role == "AXSearchField" || InputFocusRoles.blockedSubroles.contains(subrole) {
            return true
        }
        return hasExcludedMetadata(backend: backend, node: node)
    }

    /// Discovers a chat/message input box inside `window`.
    /// - Parameters:
    ///   - allowSingleFallback: When `true` (explicit button press or enabled app fallback),
    ///     a unique visible editable `AXTextArea` (or unique `AXTextField` if no `AXTextArea` exists)
    ///     is accepted even when its placeholder/description is empty.
    static func discoverTarget<B: AXTreeBackend>(
        backend: B,
        window: B.Node,
        allowSingleFallback: Bool,
        cancelled: () -> Bool = { false }
    ) -> B.Node? {
        guard !backend.isModal(window) else { return nil }

        var pending: [(B.Node, Int)] = [(window, 0)]
        var highConfidence = [B.Node]()
        var textAreas = [B.Node]()
        var otherInputs = [B.Node]()
        let deadline = Date().addingTimeInterval(discoverTimeout)
        var visited = 0

        while let (node, depth) = pending.popLast() {
            if visited >= maxDiscoverNodes || Date() >= deadline || cancelled() {
                return nil
            }
            visited += 1
            guard let role = backend.role(of: node),
                  !InputFocusRoles.blockedRoles.contains(role)
            else {
                return nil
            }

            if InputFocusRoles.editableRoles.contains(role) {
                let subrole = backend.subrole(of: node) ?? ""
                if !InputFocusRoles.blockedSubroles.contains(subrole),
                   backend.isEnabled(node),
                   backend.isFocusedSettable(node),
                   InputFocusLocatorBuilder.isVisible(backend: backend, node: node, window: window) {
                    if isChatMetadataMatch(
                        identifier: backend.identifier(of: node),
                        domIdentifier: backend.domIdentifier(of: node),
                        placeholder: backend.placeholder(of: node),
                        descriptionText: backend.descriptionText(of: node)
                    ) {
                        highConfidence.append(node)
                    } else if !hasExcludedMetadata(backend: backend, node: node) {
                        if role == "AXTextArea" {
                            textAreas.append(node)
                        } else {
                            otherInputs.append(node)
                        }
                    }
                }
                continue
            }

            if InputFocusRoles.skipDescendRoles.contains(role) {
                continue
            }
            guard let children = backend.children(of: node) else {
                return nil
            }
            if !children.isEmpty {
                if depth >= maxDepth || visited + pending.count + children.count > maxDiscoverNodes {
                    return nil
                }
                for child in children.reversed() {
                    pending.append((child, depth + 1))
                }
            }
        }

        guard pending.isEmpty, !cancelled(), Date() < deadline else {
            return nil
        }
        if highConfidence.count == 1 {
            return highConfidence[0]
        }
        if highConfidence.count > 1 {
            return nil
        }
        if allowSingleFallback {
            if textAreas.count == 1 {
                return textAreas[0]
            }
            if textAreas.isEmpty && otherInputs.count == 1 {
                return otherInputs[0]
            }
        }
        return nil
    }

    static func focusContext<B: AXTreeBackend>(
        backend: B,
        focused: B.Node?,
        window: B.Node,
        target: B.Node?
    ) -> InputFocusContextState {
        var current = focused
        for _ in 0..<maxDepth {
            guard let element = current,
                  let role = backend.role(of: element)
            else {
                return .unavailable
            }
            let subrole = backend.subrole(of: element) ?? ""
            if InputFocusRoles.blockedRoles.contains(role)
                || InputFocusRoles.blockedSubroles.contains(subrole) {
                return .blocked
            }
            if let target, element == target {
                return .already
            }
            if InputFocusRoles.editableRoles.contains(role) {
                return .editing
            }
            if element == window {
                return .clear
            }
            current = backend.parent(of: element)
        }
        return .unavailable
    }

    static func restore<B: AXTreeBackend>(
        backend: B,
        window: B.Node,
        target: B.Node,
        focusedElement: B.Node?,
        allowOverrideOtherEditor: Bool,
        allowOverrideNonSearchEditor: Bool = false,
        isSameWindowAndFrontmost: () -> Bool,
        didUserInteract: () -> Bool,
        cancelled: () -> Bool
    ) -> InputFocusResultStatus {
        if cancelled() || !isSameWindowAndFrontmost() || didUserInteract() {
            return .changed
        }
        if backend.isModal(window) {
            return .blocked
        }
        guard let role = backend.role(of: target),
              InputFocusRoles.editableRoles.contains(role),
              backend.isEnabled(target),
              backend.isFocusedSettable(target),
              InputFocusLocatorBuilder.isAttached(backend: backend, node: target, window: window)
        else {
            return .rebind
        }

        if let focusedElement {
            let state = focusContext(
                backend: backend,
                focused: focusedElement,
                window: window,
                target: target
            )
            switch state {
            case .already:
                return .already
            case .blocked:
                return .blocked
            case .editing:
                let shouldProtect = isProtectedTransientInput(backend: backend, node: focusedElement)
                if !allowOverrideOtherEditor && (shouldProtect || !allowOverrideNonSearchEditor) {
                    return .editing
                }
            case .unavailable:
                if !allowOverrideOtherEditor && !allowOverrideNonSearchEditor {
                    return .unavailable
                }
            case .clear:
                break
            }
        }

        if cancelled() || !isSameWindowAndFrontmost() || didUserInteract() {
            return .changed
        }

        _ = backend.setFocused(target)
        return backend.isFocused(target) ? .restored : .confirming
    }
}
