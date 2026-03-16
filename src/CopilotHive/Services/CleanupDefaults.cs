namespace CopilotHive.Services;

/// <summary>
/// Default configuration constants for the stale worker cleanup process.
/// </summary>
public static class CleanupDefaults
{
    /// <summary>
    /// The interval, in seconds, at which the cleanup service runs.
    /// </summary>
    public const int CleanupIntervalSeconds = 60;

    /// <summary>
    /// The timeout, in minutes, after which a worker is considered stale.
    /// </summary>
    public const int StaleTimeoutMinutes = 2;
}
