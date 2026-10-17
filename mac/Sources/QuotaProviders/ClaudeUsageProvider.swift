import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif
import QuotaCore

// Port of app/Providers/Claude/ClaudeUsageProvider.cs: polls the undocumented
// Anthropic OAuth usage endpoint (the data behind Claude Code's /usage). It
// may break without notice. The status strings match the Windows app.
public actor ClaudeUsageProvider: UsageProvider {
    static let usageEndpoint = URL(string: "https://api.anthropic.com/api/oauth/usage")!
    static let betaHeader = "oauth-2025-04-20"
    // Without a claude-code User-Agent the endpoint persistently returns 429.
    static let userAgent = "claude-code/2.0.0"
    static let backoffDelays: [TimeInterval] = [60, 5 * 60, 15 * 60]
    // Without auto-renew the session dies unattended, so warn well ahead.
    static let manualSessionWarning: TimeInterval = 60 * 60
    // Credential writes arrive as a burst; coalesce them.
    static let changeDebounce: TimeInterval = 2
    // The Keychain has no change notification, so its modification date is
    // polled; the file is polled the same way.
    static let changePollInterval: TimeInterval = 5
    public static let unreadableStatus = "Claude credentials could not be read. Allow access in the Keychain prompt"

    public nonisolated let id = ClaudeUsageMapper.providerId
    public nonisolated let displayName = ClaudeUsageMapper.providerDisplayName

    private let credentials: ClaudeCredentialSource
    private let transport: ClaudeUsageTransport
    private let renewer: ClaudeSessionRenewer
    private let now: @Sendable () -> Date
    private let minPollInterval: TimeInterval
    private let autoRenew: Bool
    private let creditsFullAmount: Double
    private let diagnosticsDirectory: URL?

    private var lastSuccess: ProviderUsage?
    private var lastResult: ProviderUsage?
    private var nextFetchAt = Date.distantPast
    private var backoffStep = 0
    private var authPaused = false
    private var authPausedStamp: Date?
    // The item has no Claude Code sign-in at all (only MCP connector tokens):
    // the hint drops "expired", and there is no session to renew.
    private var signedOut = false
    private var fetchTail: Task<Void, Never>?
    private var watchTask: Task<Void, Never>?
    private var updateHandler: (@Sendable (ProviderUsage) -> Void)?
    private var closed = false

    public init(settings: ProviderSettings,
                credentials: ClaudeCredentialSource = PreferredCredentialSource.standard(),
                transport: ClaudeUsageTransport = URLSessionUsageTransport(),
                renewer: ClaudeSessionRenewer? = nil,
                now: @escaping @Sendable () -> Date = { Date() },
                diagnosticsDirectory: URL? = nil,
                watchChanges: Bool = true) {
        // Poll interval floor is 60 seconds regardless of configuration.
        minPollInterval = TimeInterval(max(60, settings.refreshSeconds))
        autoRenew = settings.autoRenewSession
        creditsFullAmount = settings.creditsFullAmount
        self.credentials = credentials
        self.transport = transport
        self.renewer = renewer ?? ClaudeSessionRenewer(credentials: credentials, now: now)
        self.now = now
        self.diagnosticsDirectory = diagnosticsDirectory
        if watchChanges {
            Task { await self.startWatching() }
        }
    }

    public func setUpdateHandler(_ handler: @escaping @Sendable (ProviderUsage) -> Void) {
        updateHandler = handler
    }

    public func read() async throws -> ProviderUsage { await fetch(force: false) }

    public func reconnect() async throws -> ProviderUsage {
        authPaused = false
        backoffStep = 0
        nextFetchAt = .distantPast
        // An explicit reconnect also clears a renewer that had given up.
        renewer.reset()
        return await fetch(force: true)
    }

    public func close() {
        closed = true
        watchTask?.cancel()
        watchTask = nil
        // Ends a nudge that is still running, so quitting cannot strand a
        // `claude` process.
        renewer.dispose()
    }

    // The change watcher and the poll timer both call in; one fetch at a time
    // keeps the cache and backoff state consistent (the C# semaphore).
    private func fetch(force: Bool) async -> ProviderUsage {
        let previous = fetchTail
        let task = Task { () -> ProviderUsage in
            await previous?.value
            return await self.fetchCore(force: force)
        }
        fetchTail = Task { _ = await task.value }
        return await task.value
    }

    private func fetchCore(force: Bool) async -> ProviderUsage {
        // Claude switched off in settings closes the provider; a poll that was
        // already queued must not touch the Keychain or start `claude` after that.
        if closed { return withFallback(.unavailable, "Claude is off") }
        let now = self.now()

        if authPaused && !force {
            // Resume only after Claude Code rewrites the credentials.
            if await keychainStamp() == authPausedStamp {
                // An item without a Claude Code sign-in has no session to renew.
                if autoRenew && !signedOut { renewer.requestRenewal(recovery: true) }
                // Re-emitted, so a renewal that started (or gave up) shows.
                return await pauseForAuth()
            }
            // Something rewrote the credentials: clear any renewal backoff.
            authPaused = false
            renewer.reset()
        }

        if !force, now < nextFetchAt, let lastResult { return lastResult }

        // The secret is read only for a request that is actually sent. On
        // macOS every read is a Keychain data access, and until the user picks
        // "Always Allow" each one shows the access prompt again.
        guard let current = await readCredentials() else {
            // Credentials that exist but cannot be read are not a signed-out
            // session: on macOS it is the Keychain prompt, denied or still
            // waiting. Retry on the next poll instead of pausing for a
            // sign-in that would never come.
            if await keychainStamp() != nil {
                nextFetchAt = now.addingTimeInterval(minPollInterval)
                return remember(withFallback(.unavailable, Self.unreadableStatus))
            }
            return await pauseForAuth()
        }
        let expiresAt = current.expiresAt
        if (current.accessToken ?? "").isEmpty && expiresAt == nil {
            return await pauseForAuth(signedOut: true)
        }
        signedOut = false

        // Proactive renewal: hand the refresh back to Claude Code before the
        // token dies. Past the expiry stamp the nudge is no longer speculative.
        if autoRenew && ClaudeRenewalPolicy.isExpiring(expiresAt, now: now) {
            renewer.requestRenewal(recovery: expiresAt.map { $0 <= now } ?? true)
        }

        guard let token = current.accessToken, !token.isEmpty else { return await pauseForAuth() }

        do {
            let response = try await transport.get(Self.usageEndpoint, headers: [
                "Authorization": "Bearer \(token)",
                "anthropic-beta": Self.betaHeader,
                "User-Agent": Self.userAgent,
            ])
            if response.status == 401 {
                // The token died before the proactive window could fire (the
                // Mac slept, the clock jumped); ask Claude Code to renew now.
                if autoRenew { renewer.requestRenewal(recovery: true) }
                return await pauseForAuth()
            }
            if response.status == 429 {
                let delay = Self.backoffDelays[min(backoffStep, Self.backoffDelays.count - 1)]
                backoffStep = min(backoffStep + 1, Self.backoffDelays.count - 1)
                nextFetchAt = now.addingTimeInterval(delay)
                return remember(withFallback(.rateLimited, "Rate limited. Retrying in \(Self.formatDelay(delay))"))
            }
            guard (200..<300).contains(response.status) else { throw ProviderError("HTTP \(response.status)") }

            let body = String(decoding: response.body, as: UTF8.self)
            let root = try JSONValue.parse(body)
            dumpLastResponse(response.body)
            var usage = ClaudeUsageMapper.fromJSON(root, creditsFullAmount: creditsFullAmount)
            usage.statusText = sessionStatus(expiresAt, now)
            usage.updatedAt = now
            backoffStep = 0
            authPaused = false
            nextFetchAt = now.addingTimeInterval(minPollInterval)
            lastSuccess = usage
            return remember(usage)
        } catch {
            // Network, timeout, or parse failure: keep the last good data, stale.
            nextFetchAt = now.addingTimeInterval(minPollInterval)
            return remember(withFallback(.stale, staleStatus()))
        }
    }

    private func remember(_ usage: ProviderUsage) -> ProviderUsage {
        lastResult = usage
        return usage
    }

    // When Claude Code rewrites the credentials (sign-in or token refresh),
    // push a fresh read instead of waiting for the next poll.
    private func startWatching() {
        guard !closed, watchTask == nil else { return }
        let interval = Self.changePollInterval, debounce = Self.changeDebounce
        watchTask = Task { [weak self] in
            var last: Date? = nil
            if let first = await self?.watchedStamp() { last = first }
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: UInt64(interval * 1_000_000_000))
                guard let self, !Task.isCancelled else { return }
                // nil: a data read is waiting on the Keychain prompt; skip the tick.
                guard let stamp = await self.watchedStamp() else { continue }
                guard stamp != last else { continue }
                try? await Task.sleep(nanoseconds: UInt64(debounce * 1_000_000_000))
                last = await self.watchedStamp() ?? stamp
                await self.credentialsChanged()
            }
        }
    }

    // While a data read waits on the Keychain prompt, the login keychain
    // holds a lock that blocks attribute lookups too (seen on the Mac with
    // sample: stamp() parked in a Security mutex). So every Keychain call runs
    // on one serial queue off the actor, and the watcher skips its tick
    // instead of queueing behind the prompt.
    // The renewer's end-of-attempt lookup uses it too.
    static let keychainQueue = DispatchQueue(label: "QuotaScope.ClaudeCredentials", qos: .utility)
    private var readInFlight = false
    // Whether credentials existed at the last lookup, for the status text.
    private var knownToExist = false

    private func onKeychain<T: Sendable>(_ work: @escaping @Sendable () -> T) async -> T {
        await withCheckedContinuation { continuation in
            Self.keychainQueue.async { continuation.resume(returning: work()) }
        }
    }

    private func keychainStamp() async -> Date? {
        let source = credentials
        let stamp = await onKeychain { source.stamp() }
        knownToExist = stamp != nil
        return stamp
    }

    // Outer nil means "not now": a read is waiting on the prompt.
    private func watchedStamp() async -> Date?? {
        if readInFlight { return nil }
        return .some(await keychainStamp())
    }

    private func credentialsChanged() async {
        guard !closed else { return }
        renewer.reset()
        let usage = await fetch(force: true)
        updateHandler?(usage)
    }

    // Until the user answers a Keychain prompt, the read (the security tool,
    // or SecItemCopyMatching for the stamp) blocks its thread, and that must
    // not be one of the concurrency pool's threads.
    private func readCredentials() async -> ClaudeCredentials? {
        let source = credentials
        readInFlight = true
        defer { readInFlight = false }
        return await onKeychain { source.read() }
    }

    private func pauseForAuth(signedOut: Bool? = nil) async -> ProviderUsage {
        if let signedOut { self.signedOut = signedOut }
        authPaused = true
        authPausedStamp = await keychainStamp()
        return remember(.offline(id, displayName, authPausedStatus(), .unauthenticated))
    }

    // A renewal in flight is the actionable state; otherwise the sign-in hint,
    // worded by whether credentials exist at all.
    private func authPausedStatus() -> String {
        if renewer.isRenewing { return "Renewing Claude session" }
        return knownToExist && !signedOut ? "Claude session expired. Run Claude Code and sign in" : "Run Claude Code and sign in"
    }

    // The OAuth session next to the usage rows: renewal in flight, or a
    // countdown once expiry is close enough to matter.
    private func sessionStatus(_ expiresAt: Date?, _ now: Date) -> String {
        if renewer.isRenewing { return "Renewing Claude session" }
        if let expiresAt {
            let remaining = expiresAt.timeIntervalSince(now)
            if remaining <= 0 { return "Claude session expired" }
            let warnWithin = autoRenew ? ClaudeRenewalPolicy.renewLeadTime : Self.manualSessionWarning
            if remaining <= warnWithin { return "Session expires in \(Self.formatDelay(remaining))" }
        }
        return "Claude rate limit"
    }

    private func withFallback(_ state: ProviderState, _ status: String) -> ProviderUsage {
        guard var usage = lastSuccess else {
            return .offline(id, displayName, status, state == .stale ? .unavailable : state)
        }
        usage.state = state
        usage.statusText = status
        return usage
    }

    private func staleStatus() -> String {
        guard let lastSuccess else { return "Claude usage unavailable" }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "h:mm a"
        return "Stale - last \(formatter.string(from: lastSuccess.updatedAt))"
    }

    // Local-only diagnostic: the last successful response body (usage numbers,
    // no credentials), for checking live schema changes.
    private func dumpLastResponse(_ body: Data) {
        guard let diagnosticsDirectory else { return }
        try? FileManager.default.createDirectory(at: diagnosticsDirectory, withIntermediateDirectories: true)
        try? body.write(to: diagnosticsDirectory.appendingPathComponent("claude_usage_last.json"))
    }

    static func formatDelay(_ delay: TimeInterval) -> String {
        delay >= 60 ? "\(Int(delay / 60))m" : "\(Int(delay))s"
    }
}

public protocol ClaudeUsageTransport: Sendable {
    func get(_ url: URL, headers: [String: String]) async throws -> (status: Int, body: Data)
}

public struct URLSessionUsageTransport: ClaudeUsageTransport {
    public init() {}

    public func get(_ url: URL, headers: [String: String]) async throws -> (status: Int, body: Data) {
        var request = URLRequest(url: url, timeoutInterval: 15)
        for (name, value) in headers { request.setValue(value, forHTTPHeaderField: name) }
        return try await withCheckedThrowingContinuation { continuation in
            URLSession.shared.dataTask(with: request) { data, response, error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume(returning: ((response as? HTTPURLResponse)?.statusCode ?? 0, data ?? Data()))
                }
            }.resume()
        }
    }
}
