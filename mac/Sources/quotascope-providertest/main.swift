import Foundation
import QuotaCore
import QuotaProviders

// Drives QuotaProviders against fakes, so the provider behavior in
// docs/MACOS_BUILD.md 4.2 is checked without a real CLI, account, Keychain, or
// network. Builds and runs on Windows and macOS:
//   swift build && swift run quotascope-providertest
// The Codex half starts fake-codex-app-server, which must sit next to this
// executable (swift build puts both in the same directory).

var failures = 0
var passed = 0

func check(_ condition: Bool, _ message: String) {
    if condition { passed += 1 } else { failures += 1; FileHandle.standardError.write(Data("FAIL: \(message)\n".utf8)) }
}

func waitUntil(_ seconds: Double, _ condition: () -> Bool) async -> Bool {
    let deadline = Date().addingTimeInterval(seconds)
    while Date() < deadline {
        if condition() { return true }
        try? await Task.sleep(nanoseconds: 50_000_000)
    }
    return condition()
}

final class Box<T>: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: T
    init(_ value: T) { stored = value }
    var value: T {
        get { lock.lock(); defer { lock.unlock() }; return stored }
        set { lock.lock(); stored = newValue; lock.unlock() }
    }
}

// MARK: Codex

let fakeServer: String = {
    let directory = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent()
    #if os(Windows)
    return directory.appendingPathComponent("fake-codex-app-server.exe").path
    #else
    return directory.appendingPathComponent("fake-codex-app-server").path
    #endif
}()

let testEnvironment = CommandEnvironment(
    home: FileManager.default.temporaryDirectory.path,
    processPath: ProcessInfo.processInfo.environment["PATH"],
    loginShellPath: { nil },
    applicationsWithBundleID: { _ in [] },
    isExecutable: { FileManager.default.isExecutableFile(atPath: $0) })

func setFakeMode(_ mode: String) {
    #if os(Windows)
    _ = mode.withCString { value in "FAKE_CODEX_MODE".withCString { name in _putenv_s(name, value) } }
    #else
    setenv("FAKE_CODEX_MODE", mode, 1)
    #endif
}

