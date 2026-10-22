using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaScope.Providers;

// Input/expected pairs under fixtures/ at the repository root, shared with the
// macOS port so both implementations answer the same payload the same way
// (docs/MACOS_BUILD.md, section 5). They are embedded into the exe because
// --self-test also runs from a release build with no repository next to it.
// C# is the reference: a mapper change and its fixture change land together.
internal static class UsageFixtures
{
    private const string ResourcePrefix = "fixtures/";
    private const string InputSuffix = ".input.json";
    private const string ExpectedSuffix = ".expected.json";
    private const double Tolerance = 1e-9;

    internal sealed record Case(string Name, JsonElement Input, JsonNode? Expected);

    // Mapper folders: the expected file is the mapped result in Describe form.
    public static bool Run(string folder, Func<JsonElement, double, ProviderUsage> map) =>
        Run(folder, input => Describe(map(input, CreditsFullAmount(input))));

    // Every case in the folder must match, and an empty folder fails: a
    // resource that silently stopped being embedded must not read as a pass.
    // Each folder has its own input shape; `evaluate` turns one input into
    // the JSON its expected file holds.
    public static bool Run(string folder, Func<JsonElement, JsonNode?> evaluate)
    {
        var cases = Load(folder);
        if (cases.Count == 0)
        {
            Console.Error.WriteLine($"fixtures/{folder}: no cases embedded");
            return false;
        }

        foreach (var fixture in cases)
        {
            if (fixture.Expected is null)
            {
                Console.Error.WriteLine($"fixtures/{folder}/{fixture.Name}: no {ExpectedSuffix} embedded");
                return false;
            }

            string? mismatch;
            try
            {
                mismatch = FindMismatch(fixture.Expected, evaluate(fixture.Input), "$");
            }
            catch (Exception ex)
            {
                mismatch = $"threw {ex.GetType().Name}: {ex.Message}";
            }

            if (mismatch is not null)
            {
                Console.Error.WriteLine($"fixtures/{folder}/{fixture.Name}: {mismatch}");
                return false;
            }
        }
        return true;
    }

