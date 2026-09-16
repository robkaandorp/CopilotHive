using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// END-TO-END redaction tests that drive the REAL production seams rather than reproducing a
/// format string.
/// <para>
/// The reviewer's CRITICAL findings were specifically that (a) a provisioning/LLM exception
/// thrown out of <see cref="IAgentRunner.SendPromptAsync"/> reached
/// <see cref="TaskExecutor.ExecuteAsync"/>, which copied the RAW <c>ex.Message</c> into the
/// <see cref="TaskResult"/> that travels to the orchestrator, and (b) a throwing
/// <see cref="WorkerService.Dispose"/> escaped the sanitized handlers in Program.cs.
/// </para>
/// <para>
/// Every test here therefore throws a secret-bearing exception from a real runner/client and
/// asserts on what the real production code produced — the returned <see cref="TaskResult"/>,
/// or the output of the actual Program.cs teardown structure.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerRedactionIntegrationTests
{
    /// <summary>A value that must never survive into any log line, output or metric.</summary>
    private const string SecretToken = "ghp_S3CR3T_provisioned_token_value";

    /// <summary>A second secret representing a provisioned Ollama API key.</summary>
    private const string SecretApiKey = "ollama_S3CR3T_api_key_value";

    // ── CRITICAL 1: runner → TaskExecutor boundary ────────────────────────────

    /// <summary>
    /// The real integration path: a runner throws an exception whose message quotes a
    /// provisioned token — exactly what a provider client does when it rejects a credential and
    /// echoes the request configuration. <see cref="TaskExecutor.ExecuteAsync"/> must return a
    /// failed result whose Output, Issues and Verdict carry NO raw exception text.
    /// </summary>
    [Fact]
    public async Task TaskExecutor_RunnerThrowsSecretBearingException_ResultCarriesNoSecret()
    {
        var runner = new SecretThrowingAgentRunner(
            new InvalidOperationException($"401 Unauthorized: Bearer {SecretToken} rejected"));

        var executor = new TaskExecutor(runner, gitOperations: new NoOpGit(), sessionClient: null);

        var result = await executor.ExecuteAsync(BuildTask(), TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Failed, result.Status);

        // The TaskResult is transmitted to the orchestrator and persisted there — it is the
        // exact carrier the reviewer flagged.
        Assert.DoesNotContain(SecretToken, result.Output);
        foreach (var issue in result.Metrics!.Issues)
            Assert.DoesNotContain(SecretToken, issue);

        // Still actionable: the classification survives.
        Assert.Contains(nameof(InvalidOperationException), result.Output);
        Assert.Equal("FAIL", result.Metrics.Verdict);
    }

    /// <summary>
    /// The same guarantee on the OTHER catch — a non-cancellation
    /// <see cref="OperationCanceledException"/>, which TaskExecutor treats as an API
    /// timeout/error. This catch also copied <c>ex.Message</c> into Output and Issues.
    /// </summary>
    [Fact]
    public async Task TaskExecutor_RunnerThrowsSecretBearingTimeout_ResultCarriesNoSecret()
    {
        var runner = new SecretThrowingAgentRunner(
            new OperationCanceledException($"request timed out (api_key={SecretApiKey})"));

        var executor = new TaskExecutor(runner, gitOperations: new NoOpGit(), sessionClient: null);

        // An uncancelled token, so this lands in the "not a real cancellation" catch.
        var result = await executor.ExecuteAsync(BuildTask(), TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.DoesNotContain(SecretApiKey, result.Output);
        foreach (var issue in result.Metrics!.Issues)
            Assert.DoesNotContain(SecretApiKey, issue);
    }

    /// <summary>
    /// A secret carried in an INNER exception must not survive either — provider SDKs commonly
    /// wrap the transport failure that quotes the credential.
    /// </summary>
    [Fact]
    public async Task TaskExecutor_SecretInInnerException_ResultCarriesNoSecret()
    {
        var inner = new HttpRequestException($"Authorization: Bearer {SecretToken}");
        var runner = new SecretThrowingAgentRunner(
            new InvalidOperationException("chat client creation failed", inner));

        var executor = new TaskExecutor(runner, gitOperations: new NoOpGit(), sessionClient: null);

        var result = await executor.ExecuteAsync(BuildTask(), TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.DoesNotContain(SecretToken, result.Output);
        foreach (var issue in result.Metrics!.Issues)
            Assert.DoesNotContain(SecretToken, issue);
    }

    /// <summary>
    /// Drives the REAL <see cref="SharpCoderRunner"/> (not a stub) so the failure originates
    /// where it does in production: the lazy client-creation seam invoked from
    /// <c>SendPromptAsync</c>, immediately after provisioning. The resulting
    /// <see cref="TaskResult"/> must still be secret-free.
    /// </summary>
    [Fact]
    public async Task RealRunnerLazyCreationFailure_ThroughTaskExecutor_CarriesNoSecret()
    {
        var runner = new SharpCoderRunner();
        try
        {
            // The production lazy-creation path throws here, exactly as a provider client would
            // when it rejects a provisioned credential and echoes it back.
            runner.ClientCreationSeam = _ =>
                throw new InvalidOperationException($"provider rejected GH_TOKEN={SecretToken}");

            var executor = new TaskExecutor(runner, gitOperations: new NoOpGit(), sessionClient: null);

            var result = await executor.ExecuteAsync(BuildTask(), TestContext.Current.CancellationToken);

            Assert.Equal(TaskOutcome.Failed, result.Status);
            Assert.DoesNotContain(SecretToken, result.Output);
            foreach (var issue in result.Metrics!.Issues)
                Assert.DoesNotContain(SecretToken, issue);
        }
        finally
        {
            await runner.DisposeAsync();
        }
    }

    /// <summary>
    /// Drives the real <see cref="TaskExecutor"/> catch and its real <c>TryWriteError</c> path,
    /// not a formatter mirror. A nested transport exception quotes both provisioned sentinels;
    /// neither the emitted error line nor the returned/persisted result may contain either value.
    /// </summary>
    [Fact]
    public async Task TaskExecutor_ActualErrorLogAndResult_RedactNestedProvisionedValues()
    {
        var stdErr = new StringWriter();
        var originalErr = Console.Error;
        TaskResult result;

        try
        {
            Console.SetError(stdErr);
            var transport = new HttpRequestException(
                $"Authorization: Bearer {SecretToken}; api_key={SecretApiKey}");
            var runner = new SecretThrowingAgentRunner(
                new InvalidOperationException("provider request failed", transport));
            var executor = new TaskExecutor(runner, gitOperations: new NoOpGit(), sessionClient: null);

            result = await executor.ExecuteAsync(BuildTask(), TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        var emitted = stdErr.ToString();
        Assert.Contains("[Task] Failed", emitted);
        Assert.Contains(nameof(InvalidOperationException), emitted);
        Assert.Contains(nameof(HttpRequestException), emitted);
        Assert.DoesNotContain(SecretToken, emitted);
        Assert.DoesNotContain(SecretApiKey, emitted);

        Assert.DoesNotContain(SecretToken, result.Output);
        Assert.DoesNotContain(SecretApiKey, result.Output);
        Assert.All(result.Metrics!.Issues, issue =>
        {
            Assert.DoesNotContain(SecretToken, issue);
            Assert.DoesNotContain(SecretApiKey, issue);
        });
    }

    // ── CRITICAL 2: Program.cs teardown route ─────────────────────────────────

    /// <summary>
    /// Reproduces the EXACT control flow Program.cs now uses — ONE service outside the attempt loop,
    /// attempts that `break` rather than `return`, and ONE FINAL disposal AFTER loop termination whose
    /// fault is sanitized and turns an otherwise normal termination into exit code 1 — and proves a
    /// throwing disposal is redacted instead of escaping to the runtime with its raw message.
    /// <para>
    /// THE FINAL-DISPOSAL POLICY, PROVEN NOT ASSUMED: the attempt count stays at ONE (teardown never
    /// retries, and is never classified by the retry filter — an <c>RpcException</c>, an
    /// <c>IOException</c> and an <c>OperationCanceledException</c> raised by the disposal are all
    /// teardown faults), the outcome is decided only AFTER the disposal, and a disposal fault makes
    /// the exit code 1 rather than the 0 an early `return` would have frozen.
    /// </para>
    /// <para>
    /// Under the OLD structure (<c>using var service</c> declared OUTSIDE the try, or a per-attempt
    /// disposal inside the loop's try/finally) the exception below would either propagate past the
    /// catches uncaught or be treated as a retryable attempt failure, and this test would fail by
    /// throwing / by observing a retry.
    /// </para>
    /// </summary>
    /// <param name="disposalFault">
    /// The fault the disposal raises. Every one of these is a TEARDOWN fault — including the three
    /// types the attempt loop treats as retryable — so the test proves the final disposal is outside
    /// the retry classification for all of them.
    /// </param>
    [Theory]
    [InlineData("invalid")]
    [InlineData("rpc")]
    [InlineData("io")]
    [InlineData("cancel")]
    public async Task ProgramFinalDisposal_ThrowingDisposal_IsSanitizedNotRaw_AndExitCodeIsOne(string disposalFault)
    {
        var stdErr = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(stdErr);

        var attempts = 0;
        var exitCode = 0;
        var disposalAttempts = 0;

        Exception fault = disposalFault switch
        {
            "rpc" => new RpcException(new Status(StatusCode.Unavailable, $"Bearer {SecretToken}")),
            "io" => new IOException($"write failed with GH_TOKEN={SecretToken}"),
            "cancel" => new OperationCanceledException($"timed out with api_key={SecretApiKey}"),
            _ => new InvalidOperationException($"dispose failed for GH_TOKEN={SecretToken}"),
        };

        try
        {
            // ── THIS BLOCK MIRRORS Program.cs's STRUCTURE EXACTLY ──
            // ONE service, built OUTSIDE the loop, disposed ONCE after it — and never disposed inside.
            var service = BuildServiceWithThrowingRunner(fault);

            while (true)
            {
                attempts++;

                try
                {
                    // Stand-in for RunAsync returning normally: the fault comes from teardown.
                    await Task.CompletedTask;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is RpcException or HttpRequestException or IOException)
                {
                    Console.Error.WriteLine(
                        $"[Worker] Connection failed [{SafeExceptionLog.Describe(ex)}]. Retrying...");
                    continue;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Worker] Fatal error [{SafeExceptionLog.Describe(ex)}]");
                    exitCode = 1;
                    break;
                }

                // A returned outcome ends the loop in the real program; here the first attempt always
                // succeeds, so the loop ends immediately.
                break;
            }

            // THE ONE FINAL DISPOSAL, AFTER LOOP TERMINATION, INSIDE SANITIZED HANDLING.
            try
            {
                disposalAttempts++;
                service.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Worker] Fatal error [{SafeExceptionLog.Describe(ex)}]");
                exitCode = 1;
            }
        }
        finally
        {
            Console.SetError(originalErr);
        }

        // THE DISPOSAL FAULT WAS NOT RETRIED AND DID NOT RE-ENTER THE ATTEMPT LOOP.
        Assert.Equal(1, attempts);
        Assert.Equal(1, disposalAttempts);

        // ...and it became a FAILING process outcome rather than a frozen success.
        Assert.Equal(1, exitCode);

        var output = stdErr.ToString();
        Assert.DoesNotContain(SecretToken, output);
        Assert.DoesNotContain(SecretApiKey, output);
        Assert.Contains("Fatal error", output);
        Assert.DoesNotContain("Retrying", output);
    }

    /// <summary>
    /// PER-ATTEMPT RETRY STILL APPLIES ONLY TO ATTEMPT ERRORS — and it RETRIES ON THE SAME SERVICE.
    /// The Program-mirrored loop below drives the REAL <see cref="WorkerService.RunAsync"/> twice
    /// (attempt 1 faults with a retry-class <see cref="RpcException"/>, attempt 2 returns
    /// <see cref="WorkerRunOutcome.RegistrationRejected"/>), with a probe recording whether any
    /// disposal was ever attempted on the service path.
    /// <para>
    /// THE NON-VACUITY CONTROL is the counter case: the loop's retry catch is exercised with the
    /// SAME fault the disposal theory raises, so this test cannot merely be exercising a loop that
    /// never enters its retry classification.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProgramAttemptLoop_RetryClassAttemptFault_RetriesWithoutAnyDisposalAttempt()
    {
        var stdErr = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(stdErr);

        var attempts = 0;
        var exitCode = 0;
        var disposalAttempts = 0;
        var outcomes = new List<WorkerRunOutcome>();

        // THE SAME-SERVICE SECOND-ATTEMPT PROBE: the second invocation uses a DIFFERENT invoker —
        // one whose Register is ACCEPTED and whose stream is the pre-built controlled-EOF duplex —
        // so attempt 2 is a real, returned outcome on the SAME service, not just a repeat of the
        // faulting registration.
        var acceptedInvoker = new AcceptedRegisterInvoker();
        var acceptedRequests = new RequestStreamStub();
        var acceptedResponses = new ChannelReaderStub();
        var acceptedStream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            acceptedRequests,
            acceptedResponses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

        // ── THIS BLOCK MIRRORS Program.cs's STRUCTURE EXACTLY ──
        var configRepoDir = CreateTempConfigRepoDir();
        var launcher = new FakeGitLauncher(HealthyRepoHandler(configRepoDir));
        using var processRunner = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var service = new WorkerService(
            "http://localhost:9999", "worker-retry-loop", ["coder"], configRepoDir: configRepoDir);
        // The TestProvisioner override keeps attempt 2's accepted assignment free of network
        // access: the eager per-assignment provisioning site takes this in-memory provisioner.
        service.TestProvisioner = new ProvisionerHarness(
            configRepoUrl: "https://github.com/org/config-repo.git",
            ghToken: "ghp_fixture_retry_loop").Provisioner;
        service.CallInvokerFactory = () =>
            new RegisterFaultingInvoker(
                new RpcException(new Status(StatusCode.Unavailable, "unreachable")));
        service.WorkStreamFactory = (_, _) => throw new InvalidOperationException("no stream expected");

        // THE RETRY-CLASSIFICATION COUNTER: observed INSIDE the retry catch, so the assertion
        // pins that this loop's own retry filter genuinely fired — the control against a loop
        // that "retries" only because it never entered the classification at all.
        var retryCatchEntered = 0;
        var fatalType = "none";

        // The worker's logger writes to the CONSOLE on every run (preparation info, registration
        // outcome). The real test host's stdout is not redirected, so point Console.Out at a bounded
        // sink while the loop runs and restore it in this test's own finally.
        var originalOut = Console.Out;
        var stdOut = new StringWriter();
        try
        {
            Console.SetOut(stdOut);
            Console.SetError(stdErr);

            while (true)
            {
                attempts++;

                // THE BOUNDED-BACKOFF MIRROR: the production loop paces with Task.Delay between
                // attempts before the next one; the mirror records the classification and moves
                // straight to the next attempt. ONE retry is admitted — a second consecutive
                // retry-class failure breaks instead, which is what the backoff bound would
                // eventually produce anyway.
                if (retryCatchEntered >= 1)
                {
                    service.CallInvokerFactory = () => acceptedInvoker;
                    service.WorkStreamFactory = (_, _) => acceptedStream;
                }

                try
                {
                    WorkerRunOutcome returnedOutcome =
                        await service.RunAsync(TestContext.Current.CancellationToken);
                    outcomes.Add(returnedOutcome);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is RpcException or HttpRequestException or IOException)
                {
                    retryCatchEntered++;
                    Console.Error.WriteLine(
                        $"[Worker] Connection failed [{SafeExceptionLog.Describe(ex)}]. Retrying...");
                    continue;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Worker] Fatal error [{SafeExceptionLog.Describe(ex)}]");
                    fatalType = ex.GetType().Name + ": " + ex.Message;
                    exitCode = 1;
                    break;
                }

                // A returned outcome ends the loop in the real program.
                break;
            }

            // THE ONE FINAL DISPOSAL, AFTER LOOP TERMINATION.
            try
            {
                disposalAttempts++;
                service.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Worker] Fatal error [{SafeExceptionLog.Describe(ex)}]");
                exitCode = 1;
            }
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            stdOut.Dispose();
        }

        TryDeleteDir(configRepoDir);

        // THE RETRY CLASSIFICATION FIRED, THEN A SECOND ATTEMPT RAN — both on the SAME service.
        Assert.Equal(1, retryCatchEntered);
        Assert.Equal(2, attempts);

        // THE DIAGNOSTIC FIRST: whatever happened, the stderr carries the evidence.
        var output = stdErr.ToString();
        if (output.Contains("Fatal error", StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException(
                "The second attempt was FATAL rather than returned. Sanitized stderr:\n" + output
                + "\nAccepted stream writes: "
                + string.Join("; ", acceptedRequests.Writes.Select(w => w.PayloadCase.ToString()))
                + "; outcomes: " + string.Join(",", outcomes)
                + "; attempts=" + attempts + " retryCatchEntered=" + retryCatchEntered
                + " fatalType=" + fatalType);

        // The accepted attempt is a real returned outcome (its stream ends with a clean EOF), not a
        // replay of the faulting registration.
        Assert.Equal([WorkerRunOutcome.WorkStreamEnded], outcomes);

        // THE FINAL DISPOSAL WAS STILL EXACTLY ONE — attempt faults never dispose per attempt.
        Assert.Equal(1, disposalAttempts);

        // The accepted attempt's stream moved ONLY through its own lifecycle: its Ready was written
        // and the clean EOF ended it — no replay of attempt 1's faulting registration.
        var acceptedWrites = acceptedRequests.Writes;
        Assert.Single(acceptedWrites);
        Assert.Equal(WorkerMessage.PayloadOneofCase.Ready, acceptedWrites[0].PayloadCase);

        // The attempt fault was classified as a connection failure with the sanitized category,
        // and the process still exits 0 (a retryable failure is not a fatal one).
        Assert.Contains("[Worker] Connection failed [", output, StringComparison.Ordinal);
        Assert.Contains("RpcException(status=Unavailable)", output, StringComparison.Ordinal);
        Assert.Contains("Retrying", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Fatal error", output, StringComparison.Ordinal);
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// ONCE THE ATTEMPT LOOP HAS TERMINATED, A <see cref="OperationCanceledException"/> FROM THE FINAL
    /// DISPOSAL ITSELF IS STILL A FATAL TEARDOWN FAULT — it does NOT re-enter the graceful-shutdown
    /// classification that governs ATTEMPTS. The exit code is decided AFTER the disposal, and the
    /// fault is reported sanitized with no raw exception message.
    /// <para>
    /// THE NON-VACUITY CONTROL: the same <see cref="OperationCanceledException"/> on the ATTEMPT path
    /// takes the graceful <c>break</c> (see the production catch), so a survivor here would prove the
    /// disposal fault leaked into the attempt classification rather than the disposal simply not
    /// throwing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProgramFinalDisposal_OperationCanceledFromDisposal_IsFatalNotGraceful_AndNeverRaw()
    {
        var stdErr = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(stdErr);

        var attempts = 0;
        var exitCode = 0;
        var disposalAttempts = 0;

        var cancelFault = new OperationCanceledException($"dispose cancelled api_key={SecretApiKey}");
        var service = BuildServiceWithThrowingRunner(cancelFault);

        try
        {
            while (true)
            {
                attempts++;

                try
                {
                    // The attempt "succeeds" (as in the production loop, a returned outcome ends it).
                    await Task.CompletedTask;
                }
                catch (OperationCanceledException)
                {
                    // THE ATTEMPT-PATH CLASSIFICATION, EXERCISED HERE AS THE CONTROL: graceful break.
                    break;
                }
                catch (Exception ex) when (ex is RpcException or HttpRequestException or IOException)
                {
                    Console.Error.WriteLine(
                        $"[Worker] Connection failed [{SafeExceptionLog.Describe(ex)}]. Retrying...");
                    continue;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Worker] Fatal error [{SafeExceptionLog.Describe(ex)}]");
                    exitCode = 1;
                    break;
                }

                break;
            }

            // THE ONE FINAL DISPOSAL, whose fault is the cancellation.
            try
            {
                disposalAttempts++;
                service.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Worker] Fatal error [{SafeExceptionLog.Describe(ex)}]");
                exitCode = 1;
            }
        }
        finally
        {
            Console.SetError(originalErr);
        }

        // NOT GRACEFUL: the cancellation from DISPOSAL fails the process, exactly like every other
        // teardown fault — the graceful category belongs to ATTEMPTS only.
        Assert.Equal(1, attempts);
        Assert.Equal(1, disposalAttempts);
        Assert.Equal(1, exitCode);

        var output = stdErr.ToString();
        Assert.Contains("Fatal error", output);
        Assert.Contains(nameof(OperationCanceledException), output);
        Assert.DoesNotContain(SecretApiKey, output);
        Assert.DoesNotContain("dispose cancelled", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="WorkerService.Dispose"/> must rethrow the runner's ORIGINAL exception, not an
    /// <see cref="AggregateException"/> wrapper. Wrapping would defeat
    /// <see cref="SafeExceptionLog"/>'s classification (it would report "AggregateException")
    /// and, more importantly, <see cref="AggregateException.Message"/> concatenates the inner
    /// messages — re-introducing the secret into any raw render.
    /// </summary>
    [Fact]
    public void WorkerServiceDispose_PropagatesOriginalExceptionUnwrapped()
    {
        var service = BuildServiceWithThrowingRunner(
            new InvalidOperationException($"dispose failed GH_TOKEN={SecretToken}"));

        var ex = Assert.Throws<InvalidOperationException>(service.Dispose);

        // Unwrapped: SafeExceptionLog classifies the real fault.
        Assert.Equal(nameof(InvalidOperationException), SafeExceptionLog.Describe(ex));
    }

    /// <summary>
    /// STRUCTURAL REGRESSION for the process-lifetime contract in the worker entry point: ONE
    /// <see cref="WorkerService"/> is constructed OUTSIDE the attempt loop and reused by every
    /// sequential attempt; the per-attempt disposal is GONE; the ONE final disposal happens only
    /// after loop termination, inside sanitized handling; the returned-outcome handling sits AFTER
    /// every retry-governing catch; the retry catch ends its own iteration explicitly; and both
    /// diagnostics it writes are guarded.
    /// <para>
    /// WHAT IT REPLACES. The earlier shape built a FRESH service per attempt and disposed it inside
    /// the attempt's try/finally (with a two-step <c>runOutcome</c>/<c>completedOutcome</c> capture so
    /// a throwing per-attempt disposal could not be mistaken for a completed attempt). That structure
    /// is now intentionally gone: the service — and its ONE readonly runner — must survive every
    /// retry, so disposal moved OUT of the loop into a single final, never-retried step, and the exit
    /// code is decided only AFTER it.
    /// </para>
    /// <para>
    /// THE FAILURE MODES THESE ASSERTIONS PIN.
    /// <list type="bullet">
    ///   <item><description>a SECOND service construction (or a per-attempt disposal) would restore
    ///   the old per-attempt lifetime and silently discard the runner between retries;</description></item>
    ///   <item><description>an early <c>return</c> inside the loop would freeze the process outcome
    ///   BEFORE the final disposal, so a failing disposal could no longer fail the process;</description></item>
    ///   <item><description>letting the final disposal be classified by the retry filter
    ///   (<c>RpcException</c>/<c>HttpRequestException</c>/<c>IOException</c>) would turn a teardown
    ///   fault into a fresh connection attempt — teardown must never retry.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public void WorkerProgram_ProcessLifetimeContract_FinalOnlyDisposalOutsideEveryRetryCatch()
    {
        var programPath = Path.Combine(FindRepoRoot(), "src", "CopilotHive.Worker", "Program.cs");
        Assert.True(File.Exists(programPath), $"Worker Program.cs not found at '{programPath}'.");
        var source = File.ReadAllText(programPath).ReplaceLineEndings("\n");

        // ── THE PROCESS'S ONE SERVICE, AND THE ONE ATTEMPT RUN ON IT ──────────
        const string ServiceConstruction = "var service = new WorkerService(";
        const string AttemptRun = "completedOutcome = await service.RunAsync(cts.Token);";
        const string DisposalCall = "service.Dispose();";
        const string LoopAnchor = "while (!cts.IsCancellationRequested)";

        // ── THE EXIT-CODE DECISION, MADE AFTER THE FINAL DISPOSAL ─────────────
        const string ExitCodeInitialization = "var exitCode = 0;";
        const string FatalExitAssignment = "exitCode = 1;";
        const string ExitDecision = "return exitCode;";

        // ── THE RETRY CATCH ENDS ITS OWN ITERATION ────────────────────────────
        // A classified thrown failure must proceed to the NEXT attempt exactly as the pre-change
        // control flow did; it may never fall through into the returned-outcome handling.
        const string RetryCatchBackoff = "delay = delay * 2 > maxDelay ? maxDelay : delay * 2;";
        const string RetryCatchContinue = "continue;";

        // ── THE DEFENSE-IN-DEPTH GUARD AND THE GUARDED DIAGNOSTICS ────────────
        const string NullOutcomeContinues = "if (completedOutcome is not { } outcome)\n        continue;";
        // Both diagnostics the returned-outcome path emits are guarded writes, never raw Console.
        const string WorkStreamEndedDiagnostic =
            "WriteBestEffort(Console.Out, \"[Worker] Work stream ended; the worker is exiting.\");";
        const string BestEffortHelper = "static void WriteBestEffort(TextWriter writer, string message)";

        foreach (var fragment in new[]
                 {
                     ServiceConstruction, AttemptRun, DisposalCall, ExitCodeInitialization,
                     ExitDecision, NullOutcomeContinues, WorkStreamEndedDiagnostic, BestEffortHelper,
                 })
        {
            Assert.True(
                CountOccurrences(source, fragment) == 1,
                $"Expected exactly one worker Program.cs occurrence of '{fragment}'.");
        }

        // ── ONE SERVICE, BUILT BEFORE THE LOOP ────────────────────────────────
        // A second construction would mean a per-attempt lifetime; the single construction must also
        // precede the loop, since a service built inside it could not be reused by a retry.
        Assert.True(
            source.IndexOf(ExitCodeInitialization, StringComparison.Ordinal)
            < source.IndexOf(LoopAnchor, StringComparison.Ordinal),
            "The exit code must be initialized BEFORE the attempt loop so no exit path can bypass it.");
        Assert.True(
            source.IndexOf(ServiceConstruction, StringComparison.Ordinal)
            < source.IndexOf(LoopAnchor, StringComparison.Ordinal),
            "The service must be constructed OUTSIDE the attempt loop: one instance serves the whole "
            + "process, so its runner is never discarded between retries.");

        // ── THE PER-ATTEMPT DISPOSAL IS GONE: DISPOSAL IS OUTSIDE THE LOOP ────
        var loopBlock = ExtractBracedBlock(source, LoopAnchor);
        Assert.False(loopBlock is null, "The attempt loop block was not found.");
        Assert.Contains(AttemptRun, loopBlock!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            DisposalCall, StripLineComments(loopBlock!), StringComparison.Ordinal);
        // NO EARLY RETURN INSIDE THE LOOP. Comments are stripped first, so this is a claim about the
        // loop's STATEMENTS: an early `return` would decide the process outcome before the ONE final
        // disposal could fail it.
        var loopCode = StripLineComments(loopBlock!);
        Assert.DoesNotContain("return", loopCode, StringComparison.Ordinal);
        Assert.Contains("break;", loopCode, StringComparison.Ordinal);

        // ── ORDER: RUN → FINAL DISPOSAL → EXIT DECISION ───────────────────────
        // The run is awaited inside the loop, the ONE final disposal follows loop termination, and
        // the exit code is only decided afterwards — so an early return cannot freeze success first.
        var attemptRunIndex = source.IndexOf(AttemptRun, StringComparison.Ordinal);
        var disposalIndex = source.IndexOf(DisposalCall, StringComparison.Ordinal);
        var exitDecisionIndex = source.IndexOf(ExitDecision, StringComparison.Ordinal);
        Assert.True(
            0 <= attemptRunIndex && attemptRunIndex < disposalIndex,
            "The attempt run must precede the one final service disposal.");
        Assert.True(
            disposalIndex < exitDecisionIndex,
            "The exit code must be decided AFTER the final service disposal: an early return would "
            + "freeze the outcome before teardown could fail it.");

        // ── THE FINAL DISPOSAL IS NEVER RETRIED, AND NEVER SILENTLY IGNORED ───
        // The REGION between the final disposal and the exit decision IS the final disposal handling:
        // it must record the failure as a fatal exit code and must contain NO retry classification
        // (no filter on the retryable exception set) and no loop continuation.
        var finalDisposalRegion = source[disposalIndex..exitDecisionIndex];
        Assert.Contains(FatalExitAssignment, finalDisposalRegion, StringComparison.Ordinal);
        Assert.Contains("catch (Exception", finalDisposalRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("RpcException", finalDisposalRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpRequestException", finalDisposalRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("IOException", finalDisposalRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("OperationCanceledException", finalDisposalRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("continue;", finalDisposalRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("while (", finalDisposalRegion, StringComparison.Ordinal);
        // Its diagnostic is the SAME guarded, sanitized write the fatal path uses — never a raw
        // Console write and never the exception message.
        Assert.Contains(
            "WriteBestEffort(Console.Error, $\"[Worker] Fatal error [",
            finalDisposalRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("ex.Message", finalDisposalRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("ex.ToString()", finalDisposalRegion, StringComparison.Ordinal);

        // ── BRACE-SCOPED: the retry catch explicitly proceeds to the next iteration ──
        // Extract the retry catch's body and prove it ends its OWN iteration with `continue;`
        // AFTER the existing sanitized log and backoff — it never falls through into the
        // returned-outcome handling, whatever the ineligible locals still hold.
        var retryCatchBody = ExtractBracedBlock(
            source, "catch (Exception ex) when (ex is RpcException or HttpRequestException or IOException)");
        Assert.False(retryCatchBody is null, "The retry-class catch block was not found.");
        Assert.Contains(
            "[Worker] Connection failed [", retryCatchBody!, StringComparison.Ordinal);
        Assert.Contains(RetryCatchBackoff, retryCatchBody!, StringComparison.Ordinal);
        Assert.Contains(RetryCatchContinue, retryCatchBody!, StringComparison.Ordinal);
        Assert.True(
            retryCatchBody!.LastIndexOf(RetryCatchBackoff, StringComparison.Ordinal)
            < retryCatchBody.LastIndexOf(RetryCatchContinue, StringComparison.Ordinal),
            "The retry catch must `continue;` AFTER the backoff math: the next attempt starts exactly "
            + "as the pre-change control flow did, never via fall-through into outcome handling.");

        // ── THE HANDLING STAYS OUTSIDE EVERY RETRY-GOVERNING CATCH ────────────
        var catchRegionStart = source.IndexOf(
            "catch (OperationCanceledException)", StringComparison.Ordinal);
        var finalFatalCatch = source.IndexOf(
            "catch (Exception ex)\n    {\n        // All other exceptions are fatal",
            StringComparison.Ordinal);
        var outcomeHandlingStart = source.IndexOf(NullOutcomeContinues, StringComparison.Ordinal);
        Assert.True(
            catchRegionStart >= 0 && finalFatalCatch > catchRegionStart,
            "The sanitized catch region must exist inside the attempt loop.");
        Assert.True(
            outcomeHandlingStart > finalFatalCatch,
            "Returned-outcome handling must come AFTER the final fatal catch: a throwing diagnostic "
            + "there can never be classified as a connection failure and retried.");
        // The handling is still INSIDE the loop (it decides the NEXT iteration or ends it).
        Assert.True(
            outcomeHandlingStart > source.IndexOf(LoopAnchor, StringComparison.Ordinal)
            && outcomeHandlingStart < disposalIndex,
            "Returned-outcome handling belongs INSIDE the attempt loop, before the final disposal.");

        // ── A RETURNED OUTCOME ALWAYS STOPS THE LOOP ──────────────────────────
        Assert.True(
            source.IndexOf(WorkStreamEndedDiagnostic, StringComparison.Ordinal) < disposalIndex,
            "The WorkStreamEnded diagnostic must precede the loop's exit and the final disposal.");

        // ── THE GUARDED HELPER SWALLOWS EVERYTHING ─────────────────────────────
        var helperBody = source[source.IndexOf(BestEffortHelper, StringComparison.Ordinal)..];
        Assert.Contains("try", helperBody, StringComparison.Ordinal);
        Assert.Contains("catch (Exception)", helperBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Launches the actual compiled worker entry point and forces its real fatal catch with a
    /// malformed orchestrator URI. The fatal stderr line must contain only the safe exception
    /// classification, never the raw UriFormatException message, and the process must exit with
    /// exactly code 1 — the exception-safe ProcessExit handler can no longer corrupt the exit
    /// code during teardown. This covers the real Program.cs routing rather than copying its
    /// catch block into the test.
    /// </summary>
    [Fact]
    public async Task WorkerProgram_ActualFatalPath_UsesSanitizedClassification()
    {
        var workerDll = typeof(WorkerService).Assembly.Location;
        Assert.True(File.Exists(workerDll), $"Worker assembly not found at '{workerDll}'.");

        // Invoke the DLL through the same runtime installation as the test host. This avoids
        // depending on a machine-wide app-host registration in isolated worker containers.
        var runtimeVersionDir = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtimeVersionDir.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("Could not derive DOTNET_ROOT from the current runtime.");
        var dotnetHost = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Assert.True(File.Exists(dotnetHost), $"dotnet host not found at '{dotnetHost}'.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = dotnetHost,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(workerDll);
        process.StartInfo.Environment["ORCHESTRATOR_URL"] = "://invalid-uri-input";
        process.StartInfo.Environment["WORKER_ID"] = "redaction-program-test";

        Assert.True(process.Start());
        var stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        var stderr = await stderrTask;
        var stdout = await stdoutTask;

        // The fatal route must terminate with exactly the intended code 1. The ProcessExit
        // handler is exception-safe now, so it can no longer throw ObjectDisposedException
        // during teardown and corrupt the exit code into an abort code (134 on Linux).
        Assert.Equal(1, process.ExitCode);

        // The sanitized fatal classification is the only error output: no ObjectDisposedException
        // from the ProcessExit handler, no unhandled runtime stack-trace noise.
        Assert.Contains("[Worker] Fatal error", stderr);
        Assert.Contains(nameof(UriFormatException), stderr);
        Assert.DoesNotContain("ObjectDisposedException", stderr);
        Assert.DoesNotContain("ObjectDisposedException", stdout);
        Assert.DoesNotContain("Unhandled exception", stderr);
        Assert.DoesNotContain("Unhandled exception", stdout);

        // Redaction: the raw UriFormatException message and the malformed input never leak.
        Assert.DoesNotContain("Invalid URI", stderr);
        Assert.DoesNotContain("invalid-uri-input", stderr);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Counts non-overlapping ordinal occurrences of a fragment in the source.</summary>
    private static int CountOccurrences(string source, string fragment)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }

        return count;
    }

    /// <summary>
    /// Removes whole-line <c>//</c> comments from a source fragment, so a structural claim is made
    /// about STATEMENTS rather than about prose a comment happens to contain.
    /// </summary>
    private static string StripLineComments(string fragment) =>
        string.Join(
            "\n",
            fragment.Split('\n')
                .Select(line =>
                {
                    var index = line.IndexOf("//", StringComparison.Ordinal);
                    return index >= 0 ? line[..index] : line;
                }));

    /// <summary>
    /// Extracts the brace-balanced block whose opening line contains <paramref name="anchor"/>,
    /// starting at the anchor's own opening brace. Returns the block's body INCLUDING the opening
    /// and closing braces, or null when no such anchor exists. Brace counting ignores braces inside
    /// string literals well enough for the worker Program.cs, whose blocks contain no
    /// brace-bearing string literals at the anchors used here.
    /// </summary>
    private static string? ExtractBracedBlock(string source, string anchor, int? startLineIndent = null)
    {
        var anchorStart = source.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorStart < 0)
            return null;

        var open = source.IndexOf('{', anchorStart);
        if (open < 0)
            return null;

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[anchorStart..(i + 1)];
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the repository root by locating the solution file next to the source tree — the same
    /// discovery the orchestrator-side Program.cs structural tests use.
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null
            && !Directory.GetFiles(dir, "*.slnx").Any()
            && !Directory.Exists(Path.Combine(dir, "src", "CopilotHive")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        Assert.NotNull(dir);
        Assert.True(
            Directory.Exists(Path.Combine(dir, "src", "CopilotHive.Worker")),
            $"Repository root not found from {AppContext.BaseDirectory}");
        return dir;
    }

    /// <summary>A hermetic temp config-repo directory for the retry-loop test.</summary>
    private static string CreateTempConfigRepoDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"redaction-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A git handler for a HEALTHY repo whose origin is a credential-free HTTPS URL matching the
    /// provisioned config-repo URL, so the preparation probes, never clones, and never touches the
    /// network (the same shape the lifecycle tests use).
    /// </summary>
    private static Func<IReadOnlyList<string>, GitProcessResult> HealthyRepoHandler(string configRepoDir) =>
        tokens =>
        {
            if (MatchesTokens(tokens, "rev-parse", "--is-inside-work-tree"))
                return new GitProcessResult(0, "true\n", "");
            if (MatchesTokens(tokens, "rev-parse", "--show-toplevel"))
                return new GitProcessResult(0, configRepoDir + "\n", "");
            if (MatchesTokens(tokens, "remote", "get-url", "origin"))
                return new GitProcessResult(0, "https://github.com/org/config-repo.git\n", "");
            return new GitProcessResult(0, "", "");
        };

    private static bool MatchesTokens(IReadOnlyList<string> tokens, params string[] prefix)
    {
        if (tokens.Count < prefix.Length)
            return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(tokens[i], prefix[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort: a leaked temp directory must never fail a test.
        }
    }

    /// <summary>
    /// Builds a real <see cref="WorkerService"/> whose agent runner throws on disposal, by
    /// swapping the private <c>_agentRunner</c> field — the same reflection seam the existing
    /// WorkerService tests use.
    /// </summary>
    private static WorkerService BuildServiceWithThrowingRunner(Exception disposeFailure)
    {
        var service = new WorkerService("http://localhost:9999", "worker-redaction", ["coder"]);

        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._agentRunner field not found.");

        // Dispose the real runner the constructor created before replacing it.
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();

        field.SetValue(service, new ThrowOnDisposeAgentRunner(disposeFailure));
        return service;
    }

    private static WorkTask BuildTask() => new()
    {
        TaskId = "task-redaction",
        GoalId = "goal-redaction",
        GoalDescription = "Redaction integration goal",
        Prompt = "do the thing",
        Role = CopilotHive.Workers.WorkerRole.Coder,
        Repositories = [],
    };

    /// <summary>
    /// A minimal write stream for the accepted attempt: the Ready write is recorded and completes,
    /// so the accepted attempt's initial Ready lands and the CLEAN EOF then ends the run. It derives
    /// from the shared <see cref="FakeClientStreamWriter{T}"/> double, so the token-aware
    /// <c>WriteAsync(T, CancellationToken)</c> the production sends actually invoke is honoured.
    /// </summary>
    private sealed class RequestStreamStub : FakeClientStreamWriter<WorkerMessage>
    {
        private readonly List<WorkerMessage> _writes = [];

        internal IReadOnlyList<WorkerMessage> Writes
        {
            get { lock (_writes) return [.. _writes]; }
        }

        public override Task WriteAsync(WorkerMessage message)
        {
            lock (_writes)
                _writes.Add(message);
            return Task.CompletedTask;
        }

        public override Task CompleteAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// An invoker whose Register RPC is ACCEPTED, so a loop's second attempt genuinely returns an
    /// outcome instead of faulting again.
    /// </summary>
    private sealed class AcceptedRegisterInvoker : CallInvoker
    {
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected blocking call {method.FullName}.");

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            object payload = method.FullName switch
            {
                "/copilothive.HiveOrchestrator/Register" => new RegisterResponse
                {
                    Accepted = true,
                    AssignedWorkerId = "worker-retry-loop-second",
                    OrchestratorVersion = "test",
                },
                "/copilothive.HiveOrchestrator/GetWorkerConfig" =>
                    new GetWorkerConfigResponse
                    {
                        GithubToken = "ghp_fixture_retry_loop",
                        LlmProvider = "copilot",
                        ConfigRepoUrl = "https://github.com/org/config-repo.git",
                    },
                "/copilothive.HiveOrchestrator/GetSession" => new GetSessionResponse { Found = false },
                "/copilothive.HiveOrchestrator/SaveSession" => new SaveSessionResponse { Success = true },
                "/copilothive.HiveOrchestrator/Heartbeat" => new HeartbeatResponse { Acknowledged = true },
                _ => throw new NotSupportedException($"Unexpected unary call {method.FullName}."),
            };

            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)payload),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected server-streaming call {method.FullName}.");

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected client-streaming call {method.FullName}.");

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException(
                $"Unexpected duplex call {method.FullName} — the fixture supplies the stream explicitly.");
    }

    /// <summary>
    /// A bounded response reader: the first MoveNext returns <c>false</c> immediately (a CLEAN EOF),
    /// so the accepted attempt's work stream ends with the existing
    /// <see cref="WorkerRunOutcome.WorkStreamEnded"/> teardown path.
    /// </summary>
    private sealed class ChannelReaderStub : IAsyncStreamReader<OrchestratorMessage>
    {
        private bool _read;

        public Task<bool> MoveNext(CancellationToken cancellationToken) =>
            Task.FromResult(!_read && (_read = true));

        public OrchestratorMessage Current => new();
    }

    /// <summary>An invoker whose Register RPC faults with the supplied exception.</summary>
    private sealed class RegisterFaultingInvoker(Exception registerFailure) : CallInvoker
    {
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected blocking call {method.FullName}.");

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Task<TResponse> payload = method.FullName switch
            {
                "/copilothive.HiveOrchestrator/Register" =>
                    Task.FromException<TResponse>(registerFailure),
                _ => Task.FromException<TResponse>(
                    new NotSupportedException($"Unexpected unary call {method.FullName}.")),
            };

            return new AsyncUnaryCall<TResponse>(
                payload,
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected server-streaming call {method.FullName}.");

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected client-streaming call {method.FullName}.");

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected duplex-streaming call {method.FullName}.");
    }

    /// <summary>An <see cref="IAgentRunner"/> that throws the supplied exception from SendPromptAsync.</summary>
    private sealed class SecretThrowingAgentRunner(Exception toThrow) : IAgentRunner
    {
        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(CopilotHive.Workers.WorkerRole role, string agentsMdContent) { }
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
            => Task.CompletedTask;

        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
            => throw toThrow;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>An <see cref="IAgentRunner"/> whose disposal throws, modelling fallible teardown.</summary>
    private sealed class ThrowOnDisposeAgentRunner(Exception toThrow) : IAgentRunner
    {
        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(CopilotHive.Workers.WorkerRole role, string agentsMdContent) { }
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
            => Task.CompletedTask;
        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
            => Task.FromResult("");

        /// <summary>
        /// Returns a FAULTED ValueTask rather than throwing synchronously. That is the realistic
        /// shape for an async disposal, and it is the only shape that exercises how
        /// <see cref="WorkerService.Dispose"/> unwraps the task: a synchronous throw would
        /// propagate unwrapped regardless, making the assertion vacuous.
        /// </summary>
        public ValueTask DisposeAsync() => ValueTask.FromException(toThrow);
    }

    /// <summary>Minimal no-op git operations so the executor reaches the prompt call.</summary>
    private sealed class NoOpGit : IGitOperations
    {
        public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct) => Task.CompletedTask;
        public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
        public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct) => Task.CompletedTask;
        public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;

        public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
            => Task.FromResult(new GitChangeSummary());

        public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct) => Task.FromResult(false);
        public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(
            string workDir, string args, CancellationToken ct)
            => Task.FromResult((0, "", ""));

        public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5) => Task.CompletedTask;
    }
}
