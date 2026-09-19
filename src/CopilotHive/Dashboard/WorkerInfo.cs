namespace CopilotHive.Dashboard;

/// <summary>Worker state for the dashboard.</summary>
public sealed class WorkerInfo
{
    /// <summary>Worker identifier.</summary>
    public string Id { get; init; } = "";
    /// <summary>Current role name.</summary>
    public string Role { get; init; } = "";
    /// <summary>Whether the worker is busy.</summary>
    public bool IsBusy { get; init; }
    /// <summary>Current task ID, if any.</summary>
    public string? CurrentTaskId { get; init; }
    /// <summary>Last heartbeat timestamp.</summary>
    public DateTime LastHeartbeat { get; init; }
    /// <summary>Connection timestamp.</summary>
    public DateTime ConnectedAt { get; init; }
    /// <summary>Model used for the current task, or <c>null</c> when idle.</summary>
    public string? CurrentModel { get; init; }
    /// <summary>Estimated context window usage as a percentage (0–100), or 0 when idle.</summary>
    public int ContextUsagePercent { get; init; }

    /// <summary>
    /// Whether the worker was available in the captured snapshot: not busy, carrying no task, holding
    /// no completion publication and not awaiting its own accepted Ready.
    /// </summary>
    /// <remarks>
    /// An OBSERVATION, NOT A RESERVATION AND NOT A DELIVERY GUARANTEE: it describes the captured
    /// instant only and holds nothing for anyone.
    /// </remarks>
    public bool IsAvailable { get; init; }

    /// <summary>
    /// Whether the worker was awaiting its own accepted Ready in the captured snapshot — non-busy, but
    /// withheld from selection and therefore unavailable for assignment.
    /// </summary>
    /// <remarks>An OBSERVATION ONLY, carrying no reservation and no delivery guarantee.</remarks>
    public bool AwaitingWorkerReady { get; init; }

    /// <summary>
    /// THE PURE ROW-STATUS PRESENTATION HELPER: resolves the badge label and CSS class for one worker
    /// row from the three captured flags alone, so the page's status rendering is decided by a
    /// testable function with no UI dependency.
    /// </summary>
    /// <remarks>
    /// THE FOUR STATES IT DISTINGUISHES, in priority order:
    /// <list type="bullet">
    ///   <item><description><c>Busy</c> — currently executing a task (the busy flag wins outright, as
    ///     it did before).</description></item>
    ///   <item><description><c>Awaiting Ready</c> — non-busy but waiting for its own accepted Ready, so
    ///     it is NOT available capacity and is deliberately rendered as a distinct waiting state rather
    ///     than as green.</description></item>
    ///   <item><description><c>Available</c> — the captured availability verdict: eligible for a new
    ///     assignment at the captured instant.</description></item>
    ///   <item><description><c>Unavailable</c> — non-busy, not awaiting Ready and not available either
    ///     (for example still finishing a completion publication). Its own state, never green.</description></item>
    /// </list>
    /// A busy worker is reported as busy even when the other flags are inconsistent, so the label can
    /// never understate work in flight.
    /// </remarks>
    /// <param name="isBusy">Whether the worker was executing a task.</param>
    /// <param name="isAvailable">Whether the worker was available for a new assignment.</param>
    /// <param name="awaitingWorkerReady">Whether the worker was awaiting its own accepted Ready.</param>
    /// <returns>The badge label and CSS class for the row.</returns>
    public static (string Label, string CssClass) DescribeStatus(
        bool isBusy, bool isAvailable, bool awaitingWorkerReady)
    {
        if (isBusy)
            return ("Busy", "badge-yellow");

        if (awaitingWorkerReady)
            return ("Awaiting Ready", "badge-blue");

        return isAvailable
            ? ("Available", "badge-green")
            : ("Unavailable", "badge-muted");
    }

    /// <summary>
    /// The badge label for this worker's row, resolved by
    /// <see cref="DescribeStatus(bool, bool, bool)"/> from the captured flags.
    /// </summary>
    public string StatusLabel => DescribeStatus(IsBusy, IsAvailable, AwaitingWorkerReady).Label;

    /// <summary>
    /// The badge CSS class for this worker's row, resolved by
    /// <see cref="DescribeStatus(bool, bool, bool)"/> from the captured flags.
    /// </summary>
    public string StatusCssClass => DescribeStatus(IsBusy, IsAvailable, AwaitingWorkerReady).CssClass;

    /// <summary>
    /// Returns the model string to display for this worker: the task-specific
    /// <see cref="CurrentModel"/> if set, otherwise the role-default from
    /// <paramref name="roleModels"/>, or <c>null</c> if neither is available.
    /// </summary>
    /// <param name="roleModels">Role-to-model mapping from <see cref="OrchestratorInfo.RoleModels"/>.</param>
    /// <returns>The display model string, or <c>null</c>.</returns>
    public string? GetDisplayModel(IDictionary<string, string> roleModels)
    {
        return CurrentModel
            ?? (Role != "Unspecified" && roleModels.TryGetValue(Role.ToLowerInvariant(), out var m) ? m : null);
    }
}
