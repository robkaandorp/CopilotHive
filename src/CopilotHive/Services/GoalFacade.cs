using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;

namespace CopilotHive.Services;

/// <summary>
/// Facade over the goal operations the Goals and Goal Detail pages perform: deleting a goal,
/// changing its status, requesting a pre-execution review, cancelling it, extending its
/// iteration budget, attaching it to a release, and listing the issues linked to it. Endpoint
/// handlers (and, in a follow-up step, the Blazor components) depend on this interface instead
/// of reaching into the stores/dispatcher directly, so each operation's validation order and
/// side effects live in exactly one place and run exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Scope: ONLY the six goal routes those two pages consume are covered
/// (<c>DELETE /api/goals/{id}</c>, <c>PATCH /api/goals/{id}/status</c>,
/// <c>POST /api/goals/{goalId}/review</c>, <c>POST /api/goals/{id}/cancel</c>,
/// <c>POST /api/goals/{id}/extend-iterations</c>, <c>PATCH /api/goals/{id}/release</c>) plus
/// <see cref="GetLinkedIssuesAsync"/>, which is a component-only convenience with NO
/// corresponding goal route — the issues API is untouched. There is deliberately no detach
/// operation: no route detaches a goal from a release.
/// </para>
/// <para>
/// Exception semantics mirror the pre-facade handlers EXACTLY: an operation catches ONLY what
/// its handler caught (<see cref="UpdateGoalStatusAsync"/> catches
/// <see cref="KeyNotFoundException"/> and <see cref="ArgumentException"/>;
/// <see cref="RequestReviewAsync"/> catches <see cref="InvalidOperationException"/> from
/// <see cref="GoalReviewService"/>; <see cref="DeleteGoalAsync"/> catches everything the
/// knowledge-document cleanup throws and logs it). Anything else is RETHROWN.
/// </para>
/// <para>
/// Cancellation is preserved per operation, NOT normalised: only
/// <see cref="RequestReviewAsync"/>, <see cref="ExtendIterationsAsync"/> and
/// <see cref="GetLinkedIssuesAsync"/> took a token before the facade existed, so only they
/// accept one. <see cref="DeleteGoalAsync"/>, <see cref="UpdateGoalStatusAsync"/>,
/// <see cref="CancelGoalAsync"/> and <see cref="AttachReleaseAsync"/> take none — adding one
/// would be a behaviour change.
/// </para>
/// </remarks>
public interface IGoalFacade
{
    /// <summary>
    /// Deletes a Draft or Failed goal, then best-effort cleans up its knowledge documents and
    /// (for Failed goals) its remote feature branches, and notifies the dashboard.
    /// </summary>
    /// <param name="id">The goal ID to delete.</param>
    /// <returns>
    /// A valueless success result (the route answers 204 No Content with no body),
    /// <see cref="FacadeErrorKind.NotFound"/> when the goal does not exist (or vanished before
    /// the delete), or <see cref="FacadeErrorKind.BadRequest"/> when the goal is neither Draft
    /// nor Failed.
    /// </returns>
    /// <remarks>
    /// Takes no cancellation token: the pre-facade handler accepted none, and the
    /// knowledge-document cleanup keeps its explicit <see cref="CancellationToken.None"/>.
    /// </remarks>
    Task<FacadeResult> DeleteGoalAsync(string id);

    /// <summary>
    /// Applies a status transition to a goal. Only Draft→Pending, Pending→Draft and
    /// Failed→Draft are permitted; Failed→Draft additionally resets iteration data, clears the
    /// dispatcher's retry state, and best-effort deletes the goal's remote feature branches.
    /// </summary>
    /// <param name="id">The goal ID to update.</param>
    /// <param name="status">The requested status, parsed case-insensitively from its name.</param>
    /// <returns>
    /// The full goal after the update, <see cref="FacadeErrorKind.NotFound"/> when the goal does
    /// not exist, or <see cref="FacadeErrorKind.BadRequest"/> for an unparsable status or a
    /// disallowed transition.
    /// </returns>
    Task<FacadeResult<GoalDto>> UpdateGoalStatusAsync(string id, string status);

