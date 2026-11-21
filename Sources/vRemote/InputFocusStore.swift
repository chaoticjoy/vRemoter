import AppKit
import Foundation

enum InputFocusMode: String, Codable, CaseIterable {
    case auto
    case manual
}

struct InputFocusAppEntry: Codable, Identifiable, Hashable {
    var id: String { bundleIdentifier }
    let bundleIdentifier: String
    var name: String
    var appPath: String?
    var enabled: Bool
    var mode: InputFocusMode
    var locator: InputFocusLocator?
}

struct RemoteAppLaunchConfig: Codable, Hashable {
    let bundleIdentifier: String
    let appPath: String
    let name: String
    var autoFocusInput: Bool
}

struct InputFocusBindingState: Equatable {
    var selectingBundleID: String?
    var selectingAppName: String?
    var candidateReady = false
    var captureStatus: CaptureStatus = .waiting
    var secondsLeft = 0

    var isSelecting: Bool { selectingBundleID != nil }

    enum CaptureStatus: Equatable {
        case waiting
        case windowUnavailable
        case focusUnavailable
        case notEditor
        case captured

        var message: String {
            switch self {
            case .waiting:
                L10n.text(
                    "请切到目标应用并点击输入框，然后返回点击「确认指定」。",
                    "Switch to the target app, click its input box, then return and click Confirm."
                )
            case .windowUnavailable:
                L10n.text(
                    "暂时读不到目标应用窗口，请保持窗口打开。",
                    "Target window is not readable yet; keep it open."
                )
            case .focusUnavailable:
                L10n.text(
                    "请点击目标应用的输入框并停留 1 秒。",
                    "Click inside the target input box and wait a moment."
                )
            case .notEditor:
                L10n.text(
                    "当前选中的控件不是文本输入框，请点击真正的输入区域。",
                    "The focused control is not a text input; click inside the input box."
                )
            case .captured:
                L10n.text(
                    "已捕获输入框！确认无误后点击「确认指定」。",
                    "Input box captured! Click Confirm Binding to save."
                )
            }
        }
    }
}

final class InputFocusStore: ObservableObject {
    static let shared = InputFocusStore()

    struct Snapshot {
        let autoFocusOnSwitchEnabled: Bool
        let focusBeforeVoiceEnabled: Bool
        let pullFromOtherEditorsOnSwitch: Bool
        let apps: [String: InputFocusAppEntry]
    }

    private let defaults = UserDefaults.standard
    private let autoFocusSwitchKey = "inputFocus.autoFocusOnSwitch"
    private let focusBeforeVoiceKey = "inputFocus.focusBeforeVoice"
    private let pullFromOtherEditorsKey = "inputFocus.pullFromOtherEditors"
    private let appsKey = "inputFocus.apps"
    private let snapshotLock = NSLock()

    @Published var autoFocusOnSwitchEnabled: Bool {
        didSet {
            defaults.set(autoFocusOnSwitchEnabled, forKey: autoFocusSwitchKey)
            syncSnapshot()
        }
    }

    @Published var focusBeforeVoiceEnabled: Bool {
        didSet {
            defaults.set(focusBeforeVoiceEnabled, forKey: focusBeforeVoiceKey)
            syncSnapshot()
        }
    }

    @Published var pullFromOtherEditorsOnSwitch: Bool {
        didSet {
            defaults.set(pullFromOtherEditorsOnSwitch, forKey: pullFromOtherEditorsKey)
            syncSnapshot()
        }
    }

    @Published private(set) var apps: [InputFocusAppEntry] = [] {
        didSet { syncSnapshot() }
    }
    @Published private(set) var appStates: [String: InputFocusResultStatus] = [:]
    @Published private(set) var lastFeedback = ""
    @Published var bindingState = InputFocusBindingState()

    private var cachedSnapshot = Snapshot(
        autoFocusOnSwitchEnabled: true,
        focusBeforeVoiceEnabled: true,
        pullFromOtherEditorsOnSwitch: true,
        apps: [:]
    )

    private init() {
        if defaults.object(forKey: autoFocusSwitchKey) == nil {
            defaults.set(true, forKey: autoFocusSwitchKey)
        }
        if defaults.object(forKey: focusBeforeVoiceKey) == nil {
            defaults.set(true, forKey: focusBeforeVoiceKey)
        }
        if defaults.object(forKey: pullFromOtherEditorsKey) == nil {
            defaults.set(true, forKey: pullFromOtherEditorsKey)
        }
        autoFocusOnSwitchEnabled = defaults.bool(forKey: autoFocusSwitchKey)
        focusBeforeVoiceEnabled = defaults.bool(forKey: focusBeforeVoiceKey)
        pullFromOtherEditorsOnSwitch = defaults.bool(forKey: pullFromOtherEditorsKey)
        loadApps()
        syncSnapshot()
    }

