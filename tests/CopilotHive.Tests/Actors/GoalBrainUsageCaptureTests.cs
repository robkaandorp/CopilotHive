using CopilotHive.Actors;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder;

using Xunit;

namespace CopilotHive.Tests.Actors;

/// <summary>
/// The Brain capture-site contract: every completed per-goal Brain call records its
/// <c>AgentResult.TokenUsage</c> against the goal as <see cref="GoalUsageKind.Brain"/>, exactly once,
/// BEFORE the error check (so an Error-status result records too), best-effort (a throwing store
/// changes nothing).
/// </summary>
public sealed class GoalBrainUsageCaptureTests
{
    // The two shapes are the ones SharpCoder produces and the actor must record faithfully.
    private static UsageEntry Bucket(UsageSummary usage, string model)
    {
        var entry = Assert.Single(usage.Entries, e => e.Model == model);
        return entry;
    }

    private static UsageSummary BrainBucket(CapturingGoalStore store) =>
        store.Usages.Single().Usage;

    /// <summary>
    /// A successful Brain call records ONE add for the goal, carrying the call's reported usage
    /// (input, output, cached input, calls and both reported-calls counters).
    /// </summary>
    [Fact]
    public async Task ExecutePromptAsync_SuccessfulCall_RecordsExactlyOneBrainAddWithItsUsage()
    {
        using var fixture = new CaptureFixture();
        var store = new CapturingGoalStore();
        var client = new UsageReportingChatClient();
        await using var actor = fixture.CreateActor(client, store);
        actor.Start();

        var msg = GoalBrainActorMessages.CreateExecutePromptMessage("hi", CancellationToken.None);
        Assert.True(actor.Tell(msg));
        var reply = await AwaitReplyAsync(msg.Reply);

        Assert.Contains(UsageReportingChatClient.ReplyText, reply.Text);

        // Exactly ONE recorded add, for the goal, of kind Brain.
        var recorded = Assert.Single(store.Usages);
        Assert.Equal("goal-1", recorded.GoalId);
        Assert.Equal(GoalUsageKind.Brain, recorded.Kind);

        // …and it carries THIS execution's usage: one call, every reported category.
        Assert.Single(recorded.Usage.Entries);
        var entry = Bucket(recorded.Usage, UsageReportingChatClient.ModelId);
        Assert.Equal(UsageSource.Agent, entry.Source);
        Assert.Equal(UsageReportingChatClient.InputPerCall, entry.Usage.InputTokens);
        Assert.Equal(UsageReportingChatClient.OutputPerCall, entry.Usage.OutputTokens);
        Assert.Equal(UsageReportingChatClient.CachedPerCall, entry.Usage.CachedInputTokens);
        Assert.Equal(UsageReportingChatClient.ReasoningPerCall, entry.Usage.ReasoningTokens);
        Assert.Equal(1, entry.Usage.Calls);
        Assert.Equal(1, entry.Usage.CachedInputReportedCalls);
        Assert.Equal(1, entry.Usage.ReasoningReportedCalls);
    }

    /// <summary>
    /// An Error-status result must ALSO record: the capture sits before the error check, and a failed
    /// execution's calls were paid for exactly like a successful one's. The reply must still fault
    /// with the provider text, and the failed turn must still not save a session.
    /// </summary>
    [Fact]
    public async Task ExecutePromptAsync_ErrorStatusResult_StillRecordsItsUsageAndStillFaultsTheReply()
    {
        using var fixture = new CaptureFixture();
        var store = new CapturingGoalStore();
        // Usage is reported, then the client throws a NON-HTTP exception, which is what makes
        // SharpCoder return an Error-status result instead of propagating.
        var client = new UsageThenThrowChatClient();
        await using var actor = fixture.CreateActor(client, store);
        actor.Start();

        var msg = GoalBrainActorMessages.CreateExecutePromptMessage("hi", CancellationToken.None);
        Assert.True(actor.Tell(msg));
        await AwaitSettledAsync(msg.Reply);

        // The error path is UNCHANGED: the reply faults with the provider text.
        Assert.True(msg.Reply.Task.IsFaulted, "An Error-status result must still fault the reply");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await msg.Reply.Task);
        Assert.Contains(UsageThenThrowChatClient.ProviderFailureText, thrown.Message, StringComparison.Ordinal);

