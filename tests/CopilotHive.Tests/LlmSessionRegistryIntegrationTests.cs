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

    /// <summary>Reaches into the private <c>_sessionLock</c> field of a <see cref="DistributedBrain"/>.</summary>
    private static SemaphoreSlim GetSessionLock(DistributedBrain brain)
    {
        var sessionLockField = typeof(DistributedBrain).GetField("_sessionLock",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_sessionLock field not found on DistributedBrain");
        return (SemaphoreSlim)sessionLockField.GetValue(brain)!;
    }

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

            // The fork registered the goal session while goal A's gate was still held.
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
                Path.Combine(tempDir, "brain-goal-goal-reg-nogate.json"), TestContext.Current.CancellationToken);
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

            Assert.NotNull(FindSession(registry, "brain-goal-goal-reg-nogate"));

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
            var sessionLock = GetSessionLock(brain);

            await sessionLock.WaitAsync(TestContext.Current.CancellationToken);
            var lockHeld = true;
            try
            {
                // Start a fork while _sessionLock is held — it must park on the lock and NOT register.
                var forkTask = brain.ForkSessionForGoalAsync("goal-serialized-1", TestContext.Current.CancellationToken);

                // Deterministic wait: give the fork ample time to run to completion IF it were not
                // serialized (fork is just a file save + registry update, well under 200ms). If it is
                // correctly blocked on the held _sessionLock, it will still be incomplete after this
                // delay. This defeats an implementation that skips _sessionLock but is momentarily
                // awaiting asynchronous session-file persistence.
                await Task.Delay(200, TestContext.Current.CancellationToken);
                Assert.False(forkTask.IsCompleted,
                    "Fork must remain blocked while _sessionLock is held");
                Assert.Null(FindSession(registry, "brain-goal-goal-serialized-1"));

                // Release _sessionLock; the fork must now complete.
                sessionLock.Release();
                lockHeld = false;
                await forkTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                Assert.NotNull(FindSession(registry, "brain-goal-goal-serialized-1"));
            }
            catch
            {
                // Ensure the lock is released even if an assertion fails mid-test.
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
    public async Task DeleteGoalSession_BlocksOnSessionLock()
    {
        var tempDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), sessionRegistry: registry);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-order-b", TestContext.Current.CancellationToken);

            var sessionLock = GetSessionLock(brain);

            // Step 1: hold _sessionLock from the test thread. DeleteGoalSessionAsync acquires
            // _sessionLock as its first operation, so it must block while the lock is held.
            await sessionLock.WaitAsync(TestContext.Current.CancellationToken);
            var lockHeld = true;
            try
            {
                Assert.NotNull(FindSession(registry, "brain-goal-goal-order-b"));

                // Step 2: start the delete on a background thread. It must block on _sessionLock.
                var deleteTask = Task.Run(() => brain.DeleteGoalSessionAsync("goal-order-b"),
                    TestContext.Current.CancellationToken);

                // Step 3: the delete cannot complete while _sessionLock is held.
                await Task.Delay(200, TestContext.Current.CancellationToken);
                Assert.False(deleteTask.IsCompleted,
                    "Delete must block on _sessionLock while it is held");
                Assert.NotNull(FindSession(registry, "brain-goal-goal-order-b"));

                // Step 4: release _sessionLock; the delete completes and unregisters the session.
                sessionLock.Release();
                lockHeld = false;
                await deleteTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.Null(FindSession(registry, "brain-goal-goal-order-b"));
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
    public async Task DeleteGoalSession_WaitsForActiveLlmCallToDrain()
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

            // Hold goal-gate-delete's per-context lease via a blocked, single LLM call
            // (AskQuestionAsync issues exactly ONE ExecuteBrainAsync call — no tool-nudge retry loop —
            // so the context is used exactly once and can be safely removed mid-call by the delete).
            var askTask = brain.AskQuestionAsync("goal-gate-delete", 1, "Coding", "coder", "Proceed?",
                TestContext.Current.CancellationToken);

            // Deterministic: `entered` is signalled from inside the LLM call while goal-gate-delete's
            // lease is held. Awaiting it proves the lease is held.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Deleting a DIFFERENT goal (goal-gate-delete-2) has no active LLM call, so it drains
            // immediately and completes even while goal-gate-delete's lease is held. This proves the
            // per-goal contexts are independent — a busy goal does not block another goal's delete.
            Assert.NotNull(FindSession(registry, "brain-goal-goal-gate-delete-2"));
            await brain.DeleteGoalSessionAsync("goal-gate-delete-2", TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Null(FindSession(registry, "brain-goal-goal-gate-delete-2"));

            // Deleting goal-gate-delete itself must wait for its active LLM call to drain
            // (WaitForDrainAsync) before it can dispose the context and unregister the session.
            var deleteTask = Task.Run(() => brain.DeleteGoalSessionAsync("goal-gate-delete"),
                TestContext.Current.CancellationToken);

            // The delete cannot complete while the blocked LLM call still holds the lease.
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.False(deleteTask.IsCompleted,
                "Delete of a goal with an active LLM call must wait for the call to drain");

            // Release the LLM call; the ask completes, the lease drops, and the delete drains.
            release.SetResult(true);
            await askTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await deleteTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Null(FindSession(registry, "brain-goal-goal-gate-delete"));
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

            // This chat client replaces the active goal context's Session DURING the LLM call
            // (simulating overflow recovery). If the finally block read context.Session fresh, the idle
            // restoration would report the REPLACEMENT session's token count. The production code
            // captures a stable reference before the call, so the restored entry must report the
            // ORIGINAL session's token count.
            var replacing = new SessionReplacingChatClient("answered") { GoalId = "goal-stable-1" };
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: replacing, sessionRegistry: registry);
            replacing.Brain = brain;
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-stable-1", TestContext.Current.CancellationToken);

            // AskQuestionAsync issues exactly ONE ExecuteBrainAsync call (no tool-nudge retry loop),
            // so context.Session is replaced exactly once during the call.
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

            // Write an on-disk session file directly (no context) to simulate a restart where only
            // the file remains and no per-goal Brain context exists yet.
            var existing = AgentSession.Create("brain-goal-goal-existing-1");
            existing.MessageHistory.Add(new ChatMessage(ChatRole.User, "restored marker"));
            await existing.SaveAsync(
                Path.Combine(tempDir, "brain-goal-goal-existing-1.json"), TestContext.Current.CancellationToken);
            Assert.Null(FindSession(registry, "brain-goal-goal-existing-1"));

            await brain.RegisterExistingGoalSessionAsync("goal-existing-1", TestContext.Current.CancellationToken);

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

            // Write an on-disk goal session file directly (NO context) with extra messages so it has
            // a distinctly non-zero token count. A fresh fork from the (empty) master session would
            // have zero tokens — so a non-zero CurrentTokens after restoration proves the EXISTING
            // file was read via RegisterExistingGoalSession, not re-forked from master.
            var goalSessionFile = Path.Combine(tempDir, $"brain-goal-{goalId}.json");
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
/// A chat client that, during its LLM call, replaces the active goal context's <c>Session</c>
/// with a NEW, larger session (simulating overflow recovery). Used to prove that idle-status
/// restoration in <c>ExecuteBrainAsync</c>'s finally block uses the STABLE session reference
/// captured before the call — not the replacement session.
/// </summary>
file sealed class SessionReplacingChatClient(string reply) : IChatClient
{
    private static readonly System.Reflection.FieldInfo GoalContextsField =
        typeof(DistributedBrain).GetField("_goalContexts",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("_goalContexts field not found on DistributedBrain");

    public DistributedBrain? Brain { get; set; }

    /// <summary>The goal whose context session is replaced during the call.</summary>
    public string GoalId { get; set; } = "";

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

        // Locate the active goal context via reflection, capture the original session object, then
        // replace context.Session with a distinctly LARGER one. The replacement is made deliberately
        // huge so its token count cannot collide with the original session's post-execution count.
        var contexts = GoalContextsField.GetValue(brain)!;
        var indexer = contexts.GetType().GetProperty("Item")
            ?? throw new InvalidOperationException("_goalContexts indexer not found");
        var context = indexer.GetValue(contexts, [GoalId])
            ?? throw new InvalidOperationException($"No context for goal '{GoalId}'");
        var sessionProp = context.GetType().GetProperty("Session",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException("Session property not found on GoalBrainContext");

        OriginalSession = (AgentSession)sessionProp.GetValue(context)!;

        var replacement = AgentSession.Create("brain-goal-replacement");
        for (var i = 0; i < 400; i++)
        {
            replacement.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Replacement session message {i} carrying substantial additional token weight to guarantee a much larger context than the original session ever reaches."));
        }
        ReplacementTokens = replacement.EstimatedContextTokens;
        sessionProp.SetValue(context, replacement);

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
