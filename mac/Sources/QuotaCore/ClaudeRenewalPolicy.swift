import Foundation

// The timing rules of app/Providers/Claude/ClaudeSessionRenewer.cs, kept apart
// from the process-running part (QuotaProviders) so they build anywhere and are
// checked against fixtures/renewer.
//
// Claude Code owns OAuth refresh; QuotaScope only nudges it by running a
// read-only `claude` command shortly before the 8-hour access token expires
// (and again after a 401), and lets Claude Code rewrite its own credentials.
public enum ClaudeRenewalPolicy {
    // Which command renews is a property of the CLI, not a documented contract:
    // it has to be one that needs a live token. Measured 2026-08-13 against
    // claude 2.x: `auth status` never rewrites the credentials, `mcp list` does.
    public static let renewalArguments = ["mcp", "list"]

    // Start nudging this long before expiry so a renewal lands before the
    // first 401 rather than after it.
    public static let renewLeadTime: TimeInterval = 15 * 60

    // Escalating cooldowns for attempts that did not rewrite the credentials.
    static let cooldowns: [TimeInterval] = [2 * 60, 10 * 60, 30 * 60]

    // After this many failures in a row, stop spawning processes entirely and
    // wait for the credentials to change (a manual sign-in calls reset).
    public static let maxConsecutiveFailures = 5

    // A nudge sent while the token is still valid may legitimately do nothing:
    // Claude Code decides for itself whether a live token is worth refreshing.
    // Those retry on a flat interval instead of the escalating one.
    public static let proactiveRetry: TimeInterval = 2 * 60

    public static let processTimeout: TimeInterval = 30

    // True only when expiry is known and close; an unreadable expiry must not
    // turn into a nudge on every poll.
    public static func isExpiring(_ expiresAt: Date?, now: Date) -> Bool {
        guard let expiresAt else { return false }
        return expiresAt.timeIntervalSince(now) <= renewLeadTime
    }

    // Cooldown after `consecutiveFailures` failed attempts; nil once the
    // renewer has given up until the credentials change.
    public static func cooldownAfter(_ consecutiveFailures: Int) -> TimeInterval? {
        if consecutiveFailures <= 0 { return 0 }
        if consecutiveFailures >= maxConsecutiveFailures { return nil }
        return cooldowns[min(consecutiveFailures - 1, cooldowns.count - 1)]
    }

    // Delay before the next attempt, given how the last one ended. `failures`
    // is the running count after this attempt. `recovery` means the token was
    // already dead, so only those no-ops count as failures.
    public static func nextDelayAfter(renewed: Bool, recovery: Bool, failures: Int) -> TimeInterval? {
        if renewed { return 0 }
        if !recovery { return proactiveRetry }
        return cooldownAfter(failures)
    }
}
