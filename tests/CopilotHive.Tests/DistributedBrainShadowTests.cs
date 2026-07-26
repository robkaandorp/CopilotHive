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
    // Criterion 12: ExecuteBrainAsync split — FireShadowLlm fires on non-throwing
    // completion including Status="Error"
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteBrainAsync_SuccessfulCompletion_FiresShadowLlm()
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

                // Give the fire-and-forget shadow a moment to process.
                await Task.Delay(500, TestContext.Current.CancellationToken);

                // Verify the shadow actor received the prompt — check child's session history.
                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                var children = GetChildActors(actor!);
                Assert.True(children.ContainsKey("goal-1"), "Shadow actor should have a child for goal-1.");
                var child = children["goal-1"];

                // The child's session should contain the planning prompt (it was relayed via ExecutePromptOnChild).
                // The ExecutePromptOnChildMessage sends the prompt to the child's CodingAgent, which adds it to the session.
                Assert.True(child.Session.MessageHistory.Count > 0,
                    "Child session should have messages after shadow LLM execution.");
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ExecuteBrainAsync_StatusError_StillFiresShadowLlm()
    {
        var dir = NewTempDir();
        try
        {
            // A throwing client causes CodingAgent.ExecuteAsync to return Status="Error" (not throw).
            // ExecuteBrainAsync completes without throwing, so FireShadowLlm should still fire.
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

                // Give the fire-and-forget shadow a moment to process.
                await Task.Delay(500, TestContext.Current.CancellationToken);

                // Verify the shadow actor received the prompt despite Status="Error".
                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                var children = GetChildActors(actor!);
                Assert.True(children.ContainsKey("goal-err"), "Shadow actor should have a child for goal-err.");
                var child = children["goal-err"];

                // The child's session should have messages — the prompt was relayed.
                Assert.True(child.Session.MessageHistory.Count > 0,
                    "Child session should have messages after shadow LLM execution (Status=Error still fires).");
            }
        }
        finally { DeleteDir(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // ExecuteBrainAsync does NOT fire on exception or cancellation
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteBrainAsync_PreCanceledToken_DoesNotFireShadowLlm()
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

                // Record child's message count before the call.
                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                var children = GetChildActors(actor!);
                var child = children["goal-cancel"];
                var beforeCount = child.Session.MessageHistory.Count;

                // Use a pre-canceled token — ExecuteBrainAsync throws OperationCanceledException
                // before reaching FireShadowLlm.
                using var cts = new CancellationTokenSource();
                cts.Cancel();

                // Call AskQuestionAsync (catches exceptions and returns fallback).
                // But the retry policy re-throws OperationCanceledException immediately when ct is canceled.
                // TaskCanceledException is a subclass of OperationCanceledException.
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    brain.AskQuestionAsync("goal-cancel", 1, "coding", "coder", "q", cts.Token));

                // Give any potential shadow fire a moment (there shouldn't be one).
                await Task.Delay(200, TestContext.Current.CancellationToken);

                // The child's session should NOT have been modified.
                Assert.Equal(beforeCount, child.Session.MessageHistory.Count);
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ExecuteBrainAsync_NoGoalContext_ThrowsAndDoesNotFireShadowLlm()
    {
        var dir = NewTempDir();
        try
        {
            var stub = new PlanStubClient("call-noctx", ["coding"], "noctx test");
            var brain = NewShadowBrain(dir, chatClient: stub);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                // Do NOT fork a session for this goal — ExecuteBrainAsync will throw
                // "No Brain context for goal" immediately.
                // Use reflection to call ExecuteBrainAsync directly (bypassing the retry policy).
                var method = typeof(DistributedBrain).GetMethod("ExecuteBrainAsync", NonPublicInstance)!;

                // Invoke via dynamic to avoid needing the internal BrainToolCallResult type.
                Task task = (Task)method.Invoke(brain,
                    ["prompt", "nonexistent-goal", CancellationToken.None, "test", "TestMethod"])!;
                await Assert.ThrowsAsync<InvalidOperationException>(() => task);

                // No child should exist for the nonexistent goal.
                var actor = (BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                var children = GetChildActors(actor!);
                Assert.False(children.ContainsKey("nonexistent-goal"),
                    "Shadow should not have a child for a goal that was never forked.");
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
    // Additional: Verify FireShadowLlm is truly fire-and-forget (non-blocking)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FireShadowLlm_IsFireAndForget_DoesNotBlockCaller()
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
                // PlanIterationAsync should complete quickly — FireShadowLlm is fire-and-forget.
                // The authoritative call + shadow relay should not block.
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
                    $"PlanIterationAsync took {sw.Elapsed.TotalSeconds:F1}s — FireShadowLlm may be blocking.");
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
}