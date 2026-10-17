#if os(macOS)
import AppKit
import UserNotifications

// The Windows tray balloon (TrayController.ShowNotification) as a macOS
// notification. Permission is asked once, the first time notifications are on;
// a denied permission is shown in the Alerts tab with a way to System Settings.
enum NotificationPermission {
    case unknown
    case allowed
    case denied
    // Not running from an app bundle (swift run): the center needs one.
    case unavailable
}

@MainActor
final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    private(set) var permission: NotificationPermission = .unknown {
        didSet { if permission != oldValue { onPermissionChange(permission) } }
    }
    var onPermissionChange: (NotificationPermission) -> Void = { _ in }
    // A click on a notification.
    var onActivate: () -> Void = {}

    private let center: UNUserNotificationCenter?
    private var asking = false

    override init() {
        // UNUserNotificationCenter.current() raises outside a bundle.
        center = Bundle.main.bundleIdentifier == nil ? nil : UNUserNotificationCenter.current()
        super.init()
        if let center {
            center.delegate = self
        } else {
            permission = .unavailable
        }
    }

    // Reads the system's answer; asks only while it has never been asked.
    func prepare() {
        guard let center else { return }
        center.getNotificationSettings { settings in
            let status = settings.authorizationStatus
            Task { @MainActor in
                self.apply(status)
                if status == .notDetermined { self.ask() }
            }
        }
    }

    func show(title: String, body: String) {
        guard let center else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        center.add(request) { error in
            if let error { NSLog("QuotaScope: notification not shown: \(error.localizedDescription)") }
        }
    }

    func openSystemSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension") {
            NSWorkspace.shared.open(url)
        }
    }

    private func ask() {
        guard let center, !asking else { return }
        asking = true
        center.requestAuthorization(options: [.alert, .sound]) { granted, _ in
            Task { @MainActor in
                self.asking = false
                self.permission = granted ? .allowed : .denied
            }
        }
    }

    private func apply(_ status: UNAuthorizationStatus) {
        switch status {
        case .notDetermined: permission = .unknown
        case .denied: permission = .denied
        default: permission = .allowed
        }
    }

    // A menu bar app counts as frontmost while its popover is open; without
    // this the notification would be dropped then.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter,
                                            willPresent notification: UNNotification,
                                            withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list, .sound])
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter,
                                            didReceive response: UNNotificationResponse,
                                            withCompletionHandler completionHandler: @escaping () -> Void) {
        Task { @MainActor in self.onActivate() }
        completionHandler()
    }
}
#endif
