using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuotaScope.Providers;

namespace QuotaScope;

internal sealed class ProviderSettings
{
    public bool Enabled { get; set; } = true;
    public int RefreshSeconds { get; set; } = 60;
    public bool ShowSecondaryRows { get; set; } = false;
    public bool ShowCredits { get; set; } = false;
    public string Command { get; set; } = "codex";
    // Claude only: nudge Claude Code to refresh its own OAuth token before the
    // 8-hour access token expires, instead of going dark until a manual sign-in.
    public bool AutoRenewSession { get; set; } = true;
    // Neither provider reports a credit ceiling, so the credits gauge is drawn
    // against this. See QuotaScope.Providers.CreditsGauge.
    public double CreditsFullAmount { get; set; } = 2500;
}

internal sealed class AppSettings
{
    public string Hotkey { get; set; } = "Ctrl+Alt+U";
    public string HotkeyRefreshAll { get; set; } = "";
    public string HotkeyTogglePin { get; set; } = "";
    // Alert levels and points are remaining percentages, like the original
    // warning threshold. The warning and critical levels color the tray icon
    // and the popup rows; 2.5 keeps the critical level the app used to fix at
    // 97.5% used.
    public int WarningThresholdPercent { get; set; } = 20;
    public double CriticalThresholdPercent { get; set; } = 2.5;
    // Notify as a row's remaining capacity passes each of these. Empty means
    // the warning and critical levels, the notifications the app always gave.
    public List<double> AlertPoints { get; set; } = new();
    // Per-row overrides, same key as RowShapes: RowAlerts turns a row's
    // notifications on or off (absent: on for the provider's primary rows),
    // RowAlertPoints replaces the global points for that row. A credits row's
    // points are amounts of credits left, not percents.
    public Dictionary<string, bool> RowAlerts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<double>> RowAlertPoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FollowSystemTheme { get; set; } = true;
    public string ThemeOverride { get; set; } = "Dark";
    public bool Autostart { get; set; } = false;
    // Per-row shape overrides used when ShapeTheme is "MixMatch".
    // Key: "<providerId>|<row label>", value: "Circle" | "Bars" | "Fill".
    public Dictionary<string, string> RowShapes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // "Auto" | "OneColumn" | "TwoColumns".
    public string LayoutColumns { get; set; } = "Auto";
    // Display order overrides, same key as RowShapes; unlisted rows keep the
    // order the provider reported them in.
    public Dictionary<string, int> RowOrder { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Per-row visibility, same key as RowShapes. A row the user has never
    // touched is absent and falls back to the provider row defaults.
    public Dictionary<string, bool> RowVisibility { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string TrayIconStyle { get; set; } = "UsageArc";
    public string GaugeMetric { get; set; } = "Used";
    public string Language { get; set; } = "System";
    public bool NotifyOnThreshold { get; set; } = true;
    public double UiScale { get; set; } = 1.0;
    public bool Glassmorphism { get; set; } = false;
    // "Subtle" | "Medium" | "Strong".
    public string GlassStrength { get; set; } = "Medium";
    public string PopupGraph { get; set; } = "half-circle";
    public string PopupPosition { get; set; } = "BottomRight";
    // Used by PopupPosition "LastPosition"; int.MinValue means "not recorded yet".
    public int LastPopupX { get; set; } = int.MinValue;
    public int LastPopupY { get; set; } = int.MinValue;
    // "Bars" | "BentoCircles" | "Fill" | "MixMatch". The Windows Forms app
    // draws everything but "BentoCircles" as bars.
    public string ShapeTheme { get; set; } = "Bars";
    public string ColorTheme { get; set; } = "DarkBluePurple";
    public string TimeDisplayMode { get; set; } = "ClockTime";
    public bool IsPinned { get; set; } = false;
    public Dictionary<string, ProviderSettings> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["codex"] = new ProviderSettings()
    };

    public ProviderSettings GetProvider(string id)
    {
        if (!Providers.TryGetValue(id, out var provider))
        {
            provider = new ProviderSettings();
            Providers[id] = provider;
        }
        return provider;
    }

    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    // Set when settings.json existed but could not be read. The app then runs
    // on defaults, and its first save would overwrite the user's file, so the
    // unreadable file is kept next to it as settings.json.bad first.
    public static string? LoadWarning { get; private set; }

    public static AppSettings Load()
    {
        LoadWarning = null;
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            return FromJson(File.ReadAllText(SettingsPath));
        }
        catch (Exception ex)
        {
            var backup = SettingsPath + ".bad";
            try
            {
                File.Copy(SettingsPath, backup, overwrite: true);
                LoadWarning = $"settings.json could not be read ({ex.Message}); using defaults. The file was kept as settings.json.bad.";
            }
            catch
            {
                LoadWarning = $"settings.json could not be read ({ex.Message}); using defaults.";
            }
            return new AppSettings();
        }
    }

    // Load without the file; the self-test fixtures read their settings here.
    public static AppSettings FromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        // A hand-edited null would otherwise reach code that runs on every
        // usage update (row order, visibility, shapes, alert checks).
        settings.Providers ??= new(StringComparer.OrdinalIgnoreCase);
        settings.RowShapes ??= new(StringComparer.OrdinalIgnoreCase);
        settings.RowOrder ??= new(StringComparer.OrdinalIgnoreCase);
        settings.RowVisibility ??= new(StringComparer.OrdinalIgnoreCase);
        settings.AlertPoints ??= new();
        settings.RowAlerts ??= new(StringComparer.OrdinalIgnoreCase);
        settings.RowAlertPoints ??= new(StringComparer.OrdinalIgnoreCase);
        settings.GetProvider("codex");
        return settings;
    }

    // Samples live in fixtures/settings (docs/MACOS_BUILD.md, section 5): each
    // input's "settings" is a whole settings.json, loaded through FromJson.
    public static bool RunSelfTest() =>
        UsageFixtures.Run("settings", input => Describe(FromJson(input.GetProperty("settings").GetRawText())));

    // The fixture form of loaded settings: every key with its value, providers
    // included, in the shape Save writes. It is built from the properties, so
    // a key added here without its expected value fails the fixtures.
    public static JsonNode Describe(AppSettings settings) => JsonSerializer.SerializeToNode(settings)!;

    // Written to a temporary file and moved over the old one, so a crash or
    // power loss mid-write cannot leave a truncated settings.json behind.
    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, SettingsPath, overwrite: true);
    }
}