    /// <summary>
    /// Runs a pre-execution review for a Draft goal.
    /// </summary>
    /// <param name="goalId">The goal ID to review.</param>
    /// <param name="ct">Cancellation token propagated to the store and the review service.</param>
    /// <returns>
    /// The review verdict, <see cref="FacadeErrorKind.NotFound"/> when the goal does not exist,
    /// <see cref="FacadeErrorKind.BadRequest"/> when it is not a Draft (checked FIRST), or
    /// <see cref="FacadeErrorKind.Conflict"/> when a review is already in progress — either
    /// because the goal's review status is already Pending or because
    /// <see cref="GoalReviewService"/> reports a concurrent review.
    /// </returns>
    Task<FacadeResult<ReviewResultDto>> RequestReviewAsync(string goalId, CancellationToken ct);

    /// <summary>
    /// Cancels an InProgress or Pending goal.
    /// </summary>
    /// <param name="id">The goal ID to cancel.</param>
    /// <returns>
    /// The confirmation message, <see cref="FacadeErrorKind.NotFound"/> when the goal does not
    /// exist, or <see cref="FacadeErrorKind.BadRequest"/> when the goal has ANY other status
    /// (Draft and the terminal statuses included) or the dispatcher could not cancel it.
    /// </returns>
    /// <remarks>Takes no cancellation token: the pre-facade handler accepted none.</remarks>
    Task<FacadeResult<CancelledResult>> CancelGoalAsync(string id);

    /// <summary>
    /// Extends a goal's iteration budget and resumes it.
    /// </summary>
    /// <param name="id">The goal ID to extend.</param>
    /// <param name="additionalIterations">Number of additional iterations to grant (1–100).</param>
    /// <param name="ct">Cancellation token propagated to the dispatcher.</param>
    /// <returns>
    /// The confirmation message, <see cref="FacadeErrorKind.ServiceUnavailable"/> when no
    /// dispatcher is registered (checked FIRST, before the range check),
    /// <see cref="FacadeErrorKind.BadRequest"/> when <paramref name="additionalIterations"/> is
    /// outside 1–100, or <see cref="FacadeErrorKind.NotFound"/> when the goal or its pipeline
    /// is not resumable.
    /// </returns>
    Task<FacadeResult<ExtendedResult>> ExtendIterationsAsync(string id, int additionalIterations, CancellationToken ct);

    /// <summary>
    /// Attaches a goal to an existing release.
    /// </summary>
    /// <param name="id">The goal ID to attach.</param>
    /// <param name="releaseId">The release to attach the goal to.</param>
    /// <returns>
    /// The full goal after the update, or <see cref="FacadeErrorKind.NotFound"/> when the
    /// release (checked FIRST) or the goal does not exist.
    /// </returns>
    /// <remarks>Takes no cancellation token: the pre-facade handler accepted none.</remarks>
    Task<FacadeResult<GoalDto>> AttachReleaseAsync(string id, string releaseId);

    /// <summary>
    /// Lists the issues that reference a goal, either as their source goal or as their linked
    /// goal. Performs BOTH issue-store queries (the same two the Goal Detail page issued as two
    /// <c>GET /api/issues</c> calls), concatenates them and deduplicates by issue ID.
    /// </summary>
    /// <param name="goalId">The goal ID to look up.</param>
    /// <param name="ct">Cancellation token propagated to both queries.</param>
    /// <returns>
    /// Always a success result carrying the deduplicated issues (source-goal matches first, in
    /// store order); an empty list when nothing references the goal.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<LinkedIssueDto>>> GetLinkedIssuesAsync(string goalId, CancellationToken ct);
}

