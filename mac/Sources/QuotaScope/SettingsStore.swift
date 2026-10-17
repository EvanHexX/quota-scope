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
        raw["MenuBarText"]?.stringValue ?? MenuBarText.highestWeekly
    }

    init() {
        load()
    }

    func load() {
        loadWarning = nil
        guard let data = try? Data(contentsOf: Self.file), let text = String(data: data, encoding: .utf8) else {
            settings = AppSettings()
            raw = JSONObject()
            return
        }
        do {
            let json = try JSONValue.parse(text)
            settings = try AppSettings.fromJSON(json)
            raw = json.objectValue ?? JSONObject()
        } catch {
            // A broken file is left untouched: defaults run, and nothing is
            // saved over it until the user fixes it.
            settings = AppSettings()
            raw = JSONObject()
            loadWarning = "settings.json could not be read (\(error)); using defaults."
        }
    }

    var timeDisplayMode: String {
        get { settings.timeDisplayMode }
        set { settings.timeDisplayMode = newValue; set("TimeDisplayMode", .string(newValue)) }
    }

    var isPinned: Bool {
        get { settings.isPinned }
        set { settings.isPinned = newValue; set("IsPinned", .bool(newValue)) }
    }

    private func set(_ key: String, _ value: JSONValue) {
        guard loadWarning == nil else { return }
        raw[key] = value
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
#endif
