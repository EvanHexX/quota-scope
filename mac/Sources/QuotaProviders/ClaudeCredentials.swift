import Foundation
import QuotaCore
#if canImport(Security)
import Security
#endif

// Where Claude Code keeps its OAuth credentials (docs/MACOS_BUILD.md, 6.1).
// The access token is read for one request and dropped: never cached, copied,
// or logged. Claude Code owns refresh; nothing here writes the credentials.

public struct ClaudeCredentials: Sendable {
    public var accessToken: String?
    public var expiresAt: Date?

    public init(accessToken: String?, expiresAt: Date?) {
        self.accessToken = accessToken
        self.expiresAt = expiresAt
    }
}

public protocol ClaudeCredentialSource: AnyObject, Sendable {
    // Whether any credentials exist, without reading the secret.
    var exists: Bool { get }
    // Changes when Claude Code rewrites the credentials (sign-in, refresh),
    // without reading the secret. nil when there are none.
    func stamp() -> Date?
    // The secret, for immediate use.
    func read() -> ClaudeCredentials?
}

// The payload is the same JSON on both platforms:
// { "claudeAiOauth": { "accessToken": "...", "expiresAt": <epoch ms> } }.
// The same item also holds Claude Code's MCP connector tokens ("mcpOAuth"),
// and on a Mac where the terminal `claude` never signed in that is all it
// holds (seen 2026-10-17). That reads as "not signed in", not as unreadable.
enum ClaudeCredentialPayload {
    static func parse(_ data: Data) -> ClaudeCredentials? {
        guard let root = try? JSONValue.parse(String(decoding: data, as: UTF8.self)),
              root.objectValue != nil else { return nil }
        guard case .object(let oauth)? = root["claudeAiOauth"] else {
            return ClaudeCredentials(accessToken: nil, expiresAt: nil)
        }
        let token = oauth["accessToken"]?.stringValue
        let expiresAt = oauth["expiresAt"]?.int64Value.map { Timestamp.fromUnixMilliseconds($0).date }
        return ClaudeCredentials(accessToken: token, expiresAt: expiresAt)
    }
}

// ~/.claude/.credentials.json: what Windows uses, and what Claude Code falls
// back to on a Mac without a usable Keychain.
public final class FileCredentialSource: ClaudeCredentialSource {
    public let path: String

    public init(path: String) { self.path = path }

    public static func standard(home: String) -> FileCredentialSource {
        FileCredentialSource(path: "\(home)/.claude/.credentials.json")
    }

    public var exists: Bool { FileManager.default.fileExists(atPath: path) }

    public func stamp() -> Date? {
        (try? FileManager.default.attributesOfItem(atPath: path))?[.modificationDate] as? Date
    }

    public func read() -> ClaudeCredentials? {
        FileManager.default.contents(atPath: path).flatMap(ClaudeCredentialPayload.parse)
    }
}

#if canImport(Security)
// The login Keychain item Claude Code writes on macOS: a generic password with
// service "Claude Code-credentials" and an empty account, so it is looked up
// by service only (verified 2026-10-17).
//
// The secret is read through /usr/bin/security, the tool Claude Code itself
// uses to write the item (decision M15). A token refresh resets the item's
// partition list to apple-tool:, which dropped an "Always Allow" given to
// QuotaScope and asked again about every 8 hours (measured 2026-10-17); the
// tool is on the item's ACL and under apple-tool:, so reading through it asks
// nothing. The token only passes through a pipe to this process and is
// dropped after parsing, as before.
public final class KeychainCredentialSource: ClaudeCredentialSource {
    public static let service = "Claude Code-credentials"

    public init() {}

    private func query(returning extra: [CFString: Any]) -> [CFString: Any] {
        var query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: KeychainCredentialSource.service,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        for (key, value) in extra { query[key] = value }
        return query
    }

    // Attributes only: no secret, and (to be verified with the real app) no
    // prompt.
    private func attributes() -> [CFString: Any]? {
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query(returning: [kSecReturnAttributes: true]) as CFDictionary, &result)
        guard status == errSecSuccess else { return nil }
        return result as? [CFString: Any]
    }

    public var exists: Bool { attributes() != nil }

    public func stamp() -> Date? {
        attributes()?[kSecAttrModificationDate] as? Date
    }

    public static let securityTool = "/usr/bin/security"

    public func read() -> ClaudeCredentials? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: KeychainCredentialSource.securityTool)
        process.arguments = ["find-generic-password", "-s", KeychainCredentialSource.service, "-w"]
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        process.standardInput = FileHandle.nullDevice
        do { try process.run() } catch { return nil }
        // Read to the end before waiting, so a large payload cannot fill the
        // pipe and stall the tool.
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { return nil }
        return ClaudeCredentialPayload.parse(data)
    }
}
#endif

// The Keychain first, then the file: whichever Claude Code is using.
public final class PreferredCredentialSource: ClaudeCredentialSource {
    private let sources: [ClaudeCredentialSource]

    public init(_ sources: [ClaudeCredentialSource]) { self.sources = sources }

    public static func standard(home: String = FileManager.default.homeDirectoryForCurrentUser.path) -> PreferredCredentialSource {
        #if canImport(Security)
        return PreferredCredentialSource([KeychainCredentialSource(), FileCredentialSource.standard(home: home)])
        #else
        return PreferredCredentialSource([FileCredentialSource.standard(home: home)])
        #endif
    }

    public var exists: Bool { sources.contains { $0.exists } }

    public func stamp() -> Date? {
        for source in sources { if let stamp = source.stamp() { return stamp } }
        return nil
    }

    public func read() -> ClaudeCredentials? {
        for source in sources where source.exists { return source.read() }
        return nil
    }
}
