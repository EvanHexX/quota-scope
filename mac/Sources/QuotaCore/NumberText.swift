import Foundation

// Number parsing and formatting that has to match the C# side character for
// character, because the shared fixtures compare the text (credits footers,
// alert points, notification bodies).
public enum NumberText {
    // C#'s value.ToString("0.##", InvariantCulture) for a double: .NET first
    // takes 15 significant digits, then rounds half away from zero. Rounding
    // the binary value directly (printf) would turn 0.125 into "0.12".
    public static func hundredths(_ value: Double) -> String {
        guard value.isFinite else { return value.isNaN ? "NaN" : (value < 0 ? "-∞" : "∞") }
        guard let decimal = Decimal(string: String(format: "%.14e", value), locale: Locale(identifier: "en_US_POSIX")) else {
            return String(value)
        }
        return hundredths(decimal)
    }

    // C#'s decimal.ToString("0.##"): the exact decimal, rounded half away
    // from zero.
    public static func hundredths(_ value: Decimal) -> String {
        var source = value
        var rounded = Decimal()
        NSDecimalRound(&rounded, &source, 2, .plain)
        if rounded.isZero { return value.isSignMinus ? "-0" : "0" }
        return NSDecimalNumber(decimal: rounded).stringValue
    }

    // C#'s decimal.TryParse(text, NumberStyles.Number, InvariantCulture):
    // surrounding white space, a leading sign, group separators, and a
    // decimal point; no exponent.
    public static func parseNumberStyle(_ text: String) -> (decimal: Decimal, double: Double)? {
        let trimmed = text.trimmingCharacters(in: .whitespaces)
        var body = Substring(trimmed)
        var sign = ""
        if let first = body.first, first == "+" || first == "-" {
            sign = first == "-" ? "-" : ""
            body = body.dropFirst()
        }
        let parts = body.split(separator: ".", maxSplits: 1, omittingEmptySubsequences: false)
        let integer = parts.first.map(String.init) ?? ""
        let fraction = parts.count > 1 ? String(parts[1]) : ""
        guard integer.allSatisfy({ $0.isASCIIDigit || $0 == "," }),
              integer.first != ",",
              fraction.allSatisfy(\.isASCIIDigit),
              (integer + fraction).contains(where: \.isASCIIDigit)
        else { return nil }
        let plain = sign + integer.replacingOccurrences(of: ",", with: "") + (fraction.isEmpty ? "" : "." + fraction)
        let normalized = plain.hasSuffix(".") ? String(plain.dropLast()) : plain
        guard let double = Double(normalized.hasPrefix(".") || normalized.hasPrefix("-.") ? normalized.replacingOccurrences(of: ".", with: "0.") : normalized),
              let decimal = Decimal(string: normalized, locale: Locale(identifier: "en_US_POSIX"))
        else { return nil }
        return (decimal, double)
    }

    // C#'s double.TryParse(text, NumberStyles.Float, InvariantCulture): an
    // optional sign, digits with an optional point, an optional exponent.
    // Swift's Double(String) also takes hex and "inf"; neither is a number here.
    public static func parseFloatStyle(_ text: String) -> Double? {
        let trimmed = text.trimmingCharacters(in: .whitespaces)
        var rest = Substring(trimmed)
        if let first = rest.first, first == "+" || first == "-" { rest = rest.dropFirst() }
        var mantissa = rest
        if let e = rest.firstIndex(where: { $0 == "e" || $0 == "E" }) {
            mantissa = rest[..<e]
            var exponent = rest[rest.index(after: e)...]
            if let first = exponent.first, first == "+" || first == "-" { exponent = exponent.dropFirst() }
            guard !exponent.isEmpty, exponent.allSatisfy(\.isASCIIDigit) else { return nil }
        }
        let pieces = mantissa.split(separator: ".", maxSplits: 1, omittingEmptySubsequences: false)
        guard pieces.allSatisfy({ $0.allSatisfy(\.isASCIIDigit) }),
              mantissa.contains(where: \.isASCIIDigit),
              mantissa.filter({ $0 == "." }).count <= 1
        else { return nil }
        var normalized = trimmed
        if normalized.hasSuffix(".") { normalized.removeLast() }
        normalized = normalized.replacingOccurrences(of: ".e", with: "e").replacingOccurrences(of: ".E", with: "E")
        return Double(normalized)
    }

    // C#'s Math.Round(value, digits): to nearest, ties to even.
    public static func round(_ value: Double, digits: Int = 0) -> Double {
        guard digits > 0 else { return value.rounded(.toNearestOrEven) }
        let scale = pow(10.0, Double(digits))
        return (value * scale).rounded(.toNearestOrEven) / scale
    }
}

extension Character {
    var isASCIIDigit: Bool { ("0"..."9").contains(self) }
}
