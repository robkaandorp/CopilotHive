using CopilotHive.Dashboard;
using CopilotHive.Goals;

namespace CopilotHive.Services;

/// <summary>
/// ONE DETACHED recent-goal row of the <c>Home</c> dashboard: everything the page's "Recent Goals"
/// table renders, and nothing else.
/// <para>
/// The creation timestamp is DELIBERATELY ABSENT: it is the SORT KEY that decides which twenty rows
/// (and in which order) the projection contains, not a value the table shows, so it never leaves the
/// authorized operation. The full goal — its description, notes, repositories, usage, failure reason
/// and every other mutable field — is never returned; the live <see cref="Goal"/> instance does not
/// cross the facade boundary at all.
/// </para>
/// </summary>
/// <param name="Id">The goal identifier (also the row's route segment).</param>
/// <param name="Status">
/// The merged snapshot's DISPLAY status — the phase-derived status for a pipeline-backed goal,
/// otherwise the stored/goal status. Only <see cref="GoalStatus.Draft"/>,
/// <see cref="GoalStatus.Completed"/> and <see cref="GoalStatus.Failed"/> rows are ever projected.
/// </param>
/// <param name="Iterations">The iteration count, or <c>null</c> for a goal that never finished (rendered as an em dash).</param>
public sealed record DashboardGoalDisplayEntry(string Id, GoalStatus Status, int? Iterations);

/// <summary>
/// ONE DETACHED active-pipeline row of the <c>Home</c> dashboard: the "Active Goals" table's cells,
/// including the three timestamps the page's elapsed-time formatting needs.
/// <para>
/// THE DESCRIPTION IS A PREVIEW, NOT A COPY OF THE TEXT: <see cref="DescriptionPreview"/> is the
/// description unchanged through <see cref="OperationalTextPreview.DescriptionPreviewLength"/>
/// characters and its first that-many characters followed by an ellipsis beyond that. The full
/// description and the omitted suffix are never returned, so a long goal description cannot leak
/// past its own row. This is a preview, not a general text sanitizer: it makes no claim about any
/// other string on any other surface.
/// </para>
/// </summary>
/// <param name="GoalId">The goal identifier (also the row's route segment).</param>
/// <param name="Phase">The current phase name.</param>
/// <param name="Iteration">The current iteration number.</param>
/// <param name="TotalTests">Total test count reported for the iteration.</param>
/// <param name="PassedTests">Passed test count.</param>
/// <param name="FailedTests">Failed test count.</param>
/// <param name="CoveragePercent">Code coverage percentage (0 when not measured).</param>
/// <param name="CreatedAt">When the pipeline was created — the elapsed-time FALLBACK when the goal never started.</param>
/// <param name="GoalStartedAt">When the goal started executing, or <c>null</c> while still planning.</param>
/// <param name="CompletedAt">When the pipeline completed or failed, or <c>null</c> while still active.</param>
/// <param name="DescriptionPreview">The 80-character description preview (see the type remarks).</param>
public sealed record DashboardPipelineDisplayEntry(
    string GoalId,
    string Phase,
    int Iteration,
    int TotalTests,
    int PassedTests,
    int FailedTests,
    double CoveragePercent,
    DateTime CreatedAt,
    DateTime? GoalStartedAt,
    DateTime? CompletedAt,
    string DescriptionPreview);

/// <summary>
/// THE DETACHED DASHBOARD DISPLAY PROJECTION produced by
/// <see cref="IOperationalFacade.GetDashboardDisplayAsync"/>: the five full-set goal counts, the two
/// worker counts the dashboard cards show, the newest bounded set of recent-goal rows and the active
/// pipeline rows in their existing order.
/// <para>
/// IT CONTAINS NO WORKER ROWS AT ALL. The dashboard renders only "busy / total" for workers, so the
/// per-worker rows the merged snapshot carries are dropped here instead of being exposed on a surface
/// that has no use for them; the Workers surface has its own projection
/// (<see cref="IOperationalFacade.GetWorkerDisplayAsync"/>).
/// </para>
/// </summary>
/// <param name="DraftGoals">Full-set count of draft goals.</param>
/// <param name="PendingGoals">Full-set count of pending goals.</param>
/// <param name="ActiveGoals">Full-set count of in-progress goals.</param>
/// <param name="CompletedGoals">Full-set count of completed goals.</param>
/// <param name="FailedGoals">Full-set count of failed goals.</param>
/// <param name="BusyWorkers">Workers executing a task at the captured instant.</param>
/// <param name="TotalWorkers">Registered workers at the captured instant.</param>
/// <param name="RecentGoals">
/// The NEWEST <see cref="RecentGoalLimit"/> Draft/Completed/Failed goals, ordered by creation time
/// descending (which is why the sort key itself is not projected).
/// </param>
/// <param name="Pipelines">The active pipeline rows, in the merged snapshot's existing order.</param>
public sealed record DashboardDisplaySnapshot(
    int DraftGoals,
    int PendingGoals,
    int ActiveGoals,
    int CompletedGoals,
    int FailedGoals,
    int BusyWorkers,
    int TotalWorkers,
    IReadOnlyList<DashboardGoalDisplayEntry> RecentGoals,
    IReadOnlyList<DashboardPipelineDisplayEntry> Pipelines)
{
    /// <summary>
    /// THE RECENT-GOAL CAP: the number of newest Draft/Completed/Failed goals the dashboard's
    /// "Recent Goals" table shows. Applying the cap HERE (rather than leaving it to the page) is what
    /// bounds the projection.
    /// </summary>
    public const int RecentGoalLimit = 20;
}

