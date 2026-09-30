using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using SharpCoder;

using System.Reflection;
using System.Runtime.CompilerServices;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Pins the PRODUCTION runner's per-task token-usage accumulation: <see cref="IAgentRunner"/>
/// delivers it through <see cref="SharpCoderRunner.GetTaskUsage"/> as a DETACHED snapshot of
/// everything the task's model calls reported through <c>AgentOptions.OnUsage</c>.
/// <para>
/// The observation is taken through the runner's REAL seams — the lazily-created client
/// (<c>ClientCreationSeam</c>) and the agent-options seam (<c>OnAgentOptionsCreated</c>, the same
/// options object the production prompt turn hands to the agent) — so the assertions are made on
/// the values the production wiring actually produces, never on reflection over private state
/// (the one exception is the sink-reference check that pins REPLACE-not-CLEAR, which is by
/// definition about the private field).
/// </para>
/// <para>
/// Everything is deterministic: the fake client completes each turn in one step (or fails on a
/// scripted throw), so there are no sleeps, delays or deadline polling anywhere in this fixture.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class SharpCoderRunnerUsageTests
{
    /// <summary>The input tokens the well-behaved fake reports for every call it answers.</summary>
    private const long InputPerCall = UsageReportingChatClient.InputPerCall;

    /// <summary>The output tokens the well-behaved fake reports for every call it answers.</summary>
    private const long OutputPerCall = UsageReportingChatClient.OutputPerCall;

    /// <summary>The cached-input tokens the well-behaved fake reports, i.e. the category IS reported.</summary>
    private const long CachedPerCall = UsageReportingChatClient.CachedPerCall;

    /// <summary>The reasoning tokens the well-behaved fake reports, i.e. the category IS reported.</summary>
    private const long ReasoningPerCall = UsageReportingChatClient.ReasoningPerCall;

    /// <summary>The runner's private per-task sink field; read only to pin REPLACE-not-CLEAR.</summary>
    private static readonly FieldInfo TaskUsageSinkField =
        typeof(SharpCoderRunner).GetField("_taskUsage", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("_taskUsage field not found on SharpCoderRunner.");

    private static string CreateWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"runner-usage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A real runner whose lazy client is the supplied fake and whose role is the coder.</summary>
    private static SharpCoderRunner CreateRunner(IChatClient client, Action<AgentOptions>? observeOptions = null)
    {
        var runner = new SharpCoderRunner("/config-repo");
        runner.ClientCreationSeam = _ => client;
        runner.SetCustomAgent(WorkerRole.Coder, "coder");
        if (observeOptions is not null)
            runner.OnAgentOptionsCreated = observeOptions;
        return runner;
    }

    /// <summary>The runner's current sink instance, for the REPLACE-not-CLEAR assertion only.</summary>
    private static UsageSummary SinkOf(SharpCoderRunner runner) =>
        (UsageSummary?)TaskUsageSinkField.GetValue(runner)
        ?? throw new InvalidOperationException("The runner's usage sink is null.");

    /// <summary>
    /// Two prompt turns after ONE <see cref="SharpCoderRunner.ResetTaskUsage"/> are SUMMED: the
    /// call count, the token sums and BOTH reported-calls counters accumulate across turns, and
    /// the snapshot taken after the first turn is a detached copy that the second turn cannot
    /// change. The same turn also proves the handler is wired BEFORE the agent-options seam runs,
    /// which is what lets tests (and the executor) observe it.
    /// </summary>
    [Fact]
    public async Task GetTaskUsage_TwoPromptsAfterOneReset_SumsCallsAndCounters()
    {
        var workDir = CreateWorkDir();
        AgentOptions? observed = null;
        var runner = CreateRunner(new UsageReportingChatClient(), options => observed = options);

        try
        {
            runner.ResetTaskUsage();
            await runner.SendPromptAsync("first", workDir, TestContext.Current.CancellationToken);
            var afterFirst = runner.GetTaskUsage();

            await runner.SendPromptAsync("second", workDir, TestContext.Current.CancellationToken);
            var afterSecond = runner.GetTaskUsage();

            // The ONE accumulation holds one Agent entry per the fake's single model.
            Assert.NotNull(afterFirst);
            var firstEntry = Assert.Single(afterFirst.Entries);
            Assert.Equal(UsageSource.Agent, firstEntry.Source);
            Assert.Equal(1, firstEntry.Usage.Calls);
            Assert.Equal(InputPerCall, firstEntry.Usage.InputTokens);
            Assert.Equal(OutputPerCall, firstEntry.Usage.OutputTokens);
            Assert.Equal(CachedPerCall, firstEntry.Usage.CachedInputTokens);
            Assert.Equal(ReasoningPerCall, firstEntry.Usage.ReasoningTokens);
            Assert.Equal(1, firstEntry.Usage.CachedInputReportedCalls);
            Assert.Equal(1, firstEntry.Usage.ReasoningReportedCalls);

            var secondEntry = Assert.Single(afterSecond.Entries);
            Assert.Equal(UsageSource.Agent, secondEntry.Source);
            Assert.Equal(2, secondEntry.Usage.Calls);
            Assert.Equal(2 * InputPerCall, secondEntry.Usage.InputTokens);
            Assert.Equal(2 * OutputPerCall, secondEntry.Usage.OutputTokens);
            Assert.Equal(2 * CachedPerCall, secondEntry.Usage.CachedInputTokens);
            Assert.Equal(2 * ReasoningPerCall, secondEntry.Usage.ReasoningTokens);
            Assert.Equal(2, secondEntry.Usage.CachedInputReportedCalls);
            Assert.Equal(2, secondEntry.Usage.ReasoningReportedCalls);

            // The aggregate view agrees with the per-entry view.
            Assert.Equal(2, afterSecond.Total.Calls);
            Assert.Equal(2 * InputPerCall, afterSecond.Total.InputTokens);

            // DETACHMENT: the snapshot handed out BEFORE the second turn still reports ONE call.
            // A GetTaskUsage that returned the live accumulation would now report two.
            Assert.Equal(1, afterFirst.Entries[0].Usage.Calls);
            Assert.Equal(InputPerCall, afterFirst.Total.InputTokens);

            // The handler was already wired when the seam observed the turn's options.
            Assert.NotNull(observed);
            Assert.NotNull(observed.OnUsage);
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// <see cref="SharpCoderRunner.ResetTaskUsage"/> starts a FRESH accumulation: after a reset the
    /// reported usage is empty even though the previous task's calls are still perfectly visible in
    /// the snapshot taken before it (the positive control below).
    /// </summary>
    [Fact]
    public async Task GetTaskUsage_AfterReset_IsEmpty()
    {
        var workDir = CreateWorkDir();
        var runner = CreateRunner(new UsageReportingChatClient());

        try
        {
            runner.ResetTaskUsage();
            await runner.SendPromptAsync("first task", workDir, TestContext.Current.CancellationToken);

            // Positive control: the accumulation really was live before the reset.
            var beforeReset = runner.GetTaskUsage();
            Assert.NotNull(beforeReset);
            Assert.Equal(1, beforeReset.Total.Calls);
            Assert.Equal(InputPerCall, beforeReset.Total.InputTokens);

            runner.ResetTaskUsage();

            var afterReset = runner.GetTaskUsage();
            Assert.NotNull(afterReset);
            Assert.Empty(afterReset.Entries);
            Assert.Equal(0, afterReset.Total.Calls);
            Assert.Equal(0, afterReset.Total.InputTokens);
            Assert.Equal(0, afterReset.Total.CachedInputReportedCalls);
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// A turn that ends in SharpCoder's <c>Error</c> status — which the runner surfaces as
    /// <see cref="AgentTurnFailedException"/> — STILL has its call recorded, because recording
    /// happens per call through <c>OnUsage</c> and needs no success path. The failing call
    /// reported no cached/reasoning counts, so those categories must arrive as INCOMPLETE
    /// (counter below <c>Calls</c>), never as a measured zero.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_ErrorTurn_StillRecordsItsCall()
    {
        var workDir = CreateWorkDir();
        var runner = CreateRunner(new UsageThenThrowChatClient());

        try
        {
            runner.ResetTaskUsage();

            var ex = await Assert.ThrowsAsync<AgentTurnFailedException>(
                () => runner.SendPromptAsync("work", workDir, TestContext.Current.CancellationToken));

            // The failure shape itself is unchanged (pinned in detail by
            // SharpCoderRunnerErrorResultTests); here it only marks the failing turn.
            Assert.NotNull(ex);

            var usage = runner.GetTaskUsage();
            Assert.NotNull(usage);
            var entry = Assert.Single(usage.Entries);
            Assert.Equal(UsageSource.Agent, entry.Source);
            Assert.Equal(1, entry.Usage.Calls);
            Assert.Equal(UsageThenThrowChatClient.InputTokens, entry.Usage.InputTokens);
            Assert.Equal(UsageThenThrowChatClient.OutputTokens, entry.Usage.OutputTokens);

            // Neither optional category was reported by that call: the sums stay 0 AND the
            // counters stay below Calls, which is what makes them "unknown" rather than zero.
            Assert.Equal(0, entry.Usage.CachedInputTokens);
            Assert.Equal(0, entry.Usage.CachedInputReportedCalls);
            Assert.Equal(0, entry.Usage.ReasoningReportedCalls);
            Assert.NotEqual(entry.Usage.CachedInputReportedCalls, entry.Usage.Calls);
            Assert.NotEqual(entry.Usage.ReasoningReportedCalls, entry.Usage.Calls);
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// THE MaxStepsReached TERMINAL OUTCOME. A turn that exhausts the step budget is a
    /// NON-ERROR terminal outcome: the runner logs the status and RETURNS the agent's partial text
    /// instead of throwing, so the usage of the turn must still be in the accumulation afterwards.
    /// <para>
    /// The step budget is forced through the established seam
    /// (<c>OnAgentOptionsCreated =&gt; options.MaxSteps = ...</c>) against a tool-calling fake whose
    /// every round reports provider usage, and the outcome is INDEPENDENTLY confirmed from the
    /// runner's own closing log line (<c>status=MaxStepsReached</c>) plus the returned partial text —
    /// not from a proxy.
    /// </para>
    /// <para>
    /// Because the runner enables <c>ShowToolCallsInStream</c>, SharpCoder's MANUAL tool loop is the
    /// execution path: with a step budget of 2 and exactly one tool call per round the loop streams
    /// rounds while <c>steps &lt;= MaxSteps</c>, i.e. three rounds, and each of those rounds is a
    /// separate model call. The assertions below therefore pin the REAL counters —
    /// <c>Calls</c>, the four token sums and BOTH reported-calls counters — of exactly those three
    /// recorded calls, which is what proves provider-reported usage survives this outcome. An
    /// implementation that reported only the last round, or dropped cached/reasoning categories,
    /// fails here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_MaxStepsReachedTurn_StillRecordsEveryRoundsCall()
    {
        var workDir = CreateWorkDir();
        var runner = CreateRunner(new ToolRoundUsageChatClient());
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            // The established seam pattern: a step budget small enough to be reached immediately,
            // because the fake asks for a tool on every round and can never terminate on its own.
            runner.OnAgentOptionsCreated = options => options.MaxSteps = ToolRoundUsageChatClient.StepBudget;

            runner.ResetTaskUsage();
            var returned = await runner.SendPromptAsync("work", workDir, TestContext.Current.CancellationToken);

            // ── INDEPENDENT confirmation of the outcome ──────────────────────────
            // The runner's own closing log line reports the ACTUAL status value...
            Assert.Contains("status=MaxStepsReached", stdout.ToString(), StringComparison.Ordinal);
            // ...and the turn RETURNED the partial text rather than throwing.
            Assert.Contains(ToolRoundUsageChatClient.PartialText, returned, StringComparison.Ordinal);
            // A non-Error status keeps its message in the closing stderr line (the Error-status
            // redaction applies to the literal "Error" only).
            Assert.Contains("Agent finished with non-success status: MaxStepsReached - ", stderr.ToString(), StringComparison.Ordinal);

            // ── The usage of that same turn is retained ─────────────────────────
            var usage = runner.GetTaskUsage();
            Assert.NotNull(usage);

            var entry = Assert.Single(usage.Entries);
            Assert.Equal(UsageSource.Agent, entry.Source);
            Assert.Equal(ToolRoundUsageChatClient.ModelId, entry.Model);

            // One recorded call per streamed round — the real call count, not a proxy.
            Assert.Equal(ToolRoundUsageChatClient.Rounds, entry.Usage.Calls);
            Assert.Equal(ToolRoundUsageChatClient.Rounds * ToolRoundUsageChatClient.InputPerRound, entry.Usage.InputTokens);
            Assert.Equal(ToolRoundUsageChatClient.Rounds * ToolRoundUsageChatClient.OutputPerRound, entry.Usage.OutputTokens);
            Assert.Equal(ToolRoundUsageChatClient.Rounds * ToolRoundUsageChatClient.CachedPerRound, entry.Usage.CachedInputTokens);
            Assert.Equal(ToolRoundUsageChatClient.Rounds * ToolRoundUsageChatClient.ReasoningPerRound, entry.Usage.ReasoningTokens);

            // Both optional categories were REPORTED by every one of those calls, so each counter
            // equals Calls: the categories are complete measurements, not "unknown".
            Assert.Equal(entry.Usage.Calls, entry.Usage.CachedInputReportedCalls);
            Assert.Equal(entry.Usage.Calls, entry.Usage.ReasoningReportedCalls);
            Assert.Equal(ToolRoundUsageChatClient.Rounds, entry.Usage.CachedInputReportedCalls);
            Assert.Equal(ToolRoundUsageChatClient.Rounds, entry.Usage.ReasoningReportedCalls);

            // The aggregate view agrees.
            Assert.Equal(entry.Usage.Calls, usage.Total.Calls);
            Assert.Equal(entry.Usage.InputTokens, usage.Total.InputTokens);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// A usage-recorded turn whose fault GENUINELY PROPAGATES out of the agent. The existing
    /// Error-turn test covers the CONVERTED shape (an <see cref="InvalidOperationException"/> becomes
    /// a Completed/Error result, which the runner surfaces as
    /// <see cref="AgentTurnFailedException"/>); this test covers the ESCAPE path instead.
    /// <para>
    /// SharpCoder's manual tool loop rethrows <see cref="HttpRequestException"/> (and
    /// <see cref="OperationCanceledException"/>) from the stream enumeration and converts every
    /// OTHER fault into a Completed/Error result, so an HTTP fault is the discriminator the goal
    /// asks for. The fake reports provider usage and only THEN throws, which is exactly the shape a
    /// provider produces when a request is rejected mid-stream: the recording wrapper's
    /// <c>finally</c> records the call with the usage received so far, so the turn's tokens must
    /// survive the escape.
    /// </para>
    /// <para>
    /// The assertions pin the exception TYPE (an Error-converting fake would throw
    /// <see cref="AgentTurnFailedException"/> instead) and then the REAL counters of that same
    /// turn: <c>Calls</c>, the token sums and both reported-calls counters, which equal
    /// <c>Calls</c> because the usage update reported both optional categories.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_PropagatingHttpFault_StillRecordsTheUsageReportedBeforeIt()
    {
        var workDir = CreateWorkDir();
        var runner = CreateRunner(new UsageThenHttpFailureChatClient());

        try
        {
            runner.ResetTaskUsage();

            // ESCAPE PATH, not the Error conversion: HttpRequestException is rethrown by
            // SharpCoder's stream enumeration and travels out of SendPromptAsync unwrapped.
            var ex = await Assert.ThrowsAsync<HttpRequestException>(
                () => runner.SendPromptAsync("work", workDir, TestContext.Current.CancellationToken));

            // The fake's own fault object — nothing else wrapped or replaced it.
            Assert.Same(UsageThenHttpFailureChatClient.Fault, ex);

            // The turn's usage survived the escape.
            var usage = runner.GetTaskUsage();
            Assert.NotNull(usage);

            var entry = Assert.Single(usage.Entries);
            Assert.Equal(UsageSource.Agent, entry.Source);
            Assert.Equal(UsageThenHttpFailureChatClient.ModelId, entry.Model);

            Assert.Equal(1, entry.Usage.Calls);
            Assert.Equal(UsageThenHttpFailureChatClient.InputTokens, entry.Usage.InputTokens);
            Assert.Equal(UsageThenHttpFailureChatClient.OutputTokens, entry.Usage.OutputTokens);
            Assert.Equal(UsageThenHttpFailureChatClient.CachedTokens, entry.Usage.CachedInputTokens);
            Assert.Equal(UsageThenHttpFailureChatClient.ReasoningTokens, entry.Usage.ReasoningTokens);

            // Both categories were reported by that call, so the counters equal Calls.
            Assert.Equal(entry.Usage.Calls, entry.Usage.CachedInputReportedCalls);
            Assert.Equal(entry.Usage.Calls, entry.Usage.ReasoningReportedCalls);
            Assert.Equal(1, entry.Usage.CachedInputReportedCalls);
            Assert.Equal(1, entry.Usage.ReasoningReportedCalls);
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// THE TASK-ISOLATION CONTRACT. A usage event delivered through the PREVIOUS task's captured
    /// <c>OnUsage</c> handler AFTER <see cref="SharpCoderRunner.ResetTaskUsage"/> (SharpCoder may
    /// fire one concurrently with, or after, a turn — a sub-agent forwards its calls live) must not
    /// appear in the new task's usage.
    /// <para>
    /// The reset must REPLACE the sink, not clear the shared instance: the reference check below
    /// proves the instance changed, and the late event is then shown to have been accepted by the
    /// OLD instance (whose totals grow) while the NEW one stays untouched. Clearing the shared
    /// instance instead would let the late event's 5,000 input tokens appear in the new task.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LateUsageEventForPreviousTask_DoesNotLeakIntoTheNewTask()
    {
        var workDir = CreateWorkDir();
        AgentOptions? observed = null;
        var runner = CreateRunner(new UsageReportingChatClient(), options => observed = options);

        try
        {
            runner.ResetTaskUsage();
            await runner.SendPromptAsync("first task", workDir, TestContext.Current.CancellationToken);

            var firstTask = runner.GetTaskUsage();
            Assert.NotNull(firstTask);
            Assert.Equal(1, firstTask.Total.Calls);

            // The handler the FIRST task's turn bound, captured while its options were built.
            Assert.NotNull(observed);
            var previousOnUsage = observed.OnUsage;
            Assert.NotNull(previousOnUsage);

            var previousSink = SinkOf(runner);
            Assert.Equal(1, previousSink.Total.Calls);

            // ── THE TASK BOUNDARY ────────────────────────────────────────────────
            runner.ResetTaskUsage();

            var newSink = SinkOf(runner);
            Assert.NotSame(previousSink, newSink);

            // A late event from the previous task (a sub-agent's forwarded call) arrives through
            // the OLD handler at an arbitrary moment — here, deterministically, right after the
            // boundary.
            previousOnUsage(new UsageEvent(UsageSource.SubAgent, "late-model", new TokenUsage
            {
                InputTokens = 5_000,
                OutputTokens = 500,
                CachedInputTokens = 50,
                ReasoningTokens = 5,
            }));

            // The OLD sink accepted it (it was replaced, not cleared), so the event is genuinely
            // a late delivery rather than an ignored one...
            Assert.Equal(2, previousSink.Total.Calls);
            Assert.Equal(5_000, previousSink.Entries.Single(e => e.Source == UsageSource.SubAgent).Usage.InputTokens);

            // ...and it is INVISIBLE downstream: neither the new task's accumulation nor the
            // snapshot the first task handed out changed.
            var newTask = runner.GetTaskUsage();
            Assert.NotNull(newTask);
            Assert.Empty(newTask.Entries);
            Assert.Equal(0, newTask.Total.Calls);
            Assert.Equal(0, newTask.Total.InputTokens);
            Assert.Equal(1, firstTask.Total.Calls);

            // The NEXT turn of the new task starts from zero and contributes exactly its own call.
            await runner.SendPromptAsync("second task", workDir, TestContext.Current.CancellationToken);

            var secondTask = runner.GetTaskUsage();
            Assert.NotNull(secondTask);
            var entry = Assert.Single(secondTask.Entries);
            Assert.Equal(UsageSource.Agent, entry.Source);
            Assert.Equal(1, entry.Usage.Calls);
            Assert.Equal(InputPerCall, entry.Usage.InputTokens);
            Assert.DoesNotContain(secondTask.Entries, e => e.Usage.InputTokens == 5_000);
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }
}

// ── Stub chat clients ────────────────────────────────────────────────────────

/// <summary>
/// A client that answers every streaming call with one assistant text update plus
/// <see cref="UsageContent"/> carrying input, output, cached-input AND reasoning counts, so the
/// recorded call is a COMPLETE measurement of all four categories. It completes in a single step,
/// so one prompt turn is exactly one model call.
/// </summary>
file sealed class UsageReportingChatClient : IChatClient
{
    /// <summary>The input tokens reported for every call.</summary>
    internal const long InputPerCall = 1_000;

    /// <summary>The output tokens reported for every call.</summary>
    internal const long OutputPerCall = 100;

    /// <summary>The cached-input tokens reported for every call (this category IS reported).</summary>
    internal const long CachedPerCall = 25;

    /// <summary>The reasoning tokens reported for every call (this category IS reported).</summary>
    internal const long ReasoningPerCall = 5;

    public ChatClientMetadata Metadata => new("usage-reporting-stub", null, "usage-reporting-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "All done."))
        {
            FinishReason = ChatFinishReason.Stop,
            Usage = Details(),
        });

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("All done.")]);
        yield return new ChatResponseUpdate { Contents = [new UsageContent(Details())] };
        yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, Role = ChatRole.Assistant };
    }

    private static UsageDetails Details() => new()
    {
        InputTokenCount = InputPerCall,
        OutputTokenCount = OutputPerCall,
        CachedInputTokenCount = CachedPerCall,
        ReasoningTokenCount = ReasoningPerCall,
    };

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// A client whose streaming call reports usage and THEN throws a non-cancellation fault. The
/// recorded call therefore carries real tokens while the turn itself ends in SharpCoder's
/// <c>Error</c> status — the exact shape the runner converts into
/// <see cref="AgentTurnFailedException"/>. It reports neither cached-input nor reasoning tokens,
/// so those categories arrive as incomplete.
/// </summary>
file sealed class UsageThenThrowChatClient : IChatClient
{
    /// <summary>The input tokens reported before the failure.</summary>
    internal const long InputTokens = 100;

    /// <summary>The output tokens reported before the failure.</summary>
    internal const long OutputTokens = 7;

    public ChatClientMetadata Metadata => new("usage-then-throw-stub", null, "usage-then-throw-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("provider failure");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("partial progress.")]);
        yield return new ChatResponseUpdate
        {
            Contents =
            [
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = InputTokens,
                    OutputTokenCount = OutputTokens,
                }),
            ],
        };

        // The throw IS the point: it is not an OperationCanceledException/HttpRequestException, so
        // SharpCoder reports it as a Completed/Error result instead of propagating it.
        throw new InvalidOperationException("provider failure");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// A tool-calling fake for the MaxStepsReached outcome: every round streams partial text, a
/// <c>report_progress</c> tool call and <see cref="UsageContent"/> with all four token categories,
/// so the agent can never terminate on its own and each round is one recorded model call.
/// <para>
/// With the runner's <c>ShowToolCallsInStream</c> the manual tool loop runs
/// <c>while (steps &lt;= MaxSteps)</c>, so a budget of 2 yields exactly three rounds
/// (<see cref="Rounds"/>) before the loop exits with the <c>MaxStepsReached</c> status.
/// </para>
/// </summary>
file sealed class ToolRoundUsageChatClient : IChatClient
{
    /// <summary>The step budget the test forces through the options seam.</summary>
    internal const int StepBudget = 2;

    /// <summary>The number of model calls the manual tool loop makes at <see cref="StepBudget"/>.</summary>
    internal const int Rounds = StepBudget + 1;

    /// <summary>The partial text every round contributes (and the turn returns).</summary>
    internal const string PartialText = "partial progress.";

    /// <summary>The model this client reports through its metadata.</summary>
    internal const string ModelId = "tool-round-model";

    /// <summary>The input tokens reported for each round.</summary>
    internal const long InputPerRound = 300;

    /// <summary>The output tokens reported for each round.</summary>
    internal const long OutputPerRound = 30;

    /// <summary>The cached-input tokens reported for each round.</summary>
    internal const long CachedPerRound = 12;

    /// <summary>The reasoning tokens reported for each round.</summary>
    internal const long ReasoningPerRound = 3;

    private int _callCount;

    public ChatClientMetadata Metadata => new("tool-round-stub", null, ModelId);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, PartialText))
        {
            FinishReason = ChatFinishReason.Stop,
            Usage = Details(),
        });

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _callCount);
        await Task.Yield();

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(PartialText + " ")]);
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(Details())]);
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(
            $"round-{call}", "report_progress",
            new Dictionary<string, object?> { ["status"] = "s", ["details"] = "d" })])
        {
            ModelId = ModelId,
            FinishReason = ChatFinishReason.ToolCalls,
        };
    }

    private static UsageDetails Details() => new()
    {
        InputTokenCount = InputPerRound,
        OutputTokenCount = OutputPerRound,
        CachedInputTokenCount = CachedPerRound,
        ReasoningTokenCount = ReasoningPerRound,
    };

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// A fake that reports usage for its streaming call and THEN throws the one fault shape SharpCoder
/// genuinely PROPAGATES: <see cref="HttpRequestException"/> is rethrown from the stream
/// enumeration (along with <see cref="OperationCanceledException"/>) instead of being converted into
/// a Completed/Error result, so the exception escapes <c>SendPromptAsync</c> unwrapped.
/// <para>
/// The fault instance is exposed as <see cref="Fault"/> so the test can assert identity rather than
/// only the type, and both optional usage categories ARE reported before the throw, so the recorded
/// call's counters must equal its call count.
/// </para>
/// </summary>
file sealed class UsageThenHttpFailureChatClient : IChatClient
{
    /// <summary>The input tokens reported before the fault.</summary>
    internal const long InputTokens = 1_100;

    /// <summary>The output tokens reported before the fault.</summary>
    internal const long OutputTokens = 11;

    /// <summary>The cached-input tokens reported before the fault.</summary>
    internal const long CachedTokens = 7;

    /// <summary>The reasoning tokens reported before the fault.</summary>
    internal const long ReasoningTokens = 2;

    /// <summary>The model this client reports through its metadata.</summary>
    internal const string ModelId = "usage-then-http-failure-model";

    /// <summary>The exact fault the streaming enumeration throws, rethrown by SharpCoder unwrapped.</summary>
    internal static readonly HttpRequestException Fault = new("transport down");

    public ChatClientMetadata Metadata => new("usage-then-http-failure-stub", null, ModelId);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw Fault;

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        // The model is reported the way a provider reports it on the stream itself: SharpCoder
        // resolves the recorded entry's model from the streaming updates' ModelId (its
        // ChatClientMetadata lookup goes through IChatClient.GetService, which this fake does not
        // answer), so these updates are what make the entry attributable to this model.
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("partial progress.")])
        {
            ModelId = ModelId,
        };
        yield return new ChatResponseUpdate
        {
            ModelId = ModelId,
            Contents =
            [
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = InputTokens,
                    OutputTokenCount = OutputTokens,
                    CachedInputTokenCount = CachedTokens,
                    ReasoningTokenCount = ReasoningTokens,
                }),
            ],
        };

        // THE ESCAPE PATH: HttpRequestException is rethrown by SharpCoder's stream enumeration and
        // therefore leaves SendPromptAsync as-is — unlike the InvalidOperationException the
        // Error-turn fake uses, which becomes a Completed/Error result.
        throw Fault;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
