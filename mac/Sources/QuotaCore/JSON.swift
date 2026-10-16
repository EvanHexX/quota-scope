import Foundation

// A small JSON value with two properties Foundation's JSONSerialization does
// not guarantee on every platform: object members keep their order (the C#
// side reads payloads with System.Text.Json, which also does), and numbers
// keep their source text, so "300" and "300.0" stay distinguishable the way
// JsonElement.TryGetInt64 distinguishes them.
public enum JSONValue: Equatable {
    case null
    case bool(Bool)
    case number(String)
    case string(String)
    case array([JSONValue])
    case object(JSONObject)

    public static func number(_ value: Double) -> JSONValue { .number(JSONValue.format(value)) }
    public static func number(_ value: Int64) -> JSONValue { .number(String(value)) }

    public var isNull: Bool { if case .null = self { return true } else { return false } }

    public var objectValue: JSONObject? { if case .object(let object) = self { return object } else { return nil } }
    public var arrayValue: [JSONValue]? { if case .array(let items) = self { return items } else { return nil } }
    public var stringValue: String? { if case .string(let text) = self { return text } else { return nil } }
    public var boolValue: Bool? { if case .bool(let flag) = self { return flag } else { return nil } }

    // TryGetDouble: any number.
    public var doubleValue: Double? { if case .number(let text) = self { return Double(text) } else { return nil } }

    // TryGetInt64: only an integer literal that fits; "300.0" and "3e2" are not.
    public var int64Value: Int64? { if case .number(let text) = self { return Int64(text) } else { return nil } }

    public subscript(key: String) -> JSONValue? { objectValue?[key] }

    // Shortest text that reads back as the same double, like System.Text.Json.
    static func format(_ value: Double) -> String {
        if value == value.rounded(), abs(value) < 1e15 { return String(Int64(value)) }
        return "\(value)"
    }

    public func serialized() -> String {
        switch self {
        case .null: return "null"
        case .bool(let flag): return flag ? "true" : "false"
        case .number(let text): return text
        case .string(let text): return JSONValue.quote(text)
        case .array(let items): return "[" + items.map { $0.serialized() }.joined(separator: ",") + "]"
        case .object(let object):
            return "{" + object.members.map { JSONValue.quote($0.key) + ":" + $0.value.serialized() }.joined(separator: ",") + "}"
        }
    }

    static func quote(_ text: String) -> String {
        var out = "\""
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            case _ where scalar.value < 0x20: out += String(format: "\\u%04x", scalar.value)
            default: out.unicodeScalars.append(scalar)
            }
        }
        return out + "\""
    }

    public static func parse(_ text: String) throws -> JSONValue {
        var parser = JSONParser(Array(text.utf8))
        let value = try parser.parseValue()
        parser.skipWhitespace()
        guard parser.atEnd else { throw JSONParser.ParseError.trailing(parser.position) }
        return value
    }
}

// Ordered members. Setting an existing key keeps its position and a new key
// goes last, matching System.Text.Json.Nodes.JsonObject.
public struct JSONObject: Equatable {
    public private(set) var members: [(key: String, value: JSONValue)] = []

    public init() {}

    public init(_ members: [(String, JSONValue)]) {
        for (key, value) in members { self[key] = value }
    }

    public var keys: [String] { members.map { $0.key } }

    public subscript(key: String) -> JSONValue? {
        get { members.first { $0.key == key }?.value }
        set {
            if let index = members.firstIndex(where: { $0.key == key }) {
                if let newValue { members[index].value = newValue } else { members.remove(at: index) }
            } else if let newValue {
                members.append((key, newValue))
            }
        }
    }

    public static func == (lhs: JSONObject, rhs: JSONObject) -> Bool {
        lhs.members.count == rhs.members.count
            && zip(lhs.members, rhs.members).allSatisfy { $0.key == $1.key && $0.value == $1.value }
    }
}

struct JSONParser {
    enum ParseError: Error, CustomStringConvertible {
        case unexpected(Int)
        case trailing(Int)
        var description: String {
            switch self {
            case .unexpected(let at): return "invalid JSON at byte \(at)"
            case .trailing(let at): return "trailing data at byte \(at)"
            }
        }
    }

    private let bytes: [UInt8]
    private(set) var position = 0

    init(_ bytes: [UInt8]) {
        // A UTF-8 byte order mark, which Windows editors like to add.
        self.bytes = bytes.starts(with: [0xEF, 0xBB, 0xBF]) ? Array(bytes.dropFirst(3)) : bytes
    }

    var atEnd: Bool { position >= bytes.count }

    mutating func skipWhitespace() {
        while position < bytes.count, [0x20, 0x09, 0x0A, 0x0D].contains(bytes[position]) { position += 1 }
    }

