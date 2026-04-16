using Microsoft.Extensions.AI;

using System.Reflection;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for the private static <c>SummarizeMessage</c> helper on
/// <c>SharpCoderRunner</c>.  The method is exercised via reflection because
/// it is an internal implementation detail, but its output format is part of
/// the observable logging contract.
/// </summary>
public sealed class SharpCoderRunnerSummarizeMessageTests
{
    // ── Reflection helper ────────────────────────────────────────────────────

    private static readonly MethodInfo SummarizeMethod =
        typeof(CopilotHive.Worker.SharpCoderRunner)
            .GetMethod("SummarizeMessage", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Invokes the private static SummarizeMessage via reflection.
    /// </summary>
    private static string Summarize(ChatMessage msg)
    {
        var result = SummarizeMethod.Invoke(null, new object[] { msg });
        return (string)result!;
    }

    // ── FunctionCallContent ──────────────────────────────────────────────────

    /// <summary>
    /// A message containing a FunctionCallContent should produce the
    /// "tool:name(key="value")" format.
    /// </summary>
    [Fact]
    public void SummarizeMessage_FunctionCall_ReturnsToolNameAndFirstArg()
    {
        var args = new Dictionary<string, object?> { ["command"] = "dotnet build src/" };
        var content = new FunctionCallContent("call-1", "execute_bash_command", args!);
        var msg = new ChatMessage(ChatRole.Tool, [content]);

        var result = Summarize(msg);

        Assert.Equal("tool:execute_bash_command(command=\"dotnet build src/\")", result);
    }

    /// <summary>
    /// When the FunctionCallContent has no arguments the output should be
    /// "tool:name()".
    /// </summary>
    [Fact]
    public void SummarizeMessage_FunctionCall_NoArgs_ReturnsToolNameWithEmptyParens()
    {
        var content = new FunctionCallContent("call-2", "get_file_sizes", null);
        var msg = new ChatMessage(ChatRole.Tool, [content]);

        var result = Summarize(msg);

        Assert.Equal("tool:get_file_sizes()", result);
    }

    /// <summary>
    /// The argument value should be truncated at 100 characters.
    /// </summary>
    [Fact]
    public void SummarizeMessage_FunctionCall_LongArgValue_TruncatesAt100()
    {
        var longValue = new string('x', 150);
        var args = new Dictionary<string, object?> { ["cmd"] = longValue };
        var content = new FunctionCallContent("call-3", "run", args!);
        var msg = new ChatMessage(ChatRole.Tool, [content]);

        var result = Summarize(msg);

        // The value portion is truncated to 100 chars.
        var expected = $"tool:run(cmd=\"{new string('x', 100)}\")";
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// A null argument value should not throw; it should render as an empty string.
    /// </summary>
    [Fact]
    public void SummarizeMessage_FunctionCall_NullArgValue_RendersAsEmpty()
    {
        var args = new Dictionary<string, object?> { ["key"] = null };
        var content = new FunctionCallContent("call-4", "some_tool", args!);
        var msg = new ChatMessage(ChatRole.Tool, [content]);

        var result = Summarize(msg);

        Assert.Equal("tool:some_tool(key=\"\")", result);
    }

    // ── FunctionResultContent ────────────────────────────────────────────────

    /// <summary>
    /// A message containing a FunctionResultContent should produce a compact
    /// one-line summary instead of dumping the raw content.
    /// </summary>
    [Fact]
    public void SummarizeMessage_FunctionResult_ReturnsCallIdAndPreview()
    {
        var content = new FunctionResultContent("call-99", "Build succeeded.");
        var msg = new ChatMessage(ChatRole.Tool, [content]);

        var result = Summarize(msg);

        // Short single-line results shown inline
        Assert.Equal("result:call-99 \u2192 \"Build succeeded.\"", result);
    }

    /// <summary>
    /// Long multi-line results should show byte count and line count, not raw content.
    /// </summary>
    [Fact]
    public void SummarizeMessage_FunctionResult_LongResult_ShowsByteAndLineCount()
    {
        var longResult = new string('r', 300);
        var content = new FunctionResultContent("call-100", longResult);
        var msg = new ChatMessage(ChatRole.Tool, [content]);

        var result = Summarize(msg);

        Assert.Contains("300 bytes", result);
        Assert.Contains("1 lines", result);
    }

    /// <summary>
    /// A null result should not throw; it should render as "(empty)".
    /// </summary>
    [Fact]
    public void SummarizeMessage_FunctionResult_NullResult_RendersAsEmpty()
    {
        var content = new FunctionResultContent("call-101", null);
        var msg = new ChatMessage(ChatRole.Tool, [content]);

        var result = Summarize(msg);

        Assert.Equal("result:call-101 \u2192 (empty)", result);
    }

    /// <summary>
    /// Multi-line results should summarize with line count.
    /// </summary>
    [Fact]
    public void SummarizeMessage_FunctionResult_MultiLine_ShowsLineCount()
    {
        var multiLine = "line1\nline2\nline3\nline4\nline5";
        var content = new FunctionResultContent("call-102", multiLine);
        var msg = new ChatMessage(ChatRole.Tool, [content]);

        var result = Summarize(msg);

        Assert.Contains("5 lines", result);
        Assert.DoesNotContain("line1", result);
    }

    // ── Plain text fallback ──────────────────────────────────────────────────

    /// <summary>
    /// A plain text message shorter than 200 characters should be returned verbatim.
    /// </summary>
    [Fact]
    public void SummarizeMessage_PlainText_ReturnsTextVerbatim()
    {
        var msg = new ChatMessage(ChatRole.Assistant, "Hello, world!");

        var result = Summarize(msg);

        Assert.Equal("Hello, world!", result);
    }

    /// <summary>
    /// A plain text message longer than 200 characters should be truncated to 200.
    /// </summary>
    [Fact]
    public void SummarizeMessage_PlainText_LongText_TruncatesAt200()
    {
        var longText = new string('a', 300);
        var msg = new ChatMessage(ChatRole.Assistant, longText);

        var result = Summarize(msg);

        Assert.Equal(new string('a', 200), result);
        Assert.Equal(200, result.Length);
    }

    /// <summary>
    /// A message with null text and no special content items should return an empty string.
    /// </summary>
    [Fact]
    public void SummarizeMessage_NullText_ReturnsEmptyString()
    {
        var msg = new ChatMessage(ChatRole.User, (string?)null);

        var result = Summarize(msg);

        Assert.Equal(string.Empty, result);
    }
}
