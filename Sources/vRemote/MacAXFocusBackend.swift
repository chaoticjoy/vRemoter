import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

struct MacAXNode: Equatable {
    let element: AXUIElement

    static func == (lhs: MacAXNode, rhs: MacAXNode) -> Bool {
        CFEqual(lhs.element, rhs.element)
    }
}

struct MacFocusAppInfo: Equatable, Hashable, Identifiable {
    var id: String { bundleIdentifier }
    let bundleIdentifier: String
    let pid: pid_t
    let name: String
    let appPath: String?
    let buildHash: String?
}

final class MacAXFocusBackend: AXTreeBackend {
    typealias Node = MacAXNode

    private let lock = NSLock()
    private var enhancedAttempted: [String: pid_t] = [:]
    private var cachedMainThreadFrontmost: MacFocusAppInfo?

    init() {
        AXUIElementSetMessagingTimeout(AXUIElementCreateSystemWide(), 0.08)
    }

    func isTrusted() -> Bool {
        AXIsProcessTrusted()
    }

    func updateFrontmostFromMainThread(_ running: NSRunningApplication?) {
        let info = appInfo(from: running ?? NSWorkspace.shared.frontmostApplication)
        lock.lock()
        cachedMainThreadFrontmost = info
        lock.unlock()
    }

    func frontmostApp() -> MacFocusAppInfo? {
        if Thread.isMainThread {
            let info = appInfo(from: NSWorkspace.shared.frontmostApplication)
            lock.lock()
            cachedMainThreadFrontmost = info
            lock.unlock()
            return info
        }
        // Prefer live system-wide AX focused application PID so background GCD threads
        // never read a stale NSWorkspace.shared.frontmostApplication cache.
        if isTrusted() {
            let systemWide = AXUIElementCreateSystemWide()
            var focusedApp: AnyObject?
            if AXUIElementCopyAttributeValue(
                systemWide,
                kAXFocusedApplicationAttribute as CFString,
                &focusedApp
            ) == .success,
                let raw = focusedApp,
                CFGetTypeID(raw) == AXUIElementGetTypeID() {
                var pid: pid_t = 0
                if AXUIElementGetPid(raw as! AXUIElement, &pid) == .success,
                   pid > 0,
                   let running = NSRunningApplication(processIdentifier: pid),
                   let info = appInfo(from: running) {
                    return info
                }
            }
        }
        lock.lock()
        let cached = cachedMainThreadFrontmost
        lock.unlock()
        return cached ?? appInfo(from: NSWorkspace.shared.frontmostApplication)
    }

