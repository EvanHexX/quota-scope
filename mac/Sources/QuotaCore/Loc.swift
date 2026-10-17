import Foundation

// Port of the row naming in app-winui/Loc.cs. The C# helper keeps the
// language in a static; here it is a value, so the UI and the alerts can each
// hold the one they use. The app's own strings go in the String Catalog;
// what lives here is wording the fixtures pin down.
public struct Loc: Equatable {
    public var isKorean: Bool

    public init(isKorean: Bool) { self.isKorean = isKorean }

    public static let english = Loc(isKorean: false)
    public static let korean = Loc(isKorean: true)

    // "KO" / "Korean" / "한국어" and "EN" / "English"; anything else follows
    // the system language.
    public init(setting: String?, systemLanguageCode: String) {
        switch setting?.uppercased() {
        case "KO", "KOREAN", "한국어": isKorean = true
        case "EN", "ENGLISH": isKorean = false
        default: isKorean = systemLanguageCode.lowercased() == "ko"
        }
    }

    public func t(_ en: String, _ ko: String) -> String { isKorean ? ko : en }

    // Provider row labels are produced in English: a window ("5h", "7d"),
    // optionally with a scope ("7d Fable", Codex's "Spark 7d"). Every display
    // name reads "<scope> · <window>", and a scope is spelled out only where it
    // tells rows of one window apart: Claude's single session window is just
    // "5h", while its all-models and per-model weekly windows both carry one.
    // A row's own scope (Codex's plan on a weekly-only account) takes the
    // same place.
    public func rowLabel(_ providerId: String, _ label: String, scope: String? = nil) -> String {
        if label.isEmpty { return label }
        if label.caseInsensitiveCompare("Credits") == .orderedSame { return t("Credits", "크레딧") }

        if providerId.caseInsensitiveCompare("codex") == .orderedSame, label.lowercased().hasPrefix("spark ") {
            return scoped("Spark", String(label.dropFirst("Spark ".count)))
        }

        if providerId.caseInsensitiveCompare("claude") == .orderedSame {
            // The session window counts every model, so it needs no scope; the
            // unscoped weekly window does, next to the per-model ones.
            if label == "7d" { return scoped(t("All models", "전체 모델"), label) }
            if let space = label.firstIndex(of: " "), space > label.startIndex {
                return scoped(String(label[label.index(after: space)...]), String(label[..<space]))
            }
        }

        return scoped(scope, label)
    }

    private func scoped(_ scope: String?, _ window: String) -> String {
        guard let scope, !scope.isEmpty else { return windowLabel(window) }
        return "\(scope) · \(windowLabel(window))"
    }

    // Weekly windows read better spelled out than as "7d"; anything shorter
    // keeps its duration ("5h" / "5시간").
    private func windowLabel(_ label: String) -> String {
        if label == "7d" || label == "1w" { return t("Weekly", "주간") }
        return durationLabel(label)
    }

    // Port of Loc.ResetText: a row's reset time as the popup shows it, the
    // clock time of the reset or how long until it. `utcOffsetSeconds` is the
    // local offset of `now`; the reset is shown in that offset. Formatted by
    // hand rather than with DateFormatter, so the text matches the C# side
    // exactly (fixtures/resettext) whatever the system locale data.
    public func resetText(_ resetsAt: Timestamp?, now: Timestamp, utcOffsetSeconds: Int, remaining: Bool) -> String {
        guard let reset = resetsAt else { return t("reset --", "재설정 --") }
        return remaining
            ? resetIn(reset.ticks - now.ticks)
            : resetClock(reset, now: now, utcOffsetSeconds: utcOffsetSeconds)
    }

    // Same-day windows show only the time, longer ones (weekly) also the date.
    func resetClock(_ reset: Timestamp, now: Timestamp, utcOffsetSeconds: Int) -> String {
        let offset = Int64(utcOffsetSeconds) * Timestamp.ticksPerSecond
        let ticksPerDay = 86_400 * Timestamp.ticksPerSecond
        let local = reset.ticks + offset
        let day = local.floorDiv(ticksPerDay)
        let isToday = day == (now.ticks + offset).floorDiv(ticksPerDay)
        let minuteOfDay = (local - day * ticksPerDay) / (60 * Timestamp.ticksPerSecond)
        let hour = Int(minuteOfDay / 60), minute = Int(minuteOfDay % 60)
        let hour12 = hour % 12 == 0 ? 12 : hour % 12
        let time = "\(hour12):" + (minute < 10 ? "0\(minute)" : "\(minute)")
        let (_, month, dayOfMonth) = Timestamp.civil(fromDays: day)
        if isKorean {
            let clock = "\(hour < 12 ? "오전" : "오후") \(time)"
            return isToday ? "\(clock) 초기화" : "\(month)월 \(dayOfMonth)일 \(clock) 초기화"
        }
        let clock = "\(time) \(hour < 12 ? "AM" : "PM")"
        let months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]
        return isToday ? "resets at \(clock)" : "resets \(months[Int(month) - 1]) \(dayOfMonth), \(clock)"
    }

    // TimeSpan semantics: whole days and the hour within the day from a day
    // up, whole hours and the minute within the hour from an hour up, else
    // minutes, at least 1.
    func resetIn(_ remainingTicks: Int64) -> String {
        if remainingTicks <= 0 { return t("resets now", "곧 초기화") }
        let minute = 60 * Timestamp.ticksPerSecond, hourTicks = 60 * minute, dayTicks = 24 * hourTicks
        if remainingTicks >= dayTicks {
            let days = remainingTicks / dayTicks, hours = remainingTicks / hourTicks % 24
            return t("resets in \(days)d \(hours)h", "\(days)일 \(hours)시간 뒤 초기화")
        }
        if remainingTicks >= hourTicks {
            let hours = remainingTicks / hourTicks, minutes = remainingTicks / minute % 60
            return t("resets in \(hours)h \(minutes)m", "\(hours)시간 \(minutes)분 뒤 초기화")
        }
        let minutes = max(1, remainingTicks / minute % 60)
        return t("resets in \(minutes)m", "\(minutes)분 뒤 초기화")
    }

    // (\d+)([hdw])\b, with h/d/w spelled out in Korean.
    private func durationLabel(_ label: String) -> String {
        guard isKorean else { return label }
        let chars = Array(label)
        var out = ""
        var index = 0
        while index < chars.count {
            if chars[index].isASCIIDigit, index == 0 || !chars[index - 1].isASCIIDigit {
                var end = index
                while end < chars.count, chars[end].isASCIIDigit { end += 1 }
                let unit: Character? = end < chars.count ? chars[end] : nil
                let boundary = end + 1 >= chars.count || !(chars[end + 1].isLetter || chars[end + 1].isNumber || chars[end + 1] == "_")
                if let unit, "hdw".contains(unit), boundary {
                    let digits = String(chars[index..<end])
                    out += digits + (unit == "h" ? "시간" : unit == "d" ? "일" : "주")
                    index = end + 1
                    continue
                }
                out += String(chars[index..<end])
                index = end
                continue
            }
            out.append(chars[index])
            index += 1
        }
        return out
    }
}
