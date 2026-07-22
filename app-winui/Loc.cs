using System;
using System.Globalization;

namespace QuotaScope.WinUI;

// Minimal two-language string helper. Call sites pass both languages inline;
// "System" resolves from the current UI culture at startup.
internal static class Loc
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
}
