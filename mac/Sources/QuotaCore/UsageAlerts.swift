import Foundation

// Port of app-winui/UsageAlerts.cs. Checked against fixtures/alerts.

// One row passing one of its alert points. remaining is what the row had left
// when the poll saw it, in the row's unit: a percent, or credits on a credits
// row. critical says it is at or past the critical level (credits: none left).
public struct UsageAlert: Equatable {
    public var providerId: String
    public var providerName: String
    public var row: UsageRow
    public var point: Double
    public var remaining: Double
    public var critical: Bool

    public var isCredits: Bool { RowShapes.isCreditsRow(row.label) }
}

// Two levels (warning, critical) that color the menu bar icon and the popup
// rows, and any number of notification points per row. Points are remaining
// percentages, except on a credits row, whose points are credits left.
public enum UsageAlerts {
    public static let maxPoints = 10
    // Same ceiling the credits gauge amount accepts.
    public static let maxCredits = 1_000_000.0

    public static func warningAtUsed(_ settings: AppSettings) -> Double {
        100 - Double(min(max(settings.warningThresholdPercent, 0), 100))
    }

    public static func criticalAtUsed(_ settings: AppSettings) -> Double {
        100 - clamp(settings.criticalThresholdPercent, 0, 100)
    }

    // A row without an override notifies when the provider marks it primary.
    public static func isEnabled(_ settings: AppSettings, _ providerId: String, _ label: String, _ isPrimary: Bool) -> Bool {
        settings.rowAlerts[RowShapes.key(providerId, label)] ?? isPrimary
    }

    public static func pointsFor(_ settings: AppSettings, _ providerId: String, _ label: String) -> [Double] {
        if let own = settings.rowAlertPoints[RowShapes.key(providerId, label)], !own.isEmpty { return own }
        return RowShapes.isCreditsRow(label) ? defaultCreditPoints : defaultPoints(settings)
    }

    // No points of its own means the warning and critical levels.
    public static func defaultPoints(_ settings: AppSettings) -> [Double] {
        settings.alertPoints.isEmpty
            ? [Double(settings.warningThresholdPercent), settings.criticalThresholdPercent]
            : settings.alertPoints
    }

    // A credits row switched on without points of its own is notified when its
    // credits run out.
    public static let defaultCreditPoints: [Double] = [0]

    public enum ParseError: Error, Equatable {
        case thousandsSeparator(String)
        case notANumber(String)
        case creditsOutOfRange(String)
        case percentOutOfRange(String)
        case tooMany

        public func message(_ loc: Loc) -> String {
            switch self {
            case .thousandsSeparator(let text):
                return loc.t("\"\(text)\": write amounts without a thousands separator (e.g. 2000); commas separate points.",
                             "\"\(text)\": 수량은 천 단위 쉼표 없이 적어 주세요(예: 2000). 쉼표는 지점을 나눕니다.")
            case .notANumber(let token):
                return loc.t("\"\(token)\" is not a number.", "\"\(token)\"은(는) 숫자가 아닙니다.")
            case .creditsOutOfRange(let token):
                return loc.t("\(token): a credits point must be from 0 to 1000000 credits left.",
                             "\(token): 크레딧 지점은 0부터 1000000 사이의 남은 크레딧 수량이어야 합니다.")
            case .percentOutOfRange(let token):
                return loc.t("\(token): a point must be from 0 up to (not including) 100 (remaining %).",
                             "\(token): 지점은 0 이상 100 미만의 남은 양 %여야 합니다.")
            case .tooMany:
                return loc.t("At most \(UsageAlerts.maxPoints) points.", "지점은 최대 \(UsageAlerts.maxPoints)개입니다.")
            }
        }
    }

    // "80, 60, 40" (commas, semicolons or spaces; a trailing % is fine) into
    // distinct points, highest first. Empty text is an empty list, which means
    // "use the default". The comma is a separator, so decimals take a point.
    public static func parsePoints(_ text: String?, credits: Bool) -> Result<[Double], ParseError> {
        guard let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return .success([]) }

        // "2,000" would quietly become 2 and 0. Amounts of credits are the only
        // points that reach thousands; a comma with three digits after it is
        // refused rather than guessed at.
        if credits, let grouped = thousandsSeparator(in: text) { return .failure(.thousandsSeparator(grouped)) }

        var points: [Double] = []
        let tokens = text.split(whereSeparator: { ",; \t".contains($0) }).map(String.init)
        for token in tokens {
            var number = Substring(token)
            while number.hasSuffix("%") { number.removeLast() }
            guard var value = NumberText.parseFloatStyle(String(number)), value.isFinite else {
                return .failure(.notANumber(token))
            }
            // Stored as shown: the box writes points back with two decimals.
            value = NumberText.round(value, digits: 2)
            if credits && (value < 0 || value > maxCredits) { return .failure(.creditsOutOfRange(token)) }
            if !credits && (value < 0 || value >= 100) { return .failure(.percentOutOfRange(token)) }
            if !points.contains(value) { points.append(value) }
        }

