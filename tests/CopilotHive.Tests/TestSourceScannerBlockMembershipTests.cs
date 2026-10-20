namespace CopilotHive.Tests;

/// <summary>
/// Isolating regression tests for the BLOCK-MEMBERSHIP predicates of
/// <see cref="TestSourceScanner"/> (<see cref="TestSourceScanner.IsInsideTryBlock"/> and
/// <see cref="TestSourceScanner.IsInsideCatchBlock"/>), which the structural cleanup assertions
/// rely on to prove that a fixture's cleanup call is CAUGHT and its failure REPORTED.
/// <para>
/// Each case is a small synthetic method body run through the real predicates, so the predicate's
/// own soundness is proven independently of any production fixture. The false-positive vectors
/// are the ones an adversarial review demonstrated against an earlier keyword-and-aggregate-depth
/// implementation: a sibling block between/after completed try/catch statements shares their depth
/// but is in NO try or catch block, and an early unprotected duplicate must not be hidden by a
/// later protected one.
/// </para>
/// </summary>
public sealed class TestSourceScannerBlockMembershipTests
{
    private const string Helper = "TestHelpers.ForceDeleteDirectory(";
    private const string Reporter = "ReportCleanupFailure(_tempDir, ex);";

    // ── Positive controls: the predicates still accept genuine membership ───

    /// <summary>
    /// A helper call inside the try block of a try/catch is accepted, and the reporter inside the
    /// catch handler is accepted. Non-vacuity control for every rejection below.
    /// </summary>
    [Fact]
    public void ProtectedCallAndCatchReporter_AreAccepted()
    {
        const string code = """
            {
                try
                {
                    TestHelpers.ForceDeleteDirectory(_tempDir);
                }
                catch (Exception ex)
                {
                    ReportCleanupFailure(_tempDir, ex);
                }
            }
            """;

        Assert.True(TestSourceScanner.IsInsideTryBlock(code, Helper));
        Assert.True(TestSourceScanner.IsInsideCatchBlock(code, Reporter));
    }

    /// <summary>
    /// Wrapping the protected call in an inner <c>if</c> and the reporter in a nested try inside
    /// the catch (the shape the Composer fixture uses) keeps membership: the call is still inside
    /// the try block's concrete brace range.
    /// </summary>
    [Fact]
    public void NestedBlocksInsideTheTryAndCatch_KeepMembership()
    {
        const string code = """
            {
                if (baseDir is not null)
                {
                    try
                    {
                        if (enabled)
                        {
                            TestHelpers.ForceDeleteDirectory(baseDir);
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            ReportCleanupFailure(_tempDir, ex);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            """;

        Assert.True(TestSourceScanner.IsInsideTryBlock(code, Helper));
        Assert.True(TestSourceScanner.IsInsideCatchBlock(code, Reporter));
    }

    /// <summary>
    /// A catch handler with an exception filter (<c>when (...)</c>) still yields its own brace
    /// range, so a reporter inside it is accepted.
    /// </summary>
    [Fact]
    public void CatchWithExceptionFilter_ReporterInsideIsAccepted()
    {
        const string code = """
            {
                try
                {
                    TestHelpers.ForceDeleteDirectory(_tempDir);
                }
                catch (IOException ex) when (ex.HResult != 0)
                {
                    ReportCleanupFailure(_tempDir, ex);
                }
            }
            """;

        Assert.True(TestSourceScanner.IsInsideTryBlock(code, Helper));
        Assert.True(TestSourceScanner.IsInsideCatchBlock(code, Reporter));
    }

    // ── Reviewer's demonstrated false positives (closed / sibling blocks) ───

    /// <summary>
    /// REVIEWER CASE 1: a helper call in a SIBLING <c>if</c> block between two completed, separate
    /// try/catch statements. The earlier (already closed) try supplied the try depth and the later
    /// unrelated catch supplied the catch depth, so the old implementation wrongly accepted it. The
    /// call is in NO try block, so a failure there ESCAPES.
    /// </summary>
    [Fact]
    public void HelperCallInASiblingIfBetweenTwoCompletedTryCatches_IsRejected()
    {
        const string code = """
            {
                try
                {
                    Setup();
                }
                catch (Exception)
                {
                }

                if (cleanupNeeded)
                {
                    TestHelpers.ForceDeleteDirectory(_tempDir);
                }

                try
                {
                    Other();
                }
                catch (Exception)
                {
                }
            }
            """;

        Assert.False(TestSourceScanner.IsInsideTryBlock(code, Helper));
    }

