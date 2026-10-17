#if os(macOS)
import AppKit
import SwiftUI
import QuotaCore
import QuotaProviders

// Port of app-winui/TrayController.cs for the menu bar MVP (phase 4): owns the
// providers, polling, the status item, the popover, and the pinned panel.
// Alerts, the settings window, and hotkeys came in phase 5.
@MainActor
final class AppController: NSObject, NSPopoverDelegate, NSWindowDelegate {
    private let store = SettingsStore()
    private let model = UsageModel()
    private var providers: [UsageProvider] = []
    private var usages: [String: ProviderUsage] = [:]
    private var statusItem: NSStatusItem!
    private var popover: NSPopover?
    private var panel: NSPanel?
    private var timer: Timer?
    private var pollInFlight: Task<Void, Never>?
    // Bumped when providers are rebuilt, so a poll of the old ones is ignored.
    private var generation = 0
    private var rebuildPending: DispatchWorkItem?
    private lazy var settingsModel = SettingsModel(store: store)
    private lazy var settingsWindow = SettingsWindowController(model: settingsModel)
    private let alertTracker = UsageAlertTracker()
    private let notifier = Notifier()
    private let hotkeys = GlobalHotkeys()

    func start() {
        model.settings = store.settings
        model.loc = currentLoc()
        model.isPinned = store.isPinned
        model.onRefresh = { [weak self] in self?.refresh() }
        model.onTogglePin = { [weak self] in self?.togglePin() }
        model.onToggleTimeMode = { [weak self] in self?.toggleTimeMode() }
        model.onOpenSettings = { [weak self] in self?.openSettings() }
        // The provider notices the new credentials by itself (stamp watch).
        model.onSignIn = { ClaudeLogin.openTerminal() }
        settingsModel.loc = model.loc
        settingsModel.onChange = { [weak self] kind in self?.settingsChanged(kind) }
        settingsModel.onReconnect = { [weak self] in self?.reconnect() }
        settingsModel.onOpenNotificationSettings = { [weak self] in self?.notifier.openSystemSettings() }
        settingsModel.notificationPermission = notifier.permission
        notifier.onPermissionChange = { [weak self] in self?.settingsModel.notificationPermission = $0 }
        notifier.onActivate = { [weak self] in self?.showPopover() }
        if store.settings.notifyOnThreshold { notifier.prepare() }
        settingsModel.onRegisterHotkey = { [weak self] in self?.rebind($0, $1) }
        settingsModel.onRecordingHotkey = { [weak self] in self?.hotkeys.suspend($0) }
        applyTheme()

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.imagePosition = .imageLeading
            button.target = self
            button.action = #selector(statusItemClicked(_:))
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }

        buildProviders()
        applyVisuals()
        if let warning = store.loadWarning { NSLog("QuotaScope: \(warning)") }

