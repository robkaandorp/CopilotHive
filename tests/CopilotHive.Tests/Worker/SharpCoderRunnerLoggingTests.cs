using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for <see cref="SharpCoderRunner"/> logging: verifies that role, model,
/// elapsed time, status, and tool-call count are correctly logged during task execution.
/// Every test invokes <see cref="SharpCoderRunner.SendPromptAsync"/> and asserts on
/// the actual log output emitted to stdout.
/// </summary>
[Collection("ConsoleOutput")]
public sealed class SharpCoderRunnerLoggingTests : IDisposable
{
    private readonly StringWriter _stdOut = new();
    private readonly StringWriter _stdErr = new();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public SharpCoderRunnerLoggingTests()
    {
        _originalOut = Console.Out;
        _originalErr = Console.Error;
        Console.SetOut(_stdOut);
        Console.SetError(_stdErr);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
        _stdOut.Dispose();
        _stdErr.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a minimal stub <see cref="IChatClient"/> that returns a single assistant message.</summary>
    private static IChatClient CreateStubClient(string replyText = "Task complete.")
        => new StubChatClient(replyText);

    /// <summary>
    /// Creates a temporary directory that exists on disk, for use as WorkDirectory.
    /// The caller is responsible for deleting it after the test.
    /// </summary>
    private static string CreateTempWorkDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharpcoder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    // ── Opening log line (role + model) ───────────────────────────────────────

    /// <summary>
    /// <see cref="SharpCoderRunner.SendPromptAsync"/> must emit a log line that matches
    /// "Executing task as {role} with model {model}. WorkDir: {workDir}"
    /// using the role set by <see cref="SharpCoderRunner.SetCustomAgent"/> and the model
    /// injected at construction time.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_LogsRoleAndModelInOpeningLine()
    {
        const string ExpectedModel = "test-model-001";
        var workDir = CreateTempWorkDir();
        try
        {
            var runner = new SharpCoderRunner(CreateStubClient(), ExpectedModel);
            runner.SetCustomAgent(WorkerRole.Tester, "you are a tester");

            _stdOut.GetStringBuilder().Clear();
            await runner.SendPromptAsync("do something", workDir, CancellationToken.None);
            await runner.DisposeAsync();

            var output = _stdOut.ToString();
            var expectedLine = $"Executing task as Tester with model {ExpectedModel}. WorkDir: {workDir}";
            Assert.Contains(expectedLine, output);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// The opening log line must reflect the role that was passed to
    /// <see cref="SharpCoderRunner.SetCustomAgent"/>. Each role name must appear exactly
    /// as its enum member name in the log output.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder, "Coder")]
    [InlineData(WorkerRole.Tester, "Tester")]
    [InlineData(WorkerRole.Reviewer, "Reviewer")]
    [InlineData(WorkerRole.DocWriter, "DocWriter")]
    public async Task SendPromptAsync_OpeningLine_ContainsCorrectRoleName(WorkerRole role, string expectedRoleName)
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var runner = new SharpCoderRunner(CreateStubClient(), "any-model");
            runner.SetCustomAgent(role, "system prompt");

            _stdOut.GetStringBuilder().Clear();
            await runner.SendPromptAsync("task", workDir, CancellationToken.None);
            await runner.DisposeAsync();

            var output = _stdOut.ToString();
            Assert.Contains($"as {expectedRoleName} with model", output);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// The opening log line must contain the model name injected at construction time.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_OpeningLine_ContainsModelName()
    {
        const string ExpectedModel = "claude-sonnet-4-test";
        var workDir = CreateTempWorkDir();
        try
        {
            var runner = new SharpCoderRunner(CreateStubClient(), ExpectedModel);
            runner.SetCustomAgent(WorkerRole.Coder, "system prompt");

            _stdOut.GetStringBuilder().Clear();
            await runner.SendPromptAsync("task", workDir, CancellationToken.None);
            await runner.DisposeAsync();

            var output = _stdOut.ToString();
            Assert.Contains($"model {ExpectedModel}", output);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// The opening log line must contain the working directory path.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_OpeningLine_ContainsWorkDir()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var runner = new SharpCoderRunner(CreateStubClient(), "model-z");
            runner.SetCustomAgent(WorkerRole.Coder, "system prompt");

            _stdOut.GetStringBuilder().Clear();
            await runner.SendPromptAsync("task", workDir, CancellationToken.None);
            await runner.DisposeAsync();

            var output = _stdOut.ToString();
            Assert.Contains($"WorkDir: {workDir}", output);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ── Closing log line (elapsed + status + toolCalls) ───────────────────────

    /// <summary>
    /// <see cref="SharpCoderRunner.SendPromptAsync"/> must emit a closing log line that:
    /// (a) contains "status=" with the correct status value from the agent result,
    /// (b) contains "toolCalls=" with the correct tool-call count,
    /// (c) contains a non-negative elapsed time in seconds formatted as F2.
    /// The stub returns Status="Success" with 0 tool calls.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_LogsClosingLineWithElapsedStatusAndToolCalls()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var runner = new SharpCoderRunner(CreateStubClient("All done."), "stub-model");
            runner.SetCustomAgent(WorkerRole.Coder, "system prompt");

            _stdOut.GetStringBuilder().Clear();
            await runner.SendPromptAsync("do the task", workDir, CancellationToken.None);
            await runner.DisposeAsync();

            var output = _stdOut.ToString();

            // (a) status must be present
            Assert.Contains("status=", output);

            // (b) toolCalls must be present with a non-negative integer value
            Assert.Contains("toolCalls=", output);

            // (c) elapsed time must be present and non-negative (F2 format: digits.digits)
            var elapsedMatch = Regex.Match(output, @"Task finished in (\d+\.\d+)s");
            Assert.True(elapsedMatch.Success, $"Expected 'Task finished in <elapsed>s' in output:\n{output}");
            var elapsed = double.Parse(elapsedMatch.Groups[1].Value);
            Assert.True(elapsed >= 0.0, $"Elapsed time must be non-negative, got {elapsed}");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// The status field in the closing log line must reflect the actual agent result status.
    /// The stub client returns a response that causes the CodingAgent to terminate with "Success".
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_ClosingLine_ContainsSuccessStatus()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var runner = new SharpCoderRunner(CreateStubClient(), "model-x");
            runner.SetCustomAgent(WorkerRole.Coder, "prompt");

            _stdOut.GetStringBuilder().Clear();
            await runner.SendPromptAsync("task", workDir, CancellationToken.None);
            await runner.DisposeAsync();

            var output = _stdOut.ToString();
            Assert.Contains("status=Success", output);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// The toolCalls field in the closing log line must be a non-negative integer.
    /// For the stub client (no tool calls), it must be 0.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_ClosingLine_ContainsNonNegativeToolCallsCount()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var runner = new SharpCoderRunner(CreateStubClient(), "model-y");
            runner.SetCustomAgent(WorkerRole.Coder, "prompt");

            _stdOut.GetStringBuilder().Clear();
            await runner.SendPromptAsync("task", workDir, CancellationToken.None);
            await runner.DisposeAsync();

            var output = _stdOut.ToString();
            var match = Regex.Match(output, @"toolCalls=(\d+)");
            Assert.True(match.Success, $"Expected 'toolCalls=<n>' in output:\n{output}");
            var toolCalls = int.Parse(match.Groups[1].Value);
            Assert.True(toolCalls >= 0, $"toolCalls must be non-negative, got {toolCalls}");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// The closing log line must include the "Task finished in" prefix followed by
    /// elapsed seconds in F2 format and the parenthesized status and toolCalls fields.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_ClosingLine_HasExpectedStructure()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var runner = new SharpCoderRunner(CreateStubClient(), "model-struct");
            runner.SetCustomAgent(WorkerRole.Tester, "system");

            _stdOut.GetStringBuilder().Clear();
            await runner.SendPromptAsync("test task", workDir, CancellationToken.None);
            await runner.DisposeAsync();

            var output = _stdOut.ToString();
            // Full pattern: "Task finished in X.XXs (status=Y, toolCalls=Z)"
            var pattern = new Regex(@"Task finished in \d+\.\d+s \(status=\w+, toolCalls=\d+\)");
            Assert.True(pattern.IsMatch(output),
                $"Closing line did not match expected pattern in output:\n{output}");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}

/// <summary>
/// A minimal <see cref="IChatClient"/> stub that returns a single assistant reply and then
/// signals completion, allowing <see cref="SharpCoder.CodingAgent"/> to finish in one step.
/// </summary>
file sealed class StubChatClient(string replyText) : IChatClient
{
    /// <inheritdoc />
    public ChatClientMetadata Metadata => new("stub", null, "stub-model");

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, replyText))
        {
            FinishReason = ChatFinishReason.Stop,
        };
        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return GetStreamingUpdatesAsync(replyText, cancellationToken);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> GetStreamingUpdatesAsync(
        string replyText, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        // Yield text delta in chunks
        var remaining = replyText.AsMemory();
        const int chunkSize = 10;

        while (!remaining.IsEmpty && !cancellationToken.IsCancellationRequested)
        {
            var chunk = remaining.Length <= chunkSize
                ? remaining
                : remaining[..chunkSize];
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(chunk.ToString())]);
            remaining = remaining.Slice(chunk.Length);
        }

        // Yield final update with finish reason
        yield return new ChatResponseUpdate
        {
            FinishReason = ChatFinishReason.Stop,
            Role = ChatRole.Assistant,
        };
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() { }
}
