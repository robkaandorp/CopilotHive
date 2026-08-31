using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using SharpCoder;

using System.Runtime.CompilerServices;
using System.Text;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for <see cref="SharpCoderRunner.LogToolCallDelta"/>: only SharpCoder tool-call
/// envelope TextDeltas ("\n\n`🔧 Name(args)`\n") are logged (backticks stripped, sanitized);
/// ordinary chat deltas and non-TextDelta updates are discarded.
/// </summary>
[Collection("ConsoleOutput")]
public sealed class SharpCoderRunnerToolCallDeltaTests : IDisposable
{
    private readonly StringWriter _stdOut = new();
    private readonly TextWriter _originalOut;

    public SharpCoderRunnerToolCallDeltaTests()
    {
        _originalOut = Console.Out;
        Console.SetOut(_stdOut);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _stdOut.Dispose();
    }

    // ── Real envelope ─────────────────────────────────────────────────────────

    /// <summary>
    /// The REAL envelope emitted by SharpCoder's CodingAgent with ShowToolCallsInStream
    /// (`"\n\n`🔧 Name(args)`\n"`) must produce exactly ONE Info line whose text is
    /// `🔧 Name(args)` — surrounding backticks stripped, wrench and space preserved.
    /// </summary>
    [Fact]
    public void RealEnvelopeDelta_IsLoggedOnceWithBackticksStripped()
    {
        var output = Capture(() => SharpCoderRunner.LogToolCallDelta(
            StreamingUpdate.TextDelta("\n\n`🔧 Bash(dotnet test)`\n"), MakeLogger()));

        // Exactly one Info line, prefixed by the logger category
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var infoLine = Assert.Single(lines);
        Assert.Equal("[SharpCoder] 🔧 Bash(dotnet test)", infoLine);
    }

    // ── Non-tool-call deltas must be discarded ────────────────────────────────

    /// <summary>An ordinary chat delta must NOT be logged.</summary>
    [Fact]
    public void OrdinaryChatDelta_IsNotLogged()
    {
        var output = Capture(() => SharpCoderRunner.LogToolCallDelta(
            StreamingUpdate.TextDelta("Working on it..."), MakeLogger()));

        Assert.Equal(string.Empty, output);
    }

    /// <summary>
    /// A chat delta that merely contains 🔧 mid-line WITHOUT the leading backtick envelope
    /// must NOT be logged.
    /// </summary>
    [Fact]
    public void WrenchMidLineWithoutBacktickEnvelope_IsNotLogged()
    {
        var output = Capture(() => SharpCoderRunner.LogToolCallDelta(
            StreamingUpdate.TextDelta("Let's grab the 🔧 wrench"), MakeLogger()));

        Assert.Equal(string.Empty, output);
    }

    /// <summary>A multi-line delta must NOT be logged (inner LF breaks the envelope rule).</summary>
    [Fact]
    public void MultiLineChatDelta_IsNotLogged()
    {
        var output = Capture(() => SharpCoderRunner.LogToolCallDelta(
            StreamingUpdate.TextDelta("first line\nsecond line"), MakeLogger()));

        Assert.Equal(string.Empty, output);
    }

    /// <summary>
    /// A streamed partial fragment with no closing backtick must NOT be logged — the
    /// envelope is only complete once the trailing backtick arrives.
    /// </summary>
    [Fact]
    public void PartialFragmentWithoutClosingBacktick_IsNotLogged()
    {
        var output = Capture(() => SharpCoderRunner.LogToolCallDelta(
            StreamingUpdate.TextDelta("`🔧 Bash(dot"), MakeLogger()));

        Assert.Equal(string.Empty, output);
    }

    // ── LF/CR rejection BEFORE sanitization ───────────────────────────────────

    /// <summary>
    /// An envelope delta whose args contain an embedded LF must be REJECTED (not logged) by the
    /// pre-sanitization LF/CR check — sanitization must not rescue it into the log.
    /// </summary>
    [Fact]
    public void EnvelopeWithEmbeddedLF_IsRejectedBeforeSanitization()
    {
        var output = Capture(() => SharpCoderRunner.LogToolCallDelta(
            StreamingUpdate.TextDelta("\n\n`🔧 Bash(dotnet\ntest)`\n"), MakeLogger()));

        Assert.Equal(string.Empty, output);
    }

    /// <summary>An envelope delta whose args contain an embedded CR must likewise be REJECTED.</summary>
    [Fact]
    public void EnvelopeWithEmbeddedCR_IsRejectedBeforeSanitization()
    {
        var output = Capture(() => SharpCoderRunner.LogToolCallDelta(
            StreamingUpdate.TextDelta("\n\n`🔧 Bash(dotnet\rtest)`\n"), MakeLogger()));

        Assert.Equal(string.Empty, output);
    }

    // ── Control-character sanitization (console line-injection prevention) ────

