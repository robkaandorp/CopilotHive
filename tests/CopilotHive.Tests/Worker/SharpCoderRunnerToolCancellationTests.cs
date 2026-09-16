using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Reflection;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Proves the ASSIGNMENT'S cancellation token reaches the tool bridge through the REAL
/// <see cref="SharpCoderRunner"/> tool delegates.
/// <para>
/// The rejected revision passed <see cref="CancellationToken.None"/> to every bridge call in
/// <c>BuildCustomTools</c>. Because <c>WorkerService</c>'s bridge arms each pending tool request
/// with <c>ct.Register(() =&gt; tcs.TrySetCanceled())</c>, a <c>None</c>-bound wait could never be
/// released: cancelling the assignment left <c>request_clarification</c> / <c>get_goal</c> /
/// <c>raise_issue</c> hanging, so the drain in <c>ProcessMessagesAsync</c> blocked forever while
/// the runner still held the full-turn client lease.
/// </para>
/// <para>
/// The iteration-3 test substituted this away with a fake runner that forwarded its own
/// <c>SendPromptAsync</c> token. These tests instead invoke the runner's REAL tool delegates
/// against a bridge that records the token it was handed, and gate purely on
/// <see cref="TaskCompletionSource"/> — never a timing delay.
/// </para>
/// </summary>
public sealed class SharpCoderRunnerToolCancellationTests
{
    /// <summary>Invokes the private <c>BuildCustomTools(CancellationToken)</c> exactly as the turn does.</summary>
    private static IList<AITool> BuildTools(SharpCoderRunner runner, CancellationToken ct)
    {
        var method = typeof(SharpCoderRunner)
            .GetMethod("BuildCustomTools", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IList<AITool>)method.Invoke(runner, [ct])!;
    }

    private static AIFunction Tool(IList<AITool> tools, string name) =>
        (AIFunction)tools.Single(t => t is AIFunction f && f.Name == name);

    private static SharpCoderRunner CreateRunner(IToolCallBridge bridge)
    {
        var runner = new SharpCoderRunner();
        runner.SetToolBridge(bridge);
        runner.SetCurrentTaskId("task-1");
        runner.SetCurrentGoalId("goal-1");
        runner.SetCustomAgent(CopilotHive.Workers.WorkerRole.Coder, "coder");
        return runner;
    }

    // ── The token actually arrives at the bridge ──────────────────────────────

    /// <summary>
    /// Every bridge-backed tool must hand the bridge the assignment's token, not
    /// <see cref="CancellationToken.None"/>. A <c>None</c> token here is precisely the deadlock:
    /// it can never transition to cancelled.
    /// </summary>
    [Theory]
    [InlineData("request_clarification")]
    [InlineData("get_goal")]
    [InlineData("raise_issue")]
    [InlineData("report_progress")]
    [InlineData("report_narrative")]
    public async Task EveryBridgeTool_ForwardsAssignmentToken_NotNone(string toolName)
    {
        var bridge = new TokenCapturingBridge();
        await using var runner = CreateRunner(bridge);

        using var assignmentCts = new CancellationTokenSource();
        var tools = BuildTools(runner, assignmentCts.Token);

        await Tool(tools, toolName).InvokeAsync(ArgsFor(toolName), TestContext.Current.CancellationToken);

        var captured = Assert.Single(bridge.CapturedTokens);

        // The decisive assertion: the bridge received a token that CAN be cancelled, and it is
        // the assignment's token.
        Assert.True(captured.CanBeCanceled, $"{toolName} handed the bridge a non-cancellable token.");
        Assert.Equal(assignmentCts.Token, captured);
        Assert.NotEqual(CancellationToken.None, captured);
    }

    /// <summary>
    /// Cancelling the assignment must be OBSERVED by a tool that is already waiting on the
    /// bridge, so the turn can unwind and release the client lease. Gated by a TCS: the bridge
    /// blocks until cancelled, and the test asserts the wait actually ends.
    /// </summary>
    [Theory]
    [InlineData("request_clarification")]
    [InlineData("get_goal")]
    [InlineData("raise_issue")]
    public async Task PendingBridgeCall_ObservesAssignmentCancellation(string toolName)
    {
        var bridge = new BlockingBridge();
        await using var runner = CreateRunner(bridge);

        using var assignmentCts = new CancellationTokenSource();
        var tools = BuildTools(runner, assignmentCts.Token);

        var call = Tool(tools, toolName).InvokeAsync(ArgsFor(toolName), TestContext.Current.CancellationToken);

        // The tool is now parked inside the bridge, exactly like a real pending ToolResponse wait.
        await bridge.CallStarted.Task;
        Assert.False(call.IsCompleted, $"{toolName} should still be waiting for the orchestrator.");

        // Cancelling the ASSIGNMENT must release it. Under the rejected None-binding this never
        // happened and the await below would hang forever.
        await assignmentCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
    }