func codexTests() async {
    check(FileManager.default.fileExists(atPath: fakeServer), "fake server built at \(fakeServer)")

    setFakeMode("normal")
    var settings = ProviderSettings()
    settings.command = fakeServer
    let provider = CodexUsageProvider(settings: settings, environment: testEnvironment)
    let pushed = Box<ProviderUsage?>(nil)
    await provider.setUpdateHandler { pushed.value = $0 }

    // A read goes through initialize, initialized (the fake drops reads
    // before it), and account/rateLimits/read, ignoring the notifications the
    // fake sends along the way.
    do {
        let usage = try await provider.read()
        check(usage.rows.map(\.label) == ["5h", "7d", "Credits"], "codex read rows: \(usage.rows.map(\.label))")
        check(usage.rows.first?.window?.usedPercent == 20, "codex 5h used 20")
        check(usage.overallUsedPercent == 30, "codex overall 30")
        check(usage.state == .ok && usage.statusText == "Codex rate limit", "codex status")
    } catch {
        check(false, "codex read threw \(error)")
    }

    // The rolling update right behind the read is merged into it, not mapped
    // alone: the 5h row moves, the weekly row and credits stay.
    let gotUpdate = await waitUntil(5) { pushed.value != nil }
    check(gotUpdate, "codex rolling update pushed")
    if let update = pushed.value {
        check(update.rows.map(\.label) == ["5h", "7d", "Credits"], "codex update keeps rows: \(update.rows.map(\.label))")
        check(update.rows.first?.window?.usedPercent == 25, "codex update 5h used 25")
        check(update.rows.first?.window?.resetsAt == Timestamp.fromUnixSeconds(1893499200), "codex update keeps 5h reset time")
        check(update.rows[1].window?.usedPercent == 30, "codex update keeps weekly")
    }

    // Reconnect starts a new process; the old one is gone.
    do {
        let usage = try await provider.reconnect()
        check(usage.rows.count == 3, "codex reconnect reads again")
    } catch {
        check(false, "codex reconnect threw \(error)")
    }

    let running = await provider.client.isRunning
    check(running, "codex app-server running before close")
    await provider.close()
    let stopped = await provider.client.isRunning
    check(!stopped, "codex app-server stopped after close")

    // A process that dies mid-request fails the request at once with its
    // stderr line, instead of waiting out the timeout.
    setFakeMode("crash")
    let crashing = CodexUsageProvider(settings: settings, environment: testEnvironment)
    let started = Date()
    do {
        _ = try await crashing.read()
        check(false, "codex crash should throw")
    } catch {
        let text = "\(error)"
        check(text.contains("account/rateLimits/read") && text.contains("simulated failure"), "codex crash error: \(text)")
        check(Date().timeIntervalSince(started) < 10, "codex crash fails fast")
    }
    await crashing.close()
    setFakeMode("normal")

    // A command that cannot start says so.
    var missing = ProviderSettings()
    missing.command = FileManager.default.temporaryDirectory.appendingPathComponent("no-such-codex").path
    let absent = CodexUsageProvider(settings: missing, environment: testEnvironment)
    do {
        _ = try await absent.read()
        check(false, "missing codex should throw")
    } catch {
        check("\(error)".contains("failed to start"), "missing codex error: \(error)")
    }
    await absent.close()

    // Bundle lookup: the bundle ID decides, not the app name.
    let apps = FileManager.default.temporaryDirectory.appendingPathComponent("qs-apps-\(UUID().uuidString)")
    func makeApp(_ name: String, bundleID: String, version: String, cliVersion: String) {
        let app = apps.appendingPathComponent(name)
        let cli = app.appendingPathComponent("Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS")
        try? FileManager.default.createDirectory(at: cli, withIntermediateDirectories: true)
        _ = FileManager.default.createFile(atPath: cli.appendingPathComponent("codex").path, contents: Data())
        let plist: [String: Any] = ["CFBundleIdentifier": bundleID, "CFBundleShortVersionString": version]
        let data = try! PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        _ = FileManager.default.createFile(atPath: app.appendingPathComponent("Contents/Info.plist").path, contents: data)
        _ = FileManager.default.createFile(atPath: app.appendingPathComponent("Contents/Resources/codex-cli/codex-package.json").path,
                                       contents: Data("{\"version\":\"\(cliVersion)\"}".utf8))
    }
    makeApp("ChatGPT Classic.app", bundleID: "com.openai.chat", version: "99.0", cliVersion: "9.9.9")
    makeApp("ChatGPT.app", bundleID: "com.openai.codex", version: "26.1007.71326", cliVersion: "0.162.0-alpha.17.2")
    makeApp("Codex.app", bundleID: "com.openai.codex", version: "26.900.1", cliVersion: "0.150.0")
    let bundleEnvironment = CommandEnvironment(
        home: apps.path, processPath: nil, loginShellPath: { nil },
        applicationsWithBundleID: { _ in [apps.appendingPathComponent("ChatGPT Classic.app").path,
                                          apps.appendingPathComponent("Codex.app").path,
                                          apps.appendingPathComponent("ChatGPT.app").path] },
        isExecutable: { FileManager.default.fileExists(atPath: $0) })
    let spec = CodexCommandResolver.resolve(nil, environment: bundleEnvironment)
    check(spec?.source == .appBundle, "bundled codex found")
    check(spec?.bundlePath?.hasSuffix("ChatGPT.app") == true, "highest com.openai.codex app wins, Classic skipped: \(spec?.bundlePath ?? "nil")")
    check(spec?.version == "0.162.0-alpha.17.2", "bundled CLI version from codex-package.json: \(spec?.version ?? "nil")")
    check(spec?.arguments == ["app-server"], "bundled codex runs app-server")
    if let spec {
        let before = CodexCommandResolver.cliStamp(spec)
        _ = FileManager.default.createFile(atPath: apps.appendingPathComponent("ChatGPT.app/Contents/Resources/codex-cli/codex-package.json").path,
                                       contents: Data("{\"version\":\"0.163.0\"}".utf8))
        check(before != nil && CodexCommandResolver.cliStamp(spec) != before, "CLI stamp changes when the bundled version changes")
    }
    try? FileManager.default.removeItem(at: apps)
}

