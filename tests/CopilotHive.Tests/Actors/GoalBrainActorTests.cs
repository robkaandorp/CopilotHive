using System.Reflection;
using System.Text.RegularExpressions;

using CopilotHive.Actors;
using CopilotHive.Dashboard;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder;

using Xunit;

namespace CopilotHive.Tests.Actors;

public class GoalBrainActorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Chat client that replays a scripted sequence of responses and tracks disposal.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly Func<IList<ChatMessage>, ChatResponse> _responder;
        private int _callCount;

        internal FakeChatClient(Func<IList<ChatMessage>, ChatResponse> responder) => _responder = responder;

        internal static FakeChatClient Text(string text) =>
            new(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        internal bool WasDisposed => DisposeCallCount > 0;

        internal int DisposeCallCount { get; private set; }

        internal int CallCount => _callCount;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responder([.. messages]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => DisposeCallCount++;
    }

    /// <summary>Chat client that blocks until released, so disposal must be deferred.</summary>
    private sealed class BlockingChatClient : IChatClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _release.TrySetResult();

        internal bool WasDisposed { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "released"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => WasDisposed = true;
    }

    /// <summary>Chat client that always throws from the LLM call.</summary>
    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("boom");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Tracks a disposal attempt and then throws, exercising independent cleanup.</summary>
    private sealed class DisposeThrowingChatClient : IChatClient
    {
        internal int DisposeCallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            DisposeCallCount++;
            throw new InvalidOperationException("dispose failed");
        }
    }

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

    private static AgentOptions CreateBaseOptions(string workDir) => new()
    {
        WorkDirectory = workDir,
        MaxSteps = 5,
        EnableBash = false,
        EnableFileOps = false,
        EnableFileWrites = false,
        EnableSkills = false,
        AutoLoadWorkspaceInstructions = false,
        SystemPrompt = "You are the Brain.",
    };

    private static GoalBrainActor CreateActor(
        string stateDir,
        IChatClient chatClient,
        string goalId = "goal-1",
        bool ownsChatClient = true,
        IChatClient? compactionClient = null,
        AgentOptions? baseOptions = null,
        AgentSession? session = null,
        LlmSessionRegistry? sessionRegistry = null,
        CopilotHive.Goals.IGoalStore? goalStore = null,
        CopilotHive.Knowledge.KnowledgeGraph? knowledgeGraph = null,
        Func<IBrainMessage, bool>? parentTell = null) =>
        new(goalId,
            session ?? AgentSession.Create($"brain-goal-{goalId}"),
            chatClient,
            ownsChatClient,
            compactionClient,
            baseOptions ?? CreateBaseOptions(stateDir),
            "test-model",
            100_000,
            stateDir,
            sessionRegistry,
            NullLogger<GoalBrainActor>.Instance,
            goalStore,
            knowledgeGraph,
            parentTell);

    private static AgentOptions GetConfiguredOptions(GoalBrainActor actor) =>
        (AgentOptions)typeof(CodingAgent)
            .GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(actor.CodingAgent)!;

    private static string ReadProductionSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate production source '{relativePath}'.");
    }

    private static ChatResponse ToolCallResponse(string name, IDictionary<string, object?> args) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]));

    [Fact]
    public void ActorAndMessageTypes_AreInternalWithRequiredFactorySignatures()
    {
        Type[] internalTypes =
        [
            typeof(GoalBrainActor),
            typeof(IGoalBrainMessage),
            typeof(ExecutePromptMessage),
            typeof(InjectNoteMessage),
            typeof(GetGoalStateMessage),
            typeof(GoalBrainExecutionResult),
            typeof(GoalBrainToolCallResult),
            typeof(EscalateToolResult),
            typeof(PlanToolResult),
            typeof(GoalBrainActorState),
            typeof(GoalBrainActorMessages),
        ];

        Assert.All(internalTypes, type => Assert.True(type.IsNotPublic, $"{type.Name} must be internal."));
        Assert.Contains(typeof(IAsyncDisposable), typeof(GoalBrainActor).GetInterfaces());

        var execute = typeof(GoalBrainActorMessages).GetMethod(
            "CreateExecutePromptMessage", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(execute);
        Assert.Equal(typeof(ExecutePromptMessage), execute.ReturnType);
        Assert.Equal([typeof(string), typeof(CancellationToken)],
            execute.GetParameters().Select(parameter => parameter.ParameterType));

        var inject = typeof(GoalBrainActorMessages).GetMethod(
            "CreateInjectNoteMessage", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(inject);
        Assert.Equal(typeof(InjectNoteMessage), inject.ReturnType);
        Assert.Equal([typeof(string)], inject.GetParameters().Select(parameter => parameter.ParameterType));

        var state = typeof(GoalBrainActorMessages).GetMethod(
            "CreateGetGoalStateMessage", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(state);
        Assert.Equal(typeof(GetGoalStateMessage), state.ReturnType);
        Assert.Empty(state.GetParameters());
    }

    [Fact]
    public void ActorSource_ConfiguresExecutePromptTimeoutExactly()
    {
        var source = ReadProductionSource(Path.Combine("src", "CopilotHive", "Actors", "GoalBrainActor.cs"));

        Assert.Matches(new Regex(
            @"linkedCts\.CancelAfter\s*\(\s*TimeSpan\.FromMinutes\s*\(\s*Constants\.TaskTimeoutMinutes\s*\)\s*\)",
            RegexOptions.CultureInvariant), source);
    }

    [Fact]
    public void MessageFactories_PreservePayloadsAndCreateIndependentReplies()
    {
        using var cts = new CancellationTokenSource();
        var first = GoalBrainActorMessages.CreateExecutePromptMessage("prompt", cts.Token);
        var second = GoalBrainActorMessages.CreateExecutePromptMessage("other", CancellationToken.None);
        var note = GoalBrainActorMessages.CreateInjectNoteMessage("note");
        var state = GoalBrainActorMessages.CreateGetGoalStateMessage();

        Assert.Equal("prompt", first.Prompt);
        Assert.Equal(cts.Token, first.Ct);
        Assert.NotSame(first.Reply, second.Reply);
        Assert.Equal("note", note.Note);
        Assert.NotNull(note.Reply);
        Assert.NotNull(state.Reply);
    }

    [Fact]
    public async Task Tell_AfterMailboxCompleted_ReturnsFalse()
    {
        var dir = CreateTempDir();
        try
        {
            var actor = CreateActor(dir, FakeChatClient.Text("unused"));
            await actor.DisposeAsync();

            Assert.False(actor.Tell(GoalBrainActorMessages.CreateGetGoalStateMessage()));
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task Constructor_CopiesEveryBaseOptionAndOverridesOnlyActorInjections()
    {
        var dir = CreateTempDir();
        try
        {
            Func<string, string> shellArgs = command => $"--command={command}";
            Action onCompacting = () => { };
            Action<CompactionResult> onCompacted = _ => { };
            var logger = NullLogger.Instance;
            var originalTool = AIFunctionFactory.Create(() => "original", "original_tool");
            var compaction = FakeChatClient.Text("compact");
            var options = new AgentOptions
            {
                WorkDirectory = dir,
                MaxSteps = 17,
                EnableBash = true,
                BashShellPath = "/bin/custom-shell",
                BashShellArgsFormat = shellArgs,
                EnableFileOps = false,
                EnableFileWrites = false,
                EnableSkills = false,
                SystemPrompt = "custom-system",
                CustomInstructions = "custom-instructions",
                AutoLoadWorkspaceInstructions = false,
                CustomTools = [originalTool],
                MaxContextTokens = 41,
                CompactionThreshold = 0.73,
                CompactionRetainRecent = 7,
                EnableAutoCompaction = false,
                OnCompacting = onCompacting,
                OnCompacted = onCompacted,
                Logger = logger,
                ReasoningEffort = ReasoningEffort.ExtraHigh,
                ShowToolCallsInStream = true,
                CompactionClient = null,
                CompactionMaxTokens = 321,
            };

            await using var actor = CreateActor(dir, FakeChatClient.Text("unused"),
                compactionClient: compaction, baseOptions: options);
            var configured = GetConfiguredOptions(actor);

            foreach (var property in typeof(AgentOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.Name is nameof(AgentOptions.CustomTools)
                    or nameof(AgentOptions.CompactionClient)
                    or nameof(AgentOptions.MaxContextTokens))
                {
                    continue;
                }

                Assert.Equal(property.GetValue(options), property.GetValue(configured));
            }

            Assert.Equal(100_000, configured.MaxContextTokens);
            Assert.Same(compaction, configured.CompactionClient);
            Assert.Equal(
                ["escalate_to_composer", "report_iteration_plan", "get_goal", "search_knowledge",
                 "read_document", "traverse_graph", "get_current_time"],
                configured.CustomTools.Select(tool => tool.Name));
            Assert.DoesNotContain(configured.CustomTools, tool => tool.Name == "original_tool");
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task InjectedTools_HaveRequiredPrototypeParameterNamesAndTypes()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir, FakeChatClient.Text("unused"));
            var tools = GetConfiguredOptions(actor).CustomTools.Cast<AIFunction>().ToDictionary(tool => tool.Name);

            var escalateParameters = tools["escalate_to_composer"].UnderlyingMethod!.GetParameters();
            Assert.Equal(["question", "reason"], escalateParameters.Select(parameter => parameter.Name));
            Assert.All(escalateParameters, parameter => Assert.Equal(typeof(string), parameter.ParameterType));

            var planParameters = tools["report_iteration_plan"].UnderlyingMethod!.GetParameters();
            Assert.Equal(["phases", "phase_instructions", "reason", "model_tiers"],
                planParameters.Select(parameter => parameter.Name));
            Assert.Equal([typeof(string[]), typeof(string), typeof(string), typeof(string)],
                planParameters.Select(parameter => parameter.ParameterType));
            Assert.Equal(NullabilityState.Nullable,
                new NullabilityInfoContext().Create(planParameters[3]).ReadState);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ExecutePrompt_WithFakeClient_ReturnsTextAndSavesSession()
    {
        var dir = CreateTempDir();
        try
        {
            var client = FakeChatClient.Text("hello from brain");
            await using var actor = CreateActor(dir, client);
            actor.Start();

            var msg = GoalBrainActorMessages.CreateExecutePromptMessage("hi", CancellationToken.None);
            Assert.True(actor.Tell(msg));
            var result = await AwaitReplyAsync(msg.Reply);

            Assert.Contains("hello from brain", result.Text);
            Assert.Null(result.ToolCall);
            Assert.True(File.Exists(Path.Combine(dir, "brain-goal-goal-1.json")));
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ExecutePrompt_UsesStableSessionReferenceAndRegistersActiveThenIdle()
    {
        var dir = CreateTempDir();
        try
        {
            var client = new BlockingChatClient();
            var registry = new LlmSessionRegistry();
            var originalSession = AgentSession.Create("original-session");
            var replacementSession = AgentSession.Create("replacement-session");
            replacementSession.MessageHistory.Add(new ChatMessage(ChatRole.User, "replacement-only"));

            await using var actor = CreateActor(dir, client, session: originalSession, sessionRegistry: registry);
            actor.Start();

            var msg = GoalBrainActorMessages.CreateExecutePromptMessage("stable prompt", CancellationToken.None);
            Assert.True(actor.Tell(msg));
            await client.Entered.WaitAsync(Timeout, CancellationToken.None);

            var active = Assert.Single(registry.GetAll());
            Assert.Equal("brain-goal-goal-1", active.SessionId);
            Assert.Equal(LlmSessionType.BrainGoal, active.SessionType);
            Assert.Equal("goal-1", active.GoalId);
            Assert.Equal("test-model", active.Model);
            Assert.Equal("active", active.Status);
            Assert.Equal(100_000, active.MaxTokens);

            actor.Session = replacementSession;
            client.Release();
            var result = await AwaitReplyAsync(msg.Reply);

            Assert.Contains("released", result.Text);
            Assert.Contains(originalSession.MessageHistory, message => message.Text == "stable prompt");
            Assert.DoesNotContain(replacementSession.MessageHistory, message => message.Text == "stable prompt");
            var idle = Assert.Single(registry.GetAll());
            Assert.Equal("idle", idle.Status);
            Assert.Equal(originalSession.EstimatedContextTokens, idle.CurrentTokens);
            Assert.True(File.Exists(Path.Combine(dir, "brain-goal-goal-1.json")));
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ExecutePrompt_ReportIterationPlanTool_RecordsPlanResult()
    {
        var dir = CreateTempDir();
        try
        {
            var calls = 0;
            var client = new FakeChatClient(_ => Interlocked.Increment(ref calls) == 1
                ? ToolCallResponse("report_iteration_plan", new Dictionary<string, object?>
                {
                    ["phases"] = new[] { "coding", "testing" },
                    ["phase_instructions"] = "do work",
                    ["reason"] = "because",
                    ["model_tiers"] = "{\"coding\":\"high\"}",
                })
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

            await using var actor = CreateActor(dir, client);
            actor.Start();

            var msg = GoalBrainActorMessages.CreateExecutePromptMessage("plan it", CancellationToken.None);
            Assert.True(actor.Tell(msg));
            var result = await AwaitReplyAsync(msg.Reply);

            var plan = Assert.IsType<PlanToolResult>(result.ToolCall);
            Assert.Equal(["coding", "testing"], plan.Phases);
            Assert.Equal("do work", plan.PhaseInstructions);
            Assert.Equal("because", plan.Reason);
            Assert.Equal("{\"coding\":\"high\"}", plan.ModelTiers);
            Assert.Equal("report_iteration_plan", plan.ToolName);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ExecutePrompt_ReportIterationPlanAcceptsEmptyPrototypeValuesWithoutValidation()
    {
        var dir = CreateTempDir();
        try
        {
            var calls = 0;
            var client = new FakeChatClient(_ => Interlocked.Increment(ref calls) == 1
                ? ToolCallResponse("report_iteration_plan", new Dictionary<string, object?>
                {
                    ["phases"] = Array.Empty<string>(),
                    ["phase_instructions"] = string.Empty,
                    ["reason"] = string.Empty,
                    ["model_tiers"] = null,
                })
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

            await using var actor = CreateActor(dir, client);
            actor.Start();

            var msg = GoalBrainActorMessages.CreateExecutePromptMessage("plan it", CancellationToken.None);
            Assert.True(actor.Tell(msg));
            var result = await AwaitReplyAsync(msg.Reply);

            var plan = Assert.IsType<PlanToolResult>(result.ToolCall);
            Assert.Empty(plan.Phases);
            Assert.Equal(string.Empty, plan.PhaseInstructions);
            Assert.Equal(string.Empty, plan.Reason);
            Assert.Null(plan.ModelTiers);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ExecutePrompt_EscalateTool_RecordsEscalationResult()
    {
        var dir = CreateTempDir();
        try
        {
            var calls = 0;
            var client = new FakeChatClient(_ => Interlocked.Increment(ref calls) == 1
                ? ToolCallResponse("escalate_to_composer", new Dictionary<string, object?>
                {
                    ["question"] = "which database?",
                    ["reason"] = "not in codebase",
                })
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "escalated")));

            await using var actor = CreateActor(dir, client);
            actor.Start();

            var msg = GoalBrainActorMessages.CreateExecutePromptMessage("question", CancellationToken.None);
            Assert.True(actor.Tell(msg));
            var result = await AwaitReplyAsync(msg.Reply);

            var escalate = Assert.IsType<EscalateToolResult>(result.ToolCall);
            Assert.Equal("which database?", escalate.Question);
            Assert.Equal("not in codebase", escalate.Reason);
            Assert.Equal("escalate_to_composer", escalate.ToolName);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task InjectNote_AppendsUserMessageAndResetsKnownTokens()
    {
        var dir = CreateTempDir();
        try
        {
            var session = AgentSession.Create("brain-goal-goal-1");
            session.LastKnownContextTokens = 4321;
            var before = session.MessageHistory.Count;

            await using var actor = CreateActor(dir, FakeChatClient.Text("x"), session: session);
            actor.Start();

            var msg = GoalBrainActorMessages.CreateInjectNoteMessage("a note");
            Assert.True(actor.Tell(msg));
            Assert.True(await AwaitReplyAsync(msg.Reply));

            Assert.Equal(before + 1, session.MessageHistory.Count);
            var last = session.MessageHistory[^1];
            Assert.Equal(ChatRole.User, last.Role);
            Assert.Equal("a note", last.Text);
            Assert.Equal(0, session.LastKnownContextTokens);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task GetGoalState_ReturnsCurrentSnapshot()
    {
        var dir = CreateTempDir();
        try
        {
            var session = AgentSession.Create("brain-goal-goal-7");
            session.MessageHistory.Add(new ChatMessage(ChatRole.User, "hello"));

            await using var actor = CreateActor(dir, FakeChatClient.Text("x"), goalId: "goal-7", session: session);
            actor.Start();

            var msg = GoalBrainActorMessages.CreateGetGoalStateMessage();
            Assert.True(actor.Tell(msg));
            var state = await AwaitReplyAsync(msg.Reply);

            Assert.Equal("goal-7", state.GoalId);
            Assert.Equal(session.MessageHistory.Count, state.MessageCount);
            Assert.Equal(session.EstimatedContextTokens, state.EstimatedTokens);
            Assert.Equal("test-model", state.Model);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ExecutePrompt_WhenClientThrows_FaultsReplyAndLoopContinues()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir, new ThrowingChatClient());
            actor.Start();

            var exec = GoalBrainActorMessages.CreateExecutePromptMessage("hi", CancellationToken.None);
            Assert.True(actor.Tell(exec));
            await AwaitSettledAsync(exec.Reply);
            Assert.True(exec.Reply.Task.IsFaulted);

            var state = GoalBrainActorMessages.CreateGetGoalStateMessage();
            Assert.True(actor.Tell(state));
            var snapshot = await AwaitReplyAsync(state.Reply);
            Assert.Equal("goal-1", snapshot.GoalId);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ExecutePrompt_WhenAgentReturnsError_RepliesWithErrorText()
    {
        var dir = CreateTempDir();
        try
        {
            var client = new FakeChatClient(_ => throw new InvalidOperationException("agent-level failure"));
            await using var actor = CreateActor(dir, client);
            actor.Start();

            var exec = GoalBrainActorMessages.CreateExecutePromptMessage("hi", CancellationToken.None);
            Assert.True(actor.Tell(exec));
            var result = await AwaitReplyAsync(exec.Reply);

            Assert.Contains("agent-level failure", result.Text, StringComparison.Ordinal);
            Assert.Null(result.ToolCall);
            Assert.False(File.Exists(Path.Combine(dir, "brain-goal-goal-1.json")));
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task ExecutePrompt_WhenHttpExceptionPropagates_RegistryReturnsToIdleInFinally()
    {
        var dir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            await using var actor = CreateActor(dir, new ThrowingChatClient(), sessionRegistry: registry);
            actor.Start();

            var exec = GoalBrainActorMessages.CreateExecutePromptMessage("hi", CancellationToken.None);
            Assert.True(actor.Tell(exec));
            await AwaitSettledAsync(exec.Reply);

            Assert.True(exec.Reply.Task.IsFaulted);
            var session = Assert.Single(registry.GetAll());
            Assert.Equal("idle", session.Status);
            Assert.Equal("goal-1", session.GoalId);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task TwoActors_RunConcurrently_BothComplete()
    {
        var dir = CreateTempDir();
        try
        {
            await using var a = CreateActor(dir, FakeChatClient.Text("one"), goalId: "goal-a");
            await using var b = CreateActor(dir, FakeChatClient.Text("two"), goalId: "goal-b");
            a.Start();
            b.Start();

            var ma = GoalBrainActorMessages.CreateExecutePromptMessage("p", CancellationToken.None);
            var mb = GoalBrainActorMessages.CreateExecutePromptMessage("p", CancellationToken.None);
            Assert.True(a.Tell(ma));
            Assert.True(b.Tell(mb));

            var ra = await AwaitReplyAsync(ma.Reply);
            var rb = await AwaitReplyAsync(mb.Reply);
            Assert.Contains("one", ra.Text);
            Assert.Contains("two", rb.Text);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task DisposeAsync_AfterExecution_CompletesAndDisposesChatClient()
    {
        var dir = CreateTempDir();
        try
        {
            var client = FakeChatClient.Text("hi");
            var actor = CreateActor(dir, client);
            actor.Start();

            var msg = GoalBrainActorMessages.CreateExecutePromptMessage("p", CancellationToken.None);
            Assert.True(actor.Tell(msg));
            await AwaitReplyAsync(msg.Reply);

            await actor.DisposeAsync();

            Assert.True(actor.IsCompleted);
            Assert.True(actor.Completion.IsCompletedSuccessfully);
            Assert.True(client.WasDisposed);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task DisposeAsync_BeforeStart_CancelsQueuedRepliesAndDisposesClient()
    {
        var dir = CreateTempDir();
        try
        {
            var client = FakeChatClient.Text("hi");
            var actor = CreateActor(dir, client);

            var msg = GoalBrainActorMessages.CreateGetGoalStateMessage();
            Assert.True(actor.Tell(msg));

            await actor.DisposeAsync();

            Assert.True(actor.IsCompleted);
            await AwaitSettledAsync(msg.Reply);
            Assert.True(msg.Reply.Task.IsCanceled);
            Assert.True(client.WasDisposed);
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
            var client = FakeChatClient.Text("hi");
            var actor = CreateActor(dir, client);
            using var barrier = new Barrier(2);

            var starter = StartProducer(() => { barrier.SignalAndWait(); actor.Start(); });
            var disposer = StartProducer(() => { barrier.SignalAndWait(); actor.DisposeAsync().AsTask().GetAwaiter().GetResult(); });

            var all = Task.WhenAll(starter, disposer);
            await Task.WhenAny(all, Task.Delay(Timeout, CancellationToken.None));
            Assert.True(all.IsCompletedSuccessfully, "Start/Dispose race did not settle in time.");
            Assert.True(actor.IsCompleted);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DisposesClientExactlyOnce()
    {
        var dir = CreateTempDir();
        try
        {
            var client = FakeChatClient.Text("hi");
            var compaction = FakeChatClient.Text("compact");
            var actor = CreateActor(dir, client, compactionClient: compaction);
            actor.Start();

            await actor.DisposeAsync();
            await actor.DisposeAsync();

            Assert.Equal(1, client.DisposeCallCount);
            Assert.Equal(1, compaction.DisposeCallCount);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_InvalidGoalId_ThrowsAndDoesNotDisposeClients(string goalId)
    {
        var dir = CreateTempDir();
        try
        {
            var chat = FakeChatClient.Text("hi");
            var compaction = FakeChatClient.Text("c");

            Assert.Throws<ArgumentException>(() => new GoalBrainActor(
                goalId, AgentSession.Create("s"), chat, ownsChatClient: true, compaction,
                CreateBaseOptions(dir), "test-model", 100_000, dir, null, NullLogger<GoalBrainActor>.Instance));

            Assert.False(chat.WasDisposed);
            Assert.False(compaction.WasDisposed);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task Constructor_DoesNotMutateBaseOptions()
    {
        var dir = CreateTempDir();
        try
        {
            var baseOptions = CreateBaseOptions(dir);
            baseOptions.MaxContextTokens = 42;
            var originalTools = baseOptions.CustomTools;

            await using var actor = CreateActor(dir, FakeChatClient.Text("hi"), baseOptions: baseOptions);

            Assert.Same(originalTools, baseOptions.CustomTools);
            Assert.True(baseOptions.CustomTools is null || baseOptions.CustomTools.Count == 0);
            Assert.Equal(42, baseOptions.MaxContextTokens);
            Assert.Null(baseOptions.CompactionClient);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public void Constructor_WhenSessionPathConstructionFails_DisposesOwnedClients()
    {
        var dir = CreateTempDir();
        var chat = FakeChatClient.Text("hi");
        var compaction = FakeChatClient.Text("c");
        try
        {
            // A null stateDir makes Path.Combine throw *after* the clients have been stored,
            // but before the coding agent is created.
            Assert.Throws<ArgumentNullException>(() => new GoalBrainActor(
                "goal-x", AgentSession.Create("s"), chat, ownsChatClient: true, compaction,
                CreateBaseOptions(dir), "test-model", 100_000, null!, null, NullLogger<GoalBrainActor>.Instance));

            Assert.True(chat.WasDisposed);
            Assert.True(compaction.WasDisposed);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public void Constructor_WhenSessionPathConstructionFailsAndChatNotOwned_DisposesCompactionOnly()
    {
        var dir = CreateTempDir();
        var chat = FakeChatClient.Text("hi");
        var compaction = FakeChatClient.Text("c");
        try
        {
            Assert.Throws<ArgumentNullException>(() => new GoalBrainActor(
                "goal-x", AgentSession.Create("s"), chat, ownsChatClient: false, compaction,
                CreateBaseOptions(dir), "test-model", 100_000, null!, null, NullLogger<GoalBrainActor>.Instance));

            Assert.False(chat.WasDisposed);
            Assert.True(compaction.WasDisposed);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public void Constructor_WhenAgentCreationFails_DisposesOwnedClients()
    {
        var dir = CreateTempDir();
        var chat = FakeChatClient.Text("hi");
        var compaction = FakeChatClient.Text("c");
        try
        {
            // Valid at construction time, then removed so copying it inside the actor throws.
            var workDir = Path.Combine(dir, "work");
            Directory.CreateDirectory(workDir);
            var badOptions = CreateBaseOptions(workDir);
            Directory.Delete(workDir);

            Assert.ThrowsAny<Exception>(() => new GoalBrainActor(
                "goal-x", AgentSession.Create("s"), chat, ownsChatClient: true, compaction,
                badOptions, "test-model", 100_000, dir, null, NullLogger<GoalBrainActor>.Instance));

            Assert.True(chat.WasDisposed);
            Assert.True(compaction.WasDisposed);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public void Constructor_WhenAgentCreationFails_DisposesSeparateCompactionButNotUnownedChat()
    {
        var dir = CreateTempDir();
        var chat = FakeChatClient.Text("hi");
        var compaction = FakeChatClient.Text("c");
        try
        {
            var workDir = Path.Combine(dir, "work");
            Directory.CreateDirectory(workDir);
            var badOptions = CreateBaseOptions(workDir);
            Directory.Delete(workDir);

            Assert.ThrowsAny<Exception>(() => new GoalBrainActor(
                "goal-x", AgentSession.Create("s"), chat, ownsChatClient: false, compaction,
                badOptions, "test-model", 100_000, dir, null, NullLogger<GoalBrainActor>.Instance));

            Assert.False(chat.WasDisposed);
            Assert.Equal(1, compaction.DisposeCallCount);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task Dispose_WhenOneResourceThrows_StillDisposesOtherResourceExactlyOnce()
    {
        var dir = CreateTempDir();
        try
        {
            var chat = FakeChatClient.Text("hi");
            var compaction = new DisposeThrowingChatClient();
            var actor = CreateActor(dir, chat, ownsChatClient: true, compactionClient: compaction);
            actor.Start();

            await actor.DisposeAsync();
            await actor.DisposeAsync();

            Assert.Equal(1, compaction.DisposeCallCount);
            Assert.Equal(1, chat.DisposeCallCount);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task Dispose_WhenCompactionIsChatClientAndOwned_DisposesOnce()
    {
        var dir = CreateTempDir();
        try
        {
            var client = FakeChatClient.Text("hi");
            var actor = CreateActor(dir, client, ownsChatClient: true, compactionClient: client);
            actor.Start();

            await actor.DisposeAsync();

            Assert.Equal(1, client.DisposeCallCount);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task Dispose_WhenCompactionIsChatClientAndNotOwned_DisposesNothing()
    {
        var dir = CreateTempDir();
        try
        {
            var client = FakeChatClient.Text("hi");
            var actor = CreateActor(dir, client, ownsChatClient: false, compactionClient: client);
            actor.Start();

            await actor.DisposeAsync();

            Assert.Equal(0, client.DisposeCallCount);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task Dispose_WithSeparateCompactionAndUnownedChat_DisposesCompactionOnly()
    {
        var dir = CreateTempDir();
        try
        {
            var chat = FakeChatClient.Text("hi");
            var compaction = FakeChatClient.Text("c");
            var actor = CreateActor(dir, chat, ownsChatClient: false, compactionClient: compaction);
            actor.Start();

            await actor.DisposeAsync();

            Assert.False(chat.WasDisposed);
            Assert.True(compaction.WasDisposed);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }

    [Fact]
    public async Task Dispose_WhenLoopStuck_DefersClientDisposalUntilLoopExits()
    {
        var dir = CreateTempDir();
        try
        {
            var client = new BlockingChatClient();
            var actor = CreateActor(dir, client);
            actor.Start();

            var msg = GoalBrainActorMessages.CreateExecutePromptMessage("p", CancellationToken.None);
            Assert.True(actor.Tell(msg));
            await client.Entered.WaitAsync(Timeout, CancellationToken.None);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await actor.DisposeAsync();
            sw.Stop();
            Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(4),
                $"DisposeAsync returned too fast ({sw.Elapsed.TotalSeconds:F1}s) — timeout may not be 5 seconds");
            Assert.False(client.WasDisposed);
            Assert.False(actor.Completion.IsCompleted);

            client.Release();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!client.WasDisposed && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, CancellationToken.None);
            }

            Assert.True(client.WasDisposed);
            Assert.True(actor.Completion.IsCompletedSuccessfully);
        }
        finally
        {
            DeleteTempPath(dir);
        }
    }
    [Fact]
    public async Task BuildTools_ReturnsSevenTools()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir, FakeChatClient.Text("unused"));
            var tools = GetConfiguredOptions(actor).CustomTools;
            Assert.Equal(7, tools.Count);
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task GetGoalTool_NoGoalStore_ReturnsUnavailable()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir, FakeChatClient.Text("unused"));
            var tool = GetConfiguredOptions(actor).CustomTools.Cast<AIFunction>().First(t => t.Name == "get_goal");
            var result = (await tool.InvokeAsync(
                new AIFunctionArguments { ["goal_id"] = "g1" }, TestContext.Current.CancellationToken))?.ToString();
            Assert.Equal("Goal store is not available.", result);
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task GetGoalTool_PipelineResolverTimesOut_ReportsPipelineNotActive()
    {
        var dir = CreateTempDir();
        try
        {
            var store = new CopilotHive.Tests.InMemoryGoalStore();
            store.AddGoal(new CopilotHive.Goals.Goal
            {
                Id = "g1",
                Description = "desc",
                RepositoryNames = ["repo"],
            });

            // parentTell accepts the message but never replies → resolver times out → null pipeline.
            await using var actor = CreateActor(dir, FakeChatClient.Text("unused"),
                goalStore: store, parentTell: _ => true);

            var tool = GetConfiguredOptions(actor).CustomTools.Cast<AIFunction>().First(t => t.Name == "get_goal");
            var result = (await tool.InvokeAsync(
                new AIFunctionArguments { ["goal_id"] = "g1" }, TestContext.Current.CancellationToken))?.ToString();

            Assert.Contains("Goal ID: g1", result);
            Assert.Contains("Pipeline not active.", result);
        }
        finally { DeleteTempPath(dir); }
    }
}
