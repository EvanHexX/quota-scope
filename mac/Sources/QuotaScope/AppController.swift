#if os(macOS)
import AppKit
import SwiftUI
import QuotaCore
import QuotaProviders

// Port of app-winui/TrayController.cs for the menu bar MVP (phase 4): owns the
// providers, polling, the status item, the popover, and the pinned panel.
// Alerts, hotkeys, the settings window, and the login item come in phase 5.
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

        scheduleTimer()
        refresh()
    }

    private func scheduleTimer() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: pollInterval(), repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
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
           claudeUsage?.state == .unauthenticated || !PreferredCredentialSource.standard().exists {
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
    }

    private func update(_ usage: ProviderUsage) {
        // A disabled provider's late update must not bring its section back.
        guard providers.contains(where: { $0.id == usage.providerId }) else { return }
        usages[usage.providerId] = usage
        applyVisuals()
        settingsModel.rows = rowRefs()
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
        let claude = ClaudeCommandResolver.resolve(["/login"])?.executable ?? "claude"
        let script = FileManager.default.temporaryDirectory.appendingPathComponent("quotascope-claude-login.command")
        let quoted = "'" + claude.replacingOccurrences(of: "'", with: "'\\''") + "'"
        let body = "#!/bin/zsh -l\n\(quoted) /login\n"
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
