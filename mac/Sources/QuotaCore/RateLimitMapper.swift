import Foundation

// Port of app/Providers/Codex/RateLimitMapper.cs. Checked against
// fixtures/codex; a behavior change lands in C# and the fixtures first.

public struct CodexCredits: Equatable {
    public var hasCredits: Bool
    public var unlimited: Bool
    public var balance: String?
}

public struct RateLimitSnapshot: Equatable {
    public var limitId: String?
    public var limitName: String?
    public var planType: String?
    public var primary: RateLimitWindow?
    public var secondary: RateLimitWindow?
    public var rateLimitReachedType: String?
    public var credits: CodexCredits?
}

public enum RateLimitMapperError: Error {
    case noRateLimitsSnapshot
    case notAnObject
}

public enum RateLimitMapper {
    public static let providerId = "codex"
    public static let providerDisplayName = "Codex"

    // Only consulted when a read does not name its main bucket; this is the id
    // the backend has used for it in every payload seen so far.
    private static let mainLimitId = "codex"

    public static func fromJSONResult(_ result: JSONValue, creditsFullAmount: Double) -> ProviderUsage {
        let mainSnapshotElement = result["rateLimits"] ?? result
        let mainSnapshot = parseSnapshot(mainSnapshotElement)
        let sparkSnapshot = tryFindSparkSnapshot(result)
        return toProviderUsage(mainSnapshot, creditsFullAmount: creditsFullAmount, sparkSnapshot: sparkSnapshot)
    }

    // account/rateLimits/updated is a sparse rolling update, not a snapshot: it
    // carries one bucket, and a null or absent field means "not in this update",
    // never "cleared". Mapped on its own it would drop every window, Spark row,
    // and credits balance it leaves out, so it is folded into the last full read
    // instead. Returns a read-shaped result that fromJSONResult maps as usual.
    public static func mergeRollingUpdate(_ lastRead: JSONValue, _ notificationParams: JSONValue) throws -> JSONValue {
        guard case .object(let update)? = notificationParams["rateLimits"] else {
            throw RateLimitMapperError.noRateLimitsSnapshot
        }
        guard case .object(var merged) = lastRead else { throw RateLimitMapperError.notAnObject }

        // The update names its bucket by limitId; none means the main bucket.
        // The main bucket is also mirrored in rateLimitsByLimitId, and both
        // copies have to move together.
        let mainId = merged["rateLimits"]?["limitId"]?.stringValue ?? mainLimitId
        let updateId = update["limitId"]?.stringValue
        if updateId == nil || updateId == mainId {
            withObject(&merged, "rateLimits") { mergeSnapshot(&$0, update) }
            if case .object(var byLimitId)? = merged["rateLimitsByLimitId"], case .object(var mainEntry)? = byLimitId[mainId] {
                mergeSnapshot(&mainEntry, update)
                byLimitId[mainId] = .object(mainEntry)
                merged["rateLimitsByLimitId"] = .object(byLimitId)
            }
        } else if let updateId {
            withObject(&merged, "rateLimitsByLimitId") { byLimitId in
                withObject(&byLimitId, updateId) { mergeSnapshot(&$0, update) }
            }
        }
        return .object(merged)
    }

    private static func mergeSnapshot(_ target: inout JSONObject, _ update: JSONObject) {
        let slots = assignWindowSlots(target, update)
        for (name, value) in update.members {
            guard let slot = slots[name] else {
                mergeValue(&target, name, value)
                continue
            }

            // A window of a different length is a different window: merged
            // field by field it would keep the old window's reset time, so it
            // replaces the stored one. No reset time beats a wrong one.
            let mins = value["windowDurationMins"]?.int64Value
            let storedMins = target[slot]?["windowDurationMins"]?.int64Value
            if let mins, let storedMins, mins != storedMins {
                target[slot] = value
            } else {
                mergeValue(&target, slot, value)
            }
        }
    }

