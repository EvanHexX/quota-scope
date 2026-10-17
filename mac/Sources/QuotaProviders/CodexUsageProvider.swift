import Foundation
import QuotaCore

// Port of app/Providers/Codex/CodexUsageProvider.cs.
public final class CodexUsageProvider: UsageProvider {
    public let client: CodexAppServerClient

    public nonisolated var id: String { RateLimitMapper.providerId }
    public nonisolated var displayName: String { RateLimitMapper.providerDisplayName }

    public init(settings: ProviderSettings, environment: CommandEnvironment = .current) {
        client = CodexAppServerClient(command: settings.command, creditsFullAmount: settings.creditsFullAmount, environment: environment)
    }

    public func read() async throws -> ProviderUsage { try await client.readRateLimits() }

    public func reconnect() async throws -> ProviderUsage { try await client.restart() }

    public func setUpdateHandler(_ handler: @escaping @Sendable (ProviderUsage) -> Void) async {
        await client.setUpdateHandler(handler)
    }

    public func close() async { await client.close() }
}
