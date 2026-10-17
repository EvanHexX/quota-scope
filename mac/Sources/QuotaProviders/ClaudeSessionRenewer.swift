import Foundation
import QuotaCore

// Port of the process-running part of app/Providers/Claude/ClaudeSessionRenewer.cs;
// the timing rules are ClaudeRenewalPolicy in QuotaCore.
//
// Runs `claude mcp list` headless, a read-only command that costs no quota,
// and lets Claude Code refresh and rewrite its own credentials. The command
// output describes the user's configured MCP servers, so it is discarded
// unread and never logged.
public final class ClaudeSessionRenewer: @unchecked Sendable {
    // Runs one attempt and says whether the command exited cleanly. Injected so
    // tests do not start processes.
    public typealias Runner = @Sendable (ClaudeSessionRenewer) -> Bool
    // Where an attempt runs: a background queue in the app, inline in tests.
    public typealias Scheduler = @Sendable (@escaping @Sendable () -> Void) -> Void

    private let lock = NSLock()
    private let credentials: ClaudeCredentialSource
    private let now: @Sendable () -> Date
    private let runner: Runner
    private let schedule: Scheduler
    private var nextAttemptAt = Date.distantPast
    private var attemptStamp: Date?
    private var consecutiveFailures = 0
    private var running = false
    private var attemptIsRecovery = false
    private var current: Process?
    private var disposed = false

    public init(credentials: ClaudeCredentialSource,
                now: @escaping @Sendable () -> Date = { Date() },
                runner: Runner? = nil,
                schedule: @escaping Scheduler = { work in DispatchQueue.global(qos: .utility).async(execute: work) },
                environment: CommandEnvironment = .current) {
        self.credentials = credentials
        self.now = now
        self.schedule = schedule
        self.runner = runner ?? { renewer in renewer.runRenewalCommand(environment: environment) }
    }

    public var isRenewing: Bool {
        lock.lock(); defer { lock.unlock() }
        return running
    }

    // Whatever rewrote the credentials (Claude Code, a manual sign-in) cleared
    // the condition the renewer was backing off from.
    public func reset() {
        lock.lock(); defer { lock.unlock() }
        guard !disposed else { return }
        consecutiveFailures = 0
        nextAttemptAt = .distantPast
    }

    // Fire and forget: the credentials watcher or the next poll picks up the
    // result. `recovery` means the token is already dead, so a nudge that
    // changes nothing really is a failure. Returns whether an attempt started.
    @discardableResult
    public func requestRenewal(recovery: Bool) -> Bool {
        lock.lock()
        if disposed || running || now() < nextAttemptAt {
            lock.unlock()
            return false
        }
        running = true
        attemptIsRecovery = recovery
        attemptStamp = credentials.stamp()
        lock.unlock()

        schedule { [self] in
            let ran = runner(self)
            let renewed = ran && credentials.stamp() != attemptStampValue()
            complete(renewed: renewed)
        }
        return true
    }

    private func attemptStampValue() -> Date? {
        lock.lock(); defer { lock.unlock() }
        return attemptStamp
    }

    private func complete(renewed: Bool) {
        lock.lock(); defer { lock.unlock() }
        running = false
        let recovery = attemptIsRecovery
        if renewed { consecutiveFailures = 0 } else if recovery { consecutiveFailures += 1 }
        let delay = ClaudeRenewalPolicy.nextDelayAfter(renewed: renewed, recovery: recovery, failures: consecutiveFailures)
        nextAttemptAt = delay.map { now().addingTimeInterval($0) } ?? .distantFuture
    }

    func runRenewalCommand(environment: CommandEnvironment) -> Bool {
        guard let spec = ClaudeCommandResolver.resolve(ClaudeRenewalPolicy.renewalArguments, environment: environment) else { return false }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: spec.executable)
        process.arguments = spec.arguments
        var variables = ProcessInfo.processInfo.environment
        #if !os(Windows)
        variables["PATH"] = environment.childPath
        #endif
        process.environment = variables
        // Outside the app bundle, so no project-trust prompt can block it.
        process.currentDirectoryURL = URL(fileURLWithPath: environment.home)
        // Discarded without being read: it lists the user's MCP servers.
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        let done = DispatchSemaphore(value: 0)
        process.terminationHandler = { _ in done.signal() }

        lock.lock()
        if disposed { lock.unlock(); return false }
        do { try process.run() } catch { lock.unlock(); return false }
        current = process
        lock.unlock()
        defer { lock.lock(); current = nil; lock.unlock() }

        if done.wait(timeout: .now() + ClaudeRenewalPolicy.processTimeout) == .timedOut {
            process.terminate()
            return false
        }
        return process.terminationStatus == 0
    }

    // Quitting mid-nudge would strand the CLI it launched, holding its own
    // executable open; it is ended here instead.
    public func dispose() {
        lock.lock()
        guard !disposed else { lock.unlock(); return }
        disposed = true
        nextAttemptAt = .distantFuture
        let process = current
        lock.unlock()
        if let process, process.isRunning { process.terminate() }
    }
}
