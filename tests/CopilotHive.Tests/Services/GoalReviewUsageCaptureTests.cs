using System.Runtime.CompilerServices;

using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder;

namespace CopilotHive.Tests.Services;

/// <summary>
/// The pre-execution review capture-site contract: every completed
/// <see cref="GoalReviewService.ReviewGoalAsync"/> run records its <c>AgentResult.TokenUsage</c>
/// against the reviewed goal as <see cref="GoalUsageKind.PreExecutionReview"/>, exactly once, BEFORE
/// the error check (so an Error-status result records too), best-effort (a throwing store changes
/// nothing about the verdict, <see cref="Goal.ReviewStatus"/>, the review document or the returned
/// <see cref="ReviewResult"/>).
/// </summary>
public sealed class GoalReviewUsageCaptureTests
{
    private const string ApprovedReply =
        """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"Looks good"}""";

    private const string NeedsChangesReply =
        """{"verdict":"NeedsChanges","issues":[{"severity":"MAJOR","description":"File does not exist"}],"verified":[],"recommendation":"Fix the path"}""";

    private static Goal NewGoal(string id = "goal-1") => new()
    {
        Id = id,
        Description = "Add a null-guard to the Foo.Bar method in src/Foo.cs.",
        ReviewStatus = ReviewStatus.None,
    };

    private static HiveConfigFile ConfigWithReviewerModel() => new()
    {
        Workers = { ["reviewer"] = new WorkerConfig { Model = "reviewer-model" } },
    };

