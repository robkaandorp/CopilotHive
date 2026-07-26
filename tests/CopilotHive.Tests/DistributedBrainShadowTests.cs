using System.Reflection;

using CopilotHive.Actors;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Git;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Shared.AI;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for the DistributedBrain shadow-actor LLM mirroring (Phase 3b).
/// Covers acceptance criteria 12-16, 20-21.
/// </summary>
[Collection("EnvVarMutation")]
public class DistributedBrainShadowTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    // ── Fake chat clients ──

    /// <summary>Chat client that returns a text response and tracks disposal.</summary>
    private sealed class TrackingChatClient : IChatClient
    {
        internal bool WasDisposed { get; private set; }
        internal int DisposeCallCount { get; private set; }

        public ChatClientMetadata Metadata => new("fake", null, "fake-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fake response")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            WasDisposed = true;
            DisposeCallCount++;
        }
    }

    /// <summary>Chat client that throws from GetResponseAsync, causing CodingAgent to return Status="Error".</summary>
    private sealed class ThrowingChatClient : IChatClient
    {
        internal bool WasDisposed { get; private set; }

        public ChatClientMetadata Metadata => new("throw", null, "throw-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new InvalidOperationException("client-throws-on-purpose");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => WasDisposed = true;
    }

    /// <summary>Chat client that returns a tool-call for report_iteration_plan on first call, then text.</summary>
    private sealed class PlanStubClient : IChatClient
    {
        private int _callCount;
        private readonly string _callId;
        private readonly string[] _phases;
        private readonly string _reason;

        internal PlanStubClient(string callId, string[] phases, string reason)
        {
            _callId = callId;
            _phases = phases;
            _reason = reason;
        }

        public ChatClientMetadata Metadata => new("stub", null, "stub-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                var toolCallContent = new FunctionCallContent(_callId, "report_iteration_plan", new Dictionary<string, object?>
                {
                    ["phases"] = _phases,
                    ["phase_instructions"] = "{}",
                    ["reason"] = _reason,
                    // The goal-actor's report_iteration_plan declares model_tiers as a required
                    // (non-optional) parameter, so it must always be supplied explicitly.
                    ["model_tiers"] = null,
                });
                var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCallContent]))
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                };
                return Task.FromResult(response);
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Plan done."))
            {
                FinishReason = ChatFinishReason.Stop,
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Chat client that always returns a fixed text response.</summary>
    private sealed class TextStubClient : IChatClient
    {
        private readonly string _text;

        internal TextStubClient(string text) => _text = text;

        public ChatClientMetadata Metadata => new("text", null, "text-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _text))
            {
                FinishReason = ChatFinishReason.Stop,
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Chat client that returns an escalate_to_composer tool call, then text.</summary>
    private sealed class EscalateStubClient : IChatClient
    {
        private int _callCount;
        private readonly string _question;
        private readonly string _reason;

        internal EscalateStubClient(string question, string reason)
        {
            _question = question;
            _reason = reason;
        }

        public ChatClientMetadata Metadata => new("escalate", null, "escalate-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                var call = new FunctionCallContent("esc-1", "escalate_to_composer", new Dictionary<string, object?>
                {
                    ["question"] = _question,
                    ["reason"] = _reason,
                });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call]))
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                });
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Escalated."))
            {
                FinishReason = ChatFinishReason.Stop,
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Chat client that delays before responding, so cancellation can be observed.</summary>
    private sealed class DelayingChatClient : IChatClient
    {
        private readonly TimeSpan _delay;

        internal DelayingChatClient(TimeSpan delay) => _delay = delay;

        public ChatClientMetadata Metadata => new("delay", null, "delay-model");

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            await Task.Delay(_delay, ct);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "late"))
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// Chat client wrapper that records whether the actor was still alive when Dispose was called.
    /// Used for criterion 15 (dispose ordering test).
    /// </summary>
    private sealed class OrderingChatClient : IChatClient
    {
        private readonly IChatClient _inner;
        internal bool ActorWasAliveAtDispose { get; private set; }

        internal OrderingChatClient(IChatClient inner) => _inner = inner;

        internal Action? OnDispose { get; set; }

        public ChatClientMetadata Metadata => new("ordering", null, "ordering-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => _inner.GetResponseAsync(messages, options, ct);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => _inner.GetStreamingResponseAsync(messages, options, ct);

        public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

        public void Dispose()
        {
            OnDispose?.Invoke();
            _inner.Dispose();
        }
    }

    /// <summary>Minimal IBrainRepoManager stub for testing.</summary>
    private sealed class FakeRepoManager : IBrainRepoManager
    {
        public string WorkDirectory { get; }

        internal FakeRepoManager(string workDir) => WorkDirectory = workDir;

        public Task<string> EnsureCloneAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default)
            => Task.FromResult(Path.Combine(WorkDirectory, repoName));

        public Task<string> MergeFeatureBranchAsync(string repoName, string featureBranch, string defaultBranch, string commitMessage, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<BranchDeleteResult> DeleteRemoteBranchAsync(string repoName, string branchName, CancellationToken ct = default)
            => throw new NotSupportedException();

        public string GetClonePath(string repoName) => Path.Combine(WorkDirectory, repoName);

        public Task<string?> GetHeadShaAsync(string repoName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> MergeBranchAsync(string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> CreateTagAsync(string repoName, string tag, string branch, string message, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteTagAsync(string repoName, string tag, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<List<string>> ListRemoteBranchesAsync(string repoName, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
    }

    // ── Helpers ──

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"brain-shadow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }

    private static HiveConfigFile ActorConfig(bool enabled) =>
        new() { Orchestrator = new OrchestratorConfig { UseBrainActors = enabled } };

    private static object? GetBrainActor(DistributedBrain brain) =>
        typeof(DistributedBrain).GetField("_brainActor", NonPublicInstance)!.GetValue(brain);

    private static void SetActorFactory(DistributedBrain brain, Func<string, BrainActor> factory) =>
        typeof(DistributedBrain).GetField("_actorFactory", NonPublicInstance)!.SetValue(brain, factory);

    private static bool IsConnected(DistributedBrain brain) =>
        (bool)typeof(DistributedBrain).GetField("_connected", NonPublicInstance)!.GetValue(brain)!;

    private static object? GetField(object obj, string name) =>
        obj.GetType().GetField(name, NonPublicInstance)?.GetValue(obj);

    private static T GetField<T>(object obj, string name) => (T)GetField(obj, name)!;

    private static Dictionary<string, GoalBrainActor> GetChildActors(BrainActor actor) =>
        GetField<Dictionary<string, GoalBrainActor>>(actor, "_childActors");

    private static Dictionary<string, string> GetActiveGoalSessions(BrainActor actor) =>
        GetField<Dictionary<string, string>>(actor, "_activeGoalSessions");

    /// <summary>Sets the internal <c>_mirrorDelay</c> test seam that parks each MirrorAsync call.</summary>
    private static void SetMirrorDelay(DistributedBrain brain, TimeSpan? delay) =>
        typeof(DistributedBrain).GetField("_mirrorDelay", NonPublicInstance)!.SetValue(brain, delay);

    /// <summary>True when the session history contains a message with the given text.</summary>
    private static bool ContainsSummary(AgentSession session, string text)
    {
        // Snapshot to avoid tearing while the production code mutates the history concurrently.
        var messages = session.MessageHistory.ToArray();
        return messages.Any(m => m.Text.Contains(text, StringComparison.Ordinal));
    }

    /// <summary>Timestamps <paramref name="name"/> the first time <paramref name="predicate"/> holds.</summary>
    private static void Record(
        List<(string Name, TimeSpan At)> events,
        System.Diagnostics.Stopwatch clock,
        string name,
        Func<bool> predicate)
    {
        if (events.Any(e => e.Name == name)) return;
        if (predicate()) events.Add((name, clock.Elapsed));
    }

    /// <summary>ContainsKey on a dictionary being mutated by the actor loop; treats a torn read as "present".</summary>
    private static bool SafeContainsChild(Dictionary<string, GoalBrainActor> children, string goalId)
    {
        try { return children.ContainsKey(goalId); }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>Invokes the private ExecuteBrainAsync via reflection and returns its tuple result.</summary>
    private static async Task<(string Text, DistributedBrain.BrainToolCallResult? ToolCall)> InvokeExecuteBrainAsync(
        DistributedBrain brain, string prompt, string goalId, CancellationToken ct, string status = "test")
    {
        var method = typeof(DistributedBrain).GetMethod("ExecuteBrainAsync", NonPublicInstance)!;
        var task = (Task)method.Invoke(brain, [prompt, goalId, ct, status, "TestMethod"])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return ((string, DistributedBrain.BrainToolCallResult?))result;
    }

    private static GoalPipeline CreatePipeline(string goalId, string description = "test") =>
        new(new Goal { Id = goalId, Description = description });

    /// <summary>
    /// Creates a DistributedBrain with UseBrainActors=true, using a fake chat client and a fake
    /// chatClientFactory injected via _actorFactory so the shadow actor's child creation uses fakes.
    /// </summary>
    private static DistributedBrain NewShadowBrain(
        string dir,
        IChatClient? chatClient = null,
        Func<string, IChatClient>? factoryChatClientFactory = null,
        string? compactionModel = null,
        IBrainRepoManager? repoManager = null,
        HiveConfigFile? hiveConfig = null,
        int maxSteps = 50,
        string? systemPrompt = null,
        ReasoningEffort? reasoningEffort = null,
        ILogger<DistributedBrain>? logger = null)
    {
        var client = chatClient ?? new TrackingChatClient();
        var brain = new DistributedBrain(
            "copilot/test-model",
            logger ?? NullLogger<DistributedBrain>.Instance,
            maxSteps: maxSteps,
            repoManager: repoManager,
            stateDir: dir,
            chatClient: client,
            compactionModel: compactionModel,
            hiveConfig: hiveConfig ?? ActorConfig(true));

        // Inject a fake actor factory so the shadow BrainActor uses fake chat clients for children.
        var childFactory = factoryChatClientFactory ?? (_ => new TrackingChatClient());
        SetActorFactory(brain, stateDir =>
            new BrainActor(
                "copilot/test-model", 100_000, stateDir, NullLogger.Instance,
                chatClientFactory: childFactory,
                compactionModel: compactionModel,
                hiveConfig: hiveConfig ?? ActorConfig(true),
                maxSteps: maxSteps,
                systemPrompt: systemPrompt,
                reasoningEffort: reasoningEffort,
                workDirectory: repoManager?.WorkDirectory ?? dir));

        return brain;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Criterion 12: ExecuteBrainAsync split — authoritative routing sends the prompt
    // to the actor on non-throwing completion including Status="Error"
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteBrainAsync_SuccessfulCompletion_RoutesToActor()
    {
        var dir = NewTempDir();
        try
        {
            // Use a stub client that returns a valid plan so PlanIterationAsync succeeds.
            var stub = new PlanStubClient("call-1", ["coding"], "test reason");
            var brain = NewShadowBrain(dir, chatClient: stub);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-1", TestContext.Current.CancellationToken);

                var pipeline = CreatePipeline("goal-1", "test goal");
                await brain.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);

                // Verify the actor received the prompt — check child's session history.
                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                var children = GetChildActors(actor!);
                Assert.True(children.ContainsKey("goal-1"), "Actor should have a child for goal-1.");
                var child = children["goal-1"];

                // The child's session contains the planning prompt — the authoritative call was routed
                // through ExecutePromptOnChild to the child's CodingAgent.
                Assert.True(child.Session.MessageHistory.Count > 0,
                    "Child session should have messages after authoritative actor execution.");
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ExecuteBrainAsync_StatusError_RoutesToActor()
    {
        var dir = NewTempDir();
        try
        {
            // A throwing client causes CodingAgent.ExecuteAsync to return Status="Error" (not throw).
            // ExecuteBrainAsync completes without throwing and still routes through the actor.
            var throwing = new ThrowingChatClient();
            var brain = NewShadowBrain(dir, chatClient: throwing);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-err", TestContext.Current.CancellationToken);

                var pipeline = CreatePipeline("goal-err", "error test goal");
                // AskQuestionAsync catches exceptions and returns a fallback, so it won't throw.
                // But the throwing client causes CodingAgent to return Status="Error" — a non-throwing completion.
                var response = await brain.AskQuestionAsync("goal-err", 1, "coding", "coder", "what?", TestContext.Current.CancellationToken);
                Assert.NotNull(response);

                // Verify the actor received the prompt despite Status="Error".
                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                var children = GetChildActors(actor!);
                Assert.True(children.ContainsKey("goal-err"), "Actor should have a child for goal-err.");
                var child = children["goal-err"];

                // The child's session should have messages — the prompt was routed to the actor.
                Assert.True(child.Session.MessageHistory.Count > 0,
                    "Child session should have messages after actor execution (Status=Error still routes).");
            }
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // ExecuteBrainAsync does NOT fire on exception or cancellation
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteBrainAsync_PreCanceledToken_PropagatesCancellation()
    {
        var dir = NewTempDir();
        try
        {
            var stub = new PlanStubClient("call-cancel", ["coding"], "cancel test");
            var brain = NewShadowBrain(dir, chatClient: stub);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-cancel", TestContext.Current.CancellationToken);

                // Pre-canceled token — ExecuteBrainViaActorAsync awaits the reply with ct,
                // so cancellation propagates to the caller.
                using var cts = new CancellationTokenSource();
                await cts.CancelAsync();

                // TaskCanceledException is a subclass of OperationCanceledException.
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    brain.AskQuestionAsync("goal-cancel", 1, "coding", "coder", "q", cts.Token));
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ExecuteBrainAsync_NoGoalContext_Throws()
    {
        var dir = NewTempDir();
        try
        {
            var stub = new PlanStubClient("call-noctx", ["coding"], "noctx test");
            var brain = NewShadowBrain(dir, chatClient: stub);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                // Do NOT fork a session for this goal. With UseBrainActors=true the routing
                // method goes to the actor, which has no child for the goal and replies with
                // KeyNotFoundException.
                await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                    InvokeExecuteBrainAsync(brain, "prompt", "nonexistent-goal", CancellationToken.None));

                // No child should exist for the nonexistent goal.
                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                var children = GetChildActors(actor!);
                Assert.False(children.ContainsKey("nonexistent-goal"),
                    "Actor should not have a child for a goal that was never forked.");
            }
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Criterion 13: InjectSystemNoteAsync — FireShadowNote fire-and-forget
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InjectSystemNoteAsync_FiresShadowNote_ChildReceivesNote()
    {
        var dir = NewTempDir();
        try
        {
            var stub = new PlanStubClient("call-note", ["coding"], "note test");
            var brain = NewShadowBrain(dir, chatClient: stub);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-note", TestContext.Current.CancellationToken);

                var pipeline = CreatePipeline("goal-note", "note test goal");

                // Call InjectSystemNoteAsync — should return promptly (fire-and-forget).
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await brain.InjectSystemNoteAsync(pipeline, "SHADOW_NOTE_MARKER", TestContext.Current.CancellationToken);
                sw.Stop();

                // The method should return quickly — it doesn't wait for the shadow.
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
                    $"InjectSystemNoteAsync took {sw.Elapsed.TotalSeconds:F1}s — should return promptly.");

                // Give the fire-and-forget shadow a moment to process.
                await Task.Delay(500, TestContext.Current.CancellationToken);

                // Verify the child's session contains the note.
                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                var children = GetChildActors(actor!);
                Assert.True(children.ContainsKey("goal-note"));
                var child = children["goal-note"];

                Assert.Contains(child.Session.MessageHistory,
                    m => m.Text.Contains("SHADOW_NOTE_MARKER", StringComparison.Ordinal));
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task InjectSystemNoteAsync_FlagOff_DoesNotFireShadow()
    {
        var dir = NewTempDir();
        try
        {
            var brain = new DistributedBrain(
                "copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: dir, chatClient: new TrackingChatClient(), hiveConfig: ActorConfig(false));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-off", TestContext.Current.CancellationToken);

                var pipeline = CreatePipeline("goal-off");
                await brain.InjectSystemNoteAsync(pipeline, "NOTE_NO_SHADOW", TestContext.Current.CancellationToken);

                // No brain actor should exist.
                Assert.Null(GetBrainActor(brain));
                Assert.False(Directory.Exists(Path.Combine(dir, "actors")));
            }
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Criterion 14: StartShadowActorAsync passes all deps
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StartShadowActorAsync_PassesAllDepsToBrainActor()
    {
        var dir = NewTempDir();
        var repoDir = Path.Combine(dir, "repos");
        Directory.CreateDirectory(repoDir);
        try
        {
            var injectedClient = new TrackingChatClient();
            Func<string, IChatClient> chatClientFactory = _ => new TrackingChatClient();
            var hiveConfig = ActorConfig(true);
            var repoManager = new FakeRepoManager(repoDir);
            var goalStore = new InMemoryGoalStore();
            var knowledgeGraph = new CopilotHive.Knowledge.KnowledgeGraph();

            var brain = new DistributedBrain(
                "copilot/test-model:high",
                NullLogger<DistributedBrain>.Instance,
                maxSteps: 42,
                repoManager: repoManager,
                stateDir: dir,
                goalStore: goalStore,
                chatClient: injectedClient,
                compactionModel: null,
                knowledgeGraph: knowledgeGraph,
                hiveConfig: hiveConfig);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);

                // Verify the BrainActor's internal fields match the deps passed via StartShadowActorAsync.
                // When _actorFactory is null, StartShadowActorAsync creates the BrainActor directly
                // with all the DistributedBrain's internal fields.
                var a = actor!;

                // _chatClientFactory should be the one from DistributedBrain.
                var actorFactory = GetField<Func<string, IChatClient>>(a, "_chatClientFactory");
                Assert.NotNull(actorFactory);

                // _injectedChatClient should be the injected client.
                var actorInjected = GetField<IChatClient?>(a, "_injectedChatClient");
                Assert.Same(injectedClient, actorInjected);

                // _compactionModel should be null (we passed null).
                var actorCompaction = GetField<string?>(a, "_compactionModel");
                Assert.Null(actorCompaction);

                // _hiveConfig should match.
                var actorHiveConfig = GetField<HiveConfigFile?>(a, "_hiveConfig");
                Assert.Same(hiveConfig, actorHiveConfig);

                // _maxSteps should be 42.
                var actorMaxSteps = GetField<int>(a, "_maxSteps");
                Assert.Equal(42, actorMaxSteps);

                // _systemPrompt should not be null (DistributedBrain builds it from DefaultSystemPrompt).
                var actorSystemPrompt = GetField<string?>(a, "_systemPrompt");
                Assert.NotNull(actorSystemPrompt);

                // _reasoningEffort should be High (from "copilot/test-model:high").
                var actorReasoning = GetField<ReasoningEffort?>(a, "_reasoningEffort");
                Assert.Equal(ReasoningEffort.High, actorReasoning);

                // _workDirectory should be repoManager.WorkDirectory.
                var actorWorkDir = GetField<string?>(a, "_workDirectory");
                Assert.Equal(repoDir, actorWorkDir);

                // _goalStore and _knowledgeGraph should be propagated from the DistributedBrain.
                Assert.Same(goalStore, GetField<CopilotHive.Goals.IGoalStore?>(a, "_goalStore"));
                Assert.Same(knowledgeGraph, GetField<CopilotHive.Knowledge.KnowledgeGraph?>(a, "_knowledgeGraph"));
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task StartShadowActorAsync_NoRepoManager_WorkDirectoryIsStateDir()
    {
        var dir = NewTempDir();
        try
        {
            var brain = new DistributedBrain(
                "copilot/test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: dir,
                chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig(true));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);

                var actorWorkDir = GetField<string?>(actor!, "_workDirectory");
                // Without a repo manager, workDirectory should be _stateDir (the actors subdir is passed as stateDir).
                // The BrainActor's stateDir is Path.Combine(_stateDir, "actors"), so workDirectory should be _stateDir.
                Assert.Equal(dir, actorWorkDir);
            }
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Criterion 15: DisposeAsyncCore — actor disposed BEFORE injected client
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DisposeAsyncCore_ActorDisposedBeforeInjectedClient()
    {
        var dir = NewTempDir();
        try
        {
            var innerClient = new TrackingChatClient();
            var injectedClient = new OrderingChatClient(innerClient);

            // Track whether the actor is alive when the client is disposed.
            BrainActor? capturedActor = null;
            injectedClient.OnDispose = () =>
            {
                // When the injected client's Dispose is called, check if the actor is still alive.
                // If the actor was disposed first, its IsCompleted should be true.
                if (capturedActor is { } a)
                    Assert.True(a.IsCompleted,
                        "BrainActor must be disposed BEFORE the injected chat client.");
            };

            var brain = new DistributedBrain(
                "copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: dir, chatClient: injectedClient, hiveConfig: ActorConfig(true));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                capturedActor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(capturedActor);
            }

            // After `await using` disposes the brain, the injected client should have been disposed.
            Assert.True(innerClient.WasDisposed, "Injected client should be disposed.");
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Criterion 16: UseBrainActors=false — no mirroring
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UseBrainActorsFalse_NoActorCreated_NoActorsDirectory()
    {
        var dir = NewTempDir();
        try
        {
            var stub = new PlanStubClient("call-off", ["coding"], "flag off test");
            var brain = new DistributedBrain(
                "copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: dir, chatClient: stub, hiveConfig: ActorConfig(false));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-off", TestContext.Current.CancellationToken);

                // Execute a prompt — should work without any shadow.
                var pipeline = CreatePipeline("goal-off", "flag off goal");
                var result = await brain.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);
                Assert.NotNull(result);

                // No brain actor should exist.
                Assert.Null(GetBrainActor(brain));

                // No actors/ directory should be created.
                Assert.False(Directory.Exists(Path.Combine(dir, "actors")),
                    "actors/ directory should not be created when UseBrainActors=false.");
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task UseBrainActorsFalse_InjectSystemNote_NoShadow()
    {
        var dir = NewTempDir();
        try
        {
            var brain = new DistributedBrain(
                "copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: dir, chatClient: new TrackingChatClient(), hiveConfig: ActorConfig(false));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-off2", TestContext.Current.CancellationToken);

                var pipeline = CreatePipeline("goal-off2");
                await brain.InjectSystemNoteAsync(pipeline, "NOTE_NO_SHADOW", TestContext.Current.CancellationToken);

                Assert.Null(GetBrainActor(brain));
            }
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Criterion 20: Fork failure — compaction client creation fails
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ForkFailure_CompactionClientCreationFails_RawChatClientDisposed_NoChildRegistered()
    {
        var dir = NewTempDir();
        try
        {
            // Use a fake chat client that tracks disposal for the shadow actor's children.
            var shadowClient = new TrackingChatClient();

            // Set compactionModel to null on the DistributedBrain (authoritative path doesn't need it),
            // but pass a bad compactionModel to the shadow BrainActor via _actorFactory.
            // We unset GH_TOKEN so the "github" provider throws for the shadow's compaction client.
            var savedGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
            var savedGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            try
            {
                var brain = new DistributedBrain(
                    "copilot/test-model", NullLogger<DistributedBrain>.Instance,
                    stateDir: dir, chatClient: new TrackingChatClient(),
                    compactionModel: null,  // No compaction on the authoritative path
                    hiveConfig: ActorConfig(true));

                SetActorFactory(brain, stateDir =>
                    new BrainActor(
                        "copilot/test-model", 100_000, stateDir, NullLogger.Instance,
                        chatClientFactory: _ => shadowClient,
                        compactionModel: "github/some-model"));  // Bad compaction model — will throw

                await using (brain)
                {
                    await brain.ConnectAsync(TestContext.Current.CancellationToken);

                    // ForkSessionForGoalAsync should succeed on the authoritative path.
                    // The shadow actor's ForkSessionAsync should fail (compaction client creation throws),
                    // but the failure is logged as a warning and does NOT propagate.
                    await brain.ForkSessionForGoalAsync("goal-fail", TestContext.Current.CancellationToken);

                    // Give the mirror a moment to process.
                    await Task.Delay(500, TestContext.Current.CancellationToken);

                    // Verify the shadow actor's state — no child should be registered.
                    var actor = (BrainActor?)GetBrainActor(brain);
                    Assert.NotNull(actor);
                    var children = GetChildActors(actor!);
                    Assert.False(children.ContainsKey("goal-fail"),
                        "Shadow actor should not have a child for goal-fail (compaction creation failed).");
                    var sessions = GetActiveGoalSessions(actor!);
                    Assert.False(sessions.ContainsKey("goal-fail"),
                        "Shadow actor should not have a session entry for goal-fail.");

                    // The raw chat client should have been disposed (parent owned it, pre-constructor failure).
                    Assert.True(shadowClient.WasDisposed,
                        "Raw chat client should be disposed after pre-constructor failure in shadow fork.");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GH_TOKEN", savedGhToken);
                Environment.SetEnvironmentVariable("GITHUB_TOKEN", savedGithubToken);
            }
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Criterion 21: Post-constructor failure — session save fails
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PostConstructorFailure_SessionSaveFails_ChildDisposed_NoRegistration()
    {
        var dir = NewTempDir();
        var filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(filePath, "not-a-directory", TestContext.Current.CancellationToken);
        try
        {
            var shadowClient = new TrackingChatClient();

            var brain = new DistributedBrain(
                "copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: dir, chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig(true));

            // Create a BrainActor whose state dir is a FILE, so SaveSessionAsync throws.
            SetActorFactory(brain, _ =>
                new BrainActor(
                    "copilot/test-model", 100_000, filePath, NullLogger.Instance,
                    chatClientFactory: _ => shadowClient));

            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                // ForkSessionForGoalAsync should succeed on the authoritative path.
                // The shadow actor's ForkSessionAsync should fail (session save fails because
                // state dir is a file), but the failure is logged and does NOT propagate.
                await brain.ForkSessionForGoalAsync("goal-postfail", TestContext.Current.CancellationToken);

                // Give the mirror a moment to process.
                await Task.Delay(500, TestContext.Current.CancellationToken);

                // Verify the shadow actor's state — no child should be registered.
                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                var children = GetChildActors(actor!);
                Assert.False(children.ContainsKey("goal-postfail"),
                    "Shadow actor should not have a child for goal-postfail (session save failed).");
                var sessions = GetActiveGoalSessions(actor!);
                Assert.False(sessions.ContainsKey("goal-postfail"),
                    "Shadow actor should not have a session entry for goal-postfail.");

                // The chat client should have been disposed via the child's DisposeOwnedResources
                // (post-constructor failure disposes the child actor, not raw clients).
                Assert.True(shadowClient.WasDisposed,
                    "Chat client should be disposed via child actor disposal after post-constructor failure.");
            }
        }
        finally
        {
            DeleteDir(dir);
            try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Additional: Verify authoritative routing completes promptly (non-blocking)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteBrainAsync_AuthoritativeRouting_DoesNotBlockCaller()
    {
        var dir = NewTempDir();
        try
        {
            // Use a stub that returns a plan quickly on the authoritative path.
            var stub = new PlanStubClient("call-ff", ["coding"], "fire-and-forget test");
            var brain = NewShadowBrain(dir, chatClient: stub);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-ff", TestContext.Current.CancellationToken);

                var pipeline = CreatePipeline("goal-ff", "ff test goal");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await brain.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);
                sw.Stop();

                Assert.NotNull(result);
                // PlanIterationAsync should complete promptly via authoritative actor routing.
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
                    $"PlanIterationAsync took {sw.Elapsed.TotalSeconds:F1}s — actor routing may be blocking.");
            }
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Additional: Verify that ForkSessionForGoalAsync with UseBrainActors=true
    // creates the child actor in the shadow (integration test for criterion 19)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ForkSessionForGoalAsync_FlagOn_ShadowCreatesChildAtomically()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-atomic", TestContext.Current.CancellationToken);

                // Give the mirror a moment to process.
                await Task.Delay(300, TestContext.Current.CancellationToken);

                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                var children = GetChildActors(actor!);
                Assert.True(children.ContainsKey("goal-atomic"),
                    "Shadow actor should have a child for goal-atomic after fork.");
                var sessions = GetActiveGoalSessions(actor!);
                Assert.True(sessions.ContainsKey("goal-atomic"),
                    "Shadow actor should have a session entry for goal-atomic after fork.");

                // Session file should exist in the actors directory.
                Assert.True(File.Exists(Path.Combine(dir, "actors", "brain-goal-goal-atomic.json")),
                    "Shadow actor should have persisted the goal session file.");
            }
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Additional: DeleteGoalSessionAsync removes child from shadow
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeleteGoalSessionAsync_FlagOn_ShadowRemovesChildAndFile()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-del", TestContext.Current.CancellationToken);

                await Task.Delay(300, TestContext.Current.CancellationToken);
                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                Assert.True(GetChildActors(actor!).ContainsKey("goal-del"));

                await brain.DeleteGoalSessionAsync("goal-del", TestContext.Current.CancellationToken);

                await Task.Delay(300, TestContext.Current.CancellationToken);

                Assert.False(GetChildActors(actor!).ContainsKey("goal-del"),
                    "Shadow should have removed child after delete.");
                Assert.False(GetActiveGoalSessions(actor!).ContainsKey("goal-del"),
                    "Shadow should have removed session entry after delete.");
                Assert.False(File.Exists(Path.Combine(dir, "actors", "brain-goal-goal-del.json")),
                    "Shadow should have deleted the session file.");
            }
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Criteria 14-25: authoritative routing through the BrainActor
    // ════════════════════════════════════════════════════════════════════════

    // Criterion 14: flag on — ExecuteBrainAsync routes to the actor and returns the tool call.
    [Fact]
    public async Task ExecuteBrainAsync_FlagOn_RoutesToActor_ReturnsResultWithToolCall()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir,
                chatClient: new ThrowingChatClient(),  // context path would fail — proves the actor path ran
                factoryChatClientFactory: _ => new PlanStubClient("call-14", ["coding", "review"], "actor plan"));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-14", TestContext.Current.CancellationToken);

                var result = await brain.PlanIterationAsync(
                    CreatePipeline("goal-14"), null, TestContext.Current.CancellationToken);

                Assert.NotNull(result);
                Assert.False(result.IsEscalation);
                Assert.NotNull(result.Plan);
                Assert.NotEmpty(result.Plan!.Phases);
                Assert.Equal("actor plan", result.Plan!.Reason);
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 15: flag on but actor is null — falls back to the context path.
    [Fact]
    public async Task ExecuteBrainAsync_FlagOn_ActorNull_FallsBackToContextPath()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir,
                chatClient: new PlanStubClient("call-15", ["coding"], "context plan"),
                factoryChatClientFactory: _ => new ThrowingChatClient());
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                // Drop the actor so routing must fall back to the context path.
                var actorField = typeof(DistributedBrain).GetField("_brainActor", NonPublicInstance)!;
                var actor = (BrainActor?)actorField.GetValue(brain);
                actorField.SetValue(brain, null);
                if (actor is not null)
                    await actor.DisposeAsync();

                await brain.ForkSessionForGoalAsync("goal-15", TestContext.Current.CancellationToken);

                var result = await brain.PlanIterationAsync(
                    CreatePipeline("goal-15"), null, TestContext.Current.CancellationToken);

                Assert.NotNull(result);
                Assert.False(result.IsEscalation);
                Assert.Equal("context plan", result.Plan!.Reason);
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 16: flag on — Tell returns false (mailbox closed) → InvalidOperationException.
    [Fact]
    public async Task ExecuteBrainAsync_FlagOn_TellFalse_Throws()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir,
                factoryChatClientFactory: _ => new PlanStubClient("call-16", ["coding"], "r"));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-16", TestContext.Current.CancellationToken);

                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                await actor!.DisposeAsync();  // closes the mailbox → Tell returns false

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    InvokeExecuteBrainAsync(brain, "prompt", "goal-16", CancellationToken.None));
                Assert.Contains("mailbox closed", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 17: flag on — no child actor for the goal → KeyNotFoundException.
    [Fact]
    public async Task ExecuteBrainAsync_FlagOn_ChildNotFound_Throws()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir,
                factoryChatClientFactory: _ => new PlanStubClient("call-17", ["coding"], "r"));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                // No fork → the actor has no child for this goal.
                await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                    InvokeExecuteBrainAsync(brain, "prompt", "goal-17-missing", CancellationToken.None));
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 18: flag on — empty text is a successful completion, not an error.
    [Fact]
    public async Task ExecuteBrainAsync_FlagOn_EmptyText_SuccessfulCompletion()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir, factoryChatClientFactory: _ => new TextStubClient(""));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-18", TestContext.Current.CancellationToken);

                var (text, toolCall) = await InvokeExecuteBrainAsync(
                    brain, "prompt", "goal-18", TestContext.Current.CancellationToken);

                Assert.True(string.IsNullOrEmpty(text));
                Assert.Null(toolCall);
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 19: flag on — EscalateToolResult is mapped to DistributedBrain.EscalateResult.
    [Fact]
    public async Task ExecuteBrainAsync_FlagOn_EscalateToolResult_MappedToEscalateResult()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir,
                factoryChatClientFactory: _ => new EscalateStubClient("Which API?", "not in codebase"));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-19", TestContext.Current.CancellationToken);

                var (_, toolCall) = await InvokeExecuteBrainAsync(
                    brain, "prompt", "goal-19", TestContext.Current.CancellationToken);

                var escalate = Assert.IsType<DistributedBrain.EscalateResult>(toolCall);
                Assert.Equal("Which API?", escalate.Question);
                Assert.Equal("not in codebase", escalate.Reason);
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 20: flag on — PlanToolResult is mapped to DistributedBrain.IterationPlanResult.
    [Fact]
    public async Task ExecuteBrainAsync_FlagOn_PlanToolResult_MappedToIterationPlanResult()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir,
                factoryChatClientFactory: _ => new PlanStubClient("call-20", ["coding", "testing"], "plan reason"));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-20", TestContext.Current.CancellationToken);

                var (_, toolCall) = await InvokeExecuteBrainAsync(
                    brain, "prompt", "goal-20", TestContext.Current.CancellationToken);

                var plan = Assert.IsType<DistributedBrain.IterationPlanResult>(toolCall);
                Assert.Equal(["coding", "testing"], plan.Phases);
                Assert.Equal("{}", plan.PhaseInstructions);
                Assert.Equal("plan reason", plan.Reason);
                Assert.Null(plan.ModelTiers);
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 21: flag on — an invalid plan from the actor makes PlanIterationAsync fall back to default.
    [Fact]
    public async Task ExecuteBrainAsync_FlagOn_InvalidPlan_PlanIterationReturnsDefault()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir,
                factoryChatClientFactory: _ => new PlanStubClient("call-21", ["invalid_phase"], "bogus"));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-21", TestContext.Current.CancellationToken);

                var result = await brain.PlanIterationAsync(
                    CreatePipeline("goal-21"), null, TestContext.Current.CancellationToken);

                Assert.False(result.IsEscalation);
                var expected = IterationPlan.Default();
                Assert.Equal(expected.Phases, result.Plan!.Phases);
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 22: flag on — SummarizeAndMergeAsync routes the summary LLM call via the actor.
    [Fact]
    public async Task SummarizeAndMergeAsync_FlagOn_RoutesSummaryViaActor()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir,
                chatClient: new ThrowingChatClient(),  // context path would produce no text
                factoryChatClientFactory: _ => new TextStubClient("Summary from actor"));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-22", TestContext.Current.CancellationToken);

                var summary = await brain.SummarizeAndMergeAsync(
                    CreatePipeline("goal-22"), TestContext.Current.CancellationToken);

                Assert.Equal("Summary from actor", summary);

                var master = GetField<AgentSession>(brain, "_masterSession");
                Assert.Contains(master.MessageHistory, m => m.Text.Contains("Summary from actor", StringComparison.Ordinal));

                var contexts = GetField<System.Collections.IDictionary>(brain, "_goalContexts");
                Assert.False(contexts.Contains("goal-22"), "Goal context should be deleted after summarize+merge.");
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 23: flag on — empty/whitespace summary text falls back to the canned summary.
    [Fact]
    public async Task SummarizeAndMergeAsync_FlagOn_EmptySummary_UsesFallback()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir, factoryChatClientFactory: _ => new TextStubClient("   "));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-23", TestContext.Current.CancellationToken);

                var summary = await brain.SummarizeAndMergeAsync(
                    CreatePipeline("goal-23"), TestContext.Current.CancellationToken);

                Assert.Equal("Goal 'goal-23' completed.", summary);
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 24: flag on — ordering: local merge → actor merge mirror → local delete → actor delete mirror.
    //
    // Removal-proof design. `_mirrorDelay` parks the START of every MirrorAsync call, BEFORE the
    // message reaches the actor, so each mirror costs a known `MirrorGate` (1.5s) while the two
    // local steps are effectively instantaneous. A background poller timestamps the first moment
    // each of the four effects becomes observable:
    //   local-merge  → the brain's own master session gains the summary
    //   actor-merge  → the ACTOR's master session gains the summary
    //   local-delete → the goal leaves _goalContexts
    //   actor-delete → the child actor leaves the actor's _childActors
    //
    // The correct production sequence therefore produces this timing fingerprint:
    //   t≈0.0  local-merge   (no gate yet)
    //   t≈1.5  actor-merge   (after the merge mirror's gate)
    //   t≈1.5  local-delete  (immediately after, no gate)
    //   t≈3.0  actor-delete  (after the delete mirror's gate)
    //
    // Asserting BOTH the sequence and the gate positions makes every reordering detectable:
    //   • local-delete before local-merge / before the merge mirror → sequence assertions fail
    //   • actor-delete before actor-merge                          → sequence assertions fail
    //   • local-delete moved AFTER the delete mirror (swapping steps 5 and 6) → the sequence still
    //     looks right (both land at t≈3.0) but local-delete now sits on the FAR side of the second
    //     gate, so the "local-delete follows actor-merge without a gate" assertion fails.
    [Fact]
    public async Task SummarizeAndMergeAsync_FlagOn_Ordering_LocalMergeBeforeActorDelete()
    {
        const string Goal = "goal-24";
        const string Text = "Ordered summary";
        var mirrorGate = TimeSpan.FromMilliseconds(1500);

        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir, factoryChatClientFactory: _ => new TextStubClient(Text));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync(Goal, TestContext.Current.CancellationToken);

                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                var actorMaster = GetField<AgentSession?>(actor!, "_masterSession");
                Assert.NotNull(actorMaster);

                var localMaster = GetField<AgentSession>(brain, "_masterSession");
                var contexts = GetField<System.Collections.IDictionary>(brain, "_goalContexts");
                var children = GetChildActors(actor!);

                // Preconditions: nothing has happened yet.
                Assert.True(children.ContainsKey(Goal));
                Assert.True(contexts.Contains(Goal));
                Assert.False(ContainsSummary(localMaster, Text));
                Assert.False(ContainsSummary(actorMaster!, Text));

                // Gate every MirrorAsync (its reply timeout is 3s, so messages are still delivered).
                SetMirrorDelay(brain, mirrorGate);

                var events = new List<(string Name, TimeSpan At)>();
                using var pollerCts = new CancellationTokenSource();
                var clock = System.Diagnostics.Stopwatch.StartNew();

                var poller = PollAsync();
                async Task PollAsync()
                {
                    await Task.Yield();
                    while (!pollerCts.IsCancellationRequested)
                    {
                        Record(events, clock, "local-merge", () => ContainsSummary(localMaster, Text));
                        Record(events, clock, "actor-merge", () => ContainsSummary(actorMaster!, Text));
                        Record(events, clock, "local-delete", () => !contexts.Contains(Goal));
                        Record(events, clock, "actor-delete", () => !SafeContainsChild(children, Goal));
                        if (events.Count == 4) return;
                        try { await Task.Delay(1, pollerCts.Token); }
                        catch (OperationCanceledException) { return; }
                    }
                }

                var summary = await brain.SummarizeAndMergeAsync(
                    CreatePipeline(Goal), TestContext.Current.CancellationToken);
                Assert.Equal(Text, summary);

                // Let the poller observe an effect that landed on the very last instruction.
                await Task.Delay(250, TestContext.Current.CancellationToken);
                await pollerCts.CancelAsync();
                await poller;

                SetMirrorDelay(brain, null);

                var trace = string.Join(", ", events.Select(e => $"{e.Name}@{e.At.TotalMilliseconds:F0}ms"));

                // Every step must have been observed exactly once, in the mandated sequence.
                Assert.Equal(4, events.Count);
                Assert.Equal(
                    ["local-merge", "actor-merge", "local-delete", "actor-delete"],
                    events.Select(e => e.Name));

                var localMerge = events[0].At;
                var actorMerge = events[1].At;
                var localDelete = events[2].At;
                var actorDelete = events[3].At;

                // The merge mirror's gate sits BETWEEN the local merge and the actor merge —
                // proving the local merge ran first and was not merely observed first.
                Assert.True(actorMerge - localMerge >= mirrorGate * 0.6,
                    $"Expected the merge mirror's gate between local-merge and actor-merge. Trace: {trace}");

                // No gate between the actor merge and the local delete: the local delete must run
                // immediately after the merge mirror, NOT after the delete mirror. This is what
                // catches a swap of step 5 (local delete) and step 6 (actor delete mirror).
                Assert.True(localDelete - actorMerge < mirrorGate * 0.6,
                    $"Local delete must run right after the merge mirror, before the delete mirror's gate. Trace: {trace}");

                // The delete mirror's gate sits between the local delete and the actor delete.
                Assert.True(actorDelete - localDelete >= mirrorGate * 0.6,
                    $"Expected the delete mirror's gate between local-delete and actor-delete. Trace: {trace}");

                // The merge survived the delete on both sides.
                Assert.True(ContainsSummary(localMaster, Text));
                Assert.True(ContainsSummary(actorMaster!, Text));
                Assert.False(GetActiveGoalSessions(actor!).ContainsKey(Goal));
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 25: flag on — caller cancellation propagates out of ExecuteBrainViaActorAsync.
    [Fact]
    public async Task ExecuteBrainAsync_FlagOn_CallerCancellation_Propagates()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir,
                factoryChatClientFactory: _ => new DelayingChatClient(TimeSpan.FromSeconds(30)));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-25", TestContext.Current.CancellationToken);

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    InvokeExecuteBrainAsync(brain, "prompt", "goal-25", cts.Token));
            }
        }
        finally { DeleteDir(dir); }
    }
}
