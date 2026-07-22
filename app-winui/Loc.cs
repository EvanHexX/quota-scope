using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace QuotaScope.WinUI;

// Minimal two-language string helper. Call sites pass both languages inline;
// "System" resolves from the current UI culture at startup.
internal static partial class Loc
{
    public static bool IsKorean { get; private set; }

    public static void SetLanguage(string? setting)
    {
        IsKorean = setting?.ToUpperInvariant() switch
        {
            "KO" or "KOREAN" or "한국어" => true,
            "EN" or "ENGLISH" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ko", StringComparison.OrdinalIgnoreCase)
        };
    }

    public static string T(string en, string ko) => IsKorean ? ko : en;

    // Provider row labels are produced in English ("5h", "7d", "Spark 7d",
    // "7d Fable"). Display names follow each provider's own vocabulary: Codex
    // calls its overall weekly window "General", Claude phrases windows the way
    // its /usage view does.
    public static string RowLabel(string providerId, string label)
    {
        if (string.IsNullOrEmpty(label)) return label;
        if (label.Equals("Credits", StringComparison.OrdinalIgnoreCase)) return T("Credits", "크레딧");

        if (providerId.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            if (label.StartsWith("Spark ", StringComparison.OrdinalIgnoreCase)) return "Spark";
            if (label is "7d" or "1w") return T("General", "일반");
            return DurationLabel(label);
        }

        if (providerId.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            if (label == "5h") return T("5-hour limit", "5시간 한도");
            if (label == "7d") return T("Weekly · All models", "주간 · 전체 모델");
            if (label.StartsWith("7d ", StringComparison.Ordinal))
            {
                var model = label[3..];
                return T($"Weekly · {model}", $"주간 · {model}");
            }
            return DurationLabel(label);
        }

        return DurationLabel(label);
    }

    private static string DurationLabel(string label)
    {
        if (!IsKorean) return label;
        return DurationTokenRegex().Replace(label, match => match.Groups[2].Value switch
        {
            "h" => match.Groups[1].Value + "시간",
            "d" => match.Groups[1].Value + "일",
            _ => match.Groups[1].Value + "주"
        });
    }

    // Reset clock time: same-day windows show only the time, longer windows
    // (weekly) also need the date. Both spell out that it is a reset moment.
    public static string ResetClock(DateTimeOffset localReset)
    {
        var korean = new CultureInfo("ko-KR");
        var isToday = localReset.Date == DateTimeOffset.Now.Date;
        if (IsKorean)
        {
            return isToday
                ? localReset.ToString("tt h:mm", korean) + " 초기화"
                : localReset.ToString("M월 d일 tt h:mm", korean) + " 초기화";
        }
        return isToday
            ? "resets at " + localReset.ToString("h:mm tt", CultureInfo.InvariantCulture)
            : "resets " + localReset.ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture);
    }

    public static string ResetIn(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return T("resets now", "곧 초기화");
        if (remaining.TotalDays >= 1)
        {
            var days = (int)remaining.TotalDays;
            return T($"resets in {days}d {remaining.Hours}h", $"{days}일 {remaining.Hours}시간 뒤 초기화");
        }
        if (remaining.TotalHours >= 1)
        {
            var hours = (int)remaining.TotalHours;
            return T($"resets in {hours}h {remaining.Minutes}m", $"{hours}시간 {remaining.Minutes}분 뒤 초기화");
        }
        var minutes = Math.Max(1, remaining.Minutes);
        return T($"resets in {minutes}m", $"{minutes}분 뒤 초기화");
    }

    // Display text for settings values that are stored as stable English keys.
    public static string Option(string value)
    {
        if (!IsKorean) return value switch
        {
            "BentoCircles" => "Gauges",
            "MixMatch" => "Mix & match",
            "ClockTime" => "Clock time",
            "RemainingTime" => "Remaining time",
            "UsageArc" => "Usage arc",
            "BottomRight" => "Bottom right",
            "TopRight" => "Top right",
            "TopLeft" => "Top left",
            "BottomLeft" => "Bottom left",
            "NearCursor" => "Near cursor",
            "LastPosition" => "Last position",
            "OneColumn" => "One column",
            "TwoColumns" => "Two columns",
            "VeryStrong" => "Very strong",
            _ => value
        };

        return value switch
        {
            "Bars" => "막대",
            "BentoCircles" => "게이지",
            "MixMatch" => "믹스 & 매치",
            "Circle" => "게이지",
            "Dark" => "다크",
            "Light" => "라이트",
            "Midnight" => "미드나잇",
            "ClockTime" => "예정 시각",
            "RemainingTime" => "남은 시간",
            "Used" => "사용량",
            "Remaining" => "잔여량",
            "UsageArc" => "사용률 호",
            "Glyph" => "점",
            "System" => "시스템",
            "English" => "English",
            "BottomRight" => "오른쪽 아래",
            "TopRight" => "오른쪽 위",
            "TopLeft" => "왼쪽 위",
            "BottomLeft" => "왼쪽 아래",
            "Center" => "가운데",
            "NearCursor" => "커서 근처",
            "LastPosition" => "마지막 위치",
            "Auto" => "자동",
            "OneColumn" => "1열",
            "TwoColumns" => "2열",
            "Subtle" => "은은하게",
            "Medium" => "보통",
            "Strong" => "강하게",
            "VeryStrong" => "매우 강하게",
            _ => value
        };
    }

    [GeneratedRegex(@"(\d+)([hdw])\b")]
    private static partial Regex DurationTokenRegex();
}
