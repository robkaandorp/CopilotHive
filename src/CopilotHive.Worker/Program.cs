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
// to the PROCESS'S ONE WorkerService. The provisioning snapshot is what distinguishes an ORIGINAL
// operator override from a value the orchestrator provisioned, and that distinction belongs to the
// PROCESS, not to one connection attempt: the service keeps its identity, client, stream, provisioner
// and response provenance per attempt, but if each attempt snapshotted for itself a later attempt
// would read the PREVIOUS attempt's server-provisioned values out of the environment and promote them
// to operator authority. This one object therefore stays authoritative for the whole process. It
// carries no locks: attempts are strictly sequential and the previous one is retired and drained
// before the next starts.
var provisioningEnvironment = new WorkerProvisioningEnvironment();

// THE PROCESS'S ONE WORKER SERVICE, constructed ONCE here — OUTSIDE the attempt loop — and reused by
// EVERY sequential attempt for the whole process lifetime. The service's own Idle/Running/Disposed
// run guard is what enforces that rule: an overlapping RunAsync is refused, and this instance is
// never disposed between attempts — only ONCE, after the loop terminates, below. Each attempt still
// builds its OWN connection (identity, stream, client, provisioner, response provenance), so a
// retired WorkerConnection is never reused.
//
// It is deliberately NOT declared with `using`: that would place the compiler-generated Dispose()
// AFTER the exit-code decision below, so a throwing disposal — and runner disposal is deliberately
// fallible and propagating — would escape top level and be dumped by the runtime with its RAW message
// and stack, bypassing SafeExceptionLog. The ONE final disposal is performed explicitly, after loop
// termination, inside the sanitized handling below.
var service = new WorkerService(
    orchestratorUrl: orchestratorUrl,
    workerId: workerId,
    capabilities: capabilities,
    provisioningEnvironment: provisioningEnvironment);

// THE PROCESS EXIT CODE. It starts at success and is only ever raised to 1 by an explicit fatal
// classification. It is deliberately NOT returned early anywhere inside the loop: an early `return`
// would freeze the outcome BEFORE the ONE FINAL DISPOSAL below has run, so a failing disposal after an
// otherwise clean termination could no longer turn the process into a failure. Every loop exit
// therefore `break`s, and the ONE final exit-code return statement at the very end of the file
// decides the process outcome.
var exitCode = 0;

while (!cts.IsCancellationRequested)
{
    // THE ELIGIBLE OUTCOME — the ONLY value the post-region handling below may act on. It stays
    // NULL unless the awaited RunAsync genuinely RETURNED, so the classification catches keep sole
    // authority over the retry/fatal control flow and no reconstructed or defaulted value can stand
    // in for a real, fully completed attempt.
    WorkerRunOutcome? completedOutcome = null;

    try
    {
        // The returned outcome is the REAL result of the run — a rejected registration or an
        // accepted work stream that ended with the whole lifecycle teardown completing.
        // NOTHING else happens inside this covered region: the outcome is merely captured, so
        // no diagnostic of ours can ever be classified as a connection failure and retried.
        //
        // The attempt is awaited to COMPLETION here, so the SAME service is never entered by a
        // second run before this one has fully finished — including its lexical transport disposal.
        completedOutcome = await service.RunAsync(cts.Token);
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

        // EXPLICITLY END THIS ITERATION. A classified thrown failure must proceed to the NEXT
        // attempt on the SAME service — the previous run has already completed or faulted, so that
        // service is quiescent — and it may never fall through into the returned-outcome handling
        // below, whatever any local still holds.
        continue;
    }
    catch (Exception ex)
    {
        // All other exceptions are fatal — bad config, invalid credentials, etc.
        // Sanitized for the same reason: a provider client that rejects a provisioned
        // credential can quote that credential in its exception message.
        Console.Error.WriteLine($"[Worker] Fatal error [{SafeExceptionLog.Describe(ex)}]");

        // RECORD THE CLASSIFICATION AND LEAVE THE LOOP. Never `return` here: the ONE final disposal
        // below must still run, and the exit code is decided only AFTER it.
        exitCode = 1;
        break;
    }

    // ── RETURNED-OUTCOME HANDLING, OUTSIDE EVERY RETRY-GOVERNING CATCH ──────────────
    //
    // DEFENSE IN DEPTH. Every catch above already ends its own iteration (break / continue), and an
    // ineligible attempt never assigns the value, so this guard is unreachable in
    // the corrected flow — it stays as the safety net that keeps a non-returned attempt from ever
    // being handled as a completed one.
    if (completedOutcome is not { } outcome)
        continue;

    // From here on nothing can reach the catches above, so a throwing diagnostic can neither be
    // misclassified as a connection failure (creating a fresh attempt) nor alter the exit code.
    if (outcome == WorkerRunOutcome.WorkStreamEnded)
    {
        // THE RECONNECT TRIGGER. A returned outcome means the attempt finished cleanly — the
        // accepted work stream ended and the whole lifecycle teardown completed — which is exactly
        // what an orchestrator restart looks like from this side. The ATTEMPT is over; the PROCESS
        // is not: the SAME service is entered again with the SAME bounded backoff the classified
        // connection-failure branch above uses, so a worker holding a CARRIED assignment registers
        // again (the service still owns that assignment, and the next run claims it through
        // current_task_id) and can be adopted instead of losing the task.
        //
        // Static and secret-free, and best-effort so a closed/redirected stdout cannot change that.
        WriteBestEffort(
            Console.Out,
            $"[Worker] Work stream ended; reconnecting in {delay.TotalSeconds}s...");

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

        // EXPLICITLY END THIS ITERATION, exactly as the classified-throw branch does: the next
        // attempt starts on the SAME service, and this path may never fall through into the
        // remaining outcome handling below.
        continue;
    }

    if (outcome != WorkerRunOutcome.RegistrationRejected)
    {
        // An UNKNOWN/unexpected enum value must never silently retry and never silently exit. It is
        // an ordinary fatal InvalidOperationException, classified by the SAME sanitizer the fatal
        // catch above uses and exiting with the SAME fatal code — it is deliberately not raised into
        // those catches, since nothing here may re-enter the retry classification.
        var unexpectedOutcome = new InvalidOperationException(
            $"Unexpected worker run outcome: {(int)outcome}.");
        WriteBestEffort(
            Console.Error, $"[Worker] Fatal error [{SafeExceptionLog.Describe(unexpectedOutcome)}]");
        exitCode = 1;
        break;
    }

    // The registration-rejection diagnostic is emitted by the service itself. A REJECTED
    // registration is not retryable: this loop ends with the same exit code as before.
    break;
}

