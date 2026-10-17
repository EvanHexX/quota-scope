import Foundation

// Runs the shared fixtures in ../fixtures against this port, the Swift side of
// app/Providers/UsageFixtures.cs (docs/MACOS_BUILD.md, section 5). Each folder
// turns one input into the JSON its expected file holds, exactly as the C#
// self-test does; the comparison rules are the same (numbers within 1e-9,
// strings and booleans exact, both sides with the same keys).
public enum Fixtures {
    public static let tolerance = 1e-9

    // Every shared fixture folder. The renewer's timing rules live in
    // ClaudeRenewalPolicy; the process-running part is in QuotaProviders.
    public static let folders = ["codex", "claude", "rows", "alerts", "hotkeys", "renewer", "layout", "resettext"]

    public struct Failure: Error, CustomStringConvertible {
        public let description: String
    }

    // Every case in the folder must match, and an empty folder fails.
    // Returns the number of cases checked.
    @discardableResult
    public static func run(folder: String, in root: URL) throws -> Int {
        let directory = root.appendingPathComponent(folder)
        let names = try FileManager.default.contentsOfDirectory(atPath: directory.path)
            .filter { $0.hasSuffix(".input.json") }
            .sorted()
        guard !names.isEmpty else { throw Failure(description: "fixtures/\(folder): no cases") }

        for name in names {
            let caseName = String(name.dropLast(".input.json".count))
            let expectedURL = directory.appendingPathComponent("\(caseName).expected.json")
            guard FileManager.default.fileExists(atPath: expectedURL.path) else {
                throw Failure(description: "fixtures/\(folder)/\(caseName): no .expected.json")
            }
            let input = try JSONValue.parse(try String(contentsOf: directory.appendingPathComponent(name), encoding: .utf8))
            let expected = try JSONValue.parse(try String(contentsOf: expectedURL, encoding: .utf8))
            let actual: JSONValue
            do {
                actual = try evaluate(folder: folder, input)
            } catch {
                throw Failure(description: "fixtures/\(folder)/\(caseName): threw \(error)")
            }
            if let mismatch = findMismatch(expected, actual, "$") {
                throw Failure(description: "fixtures/\(folder)/\(caseName): \(mismatch)")
            }
        }
        return names.count
    }

    public static func evaluate(folder: String, _ input: JSONValue) throws -> JSONValue {
        switch folder {
        case "codex": return describe(try mapCodex(input))
        case "claude": return describe(mapClaude(input))
        case "rows": return try evaluateRows(input)
        case "alerts": return try evaluateAlerts(input)
        case "hotkeys": return try evaluateHotkeys(input)
        case "renewer": return try evaluateRenewer(input)
        case "layout": return try evaluateLayout(input)
        case "resettext": return try evaluateResetText(input)
        default: throw Failure(description: "no evaluator for fixtures/\(folder)")
        }
    }

    // Each list in the input is answered by a list in the same order: expiry
    // times (against "now") by whether they count as expiring, failure counts
    // by their cooldown, attempts by the delay before the next one. Delays are
    // minutes; null means the renewer has given up.
    static func evaluateRenewer(_ input: JSONValue) throws -> JSONValue {
        func minutes(_ delay: TimeInterval?) -> JSONValue { delay.map { .number($0 / 60) } ?? .null }
        func integer(_ value: JSONValue?) throws -> Int {
            guard let number = value?.int64Value else { throw Failure(description: "renewer input needs whole numbers") }
            return Int(number)
        }
        var result = JSONObject()
        if let expiries = optional(input, "expiresAt")?.arrayValue {
            guard let now = optional(input, "now")?.stringValue.flatMap(Timestamp.parseISO8601) else {
                throw Failure(description: "expiresAt needs a \"now\"")
            }
            result["expiring"] = .array(expiries.map { expiresAt in
                .bool(ClaudeRenewalPolicy.isExpiring(expiresAt.stringValue.flatMap(Timestamp.parseISO8601)?.date, now: now.date))
            })
        }
        if let failures = optional(input, "failures")?.arrayValue {
            result["cooldownMinutes"] = .array(try failures.map { minutes(ClaudeRenewalPolicy.cooldownAfter(try integer($0))) })
        }
        if let attempts = optional(input, "attempts")?.arrayValue {
            result["delayMinutes"] = .array(try attempts.map { attempt in
                guard let renewed = attempt["renewed"]?.boolValue, let recovery = attempt["recovery"]?.boolValue else {
                    throw Failure(description: "attempt needs renewed and recovery")
                }
                return minutes(ClaudeRenewalPolicy.nextDelayAfter(renewed: renewed, recovery: recovery, failures: try integer(attempt["failures"])))
            })
        }
        return .object(result)
    }

