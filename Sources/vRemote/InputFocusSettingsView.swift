import AppKit
import SwiftUI

struct InputFocusSettingsView: View {
    @ObservedObject private var store = InputFocusStore.shared
    @State private var runningApps: [MacFocusAppInfo] = []
    @State private var showRunningPicker = false
    let accessibilityGranted: Bool
    let onOpenAccessibilityGuide: () -> Void

    var body: some View {
        VStack(spacing: 12) {
            headerCard
            appsCard
        }
        .onAppear {
            refreshRunningApps()
        }
    }

    private var headerCard: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack(spacing: 10) {
                Image(systemName: "scope")
                    .foregroundStyle(ConsoleTheme.green)
                Text(L10n.text("应用切换与输入框自动聚焦", "App Switch & Input Auto-Focus"))
                    .font(.system(size: 16, weight: .semibold))
                    .foregroundStyle(ConsoleTheme.text)
                Spacer()
                if !accessibilityGranted {
                    Button(L10n.text("需要辅助功能权限", "Accessibility Needed")) {
                        onOpenAccessibilityGuide()
                    }
                    .buttonStyle(ConsoleButtonStyle(tone: ConsoleButtonTone.error, compact: true))
                } else {
                    Toggle(
                        L10n.text("切回应用时自动聚焦", "Auto-focus on app switch"),
                        isOn: $store.autoFocusOnSwitchEnabled
                    )
                    .toggleStyle(.switch)
                    .controlSize(.small)
                }
            }

            Text(L10n.text(
                "切到下方勾选的应用（含遥控器「切换应用」或「打开指定应用」）时自动将光标拉回输入框；也可在「按键映射」里把任意按键设为「聚焦当前输入框」，随时一键聚焦前台应用。",
                "Automatically moves focus into the input box when switching to enabled apps (via App Switcher, Open App, or normal switching). You can also map any button to Focus Input Box."
            ))
            .font(.system(size: 11.5))
            .foregroundStyle(ConsoleTheme.secondary)
            .fixedSize(horizontal: false, vertical: true)

