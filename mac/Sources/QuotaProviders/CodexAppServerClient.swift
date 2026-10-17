import Foundation
import QuotaCore

// Port of app/Providers/Codex/CodexAppServerClient.cs: starts `codex
// app-server` as a child and speaks line-delimited JSON-RPC over stdio.
//
// Read-only by design: initialize, initialized, account/rateLimits/read, and
// the account/rateLimits/updated notification. Never send
// account/rateLimitResetCredit/consume, which spends a reset credit for real
// (docs/MACOS_BUILD.md, 4.2).
public actor CodexAppServerClient {
    static let rateLimitsReadMethod = "account/rateLimits/read"
    static let rateLimitsUpdatedMethod = "account/rateLimits/updated"
    static let requestTimeout: TimeInterval = 15

    private struct Pending {
        let method: String
        let continuation: CheckedContinuation<JSONValue, Error>
    }

    private let command: String
    private let creditsFullAmount: Double
    private let environment: CommandEnvironment

    private var process: Process?
    private var stdin: FileHandle?
    private var pending: [Int: Pending] = [:]
    private var nextId = 0
    private var initialized = false
    // Bumped whenever the process is replaced, so lines the old process left
    // behind cannot seed or update the new process's cache.
    private var generation = 0
    private var startTask: Task<Void, Error>?
    private var spec: CommandSpec?
    private var cliStamp: String?
    private let lastError = LastLine()
    // The last account/rateLimits/read result with every rolling update since
    // merged in.
    private var latestRead: JSONValue?
    private var updateHandler: (@Sendable (ProviderUsage) -> Void)?

    public init(command: String, creditsFullAmount: Double, environment: CommandEnvironment = .current) {
        self.command = command
        self.creditsFullAmount = creditsFullAmount
        self.environment = environment
        IgnoreSigpipe.once()
    }

    public func setUpdateHandler(_ handler: @escaping @Sendable (ProviderUsage) -> Void) {
        updateHandler = handler
    }

    // What was found, for Settings → Providers: the path, where it came from,
    // and the bundled CLI's version.
    public func resolvedCommand() -> CommandSpec? {
        spec ?? CodexCommandResolver.resolve(command, environment: environment)
    }

    public var isRunning: Bool { process?.isRunning == true }

    public func readRateLimits() async throws -> ProviderUsage {
        try await ensureStarted()
        return try await readWithoutRestart()
    }

    public func restart() async throws -> ProviderUsage {
        if let startTask { _ = try? await startTask.value }
        disposeProcessOnly()
        try await start()
        return try await readWithoutRestart()
    }

    public func close() {
        disposeProcessOnly()
    }

    private func readWithoutRestart() async throws -> ProviderUsage {
        let result = try await sendRequest(Self.rateLimitsReadMethod, params: .null)
        return RateLimitMapper.fromJSONResult(result, creditsFullAmount: creditsFullAmount)
    }

    private func ensureStarted() async throws {
        if let startTask { try await startTask.value }
        // A CLI that an update replaced keeps answering from the old binary,
        // so a changed stamp restarts the app-server.
        if process?.isRunning == true, initialized {
            guard let spec, let stamp = CodexCommandResolver.cliStamp(spec), stamp != cliStamp else { return }
        }
        disposeProcessOnly()
        try await start()
    }

    // One start at a time: a second caller waits for the first one's.
    private func start() async throws {
        let task = Task { try await self.startProcess() }
        startTask = task
        defer { startTask = nil }
        try await task.value
    }

    private func startProcess() async throws {
        lastError.clear()
        guard let spec = CodexCommandResolver.resolve(command, environment: environment) else {
            throw ProviderError("codex was not found. Install the ChatGPT app, or set the Codex command in Settings.")
        }
        self.spec = spec
        cliStamp = CodexCommandResolver.cliStamp(spec)

        let process = Process()
        process.executableURL = URL(fileURLWithPath: spec.executable)
        process.arguments = spec.arguments
        var environmentVariables = ProcessInfo.processInfo.environment
        #if !os(Windows)
        environmentVariables["PATH"] = environment.childPath
        #endif
        process.environment = environmentVariables
        process.currentDirectoryURL = URL(fileURLWithPath: environment.home)
        let input = Pipe(), output = Pipe(), errors = Pipe()
        process.standardInput = input
        process.standardOutput = output
        process.standardError = errors
        do {
            try process.run()
        } catch {
            throw ProviderError("Codex app-server failed to start (\(spec.displayText)): \(error.localizedDescription)")
        }

        generation += 1
        let current = generation
        self.process = process
        stdin = input.fileHandleForWriting
        startReaders(output: output.fileHandleForReading, errors: errors.fileHandleForReading, generation: current)

        _ = try await sendRequest("initialize", params: .object(JSONObject([
            ("clientInfo", .object(JSONObject([("name", .string("quota-scope")), ("title", .string("QuotaScope")), ("version", .string("0.1.0"))]))),
            ("capabilities", .object(JSONObject([("experimentalApi", .bool(true)), ("optOutNotificationMethods", .array([]))]))),
        ])))

        // codex app-server (>= 0.146) gates every later request until the
        // client acknowledges the handshake with `initialized`; without it the
        // server silently drops account/rateLimits/read.
        try sendNotification("initialized")
        initialized = true
    }

    private func startReaders(output: FileHandle, errors: FileHandle, generation: Int) {
        let lines = LineReader.lines(output)
        Task { [weak self] in
            for await line in lines {
                guard let self else { return }
                await self.handle(line: line, generation: generation)
            }
            await self?.outputEnded(generation: generation)
        }
        let lastError = self.lastError
        Thread.detachNewThread {
            LineReader.forEachLine(errors) { line in
                let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
                if !trimmed.isEmpty { lastError.set(trimmed) }
            }
        }
    }

    private func sendNotification(_ method: String) throws {
        try write(JSONValue.object(JSONObject([("method", .string(method))])).serialized(), method: method)
    }

    private func sendRequest(_ method: String, params: JSONValue) async throws -> JSONValue {
        guard stdin != nil else { throw ProviderError("Codex app-server is not running.") }
        nextId += 1
        let id = nextId
        let line = JSONValue.object(JSONObject([("id", .number(Int64(id))), ("method", .string(method)), ("params", params)])).serialized()
        let timeout = Task { [weak self] in
            try await Task.sleep(nanoseconds: UInt64(Self.requestTimeout * 1_000_000_000))
            await self?.expire(id)
        }
        defer { timeout.cancel() }
        return try await withCheckedThrowingContinuation { continuation in
            pending[id] = Pending(method: method, continuation: continuation)
            do {
                try write(line, method: method)
            } catch {
                pending[id] = nil
                continuation.resume(throwing: error)
            }
        }
    }

    private func write(_ line: String, method: String) throws {
        guard let stdin else { throw ProviderError("Codex app-server is not running.") }
        do {
            try stdin.write(contentsOf: Data((line + "\n").utf8))
        } catch {
            throw processFailure(method)
        }
    }

    private func expire(_ id: Int) {
        guard let request = pending.removeValue(forKey: id) else { return }
        let failed = process?.isRunning != true || lastError.value != nil
        request.continuation.resume(throwing: failed
            ? processFailure(request.method)
            : ProviderError("Codex app-server failed during \(request.method): no response from codex app-server"))
    }

    private func handle(line: String, generation: Int) {
        guard generation == self.generation, !line.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        guard let root = try? JSONValue.parse(line) else {
            // A line that is not JSON ends the C# read loop; the next poll
            // starts a fresh process.
            initialized = false
            return
        }
        if let id = root["id"]?.int64Value {
            guard let request = pending.removeValue(forKey: Int(id)) else { return }
            if let error = root["error"] {
                request.continuation.resume(throwing: ProviderError(error.serialized()))
            } else if let result = root["result"] {
                // Cached here rather than by the awaiting caller, so an update
                // right behind this response merges into it.
                if request.method == Self.rateLimitsReadMethod { latestRead = result }
                request.continuation.resume(returning: result)
            }
            return
        }
        // Notifications this client does not use (remoteControl/status/changed,
        // account/updated, ...) are ignored.
        guard root["method"]?.stringValue == Self.rateLimitsUpdatedMethod, let params = root["params"],
              let usage = mergeIntoLatestRead(params) else { return }
        updateHandler?(usage)
    }

    // A rolling update only carries what changed, so it is merged into the
    // latest read. Before the first read there is nothing to merge into, and a
    // malformed update is dropped.
    private func mergeIntoLatestRead(_ params: JSONValue) -> ProviderUsage? {
        guard let latest = latestRead, let merged = try? RateLimitMapper.mergeRollingUpdate(latest, params) else { return nil }
        latestRead = merged
        return RateLimitMapper.fromJSONResult(merged, creditsFullAmount: creditsFullAmount)
    }

    // The process closed stdout (it exited): requests still waiting would only
    // time out, so they fail now.
    private func outputEnded(generation: Int) {
        guard generation == self.generation else { return }
        initialized = false
        for (id, request) in pending {
            pending[id] = nil
            request.continuation.resume(throwing: processFailure(request.method))
        }
    }

    private func processFailure(_ method: String) -> ProviderError {
        let detail: String
        if let line = lastError.value {
            detail = line
        } else if let process, !process.isRunning {
            detail = "process exited with code \(process.terminationStatus)"
        } else {
            detail = "no response from codex app-server"
        }
        return ProviderError("Codex app-server failed during \(method): \(detail)")
    }

    private func disposeProcessOnly() {
        initialized = false
        generation += 1
        // The next process may be signed in to another account; its updates
        // must wait for its own first read.
        latestRead = nil
        for (id, request) in pending {
            pending[id] = nil
            request.continuation.resume(throwing: CancellationError())
        }
        // Closing stdin ends app-server on its own (exit 0, measured on macOS);
        // terminate covers a process that does not.
        try? stdin?.close()
        stdin = nil
        if let process, process.isRunning { process.terminate() }
        process = nil
    }
}

