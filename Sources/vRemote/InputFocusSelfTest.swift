#if DEBUG
import CoreGraphics
import Foundation

enum InputFocusSelfTest {
    private final class FakeNode: Equatable {
        static func == (lhs: FakeNode, rhs: FakeNode) -> Bool { lhs === rhs }
        var role: String?
        var subrole: String?
        var identifier: String?
        var domIdentifier: String?
        var placeholder: String?
        var descriptionText: String?
        var enabled = true
        var hidden = false
        var focused = false
        var focusedSettable = true
        var modal = false
        var rect: CGRect? = CGRect(x: 100, y: 100, width: 400, height: 48)
        weak var parent: FakeNode?
        var children: [FakeNode] = []

        init(
            role: String?,
            subrole: String? = nil,
            identifier: String? = nil,
            domIdentifier: String? = nil,
            placeholder: String? = nil,
            descriptionText: String? = nil,
            rect: CGRect? = CGRect(x: 100, y: 100, width: 400, height: 48)
        ) {
            self.role = role
            self.subrole = subrole
            self.identifier = identifier
            self.domIdentifier = domIdentifier
            self.placeholder = placeholder
            self.descriptionText = descriptionText
            self.rect = rect
        }

        func addChild(_ child: FakeNode) {
            child.parent = self
            children.append(child)
        }
    }

    private final class FakeAXBackend: AXTreeBackend {
        typealias Node = FakeNode

        var focusRequests = [FakeNode]()
        var acceptFocus = true

        func role(of node: FakeNode) -> String? { node.role }
        func subrole(of node: FakeNode) -> String? { node.subrole }
        func identifier(of node: FakeNode) -> String? { node.identifier }
        func domIdentifier(of node: FakeNode) -> String? { node.domIdentifier }
        func placeholder(of node: FakeNode) -> String? { node.placeholder }
        func descriptionText(of node: FakeNode) -> String? { node.descriptionText }
        func isEnabled(_ node: FakeNode) -> Bool { node.enabled }
        func isHidden(_ node: FakeNode) -> Bool { node.hidden }
        func isFocused(_ node: FakeNode) -> Bool { node.focused }
        func isFocusedSettable(_ node: FakeNode) -> Bool { node.focusedSettable }
        func isModal(_ window: FakeNode) -> Bool { window.modal }
        func rect(of node: FakeNode) -> CGRect? { node.rect }
        func parent(of node: FakeNode) -> FakeNode? { node.parent }
        func children(of node: FakeNode) -> [FakeNode]? { node.children }
        func setFocused(_ node: FakeNode) -> Bool {
            focusRequests.append(node)
            if acceptFocus { node.focused = true }
            return acceptFocus
        }
    }

    static func run(expect: (@autoclosure () -> Bool, String) -> Void) {
        testAntigravityStyleDescriptionDiscovery(expect)
        testDeepTreeBeyond700Nodes(expect)
        testAntiStealWhenEditingSearchField(expect)
        testPullFromNonSearchCodeEditorOnSwitch(expect)
        testManualButtonOverridesOtherEditor(expect)
        testSingleFallbackWhenNoMetadata(expect)
        testLocatorDescribeResolveAndVersionMismatch(expect)
        testModalWindowBlocked(expect)
        testUserInteractionAbortsRestore(expect)
    }

    private static func makeWindow(rect: CGRect = CGRect(x: 0, y: 0, width: 1200, height: 800)) -> FakeNode {
        FakeNode(role: "AXWindow", rect: rect)
    }

    private static func testAntigravityStyleDescriptionDiscovery(
        _ expect: (@autoclosure () -> Bool, String) -> Void
    ) {
        let backend = FakeAXBackend()
        let window = makeWindow()
        let webArea = FakeNode(role: "AXWebArea", rect: CGRect(x: 0, y: 0, width: 1200, height: 800))
        let search = FakeNode(role: "AXTextField", placeholder: "Search files", rect: CGRect(x: 20, y: 20, width: 200, height: 30))
        let chatInput = FakeNode(role: "AXTextArea", descriptionText: "Message input", rect: CGRect(x: 700, y: 700, width: 450, height: 48))
        window.addChild(search)
        window.addChild(webArea)
        webArea.addChild(chatInput)

        let discovered = InputFocusDiscovery.discoverTarget(
            backend: backend,
            window: window,
            allowSingleFallback: false
        )
        expect(discovered === chatInput, "focus: discovers Antigravity-style Message input and ignores search")
    }

