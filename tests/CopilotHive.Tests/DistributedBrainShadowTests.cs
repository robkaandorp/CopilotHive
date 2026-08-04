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

    /// <summary>
    /// Chat client that reports a different iteration plan on each planning attempt.
    /// Every attempt uses two calls: the first returns the <c>report_iteration_plan</c> tool call,
    /// the second returns plain text so the agent loop terminates.
    /// Records every user prompt it observes so nudge feedback can be asserted.
    /// </summary>
    private sealed class SequencedPlanStubClient : IChatClient
    {
        private readonly string[][] _planSequence;
        private int _callCount;

        internal SequencedPlanStubClient(params string[][] planSequence) => _planSequence = planSequence;

        /// <summary>Number of <c>report_iteration_plan</c> tool calls emitted (one per planning attempt).</summary>
        internal int ToolCallCount { get; private set; }

        /// <summary>Every distinct user message text seen by this client, in order.</summary>
        internal List<string> ObservedUserPrompts { get; } = [];

        public ChatClientMetadata Metadata => new("sequenced-plan", null, "stub-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            foreach (var message in messages.Where(m => m.Role == ChatRole.User))
            {
                var text = message.Text;
                if (!string.IsNullOrEmpty(text) && !ObservedUserPrompts.Contains(text))
                    ObservedUserPrompts.Add(text);
            }

            var call = Interlocked.Increment(ref _callCount);
            if (call % 2 == 1)
            {
                var index = Math.Min((call - 1) / 2, _planSequence.Length - 1);
                ToolCallCount++;
                var toolCallContent = new FunctionCallContent(
                    $"plan-call-{ToolCallCount}", "report_iteration_plan", new Dictionary<string, object?>
                    {
                        ["phases"] = _planSequence[index],
                        ["phase_instructions"] = "{}",
                        ["reason"] = "sequenced plan",
                        ["model_tiers"] = null,
                    });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCallContent]))
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                });
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Plan reported."))
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

    private static HiveConfigFile ActorConfig() =>
        new() { Orchestrator = new OrchestratorConfig() };

    private static object? GetBrainActor(DistributedBrain brain) =>
        typeof(DistributedBrain).GetField("_brainActor", NonPublicInstance)!.GetValue(brain);

    private static void SetActorFactory(DistributedBrain brain, Func<string, BrainActor> factory) =>
        typeof(DistributedBrain).GetField("_actorFactory", NonPublicInstance)!.SetValue(brain, factory);

    private static LlmSessionInfo? FindSession(LlmSessionRegistry registry, string sessionId) =>
        registry.GetAll().FirstOrDefault(s => s.SessionId == sessionId);

    private static bool IsResetting(DistributedBrain brain) =>
        (bool)typeof(DistributedBrain).GetField("_resetting", NonPublicInstance)!.GetValue(brain)!;

    private static void SetResetting(DistributedBrain brain, bool value) =>
        typeof(DistributedBrain).GetField("_resetting", NonPublicInstance)!.SetValue(brain, value);

    private static bool IsConnected(DistributedBrain brain) =>
        (bool)typeof(DistributedBrain).GetField("_connected", NonPublicInstance)!.GetValue(brain)!;

    private static object? GetField(object obj, string name) =>
        obj.GetType().GetField(name, NonPublicInstance)?.GetValue(obj);

    private static T GetField<T>(object obj, string name) => (T)GetField(obj, name)!;

    private static Dictionary<string, GoalBrainActor> GetChildActors(BrainActor actor) =>
        GetField<Dictionary<string, GoalBrainActor>>(actor, "_childActors");

    private static Dictionary<string, string> GetActiveGoalSessions(BrainActor actor) =>
        GetField<Dictionary<string, string>>(actor, "_activeGoalSessions");

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
        DistributedBrain brain, string prompt, string goalId, CancellationToken ct)
    {
        var method = typeof(DistributedBrain).GetMethod("ExecuteBrainAsync", NonPublicInstance)!;
        var task = (Task)method.Invoke(brain, [prompt, goalId, ct, "TestMethod"])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return ((string, DistributedBrain.BrainToolCallResult?))result;
    }

    private static GoalPipeline CreatePipeline(string goalId, string description = "test") =>
        new(new Goal { Id = goalId, Description = description });

    /// <summary>
    /// Creates a DistributedBrain with a fake chat client and a fake chatClientFactory injected
    /// via _actorFactory so the BrainActor's child creation uses fakes.
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
        ILogger<DistributedBrain>? logger = null,
        LlmSessionRegistry? sessionRegistry = null)
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
            hiveConfig: hiveConfig ?? ActorConfig(),
            sessionRegistry: sessionRegistry);

        // Inject a fake actor factory so the shadow BrainActor uses fake chat clients for children.
        var childFactory = factoryChatClientFactory ?? (_ => new TrackingChatClient());
        SetActorFactory(brain, stateDir =>
            new BrainActor(
                "copilot/test-model", 100_000, stateDir, NullLogger.Instance,
                chatClientFactory: childFactory,
                compactionModel: compactionModel,
                hiveConfig: hiveConfig ?? ActorConfig(),
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

                // Do NOT fork a session for this goal. The actor-only routing
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

    // ════════════════════════════════════════════════════════════════════════
    // Criterion 14: StartBrainActorAsync passes all deps
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StartBrainActorAsync_PassesAllDepsToBrainActor()
    {
        var dir = NewTempDir();
        var repoDir = Path.Combine(dir, "repos");
        Directory.CreateDirectory(repoDir);
        try
        {
            var injectedClient = new TrackingChatClient();
            Func<string, IChatClient> chatClientFactory = _ => new TrackingChatClient();
            var hiveConfig = ActorConfig();
            var repoManager = new FakeRepoManager(repoDir);
            var goalStore = new InMemoryGoalStore();
            var knowledgeGraph = new CopilotHive.Knowledge.KnowledgeGraph();
            var sessionRegistry = new LlmSessionRegistry();

            var brain = new DistributedBrain(
                "copilot/test-model",
                NullLogger<DistributedBrain>.Instance,
                maxSteps: 42,
                repoManager: repoManager,
                stateDir: dir,
                goalStore: goalStore,
                chatClient: injectedClient,
                compactionModel: null,
                knowledgeGraph: knowledgeGraph,
                hiveConfig: hiveConfig,
                sessionRegistry: sessionRegistry,
                reasoningEffort: ReasoningEffort.High);
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

                // _reasoningEffort should be High (from the explicitly configured value).
                var actorReasoning = GetField<ReasoningEffort?>(a, "_reasoningEffort");
                Assert.Equal(ReasoningEffort.High, actorReasoning);

                // _workDirectory should be repoManager.WorkDirectory.
                var actorWorkDir = GetField<string?>(a, "_workDirectory");
                Assert.Equal(repoDir, actorWorkDir);

                // _goalStore and _knowledgeGraph should be propagated from the DistributedBrain.
                Assert.Same(goalStore, GetField<CopilotHive.Goals.IGoalStore?>(a, "_goalStore"));
                Assert.Same(knowledgeGraph, GetField<CopilotHive.Knowledge.KnowledgeGraph?>(a, "_knowledgeGraph"));

                // _sessionRegistry should be propagated from the DistributedBrain.
                Assert.Same(sessionRegistry, GetField<CopilotHive.Dashboard.LlmSessionRegistry?>(a, "_sessionRegistry"));
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task StartBrainActorAsync_NoRepoManager_WorkDirectoryIsNull()
    {
        var dir = NewTempDir();
        try
        {
            var brain = new DistributedBrain(
                "copilot/test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: dir,
                chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig());
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);

                var actorWorkDir = GetField<string?>(actor!, "_workDirectory");
                // Without a repo manager, workDirectory should be null (not a fallback to _stateDir).
                Assert.Null(actorWorkDir);
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task StartBrainActorAsync_MigratesLegacyMasterSession_WhenActorMasterMissing()
    {
        var dir = NewTempDir();
        try
        {
            var legacyMaster = AgentSession.Create("brain");
            legacyMaster.MessageHistory.Add(new ChatMessage(ChatRole.User, "LEGACY_MASTER_MARKER"));
            await legacyMaster.SaveAsync(Path.Combine(dir, "brain-master.json"), TestContext.Current.CancellationToken);

            var brain = new DistributedBrain(
                "copilot/test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: dir,
                chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig());
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                var actorMasterPath = Path.Combine(dir, "actors", "brain-master.json");
                Assert.True(File.Exists(actorMasterPath), "Actor master session should be migrated.");
                Assert.True(File.Exists(Path.Combine(dir, "actors", ".migrated")), ".migrated marker should be created.");

                var actorMaster = await AgentSession.LoadAsync(actorMasterPath, TestContext.Current.CancellationToken);
                Assert.Contains(actorMaster.MessageHistory,
                    m => m.Text.Contains("LEGACY_MASTER_MARKER", StringComparison.Ordinal));
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task StartBrainActorAsync_DoesNotOverwriteExistingActorMasterSession()
    {
        var dir = NewTempDir();
        try
        {
            var legacyMaster = AgentSession.Create("brain");
            legacyMaster.MessageHistory.Add(new ChatMessage(ChatRole.User, "LEGACY_MASTER_MARKER"));
            await legacyMaster.SaveAsync(Path.Combine(dir, "brain-master.json"), TestContext.Current.CancellationToken);

            Directory.CreateDirectory(Path.Combine(dir, "actors"));
            var existingMaster = AgentSession.Create("brain");
            existingMaster.MessageHistory.Add(new ChatMessage(ChatRole.User, "EXISTING_ACTOR_MASTER_MARKER"));
            var actorMasterPath = Path.Combine(dir, "actors", "brain-master.json");
            await existingMaster.SaveAsync(actorMasterPath, TestContext.Current.CancellationToken);

            var brain = new DistributedBrain(
                "copilot/test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: dir,
                chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig());
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                Assert.True(File.Exists(Path.Combine(dir, "actors", ".migrated")), ".migrated marker should be created.");

                var actorMaster = await AgentSession.LoadAsync(actorMasterPath, TestContext.Current.CancellationToken);
                Assert.DoesNotContain(actorMaster.MessageHistory,
                    m => m.Text.Contains("LEGACY_MASTER_MARKER", StringComparison.Ordinal));
                Assert.Contains(actorMaster.MessageHistory,
                    m => m.Text.Contains("EXISTING_ACTOR_MASTER_MARKER", StringComparison.Ordinal));
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task StartBrainActorAsync_MigratesLegacyGoalSessions_WhenActorGoalSessionsMissing()
    {
        var dir = NewTempDir();
        try
        {
            for (int i = 1; i <= 2; i++)
            {
                var goalId = $"goal-{i}";
                var session = AgentSession.Create($"brain-goal-{goalId}");
                session.MessageHistory.Add(new ChatMessage(ChatRole.User, $"LEGACY_GOAL_{i}_MARKER"));
                await session.SaveAsync(Path.Combine(dir, $"brain-goal-{goalId}.json"), TestContext.Current.CancellationToken);
            }

            var brain = new DistributedBrain(
                "copilot/test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: dir,
                chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig());
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                Assert.True(File.Exists(Path.Combine(dir, "actors", ".migrated")), ".migrated marker should be created.");

                for (int i = 1; i <= 2; i++)
                {
                    var goalId = $"goal-{i}";
                    var actorGoalPath = Path.Combine(dir, "actors", $"brain-goal-{goalId}.json");
                    Assert.True(File.Exists(actorGoalPath), $"Actor goal session for {goalId} should be migrated.");

                    var actorGoalSession = await AgentSession.LoadAsync(actorGoalPath, TestContext.Current.CancellationToken);
                    Assert.Contains(actorGoalSession.MessageHistory,
                        m => m.Text.Contains($"LEGACY_GOAL_{i}_MARKER", StringComparison.Ordinal));
                }
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task StartBrainActorAsync_DoesNotOverwriteExistingActorGoalSessions()
    {
        var dir = NewTempDir();
        try
        {
            for (int i = 1; i <= 2; i++)
            {
                var goalId = $"goal-{i}";
                var legacySession = AgentSession.Create($"brain-goal-{goalId}");
                legacySession.MessageHistory.Add(new ChatMessage(ChatRole.User, $"LEGACY_GOAL_{i}_MARKER"));
                await legacySession.SaveAsync(Path.Combine(dir, $"brain-goal-{goalId}.json"), TestContext.Current.CancellationToken);
            }

            Directory.CreateDirectory(Path.Combine(dir, "actors"));
            for (int i = 1; i <= 2; i++)
            {
                var goalId = $"goal-{i}";
                var existingSession = AgentSession.Create($"brain-goal-{goalId}");
                existingSession.MessageHistory.Add(new ChatMessage(ChatRole.User, $"EXISTING_ACTOR_GOAL_{i}_MARKER"));
                await existingSession.SaveAsync(Path.Combine(dir, "actors", $"brain-goal-{goalId}.json"), TestContext.Current.CancellationToken);
            }

            var brain = new DistributedBrain(
                "copilot/test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: dir,
                chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig());
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                Assert.True(File.Exists(Path.Combine(dir, "actors", ".migrated")), ".migrated marker should be created.");

                for (int i = 1; i <= 2; i++)
                {
                    var goalId = $"goal-{i}";
                    var actorGoalPath = Path.Combine(dir, "actors", $"brain-goal-{goalId}.json");
                    var actorGoalSession = await AgentSession.LoadAsync(actorGoalPath, TestContext.Current.CancellationToken);

                    Assert.DoesNotContain(actorGoalSession.MessageHistory,
                        m => m.Text.Contains($"LEGACY_GOAL_{i}_MARKER", StringComparison.Ordinal));
                    Assert.Contains(actorGoalSession.MessageHistory,
                        m => m.Text.Contains($"EXISTING_ACTOR_GOAL_{i}_MARKER", StringComparison.Ordinal));
                }
            }
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Criterion 11-15: marker-based migration durability
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StartBrainActorAsync_Migration_CreatesMigratedMarker()
    {
        var dir = NewTempDir();
        try
        {
            var legacyMaster = AgentSession.Create("brain");
            legacyMaster.MessageHistory.Add(new ChatMessage(ChatRole.User, "LEGACY_MASTER"));
            await legacyMaster.SaveAsync(Path.Combine(dir, "brain-master.json"), TestContext.Current.CancellationToken);

            var legacyGoal = AgentSession.Create("brain-goal-g1");
            legacyGoal.MessageHistory.Add(new ChatMessage(ChatRole.User, "LEGACY_GOAL"));
            await legacyGoal.SaveAsync(Path.Combine(dir, "brain-goal-g1.json"), TestContext.Current.CancellationToken);

            var brain = new DistributedBrain(
                "copilot/test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: dir,
                chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig());
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                Assert.True(File.Exists(Path.Combine(dir, "actors", ".migrated")), "Marker should be created.");
                Assert.True(File.Exists(Path.Combine(dir, "actors", "brain-master.json")), "Master should be copied.");
                Assert.True(File.Exists(Path.Combine(dir, "actors", "brain-goal-g1.json")), "Goal should be copied.");
                Assert.True(File.Exists(Path.Combine(dir, "brain-master.json")), "Legacy master should still exist.");
                Assert.True(File.Exists(Path.Combine(dir, "brain-goal-g1.json")), "Legacy goal should still exist.");
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task StartBrainActorAsync_Migration_DoesNotReRunWhenMarkerExists()
    {
        var dir = NewTempDir();
        try
        {
            var legacyMaster = AgentSession.Create("brain");
            legacyMaster.MessageHistory.Add(new ChatMessage(ChatRole.User, "LEGACY"));
            await legacyMaster.SaveAsync(Path.Combine(dir, "brain-master.json"), TestContext.Current.CancellationToken);

            var brain1 = new DistributedBrain(
                "copilot/test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: dir,
                chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig());
            await using (brain1)
            {
                await brain1.ConnectAsync(TestContext.Current.CancellationToken);
                Assert.True(File.Exists(Path.Combine(dir, "actors", ".migrated")));
            }

            // Delete the migrated actor master and set a trap copier to prove migration does NOT re-copy.
            var actorMasterPath = Path.Combine(dir, "actors", "brain-master.json");
            File.Delete(actorMasterPath);

            var copyInvoked = false;
            var brain2 = new DistributedBrain(
                "copilot/test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: dir,
                chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig());
            brain2._fileCopier = (src, dst) =>
            {
                copyInvoked = true;
                return false;
            };

            await using (brain2)
            {
                await brain2.ConnectAsync(TestContext.Current.CancellationToken);

                Assert.False(copyInvoked, "Migration should be skipped when .migrated marker exists.");

                // The actor's own startup may save a fresh master; the key point is that the legacy
                // marker did NOT get re-migrated into it.
                var actorMasterContent = await File.ReadAllTextAsync(actorMasterPath, TestContext.Current.CancellationToken);
                Assert.DoesNotContain("LEGACY", actorMasterContent);

                Assert.True(File.Exists(Path.Combine(dir, "actors", ".migrated")), "Marker should still exist.");
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task StartBrainActorAsync_Migration_FailedCopy_CreatesMarker_RetainsSource()
    {
        var dir = NewTempDir();
        try
        {
            var legacyMaster = AgentSession.Create("brain");
            legacyMaster.MessageHistory.Add(new ChatMessage(ChatRole.User, "LEGACY"));
            var legacyPath = Path.Combine(dir, "brain-master.json");
            await legacyMaster.SaveAsync(legacyPath, TestContext.Current.CancellationToken);

            var brain = new DistributedBrain(
                "copilot/test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: dir,
                chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig());
            brain._fileCopier = (src, dst) => false;

            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                Assert.True(File.Exists(Path.Combine(dir, "actors", ".migrated")), "Marker should be created despite copy failure.");
                Assert.True(File.Exists(legacyPath), "Legacy source must be retained.");

                // The actor's own startup saved a fresh master; verify it does NOT contain the legacy marker.
                var actorMasterPath = Path.Combine(dir, "actors", "brain-master.json");
                Assert.True(File.Exists(actorMasterPath), "Actor master file should exist after actor startup.");
                var actorMasterContent = await File.ReadAllTextAsync(actorMasterPath, TestContext.Current.CancellationToken);
                Assert.DoesNotContain("LEGACY", actorMasterContent);
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task StartBrainActorAsync_Migration_Conflict_KeepsActorAndLegacy()
    {
        var dir = NewTempDir();
        try
        {
            var legacyMaster = AgentSession.Create("brain");
            legacyMaster.MessageHistory.Add(new ChatMessage(ChatRole.User, "LEGACY_MARKER"));
            await legacyMaster.SaveAsync(Path.Combine(dir, "brain-master.json"), TestContext.Current.CancellationToken);

            Directory.CreateDirectory(Path.Combine(dir, "actors"));
            var actorMaster = AgentSession.Create("brain");
            actorMaster.MessageHistory.Add(new ChatMessage(ChatRole.User, "ACTOR_MARKER"));
            var actorMasterPath = Path.Combine(dir, "actors", "brain-master.json");
            await actorMaster.SaveAsync(actorMasterPath, TestContext.Current.CancellationToken);

            var brain = new DistributedBrain(
                "copilot/test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: dir,
                chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig());
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                Assert.True(File.Exists(Path.Combine(dir, "actors", ".migrated")), "Marker should be created.");

                var loadedActorMaster = await AgentSession.LoadAsync(actorMasterPath, TestContext.Current.CancellationToken);
                Assert.Contains(loadedActorMaster.MessageHistory,
                    m => m.Text.Contains("ACTOR_MARKER", StringComparison.Ordinal));
                Assert.DoesNotContain(loadedActorMaster.MessageHistory,
                    m => m.Text.Contains("LEGACY_MARKER", StringComparison.Ordinal));

                var loadedLegacyMaster = await AgentSession.LoadAsync(Path.Combine(dir, "brain-master.json"), TestContext.Current.CancellationToken);
                Assert.Contains(loadedLegacyMaster.MessageHistory,
                    m => m.Text.Contains("LEGACY_MARKER", StringComparison.Ordinal));
            }
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Criteria 16-22: reset durability
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResetSessionAsync_DeletesAllSessionFilesAndRestartsWithEmptyHistory()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("reset-goal", TestContext.Current.CancellationToken);

                // Simulate leftover legacy state-dir session files from a pre-actor run: reset must
                // clear those too, not just the actor's own files.
                File.WriteAllText(Path.Combine(dir, "brain-master.json"), "{}");
                File.WriteAllText(Path.Combine(dir, "brain-goal-reset-goal.json"), "{}");

                Assert.True(File.Exists(Path.Combine(dir, "brain-master.json")));
                Assert.True(File.Exists(Path.Combine(dir, "brain-goal-reset-goal.json")));
                Assert.True(File.Exists(Path.Combine(dir, "actors", "brain-master.json")));
                Assert.True(File.Exists(Path.Combine(dir, "actors", "brain-goal-reset-goal.json")));
                Assert.True(File.Exists(Path.Combine(dir, "actors", ".migrated")));

                await brain.ResetSessionAsync(TestContext.Current.CancellationToken);

                Assert.False(File.Exists(Path.Combine(dir, "brain-master.json")), "stateDir master should be deleted.");
                Assert.False(File.Exists(Path.Combine(dir, "brain-goal-reset-goal.json")), "stateDir goal should be deleted.");
                Assert.False(File.Exists(Path.Combine(dir, "actors", "brain-goal-reset-goal.json")), "actor goal should be deleted.");

                var freshActorMasterPath = Path.Combine(dir, "actors", "brain-master.json");
                Assert.True(File.Exists(freshActorMasterPath), "New actor master should be created after restart.");

                var freshMaster = await AgentSession.LoadAsync(freshActorMasterPath, TestContext.Current.CancellationToken);
                Assert.Empty(freshMaster.MessageHistory);

                var stats = ((BrainActor?)GetBrainActor(brain))?.Tell(BrainActorMessages.CreateGetStatsMessage()) ?? false;
                Assert.True(stats, "Recreated actor should be live.");
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ResetSessionAsync_DeletionFailure_ActorsMaster_Throws()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("del-fail", TestContext.Current.CancellationToken);

                brain._fileDeleter = path =>
                {
                    if (path.Contains(Path.Combine(dir, "actors")) && path.Contains("brain-master"))
                        throw new IOException("simulated actors master delete failure");
                    File.Delete(path);
                };

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => brain.ResetSessionAsync(TestContext.Current.CancellationToken));
                Assert.Contains("Failed to clear session state during reset", ex.Message);
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ResetSessionAsync_DeletionFailure_StateDirMaster_Throws()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("state-fail", TestContext.Current.CancellationToken);

                // Leftover legacy state-dir master file whose deletion will be made to fail.
                File.WriteAllText(Path.Combine(dir, "brain-master.json"), "{}");

                brain._fileDeleter = path =>
                {
                    if (path.StartsWith(dir) && !path.Contains("actors") && path.Contains("brain-master"))
                        throw new IOException("simulated state dir master delete failure");
                    File.Delete(path);
                };

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => brain.ResetSessionAsync(TestContext.Current.CancellationToken));
                Assert.Contains("Failed to clear session state during reset", ex.Message);
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ResetSessionAsync_DeletionFailure_ActorsGoal_Throws()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-fail", TestContext.Current.CancellationToken);

                brain._fileDeleter = path =>
                {
                    if (path.Contains(Path.Combine(dir, "actors")) && path.Contains("brain-goal-goal-fail"))
                        throw new IOException("simulated actors goal delete failure");
                    File.Delete(path);
                };

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => brain.ResetSessionAsync(TestContext.Current.CancellationToken));
                Assert.Contains("Failed to clear session state during reset", ex.Message);
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ResetSessionAsync_DeletesMigratedMarker()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                var markerPath = Path.Combine(dir, "actors", ".migrated");
                Assert.True(File.Exists(markerPath));

                // Track whether the deleter was invoked for the .migrated marker.
                var markerDeleted = false;
                brain._fileDeleter = path =>
                {
                    if (path == markerPath)
                        markerDeleted = true;
                    File.Delete(path);
                };

                await brain.ResetSessionAsync(TestContext.Current.CancellationToken);

                Assert.True(markerDeleted, "Reset must delete the .migrated marker via _fileDeleter.");
                // MigrateSessionFiles is suppressed while _resetting is set, so the restarting actor
                // must NOT re-import the legacy session files the reset just deleted — and therefore
                // must not recreate the marker either.
                Assert.False(File.Exists(markerPath),
                    "Marker must not be recreated: migration is suppressed during a reset.");
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ResetSessionAsync_RestartFailure_RollsbackAndRecovers()
    {
        var dir = NewTempDir();
        var registry = new LlmSessionRegistry();
        try
        {
            var brain = NewShadowBrain(dir, sessionRegistry: registry);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                Assert.NotNull(FindSession(registry, "brain-master"));

                SetActorFactory(brain, _ => throw new InvalidOperationException("restart factory boom"));

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => brain.ResetSessionAsync(TestContext.Current.CancellationToken));
                Assert.Contains("restart factory boom", ex.Message, StringComparison.Ordinal);

                // The actor is the only execution path, so a failed restart must leave the brain
                // fully rolled back: disconnected, unregistered, and no longer flagged as resetting.
                Assert.False(IsConnected(brain), "Brain must not report itself connected without an actor.");
                Assert.Null(GetBrainActor(brain));
                Assert.Null(FindSession(registry, "brain-master"));
                Assert.False(IsResetting(brain), "_resetting must be cleared in the finally block.");

                // Recovery: a working factory plus ConnectAsync restores a usable brain.
                SetActorFactory(brain, stateDir =>
                    new BrainActor("copilot/test-model", 100_000, stateDir, NullLogger.Instance,
                        chatClientFactory: _ => new TrackingChatClient(),
                        workDirectory: dir));

                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                Assert.True(IsConnected(brain));
                Assert.NotNull(GetBrainActor(brain));
                Assert.NotNull(FindSession(registry, "brain-master"));
            }
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Criteria 22/23/26: concurrency contracts around reset and dispose
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ForkSessionForGoalAsync_DuringActiveReset_Throws()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir);

            // The first factory call satisfies ConnectAsync; the second (the reset's restart) blocks
            // until the test releases it, so the reset genuinely stays in-flight while we fork.
            var restartEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRestart = new ManualResetEventSlim(false);
            var firstCall = true;
            SetActorFactory(brain, stateDir =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    return new BrainActor("copilot/test-model", 100_000, stateDir, NullLogger.Instance,
                        chatClientFactory: _ => new TrackingChatClient(),
                        workDirectory: dir);
                }

                restartEntered.TrySetResult(true);
                releaseRestart.Wait(TimeSpan.FromSeconds(30));
                throw new InvalidOperationException("restart fails");
            });

            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                // Reset runs on a background thread. It flips _resetting, detaches the old actor and
                // then parks inside the restart factory while still holding _sessionLock.
                var resetTask = Task.Run(
                    () => brain.ResetSessionAsync(TestContext.Current.CancellationToken),
                    TestContext.Current.CancellationToken);
                await restartEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

                // A fork issued while that reset is genuinely in flight must fail fast. This only
                // passes because ForkSessionForGoalAsync checks _resetting BEFORE touching the
                // actor — a lock-first implementation would deadlock behind the reset instead.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => brain.ForkSessionForGoalAsync("g1", TestContext.Current.CancellationToken)
                        .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                Assert.Contains("reset", ex.Message, StringComparison.OrdinalIgnoreCase);

                releaseRestart.Set();
                await Assert.ThrowsAsync<InvalidOperationException>(() => resetTask);

                // The failed reset rolled the brain back rather than leaving it half-reset.
                Assert.False(IsResetting(brain), "_resetting must be cleared once the reset unwinds.");
                Assert.False(IsConnected(brain));
                Assert.Null(GetBrainActor(brain));
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task GetStats_WhenActorDetached_ReturnsNull()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                Assert.NotNull(brain.GetStats());

                // A reset detaches the actor. GetStats must report null because the actor is gone,
                // NOT because it inspects the _resetting flag (which it deliberately never reads).
                var actor = (BrainActor?)GetBrainActor(brain);
                typeof(DistributedBrain).GetField("_brainActor", NonPublicInstance)!.SetValue(brain, null);
                if (actor is not null)
                    await actor.DisposeAsync();

                Assert.Null(brain.GetStats());

                // Proof that _resetting plays no part: with the actor detached the answer is null
                // regardless of the flag's value.
                SetResetting(brain, true);
                try { Assert.Null(brain.GetStats()); }
                finally { SetResetting(brain, false); }
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task DisposeAsync_DisposesInjectedChatClientAfterActor()
    {
        var dir = NewTempDir();
        var injectedClient = new TrackingChatClient();
        try
        {
            var brain = NewShadowBrain(dir, chatClient: injectedClient);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                Assert.True(actor!.IsStarted);

                // While the actor is alive the borrowed client must remain usable: child actors
                // receive it with ownsClient=false, so an early dispose would break them.
                Assert.False(injectedClient.WasDisposed);
            }

            // Disposal order matters — the actor (and every child that borrows the client) is torn
            // down first, and only then is the injected client released, exactly once.
            Assert.True(injectedClient.WasDisposed,
                "Injected chat client must be disposed after brain disposal.");
            Assert.Equal(1, injectedClient.DisposeCallCount);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ConnectAsync_ConcurrentDispose_RacesAndThrowsObjectDisposed()
    {
        var dir = NewTempDir();
        var registry = new LlmSessionRegistry();
        try
        {
            var brain = NewShadowBrain(dir, sessionRegistry: registry);

            // The factory stalls inside StartBrainActorAsync while ConnectAsync holds _sessionLock,
            // opening a real window for a concurrent DisposeAsync — no reflection required.
            var factoryEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFactory = new ManualResetEventSlim(false);
            SetActorFactory(brain, stateDir =>
            {
                factoryEntered.TrySetResult(true);
                releaseFactory.Wait(TimeSpan.FromSeconds(30));
                return new BrainActor("copilot/test-model", 100_000, stateDir, NullLogger.Instance,
                    chatClientFactory: _ => new TrackingChatClient(),
                    workDirectory: dir);
            });

            // Started on a background thread: the factory blocks its caller synchronously, and
            // ConnectAsync runs inline up to that point, so calling it directly would stall the test.
            var connectTask = Task.Run(
                () => brain.ConnectAsync(TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);
            await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // A genuine DisposeAsync: it sets _disposing under _lifecycleLock and then blocks on
            // _sessionLock, which ConnectAsync still holds.
            var disposeTask = brain.DisposeAsync().AsTask();
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(disposeTask.IsCompleted, "Dispose must be parked on _sessionLock held by Connect.");

            releaseFactory.Set();

            // ConnectAsync's second _disposing check now observes the concurrent dispose and rolls
            // back the actor it just published rather than handing back a doomed brain.
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await connectTask);
            Assert.False(IsConnected(brain));
            Assert.Null(GetBrainActor(brain));
            Assert.Null(FindSession(registry, "brain-master"));

            await disposeTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ResetSessionAsync_Unconnected_DoesNotTouchFiles()
    {
        var dir = NewTempDir();
        try
        {
            var legacyMaster = AgentSession.Create("brain");
            legacyMaster.MessageHistory.Add(new ChatMessage(ChatRole.User, "LEGACY"));
            await legacyMaster.SaveAsync(Path.Combine(dir, "brain-master.json"), TestContext.Current.CancellationToken);

            var brain = new DistributedBrain(
                "copilot/test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: dir,
                chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig());

            Assert.False(Directory.Exists(Path.Combine(dir, "actors")));

            await brain.ResetSessionAsync(TestContext.Current.CancellationToken);

            Assert.False(Directory.Exists(Path.Combine(dir, "actors")));
            Assert.True(File.Exists(Path.Combine(dir, "brain-master.json")), "Legacy master should be untouched.");
            Assert.Null(GetBrainActor(brain));
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Criterion 15: DisposeAsyncCore — actor disposed BEFORE injected client
    // ════════════════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════════════════
    // Criterion 20: Fork failure — compaction client creation fails
    // ════════════════════════════════════════════════════════════════════════

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
                    hiveConfig: ActorConfig());

                SetActorFactory(brain, stateDir =>
                    new BrainActor(
                        "copilot/test-model", 100_000, stateDir, NullLogger.Instance,
                        chatClientFactory: _ => shadowClient,
                        compactionModel: "github/some-model"));  // Bad compaction model — will throw

                await using (brain)
                {
                    await brain.ConnectAsync(TestContext.Current.CancellationToken);

                    // The actor is authoritative, so its fork failure (compaction client creation
                    // throws) now propagates out of ForkSessionForGoalAsync.
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => brain.ForkSessionForGoalAsync("goal-fail", TestContext.Current.CancellationToken));

                    // Verify the actor's state — no child should be registered.
                    var actor = (BrainActor?)GetBrainActor(brain);
                    Assert.NotNull(actor);
                    var children = GetChildActors(actor!);
                    Assert.False(children.ContainsKey("goal-fail"),
                        "Actor should not have a child for goal-fail (compaction creation failed).");
                    var sessions = GetActiveGoalSessions(actor!);
                    Assert.False(sessions.ContainsKey("goal-fail"),
                        "Actor should not have a session entry for goal-fail.");

                    // The raw chat client should have been disposed (parent owned it, pre-constructor failure).
                    Assert.True(shadowClient.WasDisposed,
                        "Raw chat client should be disposed after pre-constructor failure in the fork.");
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
        try
        {
            var shadowClient = new TrackingChatClient();

            var brain = new DistributedBrain(
                "copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: dir, chatClient: new TrackingChatClient(),
                hiveConfig: ActorConfig());

            // The actor gets a VALID state directory so ConnectAsync succeeds and
            // _brainActor is set. The fork failure is induced afterwards.
            string? actorStateDir = null;
            SetActorFactory(brain, stateDir =>
            {
                actorStateDir = stateDir;
                return new BrainActor(
                    "copilot/test-model", 100_000, stateDir, NullLogger.Instance,
                    chatClientFactory: _ => shadowClient,
                    sessionRegistry: new LlmSessionRegistry());
            });

            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                Assert.NotNull(actorStateDir);

                // Occupy the goal session file path with a DIRECTORY so the shadow actor's
                // SaveSessionAsync cannot write the file. The failure happens after the child
                // actor has been constructed and started — a true post-constructor failure.
                var blockedGoalSessionPath = Path.Combine(actorStateDir!, "brain-goal-goal-postfail.json");
                Directory.CreateDirectory(blockedGoalSessionPath);

                // The actor is authoritative, so the faulted fork now propagates to the caller.
                await Assert.ThrowsAnyAsync<Exception>(
                    () => brain.ForkSessionForGoalAsync("goal-postfail", TestContext.Current.CancellationToken));

                // ForkSessionAsync assigns both dictionaries only AFTER the save succeeds, so a
                // failing save must leave no child and no session entry behind.
                Assert.False(GetChildActors(actor!).ContainsKey("goal-postfail"),
                    "Actor must not track a child for goal-postfail when the session save fails.");
                Assert.False(GetActiveGoalSessions(actor!).ContainsKey("goal-postfail"),
                    "Actor must not track a session entry for goal-postfail when the session save fails.");
            }
        }
        finally
        {
            // Recursive delete also removes the directory planted at the goal session path.
            DeleteDir(dir);
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
    // Additional: Verify that ForkSessionForGoalAsync
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
    public async Task ExecuteBrainAsync_RoutesToActor_ReturnsResultWithToolCall()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir,
                chatClient: new ThrowingChatClient(),  // context path would fail — proves the actor path ran
                factoryChatClientFactory: _ => new PlanStubClient("call-14", ["coding", "testing", "review", "merging"], "actor plan"));
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
    public async Task ExecuteBrainAsync_ActorNull_Throws()
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
                await brain.ForkSessionForGoalAsync("goal-15", TestContext.Current.CancellationToken);

                // Drop the actor. There is no context fallback any more, so every subsequent
                // Brain operation must fail loudly rather than silently using a local path.
                var actorField = typeof(DistributedBrain).GetField("_brainActor", NonPublicInstance)!;
                var actor = (BrainActor?)actorField.GetValue(brain);
                actorField.SetValue(brain, null);
                if (actor is not null)
                    await actor.DisposeAsync();

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    InvokeExecuteBrainAsync(brain, "prompt", "goal-15", CancellationToken.None));
                Assert.Contains("BrainActor not available", ex.Message, StringComparison.Ordinal);
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 16: Tell returns false (mailbox closed) → InvalidOperationException.
    [Fact]
    public async Task ExecuteBrainAsync_TellFalse_Throws()
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
    public async Task ExecuteBrainAsync_ChildNotFound_Throws()
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
    public async Task ExecuteBrainAsync_EmptyText_SuccessfulCompletion()
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
    public async Task ExecuteBrainAsync_EscalateToolResult_MappedToEscalateResult()
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
    public async Task ExecuteBrainAsync_PlanToolResult_MappedToIterationPlanResult()
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

    // Criterion 21: flag on — an invalid plan from the actor makes PlanIterationAsync fail explicitly.
    [Fact]
    public async Task ExecuteBrainAsync_InvalidPlan_PlanIterationReturnsFailed()
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
                Assert.True(result.IsFailed);
                Assert.Null(result.Plan);
                Assert.NotNull(result.FailureReason);
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 22: SummarizeAndMergeAsync routes the summary LLM call via the actor.
    [Fact]
    public async Task SummarizeAndMergeAsync_RoutesSummaryViaActor()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewShadowBrain(dir,
                chatClient: new ThrowingChatClient(),  // must never be used — the actor owns execution
                factoryChatClientFactory: _ => new TextStubClient("Summary from actor"));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-22", TestContext.Current.CancellationToken);

                var summary = await brain.SummarizeAndMergeAsync(
                    CreatePipeline("goal-22"), TestContext.Current.CancellationToken);

                Assert.Equal("Summary from actor", summary);

                // The summary is merged into the actor's master session, not any local state.
                var actor = (BrainActor)GetBrainActor(brain)!;
                var master = GetField<AgentSession>(actor, "_masterSession");
                Assert.Contains(master.MessageHistory, m => m.Text.Contains("Summary from actor", StringComparison.Ordinal));

                Assert.False(SafeContainsChild(GetChildActors(actor), "goal-22"),
                    "Goal child actor should be deleted after summarize+merge.");
            }
        }
        finally { DeleteDir(dir); }
    }

    // Criterion 23: empty/whitespace summary text falls back to the canned summary.
    [Fact]
    public async Task SummarizeAndMergeAsync_EmptySummary_UsesFallback()
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

    // Criterion 25: flag on — caller cancellation propagates out of ExecuteBrainViaActorAsync.
    [Fact]
    public async Task ExecuteBrainAsync_CallerCancellation_Propagates()
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

    // ── validate-and-reject planning contract ───────────────────────────────

    /// <summary>
    /// An invalid first plan is rejected, the rejection reasons are fed back to the Brain,
    /// and the corrected second plan is accepted.
    /// </summary>
    [Fact]
    public async Task PlanIterationAsync_InvalidThenValidPlan_FeedsBackReasonsAndAcceptsReplan()
    {
        var dir = NewTempDir();
        try
        {
            SequencedPlanStubClient? stub = null;
            var brain = NewShadowBrain(dir,
                factoryChatClientFactory: _ => stub = new SequencedPlanStubClient(
                    ["review"],                                    // invalid: no content phase, no merging
                    ["coding", "testing", "review", "merging"]));   // valid
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-reject-1", TestContext.Current.CancellationToken);

                var result = await brain.PlanIterationAsync(
                    CreatePipeline("goal-reject-1"), null, TestContext.Current.CancellationToken);

                Assert.False(result.IsFailed);
                Assert.False(result.IsEscalation);
                Assert.NotNull(result.Plan);
                Assert.Equal(
                    [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
                    result.Plan!.Phases);

                Assert.NotNull(stub);
                Assert.Equal(2, stub!.ToolCallCount);

                // The nudge prompt carried the rejection reasons back to the Brain.
                var nudge = stub.ObservedUserPrompts.FirstOrDefault(p => p.Contains("rejected because"));
                Assert.NotNull(nudge);
                Assert.Contains("R1 (Occupancy)", nudge!);
                Assert.Contains("R5 (Merging)", nudge!);
            }
        }
        finally { DeleteDir(dir); }
    }

    /// <summary>
    /// When the Brain never produces a valid plan, planning fails after the bounded
    /// 3-attempt budget — no default plan is substituted.
    /// </summary>
    [Fact]
    public async Task PlanIterationAsync_NeverValid_ReturnsFailedAfterThreeAttempts()
    {
        var dir = NewTempDir();
        try
        {
            SequencedPlanStubClient? stub = null;
            var brain = NewShadowBrain(dir,
                factoryChatClientFactory: _ => stub = new SequencedPlanStubClient(["review"]));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-reject-2", TestContext.Current.CancellationToken);

                var result = await brain.PlanIterationAsync(
                    CreatePipeline("goal-reject-2"), null, TestContext.Current.CancellationToken);

                Assert.True(result.IsFailed);
                Assert.Null(result.Plan);
                // Exhaustion preserves WHY the last plan was rejected — the reason is not generic.
                Assert.StartsWith(
                    "Brain failed to produce a valid iteration plan after 3 attempts. Last rejection: ",
                    result.FailureReason);
                Assert.Contains("R1 (Occupancy)", result.FailureReason);

                // Budget is bounded at 3 planning attempts.
                Assert.NotNull(stub);
                Assert.Equal(3, stub!.ToolCallCount);
            }
        }
        finally { DeleteDir(dir); }
    }

    // ── unrecognized phase names are rejected in-loop, never silently dropped ────

    /// <summary>
    /// An unrecognized phase name does NOT throw out of the actor mapping — it is rejected
    /// inside <c>PlanIterationAsync</c>'s bounded loop with an actionable reason naming the
    /// bad token, and a valid replacement submitted within budget is accepted.
    /// </summary>
    [Fact]
    public async Task PlanIterationAsync_UnrecognizedPhaseName_RejectedInLoopThenValidReplanAccepted()
    {
        var dir = NewTempDir();
        try
        {
            SequencedPlanStubClient? stub = null;
            var brain = NewShadowBrain(dir,
                factoryChatClientFactory: _ => stub = new SequencedPlanStubClient(
                    ["coding", "GarbageName", "testing", "review", "merging"],  // unrecognized token
                    ["coding", "testing", "review", "merging"]));               // valid
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-unrecognized-1", TestContext.Current.CancellationToken);

                var result = await brain.PlanIterationAsync(
                    CreatePipeline("goal-unrecognized-1"), null, TestContext.Current.CancellationToken);

                // No early throw: planning completed and the corrected plan was accepted.
                Assert.False(result.IsFailed);
                Assert.False(result.IsEscalation);
                Assert.NotNull(result.Plan);
                Assert.Equal(
                    [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
                    result.Plan!.Phases);
                Assert.Empty(result.Plan.UnrecognizedPhases);

                Assert.NotNull(stub);
                Assert.Equal(2, stub!.ToolCallCount);

                // The rejection reason fed back to the Brain names the offending token.
                var nudge = stub.ObservedUserPrompts.FirstOrDefault(p => p.Contains("rejected because"));
                Assert.NotNull(nudge);
                Assert.Contains("Unrecognized phase names: GarbageName", nudge!);
                Assert.Contains("coding, testing, docwriting, review, improve, merging", nudge!);
            }
        }
        finally { DeleteDir(dir); }
    }

    /// <summary>
    /// When the Brain keeps submitting unrecognized phase names, the goal fails after the
    /// bounded attempt budget and the failure reason still names the bad token.
    /// </summary>
    [Fact]
    public async Task PlanIterationAsync_AlwaysUnrecognizedPhase_FailsWithReasonNamingTheToken()
    {
        var dir = NewTempDir();
        try
        {
            SequencedPlanStubClient? stub = null;
            var brain = NewShadowBrain(dir,
                factoryChatClientFactory: _ => stub = new SequencedPlanStubClient(
                    ["coding", "GarbageName", "testing", "review", "merging"]));
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-unrecognized-2", TestContext.Current.CancellationToken);

                var result = await brain.PlanIterationAsync(
                    CreatePipeline("goal-unrecognized-2"), null, TestContext.Current.CancellationToken);

                Assert.True(result.IsFailed);
                Assert.Null(result.Plan);
                Assert.StartsWith(
                    "Brain failed to produce a valid iteration plan after 3 attempts. Last rejection: ",
                    result.FailureReason);
                Assert.Contains("GarbageName", result.FailureReason);

                Assert.NotNull(stub);
                Assert.Equal(3, stub!.ToolCallCount);
            }
        }
        finally { DeleteDir(dir); }
    }

    /// <summary>
    /// A numeric-string phase token (e.g. "1") is rejected as unrecognized inside the bounded
    /// loop — it is NOT silently mapped to a GoalPhase via Enum.TryParse. The rejection reason
    /// names the offending token, and a valid replacement is accepted within budget.
    /// </summary>
    [Fact]
    public async Task PlanIterationAsync_NumericPhaseToken_RejectedAsUnrecognizedThenValidAccepted()
    {
        var dir = NewTempDir();
        try
        {
            SequencedPlanStubClient? stub = null;
            var brain = NewShadowBrain(dir,
                factoryChatClientFactory: _ => stub = new SequencedPlanStubClient(
                    ["coding", "1", "testing", "review", "merging"],  // numeric token — not a phase name
                    ["coding", "testing", "review", "merging"]));     // valid replacement
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("goal-numeric-1", TestContext.Current.CancellationToken);

                var result = await brain.PlanIterationAsync(
                    CreatePipeline("goal-numeric-1"), null, TestContext.Current.CancellationToken);

                // No early throw: the numeric token was rejected in-loop, valid plan accepted.
                Assert.False(result.IsFailed);
                Assert.False(result.IsEscalation);
                Assert.NotNull(result.Plan);
                Assert.Equal(
                    [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
                    result.Plan!.Phases);
                Assert.Empty(result.Plan.UnrecognizedPhases);

                Assert.NotNull(stub);
                Assert.Equal(2, stub!.ToolCallCount);

                // The rejection reason fed back to the Brain names the numeric token.
                var nudge = stub.ObservedUserPrompts.FirstOrDefault(p => p.Contains("rejected because"));
                Assert.NotNull(nudge);
                Assert.Contains("Unrecognized phase names: 1", nudge!);
            }
        }
        finally { DeleteDir(dir); }
    }

    /// <summary>
    /// Lifecycle phase names (Planning/Done/Failed) are rejected as unrecognized inside the
    /// bounded loop — they are not executable phases. The rejection reason names the offending
    /// token, and a valid replacement is accepted within budget.
    /// </summary>
    [Theory]
    [InlineData("Planning")]
    [InlineData("Done")]
    [InlineData("Failed")]
    public async Task PlanIterationAsync_LifecyclePhaseName_RejectedAsUnrecognizedThenValidAccepted(string lifecyclePhase)
    {
        var dir = NewTempDir();
        try
        {
            SequencedPlanStubClient? stub = null;
            var brain = NewShadowBrain(dir,
                factoryChatClientFactory: _ => stub = new SequencedPlanStubClient(
                    ["coding", lifecyclePhase, "testing", "review", "merging"],  // lifecycle token
                    ["coding", "testing", "review", "merging"]));                  // valid replacement
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync($"goal-lifecycle-{lifecyclePhase}", TestContext.Current.CancellationToken);

                var result = await brain.PlanIterationAsync(
                    CreatePipeline($"goal-lifecycle-{lifecyclePhase}"), null, TestContext.Current.CancellationToken);

                // No early throw: the lifecycle token was rejected in-loop, valid plan accepted.
                Assert.False(result.IsFailed);
                Assert.False(result.IsEscalation);
                Assert.NotNull(result.Plan);
                Assert.Equal(
                    [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
                    result.Plan!.Phases);
                Assert.Empty(result.Plan.UnrecognizedPhases);

                Assert.NotNull(stub);
                Assert.Equal(2, stub!.ToolCallCount);

                // The rejection reason fed back to the Brain names the lifecycle token.
                var nudge = stub.ObservedUserPrompts.FirstOrDefault(p => p.Contains("rejected because"));
                Assert.NotNull(nudge);
                Assert.Contains(lifecyclePhase, nudge!);
            }
        }
        finally { DeleteDir(dir); }
    }
}
