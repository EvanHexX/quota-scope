import Foundation

// Port of app/Providers/CreditsGauge.cs.
// Credits are the one row the providers do not hand over as a percentage.
// Codex reports a bare remaining balance and Claude reports credits already
// spent, neither with a ceiling attached. The configured full amount supplies
// the missing denominator so a credits row can render as a gauge.
public enum CreditsGauge {
    public static let defaultFullAmount = 2500.0

    // Zero means "do not draw a gauge", and a hand-edited settings file can
    // hold anything, so callers ask before dividing. NaN slips past "> 0".
    public static func hasUsableCeiling(_ fullAmount: Double) -> Bool {
        fullAmount.isFinite && fullAmount > 0
    }

    // Codex: the payload is what is left, so a full wallet is 0% used.
    public static func usedPercentFromBalance(_ remaining: Double, _ fullAmount: Double) -> Double {
        guard hasUsableCeiling(fullAmount), remaining.isFinite else { return 0 }
        return clamp((1 - remaining / fullAmount) * 100, 0, 100)
    }

    // Claude: the payload is what has been consumed.
    public static func usedPercentFromSpend(_ spent: Double, _ fullAmount: Double) -> Double {
        guard hasUsableCeiling(fullAmount), spent.isFinite else { return 0 }
        return clamp(spent / fullAmount * 100, 0, 100)
    }

    // The full amount is the user's reference, not a real ceiling: these say
    // when the percent text should read ">100%" instead of exactly 100%.
    public static func balanceBeyondFull(_ remaining: Double, _ fullAmount: Double) -> Bool {
        hasUsableCeiling(fullAmount) && remaining.isFinite && remaining > fullAmount
    }

    public static func spendBeyondFull(_ spent: Double, _ fullAmount: Double) -> Bool {
        hasUsableCeiling(fullAmount) && spent.isFinite && spent > fullAmount
    }

    // Both providers render as "left / full".
    public static func formatRemaining(_ remaining: Double, _ fullAmount: Double) -> String {
        "\(NumberText.hundredths(max(0, remaining))) / \(NumberText.hundredths(fullAmount))"
    }
}

// Math.Clamp, which also passes NaN through unchanged.
func clamp(_ value: Double, _ low: Double, _ high: Double) -> Double {
    value.isNaN ? value : min(max(value, low), high)
}