// The last non-empty stderr line, written from the reader thread.
final class LastLine: @unchecked Sendable {
    private let lock = NSLock()
    private var line: String?

    var value: String? { lock.lock(); defer { lock.unlock() }; return line }
    func set(_ text: String) { lock.lock(); line = text; lock.unlock() }
    func clear() { lock.lock(); line = nil; lock.unlock() }
}

// Splits a pipe into lines on a background thread. availableData blocks until
// data or end of file, which is how both macOS and Windows pipes behave.
enum LineReader {
    static func lines(_ handle: FileHandle) -> AsyncStream<String> {
        AsyncStream { continuation in
            Thread.detachNewThread {
                forEachLine(handle) { continuation.yield($0) }
                continuation.finish()
            }
        }
    }

    static func forEachLine(_ handle: FileHandle, _ body: (String) -> Void) {
        var buffer = Data()
        while true {
            let chunk = handle.availableData
            if chunk.isEmpty { break }
            buffer.append(chunk)
            while let newline = buffer.firstIndex(of: 0x0A) {
                var lineData = buffer[buffer.startIndex..<newline]
                if lineData.last == 0x0D { lineData = lineData.dropLast() }
                body(String(decoding: lineData, as: UTF8.self))
                buffer.removeSubrange(buffer.startIndex...newline)
            }
        }
        if !buffer.isEmpty { body(String(decoding: buffer, as: UTF8.self)) }
    }
}

// Writing to a pipe whose reader has exited raises SIGPIPE, which would end
// the whole app; ignored, the write throws instead.
enum IgnoreSigpipe {
    private static let done: Void = {
        #if !os(Windows)
        signal(SIGPIPE, SIG_IGN)
        #endif
    }()

    static func once() { _ = done }
}