    // Each provider ({ providerId, rows: [{ label, isPrimary }] }) under the
    // case's settings is answered by the lines the popup draws, each a shape
    // and the labels of its one or two rows, plus the column decision.
    static func evaluateLayout(_ input: JSONValue) throws -> JSONValue {
        let settings = try readSettings(input["settings"])
        let usages = try (input["providers"]?.arrayValue ?? []).map { provider -> ProviderUsage in
            guard let id = provider["providerId"]?.stringValue else { throw Failure(description: "provider needs providerId") }
            let rows = try (provider["rows"]?.arrayValue ?? []).map { row -> UsageRow in
                guard let label = row["label"]?.stringValue, let isPrimary = row["isPrimary"]?.boolValue else {
                    throw Failure(description: "row needs label and isPrimary")
                }
                return UsageRow(label: label, window: nil, isPrimary: isPrimary)
            }
            return ProviderUsage(providerId: id, displayName: id, rows: rows, overallUsedPercent: 0, statusText: "", state: .ok)
        }
        let sections = RowLayout.sections(settings, usages)
        let twoColumn = RowLayout.usesTwoColumns(settings, sections)
        return .object(JSONObject([
            ("twoColumn", .bool(twoColumn)),
            ("sections", .array(sections.map { section in
                .object(JSONObject([
                    ("providerId", .string(section.usage.providerId)),
                    ("lines", .array(RowLayout.lines(settings, section.usage.providerId, section.rows, twoColumn: twoColumn).map { line in
                        .object(JSONObject([
                            ("shape", .string(line.shape)),
                            ("rows", .array([line.left, line.right].compactMap { $0 }.map { .string($0.label) })),
                        ]))
                    })),
                ]))
            })),
        ]))
    }

    // Each reset time against the case's "now" (null: no reset known) in both
    // display modes and both languages. Now's UTC offset is the local zone.
    static func evaluateResetText(_ input: JSONValue) throws -> JSONValue {
        guard let now = optional(input, "now")?.stringValue.flatMap(Timestamp.parseISO8601WithOffset) else {
            throw Failure(description: "resettext needs a \"now\"")
        }
        let results = (input["resetsAt"]?.arrayValue ?? []).map { item -> JSONValue in
            let reset = item.stringValue.flatMap(Timestamp.parseISO8601)
            func text(_ loc: Loc, _ remaining: Bool) -> JSONValue {
                .string(loc.resetText(reset, now: now.timestamp, utcOffsetSeconds: now.offsetSeconds, remaining: remaining))
            }
            return .object(JSONObject([
                ("clock", .object(JSONObject([("en", text(.english, false)), ("ko", text(.korean, false))]))),
                ("remaining", .object(JSONObject([("en", text(.english, true)), ("ko", text(.korean, true))]))),
            ]))
        }
        return .object(JSONObject([("results", .array(results))]))
    }

    // One full read plus the rolling updates merged into it, in order.
    static func mapCodex(_ input: JSONValue) throws -> ProviderUsage {
        guard var merged = input["read"] else { throw Failure(description: "codex input has no read") }
        for update in input["updates"]?.arrayValue ?? [] {
            merged = try RateLimitMapper.mergeRollingUpdate(merged, update)
        }
        return RateLimitMapper.fromJSONResult(merged, creditsFullAmount: creditsFullAmount(input))
    }

    static func mapClaude(_ input: JSONValue) -> ProviderUsage {
        ClaudeUsageMapper.fromJSON(input["response"] ?? .null, creditsFullAmount: creditsFullAmount(input))
    }

    static func creditsFullAmount(_ input: JSONValue) -> Double {
        input["options"]?["creditsFullAmount"]?.doubleValue ?? CreditsGauge.defaultFullAmount
    }

