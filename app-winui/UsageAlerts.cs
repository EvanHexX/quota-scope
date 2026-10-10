using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuotaScope.Providers;

namespace QuotaScope.WinUI;

// One row passing one of its alert points. Remaining is what the row had left
// when the poll saw it; Critical says it is at or past the critical level.
internal sealed record UsageAlert(string ProviderId, string ProviderName, UsageRow Row, double Point, double Remaining, bool Critical);

// Usage alerts: two levels (warning, critical) that color the tray icon and
// the popup rows, and any number of notification points per row. Everything is
// a remaining percentage, like the warning threshold the app started with.
internal static class UsageAlerts
{
    public const int MaxPoints = 10;

    public static double WarningAtUsed(AppSettings settings) => 100d - Math.Clamp(settings.WarningThresholdPercent, 0, 100);

    public static double CriticalAtUsed(AppSettings settings) => 100d - Math.Clamp(settings.CriticalThresholdPercent, 0d, 100d);

    // A row without an override notifies when the provider marks it primary:
    // the rows that always drove the warning, plus a Claude per-model limit
    // while it is the active one. Spark, credits and inactive per-model rows
    // stay quiet.
    public static bool IsEnabled(AppSettings settings, string providerId, string label, bool isPrimary) =>
        settings.RowAlerts.TryGetValue(RowShapes.Key(providerId, label), out var enabled) ? enabled : isPrimary;

    public static IReadOnlyList<double> PointsFor(AppSettings settings, string providerId, string label) =>
        settings.RowAlertPoints.TryGetValue(RowShapes.Key(providerId, label), out var own) && own is { Count: > 0 }
            ? own
            : DefaultPoints(settings);

    // No points of its own means the warning and critical levels, which is
    // what the single warning notification used to cover.
    public static IReadOnlyList<double> DefaultPoints(AppSettings settings) =>
        settings.AlertPoints.Count > 0
            ? settings.AlertPoints
            : new[] { (double)settings.WarningThresholdPercent, settings.CriticalThresholdPercent };

    // "80, 60, 40" (commas, semicolons or spaces; a trailing % is fine) into
    // distinct points strictly between 0 and 100, highest first. Empty text is
    // an empty list, which means "use the default". The comma is a separator,
    // so decimals take a point: "2.5".
    public static bool TryParsePoints(string? text, out List<double> points, out string? error)
    {
        points = new List<double>();
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        foreach (var token in text.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var number = token.TrimEnd('%');
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            {
                error = Loc.T($"\"{token}\" is not a number.", $"\"{token}\"은(는) 숫자가 아닙니다.");
                return false;
            }
            // Stored as shown: the box writes points back with two decimals.
            value = Math.Round(value, 2);
            if (value <= 0 || value >= 100)
            {
                error = Loc.T($"{token}: a point must be greater than 0 and less than 100 (remaining %).",
                              $"{token}: 지점은 0보다 크고 100보다 작은 남은 양 %여야 합니다.");
                return false;
            }
            if (!points.Contains(value)) points.Add(value);
        }

        if (points.Count > MaxPoints)
        {
            error = Loc.T($"At most {MaxPoints} points.", $"지점은 최대 {MaxPoints}개입니다.");
            return false;
        }
        points.Sort((a, b) => b.CompareTo(a));
        return true;
    }

    public static string FormatPoints(IEnumerable<double> points) =>
        string.Join(", ", points.Select(point => point.ToString("0.##", CultureInfo.InvariantCulture)));

    // Everything one check passed goes into one notification, titled critical
    // when any of it is at the critical level. Win32 cuts balloon text at 255.
    public static (string Title, string Body) BuildNotification(IReadOnlyList<UsageAlert> alerts)
    {
        var title = alerts.Any(alert => alert.Critical)
            ? Loc.T("Usage critical", "사용량 위험")
            : Loc.T("Usage warning", "사용량 경고");
        var body = string.Join("\n", alerts.Select(alert =>
        {
            // Rounded the way the popup and the tooltip round it.
            var left = 100 - (int)Math.Round(100d - alert.Remaining);
            var name = Loc.RowLabel(alert.ProviderId, alert.Row.Label, alert.Row.Scope);
            return $"{alert.ProviderName} {name} " + Loc.T($"{left}% left", $"{left}% 남음");
        }));
        return (title, body.Length <= 255 ? body : body[..255]);
    }

