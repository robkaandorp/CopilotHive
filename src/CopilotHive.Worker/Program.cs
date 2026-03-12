using CopilotHive.Worker;

var orchestratorUrl = Environment.GetEnvironmentVariable("ORCHESTRATOR_URL");
if (string.IsNullOrWhiteSpace(orchestratorUrl))
{
    Console.Error.WriteLine("ORCHESTRATOR_URL environment variable is required.");
    return 1;
}

var roleStr = Environment.GetEnvironmentVariable("WORKER_ROLE");
if (string.IsNullOrWhiteSpace(roleStr))
{
    Console.Error.WriteLine("WORKER_ROLE environment variable is required (coder/reviewer/tester/improver).");
    return 1;
}

var workerId = Environment.GetEnvironmentVariable("WORKER_ID")
    ?? Guid.NewGuid().ToString("N")[..12];

var capabilities = Environment.GetEnvironmentVariable("WORKER_CAPABILITIES")
    ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? [];

var copilotPort = int.TryParse(Environment.GetEnvironmentVariable("COPILOT_PORT"), out var p) ? p : 8000;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

Console.WriteLine($"[Worker] Starting worker {workerId} (role={roleStr})");
Console.WriteLine($"[Worker] Orchestrator: {orchestratorUrl}");

var service = new WorkerService(
    orchestratorUrl: orchestratorUrl,
    workerId: workerId,
    role: roleStr,
    capabilities: capabilities,
    copilotPort: copilotPort);

try
{
    await service.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("[Worker] Shutting down gracefully.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[Worker] Fatal error: {ex}");
    return 1;
}

return 0;
