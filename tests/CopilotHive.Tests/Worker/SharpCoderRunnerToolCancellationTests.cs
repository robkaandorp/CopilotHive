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
        var runner = CreateRunner(bridge);

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
        var runner = CreateRunner(bridge);

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
        var runner = CreateRunner(bridge);

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

        typeof(WorkerService).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, stream);
        typeof(WorkerService).GetField("_assignedId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, "worker-tok");

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
