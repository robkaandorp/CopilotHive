using CopilotHive.Tests.Orchestration;
using CopilotHive.Tests.Services;

namespace CopilotHive.Tests;

/// <summary>
/// BEHAVIOURAL contract of the best-effort cleanup paths: when the shared hardened helper
/// (<see cref="TestHelpers.ForceDeleteDirectory"/>) FAILS loudly, the fixture that owns the
/// cleanup must (a) NOT fail the test — a disposable's exception would replace the test's own
/// primary assertion failure — and (b) REPORT the failure DIAGNOSTICALLY, preserving BOTH the
/// directory path and the exception. A silent swallow would move the symptom to an unrelated
/// later test and hide the cause.
/// <para>
/// The loud failure is produced through the deterministic
/// <see cref="TestCleanupSeam"/> seam: a real locked directory cannot be produced on every
/// platform (on Linux an open handle does not block <c>rm -r</c>), and waiting out the helper's
/// real 3.6 s backoff would make the test a timing test. The seam's delete override throws the
/// very exception the helper's production path would surface (an
/// <see cref="System.IO.IOException"/>, which the helper retries and finally reports).
/// </para>
/// </summary>
public sealed class CleanupDiagnosticPreservationTests
{
    /// <summary>
    /// The exception the seam raises: the helper only retries <see cref="System.IO.IOException"/>
    /// and <see cref="System.UnauthorizedAccessException"/>, so an IOException exercises the
    /// helper's real retry-then-fail path rather than an immediate, unretryable error.
    /// </summary>
    private static IOException SimulatedCleanupFailure() =>
        new("simulated transient lock holding the temp directory");

