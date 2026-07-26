using System.Reflection;

using CopilotHive.Actors;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder;

using Xunit;

namespace CopilotHive.Tests.Actors;

public class BrainActorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static async Task<T> AwaitReplyAsync<T>(TaskCompletionSource<T> reply)
    {
        await Task.WhenAny(reply.Task, Task.Delay(Timeout));
        Assert.True(reply.Task.IsCompletedSuccessfully, "Reply did not complete successfully in time.");
        return reply.Task.Result;
    }

    private static async Task AwaitSettledAsync<T>(TaskCompletionSource<T> reply)
    {
        await Task.WhenAny(reply.Task, Task.Delay(Timeout));
        Assert.True(reply.Task.IsCompleted, "Reply did not settle in time.");
    }

    /// <summary>Runs on a dedicated thread so barrier-synchronized producers cannot starve the thread pool.</summary>
    private static Task StartProducer(Action action) =>
        Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static async Task AwaitCompletionAsync(BrainActor actor)
    {
        await Task.WhenAny(actor.Completion, Task.Delay(Timeout));
        Assert.True(actor.IsCompleted, "Actor did not stop in time.");
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Chat client stub so child-actor creation never reaches the real <c>ChatClientFactory</c>
    /// (which would require provider credentials and attempt HTTP calls).
    /// </summary>
    private sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("stub", null, "stub-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stub response")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException("Streaming not used in stub client.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static BrainActor CreateActor(
        string stateDir,
        string model = "test-model",
        int maxContextTokens = 100_000,
        int maxSteps = 50,
        LlmSessionRegistry? sessionRegistry = null) =>
        new(model, maxContextTokens, stateDir, NullLogger<BrainActor>.Instance,
            chatClientFactory: _ => new StubChatClient(),
            maxSteps: maxSteps,
            sessionRegistry: sessionRegistry);

    private static GoalPipeline CreatePipeline(string goalId) =>
        new(new Goal { Id = goalId, Description = $"Description for {goalId}" });

    private static AgentSession GetMasterSession(BrainActor actor)
    {
        var field = typeof(BrainActor).GetField("_masterSession", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (AgentSession)field.GetValue(actor)!;
    }

    private static async Task<bool> ConnectAsync(BrainActor actor)
    {
        var connect = BrainActorMessages.CreateConnectMessage();
        Assert.True(actor.Tell(connect));
        return await AwaitReplyAsync(connect.Reply);
    }

    [Fact]
    public async Task Connect_ThenGetStats_ReportsConnectedState()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();

            Assert.True(await ConnectAsync(actor));

            var stats = BrainActorMessages.CreateGetStatsMessage();
            Assert.True(actor.Tell(stats));
            var result = await AwaitReplyAsync(stats.Reply);

            Assert.NotNull(result);
            Assert.True(result!.IsConnected);
            Assert.Equal(0, result.MessageCount);
            Assert.Equal("test-model", result.Model);
            Assert.Equal(100_000, result.MaxContextTokens);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ForkSession_ThenExists_ThenDelete_RemovesFile()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();
            await ConnectAsync(actor);
            var fork = BrainActorMessages.CreateForkSessionMessage("goal-1");
            actor.Tell(fork);
            Assert.True(await AwaitReplyAsync(fork.Reply));

            var exists = BrainActorMessages.CreateGoalSessionExistsMessage("goal-1");
            actor.Tell(exists);
            Assert.True(await AwaitReplyAsync(exists.Reply));

            var delete = BrainActorMessages.CreateDeleteSessionMessage("goal-1");
            actor.Tell(delete);
            Assert.True(await AwaitReplyAsync(delete.Reply));

            var existsAfter = BrainActorMessages.CreateGoalSessionExistsMessage("goal-1");
            actor.Tell(existsAfter);
            Assert.False(await AwaitReplyAsync(existsAfter.Reply));
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ForkSession_CalledTwice_IsIdempotent()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();
            await ConnectAsync(actor);
            var first = BrainActorMessages.CreateForkSessionMessage("goal-1");
            actor.Tell(first);
            Assert.True(await AwaitReplyAsync(first.Reply));

            var sessionFile = Path.Combine(dir, "brain-goal-goal-1.json");
            var contentAfterFirstFork = await File.ReadAllTextAsync(sessionFile, TestContext.Current.CancellationToken);

            // Mutate the master session so a non-idempotent second fork would produce different content.
            var merge = BrainActorMessages.CreateMergeSummaryMessage("other-goal", "master session mutation");
            actor.Tell(merge);
            Assert.True(await AwaitReplyAsync(merge.Reply));

            var second = BrainActorMessages.CreateForkSessionMessage("goal-1");
            actor.Tell(second);
            Assert.True(await AwaitReplyAsync(second.Reply));

            var contentAfterSecondFork = await File.ReadAllTextAsync(sessionFile, TestContext.Current.CancellationToken);
            Assert.Equal(contentAfterFirstFork, contentAfterSecondFork);
            Assert.DoesNotContain("master session mutation", contentAfterSecondFork, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task MergeSummary_AppendsUserAndAssistantMessages()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();
            await ConnectAsync(actor);
            // Seed a non-zero token count so the reset in MergeSummary is observable.
            GetMasterSession(actor).LastKnownContextTokens = 4242;

            var merge = BrainActorMessages.CreateMergeSummaryMessage("goal-1", "Test summary content");
            actor.Tell(merge);
            Assert.True(await AwaitReplyAsync(merge.Reply));

            var stats = BrainActorMessages.CreateGetStatsMessage();
            actor.Tell(stats);
            var result = await AwaitReplyAsync(stats.Reply);

            Assert.NotNull(result);
            Assert.Equal(2, result!.MessageCount);

            var masterSession = GetMasterSession(actor);
            var history = masterSession.MessageHistory;
            var userMessage = history[^2];
            var assistantMessage = history[^1];

            Assert.Equal(ChatRole.User, userMessage.Role);
            Assert.Contains("[Goal completed: goal-1] Summarize what was done.", userMessage.Text, StringComparison.Ordinal);
            Assert.Equal(ChatRole.Assistant, assistantMessage.Role);
            Assert.Contains("Test summary content", assistantMessage.Text, StringComparison.Ordinal);
            Assert.Equal(0, masterSession.LastKnownContextTokens);
            Assert.True(File.Exists(Path.Combine(dir, "brain-master.json")), "Master session was not persisted.");
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task Pipeline_RegisterGetDeregister_RoundTrips()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();

            var pipeline = CreatePipeline("goal-1");
            actor.Tell(new RegisterPipelineMessage("goal-1", pipeline));

            var get = BrainActorMessages.CreateGetPipelineMessage("goal-1");
            actor.Tell(get);
            Assert.Same(pipeline, await AwaitReplyAsync(get.Reply));

            actor.Tell(new DeregisterPipelineMessage("goal-1"));

            var getAfter = BrainActorMessages.CreateGetPipelineMessage("goal-1");
            actor.Tell(getAfter);
            Assert.Null(await AwaitReplyAsync(getAfter.Reply));
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task UpdateModel_ChangesModelAndMaxContextTokens()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();
            await ConnectAsync(actor);
            var update = BrainActorMessages.CreateUpdateModelMessage("new-model", 50_000);
            actor.Tell(update);
            Assert.True(await AwaitReplyAsync(update.Reply));

            var stats = BrainActorMessages.CreateGetStatsMessage();
            actor.Tell(stats);
            var result = await AwaitReplyAsync(stats.Reply);

            Assert.NotNull(result);
            Assert.Equal("new-model", result!.Model);
            Assert.Equal(50_000, result.MaxContextTokens);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ForkSession_BeforeConnect_Faults()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();

            var fork = BrainActorMessages.CreateForkSessionMessage("goal-1");
            actor.Tell(fork);
            await AwaitSettledAsync(fork.Reply);

            Assert.True(fork.Reply.Task.IsFaulted);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task GetStats_BeforeConnect_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();

            var stats = BrainActorMessages.CreateGetStatsMessage();
            actor.Tell(stats);

            Assert.Null(await AwaitReplyAsync(stats.Reply));
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task DeleteSession_BeforeConnect_Succeeds()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();

            var delete = BrainActorMessages.CreateDeleteSessionMessage("goal-1");
            actor.Tell(delete);

            Assert.True(await AwaitReplyAsync(delete.Reply));
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ConcurrentRegisterPipeline_AllPipelinesVisible()
    {
        const int producerCount = 50;
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();

            using var barrier = new Barrier(producerCount);
            var producers = new List<Task>(producerCount);
            for (var i = 0; i < producerCount; i++)
            {
                var goalId = $"goal-{i}";
                producers.Add(StartProducer(() =>
                {
                    barrier.SignalAndWait();
                    Assert.True(actor.Tell(new RegisterPipelineMessage(goalId, CreatePipeline(goalId))));
                }));
            }

            await Task.WhenAll(producers);

            for (var i = 0; i < producerCount; i++)
            {
                var get = BrainActorMessages.CreateGetPipelineMessage($"goal-{i}");
                actor.Tell(get);
                var pipeline = await AwaitReplyAsync(get.Reply);
                Assert.NotNull(pipeline);
                Assert.Equal($"goal-{i}", pipeline!.GoalId);
            }
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task DisposeAsyncBeforeStart_CancelsQueuedReplies()
    {
        var dir = CreateTempDir();
        try
        {
            var actor = CreateActor(dir);
            var stats = BrainActorMessages.CreateGetStatsMessage();
            Assert.True(actor.Tell(stats));

            await actor.DisposeAsync();

            Assert.True(actor.IsCompleted);
            Assert.True(stats.Reply.Task.IsCanceled);
            Assert.False(actor.IsStarted);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task HandlerException_FaultsReply_AndLoopContinues()
    {
        // An invalid goal id triggers an exception in the handler. The reply must be faulted,
        // and the actor loop must keep processing subsequent messages.
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();

            Assert.True(await ConnectAsync(actor));

            var fork = BrainActorMessages.CreateForkSessionMessage("../escape");
            actor.Tell(fork);
            await AwaitSettledAsync(fork.Reply);
            Assert.True(fork.Reply.Task.IsFaulted);
            Assert.False(fork.Reply.Task.IsCanceled);

            var stats = BrainActorMessages.CreateGetStatsMessage();
            actor.Tell(stats);
            var result = await AwaitReplyAsync(stats.Reply);
            Assert.NotNull(result);
            Assert.True(result!.IsConnected);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ForkSession_TraversalGoalId_Faults()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();
            await ConnectAsync(actor);
            var fork = BrainActorMessages.CreateForkSessionMessage("../../etc/passwd");
            actor.Tell(fork);
            await AwaitSettledAsync(fork.Reply);

            Assert.True(fork.Reply.Task.IsFaulted);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    /// <summary>
    /// A raw <c>..</c> goal id contains no path separators, so only the explicit <c>..</c> check
    /// in ValidateGoalPath can reject it. Removing that check makes this test fail.
    /// </summary>
    [Fact]
    public async Task ForkSession_RawDoubleDotGoalId_Faults_AndActorKeepsProcessing()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();
            await ConnectAsync(actor);
            var traversal = BrainActorMessages.CreateForkSessionMessage("..");
            actor.Tell(traversal);
            await AwaitSettledAsync(traversal.Reply);

            Assert.True(traversal.Reply.Task.IsFaulted, "Raw '..' goal id was not rejected.");
            Assert.False(File.Exists(Path.Combine(dir, "brain-goal-...json")));

            var legitimate = BrainActorMessages.CreateForkSessionMessage("normal-goal");
            actor.Tell(legitimate);
            Assert.True(await AwaitReplyAsync(legitimate.Reply));
            Assert.True(File.Exists(Path.Combine(dir, "brain-goal-normal-goal.json")));
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ConcurrentStartAndDispose_DoesNotHang()
    {
        var dir = CreateTempDir();
        try
        {
            var actor = CreateActor(dir);
            using var barrier = new Barrier(2);

            var starter = StartProducer(() =>
            {
                barrier.SignalAndWait();
                actor.Start();
            });

            var disposer = StartProducer(() =>
            {
                barrier.SignalAndWait();
                actor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            });

            var raceTask = Task.WhenAll(starter, disposer);
            await Task.WhenAny(raceTask, Task.Delay(Timeout, TestContext.Current.CancellationToken));
            Assert.True(raceTask.IsCompletedSuccessfully, "Start/Dispose race did not settle in time.");
            await AwaitCompletionAsync(actor);
            Assert.True(actor.IsCompleted);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task DisposeAsyncTwice_SecondReturnsImmediately()
    {
        var dir = CreateTempDir();
        try
        {
            var actor = CreateActor(dir);
            actor.Start();

            await actor.DisposeAsync();
            await actor.DisposeAsync();

            Assert.True(actor.IsCompleted);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    // ── Additional integration tests for coverage gaps ──

    [Fact]
    public async Task ConcurrentForkSession_SameGoalId_AllIdempotent()
    {
        const int producerCount = 20;
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();
            await ConnectAsync(actor);
            using var barrier = new Barrier(producerCount);
            var forkMsgs = new ForkSessionMessage[producerCount];
            var producers = new List<Task>(producerCount);
            for (var i = 0; i < producerCount; i++)
            {
                var idx = i;
                producers.Add(StartProducer(() =>
                {
                    barrier.SignalAndWait();
                    forkMsgs[idx] = BrainActorMessages.CreateForkSessionMessage("goal-shared");
                    actor.Tell(forkMsgs[idx]);
                }));
            }

            await Task.WhenAll(producers);

            // Every fork must have replied true (idempotent — first creates, rest are no-ops)
            foreach (var msg in forkMsgs)
            {
                Assert.True(await AwaitReplyAsync(msg.Reply), "Concurrent fork did not reply true.");
            }

            // Exactly one file should exist on disk
            var exists = BrainActorMessages.CreateGoalSessionExistsMessage("goal-shared");
            actor.Tell(exists);
            Assert.True(await AwaitReplyAsync(exists.Reply));
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task DeleteSession_NonExistentGoalId_RepliesTrue()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();

            // No prior ForkSession — the goal ID has no file and no dict entry
            var delete = BrainActorMessages.CreateDeleteSessionMessage("never-existed");
            actor.Tell(delete);

            Assert.True(await AwaitReplyAsync(delete.Reply));
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Theory]
    [InlineData("foo/../bar")]
    [InlineData("..")]
    [InlineData("a/../../b")]
    [InlineData("goal%2e%2e")]
    [InlineData("....//")]
    public async Task GoalSessionExists_TraversalAttempt_RepliesFalseOrFaults(string goalId)
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();

            var exists = BrainActorMessages.CreateGoalSessionExistsMessage(goalId);
            actor.Tell(exists);
            await AwaitSettledAsync(exists.Reply);

            // Path traversal should either fault (invalid characters) or reply false (no file).
            // It must never reply true (which would mean a file was found outside the state dir).
            if (exists.Reply.Task.IsCompletedSuccessfully)
            {
                var result = await exists.Reply.Task;
                Assert.False(result,
                    "GoalSessionExists must not return true for a traversal attempt.");
            }
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    // -- RegisterExistingSessionMessage / InjectOrchestratorInstructionsMessage tests --

    private static async Task<BrainActor> CreateConnectedActorAsync(string dir)
    {
        var actor = CreateActor(dir);
        actor.Start();
        var connect = BrainActorMessages.CreateConnectMessage();
        Assert.True(actor.Tell(connect));
        await AwaitReplyAsync(connect.Reply);
        return actor;
    }

    [Fact]
    public async Task RegisterExistingSession_FileExists_TracksSession()
    {
        var dir = CreateTempDir();
        try
        {
            var actor = await CreateConnectedActorAsync(dir);
            await using (actor)
            {
                // Pre-create a session file carrying a marker that a fork of the master would not contain.
                var path = Path.Combine(dir, "brain-goal-g1.json");
                var preExisting = AgentSession.Create("brain-goal-g1");
                preExisting.MessageHistory.Add(new ChatMessage(ChatRole.User, "PRE_EXISTING_MARKER"));
                await preExisting.SaveAsync(path, TestContext.Current.CancellationToken);

                var register = BrainActorMessages.CreateRegisterExistingSessionMessage("g1");
                Assert.True(actor.Tell(register));
                Assert.True(await AwaitReplyAsync(register.Reply));

                // The pre-existing file must be adopted, not overwritten by a fresh fork.
                var afterRegister = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
                Assert.Contains("PRE_EXISTING_MARKER", afterRegister, StringComparison.Ordinal);

                // Mutate the master so a fork would now produce clearly different content.
                var merge = BrainActorMessages.CreateMergeSummaryMessage("other-goal", "MASTER_MUTATION");
                Assert.True(actor.Tell(merge));
                Assert.True(await AwaitReplyAsync(merge.Reply));

                // ForkSession short-circuits only when the goal is present in the tracking dictionary.
                // If RegisterExistingSession had not tracked it, this fork would overwrite the file.
                var fork = BrainActorMessages.CreateForkSessionMessage("g1");
                Assert.True(actor.Tell(fork));
                Assert.True(await AwaitReplyAsync(fork.Reply));

                var afterFork = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
                Assert.Equal(afterRegister, afterFork);
                Assert.Contains("PRE_EXISTING_MARKER", afterFork, StringComparison.Ordinal);
                Assert.DoesNotContain("MASTER_MUTATION", afterFork, StringComparison.Ordinal);

                // The actor owns the tracked path and can delete it.
                var delete = BrainActorMessages.CreateDeleteSessionMessage("g1");
                Assert.True(actor.Tell(delete));
                Assert.True(await AwaitReplyAsync(delete.Reply));
                Assert.False(File.Exists(path));

                var exists = BrainActorMessages.CreateGoalSessionExistsMessage("g1");
                Assert.True(actor.Tell(exists));
                Assert.False(await AwaitReplyAsync(exists.Reply));
            }
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task RegisterExistingSession_FileMissing_ForksAndSaves()
    {
        var dir = CreateTempDir();
        try
        {
            var actor = await CreateConnectedActorAsync(dir);
            await using (actor)
            {
                // Seed the master session so a genuine fork is distinguishable from an empty file.
                var merge = BrainActorMessages.CreateMergeSummaryMessage("seed-goal", "MASTER_FORK_MARKER");
                Assert.True(actor.Tell(merge));
                Assert.True(await AwaitReplyAsync(merge.Reply));

                var path = Path.Combine(dir, "brain-goal-g2.json");
                Assert.False(File.Exists(path));

                var register = BrainActorMessages.CreateRegisterExistingSessionMessage("g2");
                Assert.True(actor.Tell(register));
                Assert.True(await AwaitReplyAsync(register.Reply));

                Assert.True(File.Exists(path));

                // The saved file must be a real fork of the master, not an empty placeholder.
                var forked = await AgentSession.LoadAsync(path, TestContext.Current.CancellationToken);
                Assert.NotEmpty(forked.MessageHistory);
                Assert.Contains(forked.MessageHistory, m => m.Text.Contains("MASTER_FORK_MARKER", StringComparison.Ordinal));

                var exists = BrainActorMessages.CreateGoalSessionExistsMessage("g2");
                Assert.True(actor.Tell(exists));
                Assert.True(await AwaitReplyAsync(exists.Reply));

                // Tracked: a subsequent fork must not rewrite the file.
                var contentAfterRegister = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
                var mutate = BrainActorMessages.CreateMergeSummaryMessage("other-goal", "LATER_MUTATION");
                Assert.True(actor.Tell(mutate));
                Assert.True(await AwaitReplyAsync(mutate.Reply));

                var fork = BrainActorMessages.CreateForkSessionMessage("g2");
                Assert.True(actor.Tell(fork));
                Assert.True(await AwaitReplyAsync(fork.Reply));

                Assert.Equal(contentAfterRegister, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            }
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task RegisterExistingSession_AlreadyRegistered_RepliesTrue()
    {
        var dir = CreateTempDir();
        try
        {
            var actor = await CreateConnectedActorAsync(dir);
            await using (actor)
            {
                var first = BrainActorMessages.CreateRegisterExistingSessionMessage("g3");
                Assert.True(actor.Tell(first));
                Assert.True(await AwaitReplyAsync(first.Reply));

                var path = Path.Combine(dir, "brain-goal-g3.json");
                Assert.True(File.Exists(path));

                // Mutate the master so a non-idempotent second registration would rewrite the file.
                var merge = BrainActorMessages.CreateMergeSummaryMessage("other-goal", "SECOND_PASS_MUTATION");
                Assert.True(actor.Tell(merge));
                Assert.True(await AwaitReplyAsync(merge.Reply));

                // Remove the file behind the actor's back. The already-tracked guard must short-circuit
                // on the dictionary alone; without it the handler would observe the missing file and
                // re-fork, recreating it.
                File.Delete(path);

                var second = BrainActorMessages.CreateRegisterExistingSessionMessage("g3");
                Assert.True(actor.Tell(second));
                Assert.True(await AwaitReplyAsync(second.Reply));

                Assert.False(File.Exists(path));
            }
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task RegisterExistingSession_NotConnected_ReplyFaulted()
    {
        var dir = CreateTempDir();
        try
        {
            var actor = CreateActor(dir);
            actor.Start();
            await using (actor)
            {
                var register = BrainActorMessages.CreateRegisterExistingSessionMessage("g4");
                Assert.True(actor.Tell(register));
                await AwaitSettledAsync(register.Reply);
                Assert.True(register.Reply.Task.IsFaulted);
            }
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task RegisterExistingSession_InvalidGoalId_ReplyFaulted_LoopContinues()
    {
        var dir = CreateTempDir();
        try
        {
            var actor = await CreateConnectedActorAsync(dir);
            await using (actor)
            {
                var register = BrainActorMessages.CreateRegisterExistingSessionMessage("../escape");
                Assert.True(actor.Tell(register));
                await AwaitSettledAsync(register.Reply);
                Assert.True(register.Reply.Task.IsFaulted);

                // The loop must survive the fault.
                var stats = BrainActorMessages.CreateGetStatsMessage();
                Assert.True(actor.Tell(stats));
                Assert.NotNull(await AwaitReplyAsync(stats.Reply));
            }
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task InjectOrchestratorInstructions_StoresAndRepliesTrue()
    {
        var dir = CreateTempDir();
        try
        {
            var actor = await CreateConnectedActorAsync(dir);
            await using (actor)
            {
                var msg = BrainActorMessages.CreateInjectOrchestratorInstructionsMessage("test instructions");
                Assert.True(actor.Tell(msg));
                Assert.True(await AwaitReplyAsync(msg.Reply));
            }
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task InjectOrchestratorInstructions_EmptyString_RepliesTrue()
    {
        var dir = CreateTempDir();
        try
        {
            var actor = await CreateConnectedActorAsync(dir);
            await using (actor)
            {
                var seed = BrainActorMessages.CreateInjectOrchestratorInstructionsMessage("seed");
                Assert.True(actor.Tell(seed));
                Assert.True(await AwaitReplyAsync(seed.Reply));

                var msg = BrainActorMessages.CreateInjectOrchestratorInstructionsMessage(string.Empty);
                Assert.True(actor.Tell(msg));
                Assert.True(await AwaitReplyAsync(msg.Reply));
            }
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task CancelDrain_UnstartedActor_NewMessagesCanceled()
    {
        var dir = CreateTempDir();
        try
        {
            var actor = CreateActor(dir);
            var register = BrainActorMessages.CreateRegisterExistingSessionMessage("g5");
            var inject = BrainActorMessages.CreateInjectOrchestratorInstructionsMessage("x");
            Assert.True(actor.Tell(register));
            Assert.True(actor.Tell(inject));

            await actor.DisposeAsync();

            Assert.True(register.Reply.Task.IsCanceled);
            Assert.True(inject.Reply.Task.IsCanceled);
        }
        finally { DeleteTempPath(dir); }
    }

    // ── Phase 3c-3b: BrainActor authoritative capabilities ──

    [Fact]
    public async Task ConnectAsync_SavesBrainMasterFile()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir);
            actor.Start();

            Assert.True(await ConnectAsync(actor));
            Assert.True(File.Exists(Path.Combine(dir, "brain-master.json")), "brain-master.json should exist after connect.");
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task DeleteSessionAsync_UnregistersBrainGoalSession()
    {
        var dir = CreateTempDir();
        var registry = new LlmSessionRegistry();
        var goalId = "goal-9";
        try
        {
            await using var actor = CreateActor(dir, sessionRegistry: registry);
            actor.Start();
            await ConnectAsync(actor);
            // Register the goal session so the registry is seeded.
            registry.RegisterOrUpdate(new LlmSessionInfo
            {
                SessionId = $"brain-goal-{goalId}",
                SessionType = LlmSessionType.BrainGoal,
                GoalId = goalId,
                Model = "test-model",
                Status = "idle",
            });
            Assert.Contains(registry.GetAll(), s => s.SessionId == $"brain-goal-{goalId}");

            var delete = BrainActorMessages.CreateDeleteSessionMessage(goalId);
            Assert.True(actor.Tell(delete));
            Assert.True(await AwaitReplyAsync(delete.Reply));

            Assert.DoesNotContain(registry.GetAll(), s => s.SessionId == $"brain-goal-{goalId}");
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task OnShutdownAsync_UnregistersAllChildSessions()
    {
        var dir = CreateTempDir();
        var registry = new LlmSessionRegistry();
        const string goalId1 = "goal-10a";
        const string goalId2 = "goal-10b";
        try
        {
            var actor = CreateActor(dir, sessionRegistry: registry);
            actor.Start();
            await ConnectAsync(actor);
            await ForkSessionAsync(actor, goalId1);
            await ForkSessionAsync(actor, goalId2);

            // Seed registry entries for the two children. This gives OnShutdownAsync something
            // to unregister, so the test is non-vacuous: deleting the unregister loop would make
            // the post-dispose assertions fail.
            registry.RegisterOrUpdate(new LlmSessionInfo
            {
                SessionId = $"brain-goal-{goalId1}",
                SessionType = LlmSessionType.BrainGoal,
                GoalId = goalId1,
                Model = "test-model",
                Status = "active",
                CurrentTokens = 0,
                MaxTokens = 100_000,
            });
            registry.RegisterOrUpdate(new LlmSessionInfo
            {
                SessionId = $"brain-goal-{goalId2}",
                SessionType = LlmSessionType.BrainGoal,
                GoalId = goalId2,
                Model = "test-model",
                Status = "active",
                CurrentTokens = 0,
                MaxTokens = 100_000,
            });

            // Verify the seeded entries exist before disposal.
            Assert.Contains(registry.GetAll(), s => s.SessionId == $"brain-goal-{goalId1}");
            Assert.Contains(registry.GetAll(), s => s.SessionId == $"brain-goal-{goalId2}");

            // Dispose triggers OnShutdownAsync. Wait for completion so the unregister loop runs.
            await actor.DisposeAsync();
            await Task.WhenAny(actor.Completion, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(actor.IsCompleted, "Actor loop should have completed after dispose.");

            // OnShutdownAsync unregisters every captured child.
            Assert.DoesNotContain(registry.GetAll(), s => s.SessionId == $"brain-goal-{goalId1}");
            Assert.DoesNotContain(registry.GetAll(), s => s.SessionId == $"brain-goal-{goalId2}");
        }
        finally { DeleteTempPath(dir); }
    }

    private static async Task ForkSessionAsync(BrainActor actor, string goalId)
    {
        var fork = BrainActorMessages.CreateForkSessionMessage(goalId);
        Assert.True(actor.Tell(fork));
        await AwaitReplyAsync(fork.Reply);
    }

    [Fact]
    public async Task MergeSummaryAsync_UpdatesBrainMasterRegistry()
    {
        var dir = CreateTempDir();
        var registry = new LlmSessionRegistry();
        const string goalId = "goal-11";
        try
        {
            await using var actor = CreateActor(dir, model: "registry-test-model", sessionRegistry: registry);
            actor.Start();
            await ConnectAsync(actor);
            var merge = BrainActorMessages.CreateMergeSummaryMessage(goalId, "Registry update test summary.");
            Assert.True(actor.Tell(merge));
            Assert.True(await AwaitReplyAsync(merge.Reply));

            var master = Assert.Single(registry.GetAll(), s => s.SessionId == "brain-master");
            Assert.Equal(LlmSessionType.Brain, master.SessionType);
            Assert.Equal("registry-test-model", master.Model);
            Assert.Equal("idle", master.Status);
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task GetStats_ReturnsCompleteStats()
    {
        var dir = CreateTempDir();
        const int maxSteps = 77;
        try
        {
            await using var actor = CreateActor(dir, maxContextTokens: 100_000, maxSteps: maxSteps);
            actor.Start();
            await ConnectAsync(actor);

            // Seed distinguishable, non-zero values on the master session so every field mapping
            // in CreateStats is exercised. Hard-coded zeros or swapped mappings will fail.
            var masterSession = GetMasterSession(actor);
            masterSession.LastKnownContextTokens = 5000;
            masterSession.InputTokensUsed = 12345;
            masterSession.OutputTokensUsed = 67890;

            var stats = BrainActorMessages.CreateGetStatsMessage();
            Assert.True(actor.Tell(stats));
            var result = await AwaitReplyAsync(stats.Reply);

            Assert.NotNull(result);
            Assert.Equal("test-model", result!.Model);
            Assert.Equal(0, result.MessageCount);
            Assert.Equal(5000, result.ContextTokens);
            Assert.Equal(100_000, result.MaxContextTokens);
            Assert.True(result.IsConnected);
            Assert.Equal(12345L, result.CumulativeInputTokens);
            Assert.Equal(67890L, result.CumulativeOutputTokens);
            Assert.Equal(maxSteps, result.MaxSteps);
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task CreateChildActor_ReceivesNonNullRegistry()
    {
        var dir = CreateTempDir();
        var registry = new LlmSessionRegistry();
        const string goalId = "goal-12";
        try
        {
            await using var actor = CreateActor(dir, sessionRegistry: registry);
            actor.Start();
            await ConnectAsync(actor);
            await ForkSessionAsync(actor, goalId);

            var childActorsField = typeof(BrainActor).GetField("_childActors", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var children = (Dictionary<string, GoalBrainActor>)childActorsField.GetValue(actor)!;
            Assert.True(children.TryGetValue(goalId, out var child));

            var childRegistryField = typeof(GoalBrainActor).GetField("_sessionRegistry", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var childRegistry = childRegistryField.GetValue(child);
            Assert.NotNull(childRegistry);
            Assert.Same(registry, childRegistry);
        }
        finally { DeleteTempPath(dir); }
    }
}
