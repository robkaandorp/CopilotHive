using Grpc.Core;
using Grpc.Net.Client;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Worker;

/// <summary>
/// Core worker lifecycle: register, heartbeat, stream tasks, execute, report.
/// </summary>
public sealed class WorkerService(
    string orchestratorUrl,
    string workerId,
    string role,
    string[] capabilities,
    int copilotPort)
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly CopilotRunner _copilotRunner = new(copilotPort);

    public async Task RunAsync(CancellationToken ct)
    {
        var workerRole = ParseRole(role);

        // Connect to the local Copilot CLI via SDK before registering with orchestrator
        Console.WriteLine("[Worker] Connecting to local Copilot CLI...");
        await _copilotRunner.ConnectAsync(ct);

        // Enable HTTP/2 over plaintext (required for gRPC without TLS in Docker network)
        using var channel = GrpcChannel.ForAddress(orchestratorUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
            }
        });
        var client = new HiveOrchestrator.HiveOrchestratorClient(channel);

        // 1. Register
        var registerRequest = new RegisterRequest
        {
            WorkerId = workerId,
            Role = workerRole,
        };
        registerRequest.Capabilities.AddRange(capabilities);

        var registerResponse = await client.RegisterAsync(registerRequest, cancellationToken: ct);

        if (!registerResponse.Accepted)
        {
            Console.Error.WriteLine("[Worker] Registration rejected by orchestrator.");
            return;
        }

        var assignedId = string.IsNullOrEmpty(registerResponse.AssignedWorkerId)
            ? workerId
            : registerResponse.AssignedWorkerId;

        Console.WriteLine($"[Worker] Registered as {assignedId} (orchestrator v{registerResponse.OrchestratorVersion})");

        // 2. Start heartbeat background task
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunHeartbeatAsync(client, assignedId, workerRole, heartbeatCts.Token);

        try
        {
            // 3. Open bidirectional work stream
            using var stream = client.WorkStream(cancellationToken: ct);

            // 4. Send WorkerReady
            await SendWorkerReady(stream, assignedId, ct);

            // 5. Main message loop
            await ProcessMessagesAsync(stream, assignedId, workerRole, ct);
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task ProcessMessagesAsync(
        Grpc.Core.AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId,
        WorkerRole workerRole,
        CancellationToken ct)
    {
        CancellationTokenSource? taskCts = null;

        await foreach (var message in ReadMessages(stream.ResponseStream, ct))
        {
            switch (message.PayloadCase)
            {
                case OrchestratorMessage.PayloadOneofCase.Assignment:
                    taskCts?.Dispose();
                    taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    var assignment = message.Assignment;
                    Console.WriteLine($"[Worker] Received task {assignment.TaskId}: {assignment.GoalDescription}");

                    // Reset Copilot session to prevent context leakage between tasks
                    await _copilotRunner.ResetSessionAsync(ct);

                    var executor = new TaskExecutor(_copilotRunner);
                    var result = await executor.ExecuteAsync(assignment, taskCts.Token);

                    await stream.RequestStream.WriteAsync(new WorkerMessage
                    {
                        WorkerId = assignedId,
                        Complete = result,
                    }, ct);

                    Console.WriteLine($"[Worker] Task {assignment.TaskId} completed ({result.Status})");

                    // Signal ready for next task
                    await SendWorkerReady(stream, assignedId, ct);
                    break;

                case OrchestratorMessage.PayloadOneofCase.Cancel:
                    var cancel = message.Cancel;
                    Console.WriteLine($"[Worker] Cancel requested for task {cancel.TaskId}: {cancel.Reason}");
                    await taskCts?.CancelAsync()!;
                    await SendWorkerReady(stream, assignedId, ct);
                    break;

                case OrchestratorMessage.PayloadOneofCase.UpdateAgents:
                    var update = message.UpdateAgents;
                    Console.WriteLine($"[Worker] Updating AGENTS.md for role: {update.Role}");
                    await UpdateAgentsMdAsync(update, ct);
                    break;

                case OrchestratorMessage.PayloadOneofCase.None:
                    break;
            }
        }

        taskCts?.Dispose();
    }

    private static async Task SendWorkerReady(
        Grpc.Core.AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId,
        CancellationToken ct)
    {
        await stream.RequestStream.WriteAsync(new WorkerMessage
        {
            WorkerId = assignedId,
            Ready = new WorkerReady(),
        }, ct);

        Console.WriteLine("[Worker] Ready for tasks.");
    }

    private static async Task RunHeartbeatAsync(
        HiveOrchestrator.HiveOrchestratorClient client,
        string assignedId,
        WorkerRole workerRole,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await client.HeartbeatAsync(new HeartbeatRequest
                {
                    WorkerId = assignedId,
                    Role = workerRole,
                    Busy = false,
                }, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[Worker] Heartbeat failed: {ex.Message}");
            }
        }
    }

    private static async Task UpdateAgentsMdAsync(UpdateAgents update, CancellationToken ct)
    {
        var agentsDir = Path.Combine(AppContext.BaseDirectory, "agents");
        Directory.CreateDirectory(agentsDir);

        var filePath = Path.Combine(agentsDir, $"{update.Role}.agents.md");
        await File.WriteAllTextAsync(filePath, update.AgentsMdContent, ct);

        Console.WriteLine($"[Worker] AGENTS.md updated: {filePath}");
    }

    private static WorkerRole ParseRole(string role) => role.ToLowerInvariant() switch
    {
        "coder" => WorkerRole.Coder,
        "reviewer" => WorkerRole.Reviewer,
        "tester" => WorkerRole.Tester,
        "improver" => WorkerRole.Improver,
        _ => throw new ArgumentException($"Unknown worker role: {role}. Must be coder/reviewer/tester/improver."),
    };

    private static async IAsyncEnumerable<T> ReadMessages<T>(
        IAsyncStreamReader<T> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await reader.MoveNext(ct))
        {
            yield return reader.Current;
        }
    }
}
