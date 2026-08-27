using CopilotHive.Goals;
using CopilotHive.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for <see cref="IssueFacade"/> — the facade the five issue endpoints and the
/// <c>Issues</c> component use instead of touching <see cref="IIssueStore"/> directly.
/// </summary>
/// <remarks>
/// The load-bearing contracts pinned down here mirror the pre-facade handlers exactly:
/// <list type="bullet">
///   <item>List-filter parsing rejects numeric strings and comma-combined values with the
///   byte-for-byte frozen error messages.</item>
///   <item>Create validation order is frozen: Type → Title → Description, then ID format.</item>
///   <item>A duplicate ID (<see cref="InvalidOperationException"/> from the store) maps to
///   <see cref="FacadeErrorKind.Conflict"/>; ANY other exception is RETHROWN (a non-duplicate
///   <c>DbUpdateException</c> becomes 500 as today).</item>
///   <item>Update validation order is frozen: existence FIRST, then blank Title, then blank
///   Description. NO new enum validation.</item>
///   <item><c>IssueRaised</c> is published after successful create; <c>IssueResolved</c> ONLY on
///   a non-terminal → Resolved/Closed transition; a null <see cref="IEventBus"/> skips
///   publication silently.</item>
///   <item>All five operations propagate the caller's <see cref="CancellationToken"/> to the
///   store.</item>
/// </list>
/// </remarks>
public sealed class IssueFacadeTests
{
    // ── Fakes and builders ───────────────────────────────────────────────────

    /// <summary>
    /// In-memory <see cref="IIssueStore"/> that records the calls the facade makes, so filter
    /// forwarding, cancellation-token propagation and side effects can be asserted exactly.
    /// </summary>
    private sealed class RecordingIssueStore : IIssueStore
    {
        private readonly Dictionary<string, Issue> _issues = new();

        /// <summary>Every (status, type, severity, repository, sourceGoalId, linkedGoalId) filter tuple, in call order.</summary>
        public List<(IssueStatus? Status, IssueType? Type, IssueSeverity? Severity, string? Repository, string? SourceGoalId, string? LinkedGoalId)> Queries { get; } = [];

        /// <summary>Every cancellation token the facade forwarded, in call order.</summary>
        public List<CancellationToken> Tokens { get; } = [];

        /// <summary>When set, <see cref="CreateIssueAsync"/> throws this exception.</summary>
        public Exception? CreateThrows { get; set; }

        /// <summary>When set, <see cref="UpdateIssueAsync"/> throws this exception.</summary>
        public Exception? UpdateThrows { get; set; }

        /// <summary>When set, <see cref="DeleteIssueAsync"/> reports "not found" instead of deleting.</summary>
        public bool DeleteReturnsFalse { get; set; }

        public void AddIssue(Issue issue) => _issues[issue.Id] = issue;

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Issue>>(_issues.Values.ToList());

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null, IssueType? type = null, IssueSeverity? severity = null,
            string? repository = null, string? sourceGoalId = null, string? linkedGoalId = null,
            CancellationToken ct = default)
        {
            Queries.Add((status, type, severity, repository, sourceGoalId, linkedGoalId));
            Tokens.Add(ct);

            IEnumerable<Issue> query = _issues.Values;
            if (status is not null) query = query.Where(i => i.Status == status);
            if (type is not null) query = query.Where(i => i.Type == type);
            if (severity is not null) query = query.Where(i => i.Severity == severity);
            if (repository is not null)
                query = query.Where(i => i.RepositoryNames.Contains(repository, StringComparer.OrdinalIgnoreCase));
            if (sourceGoalId is not null) query = query.Where(i => i.SourceGoalId == sourceGoalId);
            if (linkedGoalId is not null) query = query.Where(i => i.LinkedGoalId == linkedGoalId);
            return Task.FromResult<IReadOnlyList<Issue>>(query.ToList());
        }