    // Each row under the case's settings, answered by its settings key,
    // whether it is shown, and its shape.
    static func evaluateRows(_ input: JSONValue) throws -> JSONValue {
        let settings = try readSettings(input["settings"])
        let rows = try (input["rows"]?.arrayValue ?? []).map { item -> JSONValue in
            guard let providerId = item["providerId"]?.stringValue, let label = item["label"]?.stringValue,
                  let isPrimary = item["isPrimary"]?.boolValue else { throw Failure(description: "row needs providerId, label, isPrimary") }
            let row = UsageRow(label: label, window: nil, isPrimary: isPrimary)
            return .object(JSONObject([
                ("key", .string(RowShapes.key(providerId, label))),
                ("visible", .bool(RowShapes.isVisible(settings, providerId, row))),
                ("shape", .string(RowShapes.resolve(settings, providerId, row))),
            ]))
        }
        return .object(JSONObject([("rows", .array(rows))]))
    }

    // "parse": point texts, answered by the points they give and how the box
    // writes them back, or { "ok": false }. "steps": checks of one tracker in
    // order, each answered by the alerts it raises and the notification they
    // make (null when there are none). A step's settings apply from that step
    // on. Notifications are compared in English.
    static func evaluateAlerts(_ input: JSONValue) throws -> JSONValue {
        var result = JSONObject()
        if let texts = optional(input, "parse")?.arrayValue {
            result["parsed"] = .array(texts.map { item in
                switch UsageAlerts.parsePoints(optional(item, "text")?.stringValue, credits: item["credits"]?.boolValue == true) {
                case .failure:
                    return .object(JSONObject([("ok", .bool(false))]))
                case .success(let points):
                    return .object(JSONObject([
                        ("ok", .bool(true)),
                        ("points", .array(points.map { .number($0) })),
                        ("formatted", .string(UsageAlerts.formatPoints(points))),
                    ]))
                }
            })
        }
        // "language": "Korean" compares the notification in Korean.
        let loc = Loc(isKorean: optional(input, "language")?.stringValue.map { Loc(setting: $0, systemLanguageCode: "en").isKorean } ?? false)
        if let steps = optional(input, "steps")?.arrayValue {
            var settings = try readSettings(input["settings"])
            let tracker = UsageAlertTracker(silentFirstSight: optional(input, "silentFirstSight")?.boolValue ?? false)
            result["steps"] = .array(try steps.map { step in
                if let stepSettings = optional(step, "settings") { settings = try readSettings(stepSettings) }
                let usages = try (step["usages"]?.arrayValue ?? []).map(readUsage)
                let alerts = tracker.evaluate(settings, usages)
                var notification = JSONValue.null
                if !alerts.isEmpty {
                    let built = UsageAlerts.buildNotification(alerts, loc)
                    notification = .object(JSONObject([("title", .string(built.title)), ("body", .string(built.body))]))
                }
                return .object(JSONObject([
                    ("alerts", .array(alerts.map { alert in
                        .object(JSONObject([
                            ("providerId", .string(alert.providerId)),
                            ("label", .string(alert.row.label)),
                            ("point", .number(alert.point)),
                            ("remaining", .number(alert.remaining)),
                            ("critical", .bool(alert.critical)),
                        ]))
                    })),
                    ("notification", notification),
                ]))
            })
        }
        return .object(result)
    }

    // Each text (null allowed) is answered by { "ok": false }, or by the parsed
    // binding, its canonical text, and whether that text parses back to it.
    static func evaluateHotkeys(_ input: JSONValue) throws -> JSONValue {
        guard let texts = input["texts"]?.arrayValue else { throw Failure(description: "hotkeys input has no texts") }
        return .object(JSONObject([("results", .array(texts.map { text in
            guard let definition = HotkeyDefinition.parse(text.stringValue) else {
                return .object(JSONObject([("ok", .bool(false))]))
            }
            let formatted = definition.format()
            return .object(JSONObject([
                ("ok", .bool(true)),
                ("modifiers", .number(String(definition.modifiers))),
                ("virtualKey", .number(String(definition.virtualKey))),
                ("formatted", .string(formatted)),
                ("reparses", .bool(HotkeyDefinition.parse(formatted) == definition)),
            ]))
        }))]))
    }