    mutating func parseValue() throws -> JSONValue {
        skipWhitespace()
        guard position < bytes.count else { throw ParseError.unexpected(position) }
        switch bytes[position] {
        case UInt8(ascii: "{"): return try parseObject()
        case UInt8(ascii: "["): return try parseArray()
        case UInt8(ascii: "\""): return .string(try parseString())
        case UInt8(ascii: "t"): try expect("true"); return .bool(true)
        case UInt8(ascii: "f"): try expect("false"); return .bool(false)
        case UInt8(ascii: "n"): try expect("null"); return .null
        default: return try parseNumber()
        }
    }

    private mutating func expect(_ word: String) throws {
        let literal = Array(word.utf8)
        guard bytes.count - position >= literal.count, Array(bytes[position..<position + literal.count]) == literal else {
            throw ParseError.unexpected(position)
        }
        position += literal.count
    }

    private mutating func parseObject() throws -> JSONValue {
        position += 1
        var object = JSONObject()
        skipWhitespace()
        if position < bytes.count, bytes[position] == UInt8(ascii: "}") { position += 1; return .object(object) }
        while true {
            skipWhitespace()
            guard position < bytes.count, bytes[position] == UInt8(ascii: "\"") else { throw ParseError.unexpected(position) }
            let key = try parseString()
            skipWhitespace()
            guard position < bytes.count, bytes[position] == UInt8(ascii: ":") else { throw ParseError.unexpected(position) }
            position += 1
            object[key] = try parseValue()
            skipWhitespace()
            guard position < bytes.count else { throw ParseError.unexpected(position) }
            if bytes[position] == UInt8(ascii: ",") { position += 1; continue }
            if bytes[position] == UInt8(ascii: "}") { position += 1; return .object(object) }
            throw ParseError.unexpected(position)
        }
    }

    private mutating func parseArray() throws -> JSONValue {
        position += 1
        var items: [JSONValue] = []
        skipWhitespace()
        if position < bytes.count, bytes[position] == UInt8(ascii: "]") { position += 1; return .array(items) }
        while true {
            items.append(try parseValue())
            skipWhitespace()
            guard position < bytes.count else { throw ParseError.unexpected(position) }
            if bytes[position] == UInt8(ascii: ",") { position += 1; continue }
            if bytes[position] == UInt8(ascii: "]") { position += 1; return .array(items) }
            throw ParseError.unexpected(position)
        }
    }

    private mutating func parseString() throws -> String {
        position += 1
        var scalars = String.UnicodeScalarView()
        var raw: [UInt8] = []
        func flush() {
            if !raw.isEmpty { scalars.append(contentsOf: String(decoding: raw, as: UTF8.self).unicodeScalars); raw.removeAll() }
        }
        while position < bytes.count {
            let byte = bytes[position]
            position += 1
            if byte == UInt8(ascii: "\"") { flush(); return String(scalars) }
            if byte != UInt8(ascii: "\\") { raw.append(byte); continue }
            flush()
            guard position < bytes.count else { break }
            let escape = bytes[position]
            position += 1
            switch escape {
            case UInt8(ascii: "\""): scalars.append("\"")
            case UInt8(ascii: "\\"): scalars.append("\\")
            case UInt8(ascii: "/"): scalars.append("/")
            case UInt8(ascii: "b"): scalars.append("\u{08}")
            case UInt8(ascii: "f"): scalars.append("\u{0C}")
            case UInt8(ascii: "n"): scalars.append("\n")
            case UInt8(ascii: "r"): scalars.append("\r")
            case UInt8(ascii: "t"): scalars.append("\t")
            case UInt8(ascii: "u"):
                var code = try parseHex4()
                if (0xD800...0xDBFF).contains(code), bytes.count - position >= 6,
                   bytes[position] == UInt8(ascii: "\\"), bytes[position + 1] == UInt8(ascii: "u") {
                    position += 2
                    let low = try parseHex4()
                    code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00)
                }
                scalars.append(Unicode.Scalar(code) ?? "\u{FFFD}")
            default: throw ParseError.unexpected(position - 1)
            }
        }
        throw ParseError.unexpected(position)
    }

    private mutating func parseHex4() throws -> UInt32 {
        guard bytes.count - position >= 4, let code = UInt32(String(decoding: bytes[position..<position + 4], as: UTF8.self), radix: 16) else {
            throw ParseError.unexpected(position)
        }
        position += 4
        return code
    }

    private mutating func parseNumber() throws -> JSONValue {
        let start = position
        let allowed = Set("+-0123456789.eE".utf8)
        while position < bytes.count, allowed.contains(bytes[position]) { position += 1 }
        let text = String(decoding: bytes[start..<position], as: UTF8.self)
        guard !text.isEmpty, Double(text) != nil else { throw ParseError.unexpected(start) }
        return .number(text)
    }
}
