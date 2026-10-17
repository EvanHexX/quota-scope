#if os(macOS)
import Foundation
import QuotaCore

// settings.json under ~/Library/Application Support/QuotaScope (writing next
// to the binary would break the bundle's signature; docs/MACOS_BUILD.md 6.3).
// The file keeps the Windows schema. The raw object is kept so a save only
// rewrites the keys the app changed and leaves every other key, Windows-only
// or unknown, as it was.
final class SettingsStore {
    static let directory = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/QuotaScope", isDirectory: true)
    static let file = directory.appendingPathComponent("settings.json")

    private(set) var settings = AppSettings()
    private(set) var loadWarning: String?
    private var raw = JSONObject()

    // macOS-only key (3.1): "Off" | "HighestWeekly" | "HighestOverall" | a row key.
    var menuBarText: String {
        get { raw["MenuBarText"]?.stringValue ?? MenuBarText.highestWeekly }
        set { set("MenuBarText", .string(newValue)) }
    }

    init() {
        load()
    }

    func load() {
        loadWarning = nil
        guard let data = try? Data(contentsOf: Self.file), let text = String(data: data, encoding: .utf8) else {
            settings = Self.macDefaults(AppSettings(), raw: JSONObject())
            raw = JSONObject()
            return
        }
        do {
            let json = try JSONValue.parse(text)
            raw = json.objectValue ?? JSONObject()
            settings = Self.macDefaults(try AppSettings.fromJSON(json), raw: raw)
        } catch {
            // A broken file is left untouched: defaults run, and nothing is
            // saved over it until the user fixes it.
            settings = Self.macDefaults(AppSettings(), raw: JSONObject())
            raw = JSONObject()
            loadWarning = "settings.json could not be read (\(error)); using defaults."
        }
    }

    // Mac defaults that differ from the shared model, applied only when the
    // file does not set the key: remaining capacity, as the maintainer reads
    // the Windows app (2026-10-17). AppSettings.cs keeps "Used".
    private static func macDefaults(_ settings: AppSettings, raw: JSONObject) -> AppSettings {
        var settings = settings
        if raw["GaugeMetric"] == nil { settings.gaugeMetric = "Remaining" }
        return settings
    }

    var timeDisplayMode: String {
        get { settings.timeDisplayMode }
        set { update { $0.timeDisplayMode = newValue } }
    }

    var isPinned: Bool {
        get { settings.isPinned }
        set { update { $0.isPinned = newValue } }
    }

    // The settings window's one entry point: applies the change, then writes
    // only the keys whose value changed (Providers.<id>.<key> inside the
    // provider objects), so untouched keys keep their exact text.
    func update(_ change: (inout AppSettings) -> Void) {
        var next = settings
        change(&next)
        guard next != settings else { return }
        let previous = settings
        settings = next
        guard loadWarning == nil else { return }
        for field in SettingsFields.root where !field.equal(previous, next) {
            raw[field.key] = field.json(next, raw[field.key])
        }
        if previous.providers != next.providers {
            var providers = raw["Providers"]?.objectValue ?? JSONObject()
            for id in next.providers.keys.sorted() {
                let old = previous.providers[id]
                let new = next.provider(id)
                guard old != new else { continue }
                var object = providers[id]?.objectValue ?? JSONObject()
                for field in SettingsFields.provider where old.map({ !field.equal($0, new) }) ?? true {
                    object[field.key] = field.json(new, object[field.key])
                }
                providers[id] = .object(object)
            }
            raw["Providers"] = .object(providers)
        }
        write()
    }

    private func set(_ key: String, _ value: JSONValue) {
        guard loadWarning == nil else { return }
        raw[key] = value
        write()
    }

    private func write() {
        do {
            try FileManager.default.createDirectory(at: Self.directory, withIntermediateDirectories: true)
            try Data(JSONValue.object(raw).serialized().utf8).write(to: Self.file, options: .atomic)
        } catch {
            NSLog("QuotaScope: saving settings failed: \(error)")
        }
    }

    // Creates the file with the current values so "Open settings file" has
    // something to open on a first run.
    func ensureFileExists() {
        guard !FileManager.default.fileExists(atPath: Self.file.path), loadWarning == nil else { return }
        set("TimeDisplayMode", .string(settings.timeDisplayMode))
    }
}

// How each settings key the Mac app can change is written back, with the
// Windows key names. Maps keep the existing entries' order.
struct SettingsField<Model> {
    let key: String
    let equal: (Model, Model) -> Bool
    let json: (Model, JSONValue?) -> JSONValue

    init<Value: Equatable>(_ key: String, _ path: KeyPath<Model, Value>, _ encode: @escaping (Value, JSONValue?) -> JSONValue) {
        self.key = key
        equal = { $0[keyPath: path] == $1[keyPath: path] }
        json = { encode($0[keyPath: path], $1) }
    }

    init(_ key: String, _ path: KeyPath<Model, String>) { self.init(key, path) { value, _ in .string(value) } }
    init(_ key: String, _ path: KeyPath<Model, Bool>) { self.init(key, path) { value, _ in .bool(value) } }
    init(_ key: String, _ path: KeyPath<Model, Int>) { self.init(key, path) { value, _ in .number(Int64(value)) } }
    init(_ key: String, _ path: KeyPath<Model, Double>) { self.init(key, path) { value, _ in .number(value) } }
    init(_ key: String, _ path: KeyPath<Model, [Double]>) { self.init(key, path) { value, _ in .array(value.map { .number($0) }) } }

    init<Value: Equatable>(_ key: String, _ path: KeyPath<Model, [String: Value]>, _ encode: @escaping (Value) -> JSONValue) {
        self.init(key, path) { (map: [String: Value], existing: JSONValue?) in
            var object = existing?.objectValue ?? JSONObject()
            for key in object.keys where map[key] == nil { object[key] = nil }
            for key in map.keys.sorted() { object[key] = encode(map[key]!) }
            return .object(object)
        }
    }
}

enum SettingsFields {
    static let root: [SettingsField<AppSettings>] = [
        .init("WarningThresholdPercent", \.warningThresholdPercent),
        .init("CriticalThresholdPercent", \.criticalThresholdPercent),
        .init("AlertPoints", \.alertPoints),
        .init("RowAlerts", \.rowAlerts) { .bool($0) },
        .init("RowAlertPoints", \.rowAlertPoints) { .array($0.map { .number($0) }) },
        .init("FollowSystemTheme", \.followSystemTheme),
        .init("ThemeOverride", \.themeOverride),
        .init("Autostart", \.autostart),
        .init("RowOrder", \.rowOrder) { .number(Int64($0)) },
        .init("RowVisibility", \.rowVisibility) { .bool($0) },
        .init("GaugeMetric", \.gaugeMetric),
        .init("Language", \.language),
        .init("NotifyOnThreshold", \.notifyOnThreshold),
        .init("TimeDisplayMode", \.timeDisplayMode),
        .init("IsPinned", \.isPinned),
        .init("Hotkey", \.hotkey),
        .init("HotkeyRefreshAll", \.hotkeyRefreshAll),
        .init("HotkeyTogglePin", \.hotkeyTogglePin),
    ]

    static let provider: [SettingsField<ProviderSettings>] = [
        .init("Enabled", \.enabled),
        .init("RefreshSeconds", \.refreshSeconds),
        .init("Command", \.command),
        .init("AutoRenewSession", \.autoRenewSession),
        .init("CreditsFullAmount", \.creditsFullAmount),
    ]
}
#endif
