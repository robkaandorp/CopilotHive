using CopilotHive.Worker;
using CopilotHive.Workers;
using CopilotHive.Services;

using Microsoft.Extensions.AI;

using System.Runtime.CompilerServices;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Pins <see cref="SharpCoderRunner.SendPromptAsync"/>'s handling of SharpCoder's
/// <c>AgentResult.Status == "Error"</c> contract on a COMPLETED/Error streaming result.
/// <para>
/// SharpCoder reports most provider failures as a final <c>Completed</c> update whose result has
/// <c>Status == "Error"</c> and the failure text in <c>Message</c> — no exception leaves the
/// enumeration. The rejected revision logged that status and then RETURNED <c>result.Message</c>,
/// so the provider's failure text became the turn's normal output and <c>TaskExecutor</c> reported a
/// completed phase. The runner must instead throw <see cref="AgentTurnFailedException"/>, whose
/// message carries no provider text (provider text can echo a provisioned secret; see
/// <see cref="SafeExceptionLog"/>), so the existing <c>TaskExecutor</c> catch reports
/// <c>TaskOutcome.Failed</c> with a <c>FAIL</c> verdict.
/// </para>
/// <para>
/// The provider-failure shape is produced by a streaming fake whose
/// <c>GetStreamingResponseAsync</c> is an ASYNC ITERATOR that throws on its first
/// <c>MoveNextAsync</c>. That detail is load-bearing: a synchronous throw from
/// <c>GetStreamingResponseAsync</c>, or an <see cref="HttpRequestException"/>, propagates straight
/// out of the agent and never becomes a Completed/Error result, so it would not exercise this code
/// path at all.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class SharpCoderRunnerErrorResultTests
{
    /// <summary>The provider text the fake reports — it must never reach the thrown exception.</summary>
    private const string ProviderFailureText = "provider failure SECRET-TOKEN-123";

    /// <summary>The one fixed message <see cref="AgentTurnFailedException"/> may carry.</summary>
    private const string ExpectedMessage = "The agent turn ended with SharpCoder status 'Error'.";

    /// <summary>
    /// A bounded FAILURE failsafe for the lease assertion, so a stranded lease surfaces as a named
    /// failure instead of a hung test host. Never an ordering device: the reset completes in a
    /// <c>finally</c>, so the wait returns as soon as it does.
    /// </summary>
    private static readonly TimeSpan LeaseFailsafe = TimeSpan.FromSeconds(30);

    private static string CreateWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"error-result-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// An <c>Error</c> result must make <see cref="SharpCoderRunner.SendPromptAsync"/> THROW
    /// <see cref="AgentTurnFailedException"/>, and that exception must not carry the provider text.
    /// On the rejected revision the same call returns <c>"provider failure SECRET-TOKEN-123"</c>, so
    /// the exception assertion below fails — and the secret-token assertions fail with it.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_ErrorStatusResult_ThrowsAgentTurnFailedExceptionWithoutProviderText()
    {
        var workDir = CreateWorkDir();
        var runner = new SharpCoderRunner();
        var client = new ThrowBeforeFirstYieldChatClient(new InvalidOperationException(ProviderFailureText));

        try
        {
            runner.ClientCreationSeam = _ => client;
            runner.SetCustomAgent(WorkerRole.Coder, "coder");

            var ex = await Assert.ThrowsAsync<AgentTurnFailedException>(
                () => runner.SendPromptAsync("work", workDir, TestContext.Current.CancellationToken));

            // ONE fixed message: the failure shape is classified without quoting the provider.
            Assert.Equal(ExpectedMessage, ex.Message);
            Assert.DoesNotContain("SECRET-TOKEN-123", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("provider failure", ex.Message, StringComparison.Ordinal);
            Assert.Null(ex.InnerException);
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// The throw unwinds through <c>SendPromptAsync</c>'s existing <c>finally</c>, so the client
    /// lease is released and a later <c>ResetSessionAsync</c> completes instead of deadlocking on the
    /// lifecycle gate. The reset also disposes the client — which it can only reach after acquiring
    /// the gate — so the disposal count witnesses that the lease really came back.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_ErrorStatusResult_StillReleasesClientLease()
    {
        var workDir = CreateWorkDir();
        var runner = new SharpCoderRunner();
        var client = new ThrowBeforeFirstYieldChatClient(new InvalidOperationException(ProviderFailureText));

        try
        {
            runner.ClientCreationSeam = _ => client;
            runner.SetCustomAgent(WorkerRole.Coder, "coder");

            await Assert.ThrowsAsync<AgentTurnFailedException>(
                () => runner.SendPromptAsync("work", workDir, TestContext.Current.CancellationToken));

            // The turn does not own the client: nothing has disposed it yet.
            Assert.Equal(0, client.DisposeCount);

            var reset = runner.ResetSessionAsync("next-model", null, TestContext.Current.CancellationToken);
            await reset.WaitAsync(LeaseFailsafe, TestContext.Current.CancellationToken);

            Assert.True(reset.IsCompletedSuccessfully, "ResetSessionAsync must not deadlock after an Error turn.");
            Assert.Equal(1, client.DisposeCount);
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// The SUCCESS path is unchanged: a plain text completion (no tool calls) is returned from
    /// <see cref="SharpCoderRunner.SendPromptAsync"/> exactly as before, with no exception thrown.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_SuccessStatusResult_ReturnsAgentMessage()
    {
        var workDir = CreateWorkDir();
        var runner = new SharpCoderRunner();
        var client = new PlainTextChatClient("All done.");

        try
        {
            runner.ClientCreationSeam = _ => client;
            runner.SetCustomAgent(WorkerRole.Coder, "coder");

            var result = await runner.SendPromptAsync("work", workDir, TestContext.Current.CancellationToken);

            Assert.Equal("All done.", result);
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// POSITIVE CONTROL for the "only the exact status Error throws" rule: a
    /// <c>MaxStepsReached</c> result is NOT a provider failure, so the turn must still RETURN the
    /// agent's accumulated partial text instead of throwing. The status is read from the runner's own
    /// closing log line so the control is anchored to the ACTUAL status value — without it, a mutant
    /// that throws for every non-success status could still satisfy the assertions below.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_MaxStepsReachedStatus_ReturnsPartialTextWithoutThrowing()
    {
        var workDir = CreateWorkDir();
        var runner = new SharpCoderRunner();
        var client = new AlwaysToolCallChatClient();
        var stdout = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(stdout);
            runner.ClientCreationSeam = _ => client;
            runner.SetCustomAgent(WorkerRole.Coder, "coder");

            // A step budget small enough to be reached immediately: the fake always asks for a tool,
            // so the agent can never terminate on its own.
            runner.OnAgentOptionsCreated = options => options.MaxSteps = 2;

            var result = await runner.SendPromptAsync("work", workDir, TestContext.Current.CancellationToken);

            // The status really was MaxStepsReached...
            Assert.Contains("status=MaxStepsReached", stdout.ToString(), StringComparison.Ordinal);

            // ...and the partial text came back instead of an AgentTurnFailedException.
            Assert.Contains("partial progress.", result, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// THE ACCEPTANCE CRITERION, end to end through the REAL production seams: the real
    /// <see cref="SharpCoderRunner"/> (its provider failure faked at the <c>IChatClient</c> boundary)
    /// driven by the real <see cref="TaskExecutor"/>. An Error turn must come back as
    /// <see cref="TaskOutcome.Failed"/> with a <c>FAIL</c> verdict — never as a completed phase whose
    /// narrative is the provider's error text — and neither the result nor its issues may carry that
    /// text. On the rejected revision the same flow returns <c>TaskOutcome.Completed</c> and the
    /// provider text appears in the output, so this test fails on the old code twice over.
    /// </summary>
    [Fact]
    public async Task RealRunnerErrorTurn_ThroughTaskExecutor_ReportsFailedWithFailVerdictAndNoProviderText()
    {
        var workDir = CreateWorkDir();
        var runner = new SharpCoderRunner();
        var client = new ThrowBeforeFirstYieldChatClient(new InvalidOperationException(ProviderFailureText));
        var git = new NoOpGitOperations();

        try
        {
            runner.ClientCreationSeam = _ => client;
            runner.SetCustomAgent(WorkerRole.Coder, "coder");

            var executor = new TaskExecutor(runner, gitOperations: git, sessionClient: null);
            var task = new WorkTask
            {
                TaskId = "task-error-result",
                GoalId = "goal-error-result",
                GoalDescription = "Error-result handling",
                Prompt = "do the thing",
                Role = WorkerRole.Coder,
                Repositories = [],
            };

            var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

            // FAILED, with a FAIL verdict — not a completed phase.
            Assert.Equal(TaskOutcome.Failed, result.Status);
            Assert.Equal("FAIL", result.Metrics!.Verdict);

            // The sanitized classification of THIS exception type is what travels onward...
            Assert.Contains("AgentTurnFailedException", result.Output, StringComparison.Ordinal);
            Assert.Contains(result.Metrics.Issues, i => i.Contains("AgentTurnFailedException", StringComparison.Ordinal));

            // ...and the provider's failure text is nowhere in the result that reaches the orchestrator.
            Assert.DoesNotContain("SECRET-TOKEN-123", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("provider failure", result.Output, StringComparison.Ordinal);
            foreach (var issue in result.Metrics.Issues)
                Assert.DoesNotContain("SECRET-TOKEN-123", issue, StringComparison.Ordinal);

            // A failed turn is never a phase narrative, and the auto-commit follow-up never ran.
            Assert.False(git.PushWasCalled, "A failed turn must not have produced a pushed branch.");
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }
}

// ── Stub chat clients ────────────────────────────────────────────────────────

/// <summary>
/// Reproduces SharpCoder's provider-failure shape: the streaming response is an ASYNC ITERATOR that
/// throws from its FIRST <c>MoveNextAsync</c>, so the agent converts the fault into a final
/// <c>Completed</c> update carrying <c>Status == "Error"</c> and the failure text in <c>Message</c>
/// instead of propagating the exception.
/// </summary>
file sealed class ThrowBeforeFirstYieldChatClient(Exception failure) : IChatClient
{
    private int _disposeCount;

    public ChatClientMetadata Metadata => new("throw-before-first-yield", null, "throw-before-first-yield-model");

    internal int DisposeCount => Volatile.Read(ref _disposeCount);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw failure;

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        // The throw IS the point: it surfaces from the first MoveNextAsync, which is what makes the
        // agent report a Completed/Error result rather than rethrow.
        throw failure;
#pragma warning disable CS0162 // Unreachable: required to make this method an async iterator.
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => Interlocked.Increment(ref _disposeCount);
}

/// <summary>
/// A client whose streaming response is a plain text completion with no tool calls, so the agent
/// terminates successfully after one step.
/// </summary>
file sealed class PlainTextChatClient(string replyText) : IChatClient
{
    public ChatClientMetadata Metadata => new("plain-text", null, "plain-text-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replyText))
        {
            FinishReason = ChatFinishReason.Stop,
        });

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => StreamAsync(replyText, cancellationToken);

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        string text, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var remaining = text.AsMemory();
        const int ChunkSize = 10;

        while (!remaining.IsEmpty && !cancellationToken.IsCancellationRequested)
        {
            var chunk = remaining.Length <= ChunkSize ? remaining : remaining[..ChunkSize];
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(chunk.ToString())]);
            remaining = remaining.Slice(chunk.Length);
        }

        yield return new ChatResponseUpdate
        {
            FinishReason = ChatFinishReason.Stop,
            Role = ChatRole.Assistant,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// A fake that always answers with a tool call (so the agent can never finish on its own and runs
/// into the configured step limit) plus some partial text, so the <c>MaxStepsReached</c> positive
/// control has accumulated output to be returned.
/// </summary>
file sealed class AlwaysToolCallChatClient : IChatClient
{
    private int _callCount;

    public ChatClientMetadata Metadata => new("always-tool", null, "always-tool-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "loop")));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => StreamAsync(cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _callCount);
        await Task.Yield();

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("partial progress. ")]);
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(
            $"call-{call}", "report_progress",
            new Dictionary<string, object?> { ["status"] = "s", ["details"] = "d" })])
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>Minimal no-op git operations so the executor reaches the prompt call.</summary>
file sealed class NoOpGitOperations : IGitOperations
{
    public bool PushWasCalled { get; private set; }

    public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct) => Task.CompletedTask;

    public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;

    public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct)
        => Task.CompletedTask;

    public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct)
    {
        PushWasCalled = true;
        return Task.CompletedTask;
    }

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
