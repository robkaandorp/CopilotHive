using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Captured-runtime tests for caller cancellation versus availability fallback. Console output is
/// serialized because the production <see cref="WorkerLogger"/> has no injected logging seam.
/// All fetch coordination is driven by <see cref="TaskCompletionSource"/> gates.
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerConfigProvisionerCancellationRuntimeTests
{
    [Fact]
    public async Task EnsureProvisionedAsync_CallerCancelledRpcPropagatesWithoutFallback_UnavailableStillReverts()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        var writes = new List<(string Name, string? Value)>();
        string? Read(string name) => env.TryGetValue(name, out var value) ? value : null;
        void Write(string name, string? value)
        {
            env[name] = value;
            writes.Add((name, value));
        }

        var successEntered = NewGate();
        var successReply = new TaskCompletionSource<GetWorkerConfigResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledEntered = NewGate();
        var holdCancelledFetch = NewGate();
        var unavailableEntered = NewGate();
        var unavailableReply = new TaskCompletionSource<GetWorkerConfigResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<GetWorkerConfigResponse> Fetch(GetWorkerConfigRequest request, CancellationToken ct)
        {
            Assert.Equal("worker-cancellation", request.WorkerId);
            return Interlocked.Increment(ref calls) switch
            {
                1 => SignalThenAwait(successEntered, successReply, ct),
                2 => SurfaceCallerCancellationAsRpc(cancelledEntered, holdCancelledFetch, ct),
                3 => SignalThenAwait(unavailableEntered, unavailableReply, ct),
                var call => throw new InvalidOperationException($"Unexpected fetch call {call}."),
            };
        }

        var provisioner = new WorkerConfigProvisioner("worker-cancellation", Fetch, Read, Write);
        var output = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(output);

        try
        {
            // Establish provisioned state so a fallback would be observable as environment writes.
            var success = provisioner.EnsureProvisionedAsync(
                "copilot/gpt-5", TestContext.Current.CancellationToken);
            await successEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            successReply.TrySetResult(new GetWorkerConfigResponse
            {
                GithubToken = "provisioned-token",
                LlmProvider = "copilot",
                OllamaApiKey = "provisioned-ollama-key",
            });
            await success;

            Assert.Equal("provisioned-token", env[WorkerConfigProvisioner.GhTokenVar]);
            Assert.Equal("copilot", env[WorkerConfigProvisioner.LlmProviderVar]);
            Assert.Equal("provisioned-ollama-key", env[WorkerConfigProvisioner.OllamaApiKeyVar]);
            var writesBeforeCancellation = writes.Count;
            output.GetStringBuilder().Clear();

            // The fetch is live first; only then does the caller cancel. The delegate translates
            // that cancellation into the RpcException shape emitted by gRPC.
            using var callerCts = new CancellationTokenSource();
            var cancelledCall = provisioner.EnsureProvisionedAsync("copilot/gpt-5", callerCts.Token);
            await cancelledEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await callerCts.CancelAsync();

            var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await cancelledCall);
            Assert.Equal(callerCts.Token, cancellation.CancellationToken);

            // No fallback side effects: no revert writes, no availability warning, state intact.
            Assert.Equal(writesBeforeCancellation, writes.Count);
            Assert.Equal("provisioned-token", env[WorkerConfigProvisioner.GhTokenVar]);
            Assert.Equal("copilot", env[WorkerConfigProvisioner.LlmProviderVar]);
            Assert.Equal("provisioned-ollama-key", env[WorkerConfigProvisioner.OllamaApiKeyVar]);
            Assert.DoesNotContain("GetWorkerConfig RPC failed", output.ToString());
            Assert.DoesNotContain("falling back", output.ToString(), StringComparison.OrdinalIgnoreCase);

            // Control: a live caller plus server-side Unavailable must use revert/fallback.
            output.GetStringBuilder().Clear();
            var unavailableCall = provisioner.EnsureProvisionedAsync(
                "copilot/gpt-5", TestContext.Current.CancellationToken);
            await unavailableEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            unavailableReply.TrySetException(new RpcException(
                new Status(StatusCode.Unavailable, "server unavailable")));
            await unavailableCall;

            Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
            Assert.Null(env[WorkerConfigProvisioner.LlmProviderVar]);
            Assert.Null(env[WorkerConfigProvisioner.OllamaApiKeyVar]);
            Assert.True(writes.Count > writesBeforeCancellation);
            Assert.Contains("GetWorkerConfig RPC failed", output.ToString());
            Assert.Contains("falling back", output.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3, calls);
        }
        finally
        {
            Console.SetOut(originalOut);
            holdCancelledFetch.TrySetResult(true);
        }
    }

    private static TaskCompletionSource<bool> NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<GetWorkerConfigResponse> SignalThenAwait(
        TaskCompletionSource<bool> entered,
        TaskCompletionSource<GetWorkerConfigResponse> reply,
        CancellationToken ct)
    {
        entered.TrySetResult(true);
        return await reply.Task.WaitAsync(ct);
    }

    private static async Task<GetWorkerConfigResponse> SurfaceCallerCancellationAsRpc(
        TaskCompletionSource<bool> entered,
        TaskCompletionSource<bool> hold,
        CancellationToken ct)
    {
        entered.TrySetResult(true);
        try
        {
            await hold.Task.WaitAsync(ct);
            throw new InvalidOperationException("Cancellation gate was released without cancellation.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "caller cancelled"));
        }
    }
}