    func runningAppsForSelection() -> [MacFocusAppInfo] {
        let selfPID = ProcessInfo.processInfo.processIdentifier
        return NSWorkspace.shared.runningApplications
            .filter { app in
                app.activationPolicy == .regular
                    && app.processIdentifier != selfPID
                    && app.bundleIdentifier != nil
            }
            .compactMap { appInfo(from: $0) }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    func appInfo(from running: NSRunningApplication?) -> MacFocusAppInfo? {
        guard let running,
              let bundleID = running.bundleIdentifier,
              !bundleID.isEmpty
        else { return nil }
        let name = running.localizedName ?? bundleID
        let bundleURL = running.bundleURL
        let buildHash = bundleURL.flatMap { Self.buildHash(bundleID: bundleID, bundleURL: $0) }
        return MacFocusAppInfo(
            bundleIdentifier: bundleID,
            pid: running.processIdentifier,
            name: name,
            appPath: bundleURL?.path,
            buildHash: buildHash
        )
    }

    static func appInfo(fromAppBundleURL url: URL) -> MacFocusAppInfo? {
        guard url.pathExtension.lowercased() == "app",
              let bundle = Bundle(url: url),
              let bundleID = bundle.bundleIdentifier,
              !bundleID.isEmpty
        else { return nil }
        let info = bundle.infoDictionary
        let displayName = (info?["CFBundleDisplayName"] as? String)
            ?? (info?["CFBundleName"] as? String)
            ?? url.deletingPathExtension().lastPathComponent
        let buildHash = buildHash(bundleID: bundleID, bundleURL: url)
        return MacFocusAppInfo(
            bundleIdentifier: bundleID,
            pid: 0,
            name: displayName,
            appPath: url.path,
            buildHash: buildHash
        )
    }

    static func buildHash(bundleID: String, bundleURL: URL) -> String? {
        guard let bundle = Bundle(url: bundleURL) else { return nil }
        let version = bundle.object(forInfoDictionaryKey: "CFBundleVersion") as? String
        let short = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
        return InputFocusLocator.buildDigest(
            bundleIdentifier: bundleID,
            version: version,
            shortVersion: short
        )
    }

    func applicationElement(for app: MacFocusAppInfo) -> AXUIElement? {
        guard isTrusted(), app.pid > 0 else { return nil }
        let element = AXUIElementCreateApplication(app.pid)
        AXUIElementSetMessagingTimeout(element, 0.12)
        return element
    }

    /// Ask Electron / WebKit / Chromium apps to expose their web accessibility tree.
    func prepareEnhancedUI(for app: MacFocusAppInfo, cancelled: () -> Bool) {
        guard !cancelled(),
              enhancedAttempted[app.bundleIdentifier] != app.pid,
              let root = applicationElement(for: app)
        else { return }
        let attr = "AXEnhancedUserInterface" as CFString
        if let value = copyAttribute(root, attr) as? Bool, value {
            enhancedAttempted[app.bundleIdentifier] = app.pid
            return
        }
        var settable: DarwinBoolean = false
        let err = AXUIElementIsAttributeSettable(root, attr, &settable)
        guard err == .success, settable.boolValue, !cancelled() else { return }
        enhancedAttempted[app.bundleIdentifier] = app.pid
        // Electron/Chromium may return -25208 even when readback becomes true.
        _ = AXUIElementSetAttributeValue(root, attr, kCFBooleanTrue)
    }

    func focusedWindow(for app: MacFocusAppInfo) -> MacAXNode? {
        guard let root = applicationElement(for: app) else { return nil }
        if let win = copyElementAttribute(root, kAXFocusedWindowAttribute as CFString) {
            return win
        }
        if let windows = copyAttribute(root, kAXWindowsAttribute as CFString) as? [AXUIElement],
           let first = windows.first {
            return MacAXNode(element: first)
        }
        return nil
    }

    func focusedElement(for app: MacFocusAppInfo) -> MacAXNode? {
        guard let root = applicationElement(for: app) else { return nil }
        return copyElementAttribute(root, kAXFocusedUIElementAttribute as CFString)
    }

    func inputStamp() -> [UInt32] {
        // Exclude scrollWheel so trackpad momentum scroll from the previous app
        // does not falsely abort focus right after switching applications.
        let types: [CGEventType] = [
            .keyDown,
            .leftMouseDown,
            .rightMouseDown,
            .otherMouseDown,
        ]
        return types.map {
            CGEventSource.counterForEventType(.combinedSessionState, eventType: $0)
        }
    }

    // MARK: - AXTreeBackend

    func role(of node: MacAXNode) -> String? {
        copyStringAttribute(node.element, kAXRoleAttribute as CFString)
    }

    func subrole(of node: MacAXNode) -> String? {
        copyStringAttribute(node.element, kAXSubroleAttribute as CFString)
    }

    func identifier(of node: MacAXNode) -> String? {
        copyStringAttribute(node.element, kAXIdentifierAttribute as CFString)
    }

    func domIdentifier(of node: MacAXNode) -> String? {
        copyStringAttribute(node.element, "AXDOMIdentifier" as CFString)
    }

    func placeholder(of node: MacAXNode) -> String? {
        copyStringAttribute(node.element, kAXPlaceholderValueAttribute as CFString)
    }

    func descriptionText(of node: MacAXNode) -> String? {
        copyStringAttribute(node.element, kAXDescriptionAttribute as CFString)
    }

    func isEnabled(_ node: MacAXNode) -> Bool {
        (copyAttribute(node.element, kAXEnabledAttribute as CFString) as? Bool) ?? false
    }

    func isHidden(_ node: MacAXNode) -> Bool {
        (copyAttribute(node.element, "AXHidden" as CFString) as? Bool) ?? false
    }

    func isFocused(_ node: MacAXNode) -> Bool {
        (copyAttribute(node.element, kAXFocusedAttribute as CFString) as? Bool) ?? false
    }

    func isFocusedSettable(_ node: MacAXNode) -> Bool {
        var settable: DarwinBoolean = false
        let err = AXUIElementIsAttributeSettable(
            node.element,
            kAXFocusedAttribute as CFString,
            &settable
        )
        return err == .success && settable.boolValue
    }

    func isModal(_ window: MacAXNode) -> Bool {
        if let sheets = copyAttribute(window.element, "AXSheets" as CFString) as? [AXUIElement],
           !sheets.isEmpty {
            return true
        }
        if let modal = copyAttribute(window.element, kAXModalAttribute as CFString) as? Bool,
           modal {
            return true
        }
        if let sub = subrole(of: window),
           sub == "AXDialog" || sub == "AXSystemDialog" {
            return true
        }
        return false
    }

    func rect(of node: MacAXNode) -> CGRect? {
        guard let posVal = copyAttribute(node.element, kAXPositionAttribute as CFString),
              let sizeVal = copyAttribute(node.element, kAXSizeAttribute as CFString),
              CFGetTypeID(posVal) == AXValueGetTypeID(),
              CFGetTypeID(sizeVal) == AXValueGetTypeID()
        else { return nil }
        var point = CGPoint.zero
        var size = CGSize.zero
        guard AXValueGetValue(posVal as! AXValue, .cgPoint, &point),
              AXValueGetValue(sizeVal as! AXValue, .cgSize, &size)
        else { return nil }
        return CGRect(origin: point, size: size)
    }

    func parent(of node: MacAXNode) -> MacAXNode? {
        copyElementAttribute(node.element, kAXParentAttribute as CFString)
    }

    func children(of node: MacAXNode) -> [MacAXNode]? {
        var value: AnyObject?
        let err = AXUIElementCopyAttributeValue(
            node.element,
            kAXChildrenAttribute as CFString,
            &value
        )
        if err == .noValue || err == .attributeUnsupported {
            return []
        }
        guard err == .success else { return nil }
        guard let array = value as? [AXUIElement] else { return [] }
        return array.map(MacAXNode.init(element:))
    }

    func setFocused(_ node: MacAXNode) -> Bool {
        let err = AXUIElementSetAttributeValue(
            node.element,
            kAXFocusedAttribute as CFString,
            kCFBooleanTrue
        )
        return err == .success
    }

    // MARK: - Helpers

    private func copyAttribute(_ element: AXUIElement, _ attr: CFString) -> AnyObject? {
        var value: AnyObject?
        let err = AXUIElementCopyAttributeValue(element, attr, &value)
        return err == .success ? value : nil
    }

    private func copyStringAttribute(_ element: AXUIElement, _ attr: CFString) -> String? {
        copyAttribute(element, attr) as? String
    }

    private func copyElementAttribute(_ element: AXUIElement, _ attr: CFString) -> MacAXNode? {
        guard let raw = copyAttribute(element, attr),
              CFGetTypeID(raw) == AXUIElementGetTypeID()
        else { return nil }
        return MacAXNode(element: raw as! AXUIElement)
    }
}
