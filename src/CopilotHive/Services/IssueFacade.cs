using System.Security.Claims;
using System.Text.RegularExpressions;

using CopilotHive.Goals;
using CopilotHive.Security;

namespace CopilotHive.Services;

/// <summary>
/// Facade over the issue surface used by the issues REST API and the Issues page: listing with
/// filters, reading, creating, updating and deleting issues. Endpoint handlers and the Blazor
/// component depend on this interface instead of reaching into <see cref="IIssueStore"/>
/// directly, so the filter parsing, validation order, ID-format rules, authorization and event
/// side effects live in exactly one place and run exactly once.
/// </summary>
/// <remarks>
/// <para>
/// AUTHORIZATION IS PART OF EVERY OPERATION, AND IT RUNS FIRST. Each operation takes the caller's
/// per-request (or per-circuit) <see cref="ClaimsPrincipal"/> and re-resolves the acting principal
/// FRESH through <see cref="HivePrincipalResolver"/> on every call, so a demoted, disabled or
/// deleted account is refused on its very NEXT operation and nothing is cached between calls. A
/// refusal, or a principal that does not hold the required <see cref="HiveCapability"/>, produces
/// <see cref="FacadeErrorKind.Forbidden"/> BEFORE any store work, any validation and any side
/// effect — so an unauthorized caller sending an invalid update gets Forbidden, not BadRequest,
/// and cannot use the validation messages to probe the schema. There is deliberately NO
/// identity-free overload.
/// </para>
/// <para>
/// THE ROLE MAP this facade enforces: BOTH reads (list and single-issue) require
/// <see cref="HiveCapability.SharedRead"/>; CREATE requires
/// <see cref="HiveCapability.InteractiveMember"/>; and the ENTIRE
/// <see cref="UpdateIssueAsync"/> surface — every field, including <c>Type</c> and combined-field
/// updates, triage/status/severity/title/description/repository/link — together with
/// <see cref="DeleteIssueAsync"/> requires <see cref="HiveCapability.ExecutionOperator"/>. A Viewer
/// therefore has no mutation anywhere in this facade, and <see cref="HiveCapability.GlobalAdmin"/>
/// is never consulted. Open mode resolves the explicit open-mode local operator through the same
/// resolver, so the single-operator installation keeps its previous behaviour exactly.
/// </para>
/// <para>
/// Each operation returns a <see cref="FacadeResult{T}"/> whose <see cref="FacadeErrorKind"/>
/// mirrors the HTTP status the endpoint would have returned before the facade existed. A
/// facade method catches ONLY the exception types its previous endpoint handler caught
/// (<see cref="CreateIssueAsync"/> catches <see cref="InvalidOperationException"/> from the
/// store and maps it to <see cref="FacadeErrorKind.Conflict"/>); anything else is RETHROWN so
/// unexpected failures surface as exceptions (ASP.NET → 500) instead of being silently
/// converted into a result. Caller cancellation is never an answer: it propagates out of every
/// operation and is never converted into a denial.
/// </para>
/// <para>
/// The event side effects (<c>IssueRaised</c> after create, <c>IssueResolved</c> on a
/// non-terminal → terminal status transition) moved INTO the facade with the exact fields the
/// pre-facade handlers populated. When <see cref="IEventBus"/> is null (open mode without the
/// composer), publication is skipped silently, exactly as the <c>eventBus?.Publish(...)</c>
/// calls behaved.
/// </para>
/// <para>
/// All operations propagate the caller's <see cref="CancellationToken"/> to
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
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token forwarded to the store.</param>
    /// <returns>
    /// The matching issues as <see cref="LinkedIssueDto"/>s, <see cref="FacadeErrorKind.BadRequest"/>
    /// when a filter value is invalid, or <see cref="FacadeErrorKind.Forbidden"/> when the caller does
    /// not currently hold shared read (checked BEFORE the filter parsing).
    /// </returns>
    Task<FacadeResult<IReadOnlyList<LinkedIssueDto>>> GetIssuesAsync(
        IssueFilter filter, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads a single issue by ID.
    /// </summary>
    /// <param name="id">The issue ID.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token forwarded to the store.</param>
    /// <returns>
    /// The issue as a <see cref="LinkedIssueDto"/>, <see cref="FacadeErrorKind.NotFound"/>
    /// when no issue with that ID exists, or <see cref="FacadeErrorKind.Forbidden"/> when the caller
    /// does not currently hold shared read.
    /// </returns>
    Task<FacadeResult<LinkedIssueDto>> GetIssueAsync(string id, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Creates a new issue. Validation order is frozen: Type → Title → Description, then the
    /// caller-supplied ID format. On success the <c>IssueRaised</c> event is published on the
    /// event bus (when one is registered).
    /// </summary>
    /// <param name="request">The issue to create.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token forwarded to the store.</param>
    /// <returns>
    /// The created issue as a <see cref="LinkedIssueDto"/>, <see cref="FacadeErrorKind.BadRequest"/>
    /// for a validation failure, <see cref="FacadeErrorKind.Conflict"/> when an issue with the
    /// same ID already exists, or <see cref="FacadeErrorKind.Forbidden"/> when the caller does not
    /// currently hold <see cref="HiveCapability.InteractiveMember"/> (checked BEFORE validation).
    /// Any other exception propagates to the caller.
    /// </returns>
    Task<FacadeResult<LinkedIssueDto>> CreateIssueAsync(
        CreateIssueRequest request, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Partially updates an issue. Validation order is frozen: existence check FIRST, then a
    /// provided blank Title, then a provided blank Description. The <c>IssueResolved</c> event
    /// is published ONLY on a transition from a non-terminal status to Resolved or Closed.
    /// </summary>
    /// <param name="id">The issue ID to update.</param>
    /// <param name="request">The fields to change.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token forwarded to the store.</param>
    /// <returns>
    /// The updated issue as a <see cref="LinkedIssueDto"/>, <see cref="FacadeErrorKind.NotFound"/>
    /// when the issue does not exist, <see cref="FacadeErrorKind.BadRequest"/> for a blank
    /// provided Title/Description, or <see cref="FacadeErrorKind.Forbidden"/> when the caller does
    /// not currently hold <see cref="HiveCapability.ExecutionOperator"/> (checked BEFORE the
    /// existence read, so no store work runs for a refused caller). Any other exception propagates
    /// to the caller.
    /// </returns>
    Task<FacadeResult<LinkedIssueDto>> UpdateIssueAsync(
        string id, UpdateIssueRequest request, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Deletes an issue by ID.
    /// </summary>
    /// <param name="id">The issue ID to delete.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token forwarded to the store.</param>
    /// <returns>
    /// A success result carrying <see cref="RemovedResult"/>, <see cref="FacadeErrorKind.NotFound"/>
    /// when the issue does not exist, or <see cref="FacadeErrorKind.Forbidden"/> when the caller does
    /// not currently hold <see cref="HiveCapability.ExecutionOperator"/>.
    /// </returns>
    Task<FacadeResult<RemovedResult>> DeleteIssueAsync(string id, ClaimsPrincipal? actingUser, CancellationToken ct);
}

/// <summary>
/// Default implementation of <see cref="IIssueFacade"/> delegating to <see cref="IIssueStore"/>.
/// </summary>
/// <remarks>
/// The event bus is optional, mirroring the pre-facade endpoints' <c>[FromServices] IEventBus?
/// eventBus = null</c> parameter: in open mode (no composer) it is null and publication is
/// skipped silently. The authorization primitives are REQUIRED, so there is no unauthenticated
/// construction; the facade never captures the scoped circuit accessor.
/// </remarks>
public sealed class IssueFacade : IIssueFacade
{
    private readonly IIssueStore _issueStore;
    private readonly IEventBus? _eventBus;
    private readonly HivePrincipalResolver _resolver;
    private readonly HivePolicy _policy;
    private readonly ILogger<IssueFacade> _log;

    /// <summary>
    /// Initialises a new <see cref="IssueFacade"/>.
    /// </summary>
    /// <param name="issueStore">The issue store (required).</param>
    /// <param name="eventBus">The event bus, or <c>null</c> when not registered (open mode).</param>
    /// <param name="resolver">The stateless principal resolver every operation re-resolves through (required).</param>
    /// <param name="policy">The pure capability evaluator (required together with <paramref name="resolver"/>).</param>
    /// <param name="log">Logger instance.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public IssueFacade(
        IIssueStore issueStore,
        IEventBus? eventBus,
        HivePrincipalResolver resolver,
        HivePolicy policy,
        ILogger<IssueFacade> log)
    {
        _issueStore = issueStore;
        _eventBus = eventBus;
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<LinkedIssueDto>>> GetIssuesAsync(
        IssueFilter filter, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetIssuesAsync), ct))
            return Forbidden<IReadOnlyList<LinkedIssueDto>>();

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
    public async Task<FacadeResult<LinkedIssueDto>> GetIssueAsync(string id, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.SharedRead, nameof(GetIssueAsync), ct))
            return Forbidden<LinkedIssueDto>();

        var issue = await _issueStore.GetIssueAsync(id, ct);
        return issue is null
            ? new(false, null, $"Issue '{id}' not found.", FacadeErrorKind.NotFound)
            : new(true, LinkedIssueDto.From(issue), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<LinkedIssueDto>> CreateIssueAsync(
        CreateIssueRequest request, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.InteractiveMember, nameof(CreateIssueAsync), ct))
            return Forbidden<LinkedIssueDto>();

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
    public async Task<FacadeResult<LinkedIssueDto>> UpdateIssueAsync(
        string id, UpdateIssueRequest request, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        // THE WHOLE UpdateIssueAsync SURFACE — Type, triage/status/severity/title/description/
        // repository/link fields and combined updates alike — is an OPERATOR operation, authorized
        // before the existence read so a refused caller performs no store work at all.
        if (!await IsAllowedAsync(actingUser, HiveCapability.ExecutionOperator, nameof(UpdateIssueAsync), ct))
            return Forbidden<LinkedIssueDto>();

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
    public async Task<FacadeResult<RemovedResult>> DeleteIssueAsync(string id, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, HiveCapability.ExecutionOperator, nameof(DeleteIssueAsync), ct))
            return Forbidden<RemovedResult>();

        var deleted = await _issueStore.DeleteIssueAsync(id, ct);
        return deleted
            ? new(true, new RemovedResult(true), null, FacadeErrorKind.None)
            : new(false, null, $"Issue '{id}' not found.", FacadeErrorKind.NotFound);
    }

    // ── Authorization + shared results ──────────────────────────────────────

    /// <summary>
    /// THE ONE AUTHORIZATION CALL every operation makes, BEFORE any of its own work: one fresh
    /// resolution of the caller's CURRENT stored state and one capability evaluation.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="capability">The capability the operation requires.</param>
    /// <param name="operation">The operation name (log context only).</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates, never a denial.</param>
    /// <returns><c>true</c> only when the caller currently holds <paramref name="capability"/>.</returns>
    private Task<bool> IsAllowedAsync(
        ClaimsPrincipal? actingUser, HiveCapability capability, string operation, CancellationToken ct) =>
        FacadeAuthorization.IsAllowedAsync(_resolver, _policy, actingUser, capability, _log, operation, ct);

    /// <summary>Builds the shared denial result carrying the sanitized denial text.</summary>
    /// <typeparam name="T">The facade value type of the operation.</typeparam>
    /// <returns>A <see cref="FacadeErrorKind.Forbidden"/> result.</returns>
    private static FacadeResult<T> Forbidden<T>() =>
        new(false, default, FacadeAuthorization.ForbiddenMessage, FacadeErrorKind.Forbidden);

    /// <summary>Validates caller-supplied issue IDs: ASCII lowercase letters, digits, hyphens only.</summary>
    private static readonly Regex IssueIdPattern = new(
        "^[a-z0-9]+(-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
