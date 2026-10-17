import Foundation

// Port of app-winui/RowLayout.cs. Checked against fixtures/layout.

// One line of the popup: a bar row (right is always nil), or one or two cards
// of one shape side by side.
public struct LayoutLine: Equatable {
    public var shape: String
    public var left: UsageRow
    public var right: UsageRow?
}

public struct LayoutSection: Equatable {
    public var usage: ProviderUsage
    public var rows: [UsageRow]
}

public enum RowLayout {
    // The visible rows of each provider, in the user's order. The order
    // decides which gauges end up adjacent, and so which ones pair up.
    public static func sections(_ settings: AppSettings, _ usages: [ProviderUsage]) -> [LayoutSection] {
        usages.map { usage in
            let visible = usage.rows.filter { RowShapes.isVisible(settings, usage.providerId, $0) }
            return LayoutSection(usage: usage, rows: RowShapes.order(settings, usage.providerId, visible) { $0.label })
        }
    }

    // Two columns only pay off when some section actually has a pair of gauges
    // to place side by side. The user can force either column count.
    public static func usesTwoColumns(_ settings: AppSettings, _ sections: [LayoutSection]) -> Bool {
        if equalsIgnoringCase(settings.layoutColumns, "OneColumn") { return false }
        if equalsIgnoringCase(settings.layoutColumns, "TwoColumns") { return true }
        return sections.contains { section in
            section.rows.filter { RowShapes.resolve(settings, section.usage.providerId, $0) == RowShapes.circle }.count >= 2
        }
    }

    // Consecutive cards of one shape pair up; a bar row always spans the full
    // width and flushes any pending card first. A fill card never shares a
    // line with a gauge, so a change of shape flushes as well. In one column
    // every row gets a line of its own.
    public static func lines(_ settings: AppSettings, _ providerId: String, _ rows: [UsageRow], twoColumn: Bool) -> [LayoutLine] {
        var lines: [LayoutLine] = []
        var pending: UsageRow?
        var pendingShape = RowShapes.circle

        func flushPending() {
            guard let row = pending else { return }
            lines.append(LayoutLine(shape: pendingShape, left: row, right: nil))
            pending = nil
        }

        for row in rows {
            let shape = RowShapes.resolve(settings, providerId, row)
            if shape == RowShapes.bars {
                flushPending()
                lines.append(LayoutLine(shape: shape, left: row, right: nil))
                continue
            }
            if !twoColumn {
                lines.append(LayoutLine(shape: shape, left: row, right: nil))
                continue
            }
            if pending != nil && shape != pendingShape { flushPending() }
            guard let first = pending else {
                pending = row
                pendingShape = shape
                continue
            }
            lines.append(LayoutLine(shape: shape, left: first, right: row))
            pending = nil
        }

        flushPending()
        return lines
    }
}