// MARK: Claude

final class FakeCredentials: ClaudeCredentialSource, @unchecked Sendable {
    let token = Box<String?>("test-token")
    let expiresAt = Box<Date?>(nil)
    let changed = Box<Date?>(Date(timeIntervalSince1970: 1_000))
    let reads = Box(0)
    // The Keychain prompt denied (or not answered): the item exists, the read fails.
    let denied = Box(false)

    var exists: Bool { changed.value != nil }
    func stamp() -> Date? { changed.value }
    func read() -> ClaudeCredentials? {
        reads.value += 1
        guard exists, !denied.value else { return nil }
        return ClaudeCredentials(accessToken: token.value, expiresAt: expiresAt.value)
    }
}

final class FakeTransport: ClaudeUsageTransport, @unchecked Sendable {
    enum Reply { case status(Int, String), failure }
    let replies = Box<[Reply]>([])
    let requests = Box<[[String: String]]>([])

    func get(_ url: URL, headers: [String: String]) async throws -> (status: Int, body: Data) {
        requests.value.append(headers)
        var queue = replies.value
        guard !queue.isEmpty else { throw ProviderError("no reply queued") }
        let reply = queue.removeFirst()
        replies.value = queue
        switch reply {
        case .status(let code, let body): return (code, Data(body.utf8))
        case .failure: throw ProviderError("network down")
        }
    }
}

let usageBody = """
{ "five_hour": { "utilization": 33.0, "resets_at": "2026-04-11T07:00:00+00:00" },
  "seven_day": { "utilization": 13.0, "resets_at": "2026-04-17T00:59:59+00:00" },
  "extra_usage": { "is_enabled": false, "monthly_limit": null, "used_credits": null, "utilization": null } }
"""

