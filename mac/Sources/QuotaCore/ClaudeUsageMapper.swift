import Foundation

// Port of app/Providers/Claude/ClaudeUsageMapper.cs. Checked against
// fixtures/claude; a behavior change lands in C# and the fixtures first.
public enum ClaudeUsageMapper {
    public static let providerId = "claude"
    public static let providerDisplayName = "Claude"

    public static func fromJSON(_ root: JSONValue, creditsFullAmount: Double) -> ProviderUsage {
        var rows: [UsageRow] = []

        // Newer responses carry a "limits" array (session / weekly_all /
        // weekly_scoped with a model scope, e.g. Fable); when present it is the
        // authoritative source. Older field-based responses stay supported.
        if !tryAddLimitsRows(&rows, root) {
            addWindowRow(&rows, root, "five_hour", "5h", isPrimary: true)
            addWindowRow(&rows, root, "seven_day", "7d", isPrimary: true)
            addPerModelRows(&rows, root)
        }
        addExtraUsageRow(&rows, root, creditsFullAmount)

        return ProviderUsage(
            providerId: providerId,
            displayName: providerDisplayName,
            rows: rows,
            overallUsedPercent: computeOverallUsed(rows),
            statusText: "Claude rate limit",
            state: .ok)
    }

    private static func addWindowRow(_ rows: inout [UsageRow], _ root: JSONValue, _ property: String, _ label: String, isPrimary: Bool) {
        guard let window = parseWindow(object(root, property)) else { return }
        rows.append(UsageRow(label: label, window: window, isPrimary: isPrimary))
    }

    private static func tryAddLimitsRows(_ rows: inout [UsageRow], _ root: JSONValue) -> Bool {
        guard case .array(let limits)? = root["limits"] else { return false }

        var added = false
        for limit in limits {
            guard limit.objectValue != nil else { continue }
            let percent = limit["percent"]?.doubleValue ?? 0
            let window = RateLimitWindow(usedPercent: clamp(percent, 0, 100), resetsAt: parseResetsAt(limit), windowDurationMins: nil)
            let group = limit["group"]?.stringValue
            let baseLabel = group?.caseInsensitiveCompare("session") == .orderedSame ? "5h" : "7d"
            let isActive = limit["is_active"]?.boolValue == true

            if let scopeName = scopeName(limit) {
                // An active scoped limit is the constraint that currently
                // matters: always visible and counted into the overall number.
                rows.append(UsageRow(label: "\(baseLabel) \(scopeName)", window: window, isPrimary: isActive))
            } else {
                rows.append(UsageRow(label: baseLabel, window: window, isPrimary: true))
            }
            added = true
        }
        return added
    }

    private static func scopeName(_ limit: JSONValue) -> String? {
        guard case .object(let scope)? = limit["scope"] else { return nil }
        if case .object(let model)? = scope["model"] {
            let name = model["display_name"]?.stringValue ?? model["id"]?.stringValue
            if let name, !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return name }
        }
        return scope["surface"]?.stringValue ?? "scoped"
    }

    // Per-model weekly windows (seven_day_sonnet/opus/fable/...) are mapped
    // generically so newly introduced models show up without a code change.
    private static let perModelPrefix = "seven_day_"

    private static func addPerModelRows(_ rows: inout [UsageRow], _ root: JSONValue) {
        guard case .object(let members) = root else { return }
        for (name, value) in members.members {
            guard name.hasPrefix(perModelPrefix), value.objectValue != nil, let window = parseWindow(value) else { continue }
            rows.append(UsageRow(label: "7d \(modelLabel(String(name.dropFirst(perModelPrefix.count))))", window: window, isPrimary: false))
        }
    }

    private static func modelLabel(_ rawName: String) -> String {
        let pretty = rawName.replacingOccurrences(of: "_", with: " ")
        guard let first = pretty.first else { return rawName }
        return String(first).uppercased() + pretty.dropFirst()
    }

    // The row is always emitted. An account with extra usage switched off has
    // spent nothing, which is a real 0% rather than a row worth hiding.
    private static func addExtraUsageRow(_ rows: inout [UsageRow], _ root: JSONValue, _ fullAmount: Double) {
        let extra = object(root, "extra_usage")
        let utilization = extra?["utilization"]?.doubleValue
        let spent = extra?["used_credits"]?.doubleValue ?? 0
        let monthlyLimit = extra?["monthly_limit"]?.doubleValue

        // Prefer what the provider reports -- an explicit utilization, then a
        // monthly limit to divide by -- and fall back to the configured amount.
        let ceiling = (monthlyLimit ?? 0) > 0 ? monthlyLimit! : fullAmount
        let hasCeiling = CreditsGauge.hasUsableCeiling(ceiling)

        // Without an explicit utilization there is nothing to divide by, so the
        // row stays text-only rather than drawing a gauge against zero.
        if utilization == nil && !hasCeiling {
            rows.append(UsageRow(label: "Credits", window: nil, isPrimary: false,
                                 detailText: spent > 0 ? NumberText.hundredths(spent) : "--"))
            return
        }

        let usedPercent = utilization.map { clamp($0, 0, 100) } ?? CreditsGauge.usedPercentFromSpend(spent, ceiling)

        // Derived from the percentage actually drawn, not from used_credits: a
        // footer that contradicts its own bar is worse than no footer.
        let left: Double? = hasCeiling ? ceiling * (1 - usedPercent / 100) : nil
        let detail = left.map { CreditsGauge.formatRemaining($0, ceiling) }
        // Spend past the ceiling clamps the gauge full; the flag lets the
        // percent read >100% rather than claim the spend stopped at 100%.
        let beyondFull = utilization.map { $0 > 100 } ?? CreditsGauge.spendBeyondFull(spent, ceiling)

        rows.append(UsageRow(label: "Credits", window: RateLimitWindow(usedPercent: usedPercent, resetsAt: nil, windowDurationMins: nil),
                             isPrimary: false, detailText: detail, beyondFull: beyondFull, creditsLeft: left))
    }

    private static func computeOverallUsed(_ rows: [UsageRow]) -> Double {
        var maximum = 0.0
        for row in rows where row.isPrimary {
            guard let window = row.window else { continue }
            maximum = max(maximum, window.usedPercent)
        }
        return maximum
    }

    private static func parseWindow(_ element: JSONValue?) -> RateLimitWindow? {
        guard let element else { return nil }
        let utilization = element["utilization"]?.doubleValue ?? 0
        return RateLimitWindow(usedPercent: clamp(utilization, 0, 100), resetsAt: parseResetsAt(element), windowDurationMins: nil)
    }

    // ISO 8601 UTC strings; deliberately not shared with the Codex epoch parser.
    private static func parseResetsAt(_ element: JSONValue) -> Timestamp? {
        element["resets_at"]?.stringValue.flatMap(Timestamp.parseISO8601)
    }

    private static func object(_ element: JSONValue, _ name: String) -> JSONValue? {
        guard let value = element[name], value.objectValue != nil else { return nil }
        return value
    }
}
