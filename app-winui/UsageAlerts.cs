using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QuotaScope.Providers;

namespace QuotaScope.WinUI;

// One row passing one of its alert points. Remaining is what the row had left
// when the poll saw it, in the row's unit: a percent, or credits on a credits
// row. Critical says it is at or past the critical level (credits: none left).
internal sealed record UsageAlert(string ProviderId, string ProviderName, UsageRow Row, double Point, double Remaining, bool Critical)
{
    public bool IsCredits => RowShapes.IsCreditsRow(Row.Label);
}

// Usage alerts: two levels (warning, critical) that color the tray icon and
// the popup rows, and any number of notification points per row. Points are
// remaining percentages, like the warning threshold the app started with,
// except on a credits row: its percent is only relative to the configured
// gauge amount, so its points are amounts of credits left.
internal static partial class UsageAlerts
{
    public const int MaxPoints = 10;
    // Same ceiling the credits gauge amount accepts.
    public const double MaxCredits = 1_000_000;

    public static double WarningAtUsed(AppSettings settings) => 100d - Math.Clamp(settings.WarningThresholdPercent, 0, 100);

    public static double CriticalAtUsed(AppSettings settings) => 100d - Math.Clamp(settings.CriticalThresholdPercent, 0d, 100d);

    // A row without an override notifies when the provider marks it primary:
    // the rows that always drove the warning, plus a Claude per-model limit
    // while it is the active one. Spark, credits and inactive per-model rows
    // stay quiet.
    public static bool IsEnabled(AppSettings settings, string providerId, string label, bool isPrimary) =>
        settings.RowAlerts.TryGetValue(RowShapes.Key(providerId, label), out var enabled) ? enabled : isPrimary;

    public static IReadOnlyList<double> PointsFor(AppSettings settings, string providerId, string label)
    {
        if (settings.RowAlertPoints.TryGetValue(RowShapes.Key(providerId, label), out var own) && own is { Count: > 0 }) return own;
        return RowShapes.IsCreditsRow(label) ? DefaultCreditPoints : DefaultPoints(settings);
    }

    // No points of its own means the warning and critical levels, which is
    // what the single warning notification used to cover.
    public static IReadOnlyList<double> DefaultPoints(AppSettings settings) =>
        settings.AlertPoints.Count > 0
            ? settings.AlertPoints
            : new[] { (double)settings.WarningThresholdPercent, settings.CriticalThresholdPercent };

    // The percent levels mean nothing in credits, so a credits row switched on
    // without points of its own is notified when its credits run out.
    public static readonly IReadOnlyList<double> DefaultCreditPoints = new[] { 0d };