        if points.count > maxPoints { return .failure(.tooMany) }
        points.sort(by: >)
        return .success(points)
    }

    public static func formatPoints(_ points: [Double]) -> String {
        points.map(NumberText.hundredths).joined(separator: ", ")
    }

    // Everything one check passed goes into one notification, titled critical
    // when any of it is at the critical level. The 255-character cut is the
    // Windows balloon limit, kept so both platforms say the same thing.
    public static func buildNotification(_ alerts: [UsageAlert], _ loc: Loc) -> (title: String, body: String) {
        let title = alerts.contains { $0.critical } ? loc.t("Usage critical", "사용량 위험") : loc.t("Usage warning", "사용량 경고")
        let body = alerts.map { alert -> String in
            let name = loc.rowLabel(alert.providerId, alert.row.label, scope: alert.row.scope)
            if alert.isCredits {
                let credits = NumberText.hundredths(alert.remaining)
                return "\(alert.providerName) \(name) " + loc.t("\(credits) left", "\(credits) 남음")
            }
            // Rounded the way the popup and the tooltip round it.
            let left = 100 - Int(NumberText.round(100 - alert.remaining))
            return "\(alert.providerName) \(name) " + loc.t("\(left)% left", "\(left)% 남음")
        }.joined(separator: "\n")
        // Truncated in UTF-16 units, like the C# substring.
        let utf16 = Array(body.utf16)
        return (title, utf16.count <= 255 ? body : String(decoding: utf16[0..<255], as: UTF16.self))
    }

    // \d,\d{3}(?!\d): the first match's text.
    private static func thousandsSeparator(in text: String) -> String? {
        let chars = Array(text)
        guard chars.count >= 5 else { return nil }
        for start in 0...(chars.count - 5) {
            guard chars[start].isASCIIDigit, chars[start + 1] == ",",
                  chars[(start + 2)...(start + 4)].allSatisfy(\.isASCIIDigit) else { continue }
            if start + 5 < chars.count, chars[start + 5].isASCIIDigit { continue }
            return String(chars[start...(start + 4)])
        }
        return nil
    }
}

// Remembers, per row, the deepest point already passed since the row was last
// above all of its points. A row that climbs back above a point (its window
// resets) re-arms it; a row that goes missing (provider offline) keeps its
// state, so coming back does not repeat an alert. Rows whose notifications are
// off are tracked too, so switching one on does not replay a point it passed.
public final class UsageAlertTracker {
    private var notified: [String: Double] = [:]
    // A process that restarted lost the state above, so the points each row
    // has already passed are recorded on first sight instead of notified.
    private let silentFirstSight: Bool
    private var observed: Set<String> = []

    public init(silentFirstSight: Bool = false) { self.silentFirstSight = silentFirstSight }

    // Row keys compare ignoring case, as the C# tracker's dictionaries do.
    public func evaluate(_ settings: AppSettings, _ usages: [ProviderUsage]) -> [UsageAlert] {
        var alerts: [UsageAlert] = []
        let criticalAtUsed = UsageAlerts.criticalAtUsed(settings)
        // A second row with the same label (two undated Codex windows) would
        // overwrite the first one's state every check; the first row wins.
        var seen: Set<String> = []
        for usage in usages {
            for row in usage.rows {
                // A credits row compares the credits it has left; every other row
                // its gauge. Both as the popup shows them (whole percents,
                // credits to two decimals), so a row reading "0" has passed 0.
                let credits = RowShapes.isCreditsRow(row.label)
                let remaining: Double
                let critical: Bool
                if credits {
                    guard let left = row.creditsLeft else { continue }
                    remaining = NumberText.round(left, digits: 2)
                    critical = remaining <= 0
                } else {
                    guard let window = row.window else { continue }
                    let used = clamp(window.usedPercent, 0, 100)
                    remaining = 100 - NumberText.round(used)
                    critical = used >= criticalAtUsed
                }
                let key = RowShapes.key(usage.providerId, row.label)
                let folded = key.lowercased()
                guard seen.insert(folded).inserted else { continue }
                let firstSight = silentFirstSight && observed.insert(folded).inserted

                // 0 is a point too: it is passed once nothing is left.
                var deepest: Double?
                for point in UsageAlerts.pointsFor(settings, usage.providerId, row.label) {
                    if point >= 0, credits || point < 100, remaining <= point, deepest == nil || point < deepest! { deepest = point }
                }

                guard let passed = deepest else {
                    notified[folded] = nil
                    continue
                }
                if let previous = notified[folded], passed >= previous {
                    notified[folded] = passed
                    continue
                }

                notified[folded] = passed
                if !firstSight && UsageAlerts.isEnabled(settings, usage.providerId, row.label, row.isPrimary) {
                    alerts.append(UsageAlert(providerId: usage.providerId, providerName: usage.displayName, row: row,
                                             point: passed, remaining: remaining, critical: critical))
                }
            }
        }
        return alerts
    }
}
