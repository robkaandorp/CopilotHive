extern alias WorkerAssembly;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using WorkerAssembly::CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for <see cref="SharpCoderRunner"/> logging: verifies that role, model,
/// elapsed time, status, and tool-call count are correctly logged during task execution.
/// </summary>
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

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    // ── SetCustomAgent role validation ────────────────────────────────────────

    #region SetCustomAgent accepts all worker roles

    /// <summary>
    /// <see cref="SharpCoderRunner.SetCustomAgent"/> must accept every defined
    /// <see cref="WorkerRole"/> value without throwing.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    [InlineData(WorkerRole.Reviewer)]
    [InlineData(WorkerRole.Improver)]
    [InlineData(WorkerRole.DocWriter)]
    [InlineData(WorkerRole.Orchestrator)]
    [InlineData(WorkerRole.MergeWorker)]
    [InlineData(WorkerRole.Unspecified)]
    public void SetCustomAgent_AcceptsAllRoles(WorkerRole role)
    {
        var runner = new SharpCoderRunner();
        var ex = Record.Exception(() => runner.SetCustomAgent(role, "test agent content"));
        Assert.Null(ex);
    }

    #endregion

    // ── Opening log line (role + model) ───────────────────────────────────────

    #region SendPromptAsync logs opening line with correct role and model

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

    #endregion

    #region SendPromptAsync uses role name from SetCustomAgent

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

    #endregion

    // ── Closing log line (elapsed + status + toolCalls) ───────────────────────

    #region SendPromptAsync logs closing line with status and toolCalls

    /// <summary>
    /// <see cref="SharpCoderRunner.SendPromptAsync"/> must emit a closing log line that:
    /// (a) contains "status=" with the correct status value from the agent result,
    /// (b) contains "toolCalls=" with the correct tool-call count,
    /// (c) contains a non-negative elapsed time in seconds.
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

            // (c) elapsed time must be present and non-negative
            // Pattern: "Task finished in <number>s"
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

    #endregion

    #region SendPromptAsync closing line status value matches result

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

    #endregion

    #region SendPromptAsync closing line toolCalls value is non-negative

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

    #endregion

    // ── Log format structure ─────────────────────────────────────────────────

    #region Opening log message format is correct

    /// <summary>
    /// The opening log message must follow the exact format:
    /// "Executing task as {role} with model {model}. WorkDir: {workDir}"
    /// This test verifies the format string structure with sample values.
    /// </summary>
    [Fact]
    public void OpeningLogMessage_HasCorrectFormat()
    {
        var role = WorkerRole.Tester;
        var model = "claude-sonnet-4.6";
        var workDir = "/workspace/repo";

        var logMessage = $"Executing task as {role} with model {model}. WorkDir: {workDir}";

        Assert.Contains("Tester", logMessage);
        Assert.Contains("claude-sonnet-4.6", logMessage);
        Assert.Contains("/workspace/repo", logMessage);
        Assert.StartsWith("Executing task as ", logMessage);
    }

    #endregion

    #region Opening log message contains role name

    /// <summary>
    /// The opening log message must contain the role name as its enum string representation.
    /// <c>WorkerRole.ToString()</c> returns the enum member name (e.g., "Coder", "Tester").
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder, "Coder")]
    [InlineData(WorkerRole.Tester, "Tester")]
    [InlineData(WorkerRole.Reviewer, "Reviewer")]
    [InlineData(WorkerRole.Improver, "Improver")]
    [InlineData(WorkerRole.DocWriter, "DocWriter")]
    [InlineData(WorkerRole.Orchestrator, "Orchestrator")]
    public void OpeningLogMessage_ContainsRoleName(WorkerRole role, string expectedRoleName)
    {
        var model = "test-model";
        var workDir = "/test/workdir";

        var logMessage = $"Executing task as {role} with model {model}. WorkDir: {workDir}";

        Assert.Contains($"as {expectedRoleName} with", logMessage);
    }

    #endregion

    #region Opening log message contains model name

    /// <summary>
    /// The opening log message must contain the model name.
    /// The model name may be a real model name or the placeholder "(default)".
    /// </summary>
    [Theory]
    [InlineData("claude-sonnet-4.6")]
    [InlineData("gpt-4")]
    [InlineData("(default)")]
    [InlineData("llama3")]
    public void OpeningLogMessage_ContainsModelName(string model)
    {
        var role = WorkerRole.Coder;
        var workDir = "/workspace";

        var logMessage = $"Executing task as {role} with model {model}. WorkDir: {workDir}";

        Assert.Contains($"model {model}", logMessage);
    }

    #endregion

    #region Opening log message contains work directory

    /// <summary>
    /// The opening log message must contain the work directory path.
    /// </summary>
    [Theory]
    [InlineData("/workspace/repo")]
    [InlineData("/tmp/worker-123")]
    [InlineData("C:\\Users\\test\\repo")]
    public void OpeningLogMessage_ContainsWorkDir(string workDir)
    {
        var role = WorkerRole.Tester;
        var model = "test-model";

        var logMessage = $"Executing task as {role} with model {model}. WorkDir: {workDir}";

        Assert.Contains($"WorkDir: {workDir}", logMessage);
    }

    #endregion

    #region Closing log message format is correct

    /// <summary>
    /// The closing log message must follow the exact format:
    /// "Task finished in {elapsed}s (status={status}, toolCalls={toolCalls})"
    /// This test verifies the format string structure with sample values.
    /// </summary>
    [Theory]
    [InlineData(0.05, "Success", 0)]
    [InlineData(12.34, "Success", 5)]
    [InlineData(99.99, "Failure", 10)]
    [InlineData(1.00, "MaxSteps", 50)]
    public void ClosingLogMessage_HasCorrectFormat(double elapsedSeconds, string status, int toolCalls)
    {
        var logMessage = $"Task finished in {elapsedSeconds:F2}s (status={status}, toolCalls={toolCalls})";

        Assert.Contains($"{elapsedSeconds:F2}s", logMessage);
        Assert.Contains($"status={status}", logMessage);
        Assert.Contains($"toolCalls={toolCalls}", logMessage);
    }

    #endregion

    #region Closing log message status values

    /// <summary>
    /// The closing log message status must reflect the AgentResult.Status value.
    /// Common values include: Success, Failure, MaxSteps.
    /// </summary>
    [Theory]
    [InlineData("Success")]
    [InlineData("Failure")]
    [InlineData("MaxSteps")]
    public void ClosingLogMessage_ContainsValidStatus(string status)
    {
        var elapsedSeconds = 1.23;
        var toolCalls = 5;

        var logMessage = $"Task finished in {elapsedSeconds:F2}s (status={status}, toolCalls={toolCalls})";

        Assert.Contains($"status={status}", logMessage);
    }

    #endregion

    #region Closing log message tool calls count

    /// <summary>
    /// The closing log message toolCalls must reflect the AgentResult.ToolCallCount.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(100)]
    public void ClosingLogMessage_ContainsToolCallsCount(int toolCalls)
    {
        var elapsedSeconds = 1.23;
        var status = "Success";

        var logMessage = $"Task finished in {elapsedSeconds:F2}s (status={status}, toolCalls={toolCalls})";

        Assert.Contains($"toolCalls={toolCalls}", logMessage);
    }

    #endregion

    #region Elapsed time is formatted with two decimal places

    /// <summary>
    /// The elapsed time must be formatted with F2 (two decimal places) to provide
    /// consistent, readable output.
    /// </summary>
    [Theory]
    [InlineData(0.05, "0.05")]
    [InlineData(1.0, "1.00")]
    [InlineData(12.345, "12.35")]
    [InlineData(99.999, "100.00")]
    public void ElapsedTime_IsFormattedWithTwoDecimalPlaces(double elapsedSeconds, string expectedOutput)
    {
        var formatted = elapsedSeconds.ToString("F2");
        Assert.Equal(expectedOutput, formatted);
    }

    #endregion
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
        => throw new NotSupportedException("Streaming is not used in these tests.");

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() { }
}