/// <summary>
/// ONE DETACHED worker row of the <c>Workers</c> and <c>WorkerDetail</c> surfaces: the captured
/// flags, the display facts, and the COMPUTED presentation values those pages render.
/// <para>
/// DETACHED BY CONSTRUCTION: every member is a copied value from ONE pool-owned capture plus
/// display values computed from a COPY of the role configuration. No live
/// <c>ConnectedWorker</c>, no live configuration object and no mutable collection is retained or
/// exposed, so neither a later pool mutation nor a later configuration edit is observable through a
/// row already handed out — and a caller cannot mutate anything the pool or the configuration owns.
/// </para>
/// <para>
/// THE STATUS SEMANTICS ARE THE EXISTING PURE HELPER'S: <see cref="StatusLabel"/> /
/// <see cref="StatusCssClass"/> are resolved by <see cref="WorkerInfo.DescribeStatus(bool, bool, bool)"/>, so Busy /
/// Awaiting Ready / Available / Unavailable stay distinguishable and a non-busy worker is never
/// presented as available capacity unless the capture really was available.
/// </para>
/// </summary>
/// <param name="Id">The worker identifier.</param>
/// <param name="Role">The role NAME at the captured instant (<c>"Unspecified"</c> when unassigned).</param>
/// <param name="IsBusy">Whether the worker was executing a task.</param>
/// <param name="CurrentTaskId">The task the worker was executing, or <c>null</c> when idle.</param>
/// <param name="LastHeartbeat">UTC timestamp of the last heartbeat.</param>
/// <param name="ConnectedAt">UTC timestamp when the worker connected.</param>
/// <param name="CurrentModel">The task-specific model, or <c>null</c> when the role default applied.</param>
/// <param name="ContextUsagePercent">Context window usage (0–100), 0 when idle.</param>
/// <param name="IsAvailable">The captured availability verdict (an OBSERVATION, not a reservation).</param>
/// <param name="AwaitingWorkerReady">Whether the worker was awaiting its own accepted Ready.</param>
/// <param name="DisplayModel">
/// The model this row DISPLAYS: the task-specific <see cref="CurrentModel"/> when set, otherwise the
/// role's configured model (including the display-only "(not configured)" sentinel), otherwise
/// <c>null</c> for an unassigned/unknown role.
/// </param>
/// <param name="ReasoningLabel">
/// The reasoning-effort label matching <see cref="DisplayModel"/>: the role's PREMIUM reasoning when
/// the displayed model is its premium model (matched first), the role's STANDARD reasoning when it is
/// its standard model, and the existing unmatched/unset em dash otherwise.
/// </param>
/// <param name="StatusLabel">The badge label resolved from the captured flags.</param>
/// <param name="StatusCssClass">The badge CSS class resolved from the captured flags.</param>
public sealed record WorkerDisplayRow(
    string Id,
    string Role,
    bool IsBusy,
    string? CurrentTaskId,
    DateTime LastHeartbeat,
    DateTime ConnectedAt,
    string? CurrentModel,
    int ContextUsagePercent,
    bool IsAvailable,
    bool AwaitingWorkerReady,
    string? DisplayModel,
    string ReasoningLabel,
    string StatusLabel,
    string StatusCssClass);

/// <summary>
/// THE DETACHED WORKER DISPLAY PROJECTION produced by
/// <see cref="IOperationalFacade.GetWorkerDisplayAsync"/>: every worker row PLUS all five counts, all
/// derived from ONE <c>WorkerPool.CaptureWorkerStatus</c> call, so no count can disagree with the rows
/// beside it and the counts can never describe two different instants.
/// <para>
/// THE FIVE COUNTS KEEP THEIR OWN MEANINGS: <see cref="TotalWorkers"/> is every registered worker,
/// <see cref="BusyWorkers"/> those executing a task, <see cref="IdleWorkers"/> every NON-BUSY worker
/// (WITHHELD workers included, so it is not a capacity figure), <see cref="AvailableWorkers"/> the
/// captured assignable verdict and <see cref="AwaitingReadyWorkers"/> the readiness-wait subset.
/// </para>
/// </summary>
/// <param name="TotalWorkers">Registered workers at the captured instant.</param>
/// <param name="BusyWorkers">Workers executing a task at the captured instant.</param>
/// <param name="IdleWorkers">Workers NOT executing a task (includes withheld workers).</param>
/// <param name="AvailableWorkers">Workers the checked pool claim could take at the captured instant.</param>
/// <param name="AwaitingReadyWorkers">Workers awaiting their own accepted Ready.</param>
/// <param name="Workers">The detached rows, in the capture's order.</param>
public sealed record WorkerDisplayProjection(
    int TotalWorkers,
    int BusyWorkers,
    int IdleWorkers,
    int AvailableWorkers,
    int AwaitingReadyWorkers,
    IReadOnlyList<WorkerDisplayRow> Workers);

