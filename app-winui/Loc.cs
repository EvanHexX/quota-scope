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
    // "7d Fable"); only the duration tokens are localized so model names stay
    // recognizable.
    public static string RowLabel(string label)
    {
        if (!IsKorean || string.IsNullOrEmpty(label)) return label;
        var localized = DurationTokenRegex().Replace(label, match => match.Groups[2].Value switch
        {
            "h" => match.Groups[1].Value + "시간",
            "d" => match.Groups[1].Value + "일",
            _ => match.Groups[1].Value + "주"
        });
        return localized == "Credits" ? "크레딧" : localized.Replace("Credits", "크레딧");
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
            "OneColumn" => "One column",
            "TwoColumns" => "Two columns",
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
            "Auto" => "자동",
            "OneColumn" => "1열",
            "TwoColumns" => "2열",
            _ => value
        };
    }

    [GeneratedRegex(@"(\d+)([hdw])\b")]
    private static partial Regex DurationTokenRegex();
}
