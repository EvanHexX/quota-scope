#if os(macOS)
import AppKit
import ServiceManagement
import SwiftUI
import QuotaCore
import QuotaProviders

// Port of app-winui/Windows/SettingsWindow.cs (docs/MACOS_BUILD.md 3.3):
// toolbar tabs, grouped forms, and immediate apply. Every control writes
// settings.json through SettingsStore.update and tells the controller what
// changed; there is no OK/Cancel. Only settings the Mac app acts on are shown:
// row shapes, columns, notifications, and hotkeys come with their features.

enum SettingsChange {
    case general
    case providers
    case appearance
}

@MainActor
final class SettingsModel: ObservableObject {
    private let store: SettingsStore
    @Published private(set) var settings: AppSettings
    @Published private(set) var menuBarText: String
    @Published var loc = Loc.english
    // Every row the providers report, for the row list and the menu bar text.
    @Published var rows: [UsageRowRef] = []
    @Published var codexResolved = ""
    @Published var claudeCredentialsFound = false
    @Published var autostartEnabled = false
    @Published var autostartMessage: String?

    var onChange: (SettingsChange) -> Void = { _ in }
    var onReconnect: () -> Void = {}

    init(store: SettingsStore) {
        self.store = store
        settings = store.settings
        menuBarText = store.menuBarText
    }

    var loadWarning: String? { store.loadWarning }

    // Re-read before the window shows: the popover can change the pin and the
    // time mode while it is closed.
    func reload() {
        settings = store.settings
        menuBarText = store.menuBarText
        // Off the main thread: a Keychain lookup waits while an access prompt
        // is up.
        Task.detached {
            let found = PreferredCredentialSource.standard().exists
            await MainActor.run { self.claudeCredentialsFound = found }
        }
        refreshAutostart()
        resolveCodex()
    }

    func update(_ kind: SettingsChange, _ change: (inout AppSettings) -> Void) {
        let before = store.settings
        store.update(change)
        settings = store.settings
        guard settings != before else { return }
        onChange(kind)
        if kind == .providers { resolveCodex() }
    }

    func binding<Value>(_ kind: SettingsChange, _ path: WritableKeyPath<AppSettings, Value>) -> Binding<Value> {
        Binding(get: { self.settings[keyPath: path] },
                set: { value in self.update(kind) { $0[keyPath: path] = value } })
    }

    func providerBinding<Value>(_ id: String, _ path: WritableKeyPath<ProviderSettings, Value>,
                                clamp: @escaping (Value) -> Value = { $0 }) -> Binding<Value> {
        Binding(get: { self.settings.provider(id)[keyPath: path] },
                set: { value in
                    self.update(.providers) { settings in
                        var provider = settings.provider(id)
                        provider[keyPath: path] = clamp(value)
                        settings.providers[id] = provider
                    }
                })
    }

    var menuBarTextBinding: Binding<String> {
        Binding(get: { self.menuBarText },
                set: { value in
                    self.store.menuBarText = value
                    self.menuBarText = value
                    self.onChange(.general)
                })
    }

    // System / Light / Dark (decision M4), stored in the Windows keys.
    var themeBinding: Binding<String> {
        Binding(get: {
            if self.settings.followSystemTheme { return "System" }
            return self.settings.themeOverride.caseInsensitiveCompare("Light") == .orderedSame ? "Light" : "Dark"
        }, set: { value in
            self.update(.general) {
                $0.followSystemTheme = value == "System"
                if value != "System" { $0.themeOverride = value }
            }
        })
    }

    // Start at login (SMAppService, macOS 13). The system's own state wins
    // over the stored key: the user can turn it off in System Settings.
    var autostartBinding: Binding<Bool> {
        Binding(get: { self.autostartEnabled }, set: { self.setAutostart($0) })
    }

    private func refreshAutostart() {
        let status = SMAppService.mainApp.status
        autostartEnabled = status == .enabled || status == .requiresApproval
        autostartMessage = status == .requiresApproval
            ? loc.t("Allow QuotaScope in System Settings > General > Login Items.",
                    "시스템 설정 > 일반 > 로그인 항목에서 QuotaScope를 허용하세요.")
            : nil
    }