    // A window that names the other slot's duration belongs to that slot.
    // Merging by slot alone would turn a 5h + 7d pair into two 7d rows if an
    // update fills the slots differently from the read. Both windows are placed
    // against the read before either is merged, and never into the same slot:
    // a duration match outranks a window's own slot name, and the window that
    // loses a slot takes the one left free. Property order cannot change it.
    private static func assignWindowSlots(_ target: JSONObject, _ update: JSONObject) -> [String: String] {
        let storedMins: [String: Int64?] = [
            "primary": target["primary"]?["windowDurationMins"]?.int64Value,
            "secondary": target["secondary"]?["windowDurationMins"]?.int64Value,
        ]

        var claims: [(name: String, slot: String, byDuration: Bool)] = []
        for (name, other) in [("primary", "secondary"), ("secondary", "primary")] {
            guard case .object(let window)? = update[name] else { continue }
            let mins = window["windowDurationMins"]?.int64Value
            if let mins, mins == storedMins[name] ?? nil {
                claims.append((name, name, true))
            } else if let mins, mins == storedMins[other] ?? nil {
                claims.append((name, other, true))
            } else {
                claims.append((name, name, false))
            }
        }

        if claims.count == 2 && claims[0].slot == claims[1].slot {
            let loser = claims[0].byDuration != claims[1].byDuration
                ? (claims[0].byDuration ? 1 : 0)
                : (claims[0].name == claims[0].slot ? 1 : 0)
            let freeSlot = claims[loser].slot == "primary" ? "secondary" : "primary"
            claims[loser].slot = freeSlot
        }

        return Dictionary(uniqueKeysWithValues: claims.map { ($0.name, $0.slot) })
    }

    // Field-wise: objects (windows, credits, individualLimit) merge one field at
    // a time, so a window update without resetsAt keeps the known reset time.
    private static func mergeValue(_ target: inout JSONObject, _ name: String, _ value: JSONValue) {
        switch value {
        case .null:
            return
        case .object(let object):
            withObject(&target, name) { child in
                for (key, member) in object.members { mergeValue(&child, key, member) }
            }
        default:
            target[name] = value
        }
    }

    // GetOrAddObject: an existing object keeps its place, anything else is
    // replaced by a new object.
    private static func withObject(_ parent: inout JSONObject, _ name: String, _ body: (inout JSONObject) -> Void) {
        var child = parent[name]?.objectValue ?? JSONObject()
        body(&child)
        parent[name] = .object(child)
    }

    public static func toProviderUsage(_ snapshot: RateLimitSnapshot, creditsFullAmount: Double,
                                       sparkSnapshot: RateLimitSnapshot? = nil) -> ProviderUsage {
        var rows: [UsageRow] = []

        // Rows are payload-driven: only windows that actually exist become rows.
        // A plan whose windows are all a day or longer (Pro: weekly only) names
        // its rows after the plan, so the missing 5h row reads as how that plan
        // works rather than as data that failed to arrive.
        let mainScope = isWeeklyOnly(snapshot) ? planDisplayName(snapshot.planType) : nil
        for window in shortestWindowFirst(snapshot) {
            addWindowRow(&rows, window, { $0 }, isPrimary: true, scope: mainScope)
        }
        if let sparkSnapshot {
            for window in shortestWindowFirst(sparkSnapshot) {
                addWindowRow(&rows, window, { "Spark \($0)" }, isPrimary: false)
            }
        }

        if let credits = snapshot.credits, credits.hasCredits {
            rows.append(buildCreditsRow(credits, creditsFullAmount))
        }

        let reached = snapshot.rateLimitReachedType ?? ""
        return ProviderUsage(
            providerId: providerId,
            displayName: providerDisplayName,
            rows: rows,
            overallUsedPercent: computeOverallUsed(snapshot),
            statusText: reached.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "Codex rate limit" : reached,
            state: .ok)
    }

    private static func addWindowRow(_ rows: inout [UsageRow], _ window: RateLimitWindow?, _ labelFactory: (String) -> String,
                                     isPrimary: Bool, scope: String? = nil) {
        guard let window else { return }
        rows.append(UsageRow(label: labelFactory(formatDurationLabel(window.windowDurationMins)), window: window,
                             isPrimary: isPrimary, scope: scope))
    }

    // Every present window must be known to span a day or more: a window
    // without a duration says nothing about the plan.
    private static func isWeeklyOnly(_ snapshot: RateLimitSnapshot) -> Bool {
        let present = [snapshot.primary, snapshot.secondary].compactMap { $0 }
        return !present.isEmpty && present.allSatisfy { ($0.windowDurationMins ?? Int64.min) >= 1440 }
    }

    // planType values from the app-server schema. Only plans whose display name
    // is certain are listed; anything else gets no qualifier.
    private static func planDisplayName(_ planType: String?) -> String? {
        switch planType?.lowercased() {
        case "free": return "Free"
        case "go": return "Go"
        case "plus": return "Plus"
        case "pro": return "Pro"
        case "team": return "Team"
        case "business": return "Business"
        case "enterprise": return "Enterprise"
        case "edu": return "Edu"
        default: return nil
        }
    }

