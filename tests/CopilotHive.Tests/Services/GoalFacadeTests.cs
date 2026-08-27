using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for <see cref="GoalFacade"/> — the facade the six goal endpoints and the
/// <c>Goals</c> / <c>GoalDetail</c> components use instead of touching the stores, the
/// dispatcher and the review service directly.
/// </summary>
/// <remarks>
/// The load-bearing contracts pinned down here mirror the pre-facade handlers exactly:
/// <list type="bullet">
///   <item>Delete accepts ONLY Draft/Failed goals and reports success through the NON-generic
///   <see cref="FacadeResult"/> (the route answers 204 with no body).</item>
///   <item>The status route's invalid-transition message is byte-for-byte frozen.</item>
///   <item>Review checks non-Draft BEFORE review-already-pending, so a goal that is both
///   reports <see cref="FacadeErrorKind.BadRequest"/> (400), never a 409.</item>
///   <item>Cancel rejects EVERY status other than InProgress/Pending — Draft included.</item>
///   <item>Extend-iterations checks dispatcher availability BEFORE the 1–100 range, so an
///   invalid count with an absent dispatcher still reports
///   <see cref="FacadeErrorKind.ServiceUnavailable"/>.</item>
///   <item>Side effects (dashboard notification, goal-ready notification, knowledge-document
///   cleanup, branch deletion) run EXACTLY once per operation.</item>
/// </list>
/// Synchronisation uses <see cref="TaskCompletionSource"/> gates only — there are no delays and
/// no timing-based assertions.
/// </remarks>
public sealed class GoalFacadeTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ── Fakes and builders ───────────────────────────────────────────────────

    /// <summary>Counts <see cref="DashboardNotifier.NotifyStateChanged"/> invocations.</summary>
    private sealed class NotificationCounter
    {
        public int Count { get; private set; }

        public DashboardNotifier Notifier { get; }

        public NotificationCounter()
        {
            Notifier = new DashboardNotifier();
            Notifier.OnStateChanged += () => Count++;
        }
    }

    /// <summary>
    /// Recording <see cref="IIssueStore"/> that answers the source-goal and linked-goal queries
    /// from separate lists and records exactly which filters it was asked for.
    /// </summary>
    private sealed class RecordingIssueStore : IIssueStore
    {
        private readonly IReadOnlyList<Issue> _bySourceGoal;
        private readonly IReadOnlyList<Issue> _byLinkedGoal;

        public RecordingIssueStore(IReadOnlyList<Issue> bySourceGoal, IReadOnlyList<Issue> byLinkedGoal)
        {
            _bySourceGoal = bySourceGoal;
            _byLinkedGoal = byLinkedGoal;
        }

        /// <summary>Every (sourceGoalId, linkedGoalId) pair the facade queried, in call order.</summary>
        public List<(string? SourceGoalId, string? LinkedGoalId)> Queries { get; } = [];

        /// <summary>Every cancellation token the facade forwarded, in call order.</summary>
        public List<CancellationToken> Tokens { get; } = [];

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Issue>>([]);

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null, IssueType? type = null, IssueSeverity? severity = null,
            string? repository = null, string? sourceGoalId = null, string? linkedGoalId = null,
            CancellationToken ct = default)
        {
            Queries.Add((sourceGoalId, linkedGoalId));
            Tokens.Add(ct);

            if (sourceGoalId is not null)
                return Task.FromResult(_bySourceGoal);
            if (linkedGoalId is not null)
                return Task.FromResult(_byLinkedGoal);
            return Task.FromResult<IReadOnlyList<Issue>>([]);
        }

        public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult<Issue?>(null);

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
            => Task.FromResult(issue);

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    /// <summary>
    /// In-memory <see cref="IGoalStore"/> that records the mutating calls the facade makes, so
    /// side effects can be asserted as exact counts.
    /// </summary>
    private sealed class RecordingGoalStore : IGoalStore
    {
        private readonly Dictionary<string, Goal> _goals = new();
        private readonly Dictionary<string, Release> _releases = new();

        public string Name => "recording-goal-store";

        /// <summary>Goal IDs passed to <see cref="ResetGoalIterationDataAsync"/>, in call order.</summary>
        public List<string> ResetCalls { get; } = [];

        /// <summary>(goalId, status) pairs passed to <see cref="UpdateGoalStatusAsync"/>, in call order.</summary>
        public List<(string GoalId, GoalStatus Status)> StatusUpdates { get; } = [];

        /// <summary>Goal IDs passed to <see cref="DeleteGoalAsync"/>, in call order.</summary>
        public List<string> DeleteCalls { get; } = [];

        /// <summary>Goals passed to <see cref="UpdateGoalAsync"/>, in call order.</summary>
        public List<Goal> GoalUpdates { get; } = [];

        /// <summary>When set, <see cref="DeleteGoalAsync"/> reports "not found" instead of deleting.</summary>
        public bool DeleteReturnsFalse { get; set; }

        /// <summary>
        /// When set, <see cref="GetGoalAsync"/> returns a DETACHED copy of the stored goal, as the
        /// real EF-backed store does. This matters for the concurrent-review test: without it a
        /// caller mutating its own instance would be visible to the next reader.
        /// </summary>
        public bool DetachGoalsOnRead { get; set; }

        public void AddGoal(Goal goal) => _goals[goal.Id] = goal;

        public void AddRelease(Release release) => _releases[release.Id] = release;

        private static Goal Detach(Goal goal) => new()
        {
            Id = goal.Id,
            Description = goal.Description,
            Priority = goal.Priority,
            Scope = goal.Scope,
            Status = goal.Status,
            RepositoryNames = [.. goal.RepositoryNames],
            TargetRepositoryNames = goal.TargetRepositoryNames,
            DependsOn = [.. goal.DependsOn],
            CreatedAt = goal.CreatedAt,
            StartedAt = goal.StartedAt,
            CompletedAt = goal.CompletedAt,
            Iterations = goal.Iterations,
            FailureReason = goal.FailureReason,
            Notes = [.. goal.Notes],
            ReleaseId = goal.ReleaseId,
            Documents = [.. goal.Documents],
            BranchCleanedUp = goal.BranchCleanedUp,
            ReviewStatus = goal.ReviewStatus,
        };

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList());

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default)
        {
            if (!_goals.TryGetValue(goalId, out var goal))
                return Task.FromResult<Goal?>(null);
            return Task.FromResult<Goal?>(DetachGoalsOnRead ? Detach(goal) : goal);
        }

        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            _goals[goal.Id] = goal;
            return Task.FromResult(goal);
        }

        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            GoalUpdates.Add(goal);
            _goals[goal.Id] = goal;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default)
        {
            DeleteCalls.Add(goalId);
            if (DeleteReturnsFalse)
                return Task.FromResult(false);
            return Task.FromResult(_goals.Remove(goalId));
        }

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IterationSummary>>([]);

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            StatusUpdates.Add((goalId, status));
            if (_goals.TryGetValue(goalId, out var goal))
                goal.Status = status;
            return Task.CompletedTask;
        }

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default)
        {
            _releases[release.Id] = release;
            return Task.FromResult(release);
        }

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult(_releases.TryGetValue(releaseId, out var release) ? release : null);

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Release>>(_releases.Values.ToList());

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default)
        {
            ResetCalls.Add(goalId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
    }

    /// <summary>Recording <see cref="IBrainRepoManager"/> that captures branch deletions.</summary>
    private sealed class RecordingRepoManager : IBrainRepoManager
    {
        /// <summary>(repoName, branchName) pairs passed to DeleteRemoteBranchAsync, in call order.</summary>
        public List<(string RepoName, string BranchName)> DeletedBranches { get; } = [];

        public string WorkDirectory => "/fake/work";

        public Task<string> EnsureCloneAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default)
            => Task.FromResult($"/fake/work/{repoName}");

        public Task<string> MergeFeatureBranchAsync(string repoName, string featureBranch, string defaultBranch, string commitMessage, CancellationToken ct = default)
            => Task.FromResult("fake-sha");

        public Task<BranchDeleteResult> DeleteRemoteBranchAsync(string repoName, string branchName, CancellationToken ct = default)
        {
            DeletedBranches.Add((repoName, branchName));
            return Task.FromResult(BranchDeleteResult.Success);
        }

        public string GetClonePath(string repoName) => $"/fake/work/{repoName}";

        public Task<string?> GetHeadShaAsync(string repoName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> MergeBranchAsync(string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<bool> CreateTagAsync(string repoName, string tag, string branch, string message, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> DeleteTagAsync(string repoName, string tag, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<List<string>> ListRemoteBranchesAsync(string repoName, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
    }

    /// <summary>
    /// <see cref="IChatClient"/> that returns a fixed assistant reply, used to drive a real
    /// <see cref="GoalReviewService"/> without any network access.
    /// </summary>
    private sealed class StubChatClient(string reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
            {
                FinishReason = ChatFinishReason.Stop,
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Streaming is not used by the review agent in these tests.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// <see cref="IChatClient"/> that signals when it is entered and then blocks until the
    /// supplied task completes. Used as a TCS gate to hold a review "in progress" while a
    /// second review is attempted — no delays, no timing assumptions.
    /// </summary>
    private sealed class BlockingChatClient(Task<string> release, TaskCompletionSource<bool> entered) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult(true);
            var reply = await release.WaitAsync(cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Streaming is not used by the review agent in these tests.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private const string ApprovedReviewJson =
        """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"Looks good"}""";

    /// <summary>Creates a real <see cref="GoalReviewService"/> driven by a stub chat client.</summary>
    private GoalReviewService CreateReviewService(
        Func<string, IChatClient>? chatClientFactory = null,
        IGoalStore? goalStore = null)
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"goal-facade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        _tempDirs.Add(stateDir);

        return new GoalReviewService(
            knowledgeGraph: null,
            configRepo: null,
            config: new HiveConfigFile
            {
                Workers = { ["reviewer"] = new WorkerConfig { Model = "reviewer-model" } },
            },
            goalStore: goalStore,
            brainRepoManager: null,
            stateDir: stateDir,
            logger: NullLogger<GoalReviewService>.Instance,
            chatClientFactory: chatClientFactory ?? (_ => new StubChatClient(ApprovedReviewJson)));
    }

    /// <summary>
    /// Creates a real <see cref="GoalDispatcher"/> over fakes. A real dispatcher (rather than a
    /// stub) keeps <c>ResumeGoalAsync</c> / <c>CancelGoalAsync</c> behaviour authentic.
    /// </summary>
    private static GoalDispatcher CreateDispatcher(IGoalStore goalStore, DashboardNotifier? dashboardNotifier = null)
    {
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        return new GoalDispatcher(
            goalManager,
            new GoalPipelineManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            goalStore: goalStore,
            dashboardNotifier: dashboardNotifier);
    }

    private GoalFacade CreateFacade(
        RecordingGoalStore goalStore,
        IIssueStore? issueStore = null,
        GoalReviewService? reviewService = null,
        DashboardNotifier? dashboardNotifier = null,
        IBrainRepoManager? repoManager = null,
        KnowledgeDocumentCleanupService? docCleanup = null,
        GoalReadyNotifier? goalReadyNotifier = null,
        GoalDispatcher? dispatcher = null)
        => new(
            goalStore,
            issueStore ?? new RecordingIssueStore([], []),
            reviewService ?? CreateReviewService(),
            dashboardNotifier ?? new DashboardNotifier(),
            repoManager,
            docCleanup,
            goalReadyNotifier,
            dispatcher,
            NullLogger<GoalFacade>.Instance);

    private static Goal MakeGoal(
        string id = "goal-1",
        GoalStatus status = GoalStatus.Draft,
        ReviewStatus reviewStatus = ReviewStatus.None,
        List<string>? repositoryNames = null,
        string? failureReason = null)
        => new()
        {
            Id = id,
            Description = "Test goal",
            Status = status,
            ReviewStatus = reviewStatus,
            RepositoryNames = repositoryNames ?? [],
            FailureReason = failureReason,
        };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── DeleteGoalAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteGoal_UnknownId_ReturnsNotFound()
    {
        var store = new RecordingGoalStore();
        var facade = CreateFacade(store);

        var result = await facade.DeleteGoalAsync("missing");

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Goal 'missing' not found.", result.Error);
        // The unknown-id check runs BEFORE any delete attempt.
        Assert.Empty(store.DeleteCalls);
    }

    [Theory]
    [InlineData(GoalStatus.Pending)]
    [InlineData(GoalStatus.InProgress)]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Cancelled)]
    public async Task DeleteGoal_NonDraftOrFailedGoal_ReturnsBadRequest(GoalStatus status)
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: status));
        var counter = new NotificationCounter();
        var facade = CreateFacade(store, dashboardNotifier: counter.Notifier);

        var result = await facade.DeleteGoalAsync("goal-1");

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Only Draft or Failed goals can be deleted", result.Error);
        // A rejected delete performs no store delete and fires no notification.
        Assert.Empty(store.DeleteCalls);
        Assert.Equal(0, counter.Count);
    }

    [Theory]
    [InlineData(GoalStatus.Draft)]
    [InlineData(GoalStatus.Failed)]
    public async Task DeleteGoal_DraftOrFailedGoal_Succeeds(GoalStatus status)
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: status));
        var counter = new NotificationCounter();
        var facade = CreateFacade(store, dashboardNotifier: counter.Notifier);

        var result = await facade.DeleteGoalAsync("goal-1");

        // Success is the NON-generic FacadeResult — the route maps it to 204 with no body.
        Assert.IsType<FacadeResult>(result);
        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.Null(result.Error);
        Assert.Equal(["goal-1"], store.DeleteCalls);
        // The dashboard notification runs EXACTLY once.
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task DeleteGoal_StoreReportsNotDeleted_ReturnsNotFoundAndDoesNotNotify()
    {
        var store = new RecordingGoalStore { DeleteReturnsFalse = true };
        store.AddGoal(MakeGoal(status: GoalStatus.Draft));
        var counter = new NotificationCounter();
        var facade = CreateFacade(store, dashboardNotifier: counter.Notifier);

        var result = await facade.DeleteGoalAsync("goal-1");

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task DeleteGoal_FailedGoal_DeletesFeatureBranchOncePerRepository()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Failed, repositoryNames: ["RepoA", "RepoB"]));
        var repoManager = new RecordingRepoManager();
        var facade = CreateFacade(store, repoManager: repoManager);

        var result = await facade.DeleteGoalAsync("goal-1");

        Assert.True(result.Success);
        Assert.Equal(
            [("RepoA", "copilothive/goal-1"), ("RepoB", "copilothive/goal-1")],
            repoManager.DeletedBranches);
    }

    [Fact]
    public async Task DeleteGoal_DraftGoal_DoesNotDeleteFeatureBranches()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Draft, repositoryNames: ["RepoA"]));
        var repoManager = new RecordingRepoManager();
        var facade = CreateFacade(store, repoManager: repoManager);

        var result = await facade.DeleteGoalAsync("goal-1");

        Assert.True(result.Success);
        // Branch cleanup is a Failed-goal-only side effect.
        Assert.Empty(repoManager.DeletedBranches);
    }

    [Fact]
    public async Task DeleteGoal_RunsKnowledgeDocumentCleanupExactlyOnce()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Draft));

        // A real cleanup service over an in-memory knowledge graph: the progress/review docs for
        // the goal must be gone afterwards, proving the cleanup actually ran.
        var graph = new KnowledgeGraph();
        await graph.CreateDocumentAsync(
            "progress-goal-1", "Progress", DocumentType.Scratch, "content", topic: "progress", ct: Ct);
        await graph.CreateDocumentAsync(
            "review-goal-1", "Review", DocumentType.Scratch, "content", topic: "review", ct: Ct);
        await graph.CreateDocumentAsync(
            "progress-other", "Other", DocumentType.Scratch, "content", topic: "progress", ct: Ct);

        var cleanup = new KnowledgeDocumentCleanupService(
            graph, NullLogger<KnowledgeDocumentCleanupService>.Instance);
        var facade = CreateFacade(store, docCleanup: cleanup);

        var result = await facade.DeleteGoalAsync("goal-1");

        Assert.True(result.Success);
        Assert.Null(graph.GetDocument("progress-goal-1"));
        Assert.Null(graph.GetDocument("review-goal-1"));
        // Only this goal's documents are swept.
        Assert.NotNull(graph.GetDocument("progress-other"));
    }

    // ── UpdateGoalStatusAsync ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateGoalStatus_UnknownId_ReturnsNotFound()
    {
        var store = new RecordingGoalStore();
        var facade = CreateFacade(store);

        var result = await facade.UpdateGoalStatusAsync("missing", "Pending");

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Goal 'missing' not found.", result.Error);
    }

    [Fact]
    public async Task UpdateGoalStatus_InvalidTransition_ReturnsExactFrozenMessage()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Completed));
        var facade = CreateFacade(store);

        var result = await facade.UpdateGoalStatusAsync("goal-1", "Pending");

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal(
            "Invalid transition from Completed to Pending. Only Draft→Pending, Pending→Draft, and Failed→Draft are allowed.",
            result.Error);
        // A rejected transition performs no status write.
        Assert.Empty(store.StatusUpdates);
    }

    [Fact]
    public async Task UpdateGoalStatus_UnparsableStatus_ReturnsBadRequest()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Draft));
        var facade = CreateFacade(store);

        var result = await facade.UpdateGoalStatusAsync("goal-1", "NotAStatus");

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Invalid status 'NotAStatus'.", result.Error);
    }

    [Fact]
    public async Task UpdateGoalStatus_DraftToPending_SucceedsAndNotifiesGoalReadyOnce()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Draft));
        var counter = new NotificationCounter();
        var readyNotifier = new GoalReadyNotifier();
        var facade = CreateFacade(store, dashboardNotifier: counter.Notifier, goalReadyNotifier: readyNotifier);

        var result = await facade.UpdateGoalStatusAsync("goal-1", "Pending");

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal("goal-1", result.Value!.Id);
        Assert.Equal(GoalStatus.Pending, result.Value.Status);
        Assert.Equal([("goal-1", GoalStatus.Pending)], store.StatusUpdates);
        Assert.Equal(1, counter.Count);

        // The goal-ready signal was raised EXACTLY once: the first wait observes it, the
        // second (with an already-cancelled token treated as a non-signal) does not. The
        // semaphore is drained without any delay.
        Assert.True(await readyNotifier.WaitForSignalAsync(TimeSpan.Zero, Ct));
        Assert.False(await readyNotifier.WaitForSignalAsync(TimeSpan.Zero, Ct));
    }

    [Fact]
    public async Task UpdateGoalStatus_PendingToDraft_DoesNotSignalGoalReady()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Pending));
        var readyNotifier = new GoalReadyNotifier();
        var facade = CreateFacade(store, goalReadyNotifier: readyNotifier);

        var result = await facade.UpdateGoalStatusAsync("goal-1", "Draft");

        Assert.True(result.Success);
        // Only a transition INTO Pending wakes the dispatcher.
        Assert.False(await readyNotifier.WaitForSignalAsync(TimeSpan.Zero, Ct));
    }

    [Fact]
    public async Task UpdateGoalStatus_FailedToDraft_ResetsIterationDataAndDeletesBranchesOnce()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Failed, repositoryNames: ["RepoA", "RepoB"]));
        var repoManager = new RecordingRepoManager();
        var counter = new NotificationCounter();
        var facade = CreateFacade(store, repoManager: repoManager, dashboardNotifier: counter.Notifier);

        var result = await facade.UpdateGoalStatusAsync("goal-1", "Draft");

        Assert.True(result.Success);
        // Each Failed→Draft side effect runs exactly once.
        Assert.Equal(["goal-1"], store.ResetCalls);
        Assert.Equal(
            [("RepoA", "copilothive/goal-1"), ("RepoB", "copilothive/goal-1")],
            repoManager.DeletedBranches);
        Assert.Equal([("goal-1", GoalStatus.Draft)], store.StatusUpdates);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task UpdateGoalStatus_DraftToPending_DoesNotResetIterationData()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Draft, repositoryNames: ["RepoA"]));
        var repoManager = new RecordingRepoManager();
        var facade = CreateFacade(store, repoManager: repoManager);

        var result = await facade.UpdateGoalStatusAsync("goal-1", "Pending");

        Assert.True(result.Success);
        // The reset + branch cleanup belong to Failed→Draft only.
        Assert.Empty(store.ResetCalls);
        Assert.Empty(repoManager.DeletedBranches);
    }

    // ── RequestReviewAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task RequestReview_UnknownId_ReturnsNotFound()
    {
        var store = new RecordingGoalStore();
        var facade = CreateFacade(store);

        var result = await facade.RequestReviewAsync("missing", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Goal 'missing' not found.", result.Error);
    }

    [Theory]
    [InlineData(GoalStatus.Pending)]
    [InlineData(GoalStatus.InProgress)]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Failed)]
    [InlineData(GoalStatus.Cancelled)]
    public async Task RequestReview_NonDraftGoal_ReturnsBadRequest(GoalStatus status)
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: status));
        var facade = CreateFacade(store);

        var result = await facade.RequestReviewAsync("goal-1", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Only Draft goals can be reviewed.", result.Error);
    }

    [Fact]
    public async Task RequestReview_DraftGoalWithPendingReview_ReturnsConflict()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Draft, reviewStatus: ReviewStatus.Pending));
        var facade = CreateFacade(store);

        var result = await facade.RequestReviewAsync("goal-1", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Conflict, result.Kind);
        Assert.Equal("A review is already in progress for this goal.", result.Error);
    }

    /// <summary>
    /// ORDER PROOF: the non-Draft check runs BEFORE the review-already-pending check. The goal
    /// here satisfies BOTH rejection conditions; the 400 must win. If the order were flipped
    /// this returns <see cref="FacadeErrorKind.Conflict"/> and the test fails.
    /// </summary>
    [Fact]
    public async Task RequestReview_NonDraftAndReviewPending_ReturnsBadRequestNotConflict()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Completed, reviewStatus: ReviewStatus.Pending));
        var facade = CreateFacade(store);

        var result = await facade.RequestReviewAsync("goal-1", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Only Draft goals can be reviewed.", result.Error);
    }

    [Fact]
    public async Task RequestReview_DraftGoal_ReturnsVerdict()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Draft));
        var facade = CreateFacade(store, reviewService: CreateReviewService());

        var result = await facade.RequestReviewAsync("goal-1", Ct);

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.NotNull(result.Value);
        Assert.Equal("Approved", result.Value!.Verdict);
        Assert.Equal("Looks good", result.Value.Summary);
    }

    /// <summary>
    /// The <see cref="GoalReviewService"/>'s OWN concurrency guard (an
    /// <see cref="InvalidOperationException"/> thrown when a review is already running for the
    /// goal) maps to <see cref="FacadeErrorKind.Conflict"/>. A TCS gate holds the first review
    /// inside the agent call while the second is attempted — no delays.
    /// </summary>
    [Fact]
    public async Task RequestReview_ServiceReportsConcurrentReview_ReturnsConflict()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // goalStore is null on the review service so the Pending status is never persisted —
        // the facade's own ReviewStatus check therefore passes and the SERVICE-level guard is
        // what rejects the second review.
        var reviewService = CreateReviewService(_ => new BlockingChatClient(release.Task, entered));

        // Detached reads mirror the real EF-backed store: the first caller's in-memory mutation
        // of ReviewStatus is NOT visible to the facade's subsequent read, so the rejection can
        // only come from the review service's own concurrency guard.
        var store = new RecordingGoalStore { DetachGoalsOnRead = true };
        store.AddGoal(MakeGoal(status: GoalStatus.Draft));
        var facade = CreateFacade(store, reviewService: reviewService);

        // First review: blocks inside the agent call once the gate is entered.
        var goal = await store.GetGoalAsync("goal-1", Ct);
        Assert.NotNull(goal);
        var firstReview = reviewService.ReviewGoalAsync(goal!, Ct);
        await entered.Task;

        // Second review through the facade hits the service's in-process guard.
        var result = await facade.RequestReviewAsync("goal-1", Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Conflict, result.Kind);
        Assert.Equal("A review is already in progress for goal goal-1", result.Error);

        // Release the gate so the first review completes cleanly.
        release.SetResult(ApprovedReviewJson);
        await firstReview;
    }

    // ── CancelGoalAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task CancelGoal_UnknownId_ReturnsNotFound()
    {
        var store = new RecordingGoalStore();
        var facade = CreateFacade(store, dispatcher: CreateDispatcher(store));

        var result = await facade.CancelGoalAsync("missing");

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Goal 'missing' not found.", result.Error);
    }

    /// <summary>
    /// Cancel rejects EVERY status other than InProgress/Pending — Draft and the terminal
    /// statuses included — with the exact message.
    /// </summary>
    [Theory]
    [InlineData(GoalStatus.Draft)]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Failed)]
    [InlineData(GoalStatus.Cancelled)]
    public async Task CancelGoal_NonCancellableStatus_ReturnsBadRequestWithExactMessage(GoalStatus status)
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: status));
        var facade = CreateFacade(store, dispatcher: CreateDispatcher(store));

        var result = await facade.CancelGoalAsync("goal-1");

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal(
            $"Goal 'goal-1' is {status} and cannot be cancelled. Only InProgress or Pending goals can be cancelled.",
            result.Error);
    }

    [Fact]
    public async Task CancelGoal_PendingGoal_SucceedsWithExactMessage()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Pending));
        var facade = CreateFacade(store, dispatcher: CreateDispatcher(store));

        var result = await facade.CancelGoalAsync("goal-1");

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal("Goal 'goal-1' has been cancelled.", result.Value!.Message);
    }

    /// <summary>
    /// The dispatcher-uncancellable case: the goal passes the status gate but the dispatcher
    /// declines (here because it cannot resolve the goal through its own goal manager), which
    /// reports the "could not be cancelled" message as a 400.
    /// </summary>
    [Fact]
    public async Task CancelGoal_DispatcherDeclines_ReturnsBadRequestWithExactMessage()
    {
        var facadeStore = new RecordingGoalStore();
        facadeStore.AddGoal(MakeGoal(status: GoalStatus.InProgress));

        // The dispatcher is backed by a DIFFERENT (empty) store, so it has neither a pipeline
        // nor a known goal and returns false.
        var dispatcher = CreateDispatcher(new RecordingGoalStore());
        var facade = CreateFacade(facadeStore, dispatcher: dispatcher);

        var result = await facade.CancelGoalAsync("goal-1");

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal(
            "Goal 'goal-1' could not be cancelled (it may have already completed or failed).",
            result.Error);
    }

    // ── ExtendIterationsAsync ────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public async Task ExtendIterations_OutOfRange_ReturnsBadRequest(int additionalIterations)
    {
        var store = new RecordingGoalStore();
        var facade = CreateFacade(store, dispatcher: CreateDispatcher(store));

        var result = await facade.ExtendIterationsAsync("goal-1", additionalIterations, Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("additionalIterations must be between 1 and 100.", result.Error);
    }

    [Fact]
    public async Task ExtendIterations_NoDispatcher_ReturnsServiceUnavailable()
    {
        var store = new RecordingGoalStore();
        var facade = CreateFacade(store, dispatcher: null);

        var result = await facade.ExtendIterationsAsync("goal-1", 5, Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.ServiceUnavailable, result.Kind);
    }

    /// <summary>
    /// ORDER PROOF: the dispatcher-availability check runs BEFORE the range check. With an
    /// absent dispatcher AND an out-of-range count the result must still be
    /// <see cref="FacadeErrorKind.ServiceUnavailable"/> — this is what keeps the route's bare
    /// bodyless 503 reachable for an invalid <c>additionalIterations</c>.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task ExtendIterations_NoDispatcherAndInvalidCount_ReturnsServiceUnavailableNotBadRequest(
        int additionalIterations)
    {
        var store = new RecordingGoalStore();
        var facade = CreateFacade(store, dispatcher: null);

        var result = await facade.ExtendIterationsAsync("goal-1", additionalIterations, Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.ServiceUnavailable, result.Kind);
    }

    [Fact]
    public async Task ExtendIterations_GoalNotResumable_ReturnsNotFound()
    {
        var store = new RecordingGoalStore();
        var facade = CreateFacade(store, dispatcher: CreateDispatcher(store));

        var result = await facade.ExtendIterationsAsync("goal-1", 5, Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Goal 'goal-1' or its pipeline not found.", result.Error);
    }

    /// <summary>
    /// Extend-iterations goes through <see cref="GoalDispatcher.ResumeGoalAsync"/>, NOT an
    /// <see cref="IGoalStore"/> call. A goal that would satisfy any store-level path still
    /// yields the dispatcher's answer, and the store records no mutation whatsoever.
    /// </summary>
    [Fact]
    public async Task ExtendIterations_RoutesThroughDispatcherResume_NotTheGoalStore()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(
            status: GoalStatus.Failed,
            failureReason: "Exceeded max iterations"));
        var facade = CreateFacade(store, dispatcher: CreateDispatcher(store));

        // The goal IS iteration-exhaustion eligible, so ResumeGoalAsync gets past its first
        // gate and fails only on the missing pipeline — proving the dispatcher ran.
        var result = await facade.ExtendIterationsAsync("goal-1", 5, Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);

        // No IGoalStore mutation was performed by the facade for this operation.
        Assert.Empty(store.StatusUpdates);
        Assert.Empty(store.ResetCalls);
        Assert.Empty(store.GoalUpdates);
        Assert.Empty(store.DeleteCalls);
    }

    // ── AttachReleaseAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task AttachRelease_UnknownRelease_ReturnsNotFound()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Completed));
        var counter = new NotificationCounter();
        var facade = CreateFacade(store, dashboardNotifier: counter.Notifier);

        var result = await facade.AttachReleaseAsync("goal-1", "missing-release");

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Release 'missing-release' not found.", result.Error);
        // The release check runs FIRST: the goal is never updated and nothing is notified.
        Assert.Empty(store.GoalUpdates);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task AttachRelease_UnknownGoal_ReturnsNotFound()
    {
        var store = new RecordingGoalStore();
        store.AddRelease(new Release { Id = "rel-1", Tag = "v1.0.0" });
        var facade = CreateFacade(store);

        var result = await facade.AttachReleaseAsync("missing", "rel-1");

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Goal 'missing' not found.", result.Error);
        Assert.Empty(store.GoalUpdates);
    }

    [Fact]
    public async Task AttachRelease_KnownReleaseAndGoal_AttachesAndNotifiesOnce()
    {
        var store = new RecordingGoalStore();
        store.AddGoal(MakeGoal(status: GoalStatus.Completed));
        store.AddRelease(new Release { Id = "rel-1", Tag = "v1.0.0" });
        var counter = new NotificationCounter();
        var facade = CreateFacade(store, dashboardNotifier: counter.Notifier);

        var result = await facade.AttachReleaseAsync("goal-1", "rel-1");

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal("rel-1", result.Value!.ReleaseId);
        // The goal is persisted exactly once and the dashboard is notified exactly once.
        Assert.Single(store.GoalUpdates);
        Assert.Equal("rel-1", store.GoalUpdates[0].ReleaseId);
        Assert.Equal(1, counter.Count);
    }

    // ── GetLinkedIssuesAsync ─────────────────────────────────────────────────

    private static Issue MakeIssue(string id, string? sourceGoalId = null, string? linkedGoalId = null)
        => new()
        {
            Id = id,
            Title = $"Title {id}",
            Description = "desc",
            Type = IssueType.Bug,
            Severity = IssueSeverity.Medium,
            Status = IssueStatus.Open,
            SourceGoalId = sourceGoalId,
            LinkedGoalId = linkedGoalId,
        };

    [Fact]
    public async Task GetLinkedIssues_PerformsBothQueriesWithTheRespectiveFilter()
    {
        var issueStore = new RecordingIssueStore([MakeIssue("a", sourceGoalId: "goal-1")], [MakeIssue("b", linkedGoalId: "goal-1")]);
        var facade = CreateFacade(new RecordingGoalStore(), issueStore: issueStore);

        var result = await facade.GetLinkedIssuesAsync("goal-1", Ct);

        Assert.True(result.Success);
        // Exactly two queries: one filtered by source goal, one by linked goal.
        Assert.Equal(2, issueStore.Queries.Count);
        Assert.Equal(("goal-1", null), issueStore.Queries[0]);
        Assert.Equal((null, "goal-1"), issueStore.Queries[1]);
    }

    [Fact]
    public async Task GetLinkedIssues_IssueInBothQueries_AppearsOnce()
    {
        var issueStore = new RecordingIssueStore(
            [MakeIssue("dup", sourceGoalId: "goal-1"), MakeIssue("only-source", sourceGoalId: "goal-1")],
            [MakeIssue("dup", linkedGoalId: "goal-1"), MakeIssue("only-linked", linkedGoalId: "goal-1")]);
        var facade = CreateFacade(new RecordingGoalStore(), issueStore: issueStore);

        var result = await facade.GetLinkedIssuesAsync("goal-1", Ct);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        var ids = result.Value!.Select(i => i.Id).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Equal(1, ids.Count(i => i == "dup"));
        Assert.Contains("only-source", ids);
        Assert.Contains("only-linked", ids);
    }

    [Fact]
    public async Task GetLinkedIssues_DuplicateId_KeepsTheSourceGoalOccurrence()
    {
        // DistinctBy keeps the FIRST occurrence, and the source-goal query runs first.
        var sourceIssue = MakeIssue("dup", sourceGoalId: "goal-1");
        var linkedIssue = new Issue
        {
            Id = "dup",
            Title = "Different title",
            Description = "other",
            Type = IssueType.Suggestion,
            Severity = IssueSeverity.High,
            Status = IssueStatus.Closed,
            LinkedGoalId = "goal-1",
        };
        var issueStore = new RecordingIssueStore([sourceIssue], [linkedIssue]);
        var facade = CreateFacade(new RecordingGoalStore(), issueStore: issueStore);

        var result = await facade.GetLinkedIssuesAsync("goal-1", Ct);

        Assert.True(result.Success);
        var only = Assert.Single(result.Value!);
        Assert.Equal("Title dup", only.Title);
        Assert.Equal(IssueType.Bug, only.Type);
    }

    [Fact]
    public async Task GetLinkedIssues_NoMatches_ReturnsEmptySuccess()
    {
        var issueStore = new RecordingIssueStore([], []);
        var facade = CreateFacade(new RecordingGoalStore(), issueStore: issueStore);

        var result = await facade.GetLinkedIssuesAsync("goal-1", Ct);

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.NotNull(result.Value);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task GetLinkedIssues_ProjectsTheFullIssueShape()
    {
        var created = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var issue = new Issue
        {
            Id = "full",
            Title = "A title",
            Description = "A description",
            Type = IssueType.CodeQuality,
            Severity = IssueSeverity.High,
            Status = IssueStatus.Triaged,
            RepositoryNames = ["RepoA"],
            SourceGoalId = "goal-1",
            SourceRole = "reviewer",
            SourceIteration = 3,
            CreatedAt = created,
            UpdatedAt = created.AddHours(1),
            ResolvedAt = created.AddHours(2),
            LinkedGoalId = "goal-2",
        };
        var issueStore = new RecordingIssueStore([issue], []);
        var facade = CreateFacade(new RecordingGoalStore(), issueStore: issueStore);

        var result = await facade.GetLinkedIssuesAsync("goal-1", Ct);

        var dto = Assert.Single(result.Value!);
        // Every field of the issues-API shape is carried — not a five-field projection.
        Assert.Equal("full", dto.Id);
        Assert.Equal(IssueType.CodeQuality, dto.Type);
        Assert.Equal("A title", dto.Title);
        Assert.Equal("A description", dto.Description);
        Assert.Equal(IssueSeverity.High, dto.Severity);
        Assert.Equal(IssueStatus.Triaged, dto.Status);
        Assert.Equal(["RepoA"], dto.RepositoryNames);
        Assert.Equal("goal-1", dto.SourceGoalId);
        Assert.Equal("reviewer", dto.SourceRole);
        Assert.Equal(3, dto.SourceIteration);
        Assert.Equal(created, dto.CreatedAt);
        Assert.Equal(created.AddHours(1), dto.UpdatedAt);
        Assert.Equal(created.AddHours(2), dto.ResolvedAt);
        Assert.Equal("goal-2", dto.LinkedGoalId);
    }

    [Fact]
    public async Task GetLinkedIssues_ForwardsTheCallersTokenToBothQueries()
    {
        var issueStore = new RecordingIssueStore([], []);
        var facade = CreateFacade(new RecordingGoalStore(), issueStore: issueStore);
        using var cts = new CancellationTokenSource();

        await facade.GetLinkedIssuesAsync("goal-1", cts.Token);

        Assert.Equal(2, issueStore.Tokens.Count);
        Assert.All(issueStore.Tokens, t => Assert.Equal(cts.Token, t));
    }
}