    /// <summary>
    /// The dispatcher-maintenance SYNC fixture's Dispose: the shared helper's loud failure is
    /// caught, and the emitted diagnostic preserves the DIRECTORY PATH and the EXCEPTION.
    /// </summary>
    [Fact]
    public void DispatcherMaintenanceSyncTests_Dispose_ReportsTheCleanupFailureWithPathAndException()
    {
        using var capture = new DiagnosticMessageCapture();
        var fixture = new DispatcherMaintenanceSyncTests();
        var tempDir = GetTempDir(fixture);

        // Force the shared helper's loud failure: every delete attempt fails, so the helper
        // exhausts its bounded budget and throws with the path and the last error.
        fixture.CleanupSeam = new TestCleanupSeam
        {
            Delete = _ => throw SimulatedCleanupFailure(),
            Wait = _ => { },
        };

        try
        {
            // The Dispose must NOT throw — a cleanup problem must not replace the test's own
            // primary assertion failure.
            fixture.Dispose();

            var diagnostics = string.Join("\n", capture.Messages);
            Assert.NotEmpty(diagnostics);
            Assert.Contains(tempDir, diagnostics, StringComparison.Ordinal);
            Assert.Contains("simulated transient lock", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            // The forced failure deliberately left the directory behind; remove it so the test
            // does not accumulate residue in the temp directory.
            RemoveDirectory(tempDir);
        }
    }

    /// <summary>
    /// The dispatcher-maintenance SYNC fixture's Dispose: the diagnostic names the failing
    /// directory and carries the helper's exception, so the residue is REPORTED rather than
    /// discarded.
    /// </summary>
    [Fact]
    public void DispatcherMaintenanceSyncTests_Dispose_DiagnosticCarriesTheHelpersException()
    {
        using var capture = new DiagnosticMessageCapture();
        var fixture = new DispatcherMaintenanceSyncTests();
        var tempDir = GetTempDir(fixture);

        fixture.CleanupSeam = new TestCleanupSeam
        {
            Delete = _ => throw SimulatedCleanupFailure(),
            Wait = _ => { },
        };

        try
        {
            fixture.Dispose();

            var diagnostic = Assert.Single(capture.Messages);
            Assert.Contains(tempDir, diagnostic, StringComparison.Ordinal);

            // The helper's own loud-failure message names the directory and carries the last error
            // as its inner exception; the fixture's diagnostic must carry that information along.
            Assert.Contains("Failed to delete the temporary directory", diagnostic, StringComparison.Ordinal);
            Assert.Contains("simulated transient lock", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            RemoveDirectory(tempDir);
        }
    }

    /// <summary>
    /// The dispatcher-maintenance SYNC fixture's Dispose: the helper's BOUNDED retry budget is
    /// honoured on the failing path — the cleanup is retried a fixed number of times, not given
    /// up on immediately, and not retried forever.
    /// </summary>
    [Fact]
    public void DispatcherMaintenanceSyncTests_Dispose_RetriesTheFailingCleanupWithinTheBoundedBudget()
    {
        using var capture = new DiagnosticMessageCapture();
        var fixture = new DispatcherMaintenanceSyncTests();

        var attempts = 0;
        var waits = new List<int>();
        fixture.CleanupSeam = new TestCleanupSeam
        {
            Delete = _ =>
            {
                attempts++;
                throw SimulatedCleanupFailure();
            },
            Wait = waits.Add,
        };

        try
        {
            fixture.Dispose();

            // The helper's documented default budget: nine delete attempts, eight backoff waits.
            Assert.Equal(9, attempts);
            Assert.Equal(8, waits.Count);
            Assert.Equal(new[] { 100, 200, 300, 400, 500, 600, 700, 800 }, waits);
        }
        finally
        {
            RemoveDirectory(GetTempDir(fixture));
        }
    }

    /// <summary>
    /// The dispatcher-maintenance SYNC fixture's Dispose: a cleanup that SUCCEEDS emits NO
    /// diagnostic — the diagnostic channel reports problems, not routine cleanups.
    /// </summary>
    [Fact]
    public void DispatcherMaintenanceSyncTests_Dispose_EmitsNoDiagnosticWhenTheCleanupSucceeds()
    {
        using var capture = new DiagnosticMessageCapture();
        var fixture = new DispatcherMaintenanceSyncTests();
        var tempDir = GetTempDir(fixture);

        try
        {
            fixture.CleanupSeam = new TestCleanupSeam
            {
                Delete = path => Directory.Delete(path, recursive: true),
                Wait = _ => { },
            };

            fixture.Dispose();

            Assert.Empty(capture.Messages);
        }
        finally
        {
            // FALLBACK residue cleanup: if the assertion fails (or the Dispose regressed), the
            // directory may still exist. The assertion runs BEFORE this fallback.
            RemoveDirectory(tempDir);
        }
    }

    /// <summary>
    /// The OPTIONAL sweep's converted fixture (the config-repo git INTEGRATION fixture, whose
    /// directories real spawned git processes wrote into): its Dispose keeps the cleanup
    /// non-failing but DIAGNOSTIC, preserving the path and the exception.
    /// </summary>
    [Fact]
    public void ComposerConfigRepoGitIntegrationTests_Dispose_ReportsTheCleanupFailureWithPathAndException()
    {
        using var capture = new DiagnosticMessageCapture();
        var fixture = new ComposerConfigRepoGitIntegrationTests();
        var baseDir = Path.GetDirectoryName(fixture.ConfigRepoDir);

        fixture.CleanupSeam = new TestCleanupSeam
        {
            Delete = _ => throw SimulatedCleanupFailure(),
            Wait = _ => { },
        };

        try
        {
            // The Dispose must NOT throw.
            fixture.Dispose();

            var diagnostic = Assert.Single(capture.Messages);
            Assert.NotNull(baseDir);
            Assert.Contains(baseDir!, diagnostic, StringComparison.Ordinal);
            Assert.Contains("simulated transient lock", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            // The forced failure deliberately left the directory behind; remove it so the test
            // does not accumulate residue in the temp directory.
            if (baseDir is not null)
                RemoveDirectory(baseDir);
        }
    }

    /// <summary>
    /// The SWEEP's converted config-repo MANAGER fixture (whose real spawned <c>git clone</c>
    /// writes <c>.git</c> into the temp directory): its Dispose keeps the cleanup non-failing but
    /// DIAGNOSTIC, preserving the path and the exception.
    /// </summary>
    [Fact]
    public void ConfigRepoManagerTests_Dispose_ReportsTheCleanupFailureWithPathAndException()
    {
        using var capture = new DiagnosticMessageCapture();
        var fixture = new ConfigRepoManagerTests();
        var tempDir = GetTempDir(fixture);

        // Force the shared helper's loud failure: every delete attempt fails, so the helper
        // exhausts its bounded budget and throws with the path and the last error.
        fixture.CleanupSeam = new TestCleanupSeam
        {
            Delete = _ => throw SimulatedCleanupFailure(),
            Wait = _ => { },
        };

        try
        {
            // The Dispose must NOT throw — a cleanup problem must not replace the test's own
            // primary assertion failure.
            fixture.Dispose();

            var diagnostic = Assert.Single(capture.Messages);
            Assert.Contains(tempDir, diagnostic, StringComparison.Ordinal);
            Assert.Contains("simulated transient lock", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            // The forced failure deliberately left the directory behind; remove it so the test
            // does not accumulate residue in the temp directory.
            RemoveDirectory(tempDir);
        }
    }

    /// <summary>
    /// The SWEEP's converted config-repo MANAGER fixture: the diagnostic carries the HELPER's own
    /// loud-failure message, which names the directory and carries the last delete error — so the
    /// residue is reported with its cause, not merely announced.
    /// </summary>
    [Fact]
    public void ConfigRepoManagerTests_Dispose_DiagnosticCarriesTheHelpersException()
    {
        using var capture = new DiagnosticMessageCapture();
        var fixture = new ConfigRepoManagerTests();
        var tempDir = GetTempDir(fixture);

        fixture.CleanupSeam = new TestCleanupSeam
        {
            Delete = _ => throw SimulatedCleanupFailure(),
            Wait = _ => { },
        };

        try
        {
            fixture.Dispose();

            var diagnostic = Assert.Single(capture.Messages);
            Assert.Contains(tempDir, diagnostic, StringComparison.Ordinal);
            Assert.Contains("Failed to delete the temporary directory", diagnostic, StringComparison.Ordinal);
            Assert.Contains("simulated transient lock", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            RemoveDirectory(tempDir);
        }
    }

    /// <summary>
    /// The SWEEP's converted config-repo MANAGER fixture: a cleanup that SUCCEEDS emits NO
    /// diagnostic, and the temp directory is really gone. This is the non-vacuity control for the
    /// two failing vectors above — the diagnostic channel reports problems, not routine cleanups.
    /// </summary>
    [Fact]
    public void ConfigRepoManagerTests_Dispose_EmitsNoDiagnosticAndRemovesTheDirectoryWhenTheCleanupSucceeds()
    {
        using var capture = new DiagnosticMessageCapture();
        var fixture = new ConfigRepoManagerTests();
        var tempDir = GetTempDir(fixture);

        try
        {
            // A file inside the tree, so the delete has real work to do rather than removing an
            // already-empty directory.
            File.WriteAllText(Path.Combine(tempDir, "hive-config.yaml"), "version: \"1.0\"\n");

            fixture.Dispose();

            // BOTH assertions run before the fallback cleanup, so the fallback can never mask
            // a cleanup that failed to remove the directory.
            Assert.Empty(capture.Messages);
            Assert.False(Directory.Exists(tempDir));
        }
        finally
        {
            // FALLBACK residue cleanup: setup (File.WriteAllText) or an assertion may fail before
            // the fixture's Dispose removed the tree.
            RemoveDirectory(tempDir);
        }
    }

    /// <summary>
    /// The helper-test fixture's OWN Dispose: a left-over READ-ONLY file (the state a test that
    /// failed BEFORE the helper cleared the attribute would leave behind) does not stop the
    /// cleanup, and the fixture's directory is really gone afterwards.
    /// </summary>
    [Fact]
    public void TestHelpersForceDeleteDirectoryTests_Dispose_RemovesALeftOverReadOnlyFile()
    {
        var fixture = new TestHelpersForceDeleteDirectoryTests();
        var tempDir = GetTempDir(fixture);

        try
        {
            // The residue a failing test would leave behind: a read-only file inside the fixture's
            // temp directory. On Windows a plain one-shot delete cannot remove it.
            var readOnlyFile = Path.Combine(tempDir, "pack-file");
            File.WriteAllText(readOnlyFile, "git object");
            File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
            Assert.True(File.GetAttributes(readOnlyFile).HasFlag(FileAttributes.ReadOnly));

            // The Dispose must not throw, and the whole tree (read-only file included) must be gone.
            fixture.Dispose();

            Assert.False(Directory.Exists(tempDir));
        }
        finally
        {
            // FALLBACK residue cleanup: if an assertion above failed BEFORE the fixture's Dispose
            // ran, the read-only tree is still on disk — release it here so a failing run cannot
            // accumulate %TEMP% residue.
            RemoveDirectory(tempDir);
        }
    }

    /// <summary>
    /// The helper-test fixture's OWN Dispose: when the cleanup DOES fail, the fixture keeps a
    /// DIAGNOSTIC — the directory path and the exception — instead of swallowing the failure.
    /// </summary>
    [Fact]
    public void TestHelpersForceDeleteDirectoryTests_Dispose_KeepsDiagnosticsWhenTheCleanupFails()
    {
        using var capture = new DiagnosticMessageCapture();
        var fixture = new TestHelpersForceDeleteDirectoryTests();
        var tempDir = GetTempDir(fixture);

        fixture.CleanupSeam = new TestCleanupSeam
        {
            Delete = _ => throw SimulatedCleanupFailure(),
            Wait = _ => { },
        };

        try
        {
            // The Dispose must not throw — a cleanup problem must not replace the test's primary
            // assertion failure.
            fixture.Dispose();

            var diagnostic = Assert.Single(capture.Messages);
            Assert.Contains(tempDir, diagnostic, StringComparison.Ordinal);
            Assert.Contains("simulated transient lock", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            // The forced failure deliberately left the directory behind; remove it so the test
            // does not accumulate residue in the temp directory.
            RemoveDirectory(tempDir);
        }
    }

    /// <summary>
    /// The helper-test fixture's OWN Dispose: its cleanup is the SHARED helper, so a read-only
    /// file is cleared and deleted — the behaviour a plain one-shot delete could not provide on
    /// Windows.
    /// </summary>
    [Fact]
    public void TestHelpersForceDeleteDirectoryTests_Dispose_ClearsReadOnlyAttributesBeforeDeleting()
    {
        var fixture = new TestHelpersForceDeleteDirectoryTests();
        var tempDir = GetTempDir(fixture);

        try
        {
            var readOnlyFile = Path.Combine(tempDir, "pack-file");
            File.WriteAllText(readOnlyFile, "git object");
            File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);

            // The seam observes the tree AFTER the helper cleared the read-only attributes, so the
            // clearing is asserted from the helper's own production path.
            FileAttributes? observed = null;
            fixture.CleanupSeam = new TestCleanupSeam
            {
                Delete = path =>
                {
                    observed = File.GetAttributes(readOnlyFile);
                    Directory.Delete(path, recursive: true);
                },
                Wait = _ => { },
            };

            fixture.Dispose();

            Assert.True(observed.HasValue);
            Assert.False(observed!.Value.HasFlag(FileAttributes.ReadOnly));
            Assert.False(Directory.Exists(tempDir));
        }
        finally
        {
            // FALLBACK residue cleanup: if an assertion above failed BEFORE the fixture's Dispose
            // ran, the read-only tree is still on disk — release it here so a failing run cannot
            // accumulate %TEMP% residue.
            RemoveDirectory(tempDir);
        }
    }

    /// <summary>
    /// Reads a fixture's private temp-directory field. The field is private, so the test reads
    /// it through reflection rather than changing the fixture's public surface.
    /// </summary>
    private static string GetTempDir(object fixture)
    {
        var field = fixture.GetType().GetField(
            "_tempDir",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var value = field!.GetValue(fixture);
        Assert.IsType<string>(value);
        return (string)value!;
    }

    /// <summary>
    /// Removes a directory that a FORCED cleanup failure deliberately left behind, so the test
    /// does not accumulate residue in the temp directory.
    /// </summary>
    /// <remarks>
    /// FALLBACK CLEANUP, and it goes through the SHARED hardened helper — a private re-implementation
    /// (a one-shot <c>Directory.Delete</c> with a swallowed catch) would reintroduce exactly the
    /// swallowed-error pattern this goal removes, and it could not remove the read-only file these
    /// tests deliberately create. The helper's loud failure is still contained, because this is
    /// teardown: a failure to remove the residue must not fail the test that just proved the residue
    /// was REPORTED. It is contained the same way the fixtures contain it — by emitting a DIAGNOSTIC
    /// that keeps BOTH the path and the exception — never by discarding them.
    /// </remarks>
    private static void RemoveDirectory(string path)
    {
        try
        {
            TestHelpers.ForceDeleteDirectory(path);
        }
        catch (Exception ex)
        {
            try
            {
                TestContext.Current.SendDiagnosticMessage(
                    $"Residual temp directory '{path}' survived the test's fallback cleanup: {ex}");
            }
            catch
            {
                // A diagnostic channel that is unavailable must not fail the test either.
            }
        }
    }
}
