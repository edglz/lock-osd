using System;
using System.IO;

namespace LockOsd;

/// <summary>
/// Records process lifetime and lock key changes, enough to tell a dead process from a live one
/// that stopped reacting. It only ever receives lock key names, never other keyboard input.
/// </summary>
internal static class DiagnosticLog
{
    private const long MaxLogBytes = 1024 * 1024;

    private static readonly string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LockOsd");

    private static readonly object Gate = new();

    public static void Event(string message) => Write("events.log", $"{DateTime.Now:O} {message}\n");

    public static void Crash(object exception) => Write("crash.log", $"{DateTime.Now:O}\n{exception}\n\n");

    private static void Write(string fileName, string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Directory_);
                var path = Path.Combine(Directory_, fileName);
                if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                {
                    File.Delete(path);
                }

                File.AppendAllText(path, text);
            }
        }
        catch (IOException)
        {
            // Diagnostics must never be the reason the app stops.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
