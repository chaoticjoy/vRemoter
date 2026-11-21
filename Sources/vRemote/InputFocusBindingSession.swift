import Foundation

struct InputFocusBindingSession {
    let bundleIdentifier: String
    let appName: String
    let deadline: Date
    var draftApp: MacFocusAppInfo?
    var draftWindow: MacAXNode?
    var draftElement: MacAXNode?

    mutating func tick(backend: MacAXFocusBackend) -> InputFocusBindingState {
        let remaining = max(0, Int(ceil(deadline.timeIntervalSinceNow)))
        guard remaining > 0 else {
            return InputFocusBindingState(
                selectingBundleID: bundleIdentifier,
                selectingAppName: appName,
                candidateReady: draftElement != nil,
                captureStatus: draftElement != nil ? .captured : .focusUnavailable,
                secondsLeft: 0
            )
        }

        guard let front = backend.frontmostApp(),
              front.bundleIdentifier == bundleIdentifier
        else {
            return InputFocusBindingState(
                selectingBundleID: bundleIdentifier,
                selectingAppName: appName,
                candidateReady: draftElement != nil,
                captureStatus: draftElement != nil ? .captured : .waiting,
                secondsLeft: remaining
            )
        }

        backend.prepareEnhancedUI(for: front) { false }
        guard let window = backend.focusedWindow(for: front) else {
            return InputFocusBindingState(
                selectingBundleID: bundleIdentifier,
                selectingAppName: appName,
                candidateReady: false,
                captureStatus: .windowUnavailable,
                secondsLeft: remaining
            )
        }
        guard let target = backend.focusedElement(for: front) else {
            return InputFocusBindingState(
                selectingBundleID: bundleIdentifier,
                selectingAppName: appName,
                candidateReady: false,
                captureStatus: .focusUnavailable,
                secondsLeft: remaining
            )
        }

        let role = backend.role(of: target) ?? ""
        let subrole = backend.subrole(of: target) ?? ""
        let isValidEditor = InputFocusRoles.editableRoles.contains(role)
            && !InputFocusRoles.blockedSubroles.contains(subrole)
            && backend.isEnabled(target)
            && backend.isFocusedSettable(target)
            && InputFocusLocatorBuilder.isAttached(backend: backend, node: target, window: window)

        if isValidEditor {
            draftApp = front
            draftWindow = window
            draftElement = target
            return InputFocusBindingState(
                selectingBundleID: bundleIdentifier,
                selectingAppName: appName,
                candidateReady: true,
                captureStatus: .captured,
                secondsLeft: remaining
            )
        }

        return InputFocusBindingState(
            selectingBundleID: bundleIdentifier,
            selectingAppName: appName,
            candidateReady: draftElement != nil,
            captureStatus: draftElement != nil ? .captured : .notEditor,
            secondsLeft: remaining
        )
    }
}
