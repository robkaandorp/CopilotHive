using CopilotHive.Goals;
using CopilotHive.Services;
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
    /// Reproduces the EXACT control flow Program.cs now uses — service disposal inside the try,
    /// in a <c>finally</c>, so the sanitized catches see it — and proves a throwing disposal is
    /// redacted instead of escaping to the runtime with its raw message.
    /// <para>
    /// Under the old structure (<c>using var service</c> declared OUTSIDE the try) the exception
    /// below would propagate past the catches uncaught, and this test would fail by throwing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProgramTeardown_ThrowingDisposal_IsSanitizedNotRaw()
    {
        var stdErr = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(stdErr);

        var sanitizedFatal = false;
        try
        {
            // ── This block mirrors Program.cs's loop body exactly. ──
            var service = BuildServiceWithThrowingRunner(
                new InvalidOperationException($"dispose failed for GH_TOKEN={SecretToken}"));

            try
            {
                try
                {
                    // Stand-in for RunAsync returning normally: the fault comes from teardown.
                    await Task.CompletedTask;
                }
                finally
                {
                    service.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                // Not expected here.
            }
            catch (Exception ex) when (ex is RpcException or HttpRequestException or IOException)
            {
                Console.Error.WriteLine(
                    $"[Worker] Connection failed [{SafeExceptionLog.Describe(ex)}]. Retrying...");
            }
            catch (Exception ex)
            {
                sanitizedFatal = true;
                Console.Error.WriteLine($"[Worker] Fatal error [{SafeExceptionLog.Describe(ex)}]");
            }
        }
        finally
        {
            Console.SetError(originalErr);
        }

        // The disposal fault reached the SANITIZED fatal handler rather than escaping.
        Assert.True(sanitizedFatal, "A throwing disposal must be caught by the sanitized fatal handler.");

        var output = stdErr.ToString();
        Assert.DoesNotContain(SecretToken, output);
        Assert.Contains("Fatal error", output);
        Assert.Contains(nameof(InvalidOperationException), output);
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
    /// STRUCTURAL REGRESSION for the returned-outcome handling in the worker entry point: the
    /// outcome consumed after the attempt loop's catch region is the value the awaited
    /// <see cref="WorkerService.RunAsync"/> genuinely returned, the handling sits AFTER every
    /// retry-governing catch, and both diagnostics it writes are guarded.
    /// <para>
    /// THE REVIEWER MAJOR this pins: iteration 1 emitted the returned-outcome handling — including
    /// a <c>Console.WriteLine</c> — before <c>break</c> and INSIDE the catch region whose
    /// <c>IOException</c> branch retries. A closed redirected stdout throws
    /// <see cref="IOException"/> from <c>Console</c>, so a clean-EOF outcome could be converted
    /// into a fresh connection attempt — a clean-EOF reconnect. The structure below makes that
    /// misrouting impossible: the catch classification only ever sees exceptions raised inside the
    /// covered region (RunAsync, disposal), and every diagnostic the returned-outcome path writes is
    /// best-effort, so a degraded sink can neither create an attempt nor change the exit code nor
    /// reach the runtime unhandled.
    /// </para>
    /// </summary>
    [Fact]
    public void WorkerProgram_ReturnedOutcomeHandling_IsOutsideRetryGoverningCatches()
    {
        var programPath = Path.Combine(FindRepoRoot(), "src", "CopilotHive.Worker", "Program.cs");
        Assert.True(File.Exists(programPath), $"Worker Program.cs not found at '{programPath}'.");
        var source = File.ReadAllText(programPath).ReplaceLineEndings("\n");

        // The REAL result of the run is captured INSIDE the covered region and nothing else happens
        // there: the outcome is merely captured, so no diagnostic of ours can ever be classified as
        // a connection failure.
        const string OutcomeCapture = "completedOutcome = await service.RunAsync(cts.Token);";
        // Disposal stays in a finally INSIDE the try whose sanitized catches redact its faults.
        const string DisposalInFinally = "service.Dispose();";
        // A returned outcome unconditionally stops the loop; a thrown failure leaves the outcome
        // null and simply retries.
        const string NullOutcomeContinues = "if (completedOutcome is not { } outcome)\n        continue;";
        const string CleanExitBreak = "    break;\n}\nreturn 0;";
        // Both diagnostics the returned-outcome path emits are guarded writes, never raw Console.
        const string WorkStreamEndedDiagnostic =
            "WriteBestEffort(Console.Out, \"[Worker] Work stream ended; the worker is exiting.\");";
        const string BestEffortHelper = "static void WriteBestEffort(TextWriter writer, string message)";

        foreach (var fragment in new[]
                 {
                     OutcomeCapture, DisposalInFinally, NullOutcomeContinues,
                     CleanExitBreak, WorkStreamEndedDiagnostic, BestEffortHelper,
                 })
        {
            Assert.True(
                CountOccurrences(source, fragment) == 1,
                $"Expected exactly one worker Program.cs occurrence of '{fragment}'.");
        }

        // ORDER: the outcome is captured before disposal (the finally), and the disposal — the last
        // statement inside the covered region — is followed by the catch handlers BEFORE any
        // returned-outcome handling. The handling's first observable statement therefore appears
        // AFTER the final fatal catch, i.e. outside every retry-governing catch.
        Assert.True(
            source.IndexOf(OutcomeCapture, StringComparison.Ordinal)
            < source.IndexOf(DisposalInFinally, StringComparison.Ordinal),
            "The awaited outcome must be captured before service disposal runs.");

        var catchRegionStart = source.IndexOf(
            "catch (OperationCanceledException)", StringComparison.Ordinal);
        var finalFatalCatch = source.IndexOf(
            "catch (Exception ex)\n    {\n        // All other exceptions are fatal",
            StringComparison.Ordinal);
        var outcomeHandlingStart = source.IndexOf(NullOutcomeContinues, StringComparison.Ordinal);
        Assert.True(
            catchRegionStart >= 0 && finalFatalCatch > catchRegionStart,
            "The sanitized catch region must exist after the covered try/finally.");
        Assert.True(
            outcomeHandlingStart > finalFatalCatch,
            "Returned-outcome handling must come AFTER the final fatal catch: a throwing diagnostic "
            + "there can never be classified as a connection failure and retried.");

        // A returned outcome always stops the loop with the SAME exit code as before: the break is
        // reached for both known outcomes, and the WorkStreamEnded write cannot reroute the loop.
        Assert.True(
            source.IndexOf(WorkStreamEndedDiagnostic, StringComparison.Ordinal)
            < source.IndexOf(CleanExitBreak, StringComparison.Ordinal),
            "The WorkStreamEnded diagnostic must precede the unconditional loop exit.");

        // The guarded helper swallows everything: a degraded stdout/stderr can neither escape to the
        // runtime nor alter the control flow the outcome dictates.
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
