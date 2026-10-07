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

    // Provider row labels are produced in English: a window ("5h", "7d"),
    // optionally with a scope ("7d Fable", Codex's "Spark 7d"). Every display
    // name reads "<scope> · <window>", and a scope is spelled out only where it
    // tells rows of one window apart: Claude's single session window is just
    // "5h", while its all-models and per-model weekly windows both carry one.
    // A row's own Scope (Codex's plan on a weekly-only account) takes the
    // same place. The popup, the tray tooltip, and the settings row list all
    // read this, so the names stay short enough for the 127-character tooltip.
    public static string RowLabel(string providerId, string label, string? scope = null)
    {
        if (string.IsNullOrEmpty(label)) return label;
        if (label.Equals("Credits", StringComparison.OrdinalIgnoreCase)) return T("Credits", "크레딧");

        if (providerId.Equals("codex", StringComparison.OrdinalIgnoreCase)
            && label.StartsWith("Spark ", StringComparison.OrdinalIgnoreCase))
        {
            return Scoped("Spark", label["Spark ".Length..]);
        }

        if (providerId.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            // The session window counts every model, so it needs no scope; the
            // unscoped weekly window does, next to the per-model ones.
            if (label == "7d") return Scoped(T("All models", "전체 모델"), label);
            var space = label.IndexOf(' ');
            if (space > 0) return Scoped(label[(space + 1)..], label[..space]);
        }

        return Scoped(scope, label);
    }

    private static string Scoped(string? scope, string window) =>
        string.IsNullOrEmpty(scope) ? WindowLabel(window) : $"{scope} · {WindowLabel(window)}";

    // Weekly windows read better spelled out than as "7d"; anything shorter
    // keeps its duration ("5h" / "5시간").
    private static string WindowLabel(string label)
    {
        if (label is "7d" or "1w") return T("Weekly", "주간");
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
            "Fill" => "채우기",
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

    // Row labels are what the popup, the tray tooltip, and the settings row
    // list read, so each window a provider reports has to come out distinct.
    // Leaves the language set: the self-test path exits right after.
    public static bool RunSelfTest()
    {
        SetLanguage("English");
        if (RowLabel("codex", "5h") != "5h" || RowLabel("codex", "7d") != "Weekly") return false;
        if (RowLabel("codex", "7d", "Pro") != "Pro · Weekly") return false;
        if (RowLabel("codex", "Spark 5h") != "Spark · 5h" || RowLabel("codex", "Spark 7d") != "Spark · Weekly") return false;
        if (RowLabel("claude", "5h") != "5h" || RowLabel("claude", "7d") != "All models · Weekly") return false;
        if (RowLabel("claude", "7d Fable") != "Fable · Weekly") return false;
        if (!LabelsDistinct()) return false;

        SetLanguage("Korean");
        if (RowLabel("codex", "5h") != "5시간" || RowLabel("codex", "7d") != "주간") return false;
        if (RowLabel("codex", "7d", "Pro") != "Pro · 주간") return false;
        if (RowLabel("codex", "Spark 5h") != "Spark · 5시간" || RowLabel("codex", "Spark 7d") != "Spark · 주간") return false;
        if (RowLabel("claude", "5h") != "5시간" || RowLabel("claude", "7d") != "전체 모델 · 주간") return false;
        if (RowLabel("claude", "7d Fable") != "Fable · 주간") return false;
        return LabelsDistinct();

        // A Plus account shows Codex's two main windows and Spark's two at
        // once; Claude shows its session window next to two weekly ones.
        static bool LabelsDistinct() =>
            Distinct("codex", "5h", "7d", "Spark 5h", "Spark 7d") && Distinct("claude", "5h", "7d", "7d Fable");

        static bool Distinct(string providerId, params string[] labels) =>
            new HashSet<string>(labels.Select(label => RowLabel(providerId, label))).Count == labels.Length;
    }

    [GeneratedRegex(@"(\d+)([hdw])\b")]
    private static partial Regex DurationTokenRegex();
}