            HStack(spacing: 12) {
                Toggle(
                    L10n.text("从代码区等位置切回时也拉回输入框", "Pull from code editor on switch (protects Search)"),
                    isOn: $store.pullFromOtherEditorsOnSwitch
                )
                .font(.system(size: 11))
                .foregroundStyle(ConsoleTheme.text)

                Toggle(
                    L10n.text("按语音键前检查聚焦", "Focus before voice"),
                    isOn: $store.focusBeforeVoiceEnabled
                )
                .font(.system(size: 11))
                .foregroundStyle(ConsoleTheme.text)

                Spacer()

                if !store.lastFeedback.isEmpty {
                    Text(store.lastFeedback)
                        .font(.system(size: 10.5))
                        .foregroundStyle(ConsoleTheme.green)
                        .lineLimit(1)
                }
            }
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 14)
        .background(panelBackground(ConsoleTheme.surface, radius: 17))
    }

    private var appsCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Text(L10n.text("已配置应用（\(store.apps.count)）", "Configured Apps (\(store.apps.count))"))
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(ConsoleTheme.text)

                Spacer()

                if let ide = antigravityQuickCandidate {
                    Button(L10n.text("+ 添加 Antigravity IDE", "+ Add Antigravity IDE")) {
                        store.addOrEnableApp(
                            bundleIdentifier: ide.bundleIdentifier,
                            name: ide.name,
                            appPath: ide.appPath
                        )
                    }
                    .buttonStyle(ConsoleButtonStyle(tone: ConsoleButtonTone.good, compact: true))
                }

                Button(L10n.text("从运行中添加…", "Add Running…")) {
                    refreshRunningApps()
                    showRunningPicker = true
                }
                .buttonStyle(ConsoleButtonStyle(tone: .neutral, compact: true))
                .popover(isPresented: $showRunningPicker, arrowEdge: .bottom) {
                    runningAppsPopover
                }

                Button(L10n.text("浏览本地 .app…", "Browse .app…")) {
                    pickLocalApp()
                }
                .buttonStyle(ConsoleButtonStyle(tone: .neutral, compact: true))
            }

            if store.bindingState.isSelecting {
                bindingBanner
            }

            Divider().overlay(ConsoleTheme.lineSoft)

            if store.apps.isEmpty {
                emptyState
            } else {
                ScrollView {
                    LazyVStack(spacing: 7) {
                        ForEach(store.apps) { entry in
                            appRow(entry)
                        }
                    }
                }
            }
        }
        .padding(16)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(panelBackground(ConsoleTheme.surface, radius: 17))
    }

    private var bindingBanner: some View {
        let state = store.bindingState
        return HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 3) {
                Text(L10n.text(
                    "正在为「\(state.selectingAppName ?? "")」指定输入框（剩余 \(state.secondsLeft) 秒）",
                    "Binding input for “\(state.selectingAppName ?? "")” (\(state.secondsLeft)s left)"
                ))
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(state.candidateReady ? ConsoleTheme.green : ConsoleTheme.amber)

                Text(state.captureStatus.message)
                    .font(.system(size: 11))
                    .foregroundStyle(ConsoleTheme.text)
            }
            Spacer()
            Button(L10n.text("取消", "Cancel")) {
                InputFocusCoordinator.shared.cancelBinding()
            }
            .buttonStyle(ConsoleButtonStyle(tone: .neutral, compact: true))

            Button(L10n.text("确认指定", "Confirm")) {
                InputFocusCoordinator.shared.confirmBinding()
            }
            .buttonStyle(ConsoleButtonStyle(tone: ConsoleButtonTone.good, compact: true))
            .disabled(!state.candidateReady)
        }
        .padding(10)
        .background(state.candidateReady ? Color(red: 0.06, green: 0.20, blue: 0.13) : ConsoleTheme.amberDeep)
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .stroke(state.candidateReady ? ConsoleTheme.green : ConsoleTheme.amber, lineWidth: 1)
        )
    }

    private func appRow(_ entry: InputFocusAppEntry) -> some View {
        let status = store.appStates[entry.bundleIdentifier]
        let hasLocator = entry.locator != nil
        let statusText: String = {
            if let status { return status.shortDescription }
            if entry.mode == .manual {
                return hasLocator
                    ? L10n.text("已指定输入框（结构指纹已保存）", "Input bound (locator saved)")
                    : L10n.text("尚未指定输入框，请点右侧「指定输入框」", "Not bound yet; click Bind Input")
            }
            return L10n.text("自动识别模式：切到该应用时自动查找输入框", "Auto mode: discovers input on switch")
        }()

        return HStack(spacing: 10) {
            Toggle(
                "",
                isOn: Binding(
                    get: { entry.enabled },
                    set: { store.setAppEnabled($0, bundleIdentifier: entry.bundleIdentifier) }
                )
            )
            .toggleStyle(.switch)
            .controlSize(.small)
            .labelsHidden()

            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(entry.name)
                        .font(.system(size: 12.5, weight: .semibold))
                        .foregroundStyle(entry.enabled ? ConsoleTheme.text : ConsoleTheme.tertiary)
                    Text(entry.bundleIdentifier)
                        .font(.system(size: 10, design: .monospaced))
                        .foregroundStyle(ConsoleTheme.tertiary)
                        .lineLimit(1)
                }
                Text(statusText)
                    .font(.system(size: 10.5))
                    .foregroundStyle(
                        (status?.needsUserAction == true) ? ConsoleTheme.amber : ConsoleTheme.secondary
                    )
                    .lineLimit(1)
            }

            Spacer()

            if entry.mode == .manual {
                Button(L10n.text("切回自动", "Use Auto")) {
                    store.resetToAuto(for: entry.bundleIdentifier)
                }
                .buttonStyle(ConsoleButtonStyle(tone: .neutral, compact: true))
            }

            Button(hasLocator
                ? L10n.text("重新指定", "Rebind")
                : L10n.text("指定输入框", "Bind Input")
            ) {
                InputFocusCoordinator.shared.beginBinding(
                    bundleIdentifier: entry.bundleIdentifier,
                    appName: entry.name
                )
            }
            .buttonStyle(ConsoleButtonStyle(tone: hasLocator ? ConsoleButtonTone.neutral : ConsoleButtonTone.good, compact: true))
            .disabled(!accessibilityGranted || store.bindingState.isSelecting)

            Button {
                store.removeApp(bundleIdentifier: entry.bundleIdentifier)
            } label: {
                Image(systemName: "trash")
                    .font(.system(size: 11.5))
                    .foregroundStyle(ConsoleTheme.secondary)
            }
            .buttonStyle(.plain)
            .help(L10n.text("移出列表", "Remove"))
        }
        .padding(.horizontal, 11)
        .padding(.vertical, 8)
        .background(ConsoleTheme.surface2)
        .clipShape(RoundedRectangle(cornerRadius: 10))
    }

    private var emptyState: some View {
        VStack(spacing: 8) {
            Spacer()
            Image(systemName: "text.cursor")
                .font(.system(size: 26))
                .foregroundStyle(ConsoleTheme.tertiary)
            Text(L10n.text(
                "尚未添加自动聚焦的应用",
                "No apps configured for auto-focus yet"
            ))
            .font(.system(size: 13.5, weight: .semibold))
            .foregroundStyle(ConsoleTheme.secondary)
            Text(L10n.text(
                "点击上方「添加 Antigravity IDE」或「从运行中添加…」。添加后自动识别输入框；若应用有多个输入框，可点「指定输入框」精确绑定。",
                "Click Add Antigravity IDE or Add Running… above. Auto-discovery runs on switch, or bind an exact input box if needed."
            ))
            .font(.system(size: 11.5))
            .foregroundStyle(ConsoleTheme.tertiary)
            .multilineTextAlignment(.center)
            .frame(maxWidth: 460)
            Spacer()
        }
        .frame(maxWidth: .infinity)
    }

    private var runningAppsPopover: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(L10n.text("选择运行中的应用", "Select Running App"))
                .font(.system(size: 12.5, weight: .semibold))
                .foregroundStyle(ConsoleTheme.text)
            ScrollView {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(runningApps) { app in
                        Button {
                            store.addOrEnableApp(
                                bundleIdentifier: app.bundleIdentifier,
                                name: app.name,
                                appPath: app.appPath
                            )
                            showRunningPicker = false
                        } label: {
                            HStack {
                                Text(app.name)
                                    .font(.system(size: 12, weight: .medium))
                                Spacer()
                                if store.entry(for: app.bundleIdentifier) != nil {
                                    Image(systemName: "checkmark")
                                        .foregroundStyle(ConsoleTheme.green)
                                }
                            }
                            .padding(.horizontal, 8)
                            .padding(.vertical, 5)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
            .frame(width: 240, height: 220)
        }
        .padding(12)
        .background(ConsoleTheme.surface)
    }

    private var antigravityQuickCandidate: MacFocusAppInfo? {
        let bundleID = "com.google.antigravity-ide"
        guard store.entry(for: bundleID) == nil else { return nil }
        if let running = runningApps.first(where: { $0.bundleIdentifier == bundleID }) {
            return running
        }
        let url = URL(fileURLWithPath: "/Applications/Antigravity IDE.app")
        if FileManager.default.fileExists(atPath: url.path) {
            return MacAXFocusBackend.appInfo(fromAppBundleURL: url)
        }
        return nil
    }

    private func refreshRunningApps() {
        runningApps = MacAXFocusBackend().runningAppsForSelection()
    }

    private func pickLocalApp() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true
        panel.allowedContentTypes = [.applicationBundle]
        panel.directoryURL = URL(fileURLWithPath: "/Applications")
        guard panel.runModal() == .OK else { return }
        for url in panel.urls {
            if let info = MacAXFocusBackend.appInfo(fromAppBundleURL: url) {
                store.addOrEnableApp(
                    bundleIdentifier: info.bundleIdentifier,
                    name: info.name,
                    appPath: info.appPath
                )
            }
        }
    }
}