    public static bool RunSelfTest()
    {
        if (!TryParsePoints("80, 60,40%", out var parsed, out _) || FormatPoints(parsed) != "80, 60, 40") return false;
        if (!TryParsePoints("2.5 20;20", out parsed, out _) || FormatPoints(parsed) != "20, 2.5") return false;
        if (!TryParsePoints("  ", out parsed, out _) || parsed.Count != 0) return false;
        if (TryParsePoints("80, abc", out _, out _) || TryParsePoints("0", out _, out _) || TryParsePoints("100", out _, out _)) return false;
        // Rounded to what the box shows before the range check: 99.999 is 100.
        if (!TryParsePoints("33.333", out parsed, out _) || parsed[0] != 33.33
            || TryParsePoints("99.999", out _, out _) || TryParsePoints("0.001", out _, out _))
        {
            return false;
        }

        return RunTrackerSelfTest() && RunDefaultsSelfTest() && RunEdgeCasesSelfTest();
    }

    private static bool RunEdgeCasesSelfTest()
    {
        var settings = new AppSettings { AlertPoints = new List<double> { 80, 60, 40 } };
        var tracker = new UsageAlertTracker();
        var weekly = Usage(new UsageRow("7d", Window(65), IsPrimary: true));
        var offline = ProviderUsage.Offline("codex", "Codex", "Connecting to Codex...", ProviderState.Unavailable);

        // An offline gap (a reconnect) does not replay a point already passed.
        if (tracker.Evaluate(settings, new[] { weekly }).Count != 1) return false;
        if (tracker.Evaluate(settings, new[] { offline }).Count != 0) return false;
        if (tracker.Evaluate(settings, new[] { weekly }).Count != 0) return false;

        // Switching a row off and on again does not replay it either.
        settings.RowAlerts[RowShapes.Key("codex", "7d")] = false;
        if (tracker.Evaluate(settings, new[] { weekly }).Count != 0) return false;
        settings.RowAlerts[RowShapes.Key("codex", "7d")] = true;
        if (tracker.Evaluate(settings, new[] { weekly }).Count != 0) return false;

        // Two undated windows share the "usage" key: only the first is tracked,
        // so the second cannot re-arm it on every check.
        var twins = new ProviderUsage("codex", "Codex", new[]
        {
            new UsageRow("usage", new RateLimitWindow(85, null, null), IsPrimary: true),
            new UsageRow("usage", new RateLimitWindow(30, null, null), IsPrimary: true)
        }, 85, "", DateTimeOffset.Now, ProviderState.Ok);
        if (tracker.Evaluate(settings, new[] { twins }).Count != 1 || tracker.Evaluate(settings, new[] { twins }).Count != 0) return false;

        // Both providers in one check give one alert each, in one notification;
        // a text-only row never alerts.
        var both = new[]
        {
            Usage(new UsageRow("5h", Window(50), IsPrimary: true)),
            new ProviderUsage("claude", "Claude", new[]
            {
                new UsageRow("5h", Window(70), IsPrimary: true),
                new UsageRow("Credits", null, IsPrimary: true, "12")
            }, 70, "", DateTimeOffset.Now, ProviderState.Ok)
        };
        var alerts = tracker.Evaluate(settings, both);
        if (alerts.Count != 2 || BuildNotification(alerts).Body.Split('\n').Length != 2) return false;

        // A custom point past the critical level is reported as critical.
        var deep = new AppSettings { AlertPoints = new List<double> { 1 } };
        return new UsageAlertTracker().Evaluate(deep, new[] { Usage(new UsageRow("5h", Window(99.5), IsPrimary: true)) })
            is [{ Point: 1, Critical: true }];
    }

