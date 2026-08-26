using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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
}

internal sealed class AppSettings
{
    public string Hotkey { get; set; } = "Ctrl+Alt+U";
    public string HotkeyRefreshAll { get; set; } = "";
    public string HotkeyTogglePin { get; set; } = "";
    public int WarningThresholdPercent { get; set; } = 20;
    public bool FollowSystemTheme { get; set; } = true;
    public string ThemeOverride { get; set; } = "Dark";
    public bool Autostart { get; set; } = false;
    // Per-row shape overrides used when ShapeTheme is "MixMatch".
    // Key: "<providerId>|<row label>", value: "Circle" | "Bars".
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

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            settings.GetProvider("codex");
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