    public static IReadOnlyList<Case> Load(string folder)
    {
        var assembly = typeof(UsageFixtures).Assembly;
        var prefix = $"{ResourcePrefix}{folder}/";
        var cases = new List<Case>();
        foreach (var inputName in assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(InputSuffix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {
            var name = inputName[prefix.Length..^InputSuffix.Length];
            using var input = JsonDocument.Parse(ReadResource(assembly, inputName)
                ?? throw new InvalidOperationException($"Resource {inputName} is listed but cannot be read."));
            var expected = ReadResource(assembly, $"{prefix}{name}{ExpectedSuffix}");
            cases.Add(new Case(name, input.RootElement.Clone(), expected is null ? null : JsonNode.Parse(expected)));
        }
        return cases;
    }

    // Mapper inputs that come from settings live under "options".
    public static double CreditsFullAmount(JsonElement input)
    {
        return input.TryGetProperty("options", out var options)
            && options.ValueKind == JsonValueKind.Object
            && options.TryGetProperty("creditsFullAmount", out var amount)
            && amount.ValueKind == JsonValueKind.Number
            ? amount.GetDouble()
            : CreditsGauge.DefaultFullAmount;
    }

    // The fixture form of a mapped result: every field of every row, so a
    // label that drifts by one character fails here instead of detaching the
    // settings stored under it. UpdatedAt is wall-clock time and left out.
    public static JsonObject Describe(ProviderUsage usage)
    {
        return new JsonObject
        {
            ["providerId"] = usage.ProviderId,
            ["displayName"] = usage.DisplayName,
            ["state"] = usage.State.ToString(),
            ["statusText"] = usage.StatusText,
            ["overallUsedPercent"] = usage.OverallUsedPercent,
            ["rows"] = new JsonArray(usage.Rows.Select(row => (JsonNode)new JsonObject
            {
                ["label"] = row.Label,
                ["scope"] = row.Scope,
                ["isPrimary"] = row.IsPrimary,
                ["usedPercent"] = row.Window?.UsedPercent,
                ["resetsAt"] = FormatTimestamp(row.Window?.ResetsAt),
                ["windowDurationMins"] = row.Window?.WindowDurationMins,
                ["detailText"] = row.DetailText,
                ["beyondFull"] = row.BeyondFull,
                ["creditsLeft"] = row.CreditsLeft
            }).ToArray())
        };
    }

    // The reverse of Describe, for inputs that start from mapped usage (alerts).
    // Only label is required; a row without usedPercent has no window.
    public static ProviderUsage ReadUsage(JsonElement usage)
    {
        var rows = usage.GetProperty("rows").EnumerateArray().Select(row => new UsageRow(
            row.GetProperty("label").GetString()!,
            Number(row, "usedPercent") is { } used
                ? new RateLimitWindow(used, ReadTimestamp(Optional(row, "resetsAt")), (long?)Number(row, "windowDurationMins"))
                : null,
            Optional(row, "isPrimary")?.GetBoolean() ?? false,
            Optional(row, "detailText")?.GetString(),
            Optional(row, "scope")?.GetString(),
            Optional(row, "beyondFull")?.GetBoolean() ?? false,
            Number(row, "creditsLeft"))).ToArray();
        return new ProviderUsage(
            usage.GetProperty("providerId").GetString()!,
            usage.GetProperty("displayName").GetString()!,
            rows,
            Number(usage, "overallUsedPercent") ?? 0d,
            Optional(usage, "statusText")?.GetString() ?? "",
            DateTimeOffset.Now,
            Optional(usage, "state") is { } state ? Enum.Parse<ProviderState>(state.GetString()!) : ProviderState.Ok);
    }

    // A settings.json fragment, loaded the way AppSettings.Load loads the file.
    public static AppSettings ReadSettings(JsonElement? settings) =>
        settings is { } value ? AppSettings.FromJson(value.GetRawText()) : new AppSettings();

    // The "now" of a time-dependent case, or any other timestamp; null stays null.
    public static DateTimeOffset? ReadTimestamp(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } text
            ? DateTimeOffset.Parse(text.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

    // A property that is absent or null reads as null.
    public static JsonElement? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static double? Number(JsonElement element, string name) => Optional(element, name)?.GetDouble();

    // ISO 8601 UTC, trailing zero fractions dropped: 2030-01-01T12:00:00Z.
    private static string? FormatTimestamp(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    // Strings and booleans compare exactly, numbers within Tolerance, and both
    // sides must have the same keys, so a field added on one side is caught.
    private static string? FindMismatch(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : $"{path}: expected {Show(expected)}, got {Show(actual)}";
        }

        switch (expected)
        {
            case JsonObject expectedObject when actual is JsonObject actualObject:
                foreach (var key in expectedObject.Select(pair => pair.Key).Union(actualObject.Select(pair => pair.Key)))
                {
                    if (!expectedObject.ContainsKey(key)) return $"{path}.{key}: unexpected field";
                    if (!actualObject.ContainsKey(key)) return $"{path}.{key}: missing field";
                    var mismatch = FindMismatch(expectedObject[key], actualObject[key], $"{path}.{key}");
                    if (mismatch is not null) return mismatch;
                }
                return null;
            case JsonArray expectedArray when actual is JsonArray actualArray:
                if (expectedArray.Count != actualArray.Count)
                {
                    return $"{path}: expected {expectedArray.Count} items, got {actualArray.Count}";
                }
                for (var i = 0; i < expectedArray.Count; i++)
                {
                    var mismatch = FindMismatch(expectedArray[i], actualArray[i], $"{path}[{i}]");
                    if (mismatch is not null) return mismatch;
                }
                return null;
            case JsonValue when actual is JsonValue:
                var kind = expected.GetValueKind();
                if (kind != actual.GetValueKind()) return $"{path}: expected {Show(expected)}, got {Show(actual)}";
                if (kind == JsonValueKind.Number)
                {
                    return Math.Abs(ToDouble(expected) - ToDouble(actual)) <= Tolerance
                        ? null
                        : $"{path}: expected {Show(expected)}, got {Show(actual)}";
                }
                return expected.ToJsonString() == actual.ToJsonString() ? null : $"{path}: expected {Show(expected)}, got {Show(actual)}";
            default:
                return $"{path}: expected {Show(expected)}, got {Show(actual)}";
        }
    }

    // Through the JSON text: a built value holds its CLR type (long for
    // windowDurationMins) and GetValue<double> refuses to convert it.
    private static double ToDouble(JsonNode node) =>
        double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    // Unescaped, so a mismatch reads "Ctrl+PgDn" rather than "Ctrl+PgDn".
    private static readonly JsonSerializerOptions ShowOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Show(JsonNode? node) => node?.ToJsonString(ShowOptions) ?? "null";

    private static string? ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