    private static GoalReviewService CreateService(
        IChatClient client,
        IGoalStore? goalStore,
        KnowledgeGraph? knowledgeGraph = null,
        string? stateDir = null,
        ILogger<GoalReviewService>? logger = null)
    {
        stateDir ??= Path.Combine(Path.GetTempPath(), "goal-review-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDir);
        return new GoalReviewService(
            knowledgeGraph,
            configRepo: null,
            config: ConfigWithReviewerModel(),
            goalStore,
            brainRepoManager: null,
            stateDir: stateDir,
            logger: logger ?? NullLogger<GoalReviewService>.Instance,
            chatClientFactory: _ => client);
    }

    /// <summary>An approved review records exactly ONE PreExecutionReview add with that run's usage.</summary>
    [Fact]
    public async Task ReviewGoalAsync_Approved_RecordsExactlyOneReviewAddWithItsUsage()
    {
        var goal = NewGoal();
        var store = new RecordingUsageStore();
        var client = new UsageReportingChatClient(ApprovedReply);
        var service = CreateService(client, store);

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        // The verdict path is unchanged…
        Assert.Equal("Approved", result.Verdict);
        Assert.Equal(ReviewStatus.Approved, goal.ReviewStatus);

        // …and exactly one usage add was recorded, for the reviewed goal, of the review kind.
        var recorded = Assert.Single(store.Usages);
        Assert.Equal("goal-1", recorded.GoalId);
        Assert.Equal(GoalUsageKind.PreExecutionReview, recorded.Kind);

        var entry = Assert.Single(recorded.Usage.Entries);
        Assert.Equal(UsageSource.Agent, entry.Source);
        Assert.Equal(UsageReportingChatClient.InputPerCall, entry.Usage.InputTokens);
        Assert.Equal(UsageReportingChatClient.OutputPerCall, entry.Usage.OutputTokens);
        Assert.Equal(UsageReportingChatClient.CachedPerCall, entry.Usage.CachedInputTokens);
        Assert.Equal(1, entry.Usage.Calls);
        Assert.Equal(1, entry.Usage.CachedInputReportedCalls);
    }

    /// <summary>A NeedsChanges review records its usage too — the capture is not tied to a verdict.</summary>
    [Fact]
    public async Task ReviewGoalAsync_NeedsChanges_RecordsExactlyOneReviewAddWithItsUsage()
    {
        var goal = NewGoal();
        var store = new RecordingUsageStore();
        var service = CreateService(new UsageReportingChatClient(NeedsChangesReply), store);

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        Assert.Equal("NeedsChanges", result.Verdict);
        Assert.Contains("File does not exist", result.Issues);
        Assert.Equal(ReviewStatus.NeedsChanges, goal.ReviewStatus);

        var recorded = Assert.Single(store.Usages);
        Assert.Equal(GoalUsageKind.PreExecutionReview, recorded.Kind);
        Assert.Equal(UsageReportingChatClient.InputPerCall, recorded.Usage.Total.InputTokens);
    }

    /// <summary>
    /// An Error-status result records the usage of the calls that ran, BEFORE the error check turns
    /// the run into the failed-review outcome (NeedsChanges with a "Review failed: …" issue). The
    /// failure handling itself must be unchanged.
    /// </summary>
    [Fact]
    public async Task ReviewGoalAsync_ErrorStatusResult_RecordsUsageAndKeepsTheFailedReviewOutcome()
    {
        var goal = NewGoal();
        var store = new RecordingUsageStore();
        // Round 1 reports usage, round 2 throws a NON-HTTP fault → SharpCoder returns an Error result.
        var client = new UsageThenThrowChatClient();
        var service = CreateService(client, store);

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        // The failed-review outcome is unchanged: NeedsChanges, a "Review failed:" issue, and the
        // exception message from the provider.
        Assert.Equal("NeedsChanges", result.Verdict);
        Assert.Contains("Review failed:", result.Issues, StringComparison.Ordinal);
        Assert.Contains(UsageThenThrowChatClient.ProviderFailureText, result.Issues, StringComparison.Ordinal);
        Assert.Equal(ReviewStatus.NeedsChanges, goal.ReviewStatus);

        // …and the usage of the call that ran was still recorded.
        var recorded = Assert.Single(store.Usages);
        Assert.Equal(GoalUsageKind.PreExecutionReview, recorded.Kind);
        var entry = Assert.Single(recorded.Usage.Entries);
        Assert.Equal(UsageThenThrowChatClient.InputTokens, entry.Usage.InputTokens);
        Assert.Equal(UsageThenThrowChatClient.OutputTokens, entry.Usage.OutputTokens);
        // Two calls: the completed round 1 and the failing round 2 (recorded with zero tokens).
        Assert.Equal(2, entry.Usage.Calls);
    }

    /// <summary>
    /// A store whose <c>AddGoalUsageAsync</c> throws must change NOTHING, including the PERSISTED
    /// <see cref="Goal.ReviewStatus"/>: the verdict, the stored status, the review document and the
    /// returned result are compared against a HEALTHY-RUN CONTROL that differs only in whether the
    /// usage add succeeded.
    /// <para>
    /// The stored status is observed through the store's own snapshot, not through the caller's
    /// mutable <see cref="Goal"/>: the fake records every <see cref="IGoalStore.UpdateGoalAsync"/>
    /// argument AS IT WAS AT CALL TIME (the status VALUE, never the caller's instance), so a mutant
    /// that skips the final status persistence after the recording failure is visible here. The goal is
    /// seeded with the review document id already in <see cref="Goal.Documents"/>, so the document
    /// path's own status write (which happens only when the list changes) cannot mask the final
    /// persistence under test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReviewGoalAsync_ThrowingStore_LeavesVerdictStatusDocumentAndResultUnchanged()
    {
        var goal = SeededGoal();
        var store = new RecordingUsageStore { ThrowOnAdd = true };
        var knowledgeGraph = new KnowledgeGraph();
        var service = CreateService(new UsageReportingChatClient(ApprovedReply), store, knowledgeGraph);

        // The status the store holds BEFORE the call is the independently persisted Pending value.
        store.SeedPersistedStatus(goal.Id, ReviewStatus.Pending);
        Assert.Equal(ReviewStatus.Pending, store.PersistedStatus(goal.Id));

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        // The attempt happened and failed…
        Assert.Equal(1, store.CallCount);
        Assert.Empty(store.Usages);

        // …the first status write of this run persisted Pending…
        Assert.Equal(
            [ReviewStatus.Pending, ReviewStatus.Approved],
            store.StatusWrites);

        // …and the PERSISTED status still ends in the review's terminal status: it was first written
        // as Pending and then finally written as Approved, despite the usage add throwing.
        Assert.Equal(ReviewStatus.Approved, store.PersistedStatus(goal.Id));

        // …the returned result and the caller's own instance agree…
        Assert.Equal("Approved", result.Verdict);
        Assert.Equal("Looks good", result.Summary);
        Assert.Equal(ReviewStatus.Approved, goal.ReviewStatus);

        // …and the document CONTENT is identical to a healthy run's, field for field.
        var actualDoc = knowledgeGraph.GetDocument($"review-{goal.Id}");
        Assert.NotNull(actualDoc);

        // Expected-CONTENT pins, independent of the control comparison below: both runs use the same
        // document writer, so a writer that omitted the verdict line or the recommendation in BOTH runs
        // would still satisfy the full-content equality. These two assert the content is right in
        // absolute terms, not merely equal to another run's.
        Assert.Contains("Verdict: Approved", actualDoc!.Content, StringComparison.Ordinal);
        Assert.Contains("Looks good", actualDoc.Content, StringComparison.Ordinal);

        var control = await RunHealthyControlAsync();

        Assert.Equal(control.DocumentContent, actualDoc!.Content);
        Assert.Equal(control.Result.Verdict, result.Verdict);
        Assert.Equal(control.Result.Summary, result.Summary);
        Assert.Equal(control.Result.Issues, result.Issues);
        Assert.Equal(control.PersistedStatus, store.PersistedStatus(goal.Id));
    }

    /// <summary>
    /// The healthy-run CONTROL for the throwing-store test: the same goal shape and the same approved
    /// reply, against a store whose usage add succeeds. Every compared field must be identical to the
    /// throwing run's, so the comparison isolates the usage-recording failure as the only difference.
    /// </summary>
    private static async Task<(ReviewResult Result, string DocumentContent, ReviewStatus PersistedStatus)> RunHealthyControlAsync()
    {
        var goal = SeededGoal();
        var store = new RecordingUsageStore();
        var knowledgeGraph = new KnowledgeGraph();
        var service = CreateService(new UsageReportingChatClient(ApprovedReply), store, knowledgeGraph);
        store.SeedPersistedStatus(goal.Id, ReviewStatus.Pending);

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        // The control proves the healthy path records usage AND persists the status, so the comparison
        // above is a real one (a control that failed to persist would make the equality vacuous).
        Assert.Single(store.Usages);
        Assert.Equal(ReviewStatus.Approved, store.PersistedStatus(goal.Id));
        var doc = knowledgeGraph.GetDocument($"review-{goal.Id}");
        Assert.NotNull(doc);

        return (result, doc!.Content, store.PersistedStatus(goal.Id));
    }

    /// <summary>
    /// A goal prepared so the review's document-list update does NOT itself re-persist the goal: the
    /// review document id is already listed, so the only status write after the usage add is the
    /// FINAL verdict persistence the throwing-store test must observe.
    /// </summary>
    private static Goal SeededGoal(string id = "goal-1")
    {
        var goal = NewGoal(id);
        goal.Documents.Add($"review-{id}");
        return goal;
    }

    /// <summary>
    /// A review service with NO store completes normally AND reports no recording failure: a
    /// <c>null</c> store is a normal configuration, so the capture is skipped cleanly rather than
    /// attempted and caught. The logger is the independent channel that makes this observable.
    /// </summary>
    [Fact]
    public async Task ReviewGoalAsync_WithoutStore_CompletesNormally()
    {
        var goal = NewGoal();
        var logger = new CapturingLogger<GoalReviewService>();
        var service = CreateService(new UsageReportingChatClient(ApprovedReply), goalStore: null, logger: logger);

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        Assert.Equal("Approved", result.Verdict);
        Assert.Equal(ReviewStatus.Approved, goal.ReviewStatus);
        // No recording was ATTEMPTED, so no warning was logged (a null-store dereference would
        // surface as a caught-and-logged failure instead).
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// A review that THROWS before producing a result (a real exception, not an Error status) records
    /// nothing — the accepted limitation. The failed-review outcome must be unchanged, and the store
    /// must not have been called at all.
    /// </summary>
    [Fact]
    public async Task ReviewGoalAsync_WhenExecutionThrows_RecordsNothingAndKeepsTheFailedOutcome()
    {
        var goal = NewGoal();
        var store = new RecordingUsageStore();
        var service = CreateService(new ThrowingChatClient(), store);

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        Assert.Equal("NeedsChanges", result.Verdict);
        Assert.Contains("Review failed:", result.Issues, StringComparison.Ordinal);
        Assert.Equal(0, store.CallCount);
        Assert.Empty(store.Usages);
    }

    /// <summary>
    /// Two reviews of the same goal record one add EACH: the site records per review, and the store
    /// accumulates the goal's total.
    /// </summary>
    [Fact]
    public async Task ReviewGoalAsync_TwoReviews_RecordOneAddPerReview()
    {
        var store = new RecordingUsageStore();
        var service = CreateService(new UsageReportingChatClient(ApprovedReply), store);

        await service.ReviewGoalAsync(NewGoal(), TestContext.Current.CancellationToken);
        await service.ReviewGoalAsync(NewGoal(), TestContext.Current.CancellationToken);

        Assert.Equal(2, store.Usages.Count);
        Assert.All(store.Usages, u => Assert.Equal(GoalUsageKind.PreExecutionReview, u.Kind));
        Assert.Equal(2 * UsageReportingChatClient.InputPerCall, store.Usages.Sum(u => u.Usage.Total.InputTokens));
    }

    /// <summary>Captures the messages a service logs (with level), so a skipped capture is distinguishable from a failed one.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        internal IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// A recording <see cref="IGoalStore"/> fake that
    /// (a) captures every <see cref="IGoalStore.AddGoalUsageAsync"/> call (goal, kind, usage) and can
    /// be armed to throw from it, and
    /// (b) maintains a REAL persisted goal state: every <see cref="IGoalStore.UpdateGoalAsync"/> and
    /// <see cref="IGoalSource.UpdateGoalStatusAsync"/> call records the argument's
    /// <see cref="Goal.ReviewStatus"/> value at call time, so the test observes what was actually
    /// persisted rather than the caller's mutable instance. Aliasing is impossible: only the status
    /// ENUM VALUE is copied out, and the snapshots are keyed by goal id.
    /// </summary>
    private sealed class RecordingUsageStore : IGoalStore
    {
        private readonly List<(string GoalId, GoalUsageKind Kind, UsageSummary Usage)> _usages = [];
        private readonly Dictionary<string, ReviewStatus> _persistedStatus = new(StringComparer.Ordinal);
        private readonly List<ReviewStatus> _statusWrites = [];

        internal IReadOnlyList<(string GoalId, GoalUsageKind Kind, UsageSummary Usage)> Usages => _usages;

        /// <summary>Every status value written, in call order — shows Pending followed by the verdict.</summary>
        internal IReadOnlyList<ReviewStatus> StatusWrites => _statusWrites;

        /// <summary>When true, <see cref="AddGoalUsageAsync"/> throws instead of recording.</summary>
        internal bool ThrowOnAdd { get; init; }

        private int _calls;

        internal int CallCount => _calls;

        public string Name => "RecordingUsageStore";

        /// <summary>Seeds the store's persisted status (what a previous call had written).</summary>
        internal void SeedPersistedStatus(string goalId, ReviewStatus status) => _persistedStatus[goalId] = status;

        /// <summary>The status the STORE holds now — never the caller's instance.</summary>
        internal ReviewStatus PersistedStatus(string goalId) => _persistedStatus[goalId];

        public Task AddGoalUsageAsync(string goalId, GoalUsageKind kind, UsageSummary usage, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            if (ThrowOnAdd)
                throw new InvalidOperationException("usage persistence failed");
            _usages.Add((goalId, kind, usage));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records the caller's status AT CALL TIME (no aliasing), which is what makes a skipped final
        /// persistence observable: the stored value simply stays at whatever was written before.
        /// </summary>
        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            _statusWrites.Add(goal.ReviewStatus);
            _persistedStatus[goal.Id] = goal.ReviewStatus;
            return Task.CompletedTask;
        }

        public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult<Goal?>(null);

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default) =>
            Task.FromResult(goal);

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IterationSummary>>([]);

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
            Task.FromResult(release);

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult<Release?>(null);

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Release>>([]);

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>([]);
    }

    /// <summary>Reports usage on every call and returns the scripted reply text.</summary>
    private sealed class UsageReportingChatClient(string replyText) : IChatClient
    {
        internal const long InputPerCall = 1_100;
        internal const long OutputPerCall = 210;
        internal const long CachedPerCall = 310;

        public ChatClientMetadata Metadata => new("review-usage-stub", null, "review-usage-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replyText))
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails
                {
                    InputTokenCount = InputPerCall,
                    OutputTokenCount = OutputPerCall,
                    CachedInputTokenCount = CachedPerCall,
                },
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(replyText)])
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;

        public void Dispose() { }
    }

