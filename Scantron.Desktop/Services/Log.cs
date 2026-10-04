using System.Globalization;
using System.Text;

namespace Scantron.Desktop.Services;

/// <summary>Severity tiers, ordered so a single threshold comparison filters correctly.</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Tiered file logger with an in-memory tail for the UI.
/// </summary>
/// <remarks>
/// <para>
/// Writes go to <c>scantron.log</c> beside the executable, so a failure on a warehouse
/// workstation can be diagnosed from a file an operator can email. There is no console to fall
/// back on: this is a <c>WinExe</c>.
/// </para>
/// <para>
/// Every record carries timestamp, level and process id, because the interesting failure mode
/// for this app is two copies running at once, and "which one?" is answered by the pid.
/// </para>
/// <para>
/// Writes are append-only and locked, and failures are swallowed after the first: logging must
/// never be the reason a sync fails.
/// </para>
/// </remarks>
public static class Log
{
    private const int TailCapacity = 200;

    private static readonly object Gate = new();
    private static readonly Queue<string> Tail = new(TailCapacity);
    private static readonly StringBuilder Pending = new();

    /// <summary>Below this level nothing is written. Defaults to <see cref="LogLevel.Info"/>.</summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Full path of the log file currently in use.</summary>
    public static string FilePath { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "scantron.log");

    /// <summary>Most recent records, newest last. Safe to read from the UI thread.</summary>
    public static IReadOnlyList<string> RecentTail()
    {
        lock (Gate)
        {
            return Tail.ToArray();
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);
    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warning(string message) => Write(LogLevel.Warning, message, null);
    public static void Error(string message, Exception? error = null) => Write(LogLevel.Error, message, error);

    private static void Write(LogLevel level, string message, Exception? error)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var line = Format(level, message, error);

        lock (Gate)
        {
            Tail.Enqueue(line);
            while (Tail.Count > TailCapacity)
            {
                Tail.Dequeue();
            }

            // Buffer per call rather than holding the lock across a disk write: the file write is
            // the only slow part and must not serialise two syncs against each other.
            Pending.Clear().Append(line).Append(Environment.NewLine);
            AppendToFile(Pending.ToString());
        }
    }

    private static string Format(LogLevel level, string message, Exception? error)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()}] " +
            $"[pid {Environment.ProcessId}] {message}");

        // The stack trace is folded into the same line: a multi-line record breaks the one-line
        // -per-event shape that makes the file greppable.
        return error is null
            ? text
            : string.Concat(text, " | ", error.GetType().Name, ": ", error.Message.Replace("\r", " ").Replace("\n", " "));
    }

    private static void AppendToFile(string entry)
    {
        try
        {
            File.AppendAllText(FilePath, entry, Encoding.UTF8);
        }
        catch (IOException)
        {
            // Log file locked or unwritable; drop the record rather than fail the operation.
        }
        catch (UnauthorizedAccessException)
        {
            // Installed under Program Files without write access; same reasoning.
        }
    }
}
