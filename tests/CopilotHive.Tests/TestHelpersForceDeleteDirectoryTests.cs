namespace CopilotHive.Tests;

/// <summary>
/// Contract of the shared test-cleanup helper <see cref="TestHelpers.ForceDeleteDirectory"/>:
/// a BOUNDED, generous retry schedule for a directory whose delete transiently fails (Windows
/// keeps a dying process's file handles for a short while), and a LOUD failure — path plus the
/// original cause — when the directory survives the whole budget. A silently swallowed residue
/// only moves the symptom to an unrelated later test.
/// </summary>
/// <remarks>
/// The retry schedule and the loud failure are driven through the helper's test seams
/// (<c>deleteOverride</c> / <c>waitOverride</c>) so the contract is proven DETERMINISTICALLY and
/// without depending on elapsed time: a directory that is genuinely locked cannot be produced on
/// every platform (on Linux an open handle does not block <c>rm -r</c>), and a real 3.6 s backoff
/// would make the test a timing test.
/// </remarks>
public sealed class TestHelpersForceDeleteDirectoryTests : IDisposable
{
    private readonly string _tempDir;

    public TestHelpersForceDeleteDirectoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-forcedelete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// The default budget is the documented nine attempts — the first one immediate, then eight
    /// further attempts separated by linear 100…800 ms waits (3.6 s of total wait). The budget is
    /// FIXED: a bounded retry schedule, never a polling loop that grows until the flaky condition
    /// happens to pass.
    /// </summary>
    [Fact]
    public void ForceDeleteDirectory_TransientFailures_RetriesOnTheDocumentedBoundedSchedule()
    {
        var attempts = 0;
        var waits = new List<int>();

        // Every attempt "fails" transiently; the directory itself is never really removed, so the
        // helper must exhaust exactly its budget and then fail loudly.
        Assert.Throws<InvalidOperationException>(() =>
            TestHelpers.ForceDeleteDirectory(
                _tempDir,
                deleteOverride: _ =>
                {
                    attempts++;
                    throw new IOException("simulated transient lock");
                },
                waitOverride: waits.Add));

        // Nine delete attempts in total (the documented default budget), and one wait less than
        // the number of attempts because the first attempt is immediate.
        Assert.Equal(9, attempts);
        Assert.Equal(8, waits.Count);
    }

    /// <summary>
    /// The backoff waits are the documented linear sequence 100, 200, … 800 ms — eight waits for
    /// nine attempts, because the FIRST attempt is immediate and the FINAL attempt's failure is
    /// reported rather than slept on.
    /// </summary>
    [Fact]
    public void ForceDeleteDirectory_TransientFailures_WaitsWithTheDocumentedLinearBackoff()
    {
        var waits = new List<int>();

        Assert.Throws<InvalidOperationException>(() =>
            TestHelpers.ForceDeleteDirectory(
                _tempDir,
                deleteOverride: _ => throw new UnauthorizedAccessException("simulated transient lock"),
                waitOverride: waits.Add));

        Assert.Equal(new[] { 100, 200, 300, 400, 500, 600, 700, 800 }, waits);
        Assert.Equal(3600, waits.Sum());
    }

    /// <summary>
    /// The loud failure names the FULL directory path and carries the ORIGINAL delete exception as
    /// its inner exception — a silent swallow would leave the residue behind and hide the cause.
    /// </summary>
    [Fact]
    public void ForceDeleteDirectory_DirectorySurvives_ThrowsLoudlyWithPathAndOriginalCause()
    {
        var original = new IOException("simulated original cause");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestHelpers.ForceDeleteDirectory(
                _tempDir,
                deleteOverride: _ => throw original,
                waitOverride: _ => { }));