    private static func testDeepTreeBeyond700Nodes(
        _ expect: (@autoclosure () -> Bool, String) -> Void
    ) {
        let backend = FakeAXBackend()
        let window = makeWindow()
        let group = FakeNode(role: "AXGroup", rect: CGRect(x: 0, y: 0, width: 1200, height: 800))
        window.addChild(group)
        for _ in 0..<950 {
            group.addChild(FakeNode(role: "AXStaticText"))
        }
        let chatInput = FakeNode(role: "AXTextArea", descriptionText: "Message input", rect: CGRect(x: 700, y: 700, width: 450, height: 48))
        group.addChild(chatInput)

        let discovered = InputFocusDiscovery.discoverTarget(
            backend: backend,
            window: window,
            allowSingleFallback: false
        )
        expect(discovered === chatInput, "focus: discovers input beyond 700 AX nodes")
    }

    private static func testAntiStealWhenEditingSearchField(
        _ expect: (@autoclosure () -> Bool, String) -> Void
    ) {
        let backend = FakeAXBackend()
        let window = makeWindow()
        let search = FakeNode(role: "AXTextField", placeholder: "Search")
        let chatInput = FakeNode(role: "AXTextArea", descriptionText: "Message input")
        window.addChild(search)
        window.addChild(chatInput)

        let status = InputFocusDiscovery.restore(
            backend: backend,
            window: window,
            target: chatInput,
            focusedElement: search,
            allowOverrideOtherEditor: false,
            allowOverrideNonSearchEditor: true,
            isSameWindowAndFrontmost: { true },
            didUserInteract: { false },
            cancelled: { false }
        )
        expect(status == .editing, "focus: auto switch does not steal focus from search field")
        expect(backend.focusRequests.isEmpty, "focus: no AXFocused write when editing search field")
    }

    private static func testPullFromNonSearchCodeEditorOnSwitch(
        _ expect: (@autoclosure () -> Bool, String) -> Void
    ) {
        let backend = FakeAXBackend()
        let window = makeWindow()
        let codeEditor = FakeNode(role: "AXTextArea", descriptionText: "build-driver.sh，预览")
        let chatInput = FakeNode(role: "AXTextArea", descriptionText: "Message input")
        window.addChild(codeEditor)
        window.addChild(chatInput)

        let status = InputFocusDiscovery.restore(
            backend: backend,
            window: window,
            target: chatInput,
            focusedElement: codeEditor,
            allowOverrideOtherEditor: false,
            allowOverrideNonSearchEditor: true,
            isSameWindowAndFrontmost: { true },
            didUserInteract: { false },
            cancelled: { false }
        )
        expect(status == .restored, "focus: pulls focus from code editor into Message input on switch")
        expect(backend.focusRequests.first === chatInput, "focus: focused target is Message input")
    }

    private static func testManualButtonOverridesOtherEditor(
        _ expect: (@autoclosure () -> Bool, String) -> Void
    ) {
        let backend = FakeAXBackend()
        let window = makeWindow()
        let codeEditor = FakeNode(role: "AXTextField", identifier: "workbench.editor")
        let chatInput = FakeNode(role: "AXTextArea", descriptionText: "Message input")
        window.addChild(codeEditor)
        window.addChild(chatInput)

        let status = InputFocusDiscovery.restore(
            backend: backend,
            window: window,
            target: chatInput,
            focusedElement: codeEditor,
            allowOverrideOtherEditor: true,
            isSameWindowAndFrontmost: { true },
            didUserInteract: { false },
            cancelled: { false }
        )
        expect(status == .restored, "focus: manual button pulls focus from other editor into chat input")
        expect(backend.focusRequests.first === chatInput, "focus: focused target is chatInput")
    }

