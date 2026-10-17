// swift-tools-version: 5.9
import PackageDescription

// QuotaCore is the platform-neutral port of the C# provider, row, and alert
// logic (docs/MACOS_BUILD.md, section 4). It uses Foundation only, so it also
// builds on Windows, where quotascope-selftest checks it against the shared
// fixtures in ../fixtures.
//
// QuotaProviders runs the providers: the codex app-server client, the Claude
// usage poller, the session renewer, and command and credential lookup. The
// Keychain and app-bundle lookups are macOS-only (#if canImport); everything
// else builds on Windows too, where quotascope-providertest drives it against
// fakes (fake-codex-app-server, an in-memory transport and credentials).
//
// QuotaScope is the menu bar app (AppKit + SwiftUI). Its sources build only on
// macOS; elsewhere the target is a stub so `swift build` still works on
// Windows. mac/build.sh wraps it into QuotaScope.app.
let package = Package(
    name: "QuotaScope",
    platforms: [.macOS(.v13)],
    targets: [
        .target(name: "QuotaCore"),
        .target(name: "QuotaProviders", dependencies: ["QuotaCore"]),
        .executableTarget(name: "quotascope-selftest", dependencies: ["QuotaCore"]),
        .executableTarget(name: "quotascope-providertest", dependencies: ["QuotaCore", "QuotaProviders"]),
        .executableTarget(name: "fake-codex-app-server", dependencies: ["QuotaCore"]),
        .executableTarget(name: "QuotaScope", dependencies: ["QuotaCore", "QuotaProviders"]),
    ]
)
