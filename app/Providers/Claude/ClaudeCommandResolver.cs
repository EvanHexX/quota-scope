using System;
using System.IO;

namespace QuotaScope.Providers.Claude;

internal sealed record ClaudeCommandSpec(string FileName, string Arguments, string DisplayText);

// Locates the Claude Code CLI the same way CodexCommandResolver locates codex:
// prefer a known install path, otherwise go through cmd.exe so PATH shims
// (claude.cmd from an npm install) still work.
internal static class ClaudeCommandResolver
{
    public static ClaudeCommandSpec Resolve(string arguments)
    {
        var executable = ResolveLocalClaudeExecutable();
        return executable is null
            ? new ClaudeCommandSpec("cmd.exe", $"/c claude {arguments}", $"claude {arguments}")
            : new ClaudeCommandSpec(executable, arguments, $"{executable} {arguments}");
    }

    // Only .exe candidates: a .cmd shim cannot be started without a shell, and
    // the cmd.exe fallback already covers that case.
    private static string? ResolveLocalClaudeExecutable()
    {
        foreach (var candidate in CandidatePaths())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Unreadable path: fall through to the next candidate.
            }
        }
        return null;
    }

    private static string[] CandidatePaths()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            // Native installer (current default).
            Combine(userProfile, ".local", "bin", "claude.exe"),
            Combine(localAppData, "Programs", "claude", "claude.exe"),
            Combine(localAppData, "Anthropic", "Claude Code", "claude.exe")
        };
    }

    private static string Combine(string root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root)) return string.Empty;
        var path = root;
        foreach (var part in parts)
        {
            path = Path.Combine(path, part);
        }
        return path;
    }
}