        // …and the usage of the calls that DID run is recorded.
        var recorded = Assert.Single(store.Usages);
        Assert.Equal(GoalUsageKind.Brain, recorded.Kind);
        var entry = Assert.Single(recorded.Usage.Entries);
        Assert.Equal(UsageSource.Agent, entry.Source);
        // TWO recorded calls: round 1 completed with its tokens, and the failing round 2 is itself
        // recorded with the usage it received so far (SharpCoder's documented per-call accounting) —
        // which is zero here. The failed execution therefore reports both real tokens and both calls.
        Assert.Equal(2, entry.Usage.Calls);
        Assert.Equal(UsageThenThrowChatClient.InputTokens, entry.Usage.InputTokens);
        Assert.Equal(UsageThenThrowChatClient.OutputTokens, entry.Usage.OutputTokens);
        // The failing call reported neither optional category, so they are INCOMPLETE, not measured zero.
        Assert.Equal(0, entry.Usage.CachedInputReportedCalls);
        Assert.Equal(0, entry.Usage.ReasoningReportedCalls);
    }

    /// <summary>
    /// A store whose <see cref="IGoalStore.AddGoalUsageAsync"/> throws must change NOTHING: the reply
    /// still succeeds, the session is still saved, the registry still returns to idle, and the actor
    /// keeps serving the mailbox.
    /// </summary>
    [Fact]
    public async Task ExecutePromptAsync_ThrowingStore_LeavesTheReplySessionAndRegistryUnchanged()
    {
        using var fixture = new CaptureFixture();
        var store = new CapturingGoalStore { ThrowOnAdd = true };
        var registry = new LlmSessionRegistry();
        var client = new UsageReportingChatClient();
        await using var actor = fixture.CreateActor(client, store, sessionRegistry: registry);
        actor.Start();

        var msg = GoalBrainActorMessages.CreateExecutePromptMessage("hi", CancellationToken.None);
        Assert.True(actor.Tell(msg));
        var reply = await AwaitReplyAsync(msg.Reply);

        // The attempt happened (and failed), yet the reply is the normal success reply…
        Assert.Equal(1, store.CallCount);
        Assert.Empty(store.Usages);
        Assert.Contains(UsageReportingChatClient.ReplyText, reply.Text);
        Assert.Null(reply.ToolCall);

        // …the session was still saved (the failing store did not skip the success path)…
        Assert.True(File.Exists(Path.Combine(fixture.StateDir, "brain-goal-goal-1.json")));

        // …the registry still ended idle…
        Assert.Equal("idle", Assert.Single(registry.GetAll()).Status);

        // …and the actor still answers the next message.
        var state = GoalBrainActorMessages.CreateGetGoalStateMessage();
        Assert.True(actor.Tell(state));
        var snapshot = await AwaitReplyAsync(state.Reply);
        Assert.Equal("goal-1", snapshot.GoalId);
    }

    /// <summary>
    /// With no store wired at all, a Brain call completes normally AND nothing is reported as a
    /// recording failure: <c>null</c> store is a normal configuration (the actor's constructor argument
    /// is optional), so the capture must be skipped cleanly rather than attempted and caught. The
    /// logger is the independent channel that makes this observable.
    /// </summary>
    [Fact]
    public async Task ExecutePromptAsync_WithoutStore_CompletesNormally()
    {
        using var fixture = new CaptureFixture();
        var logger = new CapturingLogger<GoalBrainActor>();
        await using var actor = fixture.CreateActor(new UsageReportingChatClient(), goalStore: null, logger: logger);
        actor.Start();

        var msg = GoalBrainActorMessages.CreateExecutePromptMessage("hi", CancellationToken.None);
        Assert.True(actor.Tell(msg));
        var reply = await AwaitReplyAsync(msg.Reply);

        Assert.Contains(UsageReportingChatClient.ReplyText, reply.Text);
        // No recording was even ATTEMPTED: a skipped guard leaves no WARNING in the log, whereas a
        // dereference of the null store would surface as a caught-and-logged failure. (The existing
        // debug usage log still fires, so the assertion is on warnings, not on "no entries at all".)
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.StartsWith("Goal Brain usage:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two sequential Brain calls record TWO adds (one per execution) for the same goal — the site
    /// records per call, and the store is what accumulates.
    /// </summary>
    [Fact]
    public async Task ExecutePromptAsync_TwoCalls_RecordOneAddPerCall()
    {
        using var fixture = new CaptureFixture();
        var store = new CapturingGoalStore();
        await using var actor = fixture.CreateActor(new UsageReportingChatClient(), store);
        actor.Start();

        foreach (var prompt in new[] { "first", "second" })
        {
            var msg = GoalBrainActorMessages.CreateExecutePromptMessage(prompt, CancellationToken.None);
            Assert.True(actor.Tell(msg));
            await AwaitReplyAsync(msg.Reply);
        }

        Assert.Equal(2, store.Usages.Count);
        Assert.All(store.Usages, u => Assert.Equal(GoalUsageKind.Brain, u.Kind));
        Assert.All(store.Usages, u => Assert.Equal(1, u.Usage.Total.Calls));
        Assert.Equal(2 * UsageReportingChatClient.InputPerCall, store.Usages.Sum(u => u.Usage.Total.InputTokens));
    }

    private static async Task<T> AwaitReplyAsync<T>(TaskCompletionSource<T> reply)
    {
        // Bounded await as a HANG GUARD only: the reply settles because the actor processed the
        // message, never because time passed. A stuck reply fails here instead of hanging the test.
        await reply.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        return reply.Task.Result;
    }

    private static async Task AwaitSettledAsync<T>(TaskCompletionSource<T> reply)
    {
        try
        {
            await reply.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            // A faulted or cancelled reply IS an expected outcome here; the assertion below is what
            // fails when the reply never settled at all.
        }

        Assert.True(reply.Task.IsCompleted, "Reply did not settle in time.");
    }

    /// <summary>Owns the actor's temp state directory and disposes actors, joining them in Dispose.</summary>
    private sealed class CaptureFixture : IDisposable
    {
        private readonly List<GoalBrainActor> _actors = [];

        internal CaptureFixture()
        {
            StateDir = Path.Combine(Path.GetTempPath(), "brain-usage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(StateDir);
        }

        internal string StateDir { get; }

        internal GoalBrainActor CreateActor(
            IChatClient client,
            IGoalStore? goalStore,
            LlmSessionRegistry? sessionRegistry = null,
            ILogger<GoalBrainActor>? logger = null)
        {
            var actor = new GoalBrainActor(
                "goal-1",
                AgentSession.Create("brain-goal-goal-1"),
                client,
                ownsChatClient: true,
                compactionClient: null,
                new AgentOptions
                {
                    WorkDirectory = StateDir,
                    MaxSteps = 5,
                    EnableBash = false,
                    EnableFileOps = false,
                    EnableFileWrites = false,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                    SystemPrompt = "You are the Brain.",
                },
                "test-model",
                100_000,
                StateDir,
                sessionRegistry,
                logger ?? NullLogger<GoalBrainActor>.Instance,
                goalStore);
            _actors.Add(actor);
            return actor;
        }

        public void Dispose()
        {
            foreach (var actor in _actors)
            {
                try
                {
                    actor.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // Best-effort teardown: a failing actor must not hide the test's own result.
                }
            }

            try
            {
                if (Directory.Exists(StateDir))
                    Directory.Delete(StateDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    /// <summary>Captures the messages an actor logs (with level), so a skipped capture is distinguishable from a failed one.</summary>
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
    /// A recording <see cref="IGoalStore"/> fake that captures every
    /// <see cref="IGoalStore.AddGoalUsageAsync"/> call (goal, kind, usage) and can be armed to throw.
    /// </summary>
    private sealed class CapturingGoalStore : IGoalStore
    {
        private readonly List<(string GoalId, GoalUsageKind Kind, UsageSummary Usage)> _usages = [];

        internal IReadOnlyList<(string GoalId, GoalUsageKind Kind, UsageSummary Usage)> Usages => _usages;

        /// <summary>When true, <see cref="AddGoalUsageAsync"/> throws instead of recording.</summary>
        internal bool ThrowOnAdd { get; init; }

        private int _calls;

        internal int CallCount => _calls;

        public string Name => "CapturingGoalStore";

        public Task AddGoalUsageAsync(string goalId, GoalUsageKind kind, UsageSummary usage, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            if (ThrowOnAdd)
                throw new InvalidOperationException("usage persistence failed");
            _usages.Add((goalId, kind, usage));
            return Task.CompletedTask;
        }

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult<Goal?>(null);

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default) =>
            Task.FromResult(goal);

        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default) => Task.CompletedTask;

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

        public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;

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

    /// <summary>
    /// A chat client whose single (non-streaming) call reports usage and returns text. The counts are
    /// all distinct and BOTH optional categories are reported, so the recorded bucket is fully
    /// discriminating.
    /// </summary>
    private sealed class UsageReportingChatClient : IChatClient
    {
        internal const string ReplyText = "brain reply";
        internal const string ModelId = "brain-usage-model";
        internal const long InputPerCall = 1_100;
        internal const long OutputPerCall = 210;
        internal const long CachedPerCall = 310;
        internal const long ReasoningPerCall = 41;

        public ChatClientMetadata Metadata => new("brain-usage-stub", null, ModelId);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ReplyText))
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = Details(),
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static UsageDetails Details() => new()
        {
            InputTokenCount = InputPerCall,
            OutputTokenCount = OutputPerCall,
            CachedInputTokenCount = CachedPerCall,
            ReasoningTokenCount = ReasoningPerCall,
        };

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;

        public void Dispose() { }
    }

    /// <summary>
    /// Round 1 reports usage AND asks for a tool call, so the agent continues; round 2 throws a
    /// NON-HTTP fault, which SharpCoder converts into an <c>Error</c>-status result instead of
    /// propagating. The result's <c>TokenUsage</c> therefore carries the tokens of the call that DID
    /// run (a call that throws on its first and only round reports zero tokens, so the two-round shape
    /// is what makes the recorded usage non-zero). Neither optional category is reported, so both
    /// arrive incomplete.
    /// </summary>
    private sealed class UsageThenThrowChatClient : IChatClient
    {
        internal const string ProviderFailureText = "provider failure";
        internal const long InputTokens = 700;
        internal const long OutputTokens = 70;

        private int _callCount;

        public ChatClientMetadata Metadata => new("brain-usage-then-throw-stub", null, "brain-usage-then-throw-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Interlocked.Increment(ref _callCount) > 1)
                throw new InvalidOperationException(ProviderFailureText);

            // Round 1: usage plus a tool call, so the agent keeps going and the second round can fail.
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("round-1", "escalate_to_composer", new Dictionary<string, object?>
                {
                    ["question"] = "which file?",
                    ["reason"] = "not in the codebase",
                })]))
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
}