    private static func testSingleFallbackWhenNoMetadata(
        _ expect: (@autoclosure () -> Bool, String) -> Void
    ) {
        let backend = FakeAXBackend()
        let window = makeWindow()
        let tinyStub = FakeNode(role: "AXTextArea", rect: CGRect(x: 0, y: 0, width: 2, height: 2))
        let mainInput = FakeNode(role: "AXTextArea", rect: CGRect(x: 100, y: 500, width: 500, height: 60))
        window.addChild(tinyStub)
        window.addChild(mainInput)

        let strict = InputFocusDiscovery.discoverTarget(
            backend: backend,
            window: window,
            allowSingleFallback: false
        )
        let fallback = InputFocusDiscovery.discoverTarget(
            backend: backend,
            window: window,
            allowSingleFallback: true
        )
        expect(strict == nil, "focus: strict discovery requires chat metadata")
        expect(fallback === mainInput, "focus: single fallback ignores tiny 2x2 stub and finds main textarea")
    }

    private static func testLocatorDescribeResolveAndVersionMismatch(
        _ expect: (@autoclosure () -> Bool, String) -> Void
    ) {
        let backend = FakeAXBackend()
        let window = makeWindow()
        let panel = FakeNode(role: "AXGroup", identifier: "agent-panel", rect: CGRect(x: 600, y: 0, width: 600, height: 800))
        let composer = FakeNode(role: "AXGroup", identifier: "composer", rect: CGRect(x: 600, y: 650, width: 580, height: 120))
        let input = FakeNode(role: "AXTextArea", descriptionText: "Message input", rect: CGRect(x: 620, y: 700, width: 500, height: 48))
        let sendBtn = FakeNode(role: "AXButton", identifier: "send-btn", rect: CGRect(x: 1130, y: 700, width: 32, height: 32))
        window.addChild(panel)
        panel.addChild(composer)
        composer.addChild(input)
        composer.addChild(sendBtn)

        let build = InputFocusLocator.buildDigest(bundleIdentifier: "com.google.antigravity-ide", version: "100", shortVersion: "1.0.0")
        let locator = InputFocusLocatorBuilder.describe(backend: backend, window: window, target: input, build: build)
        expect(locator != nil, "focus: locator describe produces valid structural locator")

        let resolved = locator.flatMap {
            InputFocusLocatorBuilder.resolve(backend: backend, window: window, locator: $0, build: build)
        }
        expect(resolved === input, "focus: locator resolve finds exact bound node")

        let wrongBuild = InputFocusLocator.buildDigest(bundleIdentifier: "com.google.antigravity-ide", version: "101", shortVersion: "1.0.1")
        let mismatched = locator.flatMap {
            InputFocusLocatorBuilder.resolve(backend: backend, window: window, locator: $0, build: wrongBuild)
        }
        expect(mismatched == nil, "focus: locator rejects mismatched app build")
    }

    private static func testModalWindowBlocked(
        _ expect: (@autoclosure () -> Bool, String) -> Void
    ) {
        let backend = FakeAXBackend()
        let window = makeWindow()
        window.modal = true
        let input = FakeNode(role: "AXTextArea", descriptionText: "Message input")
        window.addChild(input)

        let status = InputFocusDiscovery.restore(
            backend: backend,
            window: window,
            target: input,
            focusedElement: nil,
            allowOverrideOtherEditor: true,
            isSameWindowAndFrontmost: { true },
            didUserInteract: { false },
            cancelled: { false }
        )
        expect(status == .blocked, "focus: modal window blocks focus restoration")
    }

    private static func testUserInteractionAbortsRestore(
        _ expect: (@autoclosure () -> Bool, String) -> Void
    ) {
        let backend = FakeAXBackend()
        let window = makeWindow()
        let input = FakeNode(role: "AXTextArea", descriptionText: "Message input")
        window.addChild(input)

        let status = InputFocusDiscovery.restore(
            backend: backend,
            window: window,
            target: input,
            focusedElement: nil,
            allowOverrideOtherEditor: false,
            isSameWindowAndFrontmost: { true },
            didUserInteract: { true },
            cancelled: { false }
        )
        expect(status == .changed, "focus: user input yields instead of stealing focus")
        expect(backend.focusRequests.isEmpty, "focus: no focus write after user interaction")
    }
}
#endif
