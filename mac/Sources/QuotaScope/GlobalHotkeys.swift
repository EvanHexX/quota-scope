#if os(macOS)
import AppKit
import Carbon.HIToolbox
import QuotaCore

// Port of app-winui's HotkeyWindow (RegisterHotKey) with Carbon's
// RegisterEventHotKey, which needs no Accessibility permission. Settings keep
// the Windows grammar (HotkeyDefinition: MOD_* flags and virtual-key codes);
// MacKeys maps them to the keys at the same place on a Mac keyboard, with the
// Windows key as ⌘ (decision M13).

enum HotkeyAction: UInt32, CaseIterable {
    case togglePopup = 1
    case refreshAll = 2
    case togglePin = 3

    var settingsPath: WritableKeyPath<AppSettings, String> {
        switch self {
        case .togglePopup: return \.hotkey
        case .refreshAll: return \.hotkeyRefreshAll
        case .togglePin: return \.hotkeyTogglePin
        }
    }
}

enum HotkeyRegistrationError: Error {
    case unsupportedKey
    case inUse
    case failed(OSStatus)
}

@MainActor
final class GlobalHotkeys {
    var onPressed: (HotkeyAction) -> Void = { _ in }

    nonisolated private static let signature: OSType = 0x5153_484B // "QSHK"
    private var registered: [HotkeyAction: (definition: HotkeyDefinition, ref: EventHotKeyRef?)] = [:]
    private var handler: EventHandlerRef?
    // While the settings window records a shortcut, every binding is let go
    // so pressing one is recorded instead of run.
    private var suspended = false

    init() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let status = InstallEventHandler(GetApplicationEventTarget(), { _, event, userData in
            guard let event, let userData else { return OSStatus(eventNotHandledErr) }
            var id = EventHotKeyID()
            let read = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                         nil, MemoryLayout<EventHotKeyID>.size, nil, &id)
            guard read == noErr, id.signature == GlobalHotkeys.signature,
                  let action = HotkeyAction(rawValue: id.id) else { return OSStatus(eventNotHandledErr) }
            let hotkeys = Unmanaged<GlobalHotkeys>.fromOpaque(userData).takeUnretainedValue()
            // The application event target is dispatched on the main thread.
            MainActor.assumeIsolated { hotkeys.onPressed(action) }
            return noErr
        }, 1, &spec, Unmanaged.passUnretained(self).toOpaque(), &handler)
        if status != noErr { NSLog("QuotaScope: hotkey handler not installed (\(status))") }
    }

    // Replaces the action's binding. On failure the action is left unbound;
    // the caller restores the previous one.
    func register(_ action: HotkeyAction, _ definition: HotkeyDefinition) -> HotkeyRegistrationError? {
        unregister(action)
        guard MacKeys.keyCode(definition.virtualKey) != nil else { return .unsupportedKey }
        if suspended {
            registered[action] = (definition, nil)
            return nil
        }
        switch install(action, definition) {
        case .success(let ref):
            registered[action] = (definition, ref)
            return nil
        case .failure(let error):
            return error
        }
    }

    func unregister(_ action: HotkeyAction) {
        if let ref = registered.removeValue(forKey: action)?.ref { UnregisterEventHotKey(ref) }
    }

    func suspend(_ on: Bool) {
        guard on != suspended else { return }
        suspended = on
        for (action, entry) in registered {
            if on {
                if let ref = entry.ref { UnregisterEventHotKey(ref) }
                registered[action] = (entry.definition, nil)
            } else if case .success(let ref) = install(action, entry.definition) {
                registered[action] = (entry.definition, ref)
            }
        }
    }

    private func install(_ action: HotkeyAction, _ definition: HotkeyDefinition) -> Result<EventHotKeyRef, HotkeyRegistrationError> {
        guard let code = MacKeys.keyCode(definition.virtualKey) else { return .failure(.unsupportedKey) }
        var ref: EventHotKeyRef?
        let status = RegisterEventHotKey(code, MacKeys.carbonModifiers(definition.modifiers),
                                         EventHotKeyID(signature: Self.signature, id: action.rawValue),
                                         GetApplicationEventTarget(), 0, &ref)
        if status == OSStatus(eventHotKeyExistsErr) { return .failure(.inUse) }
        guard status == noErr, let ref else { return .failure(.failed(status)) }
        return .success(ref)
    }
}

// Windows virtual-key codes and MOD_* flags <-> Carbon key codes and
// modifiers. Letters and digits are ANSI key positions, so they follow the
// physical key, as virtual-key codes do on a US layout.
enum MacKeys {
    static let escapeKeyCode = UInt16(kVK_Escape)

