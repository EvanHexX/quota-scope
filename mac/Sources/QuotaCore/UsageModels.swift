import Foundation

// Port of app/Providers/UsageModels.cs. Names and meanings match the C#
// records so a fix on one side can be found on the other.

public enum ProviderState: String {
    case ok = "Ok"
    case unauthenticated = "Unauthenticated"
    case unavailable = "Unavailable"
    case rateLimited = "RateLimited"
    case stale = "Stale"
}

// All layers speak usedPercent (0 = untouched, 100 = exhausted); there is no
// remaining-percent anywhere in the model.
public struct RateLimitWindow: Equatable {
    public var usedPercent: Double
    public var resetsAt: Timestamp?
    public var windowDurationMins: Int64?

    public init(usedPercent: Double, resetsAt: Timestamp?, windowDurationMins: Int64?) {
        self.usedPercent = usedPercent
        self.resetsAt = resetsAt
        self.windowDurationMins = windowDurationMins
    }
}

// window is nil for text-only rows (e.g. credits balance); detailText carries
// the value. label is the stable English key settings are stored under, so
// anything that only changes how a row is named goes in scope instead.
// beyondFull marks a credits row whose real amount lies past the configured
// full amount; creditsLeft is the amount credits alerts compare against.
public struct UsageRow: Equatable {
    public var label: String
    public var window: RateLimitWindow?
    public var isPrimary: Bool
    public var detailText: String?
    public var scope: String?
    public var beyondFull: Bool
    public var creditsLeft: Double?

    public init(label: String, window: RateLimitWindow?, isPrimary: Bool, detailText: String? = nil,
                scope: String? = nil, beyondFull: Bool = false, creditsLeft: Double? = nil) {
        self.label = label
        self.window = window
        self.isPrimary = isPrimary
        self.detailText = detailText
        self.scope = scope
        self.beyondFull = beyondFull
        self.creditsLeft = creditsLeft
    }
}

public struct ProviderUsage: Equatable {
    public var providerId: String
    public var displayName: String
    public var rows: [UsageRow]
    public var overallUsedPercent: Double
    public var statusText: String
    public var updatedAt: Date
    public var state: ProviderState

    public init(providerId: String, displayName: String, rows: [UsageRow], overallUsedPercent: Double,
                statusText: String, updatedAt: Date = Date(), state: ProviderState) {
        self.providerId = providerId
        self.displayName = displayName
        self.rows = rows
        self.overallUsedPercent = overallUsedPercent
        self.statusText = statusText
        self.updatedAt = updatedAt
        self.state = state
    }

    public static func offline(_ providerId: String, _ displayName: String, _ message: String, _ state: ProviderState) -> ProviderUsage {
        ProviderUsage(providerId: providerId, displayName: displayName, rows: [], overallUsedPercent: 0,
                      statusText: message, state: state)
    }
}
