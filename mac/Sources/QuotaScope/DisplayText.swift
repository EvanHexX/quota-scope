#if os(macOS)
import Foundation
import QuotaCore

// Text the menu bar item and the popover show. The wording follows
// app-winui/Loc.cs and TrayController.cs word for word.

enum MenuBarText {
    static let off = "Off"
    static let highestWeekly = "HighestWeekly"
    static let highestOverall = "HighestOverall"

    // nil hides the text; "--" when the chosen row is missing (3.1), rather
    // than silently switching rows.
    static func text(_ choice: String, usages: [ProviderUsage], settings: AppSettings) -> String? {
        let used: Double?
        switch choice {
        case off:
            return nil
        case highestOverall:
            used = usages.isEmpty ? nil : usages.map(\.overallUsedPercent).max()
        case highestWeekly:
            used = usages.flatMap { $0.rows.filter(isWeeklyMain) }.compactMap { $0.window?.usedPercent }.max()
        default:
            used = rowUsed(choice, usages)
        }
        guard let used else { return "--" }
        return "\(Display.percent(used, settings))%"
    }

    // A weekly main window: Codex 7d, Claude 7d, and Claude's active scoped
    // weekly rows (those the mapper marks primary).
    static func isWeeklyMain(_ row: UsageRow) -> Bool {
        guard row.isPrimary, let window = row.window, !RowShapes.isCreditsRow(row.label) else { return false }
        if window.windowDurationMins == 10080 { return true }
        let first = row.label.split(separator: " ").first.map(String.init) ?? row.label
        return first == "7d" || first == "1w"
    }

    private static func rowUsed(_ key: String, _ usages: [ProviderUsage]) -> Double? {
        for usage in usages {
            for row in usage.rows where RowShapes.key(usage.providerId, row.label) == key {
                return row.window?.usedPercent
            }
        }
        return nil
    }
}

enum Display {
    static func roundedUsed(_ used: Double) -> Int {
        Int(NumberText.round(min(max(used, 0), 100)))
    }

    // What gauges and percent texts show follows the configured metric.
    static func percent(_ used: Double, _ settings: AppSettings) -> Int {
        let rounded = roundedUsed(used)
        return showsRemaining(settings) ? 100 - rounded : rounded
    }

    static func showsRemaining(_ settings: AppSettings) -> Bool {
        settings.gaugeMetric.caseInsensitiveCompare("Remaining") == .orderedSame
    }

    // A credits amount past its full amount fills the gauge, and the text says
    // so rather than claiming exactly 100%.
    static func percentText(_ row: UsageRow, _ display: Int) -> String {
        row.beyondFull && display == 100 ? ">100%" : "\(display)%"
    }

    // The shared reset text (Loc.resetText, fixtures/resettext), in the
    // offset the Mac is in at that moment.
    static func rowFooter(_ row: UsageRow, _ settings: AppSettings, _ loc: Loc, now: Date) -> String {
        if RowShapes.isCreditsRow(row.label) { return row.detailText ?? "" }
        return loc.resetText(row.window?.resetsAt, now: Timestamp(now),
                             utcOffsetSeconds: TimeZone.current.secondsFromGMT(for: now),
                             remaining: settings.timeDisplayMode.caseInsensitiveCompare("RemainingTime") == .orderedSame)
    }

    // Hover text: same rows in the same order the popover shows, remaining
    // percentage spelled out.
    static func tooltip(_ usages: [ProviderUsage], _ settings: AppSettings, _ loc: Loc) -> String {
        if usages.isEmpty { return "No providers enabled" }
        return usages.map { usage -> String in
            let rows = RowShapes.order(settings, usage.providerId, usage.rows.filter {
                $0.window != nil && !RowShapes.isCreditsRow($0.label) && RowShapes.isVisible(settings, usage.providerId, $0)
            }) { $0.label }
            if rows.isEmpty { return "\(usage.displayName) --" }
            return "\(usage.displayName) " + rows.map { row in
                let remaining = 100 - roundedUsed(row.window!.usedPercent)
                return "\(loc.rowLabel(usage.providerId, row.label, scope: row.scope)) \(loc.t("\(remaining)% left", "\(remaining)% 남음"))"
            }.joined(separator: " / ")
        }.joined(separator: "\n")
    }

    // Port of TrayIconRenderer.ComputeState; levels are remaining percentages.
    enum IconState { case normal, warning, critical }

    static func iconState(_ usages: [ProviderUsage], _ settings: AppSettings) -> IconState {
        let overall = usages.map(\.overallUsedPercent).max() ?? 0
        let rateLimited = usages.contains { $0.state == .rateLimited }
        if rateLimited || overall >= UsageAlerts.criticalAtUsed(settings) { return .critical }
        return overall >= UsageAlerts.warningAtUsed(settings) ? .warning : .normal
    }
}
#endif