    private static let pairs: [(virtualKey: UInt32, keyCode: Int)] = {
        let letters = [kVK_ANSI_A, kVK_ANSI_B, kVK_ANSI_C, kVK_ANSI_D, kVK_ANSI_E, kVK_ANSI_F, kVK_ANSI_G, kVK_ANSI_H,
                       kVK_ANSI_I, kVK_ANSI_J, kVK_ANSI_K, kVK_ANSI_L, kVK_ANSI_M, kVK_ANSI_N, kVK_ANSI_O, kVK_ANSI_P,
                       kVK_ANSI_Q, kVK_ANSI_R, kVK_ANSI_S, kVK_ANSI_T, kVK_ANSI_U, kVK_ANSI_V, kVK_ANSI_W, kVK_ANSI_X,
                       kVK_ANSI_Y, kVK_ANSI_Z]
        let digits = [kVK_ANSI_0, kVK_ANSI_1, kVK_ANSI_2, kVK_ANSI_3, kVK_ANSI_4, kVK_ANSI_5, kVK_ANSI_6, kVK_ANSI_7,
                      kVK_ANSI_8, kVK_ANSI_9]
        // Carbon names F1-F20; F21-F24 have no Mac key.
        let functions = [kVK_F1, kVK_F2, kVK_F3, kVK_F4, kVK_F5, kVK_F6, kVK_F7, kVK_F8, kVK_F9, kVK_F10,
                         kVK_F11, kVK_F12, kVK_F13, kVK_F14, kVK_F15, kVK_F16, kVK_F17, kVK_F18, kVK_F19, kVK_F20]
        var pairs: [(virtualKey: UInt32, keyCode: Int)] = []
        pairs += letters.enumerated().map { (0x41 + UInt32($0.offset), $0.element) }
        pairs += digits.enumerated().map { (0x30 + UInt32($0.offset), $0.element) }
        pairs += functions.enumerated().map { (0x70 + UInt32($0.offset), $0.element) }
        pairs += [
            (0x20, kVK_Space), (0x09, kVK_Tab), (0x0D, kVK_Return),
            (0x21, kVK_PageUp), (0x22, kVK_PageDown), (0x23, kVK_End), (0x24, kVK_Home),
            (0x25, kVK_LeftArrow), (0x26, kVK_UpArrow), (0x27, kVK_RightArrow), (0x28, kVK_DownArrow),
            // Insert sits where Help is on a full Mac keyboard; Delete is ⌦.
            (0x2D, kVK_Help), (0x2E, kVK_ForwardDelete),
        ]
        return pairs
    }()

    static func keyCode(_ virtualKey: UInt32) -> UInt32? {
        pairs.first { $0.virtualKey == virtualKey }.map { UInt32($0.keyCode) }
    }

    static func virtualKey(_ keyCode: UInt16) -> UInt32? {
        pairs.first { $0.keyCode == Int(keyCode) }?.virtualKey
    }

    static func carbonModifiers(_ modifiers: UInt32) -> UInt32 {
        var carbon = 0
        if modifiers & HotkeyDefinition.modControl != 0 { carbon |= controlKey }
        if modifiers & HotkeyDefinition.modAlt != 0 { carbon |= optionKey }
        if modifiers & HotkeyDefinition.modShift != 0 { carbon |= shiftKey }
        if modifiers & HotkeyDefinition.modWin != 0 { carbon |= cmdKey }
        return UInt32(carbon)
    }

    static func modifiers(_ flags: NSEvent.ModifierFlags) -> UInt32 {
        var modifiers: UInt32 = 0
        if flags.contains(.control) { modifiers |= HotkeyDefinition.modControl }
        if flags.contains(.option) { modifiers |= HotkeyDefinition.modAlt }
        if flags.contains(.shift) { modifiers |= HotkeyDefinition.modShift }
        if flags.contains(.command) { modifiers |= HotkeyDefinition.modWin }
        return modifiers
    }

    // "Ctrl+Alt+U" as a Mac menu shows it: ⌃⌥U. Text that does not parse is
    // shown as stored.
    static func display(_ text: String) -> String {
        guard let definition = HotkeyDefinition.parse(text) else { return text }
        var shown = ""
        if definition.modifiers & HotkeyDefinition.modControl != 0 { shown += "⌃" }
        if definition.modifiers & HotkeyDefinition.modAlt != 0 { shown += "⌥" }
        if definition.modifiers & HotkeyDefinition.modShift != 0 { shown += "⇧" }
        if definition.modifiers & HotkeyDefinition.modWin != 0 { shown += "⌘" }
        let key = definition.format().split(separator: "+").last.map(String.init) ?? ""
        let symbols = ["Space": "Space", "Tab": "⇥", "Enter": "↩", "PageUp": "⇞", "PageDown": "⇟", "End": "↘",
                       "Home": "↖", "Left": "←", "Up": "↑", "Right": "→", "Down": "↓", "Insert": "Help", "Delete": "⌦"]
        return shown + (symbols[key] ?? key)
    }
}
#endif
