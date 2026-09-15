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

// THE PROCESS'S ENVIRONMENT PROVENANCE, created ONCE here — OUTSIDE the attempt loop — and handed
// to EVERY attempt's WorkerService. The provisioning snapshot is what distinguishes an ORIGINAL
// operator override from a value the orchestrator provisioned, and that distinction belongs to the
// PROCESS, not to one connection attempt: this loop deliberately builds a FRESH service per attempt
// (no stale connection state leaks through retries), so if each attempt snapshotted for itself a
// later attempt would read the PREVIOUS attempt's server-provisioned values out of the environment
// and promote them to operator authority. Sharing this one object keeps the operator snapshot
// authoritative for the whole process while every attempt keeps its own identity, client, stream,
// provisioner and response provenance. It carries no locks: attempts are strictly sequential and
// the previous one is retired and drained before the next starts.
var provisioningEnvironment = new WorkerProvisioningEnvironment();

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
        capabilities: capabilities,
        provisioningEnvironment: provisioningEnvironment);

    // The run's OBSERVED outcome. It stays NULL unless the awaited RunAsync genuinely RETURNED:
    // a thrown failure leaves it null, so the classification catches below keep sole authority over
    // the retry/fatal control flow and no reconstructed or defaulted value can stand in for a real
    // returned outcome.
    WorkerRunOutcome? completedOutcome = null;

    try
    {
        try
        {
            // The returned outcome is the REAL result of the run — a rejected registration or an
            // accepted work stream that ended with the whole lifecycle teardown completing.
            // NOTHING else happens inside this covered region: the outcome is merely captured, so
            // no diagnostic of ours can ever be classified as a connection failure and retried.
            completedOutcome = await service.RunAsync(cts.Token);
        }
        finally
        {
            // Disposal propagates (by design). Running it here means any fault it raises is
            // caught and REDACTED by the handlers below instead of reaching the runtime.
            service.Dispose();
        }
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

    // ── RETURNED-OUTCOME HANDLING, OUTSIDE EVERY RETRY-GOVERNING CATCH ──────────────
    //
    // A THROWN connection failure was already classified and backed off above and left the outcome
    // null, so this attempt simply retries. Reaching the handling below therefore means RunAsync
    // genuinely RETURNED.
    if (completedOutcome is not { } outcome)
        continue;

    // From here on nothing can reach the catches above, so a throwing diagnostic can neither be
    // misclassified as a connection failure (creating a fresh attempt) nor alter the exit code.
    if (outcome == WorkerRunOutcome.WorkStreamEnded)
    {
        // Static and secret-free: the accepted work stream ended and this process is exiting.
        // Deliberately NOT a reconnect trigger — a returned outcome ALWAYS stops the loop, and the
        // write is best-effort so a closed/redirected stdout cannot change that.
        WriteBestEffort(Console.Out, "[Worker] Work stream ended; the worker is exiting.");
    }
    else if (outcome != WorkerRunOutcome.RegistrationRejected)
    {
        // An UNKNOWN/unexpected enum value must never silently retry and never silently exit. It is
        // an ordinary fatal InvalidOperationException, classified by the SAME sanitizer the fatal
        // catch above uses and exiting with the SAME fatal code — it is deliberately not raised into
        // those catches, since nothing here may re-enter the retry classification.
        var unexpectedOutcome = new InvalidOperationException(
            $"Unexpected worker run outcome: {(int)outcome}.");
        WriteBestEffort(
            Console.Error, $"[Worker] Fatal error [{SafeExceptionLog.Describe(unexpectedOutcome)}]");
        return 1;
    }

    // The registration-rejection diagnostic is emitted by the service itself. BOTH known outcomes
    // stop this loop with the same exit code as before: the attempt is over.
    break;
}
return 0;

// Writes ONE static, already-sanitized diagnostic, GUARDED so a degraded sink (a redirected and
// closed stdout/stderr raising IOException) can never escape to the runtime, can never be
// classified as a connection failure, and can never alter the process exit code.
static void WriteBestEffort(TextWriter writer, string message)
{
    try
    {
        writer.WriteLine(message);
    }
    catch (Exception)
    {
        // A diagnostic must never change the outcome it is merely reporting.
    }
}
