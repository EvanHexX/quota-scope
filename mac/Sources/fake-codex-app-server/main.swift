import Foundation
import QuotaCore

// A stand-in for `codex app-server` that quotascope-providertest drives the
// Codex client against, so the client can be tested without a real CLI or
// account. It behaves like the measured app-server (docs/MACOS_BUILD.md, 6.2):
// - requests before the `initialized` notification are dropped;
// - notifications the client does not use arrive along the way;
// - a read is answered, then followed by one sparse rolling update;
// - closing stdin ends it with exit 0.
// FAKE_CODEX_MODE=silent never answers a read; FAKE_CODEX_MODE=crash exits
// with code 3 and a stderr line on the first read.

let mode = ProcessInfo.processInfo.environment["FAKE_CODEX_MODE"] ?? "normal"
var initialized = false
var reads = 0

func send(_ value: JSONValue) {
    FileHandle.standardOutput.write(Data((value.serialized() + "\n").utf8))
}

func respond(_ id: JSONValue, _ result: JSONValue) {
    send(.object(JSONObject([("id", id), ("result", result)])))
}

let read = try! JSONValue.parse("""
{
  "rateLimits": {
    "limitId": "codex", "limitName": null, "planType": "plus",
    "primary": { "usedPercent": 20, "resetsAt": 1893499200, "windowDurationMins": 300 },
    "secondary": { "usedPercent": 30, "resetsAt": 1894053600, "windowDurationMins": 10080 },
    "credits": { "hasCredits": true, "unlimited": false, "balance": "500" }
  },
  "rateLimitsByLimitId": {
    "codex": { "limitId": "codex",
      "primary": { "usedPercent": 20, "resetsAt": 1893499200, "windowDurationMins": 300 },
      "secondary": { "usedPercent": 30, "resetsAt": 1894053600, "windowDurationMins": 10080 } }
  }
}
""")

let update = try! JSONValue.parse("""
{ "rateLimits": { "limitId": "codex", "primary": { "usedPercent": 25, "resetsAt": null, "windowDurationMins": 300 }, "secondary": null, "credits": null } }
""")

while let line = readLine(strippingNewline: true) {
    guard let message = try? JSONValue.parse(line), let method = message["method"]?.stringValue else { continue }
    let id = message["id"]
    switch method {
    case "initialize":
        send(try! JSONValue.parse(#"{"method":"remoteControl/status/changed","params":{"status":"disabled"}}"#))
        respond(id ?? .null, .object(JSONObject([("userAgent", .string("fake-codex"))])))
    case "initialized":
        initialized = true
        send(try! JSONValue.parse(#"{"method":"account/updated","params":{"authMode":"chatgpt","planType":"plus"}}"#))
    case "account/rateLimits/read":
        guard initialized else { continue }
        if mode == "silent" { continue }
        if mode == "crash" {
            FileHandle.standardError.write(Data("fake-codex: simulated failure\n".utf8))
            exit(3)
        }
        reads += 1
        respond(id ?? .null, read)
        send(.object(JSONObject([("method", .string("account/rateLimits/updated")), ("params", update)])))
    default:
        if let id {
            send(.object(JSONObject([("id", id), ("error", .object(JSONObject([("code", .number(Int64(-32601))), ("message", .string("unknown method \(method)"))])))])))
        }
    }
}
exit(0)
