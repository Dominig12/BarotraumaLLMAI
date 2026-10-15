using System;
using System.IO;

namespace LlmAgents;

public enum LogLevel { Info, Warn, Error }

/// <summary>
/// Tiny static logger. The host (the server plugin, or tests) sets <see cref="Sink"/> for console output;
/// every line also goes to <see cref="FilePath"/> if set. Never throws.
/// </summary>
public static class Log
{
    static readonly object Lock = new();

    public static Action<LogLevel, string>? Sink { get; set; }
    public static string? FilePath { get; set; }

    /// <summary>Console sink only receives lines at or above this level (file gets everything).</summary>
    public static LogLevel ConsoleLevel { get; set; } = LogLevel.Warn;

    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m) => Write(LogLevel.Error, m);

    static void Write(LogLevel level, string message)
    {
        try
        {
            if (level >= ConsoleLevel) Sink?.Invoke(level, "[LlmAgents] " + message);
            var path = FilePath;
            if (path == null) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}";
            lock (Lock) File.AppendAllText(path, line);
        }
        catch
        {
            // logging must never break the game
        }
    }
}
