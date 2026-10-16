import Foundation

// An instant in 100-nanosecond ticks since the Unix epoch, the resolution of
// .NET's DateTimeOffset. Claude reports reset times to the microsecond
// ("2026-07-22T17:19:59.607128+00:00"); Foundation's ISO 8601 formatter keeps
// only milliseconds, and the shared fixtures compare the full value.
public struct Timestamp: Hashable, Comparable {
    public static let ticksPerSecond: Int64 = 10_000_000

    public let ticks: Int64

    public init(ticks: Int64) { self.ticks = ticks }

    public static func fromUnixSeconds(_ seconds: Int64) -> Timestamp { Timestamp(ticks: seconds * ticksPerSecond) }
    public static func fromUnixMilliseconds(_ milliseconds: Int64) -> Timestamp { Timestamp(ticks: milliseconds * 10_000) }

    public var date: Date { Date(timeIntervalSince1970: Double(ticks) / Double(Timestamp.ticksPerSecond)) }

    public init(_ date: Date) {
        self.init(ticks: Int64((date.timeIntervalSince1970 * Double(Timestamp.ticksPerSecond)).rounded()))
    }

    public static func < (lhs: Timestamp, rhs: Timestamp) -> Bool { lhs.ticks < rhs.ticks }

    // ISO 8601 UTC with trailing zero fractions dropped: 2030-01-01T12:00:00Z,
    // the format UsageFixtures.FormatTimestamp writes on the C# side.
    public var iso8601: String {
        let seconds = ticks.floorDiv(Timestamp.ticksPerSecond)
        let fraction = ticks - seconds * Timestamp.ticksPerSecond
        let days = seconds.floorDiv(86_400)
        let secondOfDay = seconds - days * 86_400
        let (year, month, day) = Timestamp.civil(fromDays: days)
        var text = String(format: "%04d-%02d-%02dT%02d:%02d:%02d", Int(year), Int(month), Int(day),
                          Int(secondOfDay / 3600), Int(secondOfDay % 3600 / 60), Int(secondOfDay % 60))
        if fraction > 0 {
            var digits = String(format: "%07d", Int(fraction))
            while digits.hasSuffix("0") { digits.removeLast() }
            text += "." + digits
        }
        return text + "Z"
    }

    // yyyy-MM-ddTHH:mm:ss[.fraction](Z|±HH:mm). Without an offset the time is
    // read as UTC; the payloads this reads always carry one.
    public static func parseISO8601(_ text: String) -> Timestamp? {
        let chars = Array(text.utf8)
        func number(_ start: Int, _ length: Int) -> Int64? {
            guard start + length <= chars.count else { return nil }
            var value: Int64 = 0
            for byte in chars[start..<start + length] {
                guard (0x30...0x39).contains(byte) else { return nil }
                value = value * 10 + Int64(byte - 0x30)
            }
            return value
        }
        guard chars.count >= 19, chars[4] == UInt8(ascii: "-"), chars[7] == UInt8(ascii: "-"),
              chars[10] == UInt8(ascii: "T") || chars[10] == UInt8(ascii: " "),
              chars[13] == UInt8(ascii: ":"), chars[16] == UInt8(ascii: ":"),
              let year = number(0, 4), let month = number(5, 2), let day = number(8, 2),
              let hour = number(11, 2), let minute = number(14, 2), let second = number(17, 2),
              (1...12).contains(month), (1...31).contains(day), hour < 24, minute < 60, second < 60
        else { return nil }

        var index = 19
        var fractionTicks: Int64 = 0
        if index < chars.count, chars[index] == UInt8(ascii: ".") {
            index += 1
            var scale: Int64 = 1_000_000
            let start = index
            while index < chars.count, (0x30...0x39).contains(chars[index]) {
                if scale > 0 { fractionTicks += Int64(chars[index] - 0x30) * scale; scale /= 10 }
                index += 1
            }
            if index == start { return nil }
        }

        var offsetSeconds: Int64 = 0
        if index < chars.count {
            if chars[index] == UInt8(ascii: "Z") {
                index += 1
            } else if chars[index] == UInt8(ascii: "+") || chars[index] == UInt8(ascii: "-") {
                guard let offsetHours = number(index + 1, 2), index + 3 < chars.count, chars[index + 3] == UInt8(ascii: ":"),
                      let offsetMinutes = number(index + 4, 2) else { return nil }
                let sign: Int64 = chars[index] == UInt8(ascii: "-") ? -1 : 1
                offsetSeconds = sign * (offsetHours * 3600 + offsetMinutes * 60)
                index += 6
            } else {
                return nil
            }
        }
        guard index == chars.count else { return nil }

        let days = days(fromCivil: year, month, day)
        let seconds = days * 86_400 + hour * 3600 + minute * 60 + second - offsetSeconds
        return Timestamp(ticks: seconds * ticksPerSecond + fractionTicks)
    }

    // Howard Hinnant's days_from_civil / civil_from_days (proleptic Gregorian).
    static func days(fromCivil year: Int64, _ month: Int64, _ day: Int64) -> Int64 {
        let y = month <= 2 ? year - 1 : year
        let era = y.floorDiv(400)
        let yoe = y - era * 400
        let mp = (month + 9) % 12
        let doy = (153 * mp + 2) / 5 + day - 1
        let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy
        return era * 146_097 + doe - 719_468
    }

    static func civil(fromDays days: Int64) -> (Int64, Int64, Int64) {
        let z = days + 719_468
        let era = z.floorDiv(146_097)
        let doe = z - era * 146_097
        let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100)
        let mp = (5 * doy + 2) / 153
        let day = doy - (153 * mp + 2) / 5 + 1
        let month = mp < 10 ? mp + 3 : mp - 9
        return (month <= 2 ? yoe + era * 400 + 1 : yoe + era * 400, month, day)
    }
}

extension Int64 {
    func floorDiv(_ divisor: Int64) -> Int64 {
        let quotient = self / divisor
        return (self % divisor != 0 && (self < 0) != (divisor < 0)) ? quotient - 1 : quotient
    }
}