    // "80, 60, 40" (commas, semicolons or spaces; a trailing % is fine) into
    // distinct points, highest first: remaining percents from 0 (none left) up
    // to but excluding 100, or with credits set, amounts of credits from 0.
    // Empty text is an empty list, which means "use the default". The comma is
    // a separator, so decimals take a point: "2.5".
    public static bool TryParsePoints(string? text, bool credits, out List<double> points, out string? error)
    {
        points = new List<double>();
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        // The comma separates points, so "2,000" would quietly become 2 and 0.
        // Amounts of credits are the only points that reach thousands; a comma
        // with three digits after it is refused rather than guessed at.
        if (credits && ThousandsSeparator().Match(text) is { Success: true } grouped)
        {
            error = Loc.T($"\"{grouped.Value}\": write amounts without a thousands separator (e.g. 2000); commas separate points.",
                          $"\"{grouped.Value}\": 수량은 천 단위 쉼표 없이 적어 주세요(예: 2000). 쉼표는 지점을 나눕니다.");
            return false;
        }

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
            if (credits && (value < 0 || value > MaxCredits))
            {
                error = Loc.T($"{token}: a credits point must be from 0 to {MaxCredits:0} credits left.",
                              $"{token}: 크레딧 지점은 0부터 {MaxCredits:0} 사이의 남은 크레딧 수량이어야 합니다.");
                return false;
            }
            if (!credits && (value < 0 || value >= 100))
            {
                error = Loc.T($"{token}: a point must be from 0 up to (not including) 100 (remaining %).",
                              $"{token}: 지점은 0 이상 100 미만의 남은 양 %여야 합니다.");
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
            var name = Loc.RowLabel(alert.ProviderId, alert.Row.Label, alert.Row.Scope);
            if (alert.IsCredits)
            {
                var credits = alert.Remaining.ToString("0.##", CultureInfo.InvariantCulture);
                return $"{alert.ProviderName} {name} " + Loc.T($"{credits} left", $"{credits} 남음");
            }
            // Rounded the way the popup and the tooltip round it.
            var left = 100 - (int)Math.Round(100d - alert.Remaining);
            return $"{alert.ProviderName} {name} " + Loc.T($"{left}% left", $"{left}% 남음");
        }));
        return (title, body.Length <= 255 ? body : body[..255]);
    }

    // Samples live in fixtures/alerts (docs/MACOS_BUILD.md, section 5).
    // Notifications are compared in English; Loc.RunSelfTest sets its own
    // language after this.
    public static bool RunSelfTest()
    {
        Loc.SetLanguage("English");
        return UsageFixtures.Run("alerts", EvaluateFixture);
    }

    // "parse": point texts, answered by the points they give and how the box
    // writes them back, or { "ok": false }. "steps": checks of one tracker in
    // order, each with the usage it sees, answered by the alerts it raises and
    // the notification they make (null when there are none). A step's
    // "settings" apply from that step on; the case's own apply before it.
    private static JsonNode EvaluateFixture(JsonElement input)
    {
        var result = new JsonObject();
        if (UsageFixtures.Optional(input, "parse") is { } texts)
        {
            result["parsed"] = new JsonArray(texts.EnumerateArray().Select(item =>
            {
                if (!TryParsePoints(UsageFixtures.Optional(item, "text")?.GetString(), item.GetProperty("credits").GetBoolean(), out var points, out _))
                {
                    return (JsonNode)new JsonObject { ["ok"] = false };
                }
                return new JsonObject
                {
                    ["ok"] = true,
                    ["points"] = new JsonArray(points.Select(point => (JsonNode)point).ToArray()),
                    ["formatted"] = FormatPoints(points)
                };
            }).ToArray());
        }
        if (UsageFixtures.Optional(input, "steps") is { } steps)
        {
            var settings = UsageFixtures.ReadSettings(UsageFixtures.Optional(input, "settings"));
            var tracker = new UsageAlertTracker(UsageFixtures.Optional(input, "silentFirstSight")?.GetBoolean() ?? false);
            result["steps"] = new JsonArray(steps.EnumerateArray().Select(step =>
            {
                if (UsageFixtures.Optional(step, "settings") is { } stepSettings) settings = UsageFixtures.ReadSettings(stepSettings);
                var alerts = tracker.Evaluate(settings, step.GetProperty("usages").EnumerateArray().Select(UsageFixtures.ReadUsage).ToArray());
                JsonNode? notification = null;
                if (alerts.Count > 0)
                {
                    var (title, body) = BuildNotification(alerts);
                    notification = new JsonObject { ["title"] = title, ["body"] = body };
                }
                return (JsonNode)new JsonObject
                {
                    ["alerts"] = new JsonArray(alerts.Select(alert => (JsonNode)new JsonObject
                    {
                        ["providerId"] = alert.ProviderId,
                        ["label"] = alert.Row.Label,
                        ["point"] = alert.Point,
                        ["remaining"] = alert.Remaining,
                        ["critical"] = alert.Critical
                    }).ToArray()),
                    ["notification"] = notification
                };
            }).ToArray());
        }
        return result;
    }

    [GeneratedRegex(@"\d,\d{3}(?!\d)")]
    private static partial Regex ThousandsSeparator();
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
    // A process that restarted itself (graphics adapters changed) lost the
    // state above, so the points each row has already passed are recorded on
    // first sight instead of being notified a second time.
    private readonly bool _silentFirstSight;
    private readonly HashSet<string> _observed = new(StringComparer.OrdinalIgnoreCase);

    public UsageAlertTracker(bool silentFirstSight = false) => _silentFirstSight = silentFirstSight;

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
                // A credits row compares the credits it has left, so it needs
                // that amount rather than a gauge; every other row its gauge.
                // Both are compared as the popup shows them (whole percents,
                // credits to two decimals), so a row reading "0" has passed 0.
                var credits = RowShapes.IsCreditsRow(row.Label);
                double remaining;
                bool critical;
                if (credits)
                {
                    if (row.CreditsLeft is not { } left) continue;
                    remaining = Math.Round(left, 2);
                    critical = remaining <= 0;
                }
                else
                {
                    if (row.Window is null) continue;
                    var used = Math.Clamp(row.Window.UsedPercent, 0d, 100d);
                    remaining = 100d - Math.Round(used);
                    critical = used >= criticalAtUsed;
                }
                var key = RowShapes.Key(usage.ProviderId, row.Label);
                if (!seen.Add(key)) continue;
                var firstSight = _silentFirstSight && _observed.Add(key);

                // 0 is a point too: it is passed once nothing is left.
                double? deepest = null;
                foreach (var point in UsageAlerts.PointsFor(settings, usage.ProviderId, row.Label))
                {
                    if (point >= 0 && (credits || point < 100) && remaining <= point && (deepest is null || point < deepest)) deepest = point;
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
                if (!firstSight && UsageAlerts.IsEnabled(settings, usage.ProviderId, row.Label, row.IsPrimary))
                {
                    alerts.Add(new UsageAlert(usage.ProviderId, usage.DisplayName, row, passed, remaining, critical));
                }
            }
        }
        return alerts;
    }
}
