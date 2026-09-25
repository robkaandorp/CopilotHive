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

    /// <summary>
    /// The grace period, in minutes, measured from the cleanup service's construction (the
    /// orchestrator start), during which a restored HELD attempt
    /// (<see cref="GoalPipeline.IsRestoredActiveAttemptHold"/>) waits for its worker to re-register
    /// and adopt it. Once the grace has elapsed, the cleanup sweep releases every still-held attempt
    /// to the ordinary reclaim (slot retired, pointer cleared, mapping unregistered, goal queued
    /// for re-dispatch). This is a POLICY timeout, not proof that the worker is gone: a worker that
    /// re-registers after the release loses the attempt — its adoption is refused and the goal
    /// proceeds through the ordinary re-dispatch. This single value governs the sweep.
    /// </summary>
    public const int HeldAttemptAdoptionGraceMinutes = 5;
}
