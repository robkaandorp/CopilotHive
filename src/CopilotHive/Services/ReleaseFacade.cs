using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;

using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Facade over the release surface used by the releases REST API (and, in a follow-up step,
/// the <c>Releases</c> / <c>ReleaseDetail</c> components): creating a release, changing its
/// status, updating notes/tag/repositories, deleting it, and validating it. Endpoint handlers
/// depend on this interface instead of reaching into <see cref="IGoalStore"/> and the
/// <see cref="ReleaseExecutionService"/> directly, so the validation order, the status
/// orchestration side effects and the failure mapping live in exactly one place and run
/// exactly once.
/// </summary>
/// <remarks>
/// <para>
/// The simple operations (<see cref="CreateReleaseAsync"/>, <see cref="UpdateReleaseNotesAsync"/>,
/// <see cref="UpdateReleaseTagAsync"/>, <see cref="UpdateReleaseRepositoriesAsync"/>,
/// <see cref="DeleteReleaseAsync"/>, <see cref="ValidateReleaseAsync"/>) return a
/// <see cref="FacadeResult{T}"/> whose <see cref="FacadeErrorKind"/> mirrors the HTTP status the
/// endpoint would have returned before the facade existed. <see cref="UpdateReleaseStatusAsync"/>
/// instead returns the discriminated <see cref="ReleaseStatusOutcome"/> — the outcome record IS
/// the complete result (success and failure alike), avoiding a success-only-Value wrapper.
/// </para>
/// <para>
/// Exception semantics mirror the pre-facade handlers EXACTLY: an operation catches ONLY what
/// its handler caught (<see cref="CreateReleaseAsync"/> catches <see cref="InvalidOperationException"/>
/// from the store; <see cref="UpdateReleaseTagAsync"/> / <see cref="UpdateReleaseRepositoriesAsync"/>
/// catch <see cref="KeyNotFoundException"/> and <see cref="InvalidOperationException"/>). Anything
/// else is RETHROWN so unexpected failures surface as exceptions (ASP.NET → 500) instead of being
/// silently converted into a result.
/// </para>
/// <para>
/// Cancellation is preserved per operation, NOT normalised: only the status operation's
/// <see cref="ReleaseExecutionService.ExecuteReleaseAsync"/> call and the knowledge-document
/// cleanup receive the caller's token (as the pre-facade handler passed
/// <c>HttpContext.RequestAborted</c> to exactly those two calls); the initial
/// <see cref="IGoalStore.GetReleaseAsync"/>, the success re-read and
/// <see cref="IGoalStore.UpdateReleaseAsync(Release, CancellationToken)"/> keep default tokens.
/// <see cref="DeleteReleaseAsync"/> and <see cref="ValidateReleaseAsync"/> propagate the token to
/// the store, exactly as their handlers did.
/// </para>
/// </remarks>
public interface IReleaseFacade
{
    /// <summary>
    /// Applies a status transition to a release. Validation order is frozen: release-not-found →
    /// blank status → numeric status → unknown status → comma-combined status →
    /// <c>Released</c>→<c>Released</c> conflict → <c>Released</c>→<c>Planning</c> revert conflict →
    /// <c>Planning</c>→<c>Planning</c> no-op → execution.
    /// </summary>
    /// <param name="id">The release ID to update.</param>
    /// <param name="request">The requested status.</param>
    /// <param name="ct">
    /// Cancellation token forwarded ONLY to <see cref="ReleaseExecutionService.ExecuteReleaseAsync"/>
    /// and the knowledge-document cleanup — the exact two calls the pre-facade handler passed
    /// <c>HttpContext.RequestAborted</c> to. The other store calls keep default tokens.
    /// </param>
    /// <returns>
    /// <see cref="PlanningNoOpOutcome"/> for a Planning→Planning no-op (bare Release JSON),
    /// <see cref="ExecutionSuccessOutcome"/> for a successful Planning→Released execution
    /// (<c>{release, result}</c>), or <see cref="StatusFailureOutcome"/> with the exact
    /// per-variant kind (404 not-found, 400 status validation / <c>{errors:[...]}</c> validation,
    /// 409 conflicts, 500 execution failure with <c>{detail, results}</c>, 503 missing execution
    /// service).
    /// </returns>
    Task<ReleaseStatusOutcome> UpdateReleaseStatusAsync(string id, UpdateReleaseStatusRequest request, CancellationToken ct);

    /// <summary>
    /// Creates a new release. The version-required check runs FIRST; a duplicate release ID/version
    /// surfaces the store's <see cref="InvalidOperationException"/> (the store converts a
    /// duplicate-ID <c>DbUpdateException</c> into <see cref="InvalidOperationException"/>) and maps
    /// to <see cref="FacadeErrorKind.Conflict"/>. Tag uniqueness is NOT enforced.
    /// </summary>
    /// <param name="request">The release to create.</param>
    /// <returns>
    /// The created release as a <see cref="ReleaseDto"/>, <see cref="FacadeErrorKind.BadRequest"/>
    /// when the version is blank, or <see cref="FacadeErrorKind.Conflict"/> when a release with the
    /// same ID already exists. Any other exception propagates to the caller.
    /// </returns>
    Task<FacadeResult<ReleaseDto>> CreateReleaseAsync(CreateReleaseRequest request);

    /// <summary>
    /// Replaces a release's notes. <paramref name="request"/>.Notes is <c>string?</c> — a null
    /// value CLEARS the notes (preserved from the pre-facade handler, which assigned it directly).
    /// </summary>
    /// <param name="id">The release ID to update.</param>
    /// <param name="request">The new notes (null clears).</param>
    /// <returns>
    /// The updated release as a <see cref="ReleaseDto"/>, or <see cref="FacadeErrorKind.NotFound"/>
    /// when the release does not exist.
    /// </returns>
    Task<FacadeResult<ReleaseDto>> UpdateReleaseNotesAsync(string id, UpdateReleaseNotesRequest request);

    /// <summary>
    /// Replaces a Planning release's tag. The tag-required check runs FIRST; a non-Planning release
    /// surfaces the store's <see cref="InvalidOperationException"/> → <see cref="FacadeErrorKind.BadRequest"/>.
    /// </summary>
    /// <param name="id">The release ID to update.</param>
    /// <param name="request">The new tag.</param>
    /// <returns>
    /// The updated release as a <see cref="ReleaseDto"/>, <see cref="FacadeErrorKind.BadRequest"/>
    /// when the tag is blank or the release is not Planning, or
    /// <see cref="FacadeErrorKind.NotFound"/> when the release does not exist.
    /// </returns>
    Task<FacadeResult<ReleaseDto>> UpdateReleaseTagAsync(string id, UpdateReleaseTagRequest request);

    /// <summary>
    /// Replaces a Planning release's repository list. <paramref name="request"/>.Repositories is
    /// <c>List&lt;string&gt;?</c> — a null value reaches <see cref="ReleaseUpdateData"/> as a no-op
    /// update followed by a 200, exactly as the pre-facade handler behaved.
    /// </summary>
    /// <param name="id">The release ID to update.</param>
    /// <param name="request">The new repository list (null = no-op).</param>
    /// <returns>
    /// The updated release as a <see cref="ReleaseDto"/>, <see cref="FacadeErrorKind.NotFound"/>
    /// when the release does not exist, or <see cref="FacadeErrorKind.BadRequest"/> when the
    /// release is not Planning.
    /// </returns>
    Task<FacadeResult<ReleaseDto>> UpdateReleaseRepositoriesAsync(string id, UpdateReleaseRepositoriesRequest request);

    /// <summary>
    /// Deletes a Planning release with no attached goals and no in-flight execution. The FIVE
    /// failure outcomes are frozen: not found → <see cref="FacadeErrorKind.NotFound"/>; non-Planning
    /// → <see cref="FacadeErrorKind.BadRequest"/>; currently executing →
    /// <see cref="FacadeErrorKind.Conflict"/>; goals attached → <see cref="FacadeErrorKind.BadRequest"/>;
    /// concurrent state change (the store's atomic delete revalidates every precondition) →
    /// <see cref="FacadeErrorKind.Conflict"/>.
    /// </summary>
    /// <param name="id">The release ID to delete.</param>
    /// <param name="ct">Cancellation token forwarded to every store call.</param>
    /// <returns>
    /// A success result carrying <see cref="RemovedResult"/> (the route answers 204 No Content),
    /// or one of the five failure outcomes above.
    /// </returns>
    Task<FacadeResult<RemovedResult>> DeleteReleaseAsync(string id, CancellationToken ct);

    /// <summary>
    /// Validates a release prior to execution. A missing execution service yields
    /// <c>{valid:true}</c> exactly as today.
    /// </summary>
    /// <param name="id">The release ID to validate.</param>
    /// <param name="ct">Cancellation token forwarded to the store and the execution service.</param>
    /// <returns>
    /// The validation outcome as a <see cref="ValidationDto"/>, or
    /// <see cref="FacadeErrorKind.NotFound"/> when the release does not exist.
    /// </returns>
    Task<FacadeResult<ValidationDto>> ValidateReleaseAsync(string id, CancellationToken ct);
}

/// <summary>
/// Default implementation of <see cref="IReleaseFacade"/> delegating to <see cref="IGoalStore"/>
/// and <see cref="ReleaseExecutionService"/>.
/// </summary>
/// <remarks>
/// Optional dependencies mirror the endpoints exactly: <see cref="ReleaseExecutionService"/>,
/// <see cref="IEventBus"/>, <see cref="NuGetPublishMonitorService"/>, <see cref="HiveConfigFile"/>,
/// <see cref="IHostApplicationLifetime"/> and <see cref="KnowledgeDocumentCleanupService"/> may be
/// <c>null</c> (the endpoints resolved them with <c>GetService</c>), while the goal store and the
/// dashboard notifier are required (the endpoints bound them as required services).
/// </remarks>
public sealed class ReleaseFacade : IReleaseFacade
{
    private readonly IGoalStore _goalStore;
    private readonly DashboardNotifier _dashboardNotifier;
    private readonly ILogger<ReleaseFacade> _log;
    private readonly ReleaseExecutionService? _executionService;
    private readonly IEventBus? _eventBus;
    private readonly NuGetPublishMonitorService? _nuGetMonitor;
    private readonly HiveConfigFile? _hiveConfig;
    private readonly IHostApplicationLifetime? _appLifetime;
    private readonly KnowledgeDocumentCleanupService? _docCleanup;

    /// <summary>
    /// Initialises a new <see cref="ReleaseFacade"/>.
    /// </summary>
    /// <param name="goalStore">The goal/release store (required).</param>
    /// <param name="dashboardNotifier">The dashboard change notifier (required).</param>
    /// <param name="log">Logger instance (required).</param>
    /// <param name="executionService">The release execution service, or <c>null</c> (503 case).</param>
    /// <param name="eventBus">The event bus, or <c>null</c> to skip publication.</param>
    /// <param name="nuGetMonitor">The NuGet publish monitor, or <c>null</c> to skip monitoring.</param>
    /// <param name="hiveConfig">The hive configuration (needed for <see cref="ApiEndpoints.LaunchNuGetMonitors"/>), or <c>null</c>.</param>
    /// <param name="appLifetime">The application lifetime, or <c>null</c>.</param>
    /// <param name="docCleanup">The knowledge-document cleanup service, or <c>null</c>.</param>
    public ReleaseFacade(
        IGoalStore goalStore,
        DashboardNotifier dashboardNotifier,
        ILogger<ReleaseFacade> log,
        ReleaseExecutionService? executionService,
        IEventBus? eventBus,
        NuGetPublishMonitorService? nuGetMonitor,
        HiveConfigFile? hiveConfig,
        IHostApplicationLifetime? appLifetime,
        KnowledgeDocumentCleanupService? docCleanup)
    {
        _goalStore = goalStore;
        _dashboardNotifier = dashboardNotifier;
        _log = log;
        _executionService = executionService;
        _eventBus = eventBus;
        _nuGetMonitor = nuGetMonitor;
        _hiveConfig = hiveConfig;
        _appLifetime = appLifetime;
        _docCleanup = docCleanup;
    }

    /// <inheritdoc />
    public async Task<ReleaseStatusOutcome> UpdateReleaseStatusAsync(
        string id, UpdateReleaseStatusRequest request, CancellationToken ct)
    {
        // The initial read keeps a DEFAULT token, exactly as the pre-facade handler called
        // store.GetReleaseAsync(id) without a token.
        var existing = await _goalStore.GetReleaseAsync(id);
        if (existing is null)
            return new StatusFailureOutcome(
                FacadeErrorKind.NotFound,
                $"Release '{id}' not found.",
                null, [], []);

        if (string.IsNullOrEmpty(request.Status))
            return new StatusFailureOutcome(
                FacadeErrorKind.BadRequest,
                "Status is required.",
                null, [], []);

        if (int.TryParse(request.Status, out _))
            return new StatusFailureOutcome(
                FacadeErrorKind.BadRequest,
                $"Invalid status '{request.Status}'. Valid values: Planning, Released.",
                null, [], []);

        if (!Enum.TryParse<ReleaseStatus>(request.Status, ignoreCase: true, out var newStatus) || !Enum.IsDefined(newStatus))
            return new StatusFailureOutcome(
                FacadeErrorKind.BadRequest,
                $"Invalid status '{request.Status}'. Valid values: Planning, Released.",
                null, [], []);

        // Reject comma-combined inputs (e.g. "Released,Planning") that Enum.TryParse may accept via bitwise OR.
        if (request.Status.Contains(','))
            return new StatusFailureOutcome(
                FacadeErrorKind.BadRequest,
                $"Invalid status '{request.Status}'. Valid values: Planning, Released.",
                null, [], []);

        if (existing.Status == ReleaseStatus.Released && newStatus == ReleaseStatus.Released)
            return new StatusFailureOutcome(
                FacadeErrorKind.Conflict,
                "Release is already in 'Released' status.",
                null, [], []);

        if (newStatus == ReleaseStatus.Planning && existing.Status == ReleaseStatus.Released)
            return new StatusFailureOutcome(
                FacadeErrorKind.Conflict,
                "Cannot revert a Released release back to Planning.",
                null, [], []);

        if (newStatus == ReleaseStatus.Planning && existing.Status == ReleaseStatus.Planning)
            return new PlanningNoOpOutcome(ReleaseDto.From(existing));

        if (newStatus == ReleaseStatus.Released)
        {
            var execService = _executionService;
            if (execService is null)
                return new StatusFailureOutcome(
                    FacadeErrorKind.ServiceUnavailable,
                    null,
                    "Release execution service is not available.",
                    [], []);

            // The ONLY call that receives the caller's token (besides the knowledge-document
            // cleanup below) — matching the pre-facade handler's HttpContext.RequestAborted.
            var result = await execService.ExecuteReleaseAsync(existing, ct);
            if (!result.Success)
            {
                return result.Failure switch
                {
                    ReleaseExecutionFailure.NotFound => new StatusFailureOutcome(
                        FacadeErrorKind.NotFound, result.Error, null, [], []),
                    ReleaseExecutionFailure.AlreadyReleased => new StatusFailureOutcome(
                        FacadeErrorKind.Conflict, result.Error, null, [], []),
                    ReleaseExecutionFailure.AlreadyExecuting => new StatusFailureOutcome(
                        FacadeErrorKind.Conflict, result.Error, null, [], []),
                    ReleaseExecutionFailure.Validation => new StatusFailureOutcome(
                        FacadeErrorKind.BadRequest, null, null,
                        [result.Error ?? "Validation failed."], []),
                    ReleaseExecutionFailure.Execution => new StatusFailureOutcome(
                        FacadeErrorKind.Internal, null, result.Error, [],
                        result.Results.Select(RepoReleaseResultDto.From).ToList()),
                    _ => throw new InvalidOperationException($"Unhandled release execution failure: {result.Failure}"),
                };
            }

            // Re-read to avoid overwriting fields that changed during execution. DEFAULT token,
            // exactly as the pre-facade handler called store.GetReleaseAsync(id).
            var updated = await _goalStore.GetReleaseAsync(id);
            updated!.Status = ReleaseStatus.Released;
            updated.ReleasedAt = DateTime.UtcNow;
            await _goalStore.UpdateReleaseAsync(updated);

            _eventBus?.Publish(new SystemEvent(
                Type: EventType.ReleaseCompleted,
                Message: $"Release '{updated.Tag}' marked as Released",
                ReleaseId: updated.Id));

            // NuGet publish monitoring: fire-and-forget monitors for every configured
            // package of every PublishNuGet repository in this release. Both services are
            // required; the application lifetime is optional (CancellationToken.None when
            // absent so the monitor is not tied to a request). Failures are logged and
            // must never fail the release.
            ApiEndpoints.LaunchNuGetMonitors(
                _nuGetMonitor,
                _hiveConfig,
                _appLifetime,
                _log,
                updated);

            // Best-effort cleanup of transient progress/review knowledge documents for
            // every goal in the release. Failures are logged and must never fail the release.
            // The goals read keeps a DEFAULT token (as today); the cleanup itself receives ct.
            var docCleanup = _docCleanup;
            if (docCleanup is not null)
            {
                try
                {
                    var goals = await _goalStore.GetGoalsByReleaseAsync(id);
                    await docCleanup.CleanupGoalsDocumentsAsync(
                        goals.Select(g => g.Id),
                        $"Cleanup progress/review docs for release '{updated.Tag}'",
                        ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex, "Failed to clean up knowledge documents for release {Tag}", updated.Tag);
                }
            }

            _dashboardNotifier.NotifyStateChanged();
            return new ExecutionSuccessOutcome(
                ReleaseDto.From(updated),
                ReleaseExecutionResultDto.From(result));
        }

        // Defensive fall-through preserved from the pre-facade handler: every transition is
        // handled above, so this is unreachable in practice — kept for byte-identical behaviour.
        existing.Status = newStatus;
        await _goalStore.UpdateReleaseAsync(existing);
        _dashboardNotifier.NotifyStateChanged();
        return new PlanningNoOpOutcome(ReleaseDto.From(existing));
    }

    /// <inheritdoc />
    public async Task<FacadeResult<ReleaseDto>> CreateReleaseAsync(CreateReleaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Version))
            return new(false, null, "Version is required.", FacadeErrorKind.BadRequest);

        var release = new Release
        {
            Id = request.Version,
            Tag = request.Version,
            RepositoryNames = string.IsNullOrEmpty(request.Repository) ? [] : [request.Repository],
        };

        try
        {
            var created = await _goalStore.CreateReleaseAsync(release);
            _dashboardNotifier.NotifyStateChanged();
            return new(true, ReleaseDto.From(created), null, FacadeErrorKind.None);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.Conflict);
        }
        // Any other exception propagates to the caller — the endpoint never caught it either.
    }

    /// <inheritdoc />
    public async Task<FacadeResult<ReleaseDto>> UpdateReleaseNotesAsync(string id, UpdateReleaseNotesRequest request)
    {
        var existing = await _goalStore.GetReleaseAsync(id);
        if (existing is null)
            return new(false, null, $"Release '{id}' not found.", FacadeErrorKind.NotFound);

        // request.Notes is string? — null CLEARS the notes, exactly as the pre-facade handler
        // assigned it directly.
        existing.Notes = request.Notes;
        await _goalStore.UpdateReleaseAsync(existing);
        _dashboardNotifier.NotifyStateChanged();
        return new(true, ReleaseDto.From(existing), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<ReleaseDto>> UpdateReleaseTagAsync(string id, UpdateReleaseTagRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Tag))
            return new(false, null, "Tag is required.", FacadeErrorKind.BadRequest);

        try
        {
            await _goalStore.UpdateReleaseAsync(id, new ReleaseUpdateData { Tag = request.Tag.Trim() });
        }
        catch (KeyNotFoundException)
        {
            return new(false, null, $"Release '{id}' not found.", FacadeErrorKind.NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
        // Any other exception propagates to the caller — the endpoint never caught it either.

        var updated = await _goalStore.GetReleaseAsync(id);
        _dashboardNotifier.NotifyStateChanged();
        return new(true, ReleaseDto.From(updated!), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<ReleaseDto>> UpdateReleaseRepositoriesAsync(string id, UpdateReleaseRepositoriesRequest request)
    {
        try
        {
            // request.Repositories is List<string>? — a null value reaches ReleaseUpdateData as a
            // no-op update followed by 200, exactly as the pre-facade handler behaved.
            await _goalStore.UpdateReleaseAsync(id, new ReleaseUpdateData { Repositories = request.Repositories });
        }
        catch (KeyNotFoundException)
        {
            return new(false, null, $"Release '{id}' not found.", FacadeErrorKind.NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
        // Any other exception propagates to the caller — the endpoint never caught it either.

        var updated = await _goalStore.GetReleaseAsync(id);
        _dashboardNotifier.NotifyStateChanged();
        return new(true, ReleaseDto.From(updated!), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<RemovedResult>> DeleteReleaseAsync(string id, CancellationToken ct)
    {
        var release = await _goalStore.GetReleaseAsync(id, ct);
        if (release is null)
            return new(false, null, $"Release '{id}' not found.", FacadeErrorKind.NotFound);

        if (release.Status != ReleaseStatus.Planning)
            return new(false, null, "Only Planning releases can be deleted.", FacadeErrorKind.BadRequest);

        if (release.ExecutionState == ReleaseExecutionState.Executing)
            return new(false, null, "Release is currently executing — cannot delete.", FacadeErrorKind.Conflict);

        var goals = await _goalStore.GetGoalsByReleaseAsync(id, ct);
        if (goals.Count > 0)
            return new(false, null,
                $"Release has {goals.Count} goal(s) attached — remove or reassign them before deleting.",
                FacadeErrorKind.BadRequest);

        // The store re-validates every precondition atomically. A false result here means a
        // concurrent state change slipped between the pre-checks above and the delete.
        var deleted = await _goalStore.DeleteReleaseAsync(id, ct);
        if (!deleted)
            return new(false, null,
                "Release could not be deleted due to a concurrent state change. Refresh and try again.",
                FacadeErrorKind.Conflict);

        _dashboardNotifier.NotifyStateChanged();
        return new(true, new RemovedResult(true), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<ValidationDto>> ValidateReleaseAsync(string id, CancellationToken ct)
    {
        var release = await _goalStore.GetReleaseAsync(id, ct);
        if (release is null)
            return new(false, null, $"Release '{id}' not found.", FacadeErrorKind.NotFound);

        var execService = _executionService;
        if (execService is null)
            return new(true, new ValidationDto(true, []), null, FacadeErrorKind.None);

        var validation = await execService.ValidateReleaseAsync(release, ct);
        return new(true, new ValidationDto(validation.IsValid, validation.Errors), null, FacadeErrorKind.None);
    }
}