    /// <summary>
    /// A token already cancelled before the tool runs must also be observed — the tool must not
    /// start an unbreakable wait.
    /// </summary>
    [Fact]
    public async Task AlreadyCancelledAssignment_ToolDoesNotBeginUnbreakableWait()
    {
        var bridge = new BlockingBridge();
        await using var runner = CreateRunner(bridge);

        using var assignmentCts = new CancellationTokenSource();
        await assignmentCts.CancelAsync();

        var tools = BuildTools(runner, assignmentCts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Tool(tools, "request_clarification")
                .InvokeAsync(ArgsFor("request_clarification"), TestContext.Current.CancellationToken));
    }

    // ── The real bridge implementation releases on cancellation ───────────────

    /// <summary>
    /// End-to-end against the REAL <see cref="WorkerService"/> bridge implementation (not a
    /// stand-in): a pending <c>RequestClarificationAsync</c> whose <c>ToolResponse</c> never
    /// arrives must be released when the assignment's token is cancelled. This is the exact wait
    /// that blocked <c>DrainAssignmentAsync</c>.
    /// </summary>
    [Fact]
    public async Task RealWorkerServiceBridge_PendingClarification_ReleasedByAssignmentCancellation()
    {
        using var service = new WorkerService("http://localhost:9999", "worker-tok", ["coder"]);

        var requests = new CapturingRequestStream();
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests,
            new EmptyResponseStream(),
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

        // The bridge sends through the PUBLISHED connection, so publishing it is all this test
        // needs — the real message loop is not driven here.
        TestConnectionFactory.Attach(service, "worker-tok", stream);

        using var assignmentCts = new CancellationTokenSource();

        var pending = ((IToolCallBridge)service).RequestClarificationAsync(
            "task-1", "why?", assignmentCts.Token);

        // The request reached the wire, so the bridge is now parked on its TCS — no response will
        // ever arrive, exactly as when the orchestrator has moved on.
        await requests.FirstWrite.Task;
        Assert.False(pending.IsCompleted);

        await assignmentCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    private static AIFunctionArguments ArgsFor(string toolName) => toolName switch
    {
        "request_clarification" => new AIFunctionArguments { ["question"] = "why?" },
        "get_goal" => new AIFunctionArguments(),
        "raise_issue" => new AIFunctionArguments
        {
            ["type"] = "concern",
            ["title"] = "t",
            ["description"] = "d",
        },
        "report_progress" => new AIFunctionArguments { ["status"] = "s", ["details"] = "d" },
        "report_narrative" => new AIFunctionArguments { ["narrative"] = "n" },
        _ => throw new ArgumentOutOfRangeException(nameof(toolName), toolName, "Unknown tool."),
    };

    // ── Bridges ───────────────────────────────────────────────────────────────

    /// <summary>Records the token each bridge method was handed.</summary>
    private sealed class TokenCapturingBridge : IToolCallBridge
    {
        private readonly List<CancellationToken> _tokens = [];

        public IReadOnlyList<CancellationToken> CapturedTokens
        {
            get { lock (_tokens) return [.. _tokens]; }
        }

        private void Capture(CancellationToken ct)
        {
            lock (_tokens) _tokens.Add(ct);
        }

        public Task<string> RequestClarificationAsync(string taskId, string question, CancellationToken ct)
        {
            Capture(ct);
            return Task.FromResult("answer");
        }

        public Task ReportProgressAsync(string taskId, string status, string details, CancellationToken ct)
        {
            Capture(ct);
            return Task.CompletedTask;
        }

        public Task ReportNarrativeAsync(string taskId, string narrative, CancellationToken ct)
        {
            Capture(ct);
            return Task.CompletedTask;
        }

        public Task<string> GetGoalAsync(string taskId, string goalId, CancellationToken ct)
        {
            Capture(ct);
            return Task.FromResult("goal");
        }

        public Task<string> RaiseIssueAsync(
            string taskId, string type, string title, string description, string severity, CancellationToken ct)
        {
            Capture(ct);
            return Task.FromResult("issue");
        }
    }

    /// <summary>
    /// Blocks every call until the supplied token is cancelled, modelling a pending
    /// <c>ToolResponse</c> that never arrives.
    /// </summary>
    private sealed class BlockingBridge : IToolCallBridge
    {
        public TaskCompletionSource CallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task<string> BlockAsync(CancellationToken ct)
        {
            CallStarted.TrySetResult();
            var never = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var reg = ct.Register(() => never.TrySetCanceled(ct));
            return await never.Task;
        }

        public Task<string> RequestClarificationAsync(string taskId, string question, CancellationToken ct)
            => BlockAsync(ct);

        public Task ReportProgressAsync(string taskId, string status, string details, CancellationToken ct)
            => BlockAsync(ct);

        public Task ReportNarrativeAsync(string taskId, string narrative, CancellationToken ct)
            => BlockAsync(ct);

        public Task<string> GetGoalAsync(string taskId, string goalId, CancellationToken ct)
            => BlockAsync(ct);

        public Task<string> RaiseIssueAsync(
            string taskId, string type, string title, string description, string severity, CancellationToken ct)
            => BlockAsync(ct);
    }

    /// <summary>
    /// Signals the first written message so the test knows the bridge is parked.
    /// <para>
    /// It MUST implement the cancellable <c>WriteAsync(T, CancellationToken)</c> overload. That
    /// member is a default interface method on <see cref="IAsyncStreamWriter{T}"/> which throws
    /// <see cref="NotSupportedException"/> ("Cancellation of stream writes is not supported by
    /// this gRPC implementation") whenever the token can be cancelled. The real
    /// <c>HttpContentClientStreamWriter</c> overrides it, so production is unaffected — but a
    /// fake that omits it silently fails every write the worker makes with a live token.
    /// </para>
    /// </summary>
    private sealed class CapturingRequestStream : IClientStreamWriter<WorkerMessage>
    {
        public TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WorkerMessage message)
        {
            FirstWrite.TrySetResult();
            return Task.CompletedTask;
        }

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(WorkerMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
        }

        public Task CompleteAsync() => Task.CompletedTask;
    }

