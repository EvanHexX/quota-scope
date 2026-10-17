using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuotaScope.Providers;

namespace QuotaScope.WinUI;

// One line of the popup: a bar row (Right is always null), or one or two
// cards of one shape side by side.
internal sealed record LayoutLine(string Shape, UsageRow Left, UsageRow? Right);

// Which rows the popup shows, in what order, in how many columns, and which
// cards share a line. Kept apart from the WinUI drawing code so the macOS
// port can follow the same rules; checked against fixtures/layout
// (docs/MACOS_BUILD.md, section 5).
internal static class RowLayout
{
    // The visible rows of each provider, in the user's order. The order
    // decides which gauges end up adjacent, and so which ones pair up.
    public static List<(ProviderUsage Usage, List<UsageRow> Rows)> Sections(AppSettings settings, IEnumerable<ProviderUsage> usages)
    {
        var sections = new List<(ProviderUsage, List<UsageRow>)>();
        foreach (var usage in usages)
        {
            var visible = usage.Rows.Where(row => RowShapes.IsVisible(settings, usage.ProviderId, row));
            sections.Add((usage, RowShapes.Order(settings, usage.ProviderId, visible, row => row.Label)));
        }
        return sections;
    }

    // Two columns only pay off when some section actually has a pair of gauges
    // to place side by side; otherwise everything stacks in one column and bar
    // rows get the single-column width. The user can force either column count.
    public static bool UsesTwoColumns(AppSettings settings, IEnumerable<(ProviderUsage Usage, List<UsageRow> Rows)> sections)
    {
        if (string.Equals(settings.LayoutColumns, "OneColumn", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(settings.LayoutColumns, "TwoColumns", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var (usage, rows) in sections)
        {
            var circles = 0;
            foreach (var row in rows)
            {
                if (RowShapes.Resolve(settings, usage.ProviderId, row) == RowShapes.Circle && ++circles >= 2)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // Consecutive cards of one shape pair up; a bar row always spans the full
    // content width and flushes any pending card first. A fill card never
    // shares a line with a gauge several times its height, so a change of
    // shape flushes as well, leaving the pending card full width. In one
    // column every row gets a line of its own.
    public static List<LayoutLine> Lines(AppSettings settings, string providerId, IReadOnlyList<UsageRow> rows, bool twoColumn)
    {
        var lines = new List<LayoutLine>();
        UsageRow? pending = null;
        var pendingShape = RowShapes.Circle;

        void FlushPending()
        {
            if (pending is null) return;
            lines.Add(new LayoutLine(pendingShape, pending, null));
            pending = null;
        }

        foreach (var row in rows)
        {
            var shape = RowShapes.Resolve(settings, providerId, row);
            if (shape == RowShapes.Bars)
            {
                FlushPending();
                lines.Add(new LayoutLine(shape, row, null));
                continue;
            }

            if (!twoColumn)
            {
                lines.Add(new LayoutLine(shape, row, null));
                continue;
            }

            if (pending is not null && shape != pendingShape)
            {
                FlushPending();
            }

            if (pending is null)
            {
                pending = row;
                pendingShape = shape;
                continue;
            }

            lines.Add(new LayoutLine(shape, pending, row));
            pending = null;
        }

        FlushPending();
        return lines;
    }

    // Samples live in fixtures/layout (docs/MACOS_BUILD.md, section 5).
    public static bool RunSelfTest() => UsageFixtures.Run("layout", EvaluateFixture);

    // Each provider ({ providerId, rows: [{ label, isPrimary }] }) under the
    // case's settings is answered by the lines the popup draws, each a shape
    // and the labels of its one or two rows, plus the column decision.
    private static JsonNode EvaluateFixture(JsonElement input)
    {
        var settings = UsageFixtures.ReadSettings(UsageFixtures.Optional(input, "settings"));
        var usages = input.GetProperty("providers").EnumerateArray().Select(provider => new ProviderUsage(
            provider.GetProperty("providerId").GetString()!,
            provider.GetProperty("providerId").GetString()!,
            provider.GetProperty("rows").EnumerateArray()
                .Select(row => new UsageRow(row.GetProperty("label").GetString()!, null, row.GetProperty("isPrimary").GetBoolean()))
                .ToArray(),
            0d, "", DateTimeOffset.Now, ProviderState.Ok)).ToArray();

        var sections = Sections(settings, usages);
        var twoColumn = UsesTwoColumns(settings, sections);
        return new JsonObject
        {
            ["twoColumn"] = twoColumn,
            ["sections"] = new JsonArray(sections.Select(section => (JsonNode)new JsonObject
            {
                ["providerId"] = section.Usage.ProviderId,
                ["lines"] = new JsonArray(Lines(settings, section.Usage.ProviderId, section.Rows, twoColumn)
                    .Select(line => (JsonNode)new JsonObject
                    {
                        ["shape"] = line.Shape,
                        ["rows"] = new JsonArray(new[] { line.Left, line.Right }
                            .Where(row => row is not null)
                            .Select(row => (JsonNode)row!.Label)
                            .ToArray())
                    }).ToArray())
            }).ToArray())
        };
    }
}