        Assert.Contains(Path.GetFullPath(_tempDir), ex.Message, StringComparison.Ordinal);
        Assert.Same(original, ex.InnerException);
        Assert.True(Directory.Exists(_tempDir), "the surviving directory must not be silently discarded");
    }

    /// <summary>
    /// A delete that succeeds on the first attempt performs NO wait at all and deletes the tree.
    /// </summary>
    [Fact]
    public void ForceDeleteDirectory_DeleteSucceeds_DeletesWithoutWaiting()
    {
        var waited = false;
        var deleted = false;

        TestHelpers.ForceDeleteDirectory(
            _tempDir,
            deleteOverride: path =>
            {
                Directory.Delete(path, recursive: true);
                deleted = true;
            },
            waitOverride: _ => waited = true);

        Assert.True(deleted);
        Assert.False(waited);
        Assert.False(Directory.Exists(_tempDir));
    }

    /// <summary>
    /// A delete that fails transiently and then succeeds is retried and reported as a SUCCESS —
    /// the helper must not throw once the directory is finally gone.
    /// </summary>
    [Fact]
    public void ForceDeleteDirectory_FailsThenSucceeds_RetriesAndReportsSuccess()
    {
        var attempts = 0;
        var waits = new List<int>();

        TestHelpers.ForceDeleteDirectory(
            _tempDir,
            deleteOverride: path =>
            {
                attempts++;
                if (attempts < 3)
                    throw new IOException("simulated transient lock");

                Directory.Delete(path, recursive: true);
            },
            waitOverride: waits.Add);

        Assert.Equal(3, attempts);
        Assert.Equal(new[] { 100, 200 }, waits);
        Assert.False(Directory.Exists(_tempDir));
    }

    /// <summary>
    /// A path that does not exist is a no-op: the helper returns immediately without attempting a
    /// delete and without throwing.
    /// </summary>
    [Fact]
    public void ForceDeleteDirectory_PathDoesNotExist_ReturnsWithoutDeleting()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist");
        var attempts = 0;

        TestHelpers.ForceDeleteDirectory(
            missing,
            deleteOverride: _ => attempts++,
            waitOverride: _ => Assert.Fail("a missing directory must not be waited on"));

        Assert.Equal(0, attempts);
    }

    /// <summary>
    /// The helper CLEARS read-only attributes before deleting, so a checkout with read-only git
    /// objects (the Windows git default for pack files) can be removed. The clearing is observable
    /// through the file's attributes after the helper ran over a tree containing a read-only file.
    /// </summary>
    [Fact]
    public void ForceDeleteDirectory_ReadOnlyFile_ClearsAttributesBeforeDeleting()
    {
        var readOnlyFile = Path.Combine(_tempDir, "pack-file");
        File.WriteAllText(readOnlyFile, "git object");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
        Assert.Equal(FileAttributes.ReadOnly, File.GetAttributes(readOnlyFile));

        // The delete seam observes the tree AFTER the read-only clearing, so the clearing is
        // asserted from the production source, not from a copy of it.
        FileAttributes? observed = null;
        TestHelpers.ForceDeleteDirectory(
            _tempDir,
            deleteOverride: path =>
            {
                observed = File.GetAttributes(readOnlyFile);
                Directory.Delete(path, recursive: true);
            },
            waitOverride: _ => { });

        Assert.Equal(FileAttributes.Normal, observed);
    }

    /// <summary>
    /// The PRODUCTION path — no test seam at all — really deletes the tree, including a read-only
    /// file inside it. This is the path every fixture uses, so it is proven here rather than
    /// assumed: a helper that only ever exercised its own seam would never delete anything.
    /// </summary>
    [Fact]
    public void ForceDeleteDirectory_ProductionPath_DeletesTheTreeIncludingReadOnlyFiles()
    {
        var nested = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(nested);
        var readOnlyFile = Path.Combine(nested, "pack-file");
        File.WriteAllText(readOnlyFile, "git object");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);

        TestHelpers.ForceDeleteDirectory(_tempDir);

        Assert.False(Directory.Exists(_tempDir));
    }
}
