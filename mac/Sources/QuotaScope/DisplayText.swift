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

    static func rowFooter(_ row: UsageRow, _ settings: AppSettings, _ loc: Loc, now: Date) -> String {
        if RowShapes.isCreditsRow(row.label) { return row.detailText ?? "" }
        guard let reset = row.window?.resetsAt else { return loc.t("reset --", "재설정 --") }
        return settings.timeDisplayMode.caseInsensitiveCompare("RemainingTime") == .orderedSame
            ? resetIn(reset.date.timeIntervalSince(now), loc)
            : resetClock(reset.date, loc, now: now)
    }

    // Same-day windows show only the time; longer windows also need the date.
    static func resetClock(_ reset: Date, _ loc: Loc, now: Date) -> String {
        let isToday = Calendar.current.isDate(reset, inSameDayAs: now)
        let formatter = DateFormatter()
        if loc.isKorean {
            formatter.locale = Locale(identifier: "ko_KR")
            formatter.dateFormat = isToday ? "a h:mm" : "M월 d일 a h:mm"
            return formatter.string(from: reset) + " 초기화"
        }
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.amSymbol = "AM"
        formatter.pmSymbol = "PM"
        formatter.dateFormat = isToday ? "h:mm a" : "MMM d, h:mm a"
        return (isToday ? "resets at " : "resets ") + formatter.string(from: reset)
    }

    static func resetIn(_ remaining: TimeInterval, _ loc: Loc) -> String {
        if remaining <= 0 { return loc.t("resets now", "곧 초기화") }
        let totalMinutes = Int(remaining / 60)
        let days = totalMinutes / (24 * 60)
        let hours = (totalMinutes / 60) % 24
        let minutes = totalMinutes % 60
        if days >= 1 { return loc.t("resets in \(days)d \(hours)h", "\(days)일 \(hours)시간 뒤 초기화") }
        if totalMinutes >= 60 { return loc.t("resets in \(totalMinutes / 60)h \(minutes)m", "\(totalMinutes / 60)시간 \(minutes)분 뒤 초기화") }
        let shown = max(1, minutes)
        return loc.t("resets in \(shown)m", "\(shown)분 뒤 초기화")
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
