using System;
using System.IO;
using System.Text.Json;

namespace QuotaScope.Providers.Claude;

// Reads the Claude Code OAuth access token for immediate use only.
// The token must never be cached, copied, or logged; Claude Code owns refresh.
internal static class ClaudeCredentialReader
{
    public static string CredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    // Split out for the credentials-file watcher, which needs a directory to
    // observe plus a name filter.
    public static string CredentialsDirectory => Path.GetDirectoryName(CredentialsPath) ?? string.Empty;

    public static string CredentialsFileName => Path.GetFileName(CredentialsPath);

    public static bool CredentialsFileExists() => File.Exists(CredentialsPath);

    // Used to detect that Claude Code rewrote the file (re-login/token refresh)
    // without retaining any token material.
    public static DateTime GetCredentialsFileStampUtc()
    {
        try
        {
            return File.Exists(CredentialsPath) ? File.GetLastWriteTimeUtc(CredentialsPath) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    // Access token expiry (epoch milliseconds). Read on its own so renewal can
    // be driven by the clock without the token ever being materialised.
    public static DateTimeOffset? TryReadExpiresAt()
    {
        try
        {
            if (!File.Exists(CredentialsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.ValueKind == JsonValueKind.Object
                && oauth.TryGetProperty("expiresAt", out var expiresAt)
                && expiresAt.ValueKind == JsonValueKind.Number
                && expiresAt.TryGetInt64(out var epochMilliseconds))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryReadAccessToken()
    {
        try
        {
            if (!File.Exists(CredentialsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.ValueKind == JsonValueKind.Object
                && oauth.TryGetProperty("accessToken", out var token)
                && token.ValueKind == JsonValueKind.String)
            {
                return token.GetString();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