    /// <summary>
    /// REVIEWER CASE 2: a reporter call in a SIBLING <c>if</c> block AFTER the catch has already
    /// closed. It shares the catch's depth but is not inside the handler, so no failure reaches it.
    /// </summary>
    [Fact]
    public void ReporterInASiblingIfAfterTheCatchClosed_IsRejected()
    {
        const string code = """
            {
                try
                {
                    TestHelpers.ForceDeleteDirectory(_tempDir);
                }
                catch (Exception ex)
                {
                    Swallow();
                }

                if (reportAnyway)
                {
                    ReportCleanupFailure(_tempDir, ex);
                }
            }
            """;

        Assert.True(TestSourceScanner.IsInsideTryBlock(code, Helper));
        Assert.False(TestSourceScanner.IsInsideCatchBlock(code, Reporter));
    }

    /// <summary>
    /// A helper call in the try's own CATCH block, or in its FINALLY block, is not protected BY
    /// that try: an exception there escapes. Neither is inside the try block's brace range.
    /// </summary>
    [Fact]
    public void HelperCallInTheCatchOrFinallyBlock_IsNotInsideTheTryBlock()
    {
        const string inCatch = """
            {
                try
                {
                    Work();
                }
                catch (Exception)
                {
                    TestHelpers.ForceDeleteDirectory(_tempDir);
                }
            }
            """;

        const string inFinally = """
            {
                try
                {
                    Work();
                }
                catch (Exception)
                {
                }
                finally
                {
                    TestHelpers.ForceDeleteDirectory(_tempDir);
                }
            }
            """;

        Assert.False(TestSourceScanner.IsInsideTryBlock(inCatch, Helper));
        Assert.False(TestSourceScanner.IsInsideTryBlock(inFinally, Helper));
    }

    /// <summary>
    /// A try with NO catch (only a finally) does not CATCH anything, so a call inside it is not
    /// protected from escaping and the predicate rejects it.
    /// </summary>
    [Fact]
    public void TryWithoutACatch_DoesNotProtectTheCall()
    {
        const string code = """
            {
                try
                {
                    TestHelpers.ForceDeleteDirectory(_tempDir);
                }
                finally
                {
                    Release();
                }
            }
            """;

        Assert.False(TestSourceScanner.IsInsideTryBlock(code, Helper));
    }

    // ── Reviewer's demonstrated false positive: early unprotected duplicate ─

    /// <summary>
    /// REVIEWER CASE 3: an UNPROTECTED duplicate helper call BEFORE the protected one. It can throw
    /// before the protected cleanup is ever reached, so the method is unsafe — the old
    /// last-occurrence-only implementation returned true (and the old absolute nesting level of the
    /// FIRST occurrence was 0). Every occurrence must be protected.
    /// </summary>
    [Fact]
    public void EarlyUnprotectedDuplicateBeforeTheProtectedCall_IsRejected()
    {
        const string code = """
            {
                TestHelpers.ForceDeleteDirectory(_tempDir);
                try
                {
                    TestHelpers.ForceDeleteDirectory(_tempDir);
                }
                catch (Exception ex)
                {
                    ReportCleanupFailure(_tempDir, ex);
                }
            }
            """;

        Assert.False(TestSourceScanner.IsInsideTryBlock(code, Helper));

        // The prior, independent guard: the FIRST occurrence sits at nesting level 0 (not 1).
        Assert.Equal(0, TestSourceScanner.NestingLevel(code, Helper));
    }

    /// <summary>
    /// The mirror image: an unprotected duplicate AFTER the protected call is equally unsafe and is
    /// also rejected (every occurrence is examined, not only the first or the last).
    /// </summary>
    [Fact]
    public void LateUnprotectedDuplicateAfterTheProtectedCall_IsRejected()
    {
        const string code = """
            {
                try
                {
                    TestHelpers.ForceDeleteDirectory(_tempDir);
                }
                catch (Exception ex)
                {
                    ReportCleanupFailure(_tempDir, ex);
                }
                TestHelpers.ForceDeleteDirectory(_tempDir);
            }
            """;

        Assert.False(TestSourceScanner.IsInsideTryBlock(code, Helper));
    }

