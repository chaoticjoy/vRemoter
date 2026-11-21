import CoreGraphics
import CryptoKit
import Foundation

/// Structural AX locator bound to a specific app build; stores no text or coordinates.
struct InputFocusLocator: Codable, Hashable {
    let version: Int
    let build: String
    let signature: String

    var isValidFormat: Bool {
        (version == 1 || version == 2)
            && Self.isSHA256Hex(build)
            && Self.isSHA256Hex(signature)
    }

    static func sha256Hex(_ input: String) -> String {
        let digest = SHA256.hash(data: Data(input.utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    static func buildDigest(
        bundleIdentifier: String,
        version: String?,
        shortVersion: String?
    ) -> String? {
        let v = version ?? ""
        let sv = shortVersion ?? ""
        guard !bundleIdentifier.isEmpty, !(v.isEmpty && sv.isEmpty) else {
            return nil
        }
        return sha256Hex("\(bundleIdentifier)|\(v)|\(sv)")
    }

    private static func isSHA256Hex(_ value: String) -> Bool {
        guard value.count == 64 else { return false }
        return value.unicodeScalars.allSatisfy { scalar in
            (scalar.value >= 48 && scalar.value <= 57)
                || (scalar.value >= 97 && scalar.value <= 102)
        }
    }
}

enum InputFocusRoles {
    static let editableRoles: Set<String> = [
        "AXTextArea",
        "AXTextField",
        "AXSearchField",
        "AXComboBox",
    ]

    static let blockedRoles: Set<String> = [
        "AXSheet",
        "AXDialog",
        "AXMenu",
        "AXMenuItem",
        "AXMenuBar",
    ]

    static let blockedSubroles: Set<String> = [
        "AXSecureTextField",
        "AXDialog",
        "AXSystemDialog",
    ]

    static let skipDescendRoles: Set<String> = [
        "AXStaticText",
        "AXImage",
        "AXMenuBar",
        "AXScrollBar",
        "AXSplitter",
        "AXValueIndicator",
    ]
}

/// Abstract AX tree backend so locator & discovery logic can be unit-tested offline.
protocol AXTreeBackend {
    associatedtype Node: Equatable

    func role(of node: Node) -> String?
    func subrole(of node: Node) -> String?
    func identifier(of node: Node) -> String?
    func domIdentifier(of node: Node) -> String?
    func placeholder(of node: Node) -> String?
    func descriptionText(of node: Node) -> String?
    func isEnabled(_ node: Node) -> Bool
    func isHidden(_ node: Node) -> Bool
    func isFocused(_ node: Node) -> Bool
    func isFocusedSettable(_ node: Node) -> Bool
    func isModal(_ window: Node) -> Bool
    func rect(of node: Node) -> CGRect?
    func parent(of node: Node) -> Node?
    func children(of node: Node) -> [Node]?
    func setFocused(_ node: Node) -> Bool
}

enum InputFocusLocatorBuilder {
    private static let maxDepth = 64
    private static let maxResolveNodes = 5000
    private static let resolveTimeout: TimeInterval = 1.5

    static func isAttached<B: AXTreeBackend>(
        backend: B,
        node: B.Node,
        window: B.Node
    ) -> Bool {
        var current: B.Node? = node
        for _ in 0..<maxDepth {
            guard let item = current else { return false }
            if item == window { return true }
            current = backend.parent(of: item)
        }
        return false
    }

    static func isVisible<B: AXTreeBackend>(
        backend: B,
        node: B.Node,
        window: B.Node
    ) -> Bool {
        if backend.isHidden(node) { return false }
        guard let elementRect = backend.rect(of: node),
              let windowRect = backend.rect(of: window)
        else {
            return false
        }
        // Reject zero-sized or tiny hidden textarea stubs (e.g. 1x1 editor helpers).
        guard elementRect.width >= 16, elementRect.height >= 12 else {
            return false
        }
        let intersection = elementRect.intersection(windowRect)
        return !intersection.isNull && intersection.width >= 8 && intersection.height >= 8
    }

    static func signature<B: AXTreeBackend>(
        backend: B,
        target: B.Node,
        window: B.Node,
        includeSurroundings: Bool = false,
        cancelled: () -> Bool = { false }
    ) -> String? {
        func metadata(of node: B.Node) -> String? {
            guard let role = backend.role(of: node),
                  !InputFocusRoles.blockedRoles.contains(role)
            else { return nil }
            let subrole = backend.subrole(of: node) ?? ""
            if InputFocusRoles.blockedSubroles.contains(subrole) {
                return nil
            }
            let ident = backend.identifier(of: node) ?? ""
            let domId = backend.domIdentifier(of: node) ?? ""
            // Only include static AXDescription for leaf/input-like nodes, not arbitrary containers.
            let desc = InputFocusRoles.editableRoles.contains(role)
                ? (backend.descriptionText(of: node) ?? "")
                : ""
            return "\(role)|\(subrole)|\(ident)|\(domId)|\(desc)"
        }

        var chain = [String]()
        var surroundings = [[String]]()
        var current: B.Node? = target
        for depth in 0..<maxDepth {
            if cancelled() { return nil }
            guard let node = current, let meta = metadata(of: node) else {
                return nil
            }
            chain.append(meta)
            if node == window {
                if includeSurroundings {
                    let payload = chain.joined(separator: "\n")
                        + "\n---\n"
                        + surroundings.map { $0.joined(separator: ",") }.joined(separator: "\n")
                    return InputFocusLocator.sha256Hex(payload)
                }
                return InputFocusLocator.sha256Hex(chain.joined(separator: "\n"))
            }
            if includeSurroundings,
               (1...3).contains(depth),
               let children = backend.children(of: node),
               children.count <= 64 {
                var childMeta = [String]()
                childMeta.reserveCapacity(children.count)
                for child in children {
                    guard let item = metadata(of: child) else { return nil }
                    childMeta.append(item)
                }
                surroundings.append(childMeta)
            }
            current = backend.parent(of: node)
        }
        return nil
    }

    static func describe<B: AXTreeBackend>(
        backend: B,
        window: B.Node,
        target: B.Node,
        build: String?
    ) -> InputFocusLocator? {
        guard let build, !build.isEmpty,
              let sig = signature(
                backend: backend,
                target: target,
                window: window,
                includeSurroundings: false
              )
        else { return nil }
        let locator = InputFocusLocator(version: 2, build: build, signature: sig)
        guard resolve(
            backend: backend,
            window: window,
            locator: locator,
            build: build
        ) == target else {
            return nil
        }
        return locator
    }

    static func resolve<B: AXTreeBackend>(
        backend: B,
        window: B.Node,
        locator: InputFocusLocator,
        build: String?,
        cancelled: () -> Bool = { false }
    ) -> B.Node? {
        guard locator.isValidFormat,
              let build, !build.isEmpty,
              locator.build == build,
              !backend.isModal(window)
        else { return nil }

        var pending: [(B.Node, Int)] = [(window, 0)]
        var matches = [B.Node]()
        let deadline = Date().addingTimeInterval(resolveTimeout)
        var visited = 0

        while let (node, depth) = pending.popLast() {
            if visited >= maxResolveNodes || Date() >= deadline || cancelled() {
                return nil
            }
            visited += 1
            guard let role = backend.role(of: node),
                  !InputFocusRoles.blockedRoles.contains(role)
            else { return nil }

            if InputFocusRoles.editableRoles.contains(role) {
                let subrole = backend.subrole(of: node) ?? ""
                if !InputFocusRoles.blockedSubroles.contains(subrole),
                   backend.isEnabled(node),
                   backend.isFocusedSettable(node),
                   isVisible(backend: backend, node: node, window: window),
                   signature(
                       backend: backend,
                       target: node,
                       window: window,
                       includeSurroundings: locator.version == 1,
                       cancelled: cancelled
                   ) == locator.signature {
                    matches.append(node)
                    if matches.count > 1 { return nil }
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
                if depth >= maxDepth || visited + pending.count + children.count > maxResolveNodes {
                    return nil
                }
                for child in children.reversed() {
                    pending.append((child, depth + 1))
                }
            }
        }

        guard pending.isEmpty, !cancelled(), Date() < deadline, matches.count == 1 else {
            return nil
        }
        return matches[0]
    }
}
