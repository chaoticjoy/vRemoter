import AppKit
import Foundation

final class InputFocusCoordinator {
    static let shared = InputFocusCoordinator()

    private let workerQueue = DispatchQueue(
        label: "io.github.ccc333cn.vremoter.input-focus",
        qos: .userInitiated
    )
    private let backend = MacAXFocusBackend()

    private var activationObserver: NSObjectProtocol?
    private var pollTimer: DispatchSourceTimer?
    private var isStarted = false

    private struct PendingFocusJob {
        let bundleIdentifier: String
        let deadline: Date
        let initialStamp: [UInt32]
        let allowSingleFallback: Bool
        let allowOverrideOtherEditor: Bool
        let allowOverrideNonSearchEditor: Bool
        let isPostLaunch: Bool
        var scanAttempts: Int
    }

    private var sessionCache: [String: (pid: pid_t, window: MacAXNode, element: MacAXNode)] = [:]
    private var pendingJob: PendingFocusJob?
    private var postLaunchExpected: (bundleID: String, expiresAt: Date)?
    private var bindingSession: InputFocusBindingSession?

    private init() {}

    func start() {
        guard !isStarted else { return }
        isStarted = true
        backend.updateFrontmostFromMainThread(NSWorkspace.shared.frontmostApplication)
        activationObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            let app = notification.userInfo?[NSWorkspace.applicationUserInfoKey]
                as? NSRunningApplication
            self?.backend.updateFrontmostFromMainThread(app)
            self?.handleAppActivated(bundleID: app?.bundleIdentifier)
        }
        print("[INPUT-FOCUS] coordinator started")
    }

    func stop() {
        if let activationObserver {
            NSWorkspace.shared.notificationCenter.removeObserver(activationObserver)
            self.activationObserver = nil
        }
        workerQueue.async { [weak self] in
            self?.stopTimerOnWorker()
            self?.pendingJob = nil
            self?.postLaunchExpected = nil
            self?.bindingSession = nil
        }
        isStarted = false
    }

    func requestPostLaunchFocus(bundleIdentifier: String) {
        workerQueue.async { [weak self] in
            guard let self else { return }
            self.postLaunchExpected = (bundleIdentifier, Date().addingTimeInterval(7.0))
            if let front = self.backend.frontmostApp(), front.bundleIdentifier == bundleIdentifier {
                self.startFocusJob(
                    bundleID: bundleIdentifier,
                    timeout: 5.0,
                    allowSingleFallback: true,
                    allowOverrideOtherEditor: false,
                    allowOverrideNonSearchEditor: InputFocusStore.shared.snapshot().pullFromOtherEditorsOnSwitch,
                    isPostLaunch: true
                )
            } else {
                self.ensureTimerRunning()
            }
        }
    }

    /// Focuses the input box of whichever external app is currently frontmost.
    func focusFrontmostNow() {
        workerQueue.async { [weak self] in
            guard let self else { return }
            guard self.backend.isTrusted() else {
                self.publishFeedback(L10n.text(
                    "未授予辅助功能权限，无法聚焦输入框",
                    "Accessibility permission required to focus input"
                ))
                return
            }
            guard let front = self.backend.frontmostApp() else { return }
            if front.bundleIdentifier == Bundle.main.bundleIdentifier {
                self.publishFeedback(L10n.text(
                    "当前前台为 vRemoter 本身，请先切换到目标应用",
                    "Frontmost app is vRemoter; switch to target app first"
                ))
                return
            }
            let entry = InputFocusStore.shared.snapshot().apps[front.bundleIdentifier]
            let status = self.attemptFocus(
                app: front,
                entry: entry,
                initialStamp: self.backend.inputStamp(),
                allowSingleFallback: true,
                allowOverrideOtherEditor: true,
                allowOverrideNonSearchEditor: true,
                updateStoreState: entry != nil
            )
            print("[INPUT-FOCUS] manual-trigger \(front.bundleIdentifier) -> \(status.rawValue)")
            self.publishFeedback("\(front.name): \(status.shortDescription)")
        }
    }

    /// Fast pre-voice check before triggering voice recognition.
    func ensureFocusedForVoiceIfNeeded() {
        workerQueue.async { [weak self] in
            guard let self else { return }
            let snapshot = InputFocusStore.shared.snapshot()
            guard snapshot.focusBeforeVoiceEnabled,
                  self.backend.isTrusted(),
                  let front = self.backend.frontmostApp(),
                  let entry = snapshot.apps[front.bundleIdentifier],
                  entry.enabled
            else { return }
            let status = self.attemptFocus(
                app: front,
                entry: entry,
                initialStamp: self.backend.inputStamp(),
                allowSingleFallback: true,
                allowOverrideOtherEditor: false,
                allowOverrideNonSearchEditor: snapshot.pullFromOtherEditorsOnSwitch,
                updateStoreState: true
            )
            print("[INPUT-FOCUS] pre-voice \(front.bundleIdentifier) -> \(status.rawValue)")
        }
    }

    func beginBinding(bundleIdentifier: String, appName: String) {
        workerQueue.async { [weak self] in
            guard let self else { return }
            guard self.backend.isTrusted() else {
                self.publishBindingState(InputFocusBindingState())
                self.publishFeedback(L10n.text(
                    "请先授予辅助功能权限，再指定输入框",
                    "Grant Accessibility permission before binding an input box"
                ))
                return
            }
            self.pendingJob = nil
            self.bindingSession = InputFocusBindingSession(
                bundleIdentifier: bundleIdentifier,
                appName: appName,
                deadline: Date().addingTimeInterval(30.0)
            )
            self.publishBindingState(InputFocusBindingState(
                selectingBundleID: bundleIdentifier,
                selectingAppName: appName,
                candidateReady: false,
                captureStatus: .waiting,
                secondsLeft: 30
            ))
            self.ensureTimerRunning()
        }
    }

    func confirmBinding() {
        workerQueue.async { [weak self] in
            guard let self, let session = self.bindingSession else { return }
            guard let app = session.draftApp,
                  let window = session.draftWindow,
                  let element = session.draftElement,
                  app.bundleIdentifier == session.bundleIdentifier,
                  InputFocusLocatorBuilder.isAttached(
                    backend: self.backend,
                    node: element,
                    window: window
                  )
            else {
                self.publishFeedback(L10n.text(
                    "尚未捕获到有效输入框，请重试",
                    "No valid input captured yet; please retry"
                ))
                return
            }
            let locator = InputFocusLocatorBuilder.describe(
                backend: self.backend,
                window: window,
                target: element,
                build: app.buildHash
            )
            self.sessionCache[session.bundleIdentifier] = (app.pid, window, element)
            self.bindingSession = nil
            self.stopTimerIfIdle()
            DispatchQueue.main.async {
                InputFocusStore.shared.saveLocator(locator, for: session.bundleIdentifier)
                InputFocusStore.shared.bindingState = InputFocusBindingState()
                InputFocusStore.shared.setFeedback(
                    locator != nil
                        ? L10n.text("已保存输入框结构指纹，重启后自动恢复", "Saved input locator; persists across restarts")
                        : L10n.text("本次已绑定输入框（当前会话内有效）", "Input bound for current session")
                )
            }
        }
    }

    func cancelBinding() {
        workerQueue.async { [weak self] in
            guard let self else { return }
            self.bindingSession = nil
            self.stopTimerIfIdle()
            self.publishBindingState(InputFocusBindingState())
        }
    }

    // MARK: - Private Worker Logic

    private func handleAppActivated(bundleID: String?) {
        workerQueue.async { [weak self] in
            guard let self else { return }
            guard let front = self.backend.frontmostApp() else { return }
            let activeID = bundleID ?? front.bundleIdentifier
            if front.bundleIdentifier != activeID {
                print("[INPUT-FOCUS] activation ignored front=\(front.bundleIdentifier) expected=\(activeID)")
                return
            }

            let snapshot = InputFocusStore.shared.snapshot()
            if let expected = self.postLaunchExpected {
                if expected.bundleID == activeID, Date() < expected.expiresAt {
                    self.postLaunchExpected = nil
                    self.startFocusJob(
                        bundleID: activeID,
                        timeout: 5.0,
                        allowSingleFallback: true,
                        allowOverrideOtherEditor: false,
                        allowOverrideNonSearchEditor: snapshot.pullFromOtherEditorsOnSwitch,
                        isPostLaunch: true
                    )
                    return
                } else if expected.bundleID != activeID {
                    self.postLaunchExpected = nil
                }
            }

            guard snapshot.autoFocusOnSwitchEnabled,
                  let entry = snapshot.apps[activeID],
                  entry.enabled
            else {
                self.pendingJob = nil
                return
            }
            self.startFocusJob(
                bundleID: activeID,
                timeout: 3.0,
                allowSingleFallback: true,
                allowOverrideOtherEditor: false,
                allowOverrideNonSearchEditor: snapshot.pullFromOtherEditorsOnSwitch,
                isPostLaunch: false
            )
        }
    }

    private func startFocusJob(
        bundleID: String,
        timeout: TimeInterval,
        allowSingleFallback: Bool,
        allowOverrideOtherEditor: Bool,
        allowOverrideNonSearchEditor: Bool,
        isPostLaunch: Bool
    ) {
        guard bindingSession == nil, backend.isTrusted() else { return }
        pendingJob = PendingFocusJob(
            bundleIdentifier: bundleID,
            deadline: Date().addingTimeInterval(timeout),
            initialStamp: backend.inputStamp(),
            allowSingleFallback: allowSingleFallback,
            allowOverrideOtherEditor: allowOverrideOtherEditor,
            allowOverrideNonSearchEditor: allowOverrideNonSearchEditor,
            isPostLaunch: isPostLaunch,
            scanAttempts: 0
        )
        ensureTimerRunning()
        tickOnWorker()
    }

    private func ensureTimerRunning() {
        guard pollTimer == nil else { return }
        let timer = DispatchSource.makeTimerSource(queue: workerQueue)
        timer.schedule(deadline: .now() + 0.25, repeating: 0.25)
        timer.setEventHandler { [weak self] in
            self?.tickOnWorker()
        }
        timer.resume()
        pollTimer = timer
    }

    private func stopTimerOnWorker() {
        pollTimer?.cancel()
        pollTimer = nil
    }

    private func stopTimerIfIdle() {
        if pendingJob == nil && bindingSession == nil && postLaunchExpected == nil {
            stopTimerOnWorker()
        }
    }

    private func tickOnWorker() {
        if let expected = postLaunchExpected, Date() >= expected.expiresAt {
            postLaunchExpected = nil
        }

        if var session = bindingSession {
            let state = session.tick(backend: backend)
            bindingSession = session
            publishBindingState(state)
            stopTimerIfIdle()
            return
        }

        guard var job = pendingJob else {
            stopTimerIfIdle()
            return
        }

        if Date() >= job.deadline {
            pendingJob = nil
            stopTimerIfIdle()
            return
        }

        guard let front = backend.frontmostApp(),
              front.bundleIdentifier == job.bundleIdentifier
        else {
            pendingJob = nil
            stopTimerIfIdle()
            return
        }

        if backend.inputStamp() != job.initialStamp {
            print("[INPUT-FOCUS] aborted due to user input on \(job.bundleIdentifier)")
            pendingJob = nil
            stopTimerIfIdle()
            if InputFocusStore.shared.snapshot().apps[job.bundleIdentifier] != nil {
                publishAppState(.changed, for: job.bundleIdentifier)
            }
            return
        }

        let entry = InputFocusStore.shared.snapshot().apps[job.bundleIdentifier]
        let status = attemptFocus(
            app: front,
            entry: entry,
            initialStamp: job.initialStamp,
            allowSingleFallback: job.allowSingleFallback,
            allowOverrideOtherEditor: job.allowOverrideOtherEditor,
            allowOverrideNonSearchEditor: job.allowOverrideNonSearchEditor,
            updateStoreState: entry != nil
        )

        switch status {
        case .loading, .manualNeeded, .needsBinding, .rebind, .unavailable:
            job.scanAttempts += 1
            let maxAttempts = job.isPostLaunch ? 16 : 8
            if job.scanAttempts >= maxAttempts || status == .needsBinding {
                print("[INPUT-FOCUS] switch-focus gave up \(job.bundleIdentifier) -> \(status.rawValue)")
                pendingJob = nil
                stopTimerIfIdle()
            } else {
                pendingJob = job
            }
        case .restored, .already, .editing, .blocked, .changed, .confirming, .unconfirmed, .waiting:
            print("[INPUT-FOCUS] switch-focus \(job.bundleIdentifier) -> \(status.rawValue)")
            pendingJob = nil
            stopTimerIfIdle()
        }
    }

    private func attemptFocus(
        app: MacFocusAppInfo,
        entry: InputFocusAppEntry?,
        initialStamp: [UInt32],
        allowSingleFallback: Bool,
        allowOverrideOtherEditor: Bool,
        allowOverrideNonSearchEditor: Bool,
        updateStoreState: Bool
    ) -> InputFocusResultStatus {
        backend.prepareEnhancedUI(for: app) { false }
        guard let window = backend.focusedWindow(for: app) else {
            if updateStoreState { publishAppState(.loading, for: app.bundleIdentifier) }
            return .loading
        }
        if backend.isModal(window) {
            if updateStoreState { publishAppState(.blocked, for: app.bundleIdentifier) }
            return .blocked
        }

        var target: MacAXNode?
        if let cached = sessionCache[app.bundleIdentifier],
           cached.pid == app.pid,
           cached.window == window,
           InputFocusLocatorBuilder.isAttached(
                backend: backend,
                node: cached.element,
                window: window
           ) {
            target = cached.element
        }

        if target == nil, let locator = entry?.locator {
            target = InputFocusLocatorBuilder.resolve(
                backend: backend,
                window: window,
                locator: locator,
                build: app.buildHash
            )
            if target == nil {
                target = InputFocusDiscovery.discoverTarget(
                    backend: backend,
                    window: window,
                    allowSingleFallback: allowSingleFallback || allowOverrideOtherEditor
                )
            }
            if target == nil {
                if updateStoreState { publishAppState(.rebind, for: app.bundleIdentifier) }
                return .rebind
            }
        }

        if target == nil {
            if entry?.mode == .manual && !allowOverrideOtherEditor && entry?.locator == nil {
                if updateStoreState { publishAppState(.needsBinding, for: app.bundleIdentifier) }
                return .needsBinding
            }
            target = InputFocusDiscovery.discoverTarget(
                backend: backend,
                window: window,
                allowSingleFallback: allowSingleFallback
            )
        }

        guard let resolvedTarget = target else {
            let status: InputFocusResultStatus = (entry?.locator != nil) ? .rebind : .manualNeeded
            if updateStoreState { publishAppState(status, for: app.bundleIdentifier) }
            return status
        }

        sessionCache[app.bundleIdentifier] = (app.pid, window, resolvedTarget)

        let focused = backend.focusedElement(for: app)
        let status = InputFocusDiscovery.restore(
            backend: backend,
            window: window,
            target: resolvedTarget,
            focusedElement: focused,
            allowOverrideOtherEditor: allowOverrideOtherEditor,
            allowOverrideNonSearchEditor: allowOverrideNonSearchEditor,
            isSameWindowAndFrontmost: { [weak self] in
                guard let self,
                      let current = self.backend.frontmostApp(),
                      current.bundleIdentifier == app.bundleIdentifier,
                      self.backend.focusedWindow(for: current) == window
                else { return false }
                return true
            },
            didUserInteract: { [weak self] in
                self?.backend.inputStamp() != initialStamp
            },
            cancelled: { false }
        )

        if updateStoreState { publishAppState(status, for: app.bundleIdentifier) }
        return status
    }

    private func publishAppState(_ status: InputFocusResultStatus, for bundleID: String) {
        DispatchQueue.main.async {
            InputFocusStore.shared.updateState(status, for: bundleID)
        }
    }

    private func publishBindingState(_ state: InputFocusBindingState) {
        DispatchQueue.main.async {
            InputFocusStore.shared.bindingState = state
        }
    }

    private func publishFeedback(_ message: String) {
        DispatchQueue.main.async {
            InputFocusStore.shared.setFeedback(message)
        }
    }
}