    private func setAutostart(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            update(.general) { $0.autostart = on }
            refreshAutostart()
        } catch {
            refreshAutostart()
            autostartMessage = loc.t("Could not change the login item: ", "로그인 항목을 바꾸지 못했습니다: ")
                + error.localizedDescription
        }
    }

    private func resolveCodex() {
        let command = settings.provider("codex").command
        Task.detached {
            let text = CodexCommandResolver.resolve(command)?.displayText
            await MainActor.run {
                self.codexResolved = text ?? "-"
            }
        }
    }

    func openSettingsFile() {
        store.ensureFileExists()
        NSWorkspace.shared.open(SettingsStore.file)
    }
}

// Option values stay the stable English keys of settings.json; only the text
// is localized (Loc.Option).
private func option(_ value: String, _ loc: Loc) -> String {
    switch value {
    case "ClockTime": return loc.t("Clock time", "예정 시각")
    case "RemainingTime": return loc.t("Remaining time", "남은 시간")
    case "Used": return loc.t("Used", "사용량")
    case "Remaining": return loc.t("Remaining", "잔여량")
    case "System": return loc.t("System", "시스템")
    case "Light": return loc.t("Light", "라이트")
    case "Dark": return loc.t("Dark", "다크")
    case MenuBarText.off: return loc.t("Off", "끔")
    case MenuBarText.highestWeekly: return loc.t("Highest weekly", "가장 높은 주간")
    case MenuBarText.highestOverall: return loc.t("Highest overall", "가장 높은 전체")
    default: return value
    }
}

// MARK: window

@MainActor
final class SettingsWindowController: NSObject, NSWindowDelegate {
    private let model: SettingsModel
    private var window: NSWindow?
    private var tabs: NSTabViewController?

    private static let pages: [(tag: String, symbol: String)] = [
        ("General", "gearshape"),
        ("Providers", "server.rack"),
        ("Appearance", "paintbrush"),
        ("Alerts", "exclamationmark.triangle"),
        ("About", "info.circle"),
    ]

    init(model: SettingsModel) {
        self.model = model
    }

    func show() {
        model.reload()
        if window == nil { build() }
        relocalize()
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    // Language changes apply live: the tab labels follow the model.
    func relocalize() {
        guard let tabs else { return }
        for (item, page) in zip(tabs.tabViewItems, Self.pages) {
            item.label = Self.title(page.tag, model.loc)
        }
        if let selected = tabs.tabViewItems[safe: tabs.selectedTabViewItemIndex] {
            window?.title = selected.label
        }
    }

    private func build() {
        let tabs = NSTabViewController()
        tabs.tabStyle = .toolbar
        for page in Self.pages {
            let hosting = NSHostingController(rootView: SettingsPage(tag: page.tag).environmentObject(model))
            hosting.sizingOptions = [.preferredContentSize]
            let item = NSTabViewItem(viewController: hosting)
            item.label = Self.title(page.tag, model.loc)
            item.image = NSImage(systemSymbolName: page.symbol, accessibilityDescription: nil)
            tabs.addTabViewItem(item)
        }
        let window = NSWindow(contentViewController: tabs)
        window.styleMask = [.titled, .closable, .miniaturizable]
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.center()
        self.tabs = tabs
        self.window = window
    }

    private static func title(_ tag: String, _ loc: Loc) -> String {
        switch tag {
        case "General": return loc.t("General", "일반")
        case "Providers": return loc.t("Providers", "프로바이더")
        case "Appearance": return loc.t("Appearance", "모양")
        case "Alerts": return loc.t("Alerts", "경고")
        default: return loc.t("About", "정보")
        }
    }

    // Commits a number field that still has focus.
    nonisolated func windowWillClose(_ notification: Notification) {
        Task { @MainActor in self.window?.makeFirstResponder(nil) }
    }
}

private extension Array {
    subscript(safe index: Int) -> Element? { indices.contains(index) ? self[index] : nil }
}

// MARK: pages

private struct SettingsPage: View {
    let tag: String
    @EnvironmentObject var model: SettingsModel

