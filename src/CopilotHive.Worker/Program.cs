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

// ProcessExit fires during process teardown, AFTER the `using var cts` below has disposed
// the token source on the fatal path. A handler that throws there is unobservable except for
// a runtime stack trace and a corrupted exit code (1 becomes 134 on Linux), so the handler
// must suppress every exception — a disposed CTS raises ObjectDisposedException and
// registered cancellation callbacks can surface AggregateException. No disposed-flag check:
// it would race with disposal. The handler still cancels the SAME cts the worker awaits, so
// a ProcessExit during a running task cancels it exactly as before.
void OnProcessExit(object? sender, EventArgs e)
{
    try
    {
        cts.Cancel();
    }
    catch (Exception)
    {
        // Swallow: a throwing teardown handler is unobservable and would corrupt the exit code.
    }
}

AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

Console.WriteLine($"[Worker] Starting worker {workerId}");
Console.WriteLine($"[Worker] Orchestrator: {orchestratorUrl}");

var delay = TimeSpan.FromSeconds(5);
var maxDelay = TimeSpan.FromSeconds(60);

while (!cts.IsCancellationRequested)
{
    // Fresh instance each attempt so no stale connection state leaks through retries.
    //
    // The instance is deliberately NOT declared with `using` at loop scope: that would place
    // the compiler-generated Dispose() AFTER the catch blocks below, so a throwing disposal —
    // and runner disposal is deliberately fallible and propagating — would escape top level and
    // be dumped by the runtime with its RAW message and stack, bypassing SafeExceptionLog.
    // Instead the service is disposed inside the try, in a finally, so every disposal fault is
    // routed through the sanitized catches below.
    var service = new WorkerService(
        orchestratorUrl: orchestratorUrl,
        workerId: workerId,
        capabilities: capabilities);

    var cleanExit = false;

    try
    {
        try
        {
            await service.RunAsync(cts.Token);
            cleanExit = true;
        }
        finally
        {
            // Disposal propagates (by design). Running it here means any fault it raises is
            // caught and REDACTED by the handlers below instead of reaching the runtime.
            service.Dispose();
        }

        if (cleanExit)
            break; // clean exit
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("[Worker] Shutting down gracefully.");
        break;
    }
    catch (Exception ex) when (ex is RpcException or HttpRequestException or IOException)
    {
        // Sanitized: this retry path sits directly on the gRPC/HTTP boundary, whose error
        // details can echo provisioned configuration (tokens, API keys) back to the worker.
        Console.Error.WriteLine(
            $"[Worker] Connection failed [{SafeExceptionLog.Describe(ex)}]. Retrying in {delay.TotalSeconds}s...");
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
        // All other exceptions are fatal — bad config, invalid credentials, etc. This also
        // covers a propagating teardown fault from the finally above.
        // Sanitized for the same reason: a provider client that rejects a provisioned
        // credential can quote that credential in its exception message.
        Console.Error.WriteLine($"[Worker] Fatal error [{SafeExceptionLog.Describe(ex)}]");
        return 1;
    }
}
return 0;