    // The 5-hour row is kept ahead of the weekly one by duration rather than by
    // slot. Equal or unknown durations keep their slot order (stable).
    private static func shortestWindowFirst(_ snapshot: RateLimitSnapshot) -> [RateLimitWindow?] {
        [snapshot.primary, snapshot.secondary]
            .enumerated()
            .sorted { lhs, rhs in
                let left = lhs.element?.windowDurationMins ?? Int64.max
                let right = rhs.element?.windowDurationMins ?? Int64.max
                return left != right ? left < right : lhs.offset < rhs.offset
            }
            .map { $0.element }
    }

    // Unified label units across providers: hours under a day, days above (10080 -> "7d").
    public static func formatDurationLabel(_ durationMins: Int64?) -> String {
        guard let mins = durationMins, mins > 0 else { return "usage" }
        if mins % 1440 == 0 { return "\(mins / 1440)d" }
        if mins % 60 == 0 { return "\(mins / 60)h" }
        return "\(mins)m"
    }

    private static func computeOverallUsed(_ snapshot: RateLimitSnapshot) -> Double {
        var maximum = 0.0
        for window in [snapshot.primary, snapshot.secondary] {
            guard let window else { continue }
            maximum = max(maximum, window.usedPercent)
        }
        return maximum
    }

    // The balance is what is left and carries no ceiling, so the gauge needs the
    // configured full amount. Unlimited credits have no meaningful fill, and a
    // balance that will not parse has no number to draw: both stay text-only.
    private static func buildCreditsRow(_ credits: CodexCredits, _ fullAmount: Double) -> UsageRow {
        let parsed = credits.balance.flatMap(NumberText.parseNumberStyle)
        guard !credits.unlimited, CreditsGauge.hasUsableCeiling(fullAmount), let parsed else {
            return UsageRow(label: "Credits", window: nil, isPrimary: false, detailText: formatCredits(credits),
                            creditsLeft: !credits.unlimited ? parsed?.double : nil)
        }

        let remaining = parsed.double
        return UsageRow(
            label: "Credits",
            window: RateLimitWindow(usedPercent: CreditsGauge.usedPercentFromBalance(remaining, fullAmount), resetsAt: nil, windowDurationMins: nil),
            isPrimary: false,
            detailText: CreditsGauge.formatRemaining(remaining, fullAmount),
            beyondFull: CreditsGauge.balanceBeyondFull(remaining, fullAmount),
            creditsLeft: remaining)
    }

    private static func formatCredits(_ credits: CodexCredits) -> String {
        if credits.unlimited { return "unlimited" }
        guard let balance = credits.balance, !balance.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return "--" }
        if let parsed = NumberText.parseNumberStyle(balance) { return NumberText.hundredths(parsed.decimal) }
        return balance
    }

    public static func parseSnapshot(_ element: JSONValue) -> RateLimitSnapshot {
        RateLimitSnapshot(
            limitId: element["limitId"]?.stringValue,
            limitName: element["limitName"]?.stringValue,
            planType: element["planType"]?.stringValue,
            primary: parseWindow(element["primary"]),
            secondary: parseWindow(element["secondary"]),
            rateLimitReachedType: element["rateLimitReachedType"]?.stringValue,
            credits: parseCredits(element["credits"]))
    }

    private static func tryFindSparkSnapshot(_ result: JSONValue) -> RateLimitSnapshot? {
        guard case .object(let byLimitId)? = result["rateLimitsByLimitId"] else { return nil }
        for (name, value) in byLimitId.members {
            let snapshot = parseSnapshot(value)
            let searchable = "\(name) \(snapshot.limitId ?? "") \(snapshot.limitName ?? "")"
            if ["spark", "bengalfox", "gpt-5.3-codex"].contains(where: { searchable.range(of: $0, options: .caseInsensitive) != nil }) {
                return snapshot
            }
        }
        return nil
    }

    private static func parseWindow(_ element: JSONValue?) -> RateLimitWindow? {
        guard let element, !element.isNull else { return nil }
        let usedPercent = element["usedPercent"]?.doubleValue ?? 0
        let resetsAt = parseTimestamp(element["resetsAt"]?.int64Value)
        let duration = element["windowDurationMins"]?.int64Value
        return RateLimitWindow(usedPercent: clamp(usedPercent, 0, 100), resetsAt: resetsAt, windowDurationMins: duration)
    }

    private static func parseCredits(_ element: JSONValue?) -> CodexCredits? {
        guard let element, !element.isNull else { return nil }
        return CodexCredits(
            hasCredits: element["hasCredits"]?.boolValue == true,
            unlimited: element["unlimited"]?.boolValue == true,
            balance: element["balance"]?.stringValue)
    }

    private static func parseTimestamp(_ raw: Int64?) -> Timestamp? {
        guard let raw else { return nil }
        return raw > 10_000_000_000 ? .fromUnixMilliseconds(raw) : .fromUnixSeconds(raw)
    }
}