    var body: some View {
        Group {
            switch tag {
            case "Providers": ProvidersPage()
            case "Appearance": AppearancePage()
            case "Alerts": AlertsPage()
            case "About": AboutPage()
            default: GeneralPage()
            }
        }
        .formStyle(.grouped)
        .frame(width: 560, height: 500)
    }
}

// Title with an optional muted description, the Windows Row().
private struct RowLabel: View {
    let title: String
    var detail: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
            if let detail {
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

// A number field with a stepper; out-of-range values are clamped on commit.
private struct NumberRow: View {
    let title: String
    var detail: String?
    @Binding var value: Double
    let range: ClosedRange<Double>
    let step: Double

    var body: some View {
        LabeledContent {
            HStack(spacing: 4) {
                TextField("", value: clamped, format: .number)
                    .labelsHidden()
                    .multilineTextAlignment(.trailing)
                    .frame(width: 80)
                Stepper("", value: clamped, in: range, step: step)
                    .labelsHidden()
            }
        } label: {
            RowLabel(title: title, detail: detail)
        }
    }

    private var clamped: Binding<Double> {
        Binding(get: { value }, set: { value = min(max($0, range.lowerBound), range.upperBound) })
    }
}

private func intBinding(_ binding: Binding<Int>) -> Binding<Double> {
    Binding(get: { Double(binding.wrappedValue) }, set: { binding.wrappedValue = Int($0.rounded()) })
}

private struct GeneralPage: View {
    @EnvironmentObject var model: SettingsModel

    var body: some View {
        let loc = model.loc
        Form {
            if let warning = model.loadWarning {
                Section {
                    Label(loc.t("settings.json has an error, so changes are not saved: ",
                                "settings.json에 오류가 있어 변경 내용을 저장하지 않습니다: ") + warning,
                          systemImage: "exclamationmark.triangle")
                        .foregroundStyle(.orange)
                }
            }
            Section {
                Picker(selection: model.binding(.general, \.language)) {
                    Text(option("System", loc)).tag("System")
                    Text("English").tag("English")
                    Text("한국어").tag("한국어")
                } label: {
                    RowLabel(title: loc.t("Language", "언어"),
                             detail: loc.t("Menus, popup, and settings text.", "메뉴, 팝업, 설정 텍스트에 적용됩니다."))
                }
                Toggle(isOn: model.autostartBinding) {
                    RowLabel(title: loc.t("Start at login", "로그인 시 자동 실행"),
                             detail: model.autostartMessage
                                ?? loc.t("Adds QuotaScope to System Settings > General > Login Items.",
                                         "시스템 설정 > 일반 > 로그인 항목에 QuotaScope를 추가합니다."))
                }
                Picker(selection: model.binding(.general, \.timeDisplayMode)) {
                    Text(option("ClockTime", loc)).tag("ClockTime")
                    Text(option("RemainingTime", loc)).tag("RemainingTime")
                } label: {
                    RowLabel(title: loc.t("Time display", "시간 표시"),
                             detail: loc.t("Reset times as clock time or remaining time.",
                                           "리셋 시각을 시계 시간 또는 남은 시간으로 표시합니다."))
                }
                Picker(selection: model.menuBarTextBinding) {
                    Text(option(MenuBarText.off, loc)).tag(MenuBarText.off)
                    Text(option(MenuBarText.highestWeekly, loc)).tag(MenuBarText.highestWeekly)
                    Text(option(MenuBarText.highestOverall, loc)).tag(MenuBarText.highestOverall)
                    ForEach(menuBarRows) { row in
                        Text(row.name).tag(row.key)
                    }
                } label: {
                    RowLabel(title: loc.t("Menu bar text", "메뉴 막대 텍스트"),
                             detail: loc.t("The percentage next to the menu bar icon. It follows the gauge metric.",
                                           "메뉴 막대 아이콘 옆에 표시할 퍼센트입니다. 게이지 지표를 따릅니다."))
                }
            }
            Section {
                LabeledContent {
                    Button(loc.t("Open", "열기"), action: model.openSettingsFile)
                } label: {
                    RowLabel(title: loc.t("Settings file", "설정 파일"),
                             detail: SettingsStore.file.path.replacingOccurrences(
                                of: FileManager.default.homeDirectoryForCurrentUser.path, with: "~"))
                }
            }
        }
    }

    // Rows with a window, plus the stored choice when its row is gone, so the
    // picker never shows an empty selection.
    private struct Choice: Identifiable {
        let key: String
        let name: String
        var id: String { key }
    }

    private var menuBarRows: [Choice] {
        var result = model.rows
            .filter { !RowShapes.isCreditsRow($0.label) }
            .map { Choice(key: RowShapes.key($0.providerId, $0.label),
                          name: "\($0.providerName) · \(model.loc.rowLabel($0.providerId, $0.label, scope: $0.scope))") }
        let fixed = [MenuBarText.off, MenuBarText.highestWeekly, MenuBarText.highestOverall]
        if !fixed.contains(model.menuBarText), !result.contains(where: { $0.key == model.menuBarText }) {
            result.append(Choice(key: model.menuBarText, name: model.menuBarText))
        }
        return result
    }
}

private struct ProvidersPage: View {
    @EnvironmentObject var model: SettingsModel

    var body: some View {
        let loc = model.loc
        let enabled = loc.t("Enabled", "사용")
        let refresh = loc.t("Refresh interval (seconds)", "갱신 주기 (초)")
        let credits = loc.t("Credits gauge full amount", "크레딧 게이지 기준량")
        Form {
            Section("Codex") {
                Toggle(isOn: model.providerBinding("codex", \.enabled)) {
                    RowLabel(title: enabled, detail: loc.t("Changes apply without restart.", "재시작 없이 반영됩니다."))
                }
                NumberRow(title: refresh, detail: loc.t("Minimum 10 seconds.", "최소 10초."),
                          value: intBinding(model.providerBinding("codex", \.refreshSeconds) { min(max($0, 10), 3600) }),
                          range: 10...3600, step: 30)
                NumberRow(title: credits,
                          detail: loc.t("Credits arrive without a ceiling, so the gauge is drawn against this. A balance above it reads >100% with the Remaining metric (0% with Used).",
                                        "크레딧은 상한 없이 전달되므로 게이지는 이 값을 100% 기준으로 그립니다. 잔액이 이 값을 넘으면 잔여량 지표에서는 >100%, 사용량 지표에서는 0%로 표시됩니다."),
                          value: model.providerBinding("codex", \.creditsFullAmount) { min(max($0, 0), 1_000_000) },
                          range: 0...1_000_000, step: 100)
                LabeledContent {
                    CommandField(value: model.providerBinding("codex", \.command))
                } label: {
                    RowLabel(title: loc.t("Codex command", "Codex 명령"),
                             detail: loc.t("Command or full path used to start codex app-server. \"codex\" finds the CLI inside ChatGPT.app.",
                                           "codex app-server 실행에 쓰는 명령 또는 전체 경로입니다. \"codex\"이면 ChatGPT.app 안의 CLI를 찾습니다."))
                }
                Text(loc.t("Resolved: ", "찾은 경로: ") + model.codexResolved)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
            }
            Section("Claude") {
                Toggle(enabled, isOn: model.providerBinding("claude", \.enabled))
                NumberRow(title: refresh, detail: loc.t("Clamped to a 60-second minimum.", "최소 60초로 제한됩니다."),
                          value: intBinding(model.providerBinding("claude", \.refreshSeconds) { min(max($0, 60), 3600) }),
                          range: 60...3600, step: 30)
                NumberRow(title: credits,
                          detail: loc.t("Credits arrive without a ceiling, so the gauge is drawn against this unless Claude reports its own monthly limit. Spend above it reads >100% with the Used metric (0% with Remaining).",
                                        "크레딧은 상한 없이 전달되므로, Claude가 월 한도를 따로 보고하지 않으면 게이지는 이 값을 100% 기준으로 그립니다. 사용량이 이 값을 넘으면 사용량 지표에서는 >100%, 잔여량 지표에서는 0%로 표시됩니다."),
                          value: model.providerBinding("claude", \.creditsFullAmount) { min(max($0, 0), 1_000_000) },
                          range: 0...1_000_000, step: 100)
                Toggle(isOn: model.providerBinding("claude", \.autoRenewSession)) {
                    RowLabel(title: loc.t("Auto-renew session", "세션 자동 갱신"),
                             detail: loc.t("Runs 'claude mcp list' before the 8-hour token expires so Claude Code refreshes it. Costs no usage.",
                                           "8시간짜리 토큰이 만료되기 전에 'claude mcp list'를 실행해 Claude Code가 갱신하도록 합니다. 사용량은 소모되지 않습니다."))
                }
                LabeledContent {
                    Button(loc.t("Reconnect providers", "프로바이더 재연결"), action: model.onReconnect)
                } label: {
                    RowLabel(title: model.claudeCredentialsFound
                                ? loc.t("Claude Code credentials found.", "Claude Code 자격증명을 찾았습니다.")
                                : loc.t("Not signed in. Run Claude Code and sign in.", "미로그인 상태입니다. Claude Code를 실행해 로그인하세요."),
                             detail: loc.t("Read from the login Keychain item \"Claude Code-credentials\".",
                                           "로그인 키체인 항목 \"Claude Code-credentials\"에서 읽습니다."))
                }
            }
            Section {
                Text(loc.t("Which rows each provider shows - model windows, credits - is picked per row under Appearance.",
                           "프로바이더별로 어떤 행(모델별 window, 크레딧)을 표시할지는 모양 탭에서 행마다 선택합니다."))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }
}

// Saves on Enter or focus loss, not on every keystroke: a provider rebuild
// restarts codex app-server.
private struct CommandField: View {
    @Binding var value: String
    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        TextField("codex", text: $text)
            .labelsHidden()
            .frame(width: 200)
            .focused($focused)
            .onAppear { text = value }
            .onSubmit(commit)
            .onChange(of: focused) { if !$0 { commit() } }
    }

    private func commit() {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let next = trimmed.isEmpty ? "codex" : trimmed
        text = next
        if next != value { value = next }
    }
}

private struct AppearancePage: View {
    @EnvironmentObject var model: SettingsModel

    var body: some View {
        let loc = model.loc
        Form {
            Section {
                Picker(selection: model.binding(.appearance, \.gaugeMetric)) {
                    Text(option("Used", loc)).tag("Used")
                    Text(option("Remaining", loc)).tag("Remaining")
                } label: {
                    RowLabel(title: loc.t("Gauge metric", "게이지 지표"),
                             detail: loc.t("Whether gauges and percentages show used or remaining capacity.",
                                           "게이지와 퍼센트가 사용량/잔여량 중 무엇을 표시할지."))
                }
                Picker(selection: model.themeBinding) {
                    Text(option("System", loc)).tag("System")
                    Text(option("Light", loc)).tag("Light")
                    Text(option("Dark", loc)).tag("Dark")
                } label: {
                    RowLabel(title: loc.t("Theme", "테마"),
                             detail: loc.t("Popup and settings. System follows macOS light/dark.",
                                           "팝업과 설정 창에 적용됩니다. 시스템은 macOS의 라이트/다크를 따릅니다."))
                }
            }
            RowList()
        }
    }
}

// Every row the providers report: check the ones to show and order them.
// Ordering is per provider, and each provider keeps at least one row.
private struct RowList: View {
    @EnvironmentObject var model: SettingsModel

    var body: some View {
        let loc = model.loc
        Section {
            if model.rows.isEmpty {
                Text(loc.t("No rows yet. Open the popup once so providers report their rows.",
                           "아직 표시할 행이 없습니다. 팝업을 한 번 열어 프로바이더 행을 받아오세요."))
                    .foregroundStyle(.secondary)
            }
            ForEach(providerIds, id: \.self) { providerId in
                let rows = ordered(providerId)
                let shown = rows.filter { RowShapes.isVisible(model.settings, $0.providerId, $0.label, $0.isPrimary) }.count
                Text(rows.first?.providerName ?? providerId)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.secondary)
                ForEach(rows.indices, id: \.self) { index in
                    line(rows, index, rows[index], shown: shown)
                }
            }
        } header: {
            Text(loc.t("Rows to show", "표시할 행"))
        } footer: {
            Text(loc.t("Clear a checkbox to drop that row from the popup and the menu bar tooltip. Every provider keeps at least one row, so its last checked row cannot be cleared.",
                       "체크를 해제하면 해당 행이 팝업과 메뉴 막대 툴팁에서 빠집니다. 프로바이더마다 최소 한 행은 남아야 해서, 마지막으로 체크된 행은 해제할 수 없습니다."))
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var providerIds: [String] {
        var seen: [String] = []
        for row in model.rows where !seen.contains(where: { $0.caseInsensitiveCompare(row.providerId) == .orderedSame }) {
            seen.append(row.providerId)
        }
        return seen
    }

    private func ordered(_ providerId: String) -> [UsageRowRef] {
        RowShapes.order(model.settings, providerId,
                        model.rows.filter { $0.providerId.caseInsensitiveCompare(providerId) == .orderedSame }) { $0.label }
    }

    private func line(_ rows: [UsageRowRef], _ index: Int, _ row: UsageRowRef, shown: Int) -> some View {
        let loc = model.loc
        let key = RowShapes.key(row.providerId, row.label)
        let visible = RowShapes.isVisible(model.settings, row.providerId, row.label, row.isPrimary)
        return HStack {
            Toggle(isOn: Binding(get: { visible },
                                 set: { on in model.update(.appearance) { $0.rowVisibility[key] = on } })) {
                Text(loc.rowLabel(row.providerId, row.label, scope: row.scope))
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            .toggleStyle(.checkbox)
            .disabled(visible && shown <= 1)
            Spacer()
            Button { move(rows, index, -1) } label: { Image(systemName: "chevron.up") }
                .buttonStyle(.borderless)
                .disabled(index == 0)
                .help(loc.t("Move up", "위로"))
            Button { move(rows, index, 1) } label: { Image(systemName: "chevron.down") }
                .buttonStyle(.borderless)
                .disabled(index == rows.count - 1)
                .help(loc.t("Move down", "아래로"))
        }
    }

    // Persists the whole provider's order so later rows stay put.
    private func move(_ rows: [UsageRowRef], _ index: Int, _ delta: Int) {
        let target = index + delta
        guard rows.indices.contains(target) else { return }
        var reordered = rows
        reordered.swapAt(index, target)
        model.update(.appearance) { settings in
            for (position, row) in reordered.enumerated() {
                settings.rowOrder[RowShapes.key(row.providerId, row.label)] = position
            }
        }
    }
}

private struct AlertsPage: View {
    @EnvironmentObject var model: SettingsModel

    var body: some View {
        let loc = model.loc
        Form {
            Section {
                NumberRow(title: loc.t("Warning level", "경고 단계"),
                          detail: loc.t("Remaining percent at which a popup row turns warning orange. The menu bar icon gets an orange dot from each provider's main rows.",
                                        "남은 양이 이 % 이하가 되면 팝업 행이 경고(주황)로 바뀝니다. 메뉴 막대 아이콘은 프로바이더의 주 행을 따라 주황 점을 표시합니다."),
                          value: intBinding(Binding(get: { model.settings.warningThresholdPercent },
                                                    set: { value in model.update(.appearance) { $0.warningThresholdPercent = min(max(value, 1), 99) } })),
                          range: 1...99, step: 5)
                // 0 means "only once nothing is left".
                NumberRow(title: loc.t("Critical level", "위험 단계"),
                          detail: loc.t("Remaining percent at which it turns critical red. Keep it below the warning level.",
                                        "남은 양이 이 % 이하가 되면 위험(빨강)으로 바뀝니다. 경고 단계보다 낮게 두세요."),
                          value: Binding(get: { model.settings.criticalThresholdPercent },
                                         set: { value in
                                             model.update(.appearance) {
                                                 $0.criticalThresholdPercent = (min(max(value, 0), 99) * 10).rounded() / 10
                                             }
                                         }),
                          range: 0...99, step: 0.5)
            }
            Section {
                Text(loc.t("Notifications at alert points come in a later version of the Mac app.",
                           "알림 지점에서 보내는 알림은 Mac 앱의 다음 버전에서 추가됩니다."))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }
}

private struct AboutPage: View {
    @EnvironmentObject var model: SettingsModel

    var body: some View {
        let info = Bundle.main.infoDictionary
        let version = info?["CFBundleShortVersionString"] as? String ?? "dev"
        let build = info?["CFBundleVersion"] as? String ?? "-"
        Form {
            Section {
                VStack(alignment: .leading, spacing: 6) {
                    Text("QuotaScope").font(.title2.weight(.semibold))
                    Text(model.loc.t("Version \(version) (build \(build), macOS preview)",
                                     "버전 \(version) (빌드 \(build), macOS 미리보기)"))
                        .foregroundStyle(.secondary)
                }
                Text("This project is not affiliated with, endorsed by, or sponsored by OpenAI or Anthropic. Codex is a product/service of OpenAI. Claude is a product/service of Anthropic.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Link("github.com/EvanHexX/quota-scope", destination: URL(string: "https://github.com/EvanHexX/quota-scope")!)
            }
        }
    }
}
#endif
