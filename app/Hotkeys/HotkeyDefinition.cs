using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuotaScope.Providers;

namespace QuotaScope.Hotkeys;

// UI-framework-free hotkey string parser/formatter shared by both apps.
// "Ctrl+Alt+U" <-> (MOD_CONTROL|MOD_ALT, 0x55). Case- and order-insensitive on
// parse; Format always emits the canonical Ctrl+Alt+Shift+Win+Key order.
internal sealed record HotkeyDefinition(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public static bool TryParse(string? text, out HotkeyDefinition definition)
    {
        definition = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint modifiers = 0;
        uint virtualKey = 0;
        var hasKey = false;

        foreach (var raw in text.Split('+'))
        {
            var token = raw.Trim().ToUpperInvariant();
            if (token.Length == 0) return false;
            switch (token)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    if (hasKey || !TryParseKey(token, out virtualKey)) return false;
                    hasKey = true;
                    break;
            }
        }

        // A global hotkey without any modifier would shadow normal typing.
        if (!hasKey || modifiers == 0) return false;
        definition = new HotkeyDefinition(modifiers, virtualKey);
        return true;
    }

    public string Format()
    {
        var builder = new StringBuilder();
        if ((Modifiers & ModControl) != 0) builder.Append("Ctrl+");
        if ((Modifiers & ModAlt) != 0) builder.Append("Alt+");
        if ((Modifiers & ModShift) != 0) builder.Append("Shift+");
        if ((Modifiers & ModWin) != 0) builder.Append("Win+");
        builder.Append(KeyName(VirtualKey));
        return builder.ToString();
    }

    public static bool IsSupportedKey(uint virtualKey) => KeyName(virtualKey).Length > 0;

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;
        if (token.Length == 1)
        {
            var c = token[0];
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }
            return false;
        }

        if (token.Length is 2 or 3 && token[0] == 'F' && int.TryParse(token[1..], out var f) && f is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + f - 1);
            return true;
        }

        if (NamedKeys.TryGetValue(token, out var vk))
        {
            virtualKey = vk;
            return true;
        }
        return false;
    }

    private static string KeyName(uint virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)virtualKey).ToString();
        }
        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x70 + 1}";
        }
        foreach (var pair in NamedKeys)
        {
            if (pair.Value == virtualKey) return Canonical(pair.Key);
        }
        return "";
    }

    private static string Canonical(string upper) => upper switch
    {
        "PAGEUP" => "PageUp",
        "PAGEDOWN" => "PageDown",
        _ => upper[0] + upper[1..].ToLowerInvariant()
    };

    private static readonly Dictionary<string, uint> NamedKeys = new()
    {
        ["SPACE"] = 0x20,
        ["TAB"] = 0x09,
        ["ENTER"] = 0x0D,
        ["RETURN"] = 0x0D,
        ["PAGEUP"] = 0x21,
        ["PGUP"] = 0x21,
        ["PAGEDOWN"] = 0x22,
        ["PGDN"] = 0x22,
        ["END"] = 0x23,
        ["HOME"] = 0x24,
        ["LEFT"] = 0x25,
        ["UP"] = 0x26,
        ["RIGHT"] = 0x27,
        ["DOWN"] = 0x28,
        ["INSERT"] = 0x2D,
        ["DELETE"] = 0x2E,
        ["DEL"] = 0x2E
    };

    // Samples live in fixtures/hotkeys (docs/MACOS_BUILD.md, section 5).
    public static bool RunSelfTest() => UsageFixtures.Run("hotkeys", EvaluateFixture);

    // Each text (null allowed) is answered by { "ok": false }, or by the parsed
    // binding, its canonical text, and whether that text parses back to it.
    private static JsonNode EvaluateFixture(JsonElement input) => new JsonObject
    {
        ["results"] = new JsonArray(input.GetProperty("texts").EnumerateArray()
            .Select(text => (JsonNode)Describe(text.ValueKind == JsonValueKind.Null ? null : text.GetString()))
            .ToArray())
    };

    private static JsonObject Describe(string? text)
    {
        if (!TryParse(text, out var definition)) return new JsonObject { ["ok"] = false };
        var formatted = definition.Format();
        return new JsonObject
        {
            ["ok"] = true,
            ["modifiers"] = definition.Modifiers,
            ["virtualKey"] = definition.VirtualKey,
            ["formatted"] = formatted,
            ["reparses"] = TryParse(formatted, out var reparsed) && reparsed == definition
        };
    }
}