        initializeHotkeys()
        scheduleTimer()
        refresh()
    }

    private func scheduleTimer() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: pollInterval(), repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
    }

    // MARK: hotkeys

    private func initializeHotkeys() {
        hotkeys.onPressed = { [weak self] action in
            guard let self else { return }
            switch action {
            case .togglePopup: self.togglePopover()
            case .refreshAll: self.refresh()
            case .togglePin: self.togglePin()
            }
        }
        var warnings: [String] = []
        for action in HotkeyAction.allCases {
            registerFromSettings(action, fallback: action == .togglePopup ? "Ctrl+Alt+U" : nil, &warnings)
        }
        settingsModel.hotkeyWarnings = warnings
        // Load-time problems show at once, not only on the settings page.
        if !warnings.isEmpty { notifier.show(title: "QuotaScope hotkeys", body: warnings.joined(separator: "\n")) }
    }

    private func registerFromSettings(_ action: HotkeyAction, fallback: String?, _ warnings: inout [String]) {
        let text = store.settings[keyPath: action.settingsPath]
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        guard let definition = HotkeyDefinition.parse(text) else {
            // An invalid stored string falls back to the default (or unbound).
            if let fallback, let definition = HotkeyDefinition.parse(fallback) {
                warnings.append("Invalid hotkey '\(text)' for \(action); using default \(fallback).")
                store.update { $0[keyPath: action.settingsPath] = fallback }
                if let error = hotkeys.register(action, definition) { warnings.append("\(action): \(message(error))") }
            } else {
                warnings.append("Invalid hotkey '\(text)' for \(action); left unbound.")
                store.update { $0[keyPath: action.settingsPath] = "" }
            }
            model.settings = store.settings
            return
        }
        if let error = hotkeys.register(action, definition) { warnings.append("\(action): \(message(error))") }
    }

    // From the settings window; on failure the previous binding comes back.
    private func rebind(_ action: HotkeyAction, _ definition: HotkeyDefinition?) -> String? {
        guard let definition else {
            hotkeys.unregister(action)
            return nil
        }
        let previous = HotkeyDefinition.parse(store.settings[keyPath: action.settingsPath])
        guard let error = hotkeys.register(action, definition) else { return nil }
        if let previous { _ = hotkeys.register(action, previous) }
        return message(error)
    }

    private func message(_ error: HotkeyRegistrationError) -> String {
        let loc = model.loc
        switch error {
        case .unsupportedKey: return loc.t("That key has no Mac equivalent.", "Mac에는 그 키가 없습니다.")
        case .inUse: return loc.t("That shortcut is already in use.", "이미 쓰이는 단축키입니다.")
        case .failed(let status): return loc.t("The shortcut could not be registered (\(status)).", "단축키를 등록하지 못했습니다(\(status)).")
        }
    }

    // MARK: settings window

    func openSettings() {
        popover?.performClose(nil)
        settingsModel.loc = model.loc
        settingsModel.rows = rowRefs()
        settingsWindow.show()
    }

    private func settingsChanged(_ kind: SettingsChange) {
        model.settings = store.settings
        let loc = currentLoc()
        if loc != model.loc {
            model.loc = loc
            settingsModel.loc = loc
            settingsWindow.relocalize()
        }
        applyTheme()
        applyVisuals()
        // Switching notifications on asks for the permission if it never was.
        if kind == .general, store.settings.notifyOnThreshold { notifier.prepare() }
        // Providers rebuild on change, as on Windows; typing a number fires
        // once per keystroke, so the rebuild waits for the last one.
        if kind == .providers {
            rebuildPending?.cancel()
            let work = DispatchWorkItem { [weak self] in
                MainActor.assumeIsolated { self?.rebuildProviders() }
            }
            rebuildPending = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 1, execute: work)
        }
    }

    private func rebuildProviders() {
        rebuildPending = nil
        generation += 1
        pollInFlight?.cancel()
        pollInFlight = nil
        model.refreshing = false
        let old = providers
        providers = []
        usages = [:]
        Task { for provider in old { await provider.close() } }
        buildProviders()
        scheduleTimer()
        applyVisuals()
        refresh()
    }

    // System / Light / Dark (decision M4) for the popover, the pinned panel,
    // and the settings window. The menu bar icon follows the menu bar itself.
    private func applyTheme() {
        let settings = store.settings
        if settings.followSystemTheme {
            NSApp.appearance = nil
        } else {
            let light = settings.themeOverride.caseInsensitiveCompare("Light") == .orderedSame
            NSApp.appearance = NSAppearance(named: light ? .aqua : .darkAqua)
        }
    }

    private func rowRefs() -> [UsageRowRef] {
        currentUsages.flatMap { usage in
            usage.rows.map {
                UsageRowRef(providerId: usage.providerId, providerName: usage.displayName,
                            label: $0.label, isPrimary: $0.isPrimary, scope: $0.scope)
            }
        }
    }

    // MARK: providers

    private func buildProviders() {
        let codex = store.settings.provider("codex")
        if codex.enabled { providers.append(CodexUsageProvider(settings: codex)) }
        let claude = store.settings.provider("claude")
        if claude.enabled {
            providers.append(ClaudeUsageProvider(settings: claude, diagnosticsDirectory: SettingsStore.directory))
        }
        for provider in providers {
            usages[provider.id] = .offline(provider.id, provider.displayName,
                                           "Waiting for \(provider.displayName) connection", .unavailable)
            Task {
                await provider.setUpdateHandler { [weak self] usage in
                    Task { @MainActor in self?.update(usage) }
                }
            }
        }
    }

    private func pollInterval() -> TimeInterval {
        let seconds = providers.map { store.settings.provider($0.id).refreshSeconds }.min() ?? 60
        return TimeInterval(max(10, seconds))
    }

    private var currentUsages: [ProviderUsage] {
        providers.compactMap { usages[$0.id] }
    }

    // A poll already running is the answer to a new request.
    func refresh() {
        guard pollInFlight == nil else { return }
        pollInFlight = Task { await poll(reconnect: false) }
    }

    func reconnect() {
        guard pollInFlight == nil else { return }
        let claudeUsage = usages["claude"]
        if store.settings.provider("claude").enabled,
           claudeUsage?.state == .unauthenticated {
            ClaudeLogin.openTerminal()
        }
        pollInFlight = Task { await poll(reconnect: true) }
    }

    private func poll(reconnect: Bool) async {
        let generation = generation
        model.refreshing = true
        // One provider's failure must not affect the others.
        for provider in providers {
            guard generation == self.generation else { return }
            if reconnect {
                update(.offline(provider.id, provider.displayName, "Connecting to \(provider.displayName)...", .unavailable))
            }
            do {
                let usage = try await withTimeout(seconds: reconnect ? 30 : 20) {
                    if reconnect { return try await provider.reconnect() }
                    return try await provider.read()
                }
                guard generation == self.generation else { return }
                update(usage)
            } catch is TimeoutError {
                guard generation == self.generation else { return }
                update(.offline(provider.id, provider.displayName,
                                "\(provider.displayName) connection timed out. Use Reconnect.", .unavailable))
            } catch {
                guard generation == self.generation else { return }
                update(.offline(provider.id, provider.displayName,
                                "\(provider.displayName) connection required: \(error)", .unavailable))
            }
        }
        model.refreshing = false
        model.lastUpdate = Date()
        pollInFlight = nil
        checkAlerts()
    }

    private func update(_ usage: ProviderUsage) {
        // A disabled provider's late update must not bring its section back.
        guard providers.contains(where: { $0.id == usage.providerId }) else { return }
        usages[usage.providerId] = usage
        applyVisuals()
        settingsModel.rows = rowRefs()
        // A poll checks once after its last provider, so both providers'
        // alerts share one notification; an update pushed between polls (a
        // Codex rolling update) is checked as it arrives.
        if !model.refreshing { checkAlerts() }
    }

    // Runs when usage arrives, never on a settings edit, as on Windows: the
    // next update is checked against the edited levels and points.
    private func checkAlerts() {
        let settings = store.settings
        // Tracked even with notifications off, so switching them on does not
        // replay every point already passed.
        let alerts = alertTracker.evaluate(settings, currentUsages)
        guard settings.notifyOnThreshold, !alerts.isEmpty else { return }
        let built = UsageAlerts.buildNotification(alerts, model.loc)
        notifier.show(title: built.title, body: built.body)
    }

    // MARK: menu bar item

    private func applyVisuals() {
        let current = currentUsages
        let settings = store.settings
        model.usages = current
        let overall = current.map(\.overallUsedPercent).max() ?? 0
        let fill = Display.showsRemaining(settings) ? 100 - overall : overall
        guard let button = statusItem.button else { return }
        button.image = StatusIcon.image(fillPercent: fill, state: Display.iconState(current, settings))
        if let text = MenuBarText.text(store.menuBarText, usages: current, settings: settings) {
            button.attributedTitle = NSAttributedString(string: " " + text, attributes: [
                .font: NSFont.monospacedDigitSystemFont(ofSize: NSFont.systemFontSize, weight: .regular),
            ])
        } else {
            button.title = ""
        }
        button.toolTip = Display.tooltip(current, settings, model.loc)
    }

    @objc private func statusItemClicked(_ sender: NSStatusBarButton) {
        guard let event = NSApp.currentEvent else { return }
        if event.type == .rightMouseUp || event.modifierFlags.contains(.control) {
            showMenu()
        } else {
            togglePopover()
        }
    }

    private func showMenu() {
        let loc = model.loc
        let menu = NSMenu()
        menu.addItem(item(loc.t("Refresh", "새로 고침"), #selector(menuRefresh)))
        menu.addItem(item(loc.t("Toggle popup", "팝업 토글"), #selector(menuTogglePopover)))
        menu.addItem(item(loc.t("Reconnect", "재연결"), #selector(menuReconnect)))
        menu.addItem(.separator())
        menu.addItem(item(loc.t("Settings...", "설정..."), #selector(menuSettings), key: ","))
        menu.addItem(item(loc.t("Open settings file", "설정 파일 열기"), #selector(menuOpenSettings)))
        menu.addItem(.separator())
        menu.addItem(item(loc.t("Quit", "종료"), #selector(menuQuit), key: "q"))
        // Attaching the menu only for this click keeps the left click free
        // for the popover.
        statusItem.menu = menu
        statusItem.button?.performClick(nil)
        statusItem.menu = nil
    }

    private func item(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        return item
    }

    @objc private func menuRefresh() { refresh() }
    @objc private func menuTogglePopover() { togglePopover() }
    @objc private func menuReconnect() { reconnect() }
    @objc private func menuQuit() { NSApp.terminate(nil) }
    @objc private func menuSettings() { openSettings() }

    @objc private func menuOpenSettings() {
        store.ensureFileExists()
        NSWorkspace.shared.open(SettingsStore.file)
    }

    // MARK: popover and pinned panel

    // A notification click: bring the usage up without closing it.
    private func showPopover() {
        if let panel {
            panel.orderFrontRegardless()
            return
        }
        if popover?.isShown == true { return }
        togglePopover()
    }

    func togglePopover() {
        if let panel {
            panel.isVisible ? panel.orderOut(nil) : panel.orderFrontRegardless()
            return
        }
        if let popover, popover.isShown {
            popover.performClose(nil)
            return
        }
        if store.isPinned {
            pin(from: nil)
            return
        }
        guard let button = statusItem.button else { return }
        let popover = NSPopover()
        popover.behavior = .transient
        popover.animates = true
        popover.delegate = self
        popover.contentViewController = hostingController()
        self.popover = popover
        NSApp.activate(ignoringOtherApps: true)
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
    }

    private func hostingController() -> NSViewController {
        let hosting = NSHostingController(rootView: PopoverView(model: model))
        // The popover and the panel follow the SwiftUI content's size.
        hosting.sizingOptions = [.preferredContentSize]
        return hosting
    }

    nonisolated func popoverDidClose(_ notification: Notification) {
        Task { @MainActor in self.popover = nil }
    }

    private func togglePin() {
        if panel != nil {
            store.isPinned = false
            model.isPinned = false
            panel?.close()
            panel = nil
        } else {
            store.isPinned = true
            model.isPinned = true
            pin(from: popover)
        }
    }

    // Pinned = the popover detached into a small floating panel that stays on
    // top and on every Space (3.2).
    private func pin(from popover: NSPopover?) {
        var origin: NSPoint?
        if let window = popover?.contentViewController?.view.window {
            origin = window.frame.origin
        }
        popover?.performClose(nil)
        self.popover = nil

        let panel = NSPanel(contentViewController: hostingController())
        panel.styleMask = [.titled, .closable, .fullSizeContentView, .nonactivatingPanel, .utilityWindow]
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.isMovableByWindowBackground = true
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.delegate = self
        if let origin {
            panel.setFrameOrigin(origin)
        } else if let buttonWindow = statusItem.button?.window {
            let frame = buttonWindow.frame
            panel.setFrameTopLeftPoint(NSPoint(x: frame.minX - PopoverView.width / 2, y: frame.minY - 8))
        }
        panel.orderFrontRegardless()
        self.panel = panel
    }

    // Closing the panel with its close button unpins it.
    nonisolated func windowWillClose(_ notification: Notification) {
        Task { @MainActor in
            guard let panel = self.panel, notification.object as? NSPanel === panel else { return }
            self.panel = nil
            self.store.isPinned = false
            self.model.isPinned = false
        }
    }

    private func toggleTimeMode() {
        let remaining = store.timeDisplayMode.caseInsensitiveCompare("RemainingTime") == .orderedSame
        store.timeDisplayMode = remaining ? "ClockTime" : "RemainingTime"
        model.settings = store.settings
        settingsModel.reload()
    }

    private func currentLoc() -> Loc {
        let system = Locale.preferredLanguages.first.map { String($0.prefix(2)) } ?? "en"
        return Loc(setting: store.settings.language, systemLanguageCode: system)
    }

    // MARK: quit

    func shutdown() async {
        timer?.invalidate()
        pollInFlight?.cancel()
        // Quitting must leave no codex or claude child process behind.
        for provider in providers { await provider.close() }
    }
}

struct TimeoutError: Error {}

@MainActor
private final class TimeoutState {
    var finished = false
}

// The provider call keeps running if it ignores cancellation; the poll moves
// on to the next provider either way, as the C# CancellationToken does.
@MainActor
func withTimeout<T>(seconds: TimeInterval, _ operation: @escaping @MainActor () async throws -> T) async throws -> T {
    try await withCheckedThrowingContinuation { continuation in
        let state = TimeoutState()
        let work = Task { @MainActor in
            do {
                let value = try await operation()
                if !state.finished { state.finished = true; continuation.resume(returning: value) }
            } catch {
                if !state.finished { state.finished = true; continuation.resume(throwing: error) }
            }
        }
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            if !state.finished {
                state.finished = true
                work.cancel()
                continuation.resume(throwing: TimeoutError())
            }
        }
    }
}

// Reconnect when Claude needs a sign-in: Terminal.app runs `claude /login`
// from a temporary .command file opened with NSWorkspace, which needs no
// Automation permission (3.4).
enum ClaudeLogin {
    static func openTerminal() {
        let script = FileManager.default.temporaryDirectory.appendingPathComponent("quotascope-claude-login.command")
        let body: String
        if let claude = ClaudeCommandResolver.resolve(["/login"])?.executable {
            let quoted = "'" + claude.replacingOccurrences(of: "'", with: "'\\''") + "'"
            body = "#!/bin/zsh -l\n\(quoted) /login\n"
        } else {
            // The Claude desktop app's built-in Claude Code is not a terminal
            // command, and its sign-in is not one QuotaScope can read.
            body = """
            #!/bin/zsh -l
            echo "QuotaScope: the Claude Code command line (claude) is not installed."
            echo "Install it, then press Sign in in QuotaScope again:"
            echo ""
            echo "  curl -fsSL https://claude.ai/install.sh | bash"
            echo ""
            exec /bin/zsh -l

            """
        }
        do {
            try body.write(to: script, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
            NSWorkspace.shared.open(script)
        } catch {
            NSLog("QuotaScope: opening the Claude sign-in failed: \(error)")
        }
    }
}
#endif
