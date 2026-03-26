namespace CopilotHive.Worker;

/// <summary>
/// Lightweight structured logger for worker processes.
/// Reads VERBOSE_LOGGING environment variable to control output detail.
/// </summary>
public sealed class WorkerLogger(string category)
{
    private static readonly bool Verbose =
        string.Equals(Environment.GetEnvironmentVariable("VERBOSE_LOGGING"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether verbose (debug-level) logging is enabled.</summary>
    public static bool IsVerbose => Verbose;

    /// <summary>Logs an informational message (always visible).</summary>
    public void Info(string message) =>
        Console.WriteLine($"[{category}] {message}");

    /// <summary>Logs an error message (always visible).</summary>
    public void Error(string message) =>
        Console.Error.WriteLine($"[{category}] ERROR: {message}");

    /// <summary>Logs a warning message (always visible).</summary>
    public void Warn(string message) =>
        Console.WriteLine($"[{category}] WARN: {message}");

    /// <summary>Logs a debug message (only visible when VERBOSE_LOGGING=true).</summary>
    public void Debug(string message)
    {
        if (Verbose)
            Console.WriteLine($"[{category}] DEBUG: {message}");
    }

    /// <summary>
    /// Logs a large block of text with a header. Truncates unless verbose is enabled.
    /// Always shows at least <paramref name="previewLength"/> characters.
    /// </summary>
    public void LogBlock(string header, string content, int previewLength = 500)
    {
        var separator = new string('─', Math.Min(header.Length + 4, 60));

        if (Verbose || content.Length <= previewLength)
        {
            Console.WriteLine($"[{category}] ┌{separator}┐");
            Console.WriteLine($"[{category}] │ {header}");
            Console.WriteLine($"[{category}] └{separator}┘");
            Console.WriteLine(content);
            Console.WriteLine($"[{category}] ─── end ({content.Length} chars) ───");
        }
        else
        {
            Console.WriteLine($"[{category}] {header} ({content.Length} chars, showing first {previewLength}):");
            Console.WriteLine(content[..previewLength]);
            Console.WriteLine($"[{category}] ... (truncated, set VERBOSE_LOGGING=true for full output)");
        }
    }
}