func claudeTests() async {
    let clock = Box(Date(timeIntervalSince1970: 1_900_000_000))
    let credentials = FakeCredentials()
    let transport = FakeTransport()
    let runs = Box(0)
    let renewBumpsStamp = Box(false)
    let renewer = ClaudeSessionRenewer(
        credentials: credentials, now: { clock.value },
        runner: { _ in
            runs.value += 1
            if renewBumpsStamp.value { credentials.changed.value = clock.value }
            return true
        },
        schedule: { $0() })
    var settings = ProviderSettings()
    settings.refreshSeconds = 30
    let provider = ClaudeUsageProvider(settings: settings, credentials: credentials, transport: transport,
                                       renewer: renewer, now: { clock.value }, watchChanges: false)

    // A good response, with the headers the endpoint needs.
    transport.replies.value = [.status(200, usageBody)]
    var usage = try! await provider.read()
    check(usage.state == .ok && usage.statusText == "Claude rate limit", "claude ok status: \(usage.statusText)")
    check(usage.rows.map(\.label) == ["5h", "7d", "Credits"], "claude rows")
    let headers = transport.requests.value.last ?? [:]
    check(headers["Authorization"] == "Bearer test-token", "claude bearer header")
    check(headers["anthropic-beta"] == "oauth-2025-04-20" && headers["User-Agent"] == "claude-code/2.0.0", "claude beta and user agent headers")

    // Within the poll floor (60 s, even with 30 configured) the cache answers.
    clock.value += 45
    usage = try! await provider.read()
    check(transport.requests.value.count == 1, "claude caches within 60 s")

    // 429 backs off 1, 5, then 15 minutes, keeping the last good rows.
    clock.value += 30
    transport.replies.value = [.status(429, "")]
    usage = try! await provider.read()
    check(usage.state == .rateLimited && usage.statusText == "Rate limited. Retrying in 1m", "claude 429: \(usage.statusText)")
    check(usage.rows.count == 3, "claude 429 keeps last rows")
    clock.value += 61
    transport.replies.value = [.status(429, "")]
    usage = try! await provider.read()
    check(usage.statusText == "Rate limited. Retrying in 5m", "claude second 429: \(usage.statusText)")

    // A network failure keeps the last good data, marked stale.
    clock.value += 301
    transport.replies.value = [.failure]
    usage = try! await provider.read()
    check(usage.state == .stale && usage.statusText.hasPrefix("Stale - last "), "claude stale: \(usage.statusText)")
    check(usage.rows.count == 3, "claude stale keeps rows")

    // 401 pauses polling and asks Claude Code to renew (recovery).
    clock.value += 61
    transport.replies.value = [.status(401, "")]
    usage = try! await provider.read()
    check(usage.state == .unauthenticated && usage.statusText == "Claude session expired. Run Claude Code and sign in",
          "claude 401: \(usage.statusText)")
    check(runs.value == 1, "claude 401 nudges the renewer: \(runs.value)")

    // While paused and nothing rewrote the credentials, no request goes out,
    // and a renewer that just failed is not retried before its cooldown.
    let before = transport.requests.value.count
    clock.value += 61
    usage = try! await provider.read()
    check(transport.requests.value.count == before && usage.state == .unauthenticated, "claude stays paused")
    check(runs.value == 1, "claude renewer waits out its cooldown: \(runs.value)")

    // Claude Code rewrote the credentials: polling resumes.
    credentials.changed.value = clock.value
    transport.replies.value = [.status(200, usageBody)]
    usage = try! await provider.read()
    check(usage.state == .ok, "claude resumes after a credentials change")

    // Close to expiry, a proactive nudge goes out; once it lands the status
    // counts down with the auto-renew lead time.
    clock.value += 61
    credentials.expiresAt.value = clock.value.addingTimeInterval(12 * 60)
    transport.replies.value = [.status(200, usageBody)]
    renewBumpsStamp.value = false
    let runsBefore = runs.value
    usage = try! await provider.reconnect()
    check(runs.value == runsBefore + 1, "claude proactive renewal")
    check(usage.statusText == "Session expires in 12m", "claude expiry countdown: \(usage.statusText)")

    // A proactive no-op retries after a flat 2 minutes, not sooner.
    check(renewer.requestRenewal(recovery: false) == false, "proactive no-op waits 2 minutes")
    clock.value += 121
    check(renewer.requestRenewal(recovery: false) == true, "proactive retry after 2 minutes")

    // Credentials that exist but cannot be read (Keychain prompt denied) are
    // not a sign-out: no renewal, no auth pause, and the next poll retries.
    credentials.denied.value = true
    let runsBeforeDenied = runs.value
    clock.value += 61
    usage = try! await provider.read()
    check(usage.state == .unavailable && usage.statusText == ClaudeUsageProvider.unreadableStatus,
          "claude unreadable credentials: \(usage.state) \(usage.statusText)")
    check(runs.value == runsBeforeDenied, "claude unreadable credentials do not nudge the renewer")
    credentials.denied.value = false
    clock.value += 61
    transport.replies.value = [.status(200, usageBody)]
    usage = try! await provider.read()
    check(usage.state == .ok, "claude recovers once the Keychain allows the read: \(usage.statusText)")

    // A poll answered from the cache does not read the secret: on macOS
    // every read is a Keychain access that can prompt.
    let readsBefore = credentials.reads.value
    _ = try! await provider.read()
    check(credentials.reads.value == readsBefore, "claude cached poll reads no credentials: \(credentials.reads.value - readsBefore)")

    // Signed out entirely: the sign-in hint without "expired".
    credentials.changed.value = nil
    usage = try! await provider.reconnect()
    check(usage.state == .unauthenticated && usage.statusText == "Run Claude Code and sign in", "claude signed out: \(usage.statusText)")

    await provider.close()
    check(renewer.requestRenewal(recovery: true) == false, "renewer stops after close")
}

await codexTests()
await claudeTests()
if failures > 0 {
    FileHandle.standardError.write(Data("\(failures) failed, \(passed) passed\n".utf8))
    exit(1)
}
print("\(passed) checks ok")