/// <summary>
/// Default implementation of <see cref="IGoalFacade"/>, delegating to the goal/issue stores,
/// the <see cref="GoalDispatcher"/>, the <see cref="GoalReviewService"/> and the notifiers.
/// </summary>
/// <remarks>
/// Optional dependencies mirror the endpoints exactly: <see cref="IBrainRepoManager"/>,
/// <see cref="KnowledgeDocumentCleanupService"/>, <see cref="GoalReadyNotifier"/> and
/// <see cref="GoalDispatcher"/> may be <c>null</c> (the endpoints resolved them with
/// <c>[FromServices]</c> nullable parameters), while the goal store, the issue store, the
/// review service and the dashboard notifier are required.
/// </remarks>
public sealed class GoalFacade : IGoalFacade
{
    private readonly IGoalStore _goalStore;
    private readonly IIssueStore _issueStore;
    private readonly GoalReviewService _reviewService;
    private readonly DashboardNotifier _dashboardNotifier;
    private readonly IBrainRepoManager? _repoManager;
    private readonly KnowledgeDocumentCleanupService? _docCleanup;
    private readonly GoalReadyNotifier? _goalReadyNotifier;
    private readonly GoalDispatcher? _dispatcher;
    private readonly ILogger<GoalFacade> _log;

    /// <summary>
    /// Initialises a new <see cref="GoalFacade"/>.
    /// </summary>
    /// <param name="goalStore">The goal store (required).</param>
    /// <param name="issueStore">The issue store (required) used by <see cref="GetLinkedIssuesAsync"/>.</param>
    /// <param name="reviewService">The pre-execution review service (required).</param>
    /// <param name="dashboardNotifier">The dashboard change notifier (required).</param>
    /// <param name="repoManager">Brain repo manager for feature-branch cleanup, or <c>null</c>.</param>
    /// <param name="docCleanup">Knowledge-document cleanup service, or <c>null</c>.</param>
    /// <param name="goalReadyNotifier">Dispatcher wake-up notifier, or <c>null</c>.</param>
    /// <param name="dispatcher">The goal dispatcher, or <c>null</c> when not registered.</param>
    /// <param name="log">Logger instance.</param>
    public GoalFacade(
        IGoalStore goalStore,
        IIssueStore issueStore,
        GoalReviewService reviewService,
        DashboardNotifier dashboardNotifier,
        IBrainRepoManager? repoManager,
        KnowledgeDocumentCleanupService? docCleanup,
        GoalReadyNotifier? goalReadyNotifier,
        GoalDispatcher? dispatcher,
        ILogger<GoalFacade> log)
    {
        _goalStore = goalStore;
        _issueStore = issueStore;
        _reviewService = reviewService;
        _dashboardNotifier = dashboardNotifier;
        _repoManager = repoManager;
        _docCleanup = docCleanup;
        _goalReadyNotifier = goalReadyNotifier;
        _dispatcher = dispatcher;
        _log = log;
    }

