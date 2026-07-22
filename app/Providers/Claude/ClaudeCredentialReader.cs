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
