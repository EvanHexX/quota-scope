#if os(macOS)
import AppKit

// Menu bar app: no Dock icon (LSUIElement in the bundle's Info.plist, and the
// accessory policy for a bare `swift run`).
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let controller = AppController()

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        controller.start()
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        Task {
            await controller.shutdown()
            NSApp.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }
}

let app = NSApplication.shared
let delegate = MainActor.assumeIsolated { AppDelegate() }
app.delegate = delegate
app.run()
#else
// The menu bar app is macOS only; QuotaCore and QuotaProviders still build
// and run their checks on Windows (docs/MACOS_BUILD.md, 7.3).
print("QuotaScope's menu bar app runs on macOS only.")
#endif
