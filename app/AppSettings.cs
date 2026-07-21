using System;
using System.IO;
using System.Text.Json;

namespace QuotaScope;

internal sealed class AppSettings
{
    public string Hotkey { get; set; } = "Ctrl+Alt+U";
    public int RefreshSeconds { get; set; } = 60;
    public int WarningThresholdPercent { get; set; } = 20;
    public string PopupGraph { get; set; } = "half-circle";
    public string CodexCommand { get; set; } = "codex";
    public string PopupPosition { get; set; } = "BottomRight";
    public string ShapeTheme { get; set; } = "Bars";
    public string ColorTheme { get; set; } = "DarkBluePurple";
    public string TimeDisplayMode { get; set; } = "ClockTime";
    public bool IsPinned { get; set; } = false;
    public bool ShowSparkUsage { get; set; } = false;

    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            return settings ?? new AppSettings();
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


