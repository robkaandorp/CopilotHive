using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Reflection;
using System.Threading.Channels;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Regression tests proving that <see cref="WorkerService"/> forwards the reasoning effort it
/// receives on a <see cref="TaskAssignment"/> to the
/// <c>ResetSessionAsync(string?, ReasoningEffort?, CancellationToken)</c> method.
/// <para>
/// These tests drive the real private <c>ProcessMessagesAsync</c> loop over fake gRPC streams and
/// swap <c>_agentRunner</c> for a capturing fake via reflection. That is deliberate: asserting on a
/// hand-rolled call to <c>ResetSessionAsync</c> would only test the fake, not the production
/// forwarding, and would keep passing if line 135 of WorkerService dropped the effort argument.
/// </para>
/// </summary>
public sealed class WorkerServiceReasoningForwardingTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the production <c>ProcessMessagesAsync</c> loop against a single assignment message and
    /// returns the arguments the agent runner received.
    /// </summary>
    private static async Task<(string? Model, ReasoningEffort? ReasoningEffort)?> ForwardAssignmentAsync(
        TaskAssignment assignment, CancellationToken ct)
    {
        using var service = new WorkerService("http://localhost:9999", "worker-1", ["coder"]);

        var runner = new CapturingAgentRunner();
        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._agentRunner field not found.");

        // Dispose the real SharpCoderRunner that the primary constructor created before replacing it.
        if (field.GetValue(service) is IAgentRunner existing)
            await existing.DisposeAsync();
        field.SetValue(service, runner);

        var responses = new FakeResponseStream([new OrchestratorMessage { Assignment = assignment }]);
        var requests = new FakeRequestStream();
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests,
            responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

        var processMessages = typeof(WorkerService).GetMethod(
            "ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService.ProcessMessagesAsync method not found.");

        // The response stream completes after the single assignment, so the loop exits on its own.
        var task = (Task)processMessages.Invoke(service, [stream, "worker-1", ct])!;
        await task;

        return runner.CapturedResetArgs;
    }

    private static TaskAssignment BuildAssignment(string model, string reasoningEffort) => new()
    {
        TaskId = "task-1",
        GoalId = "goal-1",
        GoalDescription = "Do the thing",
        Prompt = "Work on it",
        Role = GrpcWorkerRole.Coder,
        Model = model,
        ReasoningEffort = reasoningEffort,
    };

    // ── Forwarding ────────────────────────────────────────────────────────────

    /// <summary>
    /// A populated <c>reasoning_effort</c> on the wire must arrive at the agent runner as the
    /// corresponding enum, alongside the model.
    /// </summary>
    [Fact]
    public async Task WorkerService_ForwardsReasoningEffort_ToResetSession()
    {
        var assignment = BuildAssignment("test-model", "high");

        var captured = await ForwardAssignmentAsync(assignment, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("test-model", captured!.Value.Model);
        Assert.Equal(ReasoningEffort.High, captured.Value.ReasoningEffort);
    }

    [Theory]
    [InlineData("none", ReasoningEffort.None)]
    [InlineData("low", ReasoningEffort.Low)]
    [InlineData("medium", ReasoningEffort.Medium)]
    [InlineData("high", ReasoningEffort.High)]
    [InlineData("extra_high", ReasoningEffort.ExtraHigh)]
    public async Task WorkerService_ForwardsEachReasoningEffort(string wireValue, ReasoningEffort expected)
    {
        var assignment = BuildAssignment("test-model", wireValue);

        var captured = await ForwardAssignmentAsync(assignment, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(expected, captured!.Value.ReasoningEffort);
    }

    /// <summary>
    /// Proto3 sends "" for an unset reasoning effort; the worker must receive <c>null</c>,
    /// meaning reasoning effort is unset (there is no model-name fallback).
    /// </summary>
    [Fact]
    public async Task WorkerService_ForwardsNullReasoningEffort_WhenProtoEmpty()
    {
        var assignment = BuildAssignment("test-model", "");

        var captured = await ForwardAssignmentAsync(assignment, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("test-model", captured!.Value.Model);
        Assert.Null(captured.Value.ReasoningEffort);
    }

    /// <summary>
    /// An empty model resolves to <c>null</c> (SDK default) while the reasoning effort is still
    /// forwarded independently.
    /// </summary>
    [Fact]
    public async Task WorkerService_ForwardsReasoningEffort_WhenModelEmpty()
    {
        var assignment = BuildAssignment("", "medium");

        var captured = await ForwardAssignmentAsync(assignment, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Null(captured!.Value.Model);
        Assert.Equal(ReasoningEffort.Medium, captured.Value.ReasoningEffort);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    /// <summary>Captures the arguments passed to ResetSessionAsync.</summary>
    private sealed class CapturingAgentRunner : IAgentRunner
    {
        public (string? Model, ReasoningEffort? ReasoningEffort)? CapturedResetArgs { get; private set; }

        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(DomainWorkerRole role, string agentsMdContent) { }
        public void SetSession(object? session) { }
        public object? GetSession() => null;
        public void SetMaxContextTokens(int maxTokens) { }
        public int GetContextUsagePercent() => 0;
        public void SetCompactionModel(string? model) { }
        public void SetCompactionMaxTokens(int? maxTokens) { }
        public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) { }
        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) { }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
        {
            CapturedResetArgs = (model, reasoningEffort);
            return Task.CompletedTask;
        }

        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
            => Task.FromResult("");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Replays a fixed set of orchestrator messages, then completes.</summary>
    private sealed class FakeResponseStream(IReadOnlyList<OrchestratorMessage> messages)
        : IAsyncStreamReader<OrchestratorMessage>
    {
        private int _index = -1;

        public OrchestratorMessage Current =>
            _index >= 0 && _index < messages.Count
                ? messages[_index]
                : throw new InvalidOperationException("No current element.");

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _index++;
            return Task.FromResult(_index < messages.Count);
        }
    }

    /// <summary>Collects anything the worker writes back to the orchestrator.</summary>
    private sealed class FakeRequestStream : FakeClientStreamWriter<WorkerMessage>
    {
        private readonly Channel<WorkerMessage> _written = Channel.CreateUnbounded<WorkerMessage>();

        public override Task WriteAsync(WorkerMessage message)
        {
            _written.Writer.TryWrite(message);
            return Task.CompletedTask;
        }

        public override Task CompleteAsync()
        {
            _written.Writer.TryComplete();
            return Task.CompletedTask;
        }
    }
}
