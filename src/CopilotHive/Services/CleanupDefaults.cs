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

    /// <summary>
    /// Default maximum wall-clock minutes a single worker task may run before it is
    /// reclaimed. Observed healthy phases complete well inside 10 minutes, so 60 leaves
    /// generous headroom while still bounding a hung task.
    /// </summary>
    public const int WorkerTaskTimeoutMinutes = 60;
}
