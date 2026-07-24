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

    private static LlmSessionInfo? FindSession(LlmSessionRegistry registry, string sessionId) =>
        registry.GetAll().FirstOrDefault(s => s.SessionId == sessionId);

    private static GoalPipeline CreatePipeline(string goalId, string description) =>
        new(new Goal { Id = goalId, Description = description });

    /// <summary>Injects a fake chat client into a Composer and rebuilds its internal agent.</summary>
    private static void InjectComposerChatClient(Composer composer, IChatClient fakeClient)
    {
        var chatClientField = typeof(Composer).GetField("_chatClient",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_chatClient field not found on Composer");
        chatClientField.SetValue(composer, fakeClient);

        var recreateAgent = typeof(Composer).GetMethod("RecreateAgent",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RecreateAgent method not found on Composer");
        recreateAgent.Invoke(composer, null);
    }

    /// <summary>Gets the private <c>_session</c> field from a Composer.</summary>
    private static AgentSession GetComposerSession(Composer composer)
    {
        var sessionField = typeof(Composer).GetField("_session",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_session field not found on Composer");
        return (AgentSession)sessionField.GetValue(composer)!;
    }

    /// <summary>Gets the private <c>_isStreaming</c> field value from a Composer.</summary>
    private static bool GetComposerIsStreaming(Composer composer)
    {
        var field = typeof(Composer).GetField("_isStreaming",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_isStreaming field not found on Composer");
        return (bool)field.GetValue(composer)!;
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

    // ── DistributedBrain: gated operations acquire _brainCallGate ──────────────

    [Fact]
    public async Task ForkSessionForGoalAsync_DoesNotBlockOnBrainCallGate()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            // A chat client that blocks the first LLM call so _brainCallGate is held while we fork.
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocking = new BlockingBrainChatClient(release.Task, entered, "planning done");
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: blocking, sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-nogate-a", TestContext.Current.CancellationToken);

            // Hold _brainCallGate via a blocked LLM call for goal A.
            var pipeline = CreatePipeline("goal-nogate-a", "No-gate fork goal A");
            var planTask = brain.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);

            // Deterministic: the blocking client signals `entered` from inside the LLM call, which
            // only happens while _brainCallGate is held. Awaiting it proves the gate is held.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Fork a DIFFERENT goal. Because ForkSessionForGoalAsync now uses _sessionLock (NOT
            // _brainCallGate), it must complete even while the gate is held by the blocked LLM call.
            // The bounded WaitAsync is only a deadlock guard — success proves fork does not wait
            // on _brainCallGate.
            await brain.ForkSessionForGoalAsync("goal-nogate-b", TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // The fork registered the goal session while the gate was still held.
            Assert.NotNull(FindSession(registry, "brain-goal-goal-nogate-b"));

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
    public async Task ForkSessionForGoalAsync_SerializedBySessionLock()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            // Reach into the private _sessionLock and hold it, proving that ForkSessionForGoalAsync
            // is serialized behind it (i.e. it acquires _sessionLock as its first operation).
            var sessionLockField = typeof(DistributedBrain).GetField("_sessionLock",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_sessionLock field not found on DistributedBrain");
            var sessionLock = (SemaphoreSlim)sessionLockField.GetValue(brain)!;

            await sessionLock.WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                // Start a fork while _sessionLock is held — it must park on the lock and NOT register.
                var forkTask = brain.ForkSessionForGoalAsync("goal-serialized-1", TestContext.Current.CancellationToken);

                // Deterministic proof via observable registry state — the fork cannot have run its
                // core logic because _sessionLock is held.
                Assert.Null(FindSession(registry, "brain-goal-goal-serialized-1"));
                Assert.False(forkTask.IsCompleted);

                // Release _sessionLock; the fork must now complete.
                sessionLock.Release();
                await forkTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

                Assert.NotNull(FindSession(registry, "brain-goal-goal-serialized-1"));
            }
            catch
            {
                // Ensure the lock is released even if an assertion fails mid-test.
                if (sessionLock.CurrentCount == 0)
                    sessionLock.Release();
                throw;
            }
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task DeleteGoalSession_AcquiresBothLocks_InOrder()
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
            await brain.ForkSessionForGoalAsync("goal-both-a", TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-both-b", TestContext.Current.CancellationToken);

            // Part 1: prove delete blocks on _brainCallGate.
            var pipeline = CreatePipeline("goal-both-a", "Both-locks goal A");
            var planTask = brain.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.NotNull(FindSession(registry, "brain-goal-goal-both-b"));
            var deleteTask = Task.Run(() => brain.DeleteGoalSession("goal-both-b"),
                TestContext.Current.CancellationToken);

            // The gate is held, so the delete cannot have unregistered the session.
            Assert.NotNull(FindSession(registry, "brain-goal-goal-both-b"));

            release.SetResult(true);
            await deleteTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await planTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Null(FindSession(registry, "brain-goal-goal-both-b"));

            // Part 2: with _brainCallGate free, prove delete also blocks on _sessionLock.
            var sessionLockField = typeof(DistributedBrain).GetField("_sessionLock",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_sessionLock field not found on DistributedBrain");
            var sessionLock = (SemaphoreSlim)sessionLockField.GetValue(brain)!;

            await sessionLock.WaitAsync(TestContext.Current.CancellationToken);
            var lockHeld = true;
            try
            {
                Assert.NotNull(FindSession(registry, "brain-goal-goal-both-a"));
                var deleteTask2 = Task.Run(() => brain.DeleteGoalSession("goal-both-a"),
                    TestContext.Current.CancellationToken);

                // _sessionLock is held, so the delete cannot reach DeleteGoalSessionCore.
                Assert.NotNull(FindSession(registry, "brain-goal-goal-both-a"));
                Assert.False(deleteTask2.IsCompleted);

                sessionLock.Release();
                lockHeld = false;
                await deleteTask2.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.Null(FindSession(registry, "brain-goal-goal-both-a"));
            }
            catch
            {
                if (lockHeld && sessionLock.CurrentCount == 0)
                    sessionLock.Release();
                throw;
            }
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task DeleteGoalSession_AcquiresBrainCallGate_SerializesWithGatedOperation()
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
            await brain.ForkSessionForGoalAsync("goal-gate-delete", TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-gate-delete-2", TestContext.Current.CancellationToken);

            // Hold the gate via a blocked LLM call.
            var pipeline = CreatePipeline("goal-gate-delete", "Gate delete goal");
            var planTask = brain.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);

            // Deterministic: `entered` is signalled from inside the LLM call while _brainCallGate is
            // held. Awaiting it proves the gate is held.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // The goal session is registered and the delete has not run yet.
            Assert.NotNull(FindSession(registry, "brain-goal-goal-gate-delete-2"));

            // DeleteGoalSession acquires the gate synchronously before touching the registry, so run
            // it on a background thread. While the gate is held it cannot reach DeleteGoalSessionCore
            // (which is what unregisters the goal session).
            var deleteTask = Task.Run(() => brain.DeleteGoalSession("goal-gate-delete-2"),
                TestContext.Current.CancellationToken);

            // Deterministic proof of the blocked state via observable registry state — NO timing wait.
            // The entry remains present because the delete is serialized behind the held gate and
            // cannot have unregistered it. If the gate were not held, the delete would already have
            // removed the entry.
            Assert.NotNull(FindSession(registry, "brain-goal-goal-gate-delete-2"));

            // Release the gate; the delete must now complete and unregister the session. The bounded
            // WaitAsync is only a deadlock guard, not the synchronization mechanism.
            release.SetResult(true);
            await deleteTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await planTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Null(FindSession(registry, "brain-goal-goal-gate-delete-2"));
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExecuteBrainAsync_IdleRestorationUsesStableSessionReference()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();

            // This chat client replaces the brain's private _session field DURING the LLM call
            // (simulating overflow recovery). If the finally block read _session fresh, the idle
            // restoration would report the REPLACEMENT session's token count. The production code
            // captures a stable reference before the call, so the restored entry must report the
            // ORIGINAL session's token count.
            var replacing = new SessionReplacingChatClient("answered");
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: replacing, sessionRegistry: registry);
            replacing.Brain = brain;
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-stable-1", TestContext.Current.CancellationToken);

            // AskQuestionAsync issues exactly ONE ExecuteBrainAsync call (no tool-nudge retry loop),
            // so _session is replaced exactly once during the call.
            await brain.AskQuestionAsync("goal-stable-1", 1, "Coding", "coder", "Should I proceed?",
                TestContext.Current.CancellationToken);

            Assert.NotNull(replacing.OriginalSession);

            // The idle-status entry's tokens must match the STABLE (original) session reference — the
            // same object captured before the call — evaluated after the call completed. The huge
            // replacement session has a materially different token count and must NOT be used.
            var stableTokens = replacing.OriginalSession!.EstimatedContextTokens;
            Assert.True(replacing.ReplacementTokens > stableTokens,
                $"Replacement tokens ({replacing.ReplacementTokens}) must exceed stable session tokens ({stableTokens})");

            var goalSession = FindSession(registry, "brain-goal-goal-stable-1");
            Assert.NotNull(goalSession);
            Assert.Equal("idle", goalSession!.Status);
            Assert.Equal("goal-stable-1", goalSession.GoalId);

            // Reference-sensitive assertion: idle restoration must use the STABLE (original) session
            // reference captured before the call — NOT the replacement _session installed mid-call.
            Assert.Equal(stableTokens, goalSession.CurrentTokens);
            Assert.NotEqual(replacing.ReplacementTokens, goalSession.CurrentTokens);
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
            InjectComposerChatClient(composer, streaming);

            // Deterministic completion signal: OnStreamingUpdate fires in the streaming finally block
            // (after _isStreaming is set to false and the idle status is refreshed). Complete the TCS
            // once streaming has finished — no polling, no sleeps.
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            composer.OnStreamingUpdate += () =>
            {
                if (!GetComposerIsStreaming(composer))
                    finished.TrySetResult(true);
            };

            composer.SendMessage("hello");

            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.False(GetComposerIsStreaming(composer), "Streaming should have finished");

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

            InjectComposerChatClient(composer, new CannedReplyChatClient("Summary of conversation"));

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

            InjectComposerChatClient(composer, new CannedReplyChatClient("Summary of conversation"));

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

            await composer.SwitchModelAsync("copilot/other-model");

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
                knowledgeGraph: null, configRepo: null, config: null, goalStore: null,
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
                knowledgeGraph: null, configRepo: null, config: null, goalStore: null,
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
            Assert.Equal(Constants.DefaultWorkerModel, captured.Model);
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
                knowledgeGraph: null, configRepo: null, config: null, goalStore: null,
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

            // Fork to create the on-disk goal session file, then enrich it with extra messages so
            // it has a distinctly non-zero token count. A fresh fork from the (empty) master session
            // would have zero tokens — so a non-zero CurrentTokens after restoration proves the
            // EXISTING file was read via RegisterExistingGoalSession, not re-forked from master.
            await brain.ForkSessionForGoalAsync(goalId, TestContext.Current.CancellationToken);
            var goalSessionFile = Path.Combine(tempDir, $"brain-goal-{goalId}.json");
            var goalSession = await AgentSession.LoadAsync(goalSessionFile, TestContext.Current.CancellationToken);
            for (var i = 0; i < 6; i++)
            {
                goalSession.MessageHistory.Add(new ChatMessage(
                    i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                    $"Persisted conversation message {i} with enough text to accrue tokens."));
            }
            await goalSession.SaveAsync(goalSessionFile, TestContext.Current.CancellationToken);
            var expectedTokens = goalSession.EstimatedContextTokens;
            Assert.True(expectedTokens > 0, "Enriched goal session must have non-zero tokens");

            // Simulate a restart: only the on-disk session file remains; the registry entry is gone.
            registry.Unregister($"brain-goal-{goalId}");
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
                dispatchedGoals: new ConcurrentDictionary<string, bool>(),
                redispatchQueue: new ConcurrentQueue<string>(),
                logger: NullLogger.Instance,
                knowledgeGraph: null,
                goalStore: null,
                repoManager: null,
                config: null);

            await maintenance.RestoreActivePipelinesAsync(TestContext.Current.CancellationToken);

            // The session file existed, so the restore logic must take the else branch and call
            // RegisterExistingGoalSession. This re-registers the goal session in the registry using
            // the EXISTING file's token count. If that else branch were deleted, no entry would exist.
            var restored = FindSession(registry, $"brain-goal-{goalId}");
            Assert.NotNull(restored);
            Assert.Equal(LlmSessionType.BrainGoal, restored!.SessionType);
            Assert.Equal(goalId, restored.GoalId);
            Assert.Equal("idle", restored.Status);
            // Reference-sensitive: tokens come from the existing file (RegisterExistingGoalSession),
            // NOT from a fresh zero-token master fork.
            Assert.Equal(expectedTokens, restored.CurrentTokens);
            Assert.True(restored.CurrentTokens > 0);
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
            dispatchedGoals: new ConcurrentDictionary<string, bool>(),
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
/// A chat client that signals when its LLM call is entered, then blocks until released.
/// Used to prove that <c>_brainCallGate</c> serializes gated Brain operations.
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
/// A chat client that, during its LLM call, replaces the brain's private <c>_session</c> field
/// with a NEW, larger session (simulating overflow recovery). Used to prove that idle-status
/// restoration in <c>ExecuteBrainAsync</c>'s finally block uses the STABLE session reference
/// captured before the call — not the replacement session.
/// </summary>
file sealed class SessionReplacingChatClient(string reply) : IChatClient
{
    private static readonly System.Reflection.FieldInfo SessionField =
        typeof(DistributedBrain).GetField("_session",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("_session field not found on DistributedBrain");

    public DistributedBrain? Brain { get; set; }

    /// <summary>The original session object captured at the moment of the call.</summary>
    public AgentSession? OriginalSession { get; private set; }

    /// <summary>Token count of the replacement session installed during the call.</summary>
    public long ReplacementTokens { get; private set; }

    public ChatClientMetadata Metadata => new("session-replacing", null, "replacing-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var brain = Brain ?? throw new InvalidOperationException("Brain must be assigned before use");

        // Capture the original session object, then replace _session with a distinctly LARGER one.
        // The replacement is made deliberately huge so its token count cannot collide with the
        // original session's post-execution token count.
        OriginalSession = (AgentSession)SessionField.GetValue(brain)!;

        var replacement = AgentSession.Create("brain-goal-replacement");
        for (var i = 0; i < 400; i++)
        {
            replacement.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Replacement session message {i} carrying substantial additional token weight to guarantee a much larger context than the original session ever reaches."));
        }
        ReplacementTokens = replacement.EstimatedContextTokens;
        SessionField.SetValue(brain, replacement);

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

    public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) => Task.CompletedTask;

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

    public void DeleteGoalSession(string goalId) { }

    public void RegisterExistingGoalSession(string goalId) => RegisterExistingCalls.Add(goalId);

    public bool GoalSessionExists(string goalId)
    {
        GoalSessionExistsCalled = true;
        return sessionExists;
    }

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}
