using System.Text;

using CopilotHive.Services;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Focused regressions for Microsoft.Testing.Platform (MTP) failure blocks whose stack trace is
/// closed by a <c>--- End of stack trace … ---</c> marker. The marker terminates the stack section
/// exactly as it does inside a VSTest block: the frames BEFORE it stay in the stack trace while the
/// marker itself and every continuation line after it belong to neither the error nor the stack.
/// </summary>
/// <remarks>
/// <para>
/// The primary fixture is the user-supplied, SANITIZED, PARTIAL excerpt from CI run 43606604223 /
/// job 132482481229. It is neither raw bytes nor claimed to be the complete log: the
/// <c>.../CopilotHive.Tests.dll</c> and <c>.../ComposerToolTests.GitRedaction.cs</c> paths and the
/// <c>···</c> markers are reproduced exactly as the supplied text shows them, never reconstructed
/// or guessed at.
/// </para>
/// <para>
/// The timestamped variants prefix each line with a clearly SYNTHETIC GitHub Actions timestamp.
/// They prove the parser tolerates ordinary raw-log framing; they are NOT evidence of, and say
/// nothing about, the prefixes the original run actually emitted.
/// </para>
/// <para>
/// These tests are the source-level evidence for the field-separation defect (stack frames and the
/// end marker landing in <c>Error</c> with an empty <c>StackTrace</c>). They are not evidence about
/// why the original run reported no parsed failures at all — that remains unexplained without the
/// raw log bytes.
/// </para>
/// </remarks>
public sealed class CiLogStreamParserMtpStackTraceMarkerTests
{
    private const string FullTheoryName =
        "CopilotHive.Tests.Orchestration.ComposerToolTests.GitRedaction_ActualOutput_RemovesCredentialsAndRetainsGitContent(tool: \"blame\")";

    private const string Frame =
        "at CopilotHive.Tests.Orchestration.ComposerToolTests.GitRedaction_ActualOutput_RemovesCredentialsAndRetainsGitContent(String tool) in .../ComposerToolTests.GitRedaction.cs:113";

    private const string EndMarker = "--- End of stack trace from previous location ---";

    /// <summary>
    /// The user-supplied sanitized excerpt, byte-for-byte as supplied, with ordinary LF separators.
    /// </summary>
    private const string SuppliedExcerpt =
        "failed " + FullTheoryName + " (36ms)\n"
        + "  from .../CopilotHive.Tests.dll (net10.0|x64)\n"
        + "  Assert.Contains() Failure: Sub-string not found\n"
        + "  String: \"fa1efcb (Test 2026-11-14 14:57:49 +0000 1) unchang\"···\n"
        + "  Not found: \"fa1efcbb\"\n"
        + "  " + Frame + "\n"
        + "  " + EndMarker + "\n";

    /// <summary>
    /// The assertion text the excerpt carries. The first line loses its indentation to the field's
    /// final <c>Trim()</c>; interior lines keep theirs.
    /// </summary>
    private const string SuppliedError =
        "Assert.Contains() Failure: Sub-string not found\n"
        + "  String: \"fa1efcb (Test 2026-11-14 14:57:49 +0000 1) unchang\"···\n"
        + "  Not found: \"fa1efcbb\"";

    /// <summary>The single at-frame the excerpt carries, trimmed of its leading indentation.</summary>
    private const string SuppliedStackTrace = Frame;

    /// <summary>
    /// A clearly SYNTHETIC GitHub Actions timestamp prefix for the generated variants. It is not
    /// evidence of the prefix the original raw log carried.
    /// </summary>
    private const string SyntheticStamp = "2026-01-02T03:04:05.0000000Z ";

    /// <summary>
    /// The expected error text of a TIMESTAMPED block. Stripping a GitHub Actions timestamp consumes
    /// the whitespace that followed it, so every line of a timestamped block loses its leading
    /// indentation — the documented behavior of the existing MTP extraction, unchanged here.
    /// </summary>
    private static readonly string UnindentedSuppliedError = string.Join(
        '\n',
        SuppliedError.Split('\n').Select(line => line.TrimStart()));