    /// <inheritdoc />
    public async Task<FacadeResult> DeleteGoalAsync(string id)
    {
        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return new(false, $"Goal '{id}' not found.", FacadeErrorKind.NotFound);

        if (goal.Status is not (GoalStatus.Draft or GoalStatus.Failed))
            return new(false, "Only Draft or Failed goals can be deleted", FacadeErrorKind.BadRequest);

        var deleted = await _goalStore.DeleteGoalAsync(id);
        if (!deleted)
            return new(false, $"Goal '{id}' not found.", FacadeErrorKind.NotFound);

        // Best-effort cleanup of knowledge documents. The token is deliberately
        // CancellationToken.None: the delete is already committed, so the cleanup must not be
        // abandoned because the caller went away.
        if (_docCleanup is not null)
        {
            try
            {
                await _docCleanup.CleanupGoalDocumentsAsync(id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to cleanup knowledge documents for deleted goal '{GoalId}'", id);
            }
        }

        // Best-effort cleanup of remote feature branches for Failed goals
        if (goal.Status == GoalStatus.Failed)
        {
            await DeleteFeatureBranchesAsync(id, goal.RepositoryNames);
        }

        _dashboardNotifier.NotifyStateChanged();
        return new(true, null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<GoalDto>> UpdateGoalStatusAsync(string id, string status)
    {
        try
        {
            var parsed = Enum.Parse<GoalStatus>(status, ignoreCase: true);

            var existing = await _goalStore.GetGoalAsync(id);
            if (existing is null)
                return NotFoundGoal<GoalDto>(id);

            // Allowed transitions via the public API:
            //   Draft ↔ Pending (approve / revert)
            //   Failed → Draft (retry — resets iteration data and cleans up feature branch)
            var validTransition =
                existing.Status == GoalStatus.Draft && parsed == GoalStatus.Pending ||
                existing.Status == GoalStatus.Pending && parsed == GoalStatus.Draft ||
                existing.Status == GoalStatus.Failed && parsed == GoalStatus.Draft;
            if (!validTransition)
            {
                return new(
                    false,
                    null,
                    $"Invalid transition from {existing.Status} to {parsed}. Only Draft→Pending, Pending→Draft, and Failed→Draft are allowed.",
                    FacadeErrorKind.BadRequest);
            }

            // Failed→Draft: reset iteration data and delete the feature branch (best-effort)
            if (existing.Status == GoalStatus.Failed && parsed == GoalStatus.Draft)
            {
                await _goalStore.ResetGoalIterationDataAsync(id);

                // Clear GoalDispatcher runtime state so the goal can be re-dispatched fresh
                _dispatcher?.ClearGoalRetryState(id);

                await DeleteFeatureBranchesAsync(id, existing.RepositoryNames);
            }

            await _goalStore.UpdateGoalStatusAsync(id, parsed);
            var goal = await _goalStore.GetGoalAsync(id);
            _dashboardNotifier.NotifyStateChanged();
            if (parsed == GoalStatus.Pending) _goalReadyNotifier?.NotifyGoalReady();
            return new(true, goal is null ? null : GoalDto.From(goal), null, FacadeErrorKind.None);
        }
        catch (KeyNotFoundException)
        {
            return NotFoundGoal<GoalDto>(id);
        }
        catch (ArgumentException)
        {
            return new(false, null, $"Invalid status '{status}'.", FacadeErrorKind.BadRequest);
        }
    }

    /// <inheritdoc />
    public async Task<FacadeResult<ReviewResultDto>> RequestReviewAsync(string goalId, CancellationToken ct)
    {
        var goal = await _goalStore.GetGoalAsync(goalId, ct);
        if (goal is null)
            return NotFoundGoal<ReviewResultDto>(goalId);

        // Frozen order: the non-Draft check runs BEFORE the review-in-progress check, so a
        // non-Draft goal reports 400 even when its review status is Pending.
        if (goal.Status != GoalStatus.Draft)
            return new(false, null, "Only Draft goals can be reviewed.", FacadeErrorKind.BadRequest);

        if (goal.ReviewStatus == ReviewStatus.Pending)
            return new(false, null, "A review is already in progress for this goal.", FacadeErrorKind.Conflict);

        try
        {
            var result = await _reviewService.ReviewGoalAsync(goal, ct);
            return new(true, new ReviewResultDto(result.Verdict, result.Issues, result.Summary), null, FacadeErrorKind.None);
        }
        catch (InvalidOperationException ex)
        {
            // The review service's own concurrency guard — a review started between the check
            // above and the call. Reported as a conflict, exactly as before.
            return new(false, null, ex.Message, FacadeErrorKind.Conflict);
        }
    }

    /// <inheritdoc />
    public async Task<FacadeResult<CancelledResult>> CancelGoalAsync(string id)
    {
        // The pre-facade route bound GoalDispatcher as a REQUIRED service, so an absent
        // dispatcher failed the request with an exception (HTTP 500). Fail the same way here
        // instead of inventing a degraded result.
        if (_dispatcher is null)
            throw new InvalidOperationException("GoalDispatcher is not registered; goals cannot be cancelled.");

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return NotFoundGoal<CancelledResult>(id);

        // EVERY status other than InProgress/Pending is rejected — Draft and the terminal
        // statuses included.
        if (goal.Status is not (GoalStatus.InProgress or GoalStatus.Pending))
        {
            return new(
                false,
                null,
                $"Goal '{id}' is {goal.Status} and cannot be cancelled. Only InProgress or Pending goals can be cancelled.",
                FacadeErrorKind.BadRequest);
        }

        var cancelled = await _dispatcher.CancelGoalAsync(id);
        return cancelled
            ? new(true, new CancelledResult($"Goal '{id}' has been cancelled."), null, FacadeErrorKind.None)
            : new(
                false,
                null,
                $"Goal '{id}' could not be cancelled (it may have already completed or failed).",
                FacadeErrorKind.BadRequest);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<ExtendedResult>> ExtendIterationsAsync(string id, int additionalIterations, CancellationToken ct)
    {
        // Frozen order: dispatcher availability FIRST, so an invalid iteration count with an
        // absent dispatcher still reports ServiceUnavailable rather than BadRequest.
        if (_dispatcher is null)
            return new(false, null, "GoalDispatcher is not available.", FacadeErrorKind.ServiceUnavailable);

        if (additionalIterations <= 0 || additionalIterations > 100)
            return new(false, null, "additionalIterations must be between 1 and 100.", FacadeErrorKind.BadRequest);

        var success = await _dispatcher.ResumeGoalAsync(id, additionalIterations, ct);
        if (!success)
            return new(false, null, $"Goal '{id}' or its pipeline not found.", FacadeErrorKind.NotFound);

        return new(
            true,
            new ExtendedResult($"Extended iteration budget by {additionalIterations}."),
            null,
            FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<GoalDto>> AttachReleaseAsync(string id, string releaseId)
    {
        var release = await _goalStore.GetReleaseAsync(releaseId);
        if (release is null)
            return new(false, null, $"Release '{releaseId}' not found.", FacadeErrorKind.NotFound);

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return NotFoundGoal<GoalDto>(id);

        goal.ReleaseId = releaseId;
        await _goalStore.UpdateGoalAsync(goal);
        _dashboardNotifier.NotifyStateChanged();
        return new(true, GoalDto.From(goal), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<LinkedIssueDto>>> GetLinkedIssuesAsync(string goalId, CancellationToken ct)
    {
        // The two queries mirror the two GET /api/issues calls the Goal Detail page issued:
        // one filtered by source_goal_id, one by linked_goal_id.
        var sourceIssues = await _issueStore.GetIssuesAsync(sourceGoalId: goalId, ct: ct);
        var linkedIssues = await _issueStore.GetIssuesAsync(linkedGoalId: goalId, ct: ct);

        var merged = sourceIssues
            .Concat(linkedIssues)
            .DistinctBy(i => i.Id)
            .Select(LinkedIssueDto.From)
            .ToList();

        return new(true, merged, null, FacadeErrorKind.None);
    }

    /// <summary>
    /// Best-effort deletion of the goal's remote feature branches in each of its repositories.
    /// A missing repo manager is a no-op, exactly as in the pre-facade handlers.
    /// </summary>
    /// <param name="goalId">The goal whose feature branches to delete.</param>
    /// <param name="repositoryNames">The repositories to delete the branch from.</param>
    private async Task DeleteFeatureBranchesAsync(string goalId, IEnumerable<string> repositoryNames)
    {
        if (_repoManager is null)
            return;

        var branchName = $"copilothive/{goalId}";
        foreach (var repoName in repositoryNames)
        {
            _ = await _repoManager.DeleteRemoteBranchAsync(repoName, branchName);
        }
    }

    /// <summary>Builds the shared "goal not found" failure result.</summary>
    /// <typeparam name="T">The facade value type of the operation.</typeparam>
    /// <param name="id">The goal ID that was not found.</param>
    /// <returns>A <see cref="FacadeErrorKind.NotFound"/> result carrying the standard message.</returns>
    private static FacadeResult<T> NotFoundGoal<T>(string id)
        => new(false, default, $"Goal '{id}' not found.", FacadeErrorKind.NotFound);
}