        public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
        {
            Tokens.Add(ct);
            return Task.FromResult(_issues.TryGetValue(issueId, out var issue) ? issue : null);
        }

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Tokens.Add(ct);
            if (CreateThrows is not null)
                throw CreateThrows;
            if (_issues.ContainsKey(issue.Id))
                throw new InvalidOperationException($"Issue '{issue.Id}' already exists.");
            _issues[issue.Id] = issue;
            return Task.FromResult(issue);
        }

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Tokens.Add(ct);
            if (UpdateThrows is not null)
                throw UpdateThrows;
            if (!_issues.ContainsKey(issue.Id))
                throw new InvalidOperationException($"Issue '{issue.Id}' not found.");
            _issues[issue.Id] = issue;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
        {
            Tokens.Add(ct);
            if (DeleteReturnsFalse)
                return Task.FromResult(false);
            return Task.FromResult(_issues.Remove(issueId));
        }
    }

    /// <summary>A recording <see cref="IEventBus"/> that captures all published events.</summary>
    private sealed class RecordingEventBus : IEventBus
    {
        public List<SystemEvent> Published { get; } = [];

        public event Action<SystemEvent>? OnEvent;

        public void Publish(SystemEvent evt)
        {
            Published.Add(evt);
            OnEvent?.Invoke(evt);
        }
    }

    private static Issue MakeIssue(
        string id,
        IssueType type = IssueType.Bug,
        string? title = null,
        IssueSeverity severity = IssueSeverity.Medium,
        IssueStatus status = IssueStatus.Open,
        string? sourceGoalId = null,
        string? linkedGoalId = null)
        => new()
        {
            Id = id,
            Type = type,
            Title = title ?? $"Title {id}",
            Description = "desc",
            Severity = severity,
            Status = status,
            SourceGoalId = sourceGoalId,
            LinkedGoalId = linkedGoalId,
        };

    private static IssueFacade CreateFacade(
        RecordingIssueStore store,
        IEventBus? eventBus = null)
        => new(store, eventBus, NullLogger<IssueFacade>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── GetIssuesAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetIssues_NoFilters_QueriesStoreWithNulls()
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store);

        var result = await facade.GetIssuesAsync(new IssueFilter(), Ct);

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.NotNull(result.Value);
        Assert.Empty(result.Value!);
        var query = Assert.Single(store.Queries);
        Assert.Equal((null, null, null, null, null, null), query);
        Assert.Equal(Ct, Assert.Single(store.Tokens));
    }

    [Fact]
    public async Task GetIssues_AllFilters_ForwardedToStore()
    {
        var store = new RecordingIssueStore();
        var match = MakeIssue("match", type: IssueType.Bug, severity: IssueSeverity.High, status: IssueStatus.InProgress, sourceGoalId: "goal-1", linkedGoalId: "goal-2");
        match.RepositoryNames = ["CopilotHive"];
        store.AddIssue(match);
        store.AddIssue(MakeIssue("other", type: IssueType.Suggestion));
        var facade = CreateFacade(store);

        var result = await facade.GetIssuesAsync(new IssueFilter(
            Status: "in_progress", Type: "bug", Severity: "high",
            Repository: "CopilotHive", SourceGoalId: "goal-1", LinkedGoalId: "goal-2"), Ct);

        Assert.True(result.Success);
        var issue = Assert.Single(result.Value!);
        Assert.Equal("match", issue.Id);
        var query = Assert.Single(store.Queries);
        Assert.Equal(
            (IssueStatus.InProgress, IssueType.Bug, IssueSeverity.High, "CopilotHive", "goal-1", "goal-2"),
            query);
    }

    [Theory]
    [InlineData("not-a-status", "Invalid status 'not-a-status'. Valid values: open, triaged, acknowledged, in_progress, resolved, closed.")]
    [InlineData("open,closed", "Invalid status 'open,closed'. Valid values: open, triaged, acknowledged, in_progress, resolved, closed.")]
    [InlineData("1", "Invalid status '1'. Valid values: open, triaged, acknowledged, in_progress, resolved, closed.")]
    [InlineData("99_", "Invalid status '99_'. Valid values: open, triaged, acknowledged, in_progress, resolved, closed.")]
    public async Task GetIssues_InvalidStatus_ReturnsBadRequestWithExactMessage(string value, string expected)
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store);

        var result = await facade.GetIssuesAsync(new IssueFilter(Status: value), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal(expected, result.Error);
        // No store query is issued for an invalid filter.
        Assert.Empty(store.Queries);
    }

    [Theory]
    [InlineData("not-a-type", "Invalid type 'not-a-type'. Valid values: code_quality, bug, suggestion, concern, workflow.")]
    [InlineData("bug,suggestion", "Invalid type 'bug,suggestion'. Valid values: code_quality, bug, suggestion, concern, workflow.")]
    [InlineData("1", "Invalid type '1'. Valid values: code_quality, bug, suggestion, concern, workflow.")]
    [InlineData("99_", "Invalid type '99_'. Valid values: code_quality, bug, suggestion, concern, workflow.")]
    public async Task GetIssues_InvalidType_ReturnsBadRequestWithExactMessage(string value, string expected)
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store);

        var result = await facade.GetIssuesAsync(new IssueFilter(Type: value), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal(expected, result.Error);
        Assert.Empty(store.Queries);
    }

    [Theory]
    [InlineData("invalid", "Invalid severity 'invalid'. Valid values: low, medium, high.")]
    [InlineData("low,high", "Invalid severity 'low,high'. Valid values: low, medium, high.")]
    [InlineData("1", "Invalid severity '1'. Valid values: low, medium, high.")]
    [InlineData("99_", "Invalid severity '99_'. Valid values: low, medium, high.")]
    public async Task GetIssues_InvalidSeverity_ReturnsBadRequestWithExactMessage(string value, string expected)
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store);

        var result = await facade.GetIssuesAsync(new IssueFilter(Severity: value), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal(expected, result.Error);
        Assert.Empty(store.Queries);
    }

    [Fact]
    public async Task GetIssues_CodeQualityAlias_MapsToCodeQuality()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("cq", type: IssueType.CodeQuality));
        var facade = CreateFacade(store);

        var result = await facade.GetIssuesAsync(new IssueFilter(Type: "codequality"), Ct);

        Assert.True(result.Success);
        var issue = Assert.Single(result.Value!);
        Assert.Equal("cq", issue.Id);
        var query = Assert.Single(store.Queries);
        Assert.Equal(IssueType.CodeQuality, query.Type);
    }

    [Fact]
    public async Task GetIssues_SnakeCaseStatus_NormalizedBeforeParse()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("in-progress-issue", status: IssueStatus.InProgress));
        var facade = CreateFacade(store);

        var result = await facade.GetIssuesAsync(new IssueFilter(Status: "in_progress"), Ct);

        Assert.True(result.Success);
        var issue = Assert.Single(result.Value!);
        Assert.Equal("in-progress-issue", issue.Id);
        var query = Assert.Single(store.Queries);
        Assert.Equal(IssueStatus.InProgress, query.Status);
    }

    [Fact]
    public async Task GetIssues_ReturnsLinkedIssueDtos()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("a", sourceGoalId: "goal-1", linkedGoalId: "goal-2"));
        var facade = CreateFacade(store);

        var result = await facade.GetIssuesAsync(new IssueFilter(), Ct);

        Assert.True(result.Success);
        var dto = Assert.Single(result.Value!);
        Assert.IsType<LinkedIssueDto>(dto);
        Assert.Equal("a", dto.Id);
        Assert.Equal("goal-1", dto.SourceGoalId);
        Assert.Equal("goal-2", dto.LinkedGoalId);
    }

    // ── GetIssueAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetIssue_Existing_ReturnsDto()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("found"));
        var facade = CreateFacade(store);

        var result = await facade.GetIssueAsync("found", Ct);

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.Equal("found", result.Value!.Id);
        Assert.Equal(Ct, Assert.Single(store.Tokens));
    }

    [Fact]
    public async Task GetIssue_UnknownId_ReturnsNotFound()
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store);

        var result = await facade.GetIssueAsync("missing", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Issue 'missing' not found.", result.Error);
    }

    // ── CreateIssueAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateIssue_ValidRequest_SucceedsAndPublishesIssueRaised()
    {
        var store = new RecordingIssueStore();
        var eventBus = new RecordingEventBus();
        var facade = CreateFacade(store, eventBus);

        var result = await facade.CreateIssueAsync(new CreateIssueRequest(
            Id: "new-issue", Type: IssueType.Bug, Title: "My issue", Description: "Desc"), Ct);

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.Equal("new-issue", result.Value!.Id);
        Assert.Equal("My issue", result.Value!.Title);
        Assert.Equal(IssueSeverity.Low, result.Value!.Severity); // default severity
        Assert.Equal(Ct, Assert.Single(store.Tokens));

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.IssueRaised, evt.Type);
        Assert.Equal("My issue", evt.Message);
        Assert.Null(evt.GoalId);
        Assert.Equal("new-issue", evt.IssueId);
        Assert.Null(evt.ReleaseId);
        Assert.Null(evt.Repository);
    }

    [Fact]
    public async Task CreateIssue_NullEventBus_SkipsPublicationSilently()
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store, eventBus: null);

        var result = await facade.CreateIssueAsync(new CreateIssueRequest(
            Id: "new-issue", Type: IssueType.Bug, Title: "My issue", Description: "Desc"), Ct);

        Assert.True(result.Success);
        Assert.Equal("new-issue", result.Value!.Id);
    }

    [Fact]
    public async Task CreateIssue_ValidationFailure_DoesNotPublishIssueRaised()
    {
        var store = new RecordingIssueStore();
        var eventBus = new RecordingEventBus();
        var facade = CreateFacade(store, eventBus);

        var result = await facade.CreateIssueAsync(new CreateIssueRequest(
            Id: "invalid id", Type: null, Title: " ", Description: " "), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Type is required.", result.Error);
        Assert.Empty(store.Tokens);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task CreateIssue_ValidationOrder_TypeFirst()
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store);

        // Every later rule also fails: only checking Type before Title, Description, and ID
        // can produce the frozen Type error.
        var result = await facade.CreateIssueAsync(new CreateIssueRequest(
            Id: "Bad ID!", Type: null, Title: " ", Description: " "), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Type is required.", result.Error);
        Assert.Empty(store.Tokens);
    }

    [Fact]
    public async Task CreateIssue_ValidationOrder_TitleSecond()
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store);

        // Description and ID are also invalid: Title must win after Type has passed.
        var result = await facade.CreateIssueAsync(new CreateIssueRequest(
            Id: "Bad ID!", Type: IssueType.Bug, Title: "  ", Description: " "), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Title is required.", result.Error);
        Assert.Empty(store.Tokens);
    }

    [Fact]
    public async Task CreateIssue_ValidationOrder_DescriptionThird()
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store);

        // ID is also invalid: Description must win after Type and Title have passed.
        var result = await facade.CreateIssueAsync(new CreateIssueRequest(
            Id: "Bad ID!", Type: IssueType.Bug, Title: "T", Description: " "), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Description is required.", result.Error);
        Assert.Empty(store.Tokens);
    }

    [Fact]
    public async Task CreateIssue_InvalidIdFormat_ReturnsBadRequest()
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store);

        var result = await facade.CreateIssueAsync(new CreateIssueRequest(
            Id: "Bad ID!", Type: IssueType.Bug, Title: "T", Description: "D"), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Invalid issue ID 'Bad ID!'. IDs must be lowercase kebab-case (letters, digits, hyphens).", result.Error);
        Assert.Empty(store.Tokens);
    }

    [Fact]
    public async Task CreateIssue_WhitespaceId_GeneratesIdFromTitle()
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store);

        var result = await facade.CreateIssueAsync(new CreateIssueRequest(
            Id: "   ", Type: IssueType.Bug, Title: "My Great Issue", Description: "D"), Ct);

        Assert.True(result.Success);
        Assert.Equal("my-great-issue", result.Value!.Id);
    }

    [Fact]
    public async Task CreateIssue_NullId_GeneratesIdFromTitle()
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store);

        var result = await facade.CreateIssueAsync(new CreateIssueRequest(
            Id: null, Type: IssueType.Bug, Title: "My Great Issue", Description: "D"), Ct);

        Assert.True(result.Success);
        Assert.Equal("my-great-issue", result.Value!.Id);
    }

    [Fact]
    public async Task CreateIssue_DuplicateId_ReturnsConflict()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("dup"));
        var eventBus = new RecordingEventBus();
        var facade = CreateFacade(store, eventBus);

        var result = await facade.CreateIssueAsync(new CreateIssueRequest(
            Id: "dup", Type: IssueType.Bug, Title: "T", Description: "D"), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Conflict, result.Kind);
        Assert.Equal("Issue 'dup' already exists.", result.Error);
        // No event is published for a failed create.
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task CreateIssue_NonDuplicateDbUpdateException_RethrowsOriginal()
    {
        // A non-duplicate persistence failure must NOT be converted to Conflict — it
        // propagates and becomes a 500, exactly as before the facade existed.
        var persistenceFailure = new DbUpdateException("database write failed");
        var store = new RecordingIssueStore { CreateThrows = persistenceFailure };
        var eventBus = new RecordingEventBus();
        var facade = CreateFacade(store, eventBus);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(
            () => facade.CreateIssueAsync(new CreateIssueRequest(
                Id: "new-issue", Type: IssueType.Bug, Title: "T", Description: "D"), Ct));

        Assert.Same(persistenceFailure, ex);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task CreateIssue_OtherExceptionType_Rethrows()
    {
        var store = new RecordingIssueStore
        {
            CreateThrows = new ArgumentException("bad argument"),
        };
        var facade = CreateFacade(store);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => facade.CreateIssueAsync(new CreateIssueRequest(
                Id: "new-issue", Type: IssueType.Bug, Title: "T", Description: "D"), Ct));

        Assert.Equal("bad argument", ex.Message);
    }

    // ── UpdateIssueAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateIssue_UnknownId_ReturnsNotFoundBeforeAnyValidation()
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store);

        // Both later validations fail, but existence is checked FIRST.
        var result = await facade.UpdateIssueAsync(
            "missing", new UpdateIssueRequest(Title: "  ", Description: " "), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Issue 'missing' not found.", result.Error);
        Assert.Equal(Ct, Assert.Single(store.Tokens));
    }

    [Fact]
    public async Task UpdateIssue_BlankTitle_ReturnsBadRequest()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("existing"));
        var facade = CreateFacade(store);

        // Description is also blank, proving Title validation runs first.
        var result = await facade.UpdateIssueAsync(
            "existing", new UpdateIssueRequest(Title: "  ", Description: " "), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Title is required.", result.Error);
        Assert.Equal(Ct, Assert.Single(store.Tokens));
    }

    [Fact]
    public async Task UpdateIssue_BlankDescription_ReturnsBadRequest()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("existing"));
        var facade = CreateFacade(store);

        var result = await facade.UpdateIssueAsync("existing", new UpdateIssueRequest(Description: " "), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Description is required.", result.Error);
        Assert.Equal(Ct, Assert.Single(store.Tokens));
    }

    [Fact]
    public async Task UpdateIssue_ValidUpdate_SucceedsAndRefetches()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("existing", status: IssueStatus.Open));
        var facade = CreateFacade(store);

        var result = await facade.UpdateIssueAsync("existing", new UpdateIssueRequest(Title: "New title"), Ct);

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.Equal("New title", result.Value!.Title);
        // Three store calls: the existence read, the update, and the post-update re-fetch.
        Assert.Equal(3, store.Tokens.Count);
        Assert.All(store.Tokens, t => Assert.Equal(Ct, t));
    }

    [Fact]
    public async Task UpdateIssue_TransitionToResolved_PublishesIssueResolved()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("existing", status: IssueStatus.Open));
        var eventBus = new RecordingEventBus();
        var facade = CreateFacade(store, eventBus);

        var result = await facade.UpdateIssueAsync("existing", new UpdateIssueRequest(Status: IssueStatus.Resolved), Ct);

        Assert.True(result.Success);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.IssueResolved, evt.Type);
        Assert.Equal("Issue 'existing' marked as Resolved", evt.Message);
        Assert.Null(evt.GoalId);
        Assert.Equal("existing", evt.IssueId);
        Assert.Null(evt.ReleaseId);
        Assert.Null(evt.Repository);
    }

    [Fact]
    public async Task UpdateIssue_TransitionToClosed_PublishesIssueResolved()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("existing", status: IssueStatus.Triaged));
        var eventBus = new RecordingEventBus();
        var facade = CreateFacade(store, eventBus);

        var result = await facade.UpdateIssueAsync("existing", new UpdateIssueRequest(Status: IssueStatus.Closed), Ct);

        Assert.True(result.Success);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.IssueResolved, evt.Type);
        Assert.Equal("Issue 'existing' marked as Closed", evt.Message);
        Assert.Null(evt.GoalId);
        Assert.Equal("existing", evt.IssueId);
        Assert.Null(evt.ReleaseId);
        Assert.Null(evt.Repository);
    }

    [Fact]
    public async Task UpdateIssue_ClosedToResolved_DoesNotPublishAgain()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("existing", status: IssueStatus.Closed));
        var eventBus = new RecordingEventBus();
        var facade = CreateFacade(store, eventBus);

        var result = await facade.UpdateIssueAsync("existing", new UpdateIssueRequest(Status: IssueStatus.Resolved), Ct);

        Assert.True(result.Success);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task UpdateIssue_NonTerminalStatus_DoesNotPublish()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("existing", status: IssueStatus.Open));
        var eventBus = new RecordingEventBus();
        var facade = CreateFacade(store, eventBus);

        var result = await facade.UpdateIssueAsync("existing", new UpdateIssueRequest(Status: IssueStatus.InProgress), Ct);

        Assert.True(result.Success);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task UpdateIssue_ResolvedWithLinkedGoalId_PublishesGoalId()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("existing", status: IssueStatus.Open, linkedGoalId: "goal-1"));
        var eventBus = new RecordingEventBus();
        var facade = CreateFacade(store, eventBus);

        var result = await facade.UpdateIssueAsync("existing", new UpdateIssueRequest(Status: IssueStatus.Resolved), Ct);

        Assert.True(result.Success);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal("goal-1", evt.GoalId);
    }

    [Fact]
    public async Task UpdateIssue_NullEventBus_SkipsPublicationSilently()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("existing", status: IssueStatus.Open));
        var facade = CreateFacade(store, eventBus: null);

        var result = await facade.UpdateIssueAsync("existing", new UpdateIssueRequest(Status: IssueStatus.Resolved), Ct);

        Assert.True(result.Success);
        Assert.Equal("existing", result.Value!.Id);
        Assert.Equal(IssueStatus.Resolved, result.Value.Status);
    }

    [Fact]
    public async Task UpdateIssue_StoreThrows_Rethrows()
    {
        var store = new RecordingIssueStore
        {
            UpdateThrows = new InvalidOperationException("store failure"),
        };
        store.AddIssue(MakeIssue("existing"));
        var facade = CreateFacade(store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => facade.UpdateIssueAsync("existing", new UpdateIssueRequest(Title: "New"), Ct));

        Assert.Equal("store failure", ex.Message);
    }

    [Fact]
    public async Task UpdateIssue_LinkedGoalIdTriState_ClearAndSet()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("existing", linkedGoalId: "goal-1"));
        var facade = CreateFacade(store);

        // Empty string clears the linked goal.
        var cleared = await facade.UpdateIssueAsync("existing", new UpdateIssueRequest(LinkedGoalId: ""), Ct);
        Assert.True(cleared.Success);
        Assert.Null(cleared.Value!.LinkedGoalId);

        // Non-empty sets it.
        var set = await facade.UpdateIssueAsync("existing", new UpdateIssueRequest(LinkedGoalId: "goal-2"), Ct);
        Assert.True(set.Success);
        Assert.Equal("goal-2", set.Value!.LinkedGoalId);
    }

    // ── DeleteIssueAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteIssue_Existing_ReturnsRemovedResult()
    {
        var store = new RecordingIssueStore();
        store.AddIssue(MakeIssue("doomed"));
        var facade = CreateFacade(store);

        var result = await facade.DeleteIssueAsync("doomed", Ct);

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.NotNull(result.Value);
        Assert.True(result.Value!.Removed);
        Assert.Equal(Ct, Assert.Single(store.Tokens));
    }

    [Fact]
    public async Task DeleteIssue_UnknownId_ReturnsNotFound()
    {
        var store = new RecordingIssueStore();
        var facade = CreateFacade(store);

        var result = await facade.DeleteIssueAsync("missing", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Issue 'missing' not found.", result.Error);
    }

    [Fact]
    public async Task DeleteIssue_StoreReportsNotDeleted_ReturnsNotFound()
    {
        var store = new RecordingIssueStore { DeleteReturnsFalse = true };
        var facade = CreateFacade(store);

        var result = await facade.DeleteIssueAsync("missing", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Issue 'missing' not found.", result.Error);
    }
}
