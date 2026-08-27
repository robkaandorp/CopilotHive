using System.Text.RegularExpressions;

using CopilotHive.Goals;

namespace CopilotHive.Services;

/// <summary>
/// Facade over the issue surface used by the issues REST API and the Issues page: listing with
/// filters, reading, creating, updating and deleting issues. Endpoint handlers (and the Blazor
/// component) depend on this interface instead of reaching into <see cref="IIssueStore"/>
/// directly, so the filter parsing, validation order, ID-format rules and event side effects
/// live in exactly one place and run exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Each operation returns a <see cref="FacadeResult{T}"/> whose <see cref="FacadeErrorKind"/>
/// mirrors the HTTP status the endpoint would have returned before the facade existed. A
/// facade method catches ONLY the exception types its previous endpoint handler caught
/// (<see cref="CreateIssueAsync"/> catches <see cref="InvalidOperationException"/> from the
/// store and maps it to <see cref="FacadeErrorKind.Conflict"/>); anything else is RETHROWN so
/// unexpected failures surface as exceptions (ASP.NET → 500) instead of being silently
/// converted into a result.
/// </para>
/// <para>
/// The event side effects (<c>IssueRaised</c> after create, <c>IssueResolved</c> on a
/// non-terminal → terminal status transition) moved INTO the facade with the exact fields the
/// pre-facade handlers populated. When <see cref="IEventBus"/> is null (open mode without the
/// composer), publication is skipped silently, exactly as the <c>eventBus?.Publish(...)</c>
/// calls behaved.
/// </para>
/// <para>
/// All five operations propagate the caller's <see cref="CancellationToken"/> to
/// <see cref="IIssueStore"/>.
/// </para>
/// </remarks>
public interface IIssueFacade
{
    /// <summary>
    /// Lists issues filtered by the given criteria. The raw string filter values for status,
    /// type and severity are parsed here with the exact validation and error messages the
    /// pre-facade endpoint used.
    /// </summary>
    /// <param name="filter">The filter criteria (raw string values).</param>
    /// <param name="ct">Cancellation token forwarded to the store.</param>
    /// <returns>
    /// The matching issues as <see cref="LinkedIssueDto"/>s, or
    /// <see cref="FacadeErrorKind.BadRequest"/> when a filter value is invalid.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<LinkedIssueDto>>> GetIssuesAsync(IssueFilter filter, CancellationToken ct);

    /// <summary>
    /// Reads a single issue by ID.
    /// </summary>
    /// <param name="id">The issue ID.</param>
    /// <param name="ct">Cancellation token forwarded to the store.</param>
    /// <returns>
    /// The issue as a <see cref="LinkedIssueDto"/>, or <see cref="FacadeErrorKind.NotFound"/>
    /// when no issue with that ID exists.
    /// </returns>
    Task<FacadeResult<LinkedIssueDto>> GetIssueAsync(string id, CancellationToken ct);

    /// <summary>
    /// Creates a new issue. Validation order is frozen: Type → Title → Description, then the
    /// caller-supplied ID format. On success the <c>IssueRaised</c> event is published on the
    /// event bus (when one is registered).
    /// </summary>
    /// <param name="request">The issue to create.</param>
    /// <param name="ct">Cancellation token forwarded to the store.</param>
    /// <returns>
    /// The created issue as a <see cref="LinkedIssueDto"/>, <see cref="FacadeErrorKind.BadRequest"/>
    /// for a validation failure, or <see cref="FacadeErrorKind.Conflict"/> when an issue with the
    /// same ID already exists. Any other exception propagates to the caller.
    /// </returns>
    Task<FacadeResult<LinkedIssueDto>> CreateIssueAsync(CreateIssueRequest request, CancellationToken ct);

    /// <summary>
    /// Partially updates an issue. Validation order is frozen: existence check FIRST, then a
    /// provided blank Title, then a provided blank Description. The <c>IssueResolved</c> event
    /// is published ONLY on a transition from a non-terminal status to Resolved or Closed.
    /// </summary>
    /// <param name="id">The issue ID to update.</param>
    /// <param name="request">The fields to change.</param>
    /// <param name="ct">Cancellation token forwarded to the store.</param>
    /// <returns>
    /// The updated issue as a <see cref="LinkedIssueDto"/>, <see cref="FacadeErrorKind.NotFound"/>
    /// when the issue does not exist, or <see cref="FacadeErrorKind.BadRequest"/> for a blank
    /// provided Title/Description. Any other exception propagates to the caller.
    /// </returns>
    Task<FacadeResult<LinkedIssueDto>> UpdateIssueAsync(string id, UpdateIssueRequest request, CancellationToken ct);

