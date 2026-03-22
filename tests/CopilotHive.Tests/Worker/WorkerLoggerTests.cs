extern alias WorkerAssembly;

using System.IO;
using WorkerAssembly::CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for <see cref="WorkerLogger"/>: verifies that Info, Error, Debug,
/// and LogBlock methods write output in the expected format.
/// </summary>
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

    #region Info — standard message

    /// <summary>
    /// <see cref="WorkerLogger.Info"/> must write a line formatted as
    /// "[category] message" to standard output.
    /// </summary>
    [Fact]
    public void Info_StandardMessage_WritesToStdOut()
    {
        var logger = new WorkerLogger("MyCategory");

        logger.Info("hello world");

        var output = _stdOut.ToString();
        Assert.Contains("[MyCategory] hello world", output);
    }

    #endregion

    #region Info — empty message

    /// <summary>
    /// <see cref="WorkerLogger.Info"/> must handle an empty string without
    /// throwing, and must still write the category prefix.
    /// </summary>
    [Fact]
    public void Info_EmptyMessage_WritesCategoryPrefixWithNoMessage()
    {
        var logger = new WorkerLogger("Cat");

        logger.Info(string.Empty);

        var output = _stdOut.ToString();
        Assert.Contains("[Cat] ", output);
    }

    #endregion

    #region Info — special characters

    /// <summary>
    /// <see cref="WorkerLogger.Info"/> must preserve special characters in the
    /// message without alteration.
    /// </summary>
    [Fact]
    public void Info_SpecialCharacters_OutputPreservesCharacters()
    {
        var logger = new WorkerLogger("worker");
        const string Special = "line1\ttab\nnewline & <xml> \"quotes\"";

        logger.Info(Special);

        var output = _stdOut.ToString();
        Assert.Contains(Special, output);
    }

    #endregion

    #region Info — does not write to stderr

    /// <summary>
    /// <see cref="WorkerLogger.Info"/> must not write anything to standard error.
    /// </summary>
    [Fact]
    public void Info_DoesNotWriteToStdErr()
    {
        var logger = new WorkerLogger("Cat");

        logger.Info("message");

        Assert.Equal(string.Empty, _stdErr.ToString());
    }

    #endregion

    // ── Error ───────────────────────────────────────────────────────────────

    #region Error — standard message

    /// <summary>
    /// <see cref="WorkerLogger.Error"/> must write a line formatted as
    /// "[category] ERROR: message" to standard error.
    /// </summary>
    [Fact]
    public void Error_StandardMessage_WritesToStdErr()
    {
        var logger = new WorkerLogger("MyCategory");

        logger.Error("something went wrong");

        var output = _stdErr.ToString();
        Assert.Contains("[MyCategory] ERROR: something went wrong", output);
    }

    #endregion

    #region Error — does not write to stdout

    /// <summary>
    /// <see cref="WorkerLogger.Error"/> must not write anything to standard output.
    /// </summary>
    [Fact]
    public void Error_DoesNotWriteToStdOut()
    {
        var logger = new WorkerLogger("Cat");

        logger.Error("oops");

        Assert.Equal(string.Empty, _stdOut.ToString());
    }

    #endregion

    #region Error — empty message

    /// <summary>
    /// <see cref="WorkerLogger.Error"/> must handle an empty string without
    /// throwing and still emit the ERROR prefix.
    /// </summary>
    [Fact]
    public void Error_EmptyMessage_WritesErrorPrefixWithNoMessage()
    {
        var logger = new WorkerLogger("Cat");

        logger.Error(string.Empty);

        var output = _stdErr.ToString();
        Assert.Contains("[Cat] ERROR: ", output);
    }

    #endregion

    #region Error — category preserved in output

    /// <summary>
    /// Verifies that the category name passed to the constructor is included in
    /// the formatted error line.
    /// </summary>
    [Fact]
    public void Error_CategoryPreservedInOutput()
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
    /// <see cref="WorkerLogger.Debug"/> must not write anything to standard output.
    /// </summary>
    [Fact]
    public void Debug_VerboseDisabled_ProducesNoOutput()
    {
        // Guard: this test is only meaningful when VERBOSE_LOGGING is not "true".
        // In the normal CI / test environment the variable is unset.
        if (WorkerLogger.IsVerbose)
            return; // verbose mode active — skip rather than fail

        var logger = new WorkerLogger("Cat");

        logger.Debug("internal detail");

        Assert.Equal(string.Empty, _stdOut.ToString());
        Assert.Equal(string.Empty, _stdErr.ToString());
    }

    #endregion

    #region Debug — written when verbose is enabled

    /// <summary>
    /// When <see cref="WorkerLogger.IsVerbose"/> is <see langword="true"/>,
    /// <see cref="WorkerLogger.Debug"/> must write a line formatted as
    /// "[category] DEBUG: message" to standard output.
    /// </summary>
    [Fact]
    public void Debug_VerboseEnabled_WritesDebugLineToStdOut()
    {
        // This test only exercises the verbose branch when VERBOSE_LOGGING is set.
        if (!WorkerLogger.IsVerbose)
            return; // verbose mode inactive — skip rather than fail

        var logger = new WorkerLogger("Cat");

        logger.Debug("trace info");

        Assert.Contains("[Cat] DEBUG: trace info", _stdOut.ToString());
    }

    #endregion

    #region Debug — IsVerbose reflects environment variable

    /// <summary>
    /// <see cref="WorkerLogger.IsVerbose"/> must be <see langword="false"/> when
    /// the <c>VERBOSE_LOGGING</c> environment variable is absent or not "true".
    /// </summary>
    [Fact]
    public void IsVerbose_WhenEnvVarNotTrue_IsFalse()
    {
        var envValue = Environment.GetEnvironmentVariable("VERBOSE_LOGGING");
        var expectedVerbose = string.Equals(envValue, "true", StringComparison.OrdinalIgnoreCase);

        Assert.Equal(expectedVerbose, WorkerLogger.IsVerbose);
    }

    #endregion

    // ── LogBlock ─────────────────────────────────────────────────────────────

    #region LogBlock — short content shown in full

    /// <summary>
    /// When content length is within the preview limit,
    /// <see cref="WorkerLogger.LogBlock"/> must include the full content and the
    /// decorative header/footer.
    /// </summary>
    [Fact]
    public void LogBlock_ShortContent_WritesFullContentWithHeader()
    {
        var logger = new WorkerLogger("Cat");
        const string Content = "short content";

        logger.LogBlock("My Header", Content, previewLength: 500);

        var output = _stdOut.ToString();
        Assert.Contains("My Header", output);
        Assert.Contains(Content, output);
        // Full-content branch ends with the char-count trailer.
        Assert.Contains($"{Content.Length} chars", output);
    }

    #endregion

    #region LogBlock — long content truncated when not verbose

    /// <summary>
    /// When verbose mode is disabled and content exceeds the preview length limit,
    /// <see cref="WorkerLogger.LogBlock"/> must write only the first preview-length
    /// characters and append a truncation notice.
    /// </summary>
    [Fact]
    public void LogBlock_LongContentAndVerboseDisabled_TruncatesOutput()
    {
        if (WorkerLogger.IsVerbose)
            return; // verbose mode active — truncation does not apply

        var logger = new WorkerLogger("Cat");
        var content = new string('x', 1000);
        const int Preview = 200;

        logger.LogBlock("Header", content, previewLength: Preview);

        var output = _stdOut.ToString();
        Assert.Contains("truncated", output);
        Assert.Contains(content[..Preview], output);
    }

    #endregion

    // ── Category format consistency ────────────────────────────────────────

    #region Category format — brackets included for each method

    /// <summary>
    /// Verifies that both <see cref="WorkerLogger.Info"/> and
    /// <see cref="WorkerLogger.Error"/> use the same bracket-wrapped category
    /// format "[category]".
    /// </summary>
    [Fact]
    public void InfoAndError_CategoryFormat_IncludesSquareBrackets()
    {
        var logger = new WorkerLogger("worker-42");

        logger.Info("ping");
        logger.Error("pong");

        Assert.Contains("[worker-42]", _stdOut.ToString());
        Assert.Contains("[worker-42]", _stdErr.ToString());
    }

    #endregion
}
