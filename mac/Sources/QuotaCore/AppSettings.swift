import Foundation

// Port of the settings model in app/AppSettings.cs: the same keys, the same
// defaults, and the same JSON shape, so a settings.json reads the same on
// both platforms (docs/MACOS_BUILD.md, section 6.3). Loading follows
// System.Text.Json's defaults on the C# side: property names match exactly,
// a missing key keeps its default, and a value of the wrong type fails the
// whole load.
//
// Dictionary keys are compared exactly. The C# defaults are declared
// case-insensitive, but System.Text.Json replaces them with plain
// dictionaries on load, so exact-case keys ("codex|7d") are the real contract.

public struct ProviderSettings: Equatable {
    public var enabled = true
    public var refreshSeconds = 60
    public var showSecondaryRows = false
    public var showCredits = false
    public var command = "codex"
    // Claude only: nudge Claude Code to refresh its own OAuth token.
    public var autoRenewSession = true
    // Neither provider reports a credit ceiling, so the credits gauge is drawn
    // against this. See CreditsGauge.
    public var creditsFullAmount = 2500.0

    public init() {}
}

public struct AppSettings: Equatable {
    public var hotkey = "Ctrl+Alt+U"
    public var hotkeyRefreshAll = ""
    public var hotkeyTogglePin = ""
    // Alert levels and points are remaining percentages.
    public var warningThresholdPercent = 20
    public var criticalThresholdPercent = 2.5
    // Empty means the warning and critical levels.
    public var alertPoints: [Double] = []
    // Per-row overrides keyed "<providerId>|<row label>".
    public var rowAlerts: [String: Bool] = [:]
    public var rowAlertPoints: [String: [Double]] = [:]
    public var followSystemTheme = true
    public var themeOverride = "Dark"
    public var autostart = false
    // Used when shapeTheme is "MixMatch": "Circle" | "Bars" | "Fill".
    public var rowShapes: [String: String] = [:]
    // "Auto" | "OneColumn" | "TwoColumns".
    public var layoutColumns = "Auto"
    public var rowOrder: [String: Int] = [:]
    public var rowVisibility: [String: Bool] = [:]
    public var trayIconStyle = "UsageArc"
    public var gaugeMetric = "Used"
    public var language = "System"
    public var notifyOnThreshold = true
    // Windows-only; kept so the model stays one schema.
    public var uiScale = 1.0
    public var glassmorphism = false
    public var glassStrength = "Medium"
    public var popupGraph = "half-circle"
    public var popupPosition = "BottomRight"
    public var lastPopupX = Int(Int32.min)
    public var lastPopupY = Int(Int32.min)
    // "Bars" | "BentoCircles" | "Fill" | "MixMatch".
    public var shapeTheme = "Bars"
    public var colorTheme = "DarkBluePurple"
    public var timeDisplayMode = "ClockTime"
    public var isPinned = false
    public var providers: [String: ProviderSettings] = ["codex": ProviderSettings()]

    public init() {}

    // GetProvider without the side effect: an unknown provider reads as defaults.
    public func provider(_ id: String) -> ProviderSettings {
        providers[id] ?? ProviderSettings()
    }

    public enum LoadError: Error, CustomStringConvertible {
        case wrongType(String)
        public var description: String {
            switch self { case .wrongType(let key): return "settings key \(key) has the wrong type" }
        }
    }

    // AppSettings.FromJson.
    public static func fromJSON(_ json: JSONValue) throws -> AppSettings {
        var settings = AppSettings()
        guard case .object(let root) = json else {
            if json.isNull { return settings }
            throw LoadError.wrongType("(root)")
        }
        let reader = SettingsReader(root)
        try reader.read("Hotkey", into: &settings.hotkey)
        try reader.read("HotkeyRefreshAll", into: &settings.hotkeyRefreshAll)
        try reader.read("HotkeyTogglePin", into: &settings.hotkeyTogglePin)
        try reader.read("WarningThresholdPercent", into: &settings.warningThresholdPercent)
        try reader.read("CriticalThresholdPercent", into: &settings.criticalThresholdPercent)
        try reader.read("AlertPoints", into: &settings.alertPoints)
        try reader.read("RowAlerts", into: &settings.rowAlerts)
        try reader.read("RowAlertPoints", into: &settings.rowAlertPoints)
        try reader.read("FollowSystemTheme", into: &settings.followSystemTheme)
        try reader.read("ThemeOverride", into: &settings.themeOverride)
        try reader.read("Autostart", into: &settings.autostart)
        try reader.read("RowShapes", into: &settings.rowShapes)
        try reader.read("LayoutColumns", into: &settings.layoutColumns)
        try reader.read("RowOrder", into: &settings.rowOrder)
        try reader.read("RowVisibility", into: &settings.rowVisibility)
        try reader.read("TrayIconStyle", into: &settings.trayIconStyle)
        try reader.read("GaugeMetric", into: &settings.gaugeMetric)
        try reader.read("Language", into: &settings.language)
        try reader.read("NotifyOnThreshold", into: &settings.notifyOnThreshold)
        try reader.read("UiScale", into: &settings.uiScale)
        try reader.read("Glassmorphism", into: &settings.glassmorphism)
        try reader.read("GlassStrength", into: &settings.glassStrength)
        try reader.read("PopupGraph", into: &settings.popupGraph)
        try reader.read("PopupPosition", into: &settings.popupPosition)
        try reader.read("LastPopupX", into: &settings.lastPopupX)
        try reader.read("LastPopupY", into: &settings.lastPopupY)
        try reader.read("ShapeTheme", into: &settings.shapeTheme)
        try reader.read("ColorTheme", into: &settings.colorTheme)
        try reader.read("TimeDisplayMode", into: &settings.timeDisplayMode)
        try reader.read("IsPinned", into: &settings.isPinned)

        if let providers = root["Providers"] {
            switch providers {
            case .null:
                settings.providers = [:]
            case .object(let entries):
                settings.providers = [:]
                for (id, value) in entries.members {
                    settings.providers[id] = try ProviderSettings.fromJSON(value, key: "Providers.\(id)")
                }
            default:
                throw LoadError.wrongType("Providers")
            }
        }
        // FromJson always ends with GetProvider("codex").
        if settings.providers["codex"] == nil { settings.providers["codex"] = ProviderSettings() }
        return settings
    }
}