    /// <summary>
    /// Deletes an issue by ID.
    /// </summary>
    /// <param name="id">The issue ID to delete.</param>
    /// <param name="ct">Cancellation token forwarded to the store.</param>
    /// <returns>
    /// A success result carrying <see cref="RemovedResult"/>, or
    /// <see cref="FacadeErrorKind.NotFound"/> when the issue does not exist.
    /// </returns>
    Task<FacadeResult<RemovedResult>> DeleteIssueAsync(string id, CancellationToken ct);
}

/// <summary>
/// Default implementation of <see cref="IIssueFacade"/> delegating to <see cref="IIssueStore"/>.
/// </summary>
/// <remarks>
/// The event bus is optional, mirroring the pre-facade endpoints' <c>[FromServices] IEventBus?
/// eventBus = null</c> parameter: in open mode (no composer) it is null and publication is
/// skipped silently.
/// </remarks>
public sealed class IssueFacade : IIssueFacade
{
    private readonly IIssueStore _issueStore;
    private readonly IEventBus? _eventBus;
    private readonly ILogger<IssueFacade> _log;

    /// <summary>
    /// Initialises a new <see cref="IssueFacade"/>.
    /// </summary>
    /// <param name="issueStore">The issue store (required).</param>
    /// <param name="eventBus">The event bus, or <c>null</c> when not registered (open mode).</param>
    /// <param name="log">Logger instance.</param>
    public IssueFacade(IIssueStore issueStore, IEventBus? eventBus, ILogger<IssueFacade> log)
    {
        _issueStore = issueStore;
        _eventBus = eventBus;
        _log = log;
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<LinkedIssueDto>>> GetIssuesAsync(IssueFilter filter, CancellationToken ct)
    {
        // Query params use explicit snake_case parsing: reject numeric strings and
        // comma-combined values that Enum.TryParse may otherwise accept via bitwise OR.
        // Enum.TryParse does not strip underscores, so snake_case inputs ("in_progress")
        // are normalized before the standard parse.
        IssueStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(filter.Status))
        {
            var normalizedStatus = filter.Status.Replace("_", "", StringComparison.Ordinal);
            if (int.TryParse(filter.Status, out _) || filter.Status.Contains(',')
                || !Enum.TryParse<IssueStatus>(normalizedStatus, ignoreCase: true, out var parsedStatus)
                || !Enum.IsDefined(parsedStatus))
            {
                return new(false, null,
                    $"Invalid status '{filter.Status}'. Valid values: open, triaged, acknowledged, in_progress, resolved, closed.",
                    FacadeErrorKind.BadRequest);
            }
            statusFilter = parsedStatus;
        }

        IssueType? typeFilter = null;
        if (!string.IsNullOrEmpty(filter.Type))
        {
            // Explicit alias: "codequality" maps to IssueType.CodeQuality.
            IssueType parsedType;
            if (string.Equals(filter.Type, "codequality", StringComparison.OrdinalIgnoreCase))
            {
                parsedType = IssueType.CodeQuality;
            }
            else
            {
                var normalizedType = filter.Type.Replace("_", "", StringComparison.Ordinal);
                if (int.TryParse(filter.Type, out _) || filter.Type.Contains(',')
                    || !Enum.TryParse<IssueType>(normalizedType, ignoreCase: true, out parsedType)
                    || !Enum.IsDefined(parsedType))
                {
                    return new(false, null,
                        $"Invalid type '{filter.Type}'. Valid values: code_quality, bug, suggestion, concern, workflow.",
                        FacadeErrorKind.BadRequest);
                }
            }
            typeFilter = parsedType;
        }

        IssueSeverity? severityFilter = null;
        if (!string.IsNullOrEmpty(filter.Severity))
        {
            var normalizedSeverity = filter.Severity.Replace("_", "", StringComparison.Ordinal);
            if (int.TryParse(filter.Severity, out _) || filter.Severity.Contains(',')
                || !Enum.TryParse<IssueSeverity>(normalizedSeverity, ignoreCase: true, out var parsedSeverity)
                || !Enum.IsDefined(parsedSeverity))
            {
                return new(false, null,
                    $"Invalid severity '{filter.Severity}'. Valid values: low, medium, high.",
                    FacadeErrorKind.BadRequest);
            }
            severityFilter = parsedSeverity;
        }

        var issues = await _issueStore.GetIssuesAsync(
            statusFilter, typeFilter, severityFilter, filter.Repository, filter.SourceGoalId, filter.LinkedGoalId, ct);
        _log.LogDebug("Listed {Count} issue(s).", issues.Count);
        return new(true, issues.Select(LinkedIssueDto.From).ToList(), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<LinkedIssueDto>> GetIssueAsync(string id, CancellationToken ct)
    {
        var issue = await _issueStore.GetIssueAsync(id, ct);
        return issue is null
            ? new(false, null, $"Issue '{id}' not found.", FacadeErrorKind.NotFound)
            : new(true, LinkedIssueDto.From(issue), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<LinkedIssueDto>> CreateIssueAsync(CreateIssueRequest request, CancellationToken ct)
    {
        if (request.Type is null)
            return new(false, null, "Type is required.", FacadeErrorKind.BadRequest);

        if (string.IsNullOrWhiteSpace(request.Title))
            return new(false, null, "Title is required.", FacadeErrorKind.BadRequest);

        if (string.IsNullOrWhiteSpace(request.Description))
            return new(false, null, "Description is required.", FacadeErrorKind.BadRequest);

        // ID resolution: null/empty/whitespace → generate; non-empty → validate kebab-case.
        string id;
        if (request.Id is null)
        {
            id = await IssueIdGenerator.GenerateAsync(request.Title, _issueStore, ct);
        }
        else
        {
            var trimmed = request.Id.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                id = await IssueIdGenerator.GenerateAsync(request.Title, _issueStore, ct);
            }
            else if (!IssueIdPattern.IsMatch(trimmed))
            {
                return new(false, null,
                    $"Invalid issue ID '{request.Id}'. IDs must be lowercase kebab-case (letters, digits, hyphens).",
                    FacadeErrorKind.BadRequest);
            }
            else
            {
                id = trimmed;
            }
        }

        var issue = new Issue
        {
            Id = id,
            Type = request.Type.Value,
            Title = request.Title,
            Description = request.Description,
            Severity = request.Severity ?? IssueSeverity.Low,
            RepositoryNames = request.RepositoryNames ?? [],
            SourceGoalId = request.SourceGoalId,
            SourceRole = request.SourceRole,
            SourceIteration = request.SourceIteration,
        };

        try
        {
            var created = await _issueStore.CreateIssueAsync(issue, ct);
            _eventBus?.Publish(new SystemEvent(
                Type: EventType.IssueRaised,
                Message: created.Title,
                IssueId: created.Id));
            return new(true, LinkedIssueDto.From(created), null, FacadeErrorKind.None);
        }
        catch (InvalidOperationException)
        {
            return new(false, null, $"Issue '{id}' already exists.", FacadeErrorKind.Conflict);
        }
        // Any other exception propagates to the caller — the endpoint never caught it either.
    }

    /// <inheritdoc />
    public async Task<FacadeResult<LinkedIssueDto>> UpdateIssueAsync(string id, UpdateIssueRequest request, CancellationToken ct)
    {
        var existing = await _issueStore.GetIssueAsync(id, ct);
        if (existing is null)
            return new(false, null, $"Issue '{id}' not found.", FacadeErrorKind.NotFound);

        // Capture original status before mutation so we can detect a transition to terminal.
        var previousStatus = existing.Status;

        // Title/Description, when provided, must be non-empty/whitespace.
        if (request.Title is not null && string.IsNullOrWhiteSpace(request.Title))
            return new(false, null, "Title is required.", FacadeErrorKind.BadRequest);

        if (request.Description is not null && string.IsNullOrWhiteSpace(request.Description))
            return new(false, null, "Description is required.", FacadeErrorKind.BadRequest);

        if (request.Type is not null) existing.Type = request.Type.Value;
        if (request.Title is not null) existing.Title = request.Title;
        if (request.Description is not null) existing.Description = request.Description;
        if (request.Severity is not null) existing.Severity = request.Severity.Value;
        if (request.Status is not null) existing.Status = request.Status.Value;
        if (request.RepositoryNames is not null) existing.RepositoryNames = request.RepositoryNames;

        // LinkedGoalId tri-state: null = no change, "" = clear, non-empty = set.
        if (request.LinkedGoalId is not null)
            existing.LinkedGoalId = request.LinkedGoalId.Length == 0 ? null : request.LinkedGoalId;

        await _issueStore.UpdateIssueAsync(existing, ct);

        // Publish IssueResolved only on transition from non-terminal to terminal.
        if (request.Status is not null
            && request.Status.Value is IssueStatus.Resolved or IssueStatus.Closed
            && previousStatus is not IssueStatus.Resolved and not IssueStatus.Closed)
        {
            _eventBus?.Publish(new SystemEvent(
                Type: EventType.IssueResolved,
                Message: $"Issue '{id}' marked as {request.Status.Value}",
                IssueId: id,
                GoalId: existing.LinkedGoalId));
        }

        // Re-fetch for accurate timestamps (UpdatedAt / ResolvedAt are store-managed).
        var updated = await _issueStore.GetIssueAsync(id, ct);
        return new(true, LinkedIssueDto.From(updated!), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<RemovedResult>> DeleteIssueAsync(string id, CancellationToken ct)
    {
        var deleted = await _issueStore.DeleteIssueAsync(id, ct);
        return deleted
            ? new(true, new RemovedResult(true), null, FacadeErrorKind.None)
            : new(false, null, $"Issue '{id}' not found.", FacadeErrorKind.NotFound);
    }

    /// <summary>Validates caller-supplied issue IDs: ASCII lowercase letters, digits, hyphens only.</summary>
    private static readonly Regex IssueIdPattern = new(
        "^[a-z0-9]+(-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
