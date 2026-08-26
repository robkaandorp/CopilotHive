using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;

using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for <see cref="WorkerService.RaiseIssueAsync"/>, verifying that the
/// worker sends a <see cref="ToolCallRequest"/> with tool name <c>raise_issue</c>,
/// the correct task ID, and argument JSON containing type/title/description/severity.
/// </summary>
public sealed class WorkerServiceIssueToolTests
{
    [Fact]
    public async Task RaiseIssueAsync_SendsToolCallRequest_WithExpectedArguments()
    {
        using var service = new WorkerService("http://localhost:9999", "worker-1", ["coder"]);
        var requests = new FakeRequestStream();
        var responses = new FakeResponseStream();
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests,
            responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

        var streamField = typeof(WorkerService).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._stream field not found.");
        streamField.SetValue(service, stream);

        var assignedIdField = typeof(WorkerService).GetField("_assignedId", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._assignedId field not found.");
        assignedIdField.SetValue(service, "worker-1");

        // Start the raise_issue call; it will block awaiting the orchestrator response.
        var raiseTask = service.RaiseIssueAsync(
            "task-1", "bug", "Parser crashes", "It crashes on empty input", "high", CancellationToken.None);

        // Wait for the ToolCallRequest to be written to the fake stream.
        var message = await requests.ReadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(message.ToolRequest);
        Assert.Equal("raise_issue", message.ToolRequest.ToolName);
        Assert.Equal("task-1", message.ToolRequest.TaskId);
        Assert.Equal("worker-1", message.WorkerId);

        // Verify argument JSON.
        using var args = JsonDocument.Parse(message.ToolRequest.ArgumentsJson);
        Assert.Equal("bug", args.RootElement.GetProperty("type").GetString());
        Assert.Equal("Parser crashes", args.RootElement.GetProperty("title").GetString());
        Assert.Equal("It crashes on empty input", args.RootElement.GetProperty("description").GetString());
        Assert.Equal("high", args.RootElement.GetProperty("severity").GetString());

        // Resolve the pending TCS so the RaiseIssueAsync task completes.
        var pendingField = typeof(WorkerService).GetField("_pendingToolCalls", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._pendingToolCalls field not found.");
        var pending = (System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<ToolCallResponse>>)pendingField.GetValue(service)!;
        Assert.True(pending.TryGetValue(message.ToolRequest.RequestId, out var tcs));
        tcs!.TrySetResult(new ToolCallResponse
        {
            RequestId = message.ToolRequest.RequestId,
            ResultJson = "{\"acknowledged\":true,\"issue_id\":\"parser-crashes\"}",
            Success = true,
        });

        var result = await raiseTask;
        Assert.Contains("\"acknowledged\":true", result);
        Assert.Contains("\"issue_id\":\"parser-crashes\"", result);
    }

    /// <summary>Captures WorkerMessages written by the service.</summary>
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

        public Task<WorkerMessage> ReadAsync(CancellationToken ct)
            => _written.Reader.ReadAsync(ct).AsTask();
    }

    /// <summary>Empty response stream — no messages are expected in this test.</summary>
    private sealed class FakeResponseStream : IAsyncStreamReader<OrchestratorMessage>
    {
        public OrchestratorMessage Current => throw new InvalidOperationException("No messages expected.");

        public Task<bool> MoveNext(CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
