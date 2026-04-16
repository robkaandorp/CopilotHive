using CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for <see cref="WorkerLogger"/>: verifies that Info, Error, Debug,
/// and LogBlock methods write output in the expected format, including exact
/// prefixes, log-level labels, stream routing, and structural elements.
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerLoggerTests : IDisposable
{
    private readonly StringWriter _stdOut = new();
    private readonly StringWriter _stdErr = new();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    /// <summary>
    /// Initialises the fixture by redirecting <see cref="Console.Out"/> and
    /// <see cref="Console.Error"/> to in-memory writers so test methods can
    /// inspect what was written.
    /// </summary>
    public WorkerLoggerTests()
    {
        _originalOut = Console.Out;
        _originalErr = Console.Error;
        Console.SetOut(_stdOut);
        Console.SetError(_stdErr);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
        _stdOut.Dispose();
        _stdErr.Dispose();
    }

    // ── Info ────────────────────────────────────────────────────────────────

    #region Info — full "[category] message" format on stdout

    /// <summary>
    /// <see cref="WorkerLogger.Info"/> must write the exact pattern
    /// "[category] message" — including the square brackets and trailing space
    /// before the message — to standard output.
    /// </summary>
    [Fact]
    public void Info_StandardMessage_WritesExactFormatToStdOut()
    {
        var logger = new WorkerLogger("MyCategory");

        logger.Info("hello world");

        var output = _stdOut.ToString();
        // Full bracket-wrapped category prefix followed immediately by the message.
        Assert.Contains("[MyCategory] hello world", output);
    }

    #endregion

    #region Info — category enclosed in square brackets

    /// <summary>
    /// <see cref="WorkerLogger.Info"/> must enclose the category in square
    /// brackets so callers can parse the category out of log lines.
    /// </summary>
    [Fact]
    public void Info_Output_CategoryIsWrappedInSquareBrackets()
    {
        var logger = new WorkerLogger("worker-99");

        logger.Info("ping");

        var output = _stdOut.ToString();
        Assert.Contains("[worker-99]", output);
        // The message must follow the closing bracket with a single space.
        Assert.Contains("[worker-99] ping", output);
    }

    #endregion

    #region Info — empty message still writes category prefix

    /// <summary>
    /// <see cref="WorkerLogger.Info"/> must handle an empty message without
    /// throwing and must still write the bracket-wrapped category prefix.
    /// </summary>
    [Fact]
    public void Info_EmptyMessage_WritesCategoryBracketPrefix()
    {
        var logger = new WorkerLogger("Cat");

        logger.Info(string.Empty);

        var output = _stdOut.ToString();
        // The category bracket prefix must appear even with no message body.
        Assert.Contains("[Cat] ", output);
    }

    #endregion

    #region Info — special characters preserved verbatim

    /// <summary>
    /// <see cref="WorkerLogger.Info"/> must not escape or alter special
    /// characters; they must appear verbatim in the output.
    /// </summary>
    [Fact]
    public void Info_SpecialCharacters_OutputPreservesCharactersVerbatim()
    {
        var logger = new WorkerLogger("worker");
        const string Special = "line1\ttab\nnewline & <xml> \"quotes\"";

        logger.Info(Special);

        // All special characters must survive the round-trip unchanged.
        Assert.Contains(Special, _stdOut.ToString());
    }

    #endregion

    #region Info — writes to stdout only, never stderr

    /// <summary>
    /// <see cref="WorkerLogger.Info"/> must write exclusively to standard output;
    /// standard error must remain empty.
    /// </summary>
    [Fact]
    public void Info_WritesToStdOutOnly_StdErrRemainsEmpty()
    {
        var logger = new WorkerLogger("Cat");

        logger.Info("message");

        Assert.NotEmpty(_stdOut.ToString());
        Assert.Equal(string.Empty, _stdErr.ToString());
    }

    #endregion

    // ── Error ───────────────────────────────────────────────────────────────

    #region Error — full "[category] ERROR: message" format on stderr

    /// <summary>
    /// <see cref="WorkerLogger.Error"/> must write the exact pattern
    /// "[category] ERROR: message" — including the "ERROR:" label — to
    /// standard error so that error lines are distinguishable from info lines.
    /// </summary>
    [Fact]
    public void Error_StandardMessage_WritesExactFormatToStdErr()
    {
        var logger = new WorkerLogger("MyCategory");

        logger.Error("something went wrong");

        var output = _stdErr.ToString();
        Assert.Contains("[MyCategory] ERROR: something went wrong", output);
    }

    #endregion

    #region Error — "ERROR:" label distinguishes error from info

    /// <summary>
    /// The "ERROR:" label inserted by <see cref="WorkerLogger.Error"/> must not
    /// appear in the output of <see cref="WorkerLogger.Info"/> for the same
    /// message, confirming the two methods produce distinguishable formats.
    /// </summary>
    [Fact]
    public void Error_OutputContainsErrorLabel_InfoOutputDoesNot()
    {
        var logger = new WorkerLogger("Cat");

        logger.Info("msg");
        logger.Error("msg");

        // Info output on stdout must NOT contain the ERROR: label.
        Assert.DoesNotContain("ERROR:", _stdOut.ToString());
        // Error output on stderr MUST contain the ERROR: label.
        Assert.Contains("ERROR:", _stdErr.ToString());
    }

    #endregion

    #region Error — writes to stderr only, never stdout

    /// <summary>
    /// <see cref="WorkerLogger.Error"/> must write exclusively to standard error;
    /// standard output must remain empty.
    /// </summary>
    [Fact]
    public void Error_WritesToStdErrOnly_StdOutRemainsEmpty()
    {
        var logger = new WorkerLogger("Cat");

        logger.Error("oops");

        Assert.NotEmpty(_stdErr.ToString());
        Assert.Equal(string.Empty, _stdOut.ToString());
    }

    #endregion

    #region Error — empty message still writes "[category] ERROR: " prefix

    /// <summary>
    /// <see cref="WorkerLogger.Error"/> must handle an empty message without
    /// throwing and must still emit the full "[category] ERROR: " prefix.
    /// </summary>
    [Fact]
    public void Error_EmptyMessage_WritesFullErrorPrefix()
    {
        var logger = new WorkerLogger("Cat");

        logger.Error(string.Empty);

        // The ERROR: label must appear even when the message body is empty.
        Assert.Contains("[Cat] ERROR: ", _stdErr.ToString());
    }

    #endregion

    #region Error — category name preserved in bracket prefix

    /// <summary>
    /// Verifies that the category name supplied to the constructor appears
    /// verbatim inside square brackets in every error line.
    /// </summary>
    [Fact]
    public void Error_CategoryPreservedInBracketPrefix()
    {
        var logger = new WorkerLogger("tester-worker");

        logger.Error("test failure");

        Assert.Contains("[tester-worker] ERROR:", _stdErr.ToString());
    }

    #endregion

    // ── Debug ───────────────────────────────────────────────────────────────

    #region Debug — suppressed when verbose is disabled

    /// <summary>
    /// When <see cref="WorkerLogger.IsVerbose"/> is <see langword="false"/>,
    /// <see cref="WorkerLogger.Debug"/> must not write anything to either stream.
    /// </summary>
    [Fact]
    public void Debug_VerboseDisabled_ProducesNoOutput()
    {
        // Guard: this test only applies when VERBOSE_LOGGING is not "true".
        if (WorkerLogger.IsVerbose)
            return; // verbose mode active — this branch does not apply

        var logger = new WorkerLogger("Cat");

        logger.Debug("internal detail");

        Assert.Equal(string.Empty, _stdOut.ToString());
        Assert.Equal(string.Empty, _stdErr.ToString());
    }

    #endregion

    #region Debug — "[category] DEBUG: message" format when verbose enabled

    /// <summary>
    /// When <see cref="WorkerLogger.IsVerbose"/> is <see langword="true"/>,
    /// <see cref="WorkerLogger.Debug"/> must write the exact pattern
    /// "[category] DEBUG: message" to standard output.
    /// </summary>
    [Fact]
    public void Debug_VerboseEnabled_WritesExactDebugFormatToStdOut()
    {
        // Guard: this test only applies when VERBOSE_LOGGING=true.
        if (!WorkerLogger.IsVerbose)
            return; // verbose mode inactive — this branch does not apply

        var logger = new WorkerLogger("Cat");

        logger.Debug("trace info");

        Assert.Contains("[Cat] DEBUG: trace info", _stdOut.ToString());
    }

    #endregion

    #region Debug — "DEBUG:" label absent from Info output

    /// <summary>
    /// The "DEBUG:" label must not appear in <see cref="WorkerLogger.Info"/>
    /// output, confirming debug and info messages use distinct formats.
    /// </summary>
    [Fact]
    public void Info_OutputDoesNotContainDebugLabel()
    {
        var logger = new WorkerLogger("Cat");

        logger.Info("some message");

        Assert.DoesNotContain("DEBUG:", _stdOut.ToString());
    }

    #endregion

    #region Debug — IsVerbose reflects VERBOSE_LOGGING environment variable

    /// <summary>
    /// <see cref="WorkerLogger.IsVerbose"/> must return <see langword="true"/>
    /// if and only if the <c>VERBOSE_LOGGING</c> environment variable equals
    /// "true" (case-insensitive), and <see langword="false"/> otherwise.
    /// </summary>
    [Fact]
    public void IsVerbose_ReflectsVerboseLoggingEnvironmentVariable()
    {
        var envValue = Environment.GetEnvironmentVariable("VERBOSE_LOGGING");
        var expectedVerbose = string.Equals(envValue, "true", StringComparison.OrdinalIgnoreCase);

        Assert.Equal(expectedVerbose, WorkerLogger.IsVerbose);
    }

    #endregion

    // ── LogBlock ─────────────────────────────────────────────────────────────

    #region LogBlock — full header/footer box-drawing structure for short content

    /// <summary>
    /// When content length is within the preview limit,
    /// <see cref="WorkerLogger.LogBlock"/> must write the header text,
    /// the full content body, and the end trailer containing the character count.
    /// </summary>
    [Fact]
    public void LogBlock_ShortContent_WritesHeaderContentAndCharCountTrailer()
    {
        var logger = new WorkerLogger("Cat");
        const string Content = "short content";

        logger.LogBlock("My Header", Content, previewLength: 500);

        var output = _stdOut.ToString();
        // Header label must appear inside the box.
        Assert.Contains("My Header", output);
        // Full content must be present verbatim.
        Assert.Contains(Content, output);
        // The end-of-block trailer must include the char count.
        Assert.Contains($"{Content.Length} chars", output);
    }

    #endregion

    #region LogBlock — box-drawing characters in header for short content

    /// <summary>
    /// The full-content path of <see cref="WorkerLogger.LogBlock"/> must use
    /// box-drawing characters (┌, └, │) to frame the header label, giving
    /// log output a structured visual appearance.
    /// </summary>
    [Fact]
    public void LogBlock_ShortContent_OutputContainsBoxDrawingCharacters()
    {
        var logger = new WorkerLogger("Cat");

        logger.LogBlock("Header", "body text", previewLength: 500);

        var output = _stdOut.ToString();
        Assert.Contains("┌", output);
        Assert.Contains("└", output);
        Assert.Contains("│", output);
    }

    #endregion

    #region LogBlock — category prefix on every structural line

    /// <summary>
    /// Every structural line emitted by <see cref="WorkerLogger.LogBlock"/>
    /// (the box borders and the end-of-block trailer) must carry the
    /// "[category]" prefix so the log source is always identifiable.
    /// </summary>
    [Fact]
    public void LogBlock_ShortContent_StructuralLinesCarryCategoryPrefix()
    {
        const string Category = "block-worker";
        var logger = new WorkerLogger(Category);
        var expected = $"[{Category}]";

        logger.LogBlock("Header", "content", previewLength: 500);

        var output = _stdOut.ToString();
        // The output must contain at least one structural line with the prefix.
        Assert.Contains(expected, output);
    }

    #endregion

    #region LogBlock — truncation notice when content exceeds preview limit

    /// <summary>
    /// When verbose mode is disabled and content exceeds the preview length,
    /// <see cref="WorkerLogger.LogBlock"/> must write only the preview slice
    /// and append a "truncated" notice directing the user to enable verbose logging.
    /// </summary>
    [Fact]
    public void LogBlock_LongContentAndVerboseDisabled_WritesPreviewAndTruncationNotice()
    {
        if (WorkerLogger.IsVerbose)
            return; // verbose mode active — truncation path does not apply

        var logger = new WorkerLogger("Cat");
        var content = new string('x', 1000);
        const int Preview = 200;

        logger.LogBlock("Header", content, previewLength: Preview);

        var output = _stdOut.ToString();
        // The truncation notice must appear.
        Assert.Contains("truncated", output);
        // Exactly the first Preview characters of content must be present.
        Assert.Contains(content[..Preview], output);
        // The full content must NOT be present (only the preview was written).
        Assert.DoesNotContain(content[..(Preview + 1)], output);
    }

    #endregion

    #region LogBlock — truncation notice references VERBOSE_LOGGING variable

    /// <summary>
    /// The truncation notice written by <see cref="WorkerLogger.LogBlock"/>
    /// must name the <c>VERBOSE_LOGGING</c> environment variable so users know
    /// how to enable full output.
    /// </summary>
    [Fact]
    public void LogBlock_LongContentAndVerboseDisabled_TruncationNoticeReferencesEnvVar()
    {
        if (WorkerLogger.IsVerbose)
            return; // verbose mode active — truncation path does not apply

        var logger = new WorkerLogger("Cat");
        var content = new string('z', 600);

        logger.LogBlock("Header", content, previewLength: 100);

        Assert.Contains("VERBOSE_LOGGING", _stdOut.ToString());
    }

    #endregion

    #region LogBlock — total char count in truncation header line

    /// <summary>
    /// The header line of the truncated path must include the total character
    /// count of the full content so users know how much was omitted.
    /// </summary>
    [Fact]
    public void LogBlock_LongContentAndVerboseDisabled_TruncationHeaderIncludesTotalCharCount()
    {
        if (WorkerLogger.IsVerbose)
            return; // verbose mode active — truncation path does not apply

        var logger = new WorkerLogger("Cat");
        var content = new string('a', 800);

        logger.LogBlock("H", content, previewLength: 50);

        // The header line includes the total char count of the full content.
        Assert.Contains($"{content.Length} chars", _stdOut.ToString());
    }

    #endregion

    // ── Cross-method stream isolation ────────────────────────────────────────

    #region Stream isolation — Info and Error write to different streams

    /// <summary>
    /// Verifies that <see cref="WorkerLogger.Info"/> and
    /// <see cref="WorkerLogger.Error"/> route their output to different streams,
    /// ensuring that log consumers can process errors separately from info.
    /// </summary>
    [Fact]
    public void InfoAndError_RouteToDistinctStreams()
    {
        var logger = new WorkerLogger("worker-42");

        logger.Info("info-message");
        logger.Error("error-message");

        // Info text must be in stdout, not stderr.
        Assert.Contains("info-message", _stdOut.ToString());
        Assert.DoesNotContain("info-message", _stdErr.ToString());

        // Error text must be in stderr, not stdout.
        Assert.Contains("error-message", _stdErr.ToString());
        Assert.DoesNotContain("error-message", _stdOut.ToString());
    }

    #endregion

    #region Stream isolation — category brackets consistent across Info and Error

    /// <summary>
    /// Both <see cref="WorkerLogger.Info"/> and <see cref="WorkerLogger.Error"/>
    /// must use the same bracket-wrapped category format "[category]" so log
    /// lines from a single logger instance are visually consistent regardless
    /// of the severity level.
    /// </summary>
    [Fact]
    public void InfoAndError_CategoryFormat_IncludesSquareBracketsOnBothStreams()
    {
        var logger = new WorkerLogger("worker-42");

        logger.Info("ping");
        logger.Error("pong");

        Assert.Contains("[worker-42]", _stdOut.ToString());
        Assert.Contains("[worker-42]", _stdErr.ToString());
    }

    #endregion
}
