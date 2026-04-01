using CopilotHive.Worker;
using Grpc.Core;

// Required for gRPC over plaintext HTTP/2 (no TLS in Docker network)
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var orchestratorUrl = Environment.GetEnvironmentVariable("ORCHESTRATOR_URL");
if (string.IsNullOrWhiteSpace(orchestratorUrl))
{
    Console.Error.WriteLine("ORCHESTRATOR_URL environment variable is required.");
    return 1;
}

var workerId = Environment.GetEnvironmentVariable("WORKER_ID")
    ?? Guid.NewGuid().ToString("N")[..12];

var capabilities = Environment.GetEnvironmentVariable("WORKER_CAPABILITIES")
    ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? [];

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

Console.WriteLine($"[Worker] Starting worker {workerId}");
Console.WriteLine($"[Worker] Orchestrator: {orchestratorUrl}");

var delay = TimeSpan.FromSeconds(5);
var maxDelay = TimeSpan.FromSeconds(60);

while (!cts.IsCancellationRequested)
{
    // Fresh instance each attempt so no stale connection state leaks through retries
    using var service = new WorkerService(
        orchestratorUrl: orchestratorUrl,
        workerId: workerId,
        capabilities: capabilities);

    try
    {
        await service.RunAsync(cts.Token);
        break; // clean exit
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("[Worker] Shutting down gracefully.");
        break;
    }
    catch (Exception ex) when (ex is RpcException or HttpRequestException or IOException)
    {
        Console.Error.WriteLine($"[Worker] Connection failed: {ex.Message}. Retrying in {delay.TotalSeconds}s...");
        try
        {
            await Task.Delay(delay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[Worker] Shutting down gracefully.");
            break;
        }
        delay = delay * 2 > maxDelay ? maxDelay : delay * 2;
    }
    catch (Exception ex)
    {
        // All other exceptions are fatal — bad config, invalid credentials, etc.
        Console.Error.WriteLine($"[Worker] Fatal error: {ex}");
        return 1;
    }
}
return 0;