    [Fact]
    public void ParseTestFailuresFromLogs_SuppliedMtpExcerpt_SeparatesStackAtEndMarker()
    {
        var failures = CiMonitorService.ParseTestFailuresFromLogs(SuppliedExcerpt);

        var failure = Assert.Single(failures);

        // The full theory identity, including the label suffix with its spaces and quotes.
        Assert.Equal(FullTheoryName, failure.TestName);

        // The assertion text is preserved exactly — no stack frame and no end marker leaked into it.
        Assert.Equal(SuppliedError, failure.Error);
        Assert.DoesNotContain(EndMarker, failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("GitRedaction.cs:113", failure.Error, StringComparison.Ordinal);

        // The at-frame — the frame BEFORE the marker — is the stack trace.
        Assert.Equal(SuppliedStackTrace, failure.StackTrace);
        Assert.DoesNotContain(EndMarker, failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("Assert.Contains", failure.StackTrace, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsync_SuppliedMtpExcerptStreamed_SeparatesStackAtEndMarker(bool crlf)
    {
        var log = crlf ? SuppliedExcerpt.Replace("\n", "\r\n", StringComparison.Ordinal) : SuppliedExcerpt;

        var failures = await ParseStreamingAsync(log);

        var failure = Assert.Single(failures);
        Assert.Equal(FullTheoryName, failure.TestName);
        Assert.Equal(SuppliedError, NormalizeSeparators(failure.Error));
        Assert.Equal(SuppliedStackTrace, failure.StackTrace);
    }

    /// <summary>
    /// The supplied excerpt framed as a raw Actions log line-by-line: a synthetic timestamp prefix on
    /// every line, in both LF and CRLF framing, streamed through the production parser. The parsed
    /// fields must match the untimestamped expectations once the CRLF separators are normalized.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsync_SuppliedMtpExcerptWithSyntheticTimestamps_SeparatesStackAtEndMarker(bool crlf)
    {
        var log = FramedExcerpt(crlf);

        var failures = await ParseStreamingAsync(log);

        var failure = Assert.Single(failures);
        Assert.Equal(FullTheoryName, failure.TestName);

        // The CRLF framing keeps "\r\n" between interior lines (only the field's outer ends are
        // trimmed) and the synthetic stamp takes each line's indentation with it, so the comparison
        // normalizes separators and expects the documented unindented shape.
        Assert.Equal(UnindentedSuppliedError, NormalizeSeparators(failure.Error));
        Assert.Equal(SuppliedStackTrace, NormalizeSeparators(failure.StackTrace));

        Assert.DoesNotContain(EndMarker, failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(EndMarker, failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticStamp, failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticStamp, failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("from .../CopilotHive.Tests.dll", failure.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same excerpt streamed so a 16 KiB chunk boundary falls INSIDE the block: the first case
    /// splits the end-marker line, the second splits the at-frame line. Both LF and CRLF framing.
    /// </summary>
    [Theory]
    [InlineData(false, "marker")]
    [InlineData(true, "marker")]
    [InlineData(false, "frame")]
    [InlineData(true, "frame")]
    public async Task ScanAsync_SuppliedMtpExcerptSplitAtChunkBoundary_SeparatesStackAtEndMarker(
        bool crlf, string splitTarget)
    {
        var block = FramedExcerpt(crlf);
        var target = splitTarget == "marker"
            ? SyntheticStamp + "  " + EndMarker
            : SyntheticStamp + "  " + Frame;
        var targetIndex = block.IndexOf(target, StringComparison.Ordinal);
        Assert.True(targetIndex >= 0, "The split target line must exist in the framed excerpt.");

        // Place the second chunk boundary halfway through the target line, measured in BYTES (the
        // stream's own unit): the excerpt carries non-ASCII elision characters, so byte offsets and
        // character offsets differ.
        var boundary = CiLogStreamParser.StreamChunkBytes * 2;
        var targetByteOffset = Encoding.UTF8.GetByteCount(block[..targetIndex]);
        var halfLineBytes = Encoding.UTF8.GetByteCount(target[..(target.Length / 2)]);
        var fillerBytes = boundary - targetByteOffset - halfLineBytes;
        Assert.True(fillerBytes > 0, "The framed block must start before the second chunk boundary.");

        var filler = Filler(fillerBytes);
        var log = filler + block;

        // Geometry: the chunk boundary really falls inside the target line.
        var targetStartByte = Encoding.UTF8.GetByteCount(log[..(filler.Length + targetIndex)]);
        var targetEndByte = targetStartByte + Encoding.UTF8.GetByteCount(target);
        Assert.True(targetStartByte < boundary && boundary < targetEndByte,
            $"The chunk boundary must fall inside the {splitTarget} line ({targetStartByte} < {boundary} < {targetEndByte}).");

        var failures = await ParseStreamingAsync(log);

        var failure = Assert.Single(failures);
        Assert.Equal(FullTheoryName, failure.TestName);
        Assert.Equal(UnindentedSuppliedError, NormalizeSeparators(failure.Error));
        Assert.Equal(SuppliedStackTrace, NormalizeSeparators(failure.StackTrace));
        Assert.DoesNotContain(EndMarker, failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(EndMarker, failure.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>
    /// A marked stack followed by continuation frames: everything from the marker onwards is part of
    /// neither field, exactly as the VSTest extractor treats the same marker.
    /// </summary>
    [Fact]
    public void ParseTestFailuresFromLogs_MtpEndMarkerWithContinuationFrames_ExcludesMarkerAndContinuation()
    {
        // The continuation frames a marker introduces: the async state machine that awaited the
        // failing test, printed after the marker. MTP prints them with the same indentation.
        var log = SuppliedExcerpt
            + "  at Xunit.Sdk.TestInvoker`1.<>c__DisplayClass48_0.<<InvokeTestMethodAsync>b__1>d.MoveNext()\n"
            + "  at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\n";

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal(FullTheoryName, failure.TestName);
        Assert.Equal(SuppliedError, failure.Error);
        Assert.Equal(SuppliedStackTrace, failure.StackTrace);
        Assert.DoesNotContain("TestInvoker", failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("TestInvoker", failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskAwaiter", failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskAwaiter", failure.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two marked MTP blocks in one log, the second closed by MTP's per-assembly result footer: each
    /// block keeps its own fields, and neither marker nor footer text reaches either failure.
    /// </summary>
    [Fact]
    public void ParseTestFailuresFromLogs_TwoMarkedMtpBlocksBeforeAssemblyFooter_BothFieldsClean()
    {
        var log = SuppliedExcerpt
            + "failed CopilotHive.Tests.Other.Trimmed (0ms)\n"
            + "  from .../CopilotHive.Tests.dll (net10.0|x64)\n"
            + "  Assert.Equal() Failure: Values differ\n"
            + "  Expected: 42\n"
            + "  Actual:   40\n"
            + "  at CopilotHive.Tests.Other.Trimmed() in .../OtherTrimmedTests.cs:21\n"
            + "  " + EndMarker + "\n"
            + "/copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64) failed with 2 error(s) (4m 54s 690ms)\n"
            + "Test run summary: Failed!\n";

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        Assert.Equal(2, failures.Count);

        Assert.Equal(FullTheoryName, failures[0].TestName);
        Assert.Equal(SuppliedError, failures[0].Error);
        Assert.Equal(SuppliedStackTrace, failures[0].StackTrace);

        Assert.Equal("CopilotHive.Tests.Other.Trimmed", failures[1].TestName);
        Assert.Equal("Assert.Equal() Failure: Values differ\n  Expected: 42\n  Actual:   40", failures[1].Error);
        Assert.Equal(
            "at CopilotHive.Tests.Other.Trimmed() in .../OtherTrimmedTests.cs:21",
            failures[1].StackTrace);

        foreach (var failure in failures)
        {
            Assert.DoesNotContain(EndMarker, failure.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(EndMarker, failure.StackTrace, StringComparison.Ordinal);
            Assert.DoesNotContain("failed with 2 error(s)", failure.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("failed with 2 error(s)", failure.StackTrace, StringComparison.Ordinal);
            Assert.DoesNotContain("Test run summary", failure.Error, StringComparison.Ordinal);
        }

        // No cross-block contamination: the second block's text never reaches the first failure and
        // the first block's assertion text never reaches the second.
        Assert.DoesNotContain("Other.Trimmed", failures[0].Error, StringComparison.Ordinal);
        Assert.DoesNotContain("unchang", failures[1].Error, StringComparison.Ordinal);
        Assert.DoesNotContain("unchang", failures[1].StackTrace, StringComparison.Ordinal);
    }

    /// <summary>
    /// A marked block is closed by its TERMINATOR line, not by end of stream. Both terminator shapes
    /// are covered separately — MTP's next per-test result line
    /// (<c>MtpBlockTerminatorRegex</c>) and its per-assembly result footer
    /// (<c>MtpAssemblyResultRegex</c>) — and each row observes retention INCREMENTALLY through the
    /// production parser: nothing is retained while the block is still open, the failure appears
    /// immediately after the terminator line is fed, and <c>Complete()</c> afterwards adds nothing.
    /// That ordering is what pins the matching predicate: a row whose predicate is removed keeps the
    /// block open through the terminator (retention still empty) and only closes at EOF.
    /// </summary>
    /// <remarks>
    /// The two terminator lines are deliberately non-interchangeable: the result line carries no
    /// <c>.dll (tfm|arch)</c> prefix, so the assembly-result predicate cannot match it, and the footer
    /// starts with the assembly path rather than a verb, so the next-result predicate cannot match it.
    /// Every row runs in LF and CRLF framing.
    /// </remarks>
    [Theory]
    [InlineData(false, MarkedBlockTerminator.NextResult)]
    [InlineData(true, MarkedBlockTerminator.NextResult)]
    [InlineData(false, MarkedBlockTerminator.AssemblyFooter)]
    [InlineData(true, MarkedBlockTerminator.AssemblyFooter)]
    public void Feed_MarkedMtpBlock_RetentionOpensOnlyWhenTheTerminatorArrives(
        bool crlf, MarkedBlockTerminator terminator)
    {
        var separator = crlf ? "\r\n" : "\n";
        var terminatorLine = terminator switch
        {
            MarkedBlockTerminator.NextResult => "passed CopilotHive.Tests.Other.Passing (0ms)" + separator,
            MarkedBlockTerminator.AssemblyFooter =>
                "/copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64) failed with 1 error(s) (1s 2ms)" + separator,
            _ => throw new ArgumentOutOfRangeException(nameof(terminator), terminator, "Unhandled terminator shape."),
        };

        var retention = CiFailureRetention.CreateUnbounded();
        var parser = CiLogStreamParser.CreateProduction(retention);

        // Feed the marked block line by line. No terminator yet: the block is still OPEN, so nothing
        // is retained and no failure has been reported.
        foreach (var line in SuppliedExcerpt.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            parser.Feed(line + separator);
        Assert.Empty(retention.Failures);

        // The terminator closes the block: the failure is retained IMMEDIATELY, before any EOF.
        parser.Feed(terminatorLine);
        var failure = Assert.Single(retention.Failures);

        Assert.Equal(FullTheoryName, failure.TestName);
        Assert.Equal(SuppliedError, NormalizeSeparators(failure.Error));
        Assert.Equal(SuppliedStackTrace, NormalizeSeparators(failure.StackTrace));
        Assert.DoesNotContain(EndMarker, failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(EndMarker, failure.StackTrace, StringComparison.Ordinal);

        // The terminator's own text is in NEITHER field, whichever predicate matched it.
        var terminatorText = terminatorLine.TrimEnd('\r', '\n').Trim();
        Assert.DoesNotContain(terminatorText, failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(terminatorText, failure.StackTrace, StringComparison.Ordinal);

        // Completing at EOF neither re-reports the block nor changes the retained fields.
        parser.Complete();
        var afterComplete = Assert.Single(retention.Failures);
        Assert.Equal(failure, afterComplete);
    }

    /// <summary>Which MTP terminator shape a row drives, so each predicate has its own rows.</summary>
    public enum MarkedBlockTerminator
    {
        /// <summary>The next per-test result line (<c>MtpBlockTerminatorRegex</c>).</summary>
        NextResult,

        /// <summary>The per-assembly result footer (<c>MtpAssemblyResultRegex</c>).</summary>
        AssemblyFooter,
    }

    /// <summary>
    /// The per-block cap keeps its existing accounting for marked blocks, in both interleavings:
    /// (a) the marker lies BEYOND the cap, so the retained prefix is a plain trailing run of frames,
    /// and (b) the cap binds after the marker, so continuation frames follow a retained marker. In
    /// both cases the truncation marker with its omitted-character count is still reported — the
    /// marker bound changes which LINES become which FIELD, never the retention accounting.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScanAsync_MarkedMtpBlockBeyondBlockCap_KeepsTruncationAccounting(bool markerBeforeCap)
    {
        // Each frame line costs 30 characters (29 + separator), so the 16 KiB cap binds after
        // roughly 546 lines: a marker placed after 1,000 frames is definitely past it.
        static string Frames(string prefix, int first, int count)
        {
            var sb = new StringBuilder();
            for (var i = first; i < first + count; i++)
                sb.Append("    at ").Append(prefix).Append(i.ToString("D4")).Append("()\n");
            return sb.ToString();
        }

        const string header =
            "failed Tests.Big.Marked (12ms)\n"
            + "  from /repo/Tests.dll (net10.0|x64)\n"
            + "  big marked error\n";

        // (b) marker inside the retained prefix, continuation frames after it.
        // (a) marker and every continuation frame past the cap.
        var log = markerBeforeCap
            ? header + Frames("Big.Marked.Frame", 0, 5) + "  " + EndMarker + "\n" + Frames("Big.Marked.Continuation", 0, 1_000)
            : header + Frames("Big.Marked.Frame", 0, 1_000) + "  " + EndMarker + "\n" + Frames("Big.Marked.Continuation", 0, 5);

        var failures = await ParseStreamingAsync(log);

        var failure = Assert.Single(failures);
        Assert.Equal("Tests.Big.Marked", failure.TestName);
        Assert.Equal("big marked error", failure.Error);
        Assert.Contains("Big.Marked.Frame0000", failure.StackTrace, StringComparison.Ordinal);
        Assert.Contains("characters omitted]…", failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain(EndMarker, failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(EndMarker, failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("Continuation", failure.Error, StringComparison.Ordinal);

        if (markerBeforeCap)
        {
            // The marker is retained, so the continuation frames after it are in NEITHER field.
            Assert.Contains("Big.Marked.Frame0004", failure.StackTrace, StringComparison.Ordinal);
            Assert.DoesNotContain("Continuation", failure.StackTrace, StringComparison.Ordinal);
        }
        else
        {
            // The marker itself fell past the cap, so the retained prefix is a plain run of frames
            // and the cap — not the marker — bounds the stack trace.
            Assert.DoesNotContain("Frame0999", failure.StackTrace, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The marker must preserve ALL frames before it, in log order — not just the last one.
    /// Pre-fix, the marker line ended the backwards frame scan at once, so every frame AND the
    /// marker landed in <c>Error</c> with an empty <c>StackTrace</c>; bounding that scan at the
    /// marker is what recovers the whole ordered trace.
    /// </summary>
    [Fact]
    public void ParseTestFailuresFromLogs_MarkedMtpBlockWithSeveralFrames_PreservesAllFramesBeforeMarker()
    {
        const string firstFrame =
            "at CopilotHive.Tests.MultiFrame.Failing(String value) in .../MultiFrameTests.cs:17";
        const string secondFrame =
            "at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)";
        const string thirdFrame = Frame;
        var log = SuppliedExcerpt.Replace(
            "  " + Frame + "\n",
            "  " + firstFrame + "\n  " + secondFrame + "\n  " + thirdFrame + "\n",
            StringComparison.Ordinal);

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal(FullTheoryName, failure.TestName);
        Assert.Equal(SuppliedError, failure.Error);
        // SliceLines trims only the field's outer ends: interior frames keep the line's indentation.
        Assert.Equal(
            string.Join("\n", firstFrame, "  " + secondFrame, "  " + thirdFrame),
            failure.StackTrace);
        Assert.DoesNotContain(EndMarker, failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(EndMarker, failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain(firstFrame, failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(secondFrame, failure.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A marker-SHAPED line inside the assertion text must not be mistaken for the stack terminator.
    /// An assertion can print text that STARTS with the marker (e.g. the failing test renders log
    /// lines it parsed), and <c>StackTraceEndMarkerRegex</c> only anchors at the line start, so such
    /// a line looks exactly like a terminator. It is a terminator only when it directly follows a
    /// real <c>at …</c> frame. With the guard removed, <c>TryExtractMtpFailure</c> sets its body end
    /// at that assertion line: the marker-shaped text and every line after it — the real frame and
    /// the genuine marker — are dropped from BOTH fields, leaving a shortened <c>Error</c> and an
    /// empty <c>StackTrace</c>. The failure itself is still emitted when earlier error text survives;
    /// the separate row below covers the FIRST-BODY-LINE case where both slices really are empty and
    /// <c>Emit</c> is never reached.
    /// </summary>
    /// <remarks>
    /// The row's assertion text carries a marker-shaped line BEFORE any frame, then a real frame, then
    /// a genuine marker. The marker-shaped assertion line stays in the error text, the frame becomes
    /// the stack trace, and the genuine trailing marker is excluded from both fields. Rows are driven
    /// through the whole-log entry point in LF and CRLF framing, so framing cannot be what makes the
    /// separation work. The discriminating assertions are the exact <c>Error</c> (the marker-shaped
    /// line is retained once, not truncated) and the non-empty <c>StackTrace</c> (the real frame).
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseTestFailuresFromLogs_MarkerShapedAssertionLineBeforeAnyFrame_KeepsFailure(bool crlf)
    {
        var separator = crlf ? "\r\n" : "\n";

        // The marker text at the START of the line, as the extractor's anchored regex sees it, with
        // trailing assertion detail so the line is unmistakably assertion content.
        const string markerShapedAssertion = "--- End of stack trace from previous location --- (1 of 2 expected)";

        var lines = SuppliedExcerpt.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        var frameIndex = lines.FindIndex(line => line == "  " + Frame);
        Assert.True(frameIndex > 0, "The supplied frame line must be present in the excerpt.");

        // Insert the marker-shaped assertion line BEFORE the frame, in the assertion region.
        lines.Insert(frameIndex, "  " + markerShapedAssertion);
        var log = string.Join(separator, lines) + separator;

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        // The failure must still be emitted, with its FULL error text: under an unconditional marker
        // bound the marker-shaped line (and the frame after it) are truncated from Error instead.
        var failure = Assert.Single(failures);
        Assert.Equal(FullTheoryName, failure.TestName);

        // The marker-shaped line is assertion text, so it stays in the error text, ahead of the frame
        // that opens the stack section.
        Assert.Equal(
            SuppliedError + "\n  " + markerShapedAssertion,
            NormalizeSeparators(failure.Error));

        // The real frame is the stack trace; the GENUINE trailing marker is excluded from both fields.
        Assert.Equal(SuppliedStackTrace, NormalizeSeparators(failure.StackTrace));
        Assert.DoesNotContain(Frame, failure.Error, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(failure.Error, markerShapedAssertion));
        Assert.DoesNotContain(markerShapedAssertion, failure.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>
    /// The FIRST body line is a marker-shaped assertion line, with no frame and no other error text
    /// before it. An unconditional marker bound would set bodyEnd == bodyStart here, so both slices
    /// are empty and <c>TryExtractMtpFailure</c>'s empty-field guard returns without calling
    /// <c>Emit</c> — this is the genuine "zero parsed failures" case. With the frame-run guard the
    /// line is ordinary assertion text, so the failure IS emitted with that text in <c>Error</c>.
    /// </summary>
    /// <remarks>
    /// Driven through the PRODUCTION parser through both the whole-log and streaming entry points,
    /// in LF and CRLF framing. <c>ParsedFailureCount</c> is asserted directly so the row measures
    /// emission, not just the retained list: a dropped block leaves the counter at zero.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParseTestFailuresFromLogs_MarkerShapedFirstBodyLine_EmitsFailureNotDropped(bool crlf)
    {
        var separator = crlf ? "\r\n" : "\n";
        const string markerShapedAssertion = "--- End of stack trace from previous location --- (quoted by the assertion)";
        var log = string.Join(separator,
            "failed Tests.MarkerShaped.FirstBody (8ms)",
            "  from /repo/bin/Release/net10.0/Tests.dll (net10.0|x64)",
            "  " + markerShapedAssertion) + separator;

        // Whole-log path: count the emitted occurrence directly.
        var wholeRetention = CiFailureRetention.CreateUnbounded();
        var wholeParser = CiLogStreamParser.CreateWholeLog(wholeRetention);
        wholeParser.Feed(log);
        wholeParser.Complete();
        Assert.Equal(1, wholeParser.ParsedFailureCount);
        var wholeFailure = Assert.Single(wholeRetention.Failures);
        Assert.Equal("Tests.MarkerShaped.FirstBody", wholeFailure.TestName);
        Assert.Equal(markerShapedAssertion, NormalizeSeparators(wholeFailure.Error));
        Assert.Empty(wholeFailure.StackTrace);

        // Production streaming path with per-line redaction: the same emission decision.
        var streamRetention = CiFailureRetention.CreateUnbounded();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(log));
        var streamParser = await CiLogStreamParser.ScanAsync(
            stream, streamRetention, TestContext.Current.CancellationToken);
        Assert.Equal(1, streamParser.ParsedFailureCount);
        var streamFailure = Assert.Single(streamRetention.Failures);
        Assert.Equal("Tests.MarkerShaped.FirstBody", streamFailure.TestName);
        Assert.Equal(markerShapedAssertion, NormalizeSeparators(streamFailure.Error));
        Assert.Empty(streamFailure.StackTrace);
    }

    /// <summary>
    /// Assertion PROSE that merely starts with <c>at </c> ("at least two values were expected") is not
    /// a stack frame, so a marker-shaped line after it is NOT a stack terminator. Loose
    /// <c>at {non-space}</c> matching (the shape the trailing frame scan uses) accepted that prose as
    /// the frame the marker "closes", which truncated <c>Error</c> at the prose line, turned the prose
    /// into the stack trace and discarded the assertion text after it.
    /// </summary>
    /// <remarks>
    /// The block is: assertion text, an <c>at …</c> prose line, the literal marker text, trailing
    /// assertion prose, then the REAL runner frames. Everything up to the trailing prose is error
    /// text (the marker line included, once), and only the genuine frames after it are the stack
    /// trace. Driven through the whole-log entry point and the production streaming path, in LF and
    /// CRLF framing; the discriminating assertions are the exact <c>Error</c> and <c>StackTrace</c>.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MarkerAfterAtProseAssertionLine_KeepsAssertionTextAndRealFramesAfterwards(bool crlf, bool streaming)
    {
        // Captured a scratch [Fact] calling Assert.Fail with this multiline message under
        // xUnit v3 4.0.1, using `dotnet test` in MTP mode on .NET 10. Only the failure block
        // is replayed here (not the run summary); CRLF is generated from the LF capture.
        const string capturedBlock =
            "failed MtpScratch.ProseCapture.FailsWithMarkerTextInAssertion (7ms)\n"
            + "  from /tmp/copilothive-qa-r3/replay/bin/Debug/net10.0/ProseCapture.dll (net10.0|x64)\n"
            + "  Expected a larger result\n"
            + "  at least two values were expected\n"
            + "  --- End of stack trace from previous location ---\n"
            + "  but only one was found\n"
            + "    at MtpScratch.ProseCapture.FailsWithMarkerTextInAssertion() in /tmp/copilothive-qa-r3/replay/ProseCapture.cs:8\n"
            + "    at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)\n"
            + "    at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)\n";
        var log = crlf ? capturedBlock.Replace("\n", "\r\n", StringComparison.Ordinal) : capturedBlock;

        var failures = streaming
            ? await ParseStreamingAsync(log)
            : CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal("MtpScratch.ProseCapture.FailsWithMarkerTextInAssertion", failure.TestName);
        Assert.Equal(
            "Expected a larger result\n"
            + "  at least two values were expected\n"
            + "  " + EndMarker + "\n"
            + "  but only one was found",
            NormalizeSeparators(failure.Error));
        Assert.Equal(1, CountOccurrences(failure.Error, EndMarker));
        Assert.Equal(
            "at MtpScratch.ProseCapture.FailsWithMarkerTextInAssertion() in /tmp/copilothive-qa-r3/replay/ProseCapture.cs:8\n"
            + "    at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)\n"
            + "    at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)",
            NormalizeSeparators(failure.StackTrace));
    }

    /// <summary>
    /// Assertion prose that starts with <c>at </c> but is not a frame cannot make the following
    /// literal marker terminate a stack. The entire diagnostic stays in <c>Error</c>, and the
    /// later genuine frame becomes <c>StackTrace</c>. The fixtures include unqualified prose calls
    /// with parentheses as well as strings that lack a parameter list or have extra trailing text.
    /// </summary>
    [Theory]
    [InlineData("at least two values were expected", false)]
    [InlineData("at least two values were expected", true)]
    [InlineData("at Foo.Bar was never called", false)]
    [InlineData("at Foo.Bar(x) was never called", true)]
    [InlineData("at offset(0)", false)] // Has parentheses, but no dot-qualified method token.
    [InlineData("at offset(0)", true)]
    [InlineData("at lambda_method42 was never invoked", false)] // lambda_method lookalike, no parameter list.
    [InlineData("at lambda_methods(0)", true)] // Unqualified call that is NOT the runtime-generated name.
    [InlineData("at report.json", true)] // Has a dotted token, but no parameter list.
    [InlineData("at Foo.Bar(x) in that assertion", false)] // The "in" clause is prose, not a path.
    [InlineData("at C:\\temp\\a.txt (exists but empty)", false)]
    [InlineData("at e.g. the first call (expected)", true)]
    public async Task MarkerAfterAtProse_KeepsAssertionTextAndGenuineLaterFrames(string proseLine, bool crlf)
    {
        const string laterFrame = "at Tests.Prose.Only.Later() in /src/Later.cs:line 3";
        var separator = crlf ? "\r\n" : "\n";
        var log = string.Join(separator,
            "failed Tests.Prose.Only (3ms)",
            "  from /repo/bin/Release/net10.0/Tests.dll (net10.0|x64)",
            "  Assert.True() Failure",
            "  " + proseLine,
            "  " + EndMarker,
            "  " + laterFrame) + separator;

        foreach (var failures in new[]
                 {
                     CiMonitorService.ParseTestFailuresFromLogs(log),
                     await ParseStreamingAsync(log),
                 })
        {
            var failure = Assert.Single(failures);

            // The prose line and the quoted marker are assertion text and stay intact in Error.
            Assert.Equal(
                "Assert.True() Failure\n  " + proseLine + "\n  " + EndMarker,
                NormalizeSeparators(failure.Error));

            // The genuine frame AFTER the marker is the stack trace (trailing scan, unchanged).
            Assert.Equal(laterFrame, NormalizeSeparators(failure.StackTrace));
        }
    }

    /// <summary>
    /// REAL .NET 10 / xUnit v3 4.0.1 MTP output for a <c>DynamicMethod</c> exception rethrown through
    /// <c>ExceptionDispatchInfo</c>: the runtime-generated <c>at lambda_method42(String)</c> frame has
    /// no namespace and no file, and sits directly before the end marker, followed by real
    /// continuation frames. A dot-only frame guard rejected that frame, so the marker was not
    /// recognised: the pre-marker frames and the marker were reported as <c>Error</c> and the
    /// continuation became <c>StackTrace</c>.
    /// </summary>
    /// <remarks>
    /// In the scratch capture, the emitted DynamicMethod IL calls the thrower and captures the
    /// exception in its own catch block. The test rethrows it via the returned ExceptionDispatchInfo.
    /// The four streaming rows are separately identifiable: LF/plain, LF/synthetic-timestamp,
    /// CRLF/plain, CRLF/synthetic-timestamp. The synthetic timestamp prefix is a generated fixture,
    /// not evidence of what a raw Actions log carries. A stamp consumes the indentation after it, so
    /// the timestamped rows expect unindented fields; plain rows keep interior indentation.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScanAsync_CapturedDynamicMethodBlock_SeparatesFramesAtMarker(bool crlf, bool syntheticTimestamp)
    {
        var separator = crlf ? "\r\n" : "\n";
        var stamp = syntheticTimestamp ? SyntheticStamp : string.Empty;
        string[] capturedLines =
        [
            "failed MtpScratch.DynamicMethodTests.RethrownDynamicMethodFailure (7ms)",
            "  from /tmp/copilothive-qa-r5/capture/bin/Debug/net10.0/DynamicReplay.dll (net10.0|x64)",
            "  Xunit.MicrosoftTestingPlatform.XunitException: System.InvalidOperationException : dynamic boom",
            "    at MtpScratch.Holder.Thrower() in /tmp/copilothive-qa-r5/capture/Capture.cs:12",
            "    at lambda_method42(String)",
            "    --- End of stack trace from previous location ---",
            "    at MtpScratch.DynamicMethodTests.RethrownDynamicMethodFailure() in /tmp/copilothive-qa-r5/capture/Capture.cs:32",
            "    at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)",
            "    at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)",
        ];
        var log = string.Join(separator, capturedLines.Select(line => stamp + line)) + separator;

        var failures = await ParseStreamingAsync(log);

        var failure = Assert.Single(failures);
        Assert.Equal("MtpScratch.DynamicMethodTests.RethrownDynamicMethodFailure", failure.TestName);
        Assert.Equal(
            "Xunit.MicrosoftTestingPlatform.XunitException: System.InvalidOperationException : dynamic boom",
            NormalizeSeparators(failure.Error));

        // ONLY the genuine pre-marker frames, in order: the file-less runtime-generated frame
        // included. A stamp consumed each line's indentation; the plain rows keep the interior one.
        var interiorIndent = syntheticTimestamp ? string.Empty : "    ";
        Assert.Equal(
            "at MtpScratch.Holder.Thrower() in /tmp/copilothive-qa-r5/capture/Capture.cs:12\n"
            + interiorIndent + "at lambda_method42(String)",
            NormalizeSeparators(failure.StackTrace));

        // Neither the marker nor any continuation frame is in either field.
        foreach (var field in new[] { failure.Error, failure.StackTrace })
        {
            Assert.DoesNotContain(EndMarker, field, StringComparison.Ordinal);
            Assert.DoesNotContain("RethrownDynamicMethodFailure() in", field, StringComparison.Ordinal);
            Assert.DoesNotContain("InterpretedInvoke_Method", field, StringComparison.Ordinal);
            Assert.DoesNotContain("InvokeWithNoArgs", field, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("lambda_method42", failure.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Assertion text that CALLS a method named like the runtime-generated frame, then quotes the
    /// literal end marker, then states a trailing assertion, followed by the genuine runner frames.
    /// The call line is prose, not a frame: <c>at lambda_method(0)</c> has an EMPTY suffix and
    /// <c>at lambda_method٤٢(0)</c> has a non-ASCII (Arabic-Indic) decimal suffix, and the genuine
    /// <c>lambda_method{N}</c> frame has a NONEMPTY ASCII one. Treating either call as a frame made the
    /// marker a terminator: Error was cut to its opening line, the call became StackTrace, and the
    /// quoted marker, trailing assertion and genuine runner frames were discarded.
    /// </summary>
    /// <remarks>
    /// The two failure blocks come from the review's <c>bare.log</c> and <c>unicode.log</c>, captured
    /// from xUnit v3 4.0.1 MTP on .NET 10. The fixture retains both original test names, paths, and
    /// source lines. Each block has four <c>ScanAsync</c> rows (LF/CRLF × plain/synthetic timestamp).
    /// A synthetic timestamp is fixture framing, not evidence of raw Actions prefixes; stripping
    /// it also removes the indentation, so stamped rows expect unindented fields.
    /// </remarks>
    [Theory]
    [InlineData("lambda_method", false, false)] // empty suffix: LF/plain
    [InlineData("lambda_method", false, true)] // empty suffix: LF/synthetic timestamp
    [InlineData("lambda_method", true, false)] // empty suffix: CRLF/plain
    [InlineData("lambda_method", true, true)] // empty suffix: CRLF/synthetic timestamp
    [InlineData("lambda_method\u0664\u0662", false, false)] // Arabic-Indic suffix: LF/plain
    [InlineData("lambda_method\u0664\u0662", false, true)] // Arabic-Indic suffix: LF/synthetic timestamp
    [InlineData("lambda_method\u0664\u0662", true, false)] // Arabic-Indic suffix: CRLF/plain
    [InlineData("lambda_method\u0664\u0662", true, true)] // Arabic-Indic suffix: CRLF/synthetic timestamp
    public async Task ScanAsync_LambdaMethodLookalikeCallInAssertion_KeepsErrorAndGenuineFrames(
        string methodName, bool crlf, bool syntheticTimestamp)
    {
        var separator = crlf ? "\r\n" : "\n";
        var stamp = syntheticTimestamp ? SyntheticStamp : string.Empty;
        var callLine = "at " + methodName + "(0)";
        var (testName, sourceLine) = methodName switch
        {
            "lambda_method" => ("EmitProse.BareSuffixInAssertion", "14"),
            "lambda_method\u0664\u0662" => ("EmitProse.UnicodeSuffixInAssertion", "16"),
            _ => throw new ArgumentOutOfRangeException(nameof(methodName)),
        };
        const string reviewDirectory = "/tmp/copilothive-review-mtp-r5-proof.OaBT72";
        var testFrame = $"at {testName}() in {reviewDirectory}/ReviewProof.cs:{sourceLine}";
        string[] capturedLines =
        [
            $"failed {testName} (6ms)",
            $"  from {reviewDirectory}/bin/Debug/net10.0/Proof.dll (net10.0|x64)",
            "  Expected and actual call listings differ",
            "  " + callLine,
            "  " + EndMarker,
            "  Expected at least one record after the quoted marker",
            "    " + testFrame,
            "    at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)",
            "    at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)",
        ];
        var log = string.Join(separator, capturedLines.Select(line => stamp + line)) + separator;

        var failures = await ParseStreamingAsync(log);

        var failure = Assert.Single(failures);
        Assert.Equal(testName, failure.TestName);

        // The first line of each field loses its indentation to the final Trim(); interior lines keep
        // theirs unless a stamp consumed it.
        var errorIndent = syntheticTimestamp ? string.Empty : "  ";
        var frameIndent = syntheticTimestamp ? string.Empty : "    ";
        Assert.Equal(
            "Expected and actual call listings differ\n"
            + errorIndent + callLine + "\n"
            + errorIndent + EndMarker + "\n"
            + errorIndent + "Expected at least one record after the quoted marker",
            NormalizeSeparators(failure.Error));
        Assert.Equal(
            testFrame + "\n"
            + frameIndent + "at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)\n"
            + frameIndent + "at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)",
            NormalizeSeparators(failure.StackTrace));
        Assert.DoesNotContain(callLine, failure.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>
    /// POSITIVE CONTROLS: a genuine marker after a real .NET stack frame still terminates the stack,
    /// for the frame shapes xUnit/MTP actually print — with and without file/line information, with
    /// generic, compiler-generated (lambda, async state machine, <c>InvokeStub_</c>) and constructor
    /// methods, parameter lists with arrays and generic arguments, and a path containing a space. The
    /// frame stays in <c>StackTrace</c>, the marker and the continuation frame after it are in neither
    /// field, and the error text is unchanged. Whole-log and streaming, LF and CRLF.
    /// </summary>
    [Theory]
    [InlineData("at Ns.Type.Method(String a, Int32 b) in /src/F.cs:line 12", false, false)]
    [InlineData("at Ns.Type.Method(String a, Int32 b) in /src/F.cs:line 12", true, true)]
    [InlineData("at Ns.Type.Method(String a) in .../F.cs:113", true, false)]
    [InlineData("at Ns.Type.Method(String a) in .../F.cs:113", false, true)]
    [InlineData("at Ns.Type.Method()", false, false)]
    [InlineData("at Ns.Type.Method()", true, true)]
    [InlineData("at Ns.Type`1.Method[T](T value)", false, true)]
    [InlineData("at Ns.Type.<>c.<Run>b__0_0()", true, false)]
    [InlineData("at Ns.Type.<Run>d__3.MoveNext()", false, false)]
    [InlineData("at InvokeStub_Ns.Type.Method(Object, Span`1)", true, true)]
    [InlineData("at Ns.Type..ctor(String a)", false, true)]
    [InlineData("at Ns.Type.Method(Int32[] a, List`1 b)", true, false)]
    [InlineData("at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)", false, false)]
    [InlineData("at Ns.Type.Method(String a) in /src/My Tests/F.cs:line 12", true, true)]
    [InlineData("at Ns.Type.Method(String a) in C:\\src\\F.cs:line 12", false, true)]
    [InlineData("at lambda_method42(String)", false, true)]
    [InlineData("at lambda_method42(String)", true, false)]
    public async Task GenuineMarkerAfterRealFrame_StillTerminatesStack(string frame, bool crlf, bool streaming)
    {
        var separator = crlf ? "\r\n" : "\n";
        var log = string.Join(separator,
            "failed Tests.Genuine.Frame (3ms)",
            "  from /repo/bin/Release/net10.0/Tests.dll (net10.0|x64)",
            "  boom",
            "  " + frame,
            "  " + EndMarker,
            "  at Xunit.Sdk.TestInvoker`1.Continuation()") + separator;

        var failures = streaming
            ? await ParseStreamingAsync(log)
            : CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal("boom", failure.Error);
        Assert.Equal(frame, NormalizeSeparators(failure.StackTrace));
        Assert.DoesNotContain(EndMarker, failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(EndMarker, failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("TestInvoker", failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("TestInvoker", failure.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>
    /// CONTROL for the shared marker regex: a VSTest block and a marked MTP block in the SAME log
    /// each keep their existing field separation. The VSTest side guards against the MTP fix
    /// accidentally changing the extractor that already honoured the marker, and the MTP side
    /// proves the marker does not terminate the whole log or the surrounding block state.
    /// </summary>
    [Fact]
    public void ParseTestFailuresFromLogs_MarkedMtpBlockAndMarkedVstestBlock_KeepBothFormatsFieldsClean()
    {
        var log = SuppliedExcerpt
            + "Failed Tests.Vstest.Marked [7 ms]\n"
            + "  Error Message:\n"
            + "   vstest marked boom\n"
            + "  Stack Trace:\n"
            + "   at Tests.Vstest.Marked() in .../VstestMarked.cs:4\n"
            + "   --- End of stack trace from previous location ---\n"
            + "   at Xunit.Sdk.TestInvoker.Invoke()\n";

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        Assert.Equal(2, failures.Count);

        Assert.Equal(FullTheoryName, failures[0].TestName);
        Assert.Equal(SuppliedError, failures[0].Error);
        Assert.Equal(SuppliedStackTrace, failures[0].StackTrace);

        Assert.Equal("Tests.Vstest.Marked", failures[1].TestName);
        Assert.Equal("vstest marked boom", failures[1].Error);
        Assert.Equal("at Tests.Vstest.Marked() in .../VstestMarked.cs:4", failures[1].StackTrace);

        foreach (var failure in failures)
        {
            Assert.DoesNotContain(EndMarker, failure.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(EndMarker, failure.StackTrace, StringComparison.Ordinal);
            Assert.DoesNotContain("TestInvoker", failure.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("TestInvoker", failure.StackTrace, StringComparison.Ordinal);
        }

        // No cross-format contamination: the VSTest section labels are not error text of the MTP
        // failure, and the MTP assertion text does not reach the VSTest failure.
        Assert.DoesNotContain("Error Message:", failures[0].Error, StringComparison.Ordinal);
        Assert.DoesNotContain("unchang", failures[1].Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two marked MTP blocks through the PRODUCTION streaming path in both LF and CRLF framing,
    /// terminated by MTP's per-assembly footer, with a 16 KiB chunk boundary placed inside the
    /// SECOND block's marker line. Streaming must split the marker across chunks without losing
    /// either block's field separation.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsync_TwoMarkedMtpBlocksWithSecondMarkerSplitAtChunkBoundary_KeepsBothFieldsClean(bool crlf)
    {
        var separator = crlf ? "\r\n" : "\n";
        const string secondMarker = "  --- End of stack trace from previous location ---";
        const string secondFrame =
            "  at CopilotHive.Tests.Other.Streamed() in .../StreamedTests.cs:31";
        var firstBlock = SuppliedExcerpt.Replace("\n", separator, StringComparison.Ordinal);
        var secondBlock =
            "failed CopilotHive.Tests.Other.Streamed (2ms)" + separator
            + "  from .../CopilotHive.Tests.dll (net10.0|x64)" + separator
            + "  streamed error" + separator
            + secondFrame + separator
            + secondMarker + separator;
        var block = firstBlock + secondBlock;

        // Search only from the START OF THE SECOND BLOCK: the first block carries the identical
        // marker text, so a search from index 0 would locate the FIRST marker and the geometry
        // assertion below would be about that one instead of the second. The marker inside the
        // second block is the one this row is about.
        var secondBlockStart = firstBlock.Length;
        var secondMarkerIndex = block.IndexOf(secondMarker, secondBlockStart, StringComparison.Ordinal);
        Assert.True(secondMarkerIndex >= secondBlockStart,
            "The second block's own marker must exist and lie after the first block.");

        // Prove the located occurrence really is inside the second block: the first block's marker
        // text also exists, and it is strictly EARLIER than the located index.
        var firstMarkerIndex = block.IndexOf(secondMarker, StringComparison.Ordinal);
        Assert.True(firstMarkerIndex < secondMarkerIndex,
            "The first block's marker must precede the located second-block marker.");

        // Place the second chunk boundary halfway through the SECOND marker line, in BYTES.
        var boundary = CiLogStreamParser.StreamChunkBytes * 2;
        var markerByteOffset = Encoding.UTF8.GetByteCount(block[..secondMarkerIndex]);
        var halfMarkerBytes = Encoding.UTF8.GetByteCount(secondMarker[..(secondMarker.Length / 2)]);
        var fillerBytes = boundary - markerByteOffset - halfMarkerBytes;
        Assert.True(fillerBytes > 0, "The framed block must start before the second chunk boundary.");

        var filler = Filler(fillerBytes);
        var log = filler + block
            + "/copilot-home/CopilotHive/tests/CopilotHive.Tests/bin/Release/net10.0/CopilotHive.Tests.dll (net10.0|x64) failed with 2 error(s) (1s 2ms)" + separator;

        var markerStartByte = Encoding.UTF8.GetByteCount(log[..(filler.Length + secondMarkerIndex)]);
        var markerEndByte = markerStartByte + Encoding.UTF8.GetByteCount(secondMarker);
        Assert.True(markerStartByte < boundary && boundary < markerEndByte,
            $"The chunk boundary must fall inside the second marker ({markerStartByte} < {boundary} < {markerEndByte}).");

        // The FIRST block's marker must be wholly on the near side of the boundary. Measure it in
        // LOG-relative bytes (filler included), exactly like markerStartByte above: comparing a
        // block-relative offset to a log-relative boundary would be trivially true and could not
        // discriminate the two occurrences. The two marker byte ranges must be disjoint, with the
        // boundary inside the second range and strictly AFTER the first range's end.
        var firstMarkerStartByte = Encoding.UTF8.GetByteCount(log[..(filler.Length + firstMarkerIndex)]);
        var firstMarkerEndByte = firstMarkerStartByte + Encoding.UTF8.GetByteCount(secondMarker);
        Assert.True(firstMarkerEndByte < markerStartByte,
            $"The two marker byte ranges must be disjoint ({firstMarkerEndByte} < {markerStartByte}).");
        Assert.True(firstMarkerEndByte < boundary,
            $"The first block's marker must end before the chunk boundary ({firstMarkerEndByte} < {boundary}).");

        var failures = await ParseStreamingAsync(log);

        Assert.Equal(2, failures.Count);

        Assert.Equal(FullTheoryName, failures[0].TestName);
        Assert.Equal(SuppliedError, NormalizeSeparators(failures[0].Error));
        Assert.Equal(SuppliedStackTrace, NormalizeSeparators(failures[0].StackTrace));

        Assert.Equal("CopilotHive.Tests.Other.Streamed", failures[1].TestName);
        Assert.Equal("streamed error", NormalizeSeparators(failures[1].Error));
        Assert.Equal("at CopilotHive.Tests.Other.Streamed() in .../StreamedTests.cs:31", NormalizeSeparators(failures[1].StackTrace));

        foreach (var failure in failures)
        {
            Assert.DoesNotContain(EndMarker, failure.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(EndMarker, failure.StackTrace, StringComparison.Ordinal);
            Assert.DoesNotContain("failed with 2 error(s)", failure.Error, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// CONTROL: an ordinary MTP block with NO end marker keeps its current behavior — the trailing
    /// at-frames are the stack trace and everything above them is the error text.
    /// </summary>
    [Fact]
    public void ParseTestFailuresFromLogs_MtpBlockWithoutEndMarker_KeepsTrailingFramesInStackTrace()
    {
        const string log =
            "failed Tests.Alpha.First (12ms)\n"
            + "  from /repo/bin/Release/net10.0/Tests.dll (net10.0|x64)\n"
            + "  boom\n"
            + "    at Alpha.First() in /src/Alpha.cs:line 7\n"
            + "    at Alpha.Frame2() in /src/Alpha.cs:line 9\n";

        var failures = CiMonitorService.ParseTestFailuresFromLogs(log);

        var failure = Assert.Single(failures);
        Assert.Equal("Tests.Alpha.First", failure.TestName);
        Assert.Equal("boom", failure.Error);
        Assert.Equal("at Alpha.First() in /src/Alpha.cs:line 7\n    at Alpha.Frame2() in /src/Alpha.cs:line 9", failure.StackTrace);
    }

    /// <summary>
    /// Secret redaction is preserved inside a marked block: the error text is redacted line by line
    /// before the parsed fields are sliced, the raw secret reaches neither field, and the stack
    /// separation is unaffected.
    /// </summary>
    /// <remarks>
    /// The token is a synthetic fixture, never a real credential, and the assertions only ever
    /// compare against it — the test never logs the raw value.
    /// </remarks>
    [Fact]
    public async Task ScanAsync_MarkedMtpBlockWithSecretInError_RedactsSecretAndKeepsStack()
    {
        const string secret = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";
        var log = SuppliedExcerpt.Replace(
            "  Not found: \"fa1efcbb\"\n",
            "  Not found: \"fa1efcbb\"\n  Authorization: Bearer " + secret + "\n",
            StringComparison.Ordinal);

        var failures = await ParseStreamingAsync(log);

        var failure = Assert.Single(failures);
        Assert.Equal(FullTheoryName, failure.TestName);
        Assert.Equal(
            SuppliedError + "\n  Authorization: Bearer " + CiLogRedactor.RedactionMarker,
            NormalizeSeparators(failure.Error));
        Assert.Equal(SuppliedStackTrace, NormalizeSeparators(failure.StackTrace));

        Assert.DoesNotContain(secret, failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, failure.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, failure.TestName, StringComparison.Ordinal);
        Assert.DoesNotContain(EndMarker, failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(EndMarker, failure.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>Frames the supplied excerpt as a raw Actions log line-by-line, with a synthetic stamp.</summary>
    private static string FramedExcerpt(bool crlf)
    {
        var separator = crlf ? "\r\n" : "\n";
        var lines = SuppliedExcerpt.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(separator, lines.Select(line => SyntheticStamp + line)) + separator;
    }

    /// <summary>
    /// Runs the excerpt through the PRODUCTION streaming entry point over a memory stream: the same
    /// redacting parser <c>FetchJobLogsAsync</c> uses, including its chunked reads.
    /// </summary>
    private static async Task<List<(string TestName, string Error, string StackTrace)>> ParseStreamingAsync(string log)
    {
        var retention = CiFailureRetention.CreateUnbounded();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(log));
        await CiLogStreamParser.ScanAsync(stream, retention, TestContext.Current.CancellationToken);
        return retention.Failures;
    }

    /// <summary>
    /// Builds exactly <paramref name="length"/> ASCII characters of whole log lines, so the caller's
    /// stream geometry is exact.
    /// </summary>
    private static string Filler(int length)
    {
        var sb = new StringBuilder();
        while (sb.Length < length)
        {
            var lineLength = Math.Min(99, length - sb.Length - 1);
            sb.Append(new string('f', Math.Max(0, lineLength))).Append('\n');
        }

        Assert.Equal(length, sb.Length);
        return sb.ToString();
    }

    private static string NormalizeSeparators(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