extension ProviderSettings {
    static func fromJSON(_ json: JSONValue, key: String) throws -> ProviderSettings {
        var provider = ProviderSettings()
        if json.isNull { return provider }
        guard case .object(let root) = json else { throw AppSettings.LoadError.wrongType(key) }
        let reader = SettingsReader(root, prefix: key + ".")
        try reader.read("Enabled", into: &provider.enabled)
        try reader.read("RefreshSeconds", into: &provider.refreshSeconds)
        try reader.read("ShowSecondaryRows", into: &provider.showSecondaryRows)
        try reader.read("ShowCredits", into: &provider.showCredits)
        try reader.read("Command", into: &provider.command)
        try reader.read("AutoRenewSession", into: &provider.autoRenewSession)
        try reader.read("CreditsFullAmount", into: &provider.creditsFullAmount)
        return provider
    }
}

// Reads one property the way System.Text.Json would: absent keeps the default,
// a null string or collection reads as empty, and a mismatched type throws.
private struct SettingsReader {
    let root: JSONObject
    var prefix = ""

    init(_ root: JSONObject, prefix: String = "") {
        self.root = root
        self.prefix = prefix
    }

    private func fail(_ key: String) -> AppSettings.LoadError { .wrongType(prefix + key) }

    func read(_ key: String, into target: inout String) throws {
        guard let value = root[key] else { return }
        switch value {
        case .null: target = ""
        case .string(let text): target = text
        default: throw fail(key)
        }
    }

    func read(_ key: String, into target: inout Bool) throws {
        guard let value = root[key] else { return }
        guard let flag = value.boolValue else { throw fail(key) }
        target = flag
    }

    func read(_ key: String, into target: inout Int) throws {
        guard let value = root[key] else { return }
        guard let number = value.int64Value, number >= Int64(Int32.min), number <= Int64(Int32.max) else { throw fail(key) }
        target = Int(number)
    }

    func read(_ key: String, into target: inout Double) throws {
        guard let value = root[key] else { return }
        guard let number = value.doubleValue else { throw fail(key) }
        target = number
    }

    func read(_ key: String, into target: inout [Double]) throws {
        guard let value = root[key] else { return }
        if value.isNull { target = []; return }
        guard let items = value.arrayValue else { throw fail(key) }
        target = try items.map { item in
            guard let number = item.doubleValue else { throw fail(key) }
            return number
        }
    }

    func read<Value>(_ key: String, into target: inout [String: Value], _ convert: (JSONValue) -> Value?) throws {
        guard let value = root[key] else { return }
        if value.isNull { target = [:]; return }
        guard case .object(let entries) = value else { throw fail(key) }
        var result: [String: Value] = [:]
        for (entryKey, entryValue) in entries.members {
            guard let converted = convert(entryValue) else { throw fail("\(key).\(entryKey)") }
            result[entryKey] = converted
        }
        target = result
    }

    func read(_ key: String, into target: inout [String: Bool]) throws {
        try read(key, into: &target) { $0.boolValue }
    }

    func read(_ key: String, into target: inout [String: String]) throws {
        try read(key, into: &target) { $0.stringValue }
    }

    func read(_ key: String, into target: inout [String: Int]) throws {
        try read(key, into: &target) { value in
            value.int64Value.flatMap { $0 >= Int64(Int32.min) && $0 <= Int64(Int32.max) ? Int($0) : nil }
        }
    }

    func read(_ key: String, into target: inout [String: [Double]]) throws {
        try read(key, into: &target) { value in
            guard let items = value.arrayValue else { return value.isNull ? [] : nil }
            let numbers = items.compactMap(\.doubleValue)
            return numbers.count == items.count ? numbers : nil
        }
    }
}
