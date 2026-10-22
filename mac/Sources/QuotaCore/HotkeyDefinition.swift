import Foundation

// Port of app/Hotkeys/HotkeyDefinition.cs. Checked against fixtures/hotkeys.

// "Ctrl+Alt+U" <-> (modControl|modAlt, 0x55). Case- and order-insensitive on
// parse; format always emits the canonical Ctrl+Alt+Shift+Win+Key order.
// The model keeps Windows modifier flags and virtual-key codes so a settings
// string means the same binding on both platforms; mapping them to Carbon key
// codes belongs to the macOS app.
public struct HotkeyDefinition: Equatable {
    public static let modAlt: UInt32 = 0x0001
    public static let modControl: UInt32 = 0x0002
    public static let modShift: UInt32 = 0x0004
    public static let modWin: UInt32 = 0x0008

    public var modifiers: UInt32
    public var virtualKey: UInt32

    public init(modifiers: UInt32, virtualKey: UInt32) {
        self.modifiers = modifiers
        self.virtualKey = virtualKey
    }

    public static func parse(_ text: String?) -> HotkeyDefinition? {
        guard let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }

        var modifiers: UInt32 = 0
        var virtualKey: UInt32?

        for raw in text.split(separator: "+", omittingEmptySubsequences: false) {
            let token = raw.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
            if token.isEmpty { return nil }
            switch token {
            case "CTRL", "CONTROL": modifiers |= modControl
            case "ALT": modifiers |= modAlt
            case "SHIFT": modifiers |= modShift
            case "WIN", "WINDOWS": modifiers |= modWin
            default:
                guard virtualKey == nil, let key = parseKey(token) else { return nil }
                virtualKey = key
            }
        }

        // A global hotkey without any modifier would shadow normal typing.
        guard let virtualKey, modifiers != 0 else { return nil }
        return HotkeyDefinition(modifiers: modifiers, virtualKey: virtualKey)
    }

    public func format() -> String {
        var text = ""
        if modifiers & Self.modControl != 0 { text += "Ctrl+" }
        if modifiers & Self.modAlt != 0 { text += "Alt+" }
        if modifiers & Self.modShift != 0 { text += "Shift+" }
        if modifiers & Self.modWin != 0 { text += "Win+" }
        return text + Self.keyName(virtualKey)
    }

    public static func isSupportedKey(_ virtualKey: UInt32) -> Bool { !keyName(virtualKey).isEmpty }

    private static func parseKey(_ token: String) -> UInt32? {
        let units = Array(token.utf16)
        if units.count == 1 {
            let c = UInt32(units[0])
            return isLetterOrDigit(c) ? c : nil
        }

        // Like int.TryParse: surrounding ASCII whitespace and a sign are allowed.
        if units.count == 2 || units.count == 3, units[0] == UInt16(UInt8(ascii: "F")),
           let f = Int(String(token.dropFirst()).trimmingCharacters(in: integerWhitespace)), (1...24).contains(f) {
            return UInt32(0x70 + f - 1)
        }

        return namedKeys.first { $0.name == token }?.virtualKey
    }

    private static func keyName(_ virtualKey: UInt32) -> String {
        if isLetterOrDigit(virtualKey) { return String(UnicodeScalar(UInt8(virtualKey))) }
        if (0x70...0x87).contains(virtualKey) { return "F\(virtualKey - 0x70 + 1)" }
        // The first name for a code is its canonical one (Enter, not Return).
        guard let name = namedKeys.first(where: { $0.virtualKey == virtualKey })?.name else { return "" }
        return canonical(name)
    }

    private static func canonical(_ upper: String) -> String {
        switch upper {
        case "PAGEUP": return "PageUp"
        case "PAGEDOWN": return "PageDown"
        default: return String(upper.prefix(1)) + upper.dropFirst().lowercased()
        }
    }

    private static func isLetterOrDigit(_ c: UInt32) -> Bool {
        (0x41...0x5A).contains(c) || (0x30...0x39).contains(c)
    }

    private static let integerWhitespace = CharacterSet(charactersIn: "\t\n\u{0B}\u{0C}\r ")

    // In the C# dictionary's order, which decides the canonical name.
    private static let namedKeys: [(name: String, virtualKey: UInt32)] = [
        ("SPACE", 0x20),
        ("TAB", 0x09),
        ("ENTER", 0x0D),
        ("RETURN", 0x0D),
        ("PAGEUP", 0x21),
        ("PGUP", 0x21),
        ("PAGEDOWN", 0x22),
        ("PGDN", 0x22),
        ("END", 0x23),
        ("HOME", 0x24),
        ("LEFT", 0x25),
        ("UP", 0x26),
        ("RIGHT", 0x27),
        ("DOWN", 0x28),
        ("INSERT", 0x2D),
        ("DELETE", 0x2E),
        ("DEL", 0x2E),
    ]
}
