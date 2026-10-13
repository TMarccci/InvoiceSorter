namespace InvoiceSorter.Core.Logging;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant(),-7}] {Message}";
}

/// <summary>
/// Application log. Every entry goes to a daily log file (see <see cref="AppPaths.LogDir"/>),
/// to an in-memory buffer and to <see cref="EntryAdded"/> subscribers (the UI log panel).
/// </summary>
public static class AppLog
{
    private const int MaxBuffered = 50_000;
    private static readonly object Sync = new();
    private static readonly List<LogEntry> Buffer = new();

    public static event Action<LogEntry>? EntryAdded;

    public static string CurrentLogFile => Path.Combine(AppPaths.LogDir, $"InvoiceSorter_{DateTime.Now:yyyy-MM-dd}.log");

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message, Exception? ex = null) =>
        Write(LogLevel.Error, ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    public static IReadOnlyList<LogEntry> EntriesSince(DateTime since)
    {
        lock (Sync) return Buffer.Where(e => e.Time >= since).ToList();
    }

    public static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (Sync)
        {
            Buffer.Add(entry);
            if (Buffer.Count > MaxBuffered) Buffer.RemoveRange(0, Buffer.Count - MaxBuffered);
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.AppendAllText(CurrentLogFile, entry + Environment.NewLine);
            }
            catch
            {
                // Logging must never break processing.
            }
        }
        EntryAdded?.Invoke(entry);
    }
}
