import AppKit
import SwiftUI

struct AppLaunchPickerView: View {
    let buttonTitle: String
    let existingConfig: RemoteAppLaunchConfig?
    let onCancel: () -> Void
    let onSave: (RemoteAppLaunchConfig) -> Void

    @State private var selectedApp: MacFocusAppInfo?
    @State private var autoFocusInput: Bool
    @State private var addToSwitchWhitelist: Bool
    @State private var runningApps: [MacFocusAppInfo] = []
    @State private var errorMessage = ""

    init(
        buttonTitle: String,
        existingConfig: RemoteAppLaunchConfig?,
        onCancel: @escaping () -> Void,
        onSave: @escaping (RemoteAppLaunchConfig) -> Void
    ) {
        self.buttonTitle = buttonTitle
        self.existingConfig = existingConfig
        self.onCancel = onCancel
        self.onSave = onSave
        if let existingConfig {
            _selectedApp = State(initialValue: MacFocusAppInfo(
                bundleIdentifier: existingConfig.bundleIdentifier,
                pid: 0,
                name: existingConfig.name,
                appPath: existingConfig.appPath,
                buildHash: nil
            ))
            _autoFocusInput = State(initialValue: existingConfig.autoFocusInput)
        } else {
            _selectedApp = State(initialValue: nil)
            _autoFocusInput = State(initialValue: true)
        }
        _addToSwitchWhitelist = State(initialValue: true)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text(L10n.text("打开指定应用", "Open Specified App"))
                    .font(.system(size: 19, weight: .semibold))
                Text(L10n.text(
                    "为“\(buttonTitle)”选择要打开或切到前台的应用。",
                    "Choose the app to launch or bring forward for “\(buttonTitle)”."
                ))
                .font(.system(size: 12.5))
                .foregroundStyle(ConsoleTheme.secondary)
            }

            HStack(spacing: 10) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(selectedApp?.name ?? L10n.text("尚未选择应用", "No app selected"))
                        .font(.system(size: 13.5, weight: .semibold))
                        .foregroundStyle(selectedApp == nil ? ConsoleTheme.secondary : ConsoleTheme.text)
                    Text(selectedApp?.bundleIdentifier ?? L10n.text("支持任意 .app 桌面应用", "Supports any .app bundle"))
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundStyle(ConsoleTheme.tertiary)
                }
                Spacer()
                Button(L10n.text("选择本地 .app…", "Browse .app…")) {
                    pickLocalApp()
                }
                .buttonStyle(ConsoleButtonStyle(tone: .neutral, compact: true))
            }
            .padding(12)
            .background(panelBackground(ConsoleTheme.surface2, radius: 12))

            if !runningApps.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    Text(L10n.text("或从当前运行中的应用快速选择：", "Or pick a running app:"))
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(ConsoleTheme.secondary)
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 6) {
                            ForEach(runningApps) { app in
                                Button(app.name) {
                                    selectedApp = app
                                    errorMessage = ""
                                }
                                .buttonStyle(ConsoleButtonStyle(
                                    tone: selectedApp?.bundleIdentifier == app.bundleIdentifier ? ConsoleButtonTone.good : ConsoleButtonTone.neutral,
                                    compact: true
                                ))
                            }
                        }
                    }
                }
            }

            VStack(alignment: .leading, spacing: 8) {
                Toggle(
                    L10n.text("打开后自动将输入焦点切入输入框", "Auto-focus input box after opening"),
                    isOn: $autoFocusInput
                )
                .font(.system(size: 12.5))
                Toggle(
                    L10n.text("同时加入「切换应用自动聚焦」名单", "Also add to Switch-App Auto-Focus list"),
                    isOn: $addToSwitchWhitelist
                )
                .font(.system(size: 12.5))
            }

            if !errorMessage.isEmpty {
                Text(errorMessage)
                    .font(.system(size: 11.5))
                    .foregroundStyle(ConsoleTheme.red)
            }

            HStack {
                Button(L10n.text("取消", "Cancel"), action: onCancel)
                    .buttonStyle(ConsoleButtonStyle(tone: .neutral))
                Spacer()
                Button(L10n.text("保存映射", "Save Mapping")) {
                    saveSelection()
                }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(ConsoleButtonStyle(tone: ConsoleButtonTone.good))
                .disabled(selectedApp?.appPath == nil)
            }
        }
        .padding(22)
        .frame(width: 470)
        .background(ConsoleTheme.panel)
        .foregroundStyle(ConsoleTheme.text)
        .onAppear {
            runningApps = MacAXFocusBackend().runningAppsForSelection()
        }
    }

    private func pickLocalApp() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.applicationBundle]
        panel.directoryURL = URL(fileURLWithPath: "/Applications")
        guard panel.runModal() == .OK, let url = panel.url else { return }
        if let info = MacAXFocusBackend.appInfo(fromAppBundleURL: url) {
            selectedApp = info
            errorMessage = ""
        } else {
            errorMessage = L10n.text("无法读取该应用的 Bundle 信息", "Could not read app bundle metadata")
        }
    }

    private func saveSelection() {
        guard let selectedApp, let path = selectedApp.appPath else { return }
        if addToSwitchWhitelist {
            InputFocusStore.shared.addOrEnableApp(
                bundleIdentifier: selectedApp.bundleIdentifier,
                name: selectedApp.name,
                appPath: path
            )
        }
        onSave(RemoteAppLaunchConfig(
            bundleIdentifier: selectedApp.bundleIdentifier,
            appPath: path,
            name: selectedApp.name,
            autoFocusInput: autoFocusInput
        ))
    }
}
