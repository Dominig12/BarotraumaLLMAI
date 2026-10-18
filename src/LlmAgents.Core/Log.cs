using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LlmAgents;

public enum LogLevel { Info, Warn, Error }

/// <summary>
/// Tiny static logger. The host (the server plugin, or tests) sets <see cref="Sink"/> for console output;
/// every line also goes to <see cref="FilePath"/> if set. Never throws.
/// File lines are queued and written by one background thread (the game thread never waits for the disk), and the
/// file is rotated at <see cref="MaxFileBytes"/>: the old part is kept as "name.1", so a long server run cannot fill
/// the disk.
/// </summary>
public static class Log
{
    public static Action<LogLevel, string>? Sink { get; set; }

    static string? filePath;
    public static string? FilePath
    {
        get => filePath;
        set { filePath = value; EnsureWriter(); }
    }

    public static long MaxFileBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Console sink only receives lines at or above this level (file gets everything).</summary>
    public static LogLevel ConsoleLevel { get; set; } = LogLevel.Warn;

    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m) => Write(LogLevel.Error, m);

    static readonly ConcurrentDictionary<string, (DateTime At, int Skipped)> throttled = new();

    /// <summary>An error that may repeat every frame (a broken game call): logged once per <paramref name="seconds"/> per
    /// key, with the number of repeats skipped meanwhile, so one fault cannot flood the console and the file.</summary>
    public static void ErrorThrottled(string key, string message, double seconds = 60)
    {
        var now = DateTime.UtcNow;
        if (throttled.TryGetValue(key, out var last) && (now - last.At).TotalSeconds < seconds)
        {
            throttled[key] = (last.At, last.Skipped + 1);
            return;
        }
        throttled[key] = (now, 0);
        Error(last.Skipped > 0 ? $"{message} (and {last.Skipped} more time(s) since the last report)" : message);
    }

    const int MaxQueued = 20000;
    static readonly BlockingCollection<(string Path, string Line, TaskCompletionSource<bool>? Flush)> queue =
        new(new ConcurrentQueue<(string, string, TaskCompletionSource<bool>?)>());
    static readonly object startLock = new();
    static Thread? writer;

    static void Write(LogLevel level, string message)
    {
        try
        {
            if (level >= ConsoleLevel) Sink?.Invoke(level, "[LlmAgents] " + message);
            var path = filePath;
            if (path == null) return;
            if (queue.Count >= MaxQueued) return;   // the disk cannot keep up: drop rather than eat memory
            EnsureWriter();
            queue.Add((path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}", null));
        }
        catch
        {
            // logging must never break the game
        }
    }

    /// <summary>Waits (up to the timeout) until queued lines are on disk: plugin unload, tests.</summary>
    public static bool Flush(int timeoutMs = 2000)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (startLock)
        {
            if (writer == null) return true;
            // A queue marker cannot be signalled by a previous batch going idle while a producer
            // is adding new lines. It acknowledges exactly the lines queued before this call.
            queue.Add(("", "", completion));
        }
        return completion.Task.Wait(timeoutMs) && completion.Task.Result;
    }

    static volatile bool stopping;

    /// <summary>Writes what is queued and stops the writer thread: a plugin reload must not leave a thread holding the
    /// old assembly and the file open. The next line written starts a new one.</summary>
    public static void Shutdown(int timeoutMs = 2000)
    {
        Thread? t;
        lock (startLock) { t = writer; if (t == null) return; stopping = true; }
        try { t.Join(timeoutMs); } catch { }
        lock (startLock) { writer = null; stopping = false; }
    }

    static void EnsureWriter()
    {
        if (writer != null || filePath == null) return;
        lock (startLock)
        {
            if (writer != null) return;
            writer = new Thread(Run) { IsBackground = true, Name = "LlmAgents log" };
            writer.Start();
        }
    }

    static void Run()
    {
        StreamWriter? w = null;
        string? openPath = null;
        while (true)
        {
            (string Path, string Line, TaskCompletionSource<bool>? Flush) item;
            try
            {
                if (!queue.TryTake(out item, stopping ? 0 : 500))
                {
                    w?.Flush();
                    if (stopping) { w?.Dispose(); return; }
                    continue;
                }
            }
            catch { continue; }
            try
            {
                if (item.Flush != null)
                {
                    w?.Flush();
                    item.Flush.TrySetResult(true);
                    continue;
                }
                if (openPath != item.Path || w == null)
                {
                    w?.Dispose();
                    w = Open(item.Path);
                    openPath = item.Path;
                }
                if (w.BaseStream.Length >= MaxFileBytes)
                {
                    w.Dispose();
                    Rotate(item.Path);
                    w = Open(item.Path);
                }
                w.Write(item.Line);
                if (queue.Count == 0) w.Flush();
            }
            catch
            {
                try { w?.Dispose(); } catch { }
                w = null;
                item.Flush?.TrySetResult(false);
            }
        }
    }

    static StreamWriter Open(string path) =>
        new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));

    static void Rotate(string path)
    {
        var old = path + ".1";
        try { if (File.Exists(old)) File.Delete(old); File.Move(path, old); } catch { /* keep appending to the big file */ }
    }
}