    /// <summary>Never yields a response, so the pending tool call can only end by cancellation.</summary>
    private sealed class EmptyResponseStream : IAsyncStreamReader<OrchestratorMessage>
    {
        public OrchestratorMessage Current => throw new InvalidOperationException("No current element.");

        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(false);
    }
}

/// <summary>
/// Proves the CONSTRUCTION-TIME CONTEXT CAPTURE in the <c>SharpCoderRunner.BuildCustomTools</c>
/// private method: the five bridge-backed tools close over the bridge, task ID, goal ID and
/// assignment token as they were AT TOOL CONSTRUCTION, so a RETAINED tool set keeps serving the
/// assignment it was built for even after <see cref="SharpCoderRunner.SetToolBridge"/>, <see cref="SharpCoderRunner.SetCurrentTaskId"/>
/// and <see cref="SharpCoderRunner.SetCurrentGoalId"/> prepare a later assignment — while a NEWLY
/// BUILT set uses the new context. Missing-context and null-bridge construction semantics are
/// preserved against the production tools.
/// <para>
/// The tools are obtained through the REAL production path (the private <c>BuildCustomTools</c>
/// invocation the existing fixtures already use), invoked through the <c>AIFunction</c> surface the
/// agent turn uses, and gated purely on bridges that record what they received — no sleeps, no
/// polling, no source-shape or IL assertions.
/// </para>
/// </summary>
public sealed class SharpCoderRunnerConstructionContextBindingTests
{
    /// <summary>Invokes the private <c>BuildCustomTools(CancellationToken)</c> exactly as the turn does.</summary>
    private static IList<AITool> BuildTools(SharpCoderRunner runner, CancellationToken ct)
    {
        var method = typeof(SharpCoderRunner)
            .GetMethod("BuildCustomTools", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IList<AITool>)method.Invoke(runner, [ct])!;
    }

    private static AIFunction Tool(IList<AITool> tools, string name) =>
        (AIFunction)tools.Single(t => t is AIFunction f && f.Name == name);

