using System;
using System.IO;

namespace QuotaScope.WinUI;

// Appends unhandled-exception details to crash.log next to the exe so
// abnormal exits can be diagnosed from the field.
internal static class CrashLog
{
    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(AppContext.BaseDirectory, "crash.log");

    public static void Write(string source, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never make things worse.
        }
    }
}
