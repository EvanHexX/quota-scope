import Foundation

// Port of app-winui/RowShapes.cs. Checked against fixtures/rows.

// A row the user can show, order, and assign a shape to. isPrimary carries the
// provider's own default so settings can resolve visibility for a row it has
// never seen before. scope only names the row; the key stays id + label.
public struct UsageRowRef: Equatable {
    public var providerId: String
    public var providerName: String
    public var label: String
    public var isPrimary: Bool
    public var scope: String?

    public init(providerId: String, providerName: String, label: String, isPrimary: Bool, scope: String? = nil) {
        self.providerId = providerId
        self.providerName = providerName
        self.label = label
        self.isPrimary = isPrimary
        self.scope = scope
    }
}

public enum RowShapes {
    public static let circle = "Circle"
    public static let bars = "Bars"
    // Two-line card whose own background fills up to the percentage. Also the
    // shapeTheme value that applies it to every row.
    public static let fill = "Fill"

    // Stable per-row key: provider id + the provider's English row label.
    public static func key(_ providerId: String, _ label: String) -> String { "\(providerId)|\(label)" }

    // Rank used for the user-defined display order. Rows without an override
    // stay in payload order, after any row the user has pinned to a position.
    public static func rank(_ settings: AppSettings, _ providerId: String, _ label: String, _ payloadIndex: Int) -> Int {
        settings.rowOrder[key(providerId, label)] ?? 1000 + payloadIndex
    }

    public static func order<T>(_ settings: AppSettings, _ providerId: String, _ rows: [T], _ labelOf: (T) -> String) -> [T] {
        rows.enumerated()
            .map { (row: $0.element, index: $0.offset, rank: rank(settings, providerId, labelOf($0.element), $0.offset)) }
            .sorted { $0.rank != $1.rank ? $0.rank < $1.rank : $0.index < $1.index }
            .map { $0.row }
    }

    public static func isCreditsRow(_ label: String) -> Bool {
        label.caseInsensitiveCompare("Credits") == .orderedSame
    }

    // Row visibility. An explicit choice per row wins; rows without one fall
    // back to the provider defaults: primary windows are shown, and model and
    // credit rows follow the provider's own toggles.
    public static func isVisible(_ settings: AppSettings, _ providerId: String, _ label: String, _ isPrimary: Bool) -> Bool {
        if let visible = settings.rowVisibility[key(providerId, label)] { return visible }
        if isPrimary { return true }
        let provider = settings.provider(providerId)
        return isCreditsRow(label) ? provider.showCredits : provider.showSecondaryRows
    }

    public static func isVisible(_ settings: AppSettings, _ providerId: String, _ row: UsageRow) -> Bool {
        isVisible(settings, providerId, row.label, row.isPrimary)
    }

    public static func resolve(_ settings: AppSettings, _ providerId: String, _ row: UsageRow) -> String {
        if equalsIgnoringCase(settings.shapeTheme, "Bars") { return bars }
        if equalsIgnoringCase(settings.shapeTheme, "BentoCircles") { return circle }
        if equalsIgnoringCase(settings.shapeTheme, fill) { return fill }

        // Mix and match: per-row override, gauge by default.
        guard let shape = settings.rowShapes[key(providerId, row.label)] else { return circle }
        if equalsIgnoringCase(shape, bars) { return bars }
        return equalsIgnoringCase(shape, fill) ? fill : circle
    }
}

func equalsIgnoringCase(_ lhs: String, _ rhs: String) -> Bool {
    lhs.caseInsensitiveCompare(rhs) == .orderedSame
}
