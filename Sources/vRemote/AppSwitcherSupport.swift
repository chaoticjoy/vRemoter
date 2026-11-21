import CoreGraphics
import Foundation

/// 切换应用会话：⌘ 虚拟按住 + Tab 发射 + 1s 倒计时自动收尾。
/// 不依赖长按松手边沿，X6 / Chromecast 共用同一交互。
///
/// ⚠️ 收尾定时用 GCD 主队列（DispatchWorkItem），不用 Timer.scheduledTimer——
/// HID 回调在后台线程，后台 RunLoop 不跑 timer mode，定时器会永远不触发。
final class AppSwitcherSession {
    static let shared = AppSwitcherSession()

    /// 倒计时时长：会话内任何一次会话键按下都会重置。
    static let idleInterval: TimeInterval = 1.0

    private(set) var isActive = false
    private var pending: DispatchWorkItem?

    private var source: CGEventSource? { CGEventSource(stateID: .hidSystemState) }

    /// 切换应用键按下：首次=开启会话（⌘↓ + Tab），会话中=再发一次 Tab（等同连按 Tab）。
    func tapSwitcher() {
        if isActive {
            tapTab(shift: false)
        } else {
            isActive = true
            postKey(55, flags: [.maskCommand], down: true)
            tapTab(shift: false)
        }
        schedule()
    }

    /// 方向键重定向入口。
    func redirectArrow(isRight: Bool) {
        guard isActive else { return }
        tapTab(shift: !isRight)
        schedule()
    }

    /// 会话收尾（⌘↑）或外部取消。幂等。
    func cancel() {
        pending?.cancel()
        pending = nil
        guard isActive else { return }
        isActive = false
        // ⌘↑ 事件的 flags 必须为空（表示释放后无任何修饰键按下）；
        // 若仍带 .maskCommand，系统会认为 ⌘ 还按着，切换器永远不会关闭。
        postKey(55, flags: [], down: false)
    }

    private func tapTab(shift: Bool) {
        var flags: CGEventFlags = [.maskCommand]
        if shift { flags.insert(.maskShift) }
        postKey(48, flags: flags, down: true)
        postKey(48, flags: flags, down: false)
    }

    private func schedule() {
        pending?.cancel()
        let item = DispatchWorkItem { [weak self] in
            self?.cancel()
        }
        pending = item
        DispatchQueue.main.asyncAfter(
            deadline: .now() + Self.idleInterval,
            execute: item
        )
    }

    private func postKey(_ keyCode: CGKeyCode, flags: CGEventFlags, down: Bool) {
        guard let event = CGEvent(
            keyboardEventSource: source,
            virtualKey: keyCode,
            keyDown: down
        ) else { return }
        event.flags = flags
        event.setIntegerValueField(.eventSourceUserData, value: Key.syntheticMarker)
        event.post(tap: .cghidEventTap)
    }
}

enum AppSwitcherSupport {
    /// 睡眠：无可靠系统组合键（pmset/IOKit 均需 root），走 System Events。
    /// 首次触发时 macOS 会弹一次 TCC 授权（vRemote → System Events）。
    static func requestSystemSleep() {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        task.arguments = ["-e", "tell application \"System Events\" to sleep"]
        try? task.run()
    }
}