    /// <summary>
    /// Control characters inside accepted envelope args (TAB, ESC, DEL, and a C1 char) must each
    /// be replaced with '?' by <c>LogSanitizer.SanitizeText</c>, keeping the logged line single-line.
    /// </summary>
    [Theory]
    [InlineData("\t")]
    [InlineData("\u001B")]
    [InlineData("\u007F")]
    [InlineData("\u0090")]
    public void EnvelopeArgsControlCharacters_AreSanitizedToQuestionMarkChar(string controlChar)
    {
        var output = Capture(() => SharpCoderRunner.LogToolCallDelta(
            StreamingUpdate.TextDelta($"\n\n`🔧 Bash(a{controlChar}b)`\n"), MakeLogger()));

        var logged = Assert.Single(
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Equal("[SharpCoder] 🔧 Bash(a?b)", logged);
        // No newline may appear inside the logged envelope text itself.
        Assert.DoesNotContain("\r", logged);
        Assert.Equal("🔧 Bash(a?b)", logged["[SharpCoder] ".Length..]);
    }

    // ── Non-TextDelta updates are no-ops ─────────────────────────────────────

    /// <summary>
    /// Non-TextDelta updates (e.g. Completed, whose Text is null) must be no-ops: nothing logged,
    /// no exception — the helper is called once per streaming update in the drain loop.
    /// </summary>
    [Fact]
    public void CompletedUpdate_IsNoOp()
    {
        var result = new AgentResult { Status = "Success", Message = "done" };
        var output = Capture(() => SharpCoderRunner.LogToolCallDelta(
            StreamingUpdate.Completed(result), MakeLogger()));

        Assert.Equal(string.Empty, output);
    }

    /// <summary>A TextDelta with empty text must be a no-op (defensive: no envelope).</summary>
    [Fact]
    public void EmptyTextDelta_IsNoOp()
    {
        var output = Capture(() => SharpCoderRunner.LogToolCallDelta(
            StreamingUpdate.TextDelta(string.Empty), MakeLogger()));

        Assert.Equal(string.Empty, output);
    }

    // ── Wiring: the REAL drain loop must call the helper (removal-proof) ─────

    /// <summary>
    /// Drives the REAL <see cref="SharpCoderRunner"/> end-to-end and proves the drain loop
    /// actually invokes <see cref="SharpCoderRunner.LogToolCallDelta"/>:
    /// a stateful fake <c>IChatClient</c> first returns a FunctionCallContent response (so the
    /// agent loop performs a tool turn) and then a plain text completion, and the captured stdout
    /// must contain exactly one "[SharpCoder] 🔧 ..." line. If the helper call were removed from
    /// the drain loop, no envelope would ever be logged and this test would fail.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_RealAgentLoop_LogsToolCallEnvelopeFromDrain()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"toolcall-drain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var runner = new SharpCoderRunner(new ToolCallThenStopChatClient(), "drain-test-model");
            runner.SetCustomAgent(WorkerRole.Coder, "system prompt");

            _stdOut.GetStringBuilder().Clear();
            await runner.SendPromptAsync("use the tool", workDir, TestContext.Current.CancellationToken);
            await runner.DisposeAsync();

            var output = _stdOut.ToString();
            var envelopeLines = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.StartsWith("[SharpCoder] 🔧 ", StringComparison.Ordinal))
                .ToList();

            // Exactly ONE envelope line must be logged by the real drain loop (the fake
            // yields a FunctionCallContent for TestTool, so the upstream envelope shows
            // the args in `name(key="value")` form, capped by upstream arg truncation).
            Assert.Equal(
                "[SharpCoder] 🔧 TestTool(arg=\"args\")",
                Assert.Single(envelopeLines));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// A stateful <c>IChatClient</c> fake: the first streaming response contains a
    /// FunctionCallContent (the agent loop will treat it as a tool call), and every subsequent
    /// response is a plain text completion so the agent loop terminates.
    /// </summary>
    private sealed class ToolCallThenStopChatClient : IChatClient
    {
        private int _callCount;

        public ChatClientMetadata Metadata => new("tool-call-stub", null, "stub-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
            {
                FinishReason = ChatFinishReason.Stop,
            };
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var firstCall = Interlocked.Increment(ref _callCount) == 1;
            return StreamAsync(firstCall, cancellationToken);
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            bool firstCall,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();

            if (firstCall)
            {
                // A tool call response. CodingAgent (ShowToolCallsInStream) yields the
                // "\n\n`🔧 TestTool(\"args\")`\n" envelope TextDelta for this before tool lookup.
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "TestTool", new Dictionary<string, object?> { ["arg"] = "args" })])
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                };
                yield break;
            }

            // Subsequent call: a plain text completion so the agent loop terminates.
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")]);
            yield return new ChatResponseUpdate
            {
                FinishReason = ChatFinishReason.Stop,
                Role = ChatRole.Assistant,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WorkerLogger MakeLogger() => new("SharpCoder");

    /// <summary>Invokes <paramref name="action"/> and returns the console output it produced.</summary>
    private string Capture(Action action)
    {
        _stdOut.GetStringBuilder().Clear();
        action();
        return _stdOut.ToString();
    }
}