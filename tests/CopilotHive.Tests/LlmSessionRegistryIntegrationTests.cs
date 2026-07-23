using System.Runtime.CompilerServices;

using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests verifying that <see cref="DistributedBrain"/>, <see cref="Composer"/>,
/// and <see cref="GoalReviewService"/> register and update their LLM sessions in the
/// shared <see cref="LlmSessionRegistry"/>.
/// </summary>
public sealed class LlmSessionRegistryIntegrationTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"llm-registry-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static LlmSessionInfo? FindSession(LlmSessionRegistry registry, string sessionId) =>
        registry.GetAll().FirstOrDefault(s => s.SessionId == sessionId);

    private static GoalPipeline CreatePipeline(string goalId, string description) =>
        new(new Goal { Id = goalId, Description = description });

    // ── DistributedBrain: master registration ────────────────────────────────

    [Fact]
    public async Task ConnectAsync_RegistersBrainMaster()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);

            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var master = FindSession(registry, "brain-master");
            Assert.NotNull(master);
            Assert.Equal("Brain", master!.SessionType);
            Assert.Equal("idle", master.Status);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── DistributedBrain: fork registers, delete unregisters ─────────────────

    [Fact]
    public async Task ForkSessionForGoalAsync_RegistersGoalSession()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            await brain.ForkSessionForGoalAsync("goal-fork-1", TestContext.Current.CancellationToken);

            var goalSession = FindSession(registry, "brain-goal-goal-fork-1");
            Assert.NotNull(goalSession);
            Assert.Equal("BrainGoal", goalSession!.SessionType);
            Assert.Equal("goal-fork-1", goalSession.GoalId);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task DeleteGoalSession_UnregistersGoalSession_RoundTrip()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            await brain.ForkSessionForGoalAsync("goal-rt-1", TestContext.Current.CancellationToken);
            Assert.NotNull(FindSession(registry, "brain-goal-goal-rt-1"));

            brain.DeleteGoalSession("goal-rt-1");

            Assert.Null(FindSession(registry, "brain-goal-goal-rt-1"));
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── DistributedBrain: SummarizeAndMergeAsync must not deadlock ────────────

    [Fact]
    public async Task SummarizeAndMergeAsync_CompletesWithoutDeadlock_UnregistersGoalSession()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var pipeline = CreatePipeline("goal-merge-1", "Merge test goal");
            await brain.ForkSessionForGoalAsync("goal-merge-1", TestContext.Current.CancellationToken);
            Assert.NotNull(FindSession(registry, "brain-goal-goal-merge-1"));

            // If SummarizeAndMergeAsync called the gated public DeleteGoalSession while holding
            // _brainCallGate, this would deadlock. A 30s timeout guards against a hang.
            var summary = await brain.SummarizeAndMergeAsync(pipeline, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.NotNull(summary);
            Assert.Null(FindSession(registry, "brain-goal-goal-merge-1"));
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── DistributedBrain: RegisterExistingGoalSession ────────────────────────

    [Fact]
    public async Task RegisterExistingGoalSession_WithExistingSessionFile_Registers()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            // Fork to create the on-disk session file, then unregister the registry entry
            // to simulate a restart where only the file remains.
            await brain.ForkSessionForGoalAsync("goal-existing-1", TestContext.Current.CancellationToken);
            registry.Unregister("brain-goal-goal-existing-1");
            Assert.Null(FindSession(registry, "brain-goal-goal-existing-1"));

            brain.RegisterExistingGoalSession("goal-existing-1");

            var goalSession = FindSession(registry, "brain-goal-goal-existing-1");
            Assert.NotNull(goalSession);
            Assert.Equal("BrainGoal", goalSession!.SessionType);
            Assert.Equal("goal-existing-1", goalSession.GoalId);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RegisterExistingGoalSession_WithNoSessionFile_RegistersWithZeroTokens()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            brain.RegisterExistingGoalSession("goal-nofile-1");

            var goalSession = FindSession(registry, "brain-goal-goal-nofile-1");
            Assert.NotNull(goalSession);
            Assert.Equal(0, goalSession!.CurrentTokens);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── DistributedBrain: status set inside ExecuteBrainAsync, idle in finally ─

    [Fact]
    public async Task PlanIterationAsync_SetsPlanningStatusDuringCall_RestoresIdle()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var capturing = new StatusCapturingChatClient(registry, "brain-goal-goal-status-1");
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: capturing, sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-status-1", TestContext.Current.CancellationToken);

            var pipeline = CreatePipeline("goal-status-1", "Planning status goal");
            await brain.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);

            // The status captured DURING the LLM call must be "planning".
            Assert.Equal("planning", capturing.CapturedStatusDuringCall);

            // After completion, the session should be restored to "idle".
            var goalSession = FindSession(registry, "brain-goal-goal-status-1");
            Assert.NotNull(goalSession);
            Assert.Equal("idle", goalSession!.Status);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task CraftPromptAsync_SetsCraftingPromptStatusDuringCall()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var capturing = new StatusCapturingChatClient(registry, "brain-goal-goal-craft-1", reply: "Work on the coding task.");
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: capturing, sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-craft-1", TestContext.Current.CancellationToken);

            var pipeline = CreatePipeline("goal-craft-1", "Crafting prompt goal");
            await brain.CraftPromptAsync(pipeline, GoalPhase.Coding, null, TestContext.Current.CancellationToken);

            Assert.Equal("crafting-prompt", capturing.CapturedStatusDuringCall);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task GenerateCommitMessageAsync_SetsGeneratingCommitMessageStatusDuringCall()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var capturing = new StatusCapturingChatClient(registry, "brain-goal-goal-commit-1", reply: "feat: add feature");
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: capturing, sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-commit-1", TestContext.Current.CancellationToken);

            var pipeline = CreatePipeline("goal-commit-1", "Commit message goal");
            await brain.GenerateCommitMessageAsync(pipeline, TestContext.Current.CancellationToken);

            Assert.Equal("generating-commit-message", capturing.CapturedStatusDuringCall);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task AskQuestionAsync_SetsAnsweringQuestionStatusDuringCall()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var capturing = new StatusCapturingChatClient(registry, "brain-goal-goal-ask-1", reply: "Yes.");
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: capturing, sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-ask-1", TestContext.Current.CancellationToken);

            await brain.AskQuestionAsync("goal-ask-1", 1, "Coding", "coder", "Should I proceed?",
                TestContext.Current.CancellationToken);

            Assert.Equal("answering-question", capturing.CapturedStatusDuringCall);

            var goalSession = FindSession(registry, "brain-goal-goal-ask-1");
            Assert.NotNull(goalSession);
            Assert.Equal("idle", goalSession!.Status);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── DistributedBrain: InjectSystemNoteAsync / SummarizeAndMergeAsync refresh master ─

    [Fact]
    public async Task InjectSystemNoteAsync_RefreshesBrainMaster()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var pipeline = CreatePipeline("goal-note-1", "Note refresh goal");
            await brain.InjectSystemNoteAsync(pipeline, "Plan adjusted for safety.", TestContext.Current.CancellationToken);

            var master = FindSession(registry, "brain-master");
            Assert.NotNull(master);
            // The note added two messages, so estimated tokens should now be non-zero.
            Assert.True(master!.CurrentTokens > 0);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── DistributedBrain: ResetSessionAsync zeroes master ────────────────────

    [Fact]
    public async Task ResetSessionAsync_RefreshesBrainMasterWithZeroTokens()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var pipeline = CreatePipeline("goal-reset-1", "Reset goal");
            await brain.InjectSystemNoteAsync(pipeline, "Some note that adds tokens.", TestContext.Current.CancellationToken);

            await brain.ResetSessionAsync(TestContext.Current.CancellationToken);

            var master = FindSession(registry, "brain-master");
            Assert.NotNull(master);
            Assert.Equal(0, master!.CurrentTokens);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── DistributedBrain: UpdateModelAsync refreshes master and goal entries ──

    [Fact]
    public async Task UpdateModelAsync_RefreshesMasterAndRegisteredGoalEntries()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry,
                chatClientFactory: _ => new FakeChatClient());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-model-1", TestContext.Current.CancellationToken);

            await brain.UpdateModelAsync("copilot/new-model", 99999, TestContext.Current.CancellationToken);

            var master = FindSession(registry, "brain-master");
            Assert.NotNull(master);
            Assert.Equal("copilot/new-model", master!.Model);
            Assert.Equal(99999, master.MaxTokens);

            var goalSession = FindSession(registry, "brain-goal-goal-model-1");
            Assert.NotNull(goalSession);
            Assert.Equal("copilot/new-model", goalSession!.Model);
            Assert.Equal(99999, goalSession.MaxTokens);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── DistributedBrain: null registry is accepted ──────────────────────────

    [Fact]
    public async Task DistributedBrain_NullRegistry_ForkAndDeleteWork()
    {
        var tempDir = CreateTempDir();
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: null);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            await brain.ForkSessionForGoalAsync("goal-null-1", TestContext.Current.CancellationToken);
            brain.DeleteGoalSession("goal-null-1");
            brain.RegisterExistingGoalSession("goal-null-1");
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── Composer ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Composer_ConnectAsync_RegistersComposer()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var composer = new Composer("copilot/test-model", NullLogger<Composer>.Instance,
                new FakeGoalStoreForComposer(),
                stateDir: tempDir,
                chatClientFactory: _ => new FakeChatClient(),
                sessionRegistry: registry);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            var session = FindSession(registry, "composer");
            Assert.NotNull(session);
            Assert.Equal("Composer", session!.SessionType);
            Assert.Equal("idle", session.Status);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task Composer_ResetSessionAsync_RefreshesComposerWithZeroTokens()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var composer = new Composer("copilot/test-model", NullLogger<Composer>.Instance,
                new FakeGoalStoreForComposer(),
                stateDir: tempDir,
                chatClientFactory: _ => new FakeChatClient(),
                sessionRegistry: registry);
            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            await composer.ResetSessionAsync(TestContext.Current.CancellationToken);

            var session = FindSession(registry, "composer");
            Assert.NotNull(session);
            Assert.Equal(0, session!.CurrentTokens);
            Assert.Equal("idle", session.Status);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task Composer_NullRegistry_ConnectAndResetWork()
    {
        var tempDir = CreateTempDir();
        try
        {
            var composer = new Composer("copilot/test-model", NullLogger<Composer>.Instance,
                new FakeGoalStoreForComposer(),
                stateDir: tempDir,
                chatClientFactory: _ => new FakeChatClient(),
                sessionRegistry: null);
            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            await composer.ResetSessionAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── GoalReviewService ────────────────────────────────────────────────────

    [Fact]
    public async Task GoalReviewService_RegistersAndUnregistersReviewSession_OnSuccess()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var seenReviewSessions = new List<LlmSessionInfo>();
            var goal = new Goal { Id = "goal-review-ok", Description = "Review OK goal", ReviewStatus = ReviewStatus.None };

            var service = new GoalReviewService(
                knowledgeGraph: null, configRepo: null, config: null, goalStore: null,
                brainRepoManager: null, stateDir: tempDir,
                logger: NullLogger<GoalReviewService>.Instance,
                chatClientFactory: _ => new ReviewCapturingChatClient(
                    """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"ok"}""",
                    registry, seenReviewSessions),
                sessionRegistry: registry);

            await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

            // The review session must have been registered during the call with a complete payload.
            var captured = Assert.Single(seenReviewSessions);
            Assert.Equal("GoalReview", captured.SessionType);
            Assert.Equal("goal-review-ok", captured.GoalId);
            Assert.Equal("reviewing", captured.Status);
            Assert.StartsWith("goal-review-goal-review-ok-", captured.SessionId);

            // After completion, the review session must be unregistered.
            Assert.DoesNotContain(registry.GetAll(), s => s.SessionType == "GoalReview");
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task GoalReviewService_UnregistersReviewSession_OnFailure()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var goal = new Goal { Id = "goal-review-fail", Description = "Review fail goal", ReviewStatus = ReviewStatus.None };

            var service = new GoalReviewService(
                knowledgeGraph: null, configRepo: null, config: null, goalStore: null,
                brainRepoManager: null, stateDir: tempDir,
                logger: NullLogger<GoalReviewService>.Instance,
                chatClientFactory: _ => new ThrowingReviewChatClient(),
                sessionRegistry: registry);

            // The review agent throws internally, but ReviewGoalAsync returns a NeedsChanges result.
            var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);
            Assert.Equal("NeedsChanges", result.Verdict);

            // Even on failure, the review session must be unregistered.
            Assert.DoesNotContain(registry.GetAll(), s => s.SessionType == "GoalReview");
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task GoalReviewService_RejectedConcurrentReview_ThrowsAndDoesNotRegister()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var goal = new Goal { Id = "goal-review-concurrent", Description = "Concurrent review goal", ReviewStatus = ReviewStatus.None };

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var service = new GoalReviewService(
                knowledgeGraph: null, configRepo: null, config: null, goalStore: null,
                brainRepoManager: null, stateDir: tempDir,
                logger: NullLogger<GoalReviewService>.Instance,
                chatClientFactory: _ => new BlockingReviewChatClient(tcs.Task, entered),
                sessionRegistry: registry);

            var first = service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);
            await entered.Task;

            // Second concurrent review for the same goal ID (fresh instance) must be rejected.
            var secondGoal = new Goal { Id = "goal-review-concurrent", Description = "Concurrent review goal", ReviewStatus = ReviewStatus.None };
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ReviewGoalAsync(secondGoal, TestContext.Current.CancellationToken));

            // Only ONE review session should have been registered (from the first, in-flight review).
            Assert.Single(registry.GetAll(), s => s.SessionType == "GoalReview");

            // Release the first review so it completes cleanly and unregisters.
            tcs.SetResult("""{"verdict":"Approved","issues":[],"verified":[],"recommendation":"ok"}""");
            await first;

            Assert.DoesNotContain(registry.GetAll(), s => s.SessionType == "GoalReview");
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task GoalReviewService_NullRegistry_ReviewCompletes()
    {
        var tempDir = CreateTempDir();
        try
        {
            var goal = new Goal { Id = "goal-review-null", Description = "Null registry review goal", ReviewStatus = ReviewStatus.None };

            var service = new GoalReviewService(
                knowledgeGraph: null, configRepo: null, config: null, goalStore: null,
                brainRepoManager: null, stateDir: tempDir,
                logger: NullLogger<GoalReviewService>.Instance,
                chatClientFactory: _ => new ReviewCapturingChatClient(
                    """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"ok"}""",
                    registry: null, seen: null),
                sessionRegistry: null);

            var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);
            Assert.Equal("Approved", result.Verdict);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────

/// <summary>
/// A chat client that captures the goal session status recorded in the registry at the
/// moment the LLM call executes, so tests can verify the status is set BEFORE the call.
/// </summary>
file sealed class StatusCapturingChatClient(LlmSessionRegistry registry, string sessionId, string reply = "") : IChatClient
{
    public string? CapturedStatusDuringCall { get; private set; }

    public ChatClientMetadata Metadata => new("capture", null, "capture-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var session = registry.GetAll().FirstOrDefault(s => s.SessionId == sessionId);
        CapturedStatusDuringCall = session?.Status;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
        {
            FinishReason = ChatFinishReason.Stop,
        });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming not used in this fake client.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// A review chat client that captures every <c>GoalReview</c> session present in the registry
/// at the moment of the review call, then returns a canned JSON review verdict.
/// </summary>
file sealed class ReviewCapturingChatClient(string replyText, LlmSessionRegistry? registry, List<LlmSessionInfo>? seen) : IChatClient
{
    public ChatClientMetadata Metadata => new("review", null, "review-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (registry is not null && seen is not null)
            seen.AddRange(registry.GetAll().Where(s => s.SessionType == "GoalReview"));

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replyText))
        {
            FinishReason = ChatFinishReason.Stop,
        });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming not used in this fake client.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>A review chat client that throws to simulate a failed review.</summary>
file sealed class ThrowingReviewChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("throw", null, "throw-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Simulated review agent failure.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming not used in this fake client.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>A review chat client that blocks until released, signalling when it has been entered.</summary>
file sealed class BlockingReviewChatClient(Task<string> release, TaskCompletionSource<bool> entered) : IChatClient
{
    public ChatClientMetadata Metadata => new("block", null, "block-model");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        entered.TrySetResult(true);
        var reply = await release.WaitAsync(cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
        {
            FinishReason = ChatFinishReason.Stop,
        };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming not used in this fake client.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>Minimal <see cref="IGoalStore"/> for constructing a Composer in tests.</summary>
file sealed class FakeGoalStoreForComposer : IGoalStore
{
    private readonly Dictionary<string, Goal> _goals = new();

    public string Name => "FakeGoalStoreForComposer";

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList().AsReadOnly());

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.TryGetValue(goalId, out var goal) ? goal : null);

    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.FromResult(goal);
    }

    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.Remove(goalId));

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>(Array.Empty<IterationSummary>());

    public Task<int> ImportGoalsAsync(IEnumerable<Goal> goals, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>(Array.Empty<Release>());

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>(Array.Empty<ConversationEntry>());

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>(Array.Empty<(string, PersistedClarification)>());
}
