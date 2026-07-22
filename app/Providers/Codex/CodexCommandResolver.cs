using System;
using System.IO;
using System.Linq;

namespace QuotaScope.Providers.Codex;

internal sealed record CodexCommandSpec(string FileName, string Arguments, string DisplayText);

internal static class CodexCommandResolver
{
    public static CodexCommandSpec Resolve(string? configuredCommand)
    {
        var command = string.IsNullOrWhiteSpace(configuredCommand) ? "codex" : configuredCommand.Trim();
        if (!IsDefaultCodexCommand(command))
        {
            return new CodexCommandSpec(command, "app-server", $"{command} app-server");
        }

        var local = ResolveLocalCodexExecutable();
        if (!string.IsNullOrWhiteSpace(local))
        {
            return new CodexCommandSpec(local, "app-server", $"{local} app-server");
        }

        return new CodexCommandSpec("cmd.exe", "/c codex app-server", "codex app-server");
    }

    private static bool IsDefaultCodexCommand(string command)
    {
        return command.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || command.Equals("codex.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveLocalCodexExecutable()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) return null;

        var bin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        if (!Directory.Exists(bin)) return null;

        var latestHashedCodex = Directory.EnumerateDirectories(bin)
            .Select(path => Path.Combine(path, "codex.exe"))
            .Where(File.Exists)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latestHashedCodex is not null)
        {
            return latestHashedCodex.FullName;
        }

        var rootCodex = Path.Combine(bin, "codex.exe");
        return File.Exists(rootCodex) ? rootCodex : null;
    }
}