// ── THE CARRIED-ASSIGNMENT DRAIN, BEFORE THE ONE FINAL DISPOSAL ───────────────────
//
// FIRST cancel and drain any RETAINED assignment — including the carried delivery continuation it
// owns — so no live producer survives the process's own teardown and no carried task is left
// half-delivered. Only AFTER that does the ONE final service disposal below run.
//
// Each failure is reported SEPARATELY, in the same sanitized form the other fatal paths use, turns
// the process into a failure, and is NEVER retried: process-level teardown is not an attempt, so
// none of these faults may be classified as a connection failure. A drain fault must not skip the
// disposal, and the disposal must not be skipped merely because the drain threw — the two blocks
// are therefore independent, and the exit code is decided only after both.
try
{
    await service.DrainCarriedAssignmentAsync();
}
catch (Exception ex)
{
    // Sanitized for the same reason as the attempt paths: this boundary reaches the assignment's
    // transport and the agent runner, whose errors can quote provisioned configuration.
    WriteBestEffort(Console.Error, $"[Worker] Fatal error [{SafeExceptionLog.Describe(ex)}]");
    exitCode = 1;
}

// ── THE ONE FINAL SERVICE DISPOSAL, AFTER LOOP TERMINATION ────────────────────────
//
// FINAL, AND NEVER RETRIED. Only an ATTEMPT error may retry; process-level teardown must not, so this
// disposal sits OUTSIDE every retry-governing catch above and is deliberately NOT classified the way
// an attempt is: an RpcException, an HttpRequestException, an IOException and an
// OperationCanceledException raised here are all DISPOSAL faults, not connection failures, so none of
// them creates another attempt and none of them is swallowed as a graceful shutdown. The disposal is
// attempted EXACTLY ONCE, whatever it throws.
//
// The service's own guard already makes a repeat call a no-op and rejects a run afterwards, and the
// runner disposal is deliberately fallible and propagating: a fault here is reported SANITIZED
// (classification only — never the raw exception message or stack, which can echo a provisioned
// credential) and turns an otherwise normal or cancelled termination into a failure.
try
{
    service.Dispose();
}
catch (Exception ex)
{
    // Sanitized for the same reason as the attempt paths: this boundary reaches the provider client,
    // whose errors can quote provisioned configuration.
    WriteBestEffort(Console.Error, $"[Worker] Fatal error [{SafeExceptionLog.Describe(ex)}]");
    exitCode = 1;
}

// THE EXIT CODE IS DECIDED HERE — after the one final disposal — so no earlier `return` can freeze a
// success that teardown then failed to deliver.
return exitCode;

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
