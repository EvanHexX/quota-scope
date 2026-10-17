import Foundation
import QuotaCore
#if canImport(AppKit)
import AppKit
#endif

// Finding `codex` and `claude` from a GUI app (docs/MACOS_BUILD.md, 6.2).
// Apps launched from Finder or at login do not inherit the shell PATH, and on
// macOS the Codex CLI usually lives inside ChatGPT.app rather than on PATH.

public struct CommandSpec: Equatable, Sendable {
    public enum Source: String, Sendable {
        case custom
        case appBundle
        case path
    }

    public var executable: String
    public var arguments: [String]
    public var source: Source
    // The bundled CLI's version (codex-package.json), shown in Settings.
    public var version: String?
    // Where the app bundle is, for a CLI that came from one.
    public var bundlePath: String?

    public init(executable: String, arguments: [String], source: Source, version: String? = nil, bundlePath: String? = nil) {
        self.executable = executable
        self.arguments = arguments
        self.source = source
        self.version = version
        self.bundlePath = bundlePath
    }

    public var displayText: String { ([executable] + arguments).joined(separator: " ") }
}

// What the resolvers look at, injectable so the lookup can be tested without
// a real ChatGPT.app or login shell.
public struct CommandEnvironment: Sendable {
    public var home: String
    public var processPath: String?
    // The login shell's PATH, resolved at most once per app run.
    public var loginShellPath: @Sendable () -> String?
    // App bundles registered under a bundle ID (NSWorkspace on macOS).
    public var applicationsWithBundleID: @Sendable (String) -> [String]
    public var isExecutable: @Sendable (String) -> Bool

    public init(home: String, processPath: String?, loginShellPath: @escaping @Sendable () -> String?,
                applicationsWithBundleID: @escaping @Sendable (String) -> [String],
                isExecutable: @escaping @Sendable (String) -> Bool) {
        self.home = home
        self.processPath = processPath
        self.loginShellPath = loginShellPath
        self.applicationsWithBundleID = applicationsWithBundleID
        self.isExecutable = isExecutable
    }

    public static let current: CommandEnvironment = {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let shellPath = LoginShellPath()
        return CommandEnvironment(
            home: home,
            processPath: ProcessInfo.processInfo.environment["PATH"],
            loginShellPath: { shellPath.value },
            applicationsWithBundleID: { bundleID in
                #if canImport(AppKit)
                return NSWorkspace.shared.urlsForApplications(withBundleIdentifier: bundleID).map(\.path)
                #else
                return []
                #endif
            },
            isExecutable: { FileManager.default.isExecutableFile(atPath: $0) })
    }()

    // Directories searched before the login shell is asked.
    var knownDirectories: [String] {
        var directories = [
            "\(home)/.local/bin", "/opt/homebrew/bin", "/usr/local/bin",
            "\(home)/.npm-global/bin", "\(home)/.bun/bin",
        ]
        if let nvm = newestNvmBin() { directories.append(nvm) }
        return directories
    }

    private func newestNvmBin() -> String? {
        let root = "\(home)/.nvm/versions/node"
        guard let versions = try? FileManager.default.contentsOfDirectory(atPath: root) else { return nil }
        guard let newest = versions.max(by: { $0.compare($1, options: .numeric) == .orderedAscending }) else { return nil }
        return "\(root)/\(newest)/bin"
    }

    // PATH for child processes: the login shell's, else the process's, with
    // the known directories added so node-based shims find node.
    public var childPath: String {
        var parts: [String] = []
        for entry in (loginShellPath() ?? processPath ?? "").split(separator: ":").map(String.init) + knownDirectories
        where !entry.isEmpty && !parts.contains(entry) {
            parts.append(entry)
        }
        return parts.joined(separator: ":")
    }

    // A bare command name searched in the known directories, then in the
    // login shell's PATH.
    func findOnPath(_ name: String) -> String? {
        for directory in knownDirectories {
            let candidate = "\(directory)/\(name)"
            if isExecutable(candidate) { return candidate }
        }
        for directory in (loginShellPath() ?? processPath ?? "").split(separator: ":").map(String.init) where !directory.isEmpty {
            let candidate = "\(directory)/\(name)"
            if isExecutable(candidate) { return candidate }
        }
        return nil
    }
}

// Asks the user's login shell for its PATH once, with a timeout, and keeps the
// answer (the PATH only, not the rest of the environment).
final class LoginShellPath: @unchecked Sendable {
    private let lock = NSLock()
    private var resolved = false
    private var cached: String?

    var value: String? {
        lock.lock()
        defer { lock.unlock() }
        if !resolved {
            cached = LoginShellPath.query()
            resolved = true
        }
        return cached
    }