/// <summary>
/// ONE DETACHED progress-report row of the <c>WorkerDetail</c> surface, carrying exactly the four
/// columns that page renders.
/// <para>
/// THE DISCLOSURE OF <see cref="Status"/> AND <see cref="Details"/> DEPENDS ON THE INSTALLATION MODE,
/// NOT ON THE CALLER: an authentication-enabled installation projects only the seven approved
/// statuses, spelled canonically, with <see cref="Details"/> always <c>null</c> (a worker's free-text
/// details is not shared-read material). Only an EXPLICITLY open (authentication-disabled)
/// installation — where the single local operator already sees everything — keeps the worker's raw
/// status text and details. Both modes still run the authorization gate first, and a null identity in
/// authenticated mode is REFUSED rather than selecting the raw branch.
/// </para>
/// </summary>
/// <param name="Timestamp">When the report was received.</param>
/// <param name="GoalId">The goal the worker was working on.</param>
/// <param name="Status">
/// In authenticated mode one of <see cref="ApprovedStatuses"/> in its canonical spelling; in open mode
/// the worker's own status text.
/// </param>
/// <param name="Details">The worker's details text in open mode; always <c>null</c> in authenticated mode.</param>
public sealed record WorkerProgressDisplayEntry(
    DateTime Timestamp,
    string GoalId,
    string Status,
    string? Details)
{
    /// <summary>
    /// THE SEVEN APPROVED PROGRESS STATUSES, in their canonical (title case) spellings. This is the
    /// ONLY set an authentication-enabled installation projects; the match is a full-value ORDINAL,
    /// CASE-INSENSITIVE comparison with NO trimming, so a near miss — different wording, a prefix, a
    /// suffix or surrounding whitespace — is not approved.
    /// </summary>
    public static readonly IReadOnlyList<string> ApprovedStatuses =
    [
        "Reading Files",
        "Building",
        "Testing",
        "Tests Passing",
        "Committing",
        "Completed",
        "Failed",
    ];

    /// <summary>
    /// Resolves the CANONICAL spelling of an approved progress status.
    /// </summary>
    /// <remarks>
    /// The comparison is a full-value, ordinal, case-insensitive equality test against
    /// <see cref="ApprovedStatuses"/> — the same values the workers actually report — and the input is
    /// used as-is: no trimming, no prefix/suffix matching and no substring search. A status that is
    /// not exactly one of the seven approved values (ignoring case only) has no canonical spelling and
    /// answers <c>null</c>, which is how an unknown row is OMITTED rather than rewritten.
    /// </remarks>
    /// <param name="status">The raw status text, or <c>null</c>.</param>
    /// <returns>The canonical approved spelling, or <c>null</c> when <paramref name="status"/> is not approved.</returns>
    public static string? CanonicalStatus(string? status)
    {
        if (status is null)
            return null;

        foreach (var approved in ApprovedStatuses)
        {
            if (string.Equals(status, approved, StringComparison.OrdinalIgnoreCase))
                return approved;
        }

        return null;
    }
}

/// <summary>
/// THE ONE bounded-text preview the operational display projections use.
/// <para>
/// IT IS A PREVIEW, NOT A SANITIZER: it shortens a long description for a table cell and says nothing
/// about any other string, any other surface, or about untrusted content. The text is returned
/// UNCHANGED up to and including the limit; beyond it, only the first
/// <see cref="DescriptionPreviewLength"/> characters plus an ellipsis are returned, so the omitted
/// suffix never leaves the authorized operation.
/// </para>
/// </summary>
public static class OperationalTextPreview
{
    /// <summary>
    /// The number of description characters a preview keeps before it appends an ellipsis —
    /// exactly what the dashboard's row rendering showed before this projection existed.
    /// </summary>
    public const int DescriptionPreviewLength = 80;

    /// <summary>The ellipsis appended when a description is longer than the preview length.</summary>
    public const string Ellipsis = "…";

    /// <summary>
    /// Builds the description preview: unchanged through
    /// <see cref="DescriptionPreviewLength"/> characters, otherwise the first that-many characters
    /// followed by <see cref="Ellipsis"/>.
    /// </summary>
    /// <param name="text">The description to preview (never retained).</param>
    /// <returns>The preview text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static string Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Length <= DescriptionPreviewLength
            ? text
            : text[..DescriptionPreviewLength] + Ellipsis;
    }
}
