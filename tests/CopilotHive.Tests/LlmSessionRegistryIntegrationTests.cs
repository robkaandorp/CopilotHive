using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;

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

    /// <summary>Config with a configured reviewer model (Slice 3b: unconfigured ⇒ refused).</summary>
    private static HiveConfigFile ReviewerConfig() => new()
    {
        Workers =
        {
            ["reviewer"] = new WorkerConfig { Model = "reviewer-model" },
        },
    };

    private static LlmSessionInfo? FindSession(LlmSessionRegistry registry, string sessionId) =>
        registry.GetAll().FirstOrDefault(s => s.SessionId == sessionId);

    private static GoalPipeline CreatePipeline(string goalId, string description) =>
        new(new Goal { Id = goalId, Description = description });

    /// <summary>Injects a fake chat client into a Composer and rebuilds its internal agent.</summary>
    private static async Task InjectComposerChatClient(Composer composer, IChatClient fakeClient)
    {
        var agentService = GetComposerAgentService(composer);
        var serviceType = agentService.GetType();

        var chatClientField = serviceType.GetField("_chatClient",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_chatClient field not found on ComposerAgentService");
        chatClientField.SetValue(agentService, fakeClient);

        var recreateAgent = serviceType.GetMethod("RecreateAgentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("RecreateAgentAsync method not found on ComposerAgentService");
        await (Task)recreateAgent.Invoke(agentService, null)!;
    }

    /// <summary>Gets the private <c>_agentService</c> instance from a Composer.</summary>
    private static object GetComposerAgentService(Composer composer)
    {
        var field = typeof(Composer).GetField("_agentService",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_agentService field not found on Composer");
        return field.GetValue(composer)
            ?? throw new InvalidOperationException("_agentService was null");
    }

    /// <summary>Gets the private <c>_session</c> field from a Composer.</summary>
    private static AgentSession GetComposerSession(Composer composer)
    {
        var agentService = GetComposerAgentService(composer);
        var sessionField = agentService.GetType().GetField("_session",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_session field not found on ComposerAgentService");
        return (AgentSession)sessionField.GetValue(agentService)!;
    }

    /// <summary>Populates a session with a system message plus <paramref name="count"/> user/assistant messages.</summary>
    private static void PopulateSession(AgentSession session, int count)
    {
        session.MessageHistory.Clear();
        session.MessageHistory.Add(new ChatMessage(ChatRole.System, "You are a helpful assistant."));
        for (var i = 0; i < count; i++)
        {
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Message {i}"));
        }
    }

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
            Assert.Equal(LlmSessionType.Brain, master!.SessionType);
            Assert.Equal("idle", master.Status);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ConnectAsync_RegistersBrainMaster_WithCompletePayload()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                maxContextTokens: 123_456,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);

            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var master = FindSession(registry, "brain-master");
            Assert.NotNull(master);
            Assert.Equal("brain-master", master!.SessionId);
            Assert.Equal(LlmSessionType.Brain, master.SessionType);
            Assert.Equal("copilot/test-model", master.Model);
            Assert.Equal("idle", master.Status);
            Assert.Equal(123_456, master.MaxTokens);
            // A freshly-connected master session has no accumulated conversation tokens.
            Assert.Equal(0, master.CurrentTokens);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task SummarizeAndMergeAsync_RefreshesBrainMaster()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir,
                chatClient: new CannedReplyChatClient("Summary of the completed goal."),
                sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var pipeline = CreatePipeline("goal-refresh-master-1", "Refresh master goal");
            await brain.ForkSessionForGoalAsync("goal-refresh-master-1", TestContext.Current.CancellationToken);

            var summary = await brain.SummarizeAndMergeAsync(pipeline, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.NotNull(summary);

            // SummarizeAndMergeAsync appends the summary to the master session and refreshes its
            // registry entry, so the master session must now report non-zero context tokens.
            var master = FindSession(registry, "brain-master");
            Assert.NotNull(master);
            Assert.True(master!.CurrentTokens > 0,
                $"Master session tokens should be refreshed after merge (was {master.CurrentTokens})");
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── DistributedBrain: lifecycle ops use _sessionLock, not per-context gates ──

    [Fact]
    public async Task ForkSessionForGoalAsync_DoesNotBlockOnActiveGoalGate()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            // A chat client that blocks the first LLM call so goal A's per-context gate is held while we fork.
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocking = new BlockingBrainChatClient(release.Task, entered, "planning done");
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: blocking, sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-nogate-a", TestContext.Current.CancellationToken);

            // Hold goal A's per-context gate via a blocked LLM call.
            var pipeline = CreatePipeline("goal-nogate-a", "No-gate fork goal A");
            var planTask = brain.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);

            // Deterministic: the blocking client signals `entered` from inside the LLM call, which
            // only happens while goal A's per-context gate is held. Awaiting it proves the gate is held.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Fork a DIFFERENT goal. Because ForkSessionForGoalAsync uses only _sessionLock (NOT any
            // per-context gate), it must complete even while goal A's gate is held by the blocked LLM
            // call. A tight 2s bound (not 5s) ensures a brief block would still surface.
            await brain.ForkSessionForGoalAsync("goal-nogate-b", TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            // Strict proof: the fork completed WHILE goal A's gate is still held. The blocked LLM
            // call has not been released, so `release` must be uncompleted. If the fork had waited on
            // goal A's gate, it could only complete AFTER release — this assertion would then fail.
            Assert.False(release.Task.IsCompleted,
                "Fork must complete while goal A's per-context gate is still held by the blocked LLM call");

            // The fork itself does not register (the child publishes its entry around LLM calls);
            // completing under the 2s bound is the proof that it never waited on goal A.

            // Release the gate and let the plan call finish.
            release.SetResult(true);
            await planTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RegisterExistingGoalSession_DoesNotBlockOnActiveGoalGate()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocking = new BlockingBrainChatClient(release.Task, entered, "planning done");
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: blocking, sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            // Fork goal A (which will hold its per-context gate during a blocked LLM call).
            await brain.ForkSessionForGoalAsync("goal-nogate-a", TestContext.Current.CancellationToken);

            // Write a standalone session file directly to disk for a goal that has NO context yet,
            // simulating a restart where only the file remains.
            var existingSession = AgentSession.Create("brain-goal-goal-reg-nogate");
            existingSession.MessageHistory.Add(new ChatMessage(ChatRole.User, "restored marker"));
            await existingSession.SaveAsync(
                Path.Combine(tempDir, "actors", "brain-goal-goal-reg-nogate.json"), TestContext.Current.CancellationToken);
            Assert.Null(FindSession(registry, "brain-goal-goal-reg-nogate"));

            // Hold goal A's per-context gate via a blocked LLM call.
            var pipeline = CreatePipeline("goal-nogate-a", "Register no-gate goal");
            var planTask = brain.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // RegisterExistingGoalSessionAsync uses _sessionLock (NOT any per-context gate), so it must
            // complete even while goal A's gate is held. A tight 2s bound ensures a brief block surfaces.
            await brain.RegisterExistingGoalSessionAsync("goal-reg-nogate", TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            // Strict proof: registration completed WHILE goal A's gate is still held.
            Assert.False(release.Task.IsCompleted,
                "RegisterExistingGoalSessionAsync must complete while goal A's per-context gate is still held");

            release.SetResult(true);
            await planTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ForkSessionForGoalAsync_SerializedByActorMailbox()
    {
        var tempDir = CreateTempDir();
        var gate = new MailboxGate();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClientFactory: gate.CreateClient, sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            // The chat-client factory runs on the BrainActor mailbox thread while handling a fork,
            // so blocking inside it blocks the mailbox itself. This proves fork ordering is enforced
            // by the actor mailbox rather than by any ambient lock.
            gate.BlockNextCall();
            var blockingFork = brain.ForkSessionForGoalAsync("goal-blocker", TestContext.Current.CancellationToken);
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // A second fork must queue behind the blocked mailbox.
            var forkTask = brain.ForkSessionForGoalAsync("goal-serialized-1", TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.False(forkTask.IsCompleted, "Fork must remain queued while the actor mailbox is blocked");
            Assert.False(brain.GoalSessionExists("goal-serialized-1"));

            // Release the mailbox; both forks complete in order.
            gate.Release();
            await blockingFork.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await forkTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.True(brain.GoalSessionExists("goal-serialized-1"));
        }
        finally
        {
            gate.Release();
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task DeleteGoalSession_BlocksOnActorMailbox()
    {
        var tempDir = CreateTempDir();
        var gate = new MailboxGate();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClientFactory: gate.CreateClient, sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-order-b", TestContext.Current.CancellationToken);
            Assert.True(brain.GoalSessionExists("goal-order-b"));

            // Block the mailbox from inside a fork, then enqueue the delete behind it.
            gate.BlockNextCall();
            var blockingFork = brain.ForkSessionForGoalAsync("goal-blocker", TestContext.Current.CancellationToken);
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var deleteTask = brain.DeleteGoalSessionAsync("goal-order-b", TestContext.Current.CancellationToken);

            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.False(deleteTask.IsCompleted, "Delete must stay queued while the actor mailbox is blocked");

            gate.Release();
            await blockingFork.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await deleteTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(brain.GoalSessionExists("goal-order-b"));
        }
        finally
        {
            gate.Release();
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// Blocks the BrainActor mailbox by stalling inside the chat-client factory, which the actor
    /// invokes on its own mailbox thread while handling a fork.
    /// </summary>
    private sealed class MailboxGate
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockNext;

        internal Task Entered => _entered.Task;

        internal void BlockNextCall() => Interlocked.Exchange(ref _blockNext, 1);

        internal void Release() => _release.Set();

        internal IChatClient CreateClient(string model)
        {
            if (Interlocked.Exchange(ref _blockNext, 0) == 1)
            {
                _entered.TrySetResult(true);
                _release.Wait(TimeSpan.FromSeconds(30));
            }

            return new FakeChatClient();
        }
    }

    // ── DistributedBrain: fork registers, delete unregisters ─────────────────

    [Fact]
    public async Task GoalSession_IsRegistered_AfterFirstLlmCall()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            await brain.ForkSessionForGoalAsync("goal-fork-1", TestContext.Current.CancellationToken);

            // The goal's child actor owns its registry entry and publishes it around each LLM call,
            // so a bare fork registers nothing until work actually runs on that session.
            Assert.Null(FindSession(registry, "brain-goal-goal-fork-1"));

            await brain.PlanIterationAsync(
                CreatePipeline("goal-fork-1", "Fork registration goal"), null, TestContext.Current.CancellationToken);

            var goalSession = FindSession(registry, "brain-goal-goal-fork-1");
            Assert.NotNull(goalSession);
            Assert.Equal(LlmSessionType.BrainGoal, goalSession!.SessionType);
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
            await brain.PlanIterationAsync(
                CreatePipeline("goal-rt-1", "Round trip goal"), null, TestContext.Current.CancellationToken);
            Assert.NotNull(FindSession(registry, "brain-goal-goal-rt-1"));

            await brain.DeleteGoalSessionAsync("goal-rt-1", TestContext.Current.CancellationToken);

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
            await brain.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);
            Assert.NotNull(FindSession(registry, "brain-goal-goal-merge-1"));

            // SummarizeAndMergeAsync acquires the goal's per-context lease/gate and then deletes the
            // goal session. If it invoked the public DeleteGoalSessionAsync while holding _sessionLock
            // (or its own gate), this would deadlock. A 30s timeout guards against a hang.
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

            // Write an on-disk session file directly (no context) to simulate a restart where only
            // the file remains and no per-goal Brain context exists yet.
            var existing = AgentSession.Create("brain-goal-goal-existing-1");
            existing.MessageHistory.Add(new ChatMessage(ChatRole.User, "restored marker"));
            await existing.SaveAsync(
                Path.Combine(tempDir, "actors", "brain-goal-goal-existing-1.json"), TestContext.Current.CancellationToken);
            Assert.Null(FindSession(registry, "brain-goal-goal-existing-1"));

            await brain.RegisterExistingGoalSessionAsync("goal-existing-1", TestContext.Current.CancellationToken);
            await brain.PlanIterationAsync(
                CreatePipeline("goal-existing-1", "Existing session goal"), null, TestContext.Current.CancellationToken);

            var goalSession = FindSession(registry, "brain-goal-goal-existing-1");
            Assert.NotNull(goalSession);
            Assert.Equal(LlmSessionType.BrainGoal, goalSession!.SessionType);
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

            await brain.RegisterExistingGoalSessionAsync("goal-nofile-1", TestContext.Current.CancellationToken);

            // With no session file the actor forks a fresh session from the master; the entry
            // appears once the child runs its first call.
            Assert.Null(FindSession(registry, "brain-goal-goal-nofile-1"));
            await brain.PlanIterationAsync(
                CreatePipeline("goal-nofile-1", "No file goal"), null, TestContext.Current.CancellationToken);

            var goalSession = FindSession(registry, "brain-goal-goal-nofile-1");
            Assert.NotNull(goalSession);
            Assert.Equal("idle", goalSession!.Status);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── Goal actor publishes "active" during the LLM call and "idle" in its finally ─

    [Fact]
    public async Task PlanIterationAsync_SetsActiveStatusDuringCall_RestoresIdle()
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
            Assert.Equal("active", capturing.CapturedStatusDuringCall);

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
    public async Task CraftPromptAsync_SetsActiveStatusDuringCall()
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

            Assert.Equal("active", capturing.CapturedStatusDuringCall);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task GenerateCommitMessageAsync_SetsActiveStatusDuringCall()
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

            Assert.Equal("active", capturing.CapturedStatusDuringCall);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task AskQuestionAsync_SetsActiveStatusDuringCall()
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

            Assert.Equal("active", capturing.CapturedStatusDuringCall);

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
    public async Task InjectSystemNoteAsync_InjectsIntoGoalSession_NotMaster()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var pipeline = CreatePipeline("goal-note-1", "Note refresh goal");
            await brain.ForkSessionForGoalAsync("goal-note-1", TestContext.Current.CancellationToken);
            await brain.InjectSystemNoteAsync(pipeline, "Plan adjusted for safety.", TestContext.Current.CancellationToken);

            // A plan-adjustment note belongs to the goal, so it is injected into the goal's child
            // actor session. The shared master session must stay untouched by per-goal notes.
            var master = FindSession(registry, "brain-master");
            Assert.NotNull(master);
            Assert.Equal(0, master!.CurrentTokens);

            // The note is delivered to the goal's child actor; the call completing without
            // throwing proves the child accepted it (a missing child would throw KeyNotFound).
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
            await brain.ForkSessionForGoalAsync("goal-reset-1", TestContext.Current.CancellationToken);
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

    // ── DistributedBrain: UpdateModelAsync preserves existing goal context snapshots ──

    [Fact]
    public async Task UpdateModelAsync_PreservesGoalContextSnapshot()
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
            await brain.PlanIterationAsync(
                CreatePipeline("goal-model-1", "Model snapshot goal"), null, TestContext.Current.CancellationToken);

            // Capture the EXACT original snapshot values BEFORE the model change so we can assert the
            // goal context retains them verbatim (a NotEqual assertion would pass even if the value
            // changed to some unrelated third value — Assert.Equal(original, ...) is the strong check).
            const string originalModel = "copilot/test-model";
            var originalGoal = FindSession(registry, "brain-goal-goal-model-1");
            Assert.NotNull(originalGoal);
            var originalMaxTokens = originalGoal!.MaxTokens;
            Assert.Equal(originalModel, originalGoal.Model);

            await brain.UpdateModelAsync("copilot/new-model", 99999, null, TestContext.Current.CancellationToken);

            // Master session registry entry reflects the new model/context window.
            var master = FindSession(registry, "brain-master");
            Assert.NotNull(master);
            Assert.Equal("copilot/new-model", master!.Model);
            Assert.Equal(99999, master.MaxTokens);

            // Snapshot semantics: an EXISTING goal context keeps its EXACT ORIGINAL model/maxTokens.
            // UpdateModelAsync must NOT rewrite the registry entry of a context created before the
            // model change — the goal keeps the exact model and window it was forked with.
            var existingGoal = FindSession(registry, "brain-goal-goal-model-1");
            Assert.NotNull(existingGoal);
            Assert.Equal(originalModel, existingGoal!.Model);
            Assert.Equal(originalMaxTokens, existingGoal.MaxTokens);

            // The other half of snapshot semantics: a NEW goal forked AFTER the model change picks up
            // the NEW config. This proves UpdateModelAsync updated _modelOverride/_maxContextTokens for
            // future contexts while leaving existing ones untouched.
            await brain.ForkSessionForGoalAsync("goal-model-2", TestContext.Current.CancellationToken);
            await brain.PlanIterationAsync(
                CreatePipeline("goal-model-2", "Post-update goal"), null, TestContext.Current.CancellationToken);
            var newGoal = FindSession(registry, "brain-goal-goal-model-2");
            Assert.NotNull(newGoal);
            Assert.Equal("copilot/new-model", newGoal!.Model);
            Assert.Equal(99999, newGoal.MaxTokens);

            // The pre-existing goal STILL keeps its exact original snapshot after the new fork.
            var existingGoalAfter = FindSession(registry, "brain-goal-goal-model-1");
            Assert.NotNull(existingGoalAfter);
            Assert.Equal(originalModel, existingGoalAfter!.Model);
            Assert.Equal(originalMaxTokens, existingGoalAfter.MaxTokens);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    // ── Issue 4: deterministic concurrency / lifecycle / pre-connect tests ───

    [Fact]
    public async Task ExecuteBrainAsync_SameGoal_SerializedByPerContextGate()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();

            // Two DISTINCT entered/release signals — one per LLM call — so we can prove the SECOND
            // call does not enter its LLM call until the FIRST is released. The per-context gate is
            // the only thing that can hold the second call back. If the gate were removed, both
            // calls would enter immediately and enteredSecond would fire before firstRelease.
            var enteredFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var enteredSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocking = new PerCallBlockingChatClient(
                [enteredFirst, enteredSecond], [releaseFirst.Task, releaseSecond.Task], "answered");

            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: blocking, sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-serial", TestContext.Current.CancellationToken);

            // First AskQuestionAsync for the goal enters its LLM call and holds the per-context gate.
            var first = brain.AskQuestionAsync("goal-serial", 1, "Coding", "coder", "Q1?",
                TestContext.Current.CancellationToken);
            await enteredFirst.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Second call for the SAME goal. It must block on that goal's per-context gate and MUST
            // NOT enter its own LLM call while the first holds the gate.
            var second = brain.AskQuestionAsync("goal-serial", 2, "Coding", "coder", "Q2?",
                TestContext.Current.CancellationToken);

            // Deterministic proof of serialization: within a bounded window the second call must NOT
            // enter its LLM call. We wait for whichever happens first — enteredSecond firing (would be
            // a bug) or a short timeout (expected). The timeout winning proves the gate blocks it.
            var timeout = Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
            var winner = await Task.WhenAny(enteredSecond.Task, timeout);
            Assert.Same(timeout, winner);
            Assert.False(enteredSecond.Task.IsCompleted,
                "Second same-goal call must NOT enter its LLM call while the first holds the per-context gate");
            Assert.False(second.IsCompleted, "Second same-goal call must remain blocked on the gate");

            // Release the first call. Now the gate frees and the second call enters its LLM call.
            releaseFirst.SetResult(true);
            await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Deterministic proof the second proceeded ONLY after the first released the gate.
            await enteredSecond.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(enteredSecond.Task.IsCompleted,
                "Second same-goal call must enter its LLM call after the first releases the gate");

            // Release the second call and let it finish.
            releaseSecond.SetResult(true);
            await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExecuteBrainAsync_DifferentGoals_RunInParallel()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            // A single blocking client whose LLM call signals a per-goal `entered` and blocks on a
            // shared `release`. Because per-goal contexts have INDEPENDENT gates, two DIFFERENT goals
            // must both enter their LLM calls concurrently even though neither has been released. If
            // all calls shared one global gate, only one could enter and the other `entered` would
            // never fire before the first is released — this test would then time out.
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var enteredA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var enteredB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocking = new TwoGoalBlockingChatClient(release.Task, enteredA, enteredB,
                "goal-par-a", "goal-par-b", "answered");
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: blocking, sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-par-a", TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-par-b", TestContext.Current.CancellationToken);

            var callA = brain.AskQuestionAsync("goal-par-a", 1, "Coding", "coder", "Qa?",
                TestContext.Current.CancellationToken);
            var callB = brain.AskQuestionAsync("goal-par-b", 1, "Coding", "coder", "Qb?",
                TestContext.Current.CancellationToken);

            // Both LLM calls must be entered concurrently BEFORE either is released. Awaiting BOTH
            // entered signals (with a deadlock-guard timeout) proves genuine parallelism.
            await enteredA.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await enteredB.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Strict proof: both entered while neither the shared release nor either call has completed.
            Assert.True(enteredA.Task.IsCompleted, "Goal A must have entered its LLM call");
            Assert.True(enteredB.Task.IsCompleted, "Goal B must have entered its LLM call");
            Assert.False(release.Task.IsCompleted,
                "Both different-goal Brain calls entered their LLM calls before any release — proving parallelism");
            Assert.False(callA.IsCompleted, "Goal A call must still be blocked on its release");
            Assert.False(callB.IsCompleted, "Goal B call must still be blocked on its release");

            release.SetResult(true);
            await callA.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await callB.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task PublicMethods_BeforeConnect_ThrowInvalidOperationException()
    {
        var tempDir = CreateTempDir();
        try
        {
            // A fresh Brain that has NOT been connected. Every public operation must call
            // EnsureConnected and throw InvalidOperationException before doing any work —
            // EXCEPT PlanIterationAsync, which by contract never throws and instead reports
            // the failure as PlanResult.Failed (see the assertion below).
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient());
            var pipeline = CreatePipeline("goal-preconnect", "Pre-connect goal");

            // PlanIterationAsync still guards with EnsureConnected, but converts the failure
            // into an explicit PlanResult.Failed rather than throwing at its caller.
            var planResult = await brain.PlanIterationAsync(
                pipeline, null, TestContext.Current.CancellationToken);
            Assert.True(planResult.IsFailed);
            Assert.Null(planResult.Plan);
            Assert.StartsWith("Planning failed:", planResult.FailureReason);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => brain.CraftPromptAsync(pipeline, GoalPhase.Coding, null, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => brain.AskQuestionAsync("goal-preconnect", 1, "Coding", "coder", "Q?",
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => brain.ForkSessionForGoalAsync("goal-preconnect", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => brain.DeleteGoalSessionAsync("goal-preconnect", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => brain.RegisterExistingGoalSessionAsync("goal-preconnect", TestContext.Current.CancellationToken));

            // The remaining EnsureConnected-guarded public operations must also throw pre-connect.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => brain.UpdateModelAsync("copilot/other-model", null, null, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => brain.InjectSystemNoteAsync(pipeline, "note", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => brain.InjectOrchestratorInstructionsAsync("instructions", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => brain.SummarizeAndMergeAsync(pipeline, TestContext.Current.CancellationToken));
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ResetSessionAsync_ClearsAllGoalSessionsAndUnregistersThem()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry,
                chatClientFactory: _ => new FakeChatClient());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-reset-1", TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-reset-2", TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-reset-3", TestContext.Current.CancellationToken);

            foreach (var id in new[] { "goal-reset-1", "goal-reset-2", "goal-reset-3" })
            {
                await brain.PlanIterationAsync(
                    CreatePipeline(id, "Reset goal"), null, TestContext.Current.CancellationToken);
            }

            Assert.NotNull(FindSession(registry, "brain-goal-goal-reset-1"));
            Assert.NotNull(FindSession(registry, "brain-goal-goal-reset-2"));
            Assert.NotNull(FindSession(registry, "brain-goal-goal-reset-3"));

            await brain.ResetSessionAsync(TestContext.Current.CancellationToken);

            // Every goal session is torn down and unregistered from the registry.
            Assert.Null(FindSession(registry, "brain-goal-goal-reset-1"));
            Assert.Null(FindSession(registry, "brain-goal-goal-reset-2"));
            Assert.Null(FindSession(registry, "brain-goal-goal-reset-3"));

            // The master session was rebuilt fresh and re-registered with zero tokens.
            var master = FindSession(registry, "brain-master");
            Assert.NotNull(master);
            Assert.Equal(0, master!.CurrentTokens);

            // The goal sessions are gone from the actor as well, so a fresh fork is required.
            Assert.False(brain.GoalSessionExists("goal-reset-1"));
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
            await brain.DeleteGoalSessionAsync("goal-null-1", TestContext.Current.CancellationToken);
            await brain.RegisterExistingGoalSessionAsync("goal-null-1", TestContext.Current.CancellationToken);
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
            Assert.Equal(LlmSessionType.Composer, session!.SessionType);
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

    [Fact]
    public async Task Composer_RunStreamingAsync_SetsStreamingStatus_RestoresIdle()
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

            // A streaming client that captures the composer's registry status while streaming.
            var streaming = new StatusCapturingStreamingChatClient(registry, "composer");
            await InjectComposerChatClient(composer, streaming);

            // Deterministic completion signal: OnStreamingUpdate fires from the actor's terminal
            // transition callback (after _isStreaming is cleared and, per the actor's terminal
            // ordering invariant, AFTER the idle status has already been published to the
            // registry). Complete the TCS once streaming has finished — no polling, no sleeps.
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            composer.OnStreamingUpdate += () =>
            {
                if (!composer.IsStreaming)
                    finished.TrySetResult(true);
            };

            composer.SendMessage("hello");

            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.False(composer.IsStreaming, "Streaming should have finished");

            // During streaming the status must have been "streaming".
            Assert.Equal("streaming", streaming.CapturedStatusDuringStream);

            // After streaming, the composer entry must be restored to "idle".
            var session = FindSession(registry, "composer");
            Assert.NotNull(session);
            Assert.Equal("idle", session!.Status);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task Composer_CompactSessionAsync_RefreshesRegistry()
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

            await InjectComposerChatClient(composer, new CannedReplyChatClient("Summary of conversation"));

            var session = GetComposerSession(composer);
            PopulateSession(session, 15);

            var result = await composer.CompactSessionAsync(TestContext.Current.CancellationToken);
            Assert.True(result);

            // The composer registry entry must have been refreshed with the post-compaction token count.
            var entry = FindSession(registry, "composer");
            Assert.NotNull(entry);
            Assert.Equal(session.EstimatedContextTokens, entry!.CurrentTokens);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task Composer_CompactOldestPercentAsync_RefreshesRegistry()
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

            await InjectComposerChatClient(composer, new CannedReplyChatClient("Summary of conversation"));

            var session = GetComposerSession(composer);
            PopulateSession(session, 30);

            var result = await composer.CompactOldestPercentAsync(50, TestContext.Current.CancellationToken);
            Assert.True(result);

            var entry = FindSession(registry, "composer");
            Assert.NotNull(entry);
            Assert.Equal(session.EstimatedContextTokens, entry!.CurrentTokens);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task Composer_SwitchModelAsync_RefreshesRegistry()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var composer = new Composer("copilot/test-model", NullLogger<Composer>.Instance,
                new FakeGoalStoreForComposer(),
                maxContextTokens: 50_000,
                stateDir: tempDir,
                availableModels: ["copilot/test-model", "copilot/other-model"],
                chatClientFactory: _ => new FakeChatClient(),
                sessionRegistry: registry);
            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            await composer.SwitchModelAsync("copilot/other-model", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

            var entry = FindSession(registry, "composer");
            Assert.NotNull(entry);
            Assert.Equal("copilot/other-model", entry!.Model);
            Assert.Equal(LlmSessionType.Composer, entry.SessionType);
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
                knowledgeGraph: null, configRepo: null, config: ReviewerConfig(), goalStore: null,
                brainRepoManager: null, stateDir: tempDir,
                logger: NullLogger<GoalReviewService>.Instance,
                chatClientFactory: _ => new ReviewCapturingChatClient(
                    """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"ok"}""",
                    registry, seenReviewSessions),
                sessionRegistry: registry);

            await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

            // The review session must have been registered during the call with a complete payload.
            var captured = Assert.Single(seenReviewSessions);
            Assert.Equal(LlmSessionType.GoalReview, captured.SessionType);
            Assert.Equal("goal-review-ok", captured.GoalId);
            Assert.Equal("reviewing", captured.Status);
            Assert.StartsWith("goal-review-goal-review-ok-", captured.SessionId);

            // After completion, the review session must be unregistered.
            Assert.DoesNotContain(registry.GetAll(), s => s.SessionType == LlmSessionType.GoalReview);
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
                knowledgeGraph: null, configRepo: null, config: ReviewerConfig(), goalStore: null,
                brainRepoManager: null, stateDir: tempDir,
                logger: NullLogger<GoalReviewService>.Instance,
                chatClientFactory: _ => new ThrowingReviewChatClient(),
                sessionRegistry: registry);

            // The review agent throws internally, but ReviewGoalAsync returns a NeedsChanges result.
            var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);
            Assert.Equal("NeedsChanges", result.Verdict);

            // Even on failure, the review session must be unregistered.
            Assert.DoesNotContain(registry.GetAll(), s => s.SessionType == LlmSessionType.GoalReview);
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
                knowledgeGraph: null, configRepo: null, config: ReviewerConfig(), goalStore: null,
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
            Assert.Single(registry.GetAll(), s => s.SessionType == LlmSessionType.GoalReview);

            // Release the first review so it completes cleanly and unregisters.
            tcs.SetResult("""{"verdict":"Approved","issues":[],"verified":[],"recommendation":"ok"}""");
            await first;

            Assert.DoesNotContain(registry.GetAll(), s => s.SessionType == LlmSessionType.GoalReview);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task GoalReviewService_RegistersSessionWithCompletePayload()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var goal = new Goal { Id = "goal-review-payload", Description = "Payload review goal", ReviewStatus = ReviewStatus.None };

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var service = new GoalReviewService(
                knowledgeGraph: null, configRepo: null, config: ReviewerConfig(), goalStore: null,
                brainRepoManager: null, stateDir: tempDir,
                logger: NullLogger<GoalReviewService>.Instance,
                chatClientFactory: _ => new BlockingReviewChatClient(tcs.Task, entered),
                sessionRegistry: registry);

            var before = DateTime.UtcNow.AddSeconds(-1);
            var reviewTask = service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);
            await entered.Task;

            // Inspect the registry mid-review — the session must have a complete payload.
            var captured = Assert.Single(registry.GetAll(), s => s.SessionType == LlmSessionType.GoalReview);
            Assert.StartsWith("goal-review-goal-review-payload-", captured.SessionId);
            Assert.Equal(LlmSessionType.GoalReview, captured.SessionType);
            Assert.Equal("goal-review-payload", captured.GoalId);
            Assert.Equal("reviewer-model", captured.Model);
            Assert.Equal(0, captured.CurrentTokens);
            Assert.True(captured.MaxTokens > 0, "MaxTokens should be positive");
            Assert.Equal("reviewing", captured.Status);
            Assert.InRange(captured.LastActivity, before, DateTime.UtcNow.AddSeconds(1));

            // Release the review so it completes cleanly.
            tcs.SetResult("""{"verdict":"Approved","issues":[],"verified":[],"recommendation":"ok"}""");
            await reviewTask;

            Assert.DoesNotContain(registry.GetAll(), s => s.SessionType == LlmSessionType.GoalReview);
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task GoalReviewService_UnregistersReviewSession_OnCancellation()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var goal = new Goal { Id = "goal-review-cancel", Description = "Cancel review goal", ReviewStatus = ReviewStatus.None };

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource();

            var service = new GoalReviewService(
                knowledgeGraph: null, configRepo: null, config: ReviewerConfig(), goalStore: null,
                brainRepoManager: null, stateDir: tempDir,
                logger: NullLogger<GoalReviewService>.Instance,
                chatClientFactory: _ => new BlockingReviewChatClient(tcs.Task, entered),
                sessionRegistry: registry);

            var reviewTask = service.ReviewGoalAsync(goal, cts.Token);
            await entered.Task;

            Assert.Single(registry.GetAll(), s => s.SessionType == LlmSessionType.GoalReview);

            // Cancel the review — it must unregister the session and propagate cancellation.
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reviewTask);

            Assert.DoesNotContain(registry.GetAll(), s => s.SessionType == LlmSessionType.GoalReview);
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
                knowledgeGraph: null, configRepo: null, config: ReviewerConfig(), goalStore: null,
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

    // ── DispatcherMaintenance ─────────────────────────────────────────────────

    [Fact]
    public async Task DispatcherMaintenance_RegistersExistingGoalSession_WhenSessionFileExists()
    {
        var tempDir = CreateTempDir();
        try
        {
            // A real DistributedBrain is required: DispatcherMaintenance casts IDistributedBrain to
            // the concrete DistributedBrain before calling RegisterExistingGoalSession, so a fake
            // implementing only the interface can never exercise that branch.
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            const string goalId = "goal-restore-existing";

            // Write an on-disk goal session file directly (NO context) with extra messages so it has
            // a distinctly non-zero token count. A fresh fork from the (empty) master session would
            // have zero tokens — so a non-zero CurrentTokens after restoration proves the EXISTING
            // file was read via RegisterExistingGoalSession, not re-forked from master.
            var goalSessionFile = Path.Combine(tempDir, "actors", $"brain-goal-{goalId}.json");
            var goalSession = AgentSession.Create($"brain-goal-{goalId}");
            for (var i = 0; i < 6; i++)
            {
                goalSession.MessageHistory.Add(new ChatMessage(
                    i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                    $"Persisted conversation message {i} with enough text to accrue tokens."));
            }
            await goalSession.SaveAsync(goalSessionFile, TestContext.Current.CancellationToken);
            var expectedTokens = goalSession.EstimatedContextTokens;
            Assert.True(expectedTokens > 0, "Enriched goal session must have non-zero tokens");

            // Simulate a restart: only the on-disk session file remains; no registry entry, no context.
            Assert.Null(FindSession(registry, $"brain-goal-{goalId}"));

            // A store-backed pipeline manager with one active pipeline to restore for this goal.
            using var dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new PipelineStore(dbContext, NullLogger<PipelineStore>.Instance);
            var seedManager = new GoalPipelineManager(store);
            var goal = new Goal { Id = goalId, Description = "Restore existing goal", RepositoryNames = ["test-repo"] };
            var pipeline = seedManager.CreatePipeline(goal);
            pipeline.AdvanceTo(GoalPhase.Coding);
            pipeline.SetActiveTask("task-1", $"feature/{goalId}");
            seedManager.PersistFull(pipeline);

            var restoreManager = new GoalPipelineManager(store);
            var maintenance = new DispatcherMaintenance(
                restoreManager,
                new GoalManager(),
                new TaskQueue(),
                new GrpcWorkerGateway(new WorkerPool()),
                brain: brain,
                agentsManager: null,
                configRepo: null,
                redispatchQueue: new ConcurrentQueue<string>(),
                logger: NullLogger.Instance,
                knowledgeGraph: null,
                goalStore: null,
                repoManager: null,
                config: null);

            await maintenance.RestoreActivePipelinesAsync(TestContext.Current.CancellationToken);

            // The session file existed, so the restore logic must take the else branch and call
            // RegisterExistingGoalSession, which adopts the EXISTING file rather than forking a
            // fresh master session. The registry entry is published by the goal's child actor on its
            // first call, and its token count proves the restored file (not a zero-token fork) is in use.
            await brain.PlanIterationAsync(
                new GoalPipeline(new Goal { Id = goalId, Description = "Restore existing goal" }),
                null, TestContext.Current.CancellationToken);

            var restored = FindSession(registry, $"brain-goal-{goalId}");
            Assert.NotNull(restored);
            Assert.Equal(LlmSessionType.BrainGoal, restored!.SessionType);
            Assert.Equal(goalId, restored.GoalId);
            Assert.Equal("idle", restored.Status);
            // Reference-sensitive: tokens come from the existing file (RegisterExistingGoalSession),
            // NOT from a fresh zero-token master fork.
            // The plan call appended to the restored history, so the reported count must be at
            // least the restored file's token count — a zero-token master fork could never reach it.
            Assert.True(restored.CurrentTokens >= expectedTokens,
                $"Expected at least the restored file's {expectedTokens} tokens, got {restored.CurrentTokens}.");
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task DispatcherMaintenance_ForksSession_WhenSessionMissing()
    {
        using var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new PipelineStore(dbContext, NullLogger<PipelineStore>.Instance);
        var pipelineManager = new GoalPipelineManager(store);

        var goal = new Goal { Id = "goal-restore-2", Description = "Restore goal 2", RepositoryNames = ["test-repo"] };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.SetActiveTask("task-2", "feature/goal-restore-2");
        pipelineManager.PersistFull(pipeline);

        var restoreManager = new GoalPipelineManager(store);

        // Session missing → the restore logic must fork a new session from master.
        var brain = new RegisterTrackingBrain(sessionExists: false);
        var maintenance = new DispatcherMaintenance(
            restoreManager,
            new GoalManager(),
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            brain: brain,
            agentsManager: null,
            configRepo: null,
            redispatchQueue: new ConcurrentQueue<string>(),
            logger: NullLogger.Instance,
            knowledgeGraph: null,
            goalStore: null,
            repoManager: null,
            config: null);

        await maintenance.RestoreActivePipelinesAsync(TestContext.Current.CancellationToken);

        Assert.True(brain.GoalSessionExistsCalled, "GoalSessionExists should have been queried");
        Assert.Contains("goal-restore-2", brain.ForkCalls);
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
            seen.AddRange(registry.GetAll().Where(s => s.SessionType == LlmSessionType.GoalReview));

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

/// <summary>A chat client that returns a fixed non-empty reply, avoiding empty-response retry backoff.</summary>
file sealed class CannedReplyChatClient(string reply) : IChatClient
{
    public ChatClientMetadata Metadata => new("canned", null, "canned-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
        {
            FinishReason = ChatFinishReason.Stop,
        });

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming not used in this fake client.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// A chat client that signals a DISTINCT <c>entered</c> TaskCompletionSource for each LLM call,
/// in the order the calls enter, and blocks each call on its own <c>release</c> signal. This lets
/// a test prove that a second call for the SAME goal does NOT enter its LLM call until the first
/// is released — i.e. the per-context gate serializes same-goal calls. If the gate were removed,
/// both calls would enter immediately and BOTH entered signals would fire before any release.
/// </summary>
file sealed class PerCallBlockingChatClient : IChatClient
{
    private readonly TaskCompletionSource<bool>[] _entered;
    private readonly Task<bool>[] _release;
    private readonly string _reply;
    private int _callIndex = -1;

    public PerCallBlockingChatClient(
        TaskCompletionSource<bool>[] entered, Task<bool>[] release, string reply)
    {
        if (entered.Length != release.Length)
            throw new ArgumentException("entered and release arrays must have equal length");
        _entered = entered;
        _release = release;
        _reply = reply;
    }

    public ChatClientMetadata Metadata => new("per-call-blocking", null, "blocking-model");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Atomically claim the next call slot so concurrent calls take distinct signals in
        // entry order. This is the mechanism that lets the test distinguish "first" from "second".
        var index = Interlocked.Increment(ref _callIndex);
        if (index >= _entered.Length)
            throw new InvalidOperationException($"Unexpected extra LLM call at index {index}");

        _entered[index].TrySetResult(true);
        await _release[index].WaitAsync(cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply))
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

/// <summary>
/// A chat client that signals when its LLM call is entered, then blocks until released.
/// Used to prove that a goal's per-context gate serializes that goal's Brain operations while
/// letting different goals run in parallel.
/// </summary>
file sealed class BlockingBrainChatClient(Task<bool> release, TaskCompletionSource<bool> entered, string reply) : IChatClient
{
    public ChatClientMetadata Metadata => new("blocking-brain", null, "blocking-model");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        entered.TrySetResult(true);
        await release.WaitAsync(cancellationToken);
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

/// <summary>
/// A chat client that distinguishes two goals by inspecting the goal id embedded in the prompt.
/// Each goal's LLM call signals its own <c>entered</c> TCS, then all calls block on a shared
/// <c>release</c>. Used to prove that two DIFFERENT goals enter their LLM calls concurrently
/// (independent per-context gates), rather than serializing through a single shared gate.
/// </summary>
file sealed class TwoGoalBlockingChatClient(
    Task<bool> release,
    TaskCompletionSource<bool> enteredA,
    TaskCompletionSource<bool> enteredB,
    string goalIdA,
    string goalIdB,
    string reply) : IChatClient
{
    public ChatClientMetadata Metadata => new("two-goal-blocking", null, "blocking-model");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Identify which goal this call belongs to by scanning the prompt text for the goal id.
        var text = string.Join("\n", messages.Select(m => m.Text));
        if (text.Contains(goalIdA, StringComparison.Ordinal))
            enteredA.TrySetResult(true);
        else if (text.Contains(goalIdB, StringComparison.Ordinal))
            enteredB.TrySetResult(true);

        await release.WaitAsync(cancellationToken);
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

/// <summary>
/// A streaming chat client that captures the <c>composer</c> registry status while streaming,
/// then yields a single completed text update.
/// </summary>
file sealed class StatusCapturingStreamingChatClient(LlmSessionRegistry registry, string sessionId) : IChatClient
{
    public string? CapturedStatusDuringStream { get; private set; }

    public ChatClientMetadata Metadata => new("stream-capture", null, "stream-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
        {
            FinishReason = ChatFinishReason.Stop,
        });

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => StreamAsync(cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var session = registry.GetAll().FirstOrDefault(s => s.SessionId == sessionId);
        CapturedStatusDuringStream = session?.Status;
        yield return new ChatResponseUpdate(ChatRole.Assistant, "done") { FinishReason = ChatFinishReason.Stop };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// A fake <see cref="IDistributedBrain"/> that tracks fork/register/exists calls for
/// DispatcherMaintenance restoration tests.
/// </summary>
file sealed class RegisterTrackingBrain(bool sessionExists) : IDistributedBrain
{
    public List<string> ForkCalls { get; } = [];
    public List<string> RegisterExistingCalls { get; } = [];
    public bool GoalSessionExistsCalled { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) => Task.CompletedTask;

    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PlanResult.Success(IterationPlan.Default()));

    public Task<PromptResult> CraftPromptAsync(GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PromptResult.Success("prompt"));

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) => Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) => Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) => Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("ok"));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default)
    {
        ForkCalls.Add(goalId);
        return Task.CompletedTask;
    }

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default)
    {
        RegisterExistingCalls.Add(goalId);
        return Task.CompletedTask;
    }

    public bool GoalSessionExists(string goalId)
    {
        GoalSessionExistsCalled = true;
        return sessionExists;
    }

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}