    /// <summary>
    /// Two PROTECTED occurrences (in separate try blocks) are both accepted, so the all-occurrences
    /// rule does not reject a legitimately repeated protected call.
    /// </summary>
    [Fact]
    public void TwoProtectedOccurrencesInSeparateTries_AreAccepted()
    {
        const string code = """
            {
                try
                {
                    TestHelpers.ForceDeleteDirectory(a);
                }
                catch (Exception ex)
                {
                    ReportCleanupFailure(_tempDir, ex);
                }

                try
                {
                    TestHelpers.ForceDeleteDirectory(b);
                }
                catch (Exception ex)
                {
                    ReportCleanupFailure(_tempDir, ex);
                }
            }
            """;

        Assert.True(TestSourceScanner.IsInsideTryBlock(code, Helper));
        Assert.True(TestSourceScanner.IsInsideCatchBlock(code, Reporter));
    }

    // ── Masking: literals and comments cannot delimit a block or hold a call ─

    /// <summary>
    /// A brace inside a STRING LITERAL or a comment cannot open or close a block, so the try block's
    /// range is still the real one and a call outside it is still rejected.
    /// </summary>
    [Fact]
    public void BracesInsideStringLiteralsAndComments_DoNotDelimitBlocks()
    {
        const string accepted = """
            {
                try
                {
                    var s = "} catch { ";
                    // } stray closing brace in a comment
                    TestHelpers.ForceDeleteDirectory(_tempDir);
                }
                catch (Exception ex)
                {
                    ReportCleanupFailure(_tempDir, ex);
                }
            }
            """;

        const string rejected = """
            {
                var s = "try { ";
                TestHelpers.ForceDeleteDirectory(_tempDir);
                try
                {
                    Work();
                }
                catch (Exception ex)
                {
                }
            }
            """;

        Assert.True(TestSourceScanner.IsInsideTryBlock(accepted, Helper));
        Assert.False(TestSourceScanner.IsInsideTryBlock(rejected, Helper));
    }

    /// <summary>
    /// An occurrence of the fragment inside a COMMENT or STRING is not code and is ignored: an
    /// unprotected mention in prose must not make a correctly protected method fail, and a method
    /// whose ONLY occurrence is in a comment has no code occurrence at all.
    /// </summary>
    [Fact]
    public void OccurrencesInCommentsAndStrings_AreNotCode()
    {
        const string protectedWithProse = """
            {
                // TestHelpers.ForceDeleteDirectory(_tempDir); is called below
                var note = "TestHelpers.ForceDeleteDirectory(x)";
                try
                {
                    TestHelpers.ForceDeleteDirectory(_tempDir);
                }
                catch (Exception ex)
                {
                    ReportCleanupFailure(_tempDir, ex);
                }
            }
            """;

        const string onlyInComment = """
            {
                // TestHelpers.ForceDeleteDirectory(_tempDir);
            }
            """;

        Assert.True(TestSourceScanner.IsInsideTryBlock(protectedWithProse, Helper));
        Assert.False(TestSourceScanner.IsInsideTryBlock(onlyInComment, Helper));
    }

    /// <summary>
    /// An identifier that merely CONTAINS <c>try</c> (<c>retry</c>, <c>entry</c>) is not a try
    /// statement, so a call inside <c>retry { }</c>-like blocks is not mistaken for protected.
    /// </summary>
    [Fact]
    public void IdentifiersContainingTry_AreNotTryStatements()
    {
        const string code = """
            {
                retry
                {
                    TestHelpers.ForceDeleteDirectory(_tempDir);
                }
                catch (Exception)
                {
                }
            }
            """;

        Assert.False(TestSourceScanner.IsInsideTryBlock(code, Helper));
    }

    /// <summary>
    /// A fragment that does not occur at all is rejected by both predicates (no vacuous truth).
    /// </summary>
    [Fact]
    public void AbsentFragment_IsRejectedByBothPredicates()
    {
        const string code = """
            {
                try
                {
                    Work();
                }
                catch (Exception)
                {
                }
            }
            """;

        Assert.False(TestSourceScanner.IsInsideTryBlock(code, Helper));
        Assert.False(TestSourceScanner.IsInsideCatchBlock(code, Reporter));
    }
}
