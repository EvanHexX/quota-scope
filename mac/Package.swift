// swift-tools-version: 5.9
import PackageDescription

// QuotaCore is the platform-neutral port of the C# provider, row, and alert
// logic (docs/MACOS_BUILD.md, section 4). It uses Foundation only, so it also
// builds on Windows, where quotascope-selftest checks it against the shared
// fixtures in ../fixtures. The menu bar app and the providers (Keychain,
// processes, HTTP) are later targets that build on macOS only.
let package = Package(
    name: "QuotaScope",
    platforms: [.macOS(.v13)],
    targets: [
        .target(name: "QuotaCore"),
        .executableTarget(name: "quotascope-selftest", dependencies: ["QuotaCore"]),
    ]
)