    func snapshot() -> Snapshot {
        snapshotLock.lock()
        defer { snapshotLock.unlock() }
        return cachedSnapshot
    }

    func entry(for bundleID: String) -> InputFocusAppEntry? {
        apps.first { $0.bundleIdentifier == bundleID }
    }

    func addOrEnableApp(
        bundleIdentifier: String,
        name: String,
        appPath: String? = nil
    ) {
        if let idx = apps.firstIndex(where: { $0.bundleIdentifier == bundleIdentifier }) {
            apps[idx].enabled = true
            apps[idx].name = name
            if let appPath { apps[idx].appPath = appPath }
        } else {
            apps.append(InputFocusAppEntry(
                bundleIdentifier: bundleIdentifier,
                name: name,
                appPath: appPath,
                enabled: true,
                mode: .auto,
                locator: nil
            ))
            apps.sort { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        }
        saveApps()
    }

    func removeApp(bundleIdentifier: String) {
        apps.removeAll { $0.bundleIdentifier == bundleIdentifier }
        appStates.removeValue(forKey: bundleIdentifier)
        saveApps()
    }

    func setAppEnabled(_ enabled: Bool, bundleIdentifier: String) {
        guard let idx = apps.firstIndex(where: { $0.bundleIdentifier == bundleIdentifier }) else {
            return
        }
        apps[idx].enabled = enabled
        saveApps()
    }

    func saveLocator(_ locator: InputFocusLocator?, for bundleIdentifier: String) {
        guard let idx = apps.firstIndex(where: { $0.bundleIdentifier == bundleIdentifier }) else {
            return
        }
        apps[idx].mode = .manual
        apps[idx].locator = locator
        appStates[bundleIdentifier] = .waiting
        saveApps()
    }

    func resetToAuto(for bundleIdentifier: String) {
        guard let idx = apps.firstIndex(where: { $0.bundleIdentifier == bundleIdentifier }) else {
            return
        }
        apps[idx].mode = .auto
        apps[idx].locator = nil
        appStates[bundleIdentifier] = .waiting
        saveApps()
    }

    func updateState(_ status: InputFocusResultStatus, for bundleIdentifier: String) {
        appStates[bundleIdentifier] = status
    }

    func setFeedback(_ message: String) {
        lastFeedback = message
    }

    private func syncSnapshot() {
        let map = Dictionary(uniqueKeysWithValues: apps.map { ($0.bundleIdentifier, $0) })
        let snap = Snapshot(
            autoFocusOnSwitchEnabled: autoFocusOnSwitchEnabled,
            focusBeforeVoiceEnabled: focusBeforeVoiceEnabled,
            pullFromOtherEditorsOnSwitch: pullFromOtherEditorsOnSwitch,
            apps: map
        )
        snapshotLock.lock()
        cachedSnapshot = snap
        snapshotLock.unlock()
    }

    private func loadApps() {
        guard let data = defaults.data(forKey: appsKey),
              let decoded = try? JSONDecoder().decode([InputFocusAppEntry].self, from: data)
        else { return }
        apps = decoded
    }

    private func saveApps() {
        guard let data = try? JSONEncoder().encode(apps) else { return }
        defaults.set(data, forKey: appsKey)
    }
}

enum AppLaunchService {
    static func launchOrActivate(config: RemoteAppLaunchConfig) {
        let bundleID = config.bundleIdentifier
        if config.autoFocusInput {
            InputFocusCoordinator.shared.requestPostLaunchFocus(bundleIdentifier: bundleID)
        }

        if let running = NSRunningApplication.runningApplications(
            withBundleIdentifier: bundleID
        ).first {
            let activated = running.activate(options: [.activateIgnoringOtherApps])
            print("[APP-LAUNCH] activate \(bundleID) ok=\(activated)")
            if !activated || running.isHidden {
                openURL(URL(fileURLWithPath: config.appPath))
            }
            return
        }
        openURL(URL(fileURLWithPath: config.appPath))
    }

    private static func openURL(_ url: URL) {
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        NSWorkspace.shared.openApplication(at: url, configuration: configuration) { app, error in
            if let error {
                print("[APP-LAUNCH] open failed: \(error.localizedDescription)")
            } else {
                print("[APP-LAUNCH] opened \(app?.bundleIdentifier ?? url.lastPathComponent)")
            }
        }
    }
}