    private static AIFunctionArguments ArgsFor(string toolName) => toolName switch
    {
        "request_clarification" => new AIFunctionArguments { ["question"] = "why?" },
        "get_goal" => new AIFunctionArguments(),
        "raise_issue" => new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "t",
            ["description"] = "d",
        },
        "report_progress" => new AIFunctionArguments { ["status"] = "s", ["details"] = "d" },
        "report_narrative" => new AIFunctionArguments { ["narrative"] = "n" },
        _ => throw new ArgumentOutOfRangeException(nameof(toolName), toolName, "Unknown tool."),
    };

    /// <summary>
    /// RETAINED-TOOL BINDING over ALL FIVE bridge tools. The A tool set is built through the
    /// production path; the runner is then repointed at B (<c>SetToolBridge</c>,
    /// <c>SetCurrentTaskId</c>, <c>SetCurrentGoalId</c>); invoking A's retained tools must reach
    /// A's bridge with A's task/goal IDs and A's assignment token, and B's bridge must receive
    /// NOTHING from those retained tools.
    /// <para>
    /// REMOVAL-PROOFNESS: with the tools closing over the mutable runner fields (the pre-adapter
    /// shape), each invocation would read B's bridge and B's IDs, so the "A received" and
    /// "B received nothing" assertions fail by name.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("report_progress")]
    [InlineData("report_narrative")]
    [InlineData("request_clarification")]
    [InlineData("get_goal")]
    [InlineData("raise_issue")]
    public async Task RetainedTool_StayBoundToAssignmentA_AfterRunnerRepointedToB(string toolName)
    {
        var bridgeA = new RecordingBridge();
        var bridgeB = new RecordingBridge();
        await using var runner = CreateRunner(bridgeA);

        using var assignmentACts = new CancellationTokenSource();

        // A's tool set — built through the REAL production path with A's context and A's token.
        var retainedTools = BuildTools(runner, assignmentACts.Token);

        // The A→B preparation transition on the RUNNER: the next assignment's context.
        runner.SetToolBridge(bridgeB);
        runner.SetCurrentTaskId("task-B");
        runner.SetCurrentGoalId("goal-B");

        // A's RETAINED tool is invoked after the transition: it must still serve A.
        var result = (await Tool(retainedTools, toolName).InvokeAsync(
            ArgsFor(toolName), TestContext.Current.CancellationToken))?.ToString() ?? "";

        // A's bridge received the call, with A's task ID and — for get_goal, the only bridge
        // method that carries one — A's goal ID. A's token is forwarded verbatim.
        var call = Assert.Single(bridgeA.Calls);
        Assert.Equal(toolName, call.ToolName);
        Assert.Equal("task-A", call.TaskId);
        if (toolName == "get_goal")
            Assert.Equal("goal-A", call.GoalId);
        Assert.Equal(assignmentACts.Token, call.Token);
        Assert.True(call.Token.CanBeCanceled, $"{toolName} forwarded a non-cancellable token.");

        // B's bridge received NOTHING from the retained tools.
        Assert.Empty(bridgeB.Calls);

        // The tool's own return value is unchanged for the response-bearing tools.
        Assert.Equal(ExpectedReturnValue(toolName), result);
    }

    /// <summary>
    /// NEWLY BUILT TOOLS USE B. After the same A→B transition, a tool set built through the
    /// production path binds to B's bridge and B's IDs — the complement of the retained-tool case,
    /// pinning that binding is PER CONSTRUCTION, not global.
    /// </summary>
    [Theory]
    [InlineData("report_progress")]
    [InlineData("report_narrative")]
    [InlineData("request_clarification")]
    [InlineData("get_goal")]
    [InlineData("raise_issue")]
    public async Task NewlyBuiltTool_UsesTheRepointedContextB(string toolName)
    {
        var bridgeA = new RecordingBridge();
        var bridgeB = new RecordingBridge();
        await using var runner = CreateRunner(bridgeA);

        using var assignmentBCts = new CancellationTokenSource();

        _ = BuildTools(runner, CancellationToken.None); // A's set is built and (deliberately) discarded.
        runner.SetToolBridge(bridgeB);
        runner.SetCurrentTaskId("task-B");
        runner.SetCurrentGoalId("goal-B");

        var newTools = BuildTools(runner, assignmentBCts.Token);

        var result = (await Tool(newTools, toolName).InvokeAsync(
            ArgsFor(toolName), TestContext.Current.CancellationToken))?.ToString() ?? "";

        // B's bridge received the call, with B's IDs and B's token.
        var call = Assert.Single(bridgeB.Calls);
        Assert.Equal(toolName, call.ToolName);
        Assert.Equal("task-B", call.TaskId);
        if (toolName == "get_goal")
            Assert.Equal("goal-B", call.GoalId);
        Assert.Equal(assignmentBCts.Token, call.Token);

        // A's bridge received nothing.
        Assert.Empty(bridgeA.Calls);
        Assert.Equal(ExpectedReturnValue(toolName), result);
    }

    // ── Missing-context semantics (production tools, not a re-implementation) ──

    /// <summary>
    /// With NO task ID, every bridge-backed tool returns the EXACT existing string
    /// "Error: Task ID not set." and reaches NO bridge — the existing guard behavior, proved against
    /// tools the production path built.
    /// </summary>
    [Theory]
    [InlineData("report_progress")]
    [InlineData("report_narrative")]
    [InlineData("request_clarification")]
    [InlineData("get_goal")]
    [InlineData("raise_issue")]
    public async Task ToolWithoutTaskId_ReturnsExactExistingError_AndNeverReachesTheBridge(string toolName)
    {
        var bridge = new RecordingBridge();
        await using var runner = new SharpCoderRunner();
        runner.SetToolBridge(bridge);
        runner.SetCustomAgent(CopilotHive.Workers.WorkerRole.Coder, "coder");
        // No SetCurrentTaskId: _currentTaskId stays null.

        var tools = BuildTools(runner, CancellationToken.None);

        var result = await Tool(tools, toolName).InvokeAsync(
            ArgsFor(toolName), TestContext.Current.CancellationToken);

        Assert.Equal("Error: Task ID not set.", result?.ToString());
        Assert.Empty(bridge.Calls);
    }

    /// <summary>
    /// For <c>get_goal</c> with a task ID but NO goal ID, the tool returns the EXACT existing
    /// "Error: Goal ID not set." — and the EXISTING check order holds: the task-ID check runs
    /// first, so a missing goal ID alone cannot produce the task-ID error. Proved against the
    /// production tool, not a re-implementation.
    /// </summary>
    [Fact]
    public async Task GetGoalWithoutGoalId_ReturnsExactGoalError_WithTaskIdStillSet()
    {
        var bridge = new RecordingBridge();
        await using var runner = new SharpCoderRunner();
        runner.SetToolBridge(bridge);
        runner.SetCurrentTaskId("task-no-goal");
        runner.SetCustomAgent(CopilotHive.Workers.WorkerRole.Coder, "coder");
        // No SetCurrentGoalId: _currentGoalId stays null.

        var tools = BuildTools(runner, CancellationToken.None);

        var result = await Tool(tools, "get_goal").InvokeAsync(
            new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Equal("Error: Goal ID not set.", result?.ToString());
        Assert.Empty(bridge.Calls);
    }

    /// <summary>
    /// CHECK ORDER for get_goal: with BOTH IDs missing the task-ID error wins (it is checked first),
    /// so a reordered guard would fail here by name.
    /// </summary>
    [Fact]
    public async Task GetGoalWithBothIdsMissing_ReturnsTheTaskIdError_NotTheGoalError()
    {
        var bridge = new RecordingBridge();
        await using var runner = new SharpCoderRunner();
        runner.SetToolBridge(bridge);
        runner.SetCustomAgent(CopilotHive.Workers.WorkerRole.Coder, "coder");

        var tools = BuildTools(runner, CancellationToken.None);

        var result = await Tool(tools, "get_goal").InvokeAsync(
            new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Equal("Error: Task ID not set.", result?.ToString());
        Assert.Empty(bridge.Calls);
    }

    // ── Null-bridge construction semantics ────────────────────────────────────

    /// <summary>
    /// With NO bridge set, the production <c>BuildCustomTools</c> produces NO bridge-backed tools at
    /// all, and the unrelated coder role tool (<c>report_code_changes</c>) is still present and
    /// unaffected — construction does not throw and does not fabricate substitutes.
    /// </summary>
    [Fact]
    public async Task BuildCustomTools_WithoutBridge_ProducesNoBridgeBackedTools_AndRoleToolsAreUnaffected()
    {
        await using var runner = new SharpCoderRunner();
        runner.SetCustomAgent(CopilotHive.Workers.WorkerRole.Coder, "coder");

        var tools = BuildTools(runner, CancellationToken.None);

        foreach (var bridgeToolName in new[]
        {
            "report_progress", "report_narrative", "request_clarification", "get_goal", "raise_issue",
        })
        {
            Assert.DoesNotContain(tools, t => t is AIFunction f && f.Name == bridgeToolName);
        }

        // The unrelated coder role tool is unaffected by the bridge capture change.
        Assert.Contains(tools, t => t is AIFunction f && f.Name == "report_code_changes");
    }

    /// <summary>
    /// The severity default of <c>raise_issue</c> is preserved through the captured-context shape:
    /// invoking it WITHOUT the severity argument forwards "low" to the bridge, with the captured
    /// task ID and token.
    /// </summary>
    [Fact]
    public async Task RaiseIssueWithoutSeverity_ForwardsTheLowDefault_WithCapturedContext()
    {
        var bridge = new RecordingBridge();
        await using var runner = CreateRunner(bridge);
        using var assignmentCts = new CancellationTokenSource();

        var tools = BuildTools(runner, assignmentCts.Token);

        await Tool(tools, "raise_issue").InvokeAsync(
            new AIFunctionArguments
            {
                ["type"] = "concern",
                ["title"] = "t",
                ["description"] = "d",
            },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(bridge.Calls);
        Assert.Equal("low", call.Severity);
        Assert.Equal("task-A", call.TaskId);
        Assert.Equal(assignmentCts.Token, call.Token);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static SharpCoderRunner CreateRunner(IToolCallBridge bridge)
    {
        var runner = new SharpCoderRunner();
        runner.SetToolBridge(bridge);
        runner.SetCurrentTaskId("task-A");
        runner.SetCurrentGoalId("goal-A");
        runner.SetCustomAgent(CopilotHive.Workers.WorkerRole.Coder, "coder");
        return runner;
    }

    private static string ExpectedReturnValue(string toolName) => toolName switch
    {
        "request_clarification" => "answer",
        "get_goal" => "goal",
        "raise_issue" => "issue",
        "report_progress" => "Progress reported.",
        "report_narrative" => "Narrative recorded.",
        _ => throw new ArgumentOutOfRangeException(nameof(toolName), toolName, "Unknown tool."),
    };

    /// <summary>
    /// A bridge that records EVERY call's tool name, task ID, goal ID, token and (for raise_issue)
    /// the severity — the evidence surface for both A-retained and B-newly-built tool sets.
    /// </summary>
    private sealed class RecordingBridge : IToolCallBridge
    {
        private readonly object _gate = new();
        private readonly List<RecordedCall> _calls = [];

        internal IReadOnlyList<RecordedCall> Calls
        {
            get { lock (_gate) return [.. _calls]; }
        }

        private void Record(string toolName, string taskId, string? goalId, CancellationToken ct, string? severity = null)
        {
            lock (_gate) _calls.Add(new RecordedCall(toolName, taskId, goalId, ct, severity));
        }

        public Task<string> RequestClarificationAsync(string taskId, string question, CancellationToken ct)
        {
            Record("request_clarification", taskId, null, ct);
            return Task.FromResult("answer");
        }

        public Task ReportProgressAsync(string taskId, string status, string details, CancellationToken ct)
        {
            Record("report_progress", taskId, null, ct);
            return Task.CompletedTask;
        }

        public Task ReportNarrativeAsync(string taskId, string narrative, CancellationToken ct)
        {
            Record("report_narrative", taskId, null, ct);
            return Task.CompletedTask;
        }

        public Task<string> GetGoalAsync(string taskId, string goalId, CancellationToken ct)
        {
            Record("get_goal", taskId, goalId, ct);
            return Task.FromResult("goal");
        }

        public Task<string> RaiseIssueAsync(
            string taskId, string type, string title, string description, string severity, CancellationToken ct)
        {
            Record("raise_issue", taskId, null, ct, severity);
            return Task.FromResult("issue");
        }
    }

    private sealed record RecordedCall(
        string ToolName, string TaskId, string? GoalId, CancellationToken Token, string? Severity);
}