    /// <summary>
    /// Round 1 reports usage and asks for a tool call; round 2 throws a NON-HTTP fault, which
    /// SharpCoder converts into an <c>Error</c>-status result (not a propagated exception) whose
    /// <c>TokenUsage</c> still carries the tokens of the call that ran.
    /// </summary>
    private sealed class UsageThenThrowChatClient : IChatClient
    {
        internal const string ProviderFailureText = "provider failure";
        internal const long InputTokens = 700;
        internal const long OutputTokens = 70;

        private int _callCount;

        public ChatClientMetadata Metadata => new("review-usage-then-throw-stub", null, "review-usage-then-throw-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Interlocked.Increment(ref _callCount) > 1)
                throw new InvalidOperationException(ProviderFailureText);

            // A file tool call keeps the agent going without changing any state.
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("round-1", "read_file", new Dictionary<string, object?> { ["path"] = "src/Foo.cs" })]))
            {
                FinishReason = ChatFinishReason.ToolCalls,
                Usage = new UsageDetails
                {
                    InputTokenCount = InputTokens,
                    OutputTokenCount = OutputTokens,
                },
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;

        public void Dispose() { }
    }

    /// <summary>A client that throws a NON-HTTP-adjacent exception on its first call.</summary>
    private sealed class ThrowingChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("review-throwing-stub", null, "review-throwing-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("boom");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