    // One row descending through 80, 60, 40: each point notifies once, a jump
    // past several notifies the deepest only, and a reset re-arms them.
    private static bool RunTrackerSelfTest()
    {
        var settings = new AppSettings { AlertPoints = new List<double> { 80, 60, 40 } };
        var tracker = new UsageAlertTracker();
        double? Step(double used) =>
            tracker.Evaluate(settings, new[] { Usage(new UsageRow("7d", Window(used), IsPrimary: true)) }) is { Count: 1 } alerts
                ? alerts[0].Point
                : null;

        if (Step(10) is not null || Step(25) != 80 || Step(30) is not null) return false;
        if (Step(65) != 40 || Step(70) is not null) return false;
        if (Step(5) is not null || Step(45) != 60) return false;

        // Recovering past a shallower point without clearing them all re-arms
        // the deeper ones: 60 again after climbing back to 70% remaining.
        if (Step(30) is not null || Step(41) != 60) return false;

        // Secondary rows stay quiet until switched on; their own points win.
        var spark = new UsageRow("Spark 7d", Window(50), IsPrimary: false);
        if (tracker.Evaluate(settings, new[] { Usage(spark) }).Count != 0) return false;
        settings.RowAlerts[RowShapes.Key("codex", spark.Label)] = true;
        settings.RowAlertPoints[RowShapes.Key("codex", spark.Label)] = new List<double> { 55 };
        if (tracker.Evaluate(settings, new[] { Usage(spark) }) is not [{ Point: 55 }]) return false;

        // A primary row switched off never notifies.
        settings.RowAlerts[RowShapes.Key("codex", "5h")] = false;
        return tracker.Evaluate(settings, new[] { Usage(new UsageRow("5h", Window(99), IsPrimary: true)) }).Count == 0;
    }

    // With no points set, notifications come at the warning and critical
    // levels, and the critical one carries the critical title.
    private static bool RunDefaultsSelfTest()
    {
        var settings = new AppSettings();
        var tracker = new UsageAlertTracker();
        if (tracker.Evaluate(settings, new[] { Usage(new UsageRow("5h", Window(70), IsPrimary: true)) }).Count != 0) return false;
        var warning = tracker.Evaluate(settings, new[] { Usage(new UsageRow("5h", Window(85), IsPrimary: true)) });
        if (warning is not [{ Point: 20, Critical: false }]) return false;
        var critical = tracker.Evaluate(settings, new[] { Usage(new UsageRow("5h", Window(98), IsPrimary: true)) });
        if (critical is not [{ Point: 2.5, Critical: true }]) return false;
        return BuildNotification(critical).Title == Loc.T("Usage critical", "사용량 위험")
            && BuildNotification(warning).Title == Loc.T("Usage warning", "사용량 경고");
    }

    private static RateLimitWindow Window(double used) => new(used, null, 10080);

    private static ProviderUsage Usage(UsageRow row) =>
        new("codex", "Codex", new[] { row }, row.Window?.UsedPercent ?? 0, "", DateTimeOffset.Now, ProviderState.Ok);
}

// Remembers, per row, the deepest point already passed since the row was last
// above all of its points. A row that climbs back above a point (its window
// resets) re-arms it; a row that goes missing (provider offline) keeps its
// state, so coming back does not repeat an alert. Rows whose notifications
// are off are tracked too, so switching one on, or a Claude per-model limit
// turning active, does not replay a point it already passed.
internal sealed class UsageAlertTracker
{
    private readonly Dictionary<string, double> _notified = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<UsageAlert> Evaluate(AppSettings settings, IEnumerable<ProviderUsage> usages)
    {
        var alerts = new List<UsageAlert>();
        var criticalAtUsed = UsageAlerts.CriticalAtUsed(settings);
        // Settings share one key per label, so a second row with the same
        // label (two undated Codex windows) would overwrite the first one's
        // state every check and re-fire it; the first row in payload order wins.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var usage in usages)
        {
            foreach (var row in usage.Rows)
            {
                if (row.Window is null) continue;
                var key = RowShapes.Key(usage.ProviderId, row.Label);
                if (!seen.Add(key)) continue;

                var used = Math.Clamp(row.Window.UsedPercent, 0d, 100d);
                var remaining = 100d - used;
                double? deepest = null;
                foreach (var point in UsageAlerts.PointsFor(settings, usage.ProviderId, row.Label))
                {
                    if (point is > 0 and < 100 && remaining <= point && (deepest is null || point < deepest)) deepest = point;
                }

                if (deepest is not { } passed)
                {
                    _notified.Remove(key);
                    continue;
                }
                if (_notified.TryGetValue(key, out var previous) && passed >= previous)
                {
                    _notified[key] = passed;
                    continue;
                }

                _notified[key] = passed;
                if (UsageAlerts.IsEnabled(settings, usage.ProviderId, row.Label, row.IsPrimary))
                {
                    alerts.Add(new UsageAlert(usage.ProviderId, usage.DisplayName, row, passed, remaining, used >= criticalAtUsed));
                }
            }
        }
        return alerts;
    }
}