    private static func query() -> String? {
        #if os(Windows)
        return nil
        #else
        let shell = ProcessInfo.processInfo.environment["SHELL"].flatMap { $0.isEmpty ? nil : $0 } ?? "/bin/zsh"
        let process = Process()
        process.executableURL = URL(fileURLWithPath: shell)
        process.arguments = ["-l", "-c", "printf %s \"$PATH\""]
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        process.standardInput = FileHandle.nullDevice
        let done = DispatchSemaphore(value: 0)
        process.terminationHandler = { _ in done.signal() }
        do { try process.run() } catch { return nil }
        if done.wait(timeout: .now() + 5) == .timedOut {
            process.terminate()
            return nil
        }
        let text = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        return text.isEmpty ? nil : text
        #endif
    }
}

public enum CodexCommandResolver {
    // The new ChatGPT.app keeps Codex's bundle ID; ChatGPT Classic
    // (com.openai.chat) has no Codex and is never used.
    public static let bundleID = "com.openai.codex"
    static let bundledCLI = "Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex"
    static let bundledPackage = "Contents/Resources/codex-cli/codex-package.json"

    public static func resolve(_ configuredCommand: String?, environment: CommandEnvironment = .current) -> CommandSpec? {
        let command = (configuredCommand ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        let name = command.isEmpty ? "codex" : command
        if isAbsolute(name) {
            return CommandSpec(executable: name, arguments: ["app-server"], source: .custom)
        }
        if name != "codex" {
            // A custom command name: found the same way as a bare `codex`.
            return environment.findOnPath(name).map { CommandSpec(executable: $0, arguments: ["app-server"], source: .custom) }
        }
        if let bundled = bundledCLI(environment) { return bundled }
        return environment.findOnPath("codex").map { CommandSpec(executable: $0, arguments: ["app-server"], source: .path) }
    }

    // Apps registered under the bundle ID plus the usual places, checked by
    // their Info.plist rather than their name. The highest app version wins.
    static func bundledCLI(_ environment: CommandEnvironment) -> CommandSpec? {
        var candidates = environment.applicationsWithBundleID(bundleID)
        for path in ["/Applications/ChatGPT.app", "\(environment.home)/Applications/ChatGPT.app",
                     "/Applications/Codex.app", "\(environment.home)/Applications/Codex.app"]
        where !candidates.contains(path) {
            candidates.append(path)
        }

        var best: (spec: CommandSpec, version: String)?
        for app in candidates {
            guard let info = readPlist("\(app)/Contents/Info.plist"),
                  info["CFBundleIdentifier"] as? String == bundleID else { continue }
            let cli = "\(app)/\(bundledCLI)"
            guard environment.isExecutable(cli) else { continue }
            let appVersion = info["CFBundleShortVersionString"] as? String ?? "0"
            let spec = CommandSpec(executable: cli, arguments: ["app-server"], source: .appBundle,
                                   version: bundledVersion(app), bundlePath: app)
            if best == nil || appVersion.compare(best!.version, options: .numeric) == .orderedDescending {
                best = (spec, appVersion)
            }
        }
        return best?.spec
    }

    // The CLI's own version lives in codex-package.json, not in an Info.plist.
    static func bundledVersion(_ app: String) -> String? {
        guard let data = FileManager.default.contents(atPath: "\(app)/\(bundledPackage)"),
              let json = try? JSONValue.parse(String(decoding: data, as: UTF8.self)) else { return nil }
        return json["version"]?.stringValue
    }

    // Changes when an update (Sparkle replaces the whole bundle) brings a new
    // CLI: the package version, or the binary's inode. The client restarts its
    // app-server when it changes.
    public static func cliStamp(_ spec: CommandSpec) -> String? {
        guard spec.source == .appBundle, let app = spec.bundlePath else { return nil }
        let inode = (try? FileManager.default.attributesOfItem(atPath: spec.executable)[.systemFileNumber]).map { "\($0)" } ?? "?"
        return "\(bundledVersion(app) ?? "?")|\(inode)"
    }

    private static func readPlist(_ path: String) -> [String: Any]? {
        guard let data = FileManager.default.contents(atPath: path) else { return nil }
        return (try? PropertyListSerialization.propertyList(from: data, options: [], format: nil)) as? [String: Any]
    }

    static func isAbsolute(_ path: String) -> Bool {
        if path.hasPrefix("/") { return true }
        #if os(Windows)
        let chars = Array(path)
        return chars.count >= 3 && chars[1] == ":" && (chars[2] == "\\" || chars[2] == "/")
        #else
        return false
        #endif
    }
}

public enum ClaudeCommandResolver {
    // `claude` with the given arguments, or nil when it cannot be found; the
    // renewer then counts the attempt as failed.
    public static func resolve(_ arguments: [String], environment: CommandEnvironment = .current) -> CommandSpec? {
        environment.findOnPath("claude").map { CommandSpec(executable: $0, arguments: arguments, source: .path) }
    }
}