    // The fixture form of a mapped result: every field of every row.
    public static func describe(_ usage: ProviderUsage) -> JSONValue {
        func optionalString(_ text: String?) -> JSONValue { text.map { .string($0) } ?? .null }
        func optionalNumber(_ value: Double?) -> JSONValue { value.map { .number($0) } ?? .null }
        return .object(JSONObject([
            ("providerId", .string(usage.providerId)),
            ("displayName", .string(usage.displayName)),
            ("state", .string(usage.state.rawValue)),
            ("statusText", .string(usage.statusText)),
            ("overallUsedPercent", .number(usage.overallUsedPercent)),
            ("rows", .array(usage.rows.map { row in
                .object(JSONObject([
                    ("label", .string(row.label)),
                    ("scope", optionalString(row.scope)),
                    ("isPrimary", .bool(row.isPrimary)),
                    ("usedPercent", optionalNumber(row.window?.usedPercent)),
                    ("resetsAt", optionalString(row.window?.resetsAt?.iso8601)),
                    ("windowDurationMins", row.window?.windowDurationMins.map { .number($0) } ?? .null),
                    ("detailText", optionalString(row.detailText)),
                    ("beyondFull", .bool(row.beyondFull)),
                    ("creditsLeft", optionalNumber(row.creditsLeft)),
                ]))
            })),
        ]))
    }

    // The reverse of describe, for inputs that start from mapped usage.
    // Only label is required; a row without usedPercent has no window.
    static func readUsage(_ usage: JSONValue) throws -> ProviderUsage {
        guard let providerId = usage["providerId"]?.stringValue, let displayName = usage["displayName"]?.stringValue else {
            throw Failure(description: "usage needs providerId and displayName")
        }
        let rows = try (usage["rows"]?.arrayValue ?? []).map { row -> UsageRow in
            guard let label = row["label"]?.stringValue else { throw Failure(description: "row needs a label") }
            let window = optional(row, "usedPercent")?.doubleValue.map {
                RateLimitWindow(usedPercent: $0,
                                resetsAt: optional(row, "resetsAt")?.stringValue.flatMap(Timestamp.parseISO8601),
                                windowDurationMins: optional(row, "windowDurationMins")?.doubleValue.map { Int64($0) })
            }
            return UsageRow(label: label, window: window,
                            isPrimary: optional(row, "isPrimary")?.boolValue ?? false,
                            detailText: optional(row, "detailText")?.stringValue,
                            scope: optional(row, "scope")?.stringValue,
                            beyondFull: optional(row, "beyondFull")?.boolValue ?? false,
                            creditsLeft: optional(row, "creditsLeft")?.doubleValue)
        }
        var state = ProviderState.ok
        if let text = optional(usage, "state")?.stringValue {
            guard let parsed = ProviderState(rawValue: text) else { throw Failure(description: "unknown state \(text)") }
            state = parsed
        }
        return ProviderUsage(providerId: providerId, displayName: displayName, rows: rows,
                             overallUsedPercent: optional(usage, "overallUsedPercent")?.doubleValue ?? 0,
                             statusText: optional(usage, "statusText")?.stringValue ?? "", state: state)
    }

    static func readSettings(_ settings: JSONValue?) throws -> AppSettings {
        guard let settings else { return AppSettings() }
        return try AppSettings.fromJSON(settings)
    }

    // A property that is absent or null reads as nil.
    static func optional(_ element: JSONValue, _ name: String) -> JSONValue? {
        guard let value = element[name], !value.isNull else { return nil }
        return value
    }

    static func findMismatch(_ expected: JSONValue, _ actual: JSONValue, _ path: String) -> String? {
        switch (expected, actual) {
        case (.null, .null):
            return nil
        case (.object(let lhs), .object(let rhs)):
            for key in lhs.keys + rhs.keys.filter({ !lhs.keys.contains($0) }) {
                guard let left = lhs[key] else { return "\(path).\(key): unexpected field" }
                guard let right = rhs[key] else { return "\(path).\(key): missing field" }
                if let mismatch = findMismatch(left, right, "\(path).\(key)") { return mismatch }
            }
            return nil
        case (.array(let lhs), .array(let rhs)):
            guard lhs.count == rhs.count else { return "\(path): expected \(lhs.count) items, got \(rhs.count)" }
            for (index, pair) in zip(lhs, rhs).enumerated() {
                if let mismatch = findMismatch(pair.0, pair.1, "\(path)[\(index)]") { return mismatch }
            }
            return nil
        case (.number, .number):
            guard let left = expected.doubleValue, let right = actual.doubleValue, abs(left - right) <= tolerance else {
                return "\(path): expected \(expected.serialized()), got \(actual.serialized())"
            }
            return nil
        default:
            return expected == actual ? nil : "\(path): expected \(expected.serialized()), got \(actual.serialized())"
        }
    }
}
