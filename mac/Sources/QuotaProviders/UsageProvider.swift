import Foundation
import QuotaCore

// Port of app/Providers/IUsageProvider.cs. Each provider is an actor: polls,
// reconnects, and pushed updates can arrive from different places, and the
// C# providers serialise them with locks and semaphores for the same reason.
public protocol UsageProvider: AnyObject, Sendable {
    nonisolated var id: String { get }
    nonisolated var displayName: String { get }
    func read() async throws -> ProviderUsage
    func reconnect() async throws -> ProviderUsage
    // Only providers with push support call it: Codex rolling updates, and a
    // Claude credentials change.
    func setUpdateHandler(_ handler: @escaping @Sendable (ProviderUsage) -> Void) async
    // Ends child processes and timers. The app calls it on quit.
    func close() async
}

public struct ProviderError: Error, CustomStringConvertible, Equatable {
    public let description: String
    public init(_ description: String) { self.description = description }
}
