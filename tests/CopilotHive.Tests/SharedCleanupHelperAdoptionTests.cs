using System.Data;
using CopilotHive.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CopilotHive.Tests;

/// <summary>
/// Contract of the shared temp-directory cleanup ADOPTION: the fixtures that were converted from
/// a swallowed one-shot delete to the hardened shared helper
/// (<see cref="TestHelpers.ForceDeleteDirectory"/>) must (a) really route through that helper and
/// not keep a private duplicate, and (b) report a cleanup failure DIAGNOSTICALLY — preserving the
/// directory path and the exception — instead of swallowing it silently.
/// <para>
/// The behavioural half of (b) is proven through a deterministic SEAM: the fixtures' cleanup goes
/// through <see cref="TestCleanupSeam"/>, whose delete override can force the helper's loud
/// failure on a platform (Linux) where a one-shot directory delete virtually always succeeds.
/// A real locked directory cannot be produced on every platform, and waiting out the helper's real
/// backoff would make the test a timing test, so the seam is the only deterministic route.
/// </para>
/// </summary>
public sealed class SharedCleanupHelperAdoptionTests
{
    // ── Fix 1 — ConfigRepoManagerCancellationTests.Dispose ─────────────────

    /// <summary>
    /// The config-repo cancellation fixture's Dispose routes its cleanup through the SHARED
    /// hardened helper and keeps NO private one-shot delete of its own.
    /// </summary>
    [Fact]
    public void ConfigRepoManagerCancellationTests_Dispose_RoutesThroughTheSharedHelper()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile("ConfigRepoManagerCancellationTests.cs")));

        // The private swallowed duplicate is gone.
        Assert.DoesNotContain(
            "private static void ForceDeleteDirectory",
            code,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private static void TryDeleteDir",
            code,
            StringComparison.Ordinal);

        // The shared helper is the cleanup mechanism.
        Assert.Contains(
            "TestHelpers.ForceDeleteDirectory(",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "_tempDir,",
            code,
            StringComparison.Ordinal);

        // …and the fixture no longer performs a one-shot delete of its own.
        Assert.DoesNotContain(
            "Directory.Delete",
            code,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The config-repo cancellation fixture's Dispose calls the shared helper DIRECTLY: the call is
    /// NOT wrapped in a try/catch, so the helper's loud failure propagates. A best-effort catch
    /// would swallow the residue, which is exactly what this fixture's adoption was meant to
    /// remove.
    /// </summary>
    [Fact]
    public void ConfigRepoManagerCancellationTests_Dispose_CallsTheHelperUncaught()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile("ConfigRepoManagerCancellationTests.cs")));

        var dispose = TestSourceScanner.ExtractMethodBlock(code, "public void Dispose()");
        Assert.NotNull(dispose);

        // The call sits DIRECTLY in the method body (nesting level 0) — not inside a try/catch.
        Assert.Equal(
            0,
            TestSourceScanner.NestingLevel(dispose!, "TestHelpers.ForceDeleteDirectory("));

        // …and the method has no catch at all.
        Assert.DoesNotContain("catch", dispose!, StringComparison.Ordinal);
    }

    // ── Fix 2 — WorkerRedactionIntegrationTests.TryDeleteDir ───────────────

    /// <summary>
    /// The worker-redaction fixture's private <c>TryDeleteDir</c> — the helper that swallowed
    /// delete errors — is GONE, and its call site now uses the shared hardened helper.
    /// </summary>
    [Fact]
    public void WorkerRedactionIntegrationTests_PrivateTryDeleteDir_IsGoneAndTheSharedHelperIsUsed()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Worker", "WorkerRedactionIntegrationTests.cs"))));

        // The swallowed private helper is gone: neither the declaration nor any call to it remains.
        Assert.DoesNotContain("TryDeleteDir", code, StringComparison.Ordinal);

        // The shared helper is the cleanup mechanism, and it is the ONLY directory delete in the file.
        Assert.Contains(
            "TestHelpers.ForceDeleteDirectory(configRepoDir);",
            code,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The worker-redaction fixture's shared-helper call site is in the test body (setup/assert
    /// flow), NOT inside a try/catch: the helper's loud failure must surface as a test failure
    /// there, rather than being converted into a swallowed residue.
    /// </summary>
    [Fact]
    public void WorkerRedactionIntegrationTests_SharedHelperCall_IsNotWrappedInACatch()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Worker", "WorkerRedactionIntegrationTests.cs"))));

        var method = TestSourceScanner.ExtractMethodBlock(
            code,
            "public async Task ProgramAttemptLoop_RetryClassAttemptFault_RetriesWithoutAnyDisposalAttempt()");
        Assert.NotNull(method);

        // The call sits directly in the method body — no enclosing try/catch.
        Assert.Equal(
            0,
            TestSourceScanner.NestingLevel(method!, "TestHelpers.ForceDeleteDirectory(configRepoDir);"));
    }

    /// <summary>
    /// The worker-redaction fixture has NO Dispose call site at all: the class is not
    /// <see cref="IDisposable"/>, so its single shared-helper call is the loud one proven above.
    /// The fact is pinned here so a future <c>IDisposable</c> implementation cannot silently add
    /// an un-diagnostic best-effort cleanup call site.
    /// </summary>
    [Fact]
    public void WorkerRedactionIntegrationTests_HasNoDisposeCallSite()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Worker", "WorkerRedactionIntegrationTests.cs"))));

        var fixture = TestSourceScanner.ExtractTypeBlock(
            code,
            "public sealed class WorkerRedactionIntegrationTests");
        Assert.NotNull(fixture);

        // The class is not disposable…
        Assert.DoesNotContain("IDisposable", fixture!, StringComparison.Ordinal);

        // …and it declares no Dispose method.
        Assert.DoesNotContain("public void Dispose()", fixture!, StringComparison.Ordinal);
    }

    // ── Fix 3 — DispatcherMaintenanceSyncTests private duplicate ────────────

    /// <summary>
    /// The dispatcher-maintenance SYNC fixture keeps NO private duplicate of the shared helper and
    /// its Dispose routes through <see cref="TestHelpers.ForceDeleteDirectory"/>.
    /// </summary>
    [Fact]
    public void DispatcherMaintenanceSyncTests_HasNoPrivateDuplicate_AndUsesTheSharedHelper()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Services", "DispatcherMaintenanceTests.cs"))));

        // The private swallowed duplicate is gone — declared nowhere in the file.
        Assert.DoesNotContain(
            "private static void ForceDeleteDirectory",
            code,
            StringComparison.Ordinal);

        // The shared helper is the cleanup mechanism of the sync fixture's Dispose.
        var syncFixture = TestSourceScanner.ExtractTypeBlock(
            code,
            "public sealed class DispatcherMaintenanceSyncTests : IDisposable");
        Assert.NotNull(syncFixture);

        var dispose = TestSourceScanner.ExtractMethodBlock(syncFixture!, "public void Dispose()");
        Assert.NotNull(dispose);

        Assert.Contains(
            "TestHelpers.ForceDeleteDirectory(",
            dispose!,
            StringComparison.Ordinal);
        Assert.Contains(
            "_tempDir,",
            dispose!,
            StringComparison.Ordinal);

        // The private duplicate's one-shot delete is gone from the fixture too.
        Assert.DoesNotContain("Directory.Delete", dispose!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dispatcher-maintenance SYNC fixture's Dispose catches the shared helper's loud failure
    /// — so a cleanup problem never REPLACES the test's own primary assertion failure — and the
    /// catch hands the failure to the diagnostic reporter.
    /// </summary>
    [Fact]
    public void DispatcherMaintenanceSyncTests_Dispose_CatchesAndReportsTheHelperFailure()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Services", "DispatcherMaintenanceTests.cs"))));

        var syncFixture = TestSourceScanner.ExtractTypeBlock(
            code,
            "public sealed class DispatcherMaintenanceSyncTests : IDisposable");
        Assert.NotNull(syncFixture);

        var dispose = TestSourceScanner.ExtractMethodBlock(syncFixture!, "public void Dispose()");
        Assert.NotNull(dispose);

        // The helper's call IS wrapped: the call sits inside the try block, so its loud failure
        // is actually caught (a call outside the try would let it escape the disposable). The
        // predicate examines EVERY occurrence, so an earlier unprotected duplicate cannot be
        // hidden by a later protected one.
        Assert.True(
            TestSourceScanner.IsInsideTryBlock(dispose!, "TestHelpers.ForceDeleteDirectory("),
            "The shared helper's call must sit inside the try block, so its loud failure is caught "
            + "and reported instead of escaping the disposable.");

        // The previously approved, independent guard is RESTORED: the FIRST occurrence sits one
        // level deep (inside the try). An unprotected call written before the protected one has
        // level 0 and fails here as well as in the membership predicate above.
        Assert.Equal(
            1,
            TestSourceScanner.NestingLevel(dispose!, "TestHelpers.ForceDeleteDirectory("));
        // The catch exists and routes the failure to the reporter.
        Assert.Contains("catch (Exception ex)", dispose!, StringComparison.Ordinal);
        Assert.Contains("ReportCleanupFailure(_tempDir, ex);", dispose!, StringComparison.Ordinal);

        // The reporter is called from the CATCH block (the handler that received the failure),
        // not from the try block itself.
        Assert.True(
            TestSourceScanner.IsInsideCatchBlock(dispose!, "ReportCleanupFailure(_tempDir, ex);"),
            "The cleanup reporter must be called from the catch block, so it observes the failure.");
    }

    /// <summary>
    /// The reviewer's distinguishing case, applied to the REAL Dispatcher fixture source: a bare
    /// <c>TestHelpers.ForceDeleteDirectory(_tempDir)</c> PREPENDED before the existing protected
    /// call can throw before the protected cleanup is reached. The real, unmodified Dispose is
    /// accepted (control); the mutated copy is rejected by the membership predicate AND by the
    /// first-occurrence nesting level.
    /// </summary>
    [Fact]
    public void DispatcherMaintenanceSyncTests_Dispose_PrependedUnprotectedDuplicate_IsDetected()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Services", "DispatcherMaintenanceTests.cs"))));

        var syncFixture = TestSourceScanner.ExtractTypeBlock(
            code,
            "public sealed class DispatcherMaintenanceSyncTests : IDisposable");
        Assert.NotNull(syncFixture);

        var dispose = TestSourceScanner.ExtractMethodBlock(syncFixture!, "public void Dispose()");
        Assert.NotNull(dispose);

        // CONTROL: the real Dispose is fully protected.
        Assert.True(TestSourceScanner.IsInsideTryBlock(dispose!, "TestHelpers.ForceDeleteDirectory("));
        Assert.Equal(1, TestSourceScanner.NestingLevel(dispose!, "TestHelpers.ForceDeleteDirectory("));

        // MUTANT: an unprotected duplicate immediately after the method's opening brace.
        var open = dispose!.IndexOf('{');
        var mutated = dispose.Insert(open + 1, "\n        TestHelpers.ForceDeleteDirectory(_tempDir);\n");

        Assert.False(
            TestSourceScanner.IsInsideTryBlock(mutated, "TestHelpers.ForceDeleteDirectory("),
            "An earlier unprotected duplicate must be detected even though a later call is protected.");
        Assert.Equal(0, TestSourceScanner.NestingLevel(mutated, "TestHelpers.ForceDeleteDirectory("));
    }

    /// <summary>
    /// The dispatcher-maintenance SYNC fixture's cleanup reporter PRESERVES the diagnostic
    /// information: the emitted message names the directory path AND carries the exception. A
    /// reporter that dropped either would turn a loud failure into a silent swallow.
    /// </summary>
    /// <remarks>
    /// The assertions target the ARGUMENTS the reporter actually interpolates into the emitted
    /// message, not the method's text: the signature (<c>(string path, Exception failure)</c>) and
    /// the forwarding call <c>ReportCleanupFailure(_tempDir, ex)</c> already contain the words
    /// "path" and "failure", so a check for those words would pass even if the message dropped the
    /// values entirely. The argument text of the <c>SendDiagnosticMessage(...)</c> call is read from
    /// the ORIGINAL source (literals preserved) — the masked text blanks every literal, so it could
    /// never show what the message interpolates.
    /// </remarks>
    [Fact]
    public void DispatcherMaintenanceSyncTests_CleanupReporter_PreservesPathAndException()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Services", "DispatcherMaintenanceTests.cs"))));
        var withLiterals = File.ReadAllText(
            TestRepoRoot.TestFile(Path.Combine("Services", "DispatcherMaintenanceTests.cs")));

        var reporter = TestSourceScanner.ExtractMethodBlock(
            code,
            "private static void ReportCleanupFailure(string path, Exception failure)");
        Assert.NotNull(reporter);

        // The reporter emits through the diagnostic channel at all.
        Assert.Contains("SendDiagnosticMessage", reporter!, StringComparison.Ordinal);

        // …and the message it emits interpolates BOTH values into the literal.
        var reporterWithLiterals = TestSourceScanner.ExtractMethodBlockPreservingLiterals(
            withLiterals,
            "private static void ReportCleanupFailure(string path, Exception failure)");
        Assert.NotNull(reporterWithLiterals);

        var message = TestSourceScanner.CallArguments(reporterWithLiterals!, "SendDiagnosticMessage");
        Assert.NotNull(message);

        // The PATH is interpolated — the parameter, not the literal word "path".
        Assert.Contains("{path}", message!, StringComparison.Ordinal);

        // The EXCEPTION is interpolated — the parameter, not the literal word "failure".
        Assert.Contains("{failure}", message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dispatcher-maintenance SYNC fixture's cleanup reporter never itself throws: a diagnostic
    /// channel that is unavailable must not turn a cleanup problem into a test failure.
    /// </summary>
    [Fact]
    public void DispatcherMaintenanceSyncTests_CleanupReporter_NeverThrows()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Services", "DispatcherMaintenanceTests.cs"))));

        var reporter = TestSourceScanner.ExtractMethodBlock(
            code,
            "private static void ReportCleanupFailure(string path, Exception failure)");
        Assert.NotNull(reporter);

        // The SendDiagnosticMessage call is inside a try whose catch swallows ONLY the reporting
        // failure — the reporter itself has an inner catch, so it cannot propagate.
        Assert.Equal(
            1,
            TestSourceScanner.NestingLevel(reporter!, "SendDiagnosticMessage"));
        Assert.Contains("catch", reporter!, StringComparison.Ordinal);
    }

    // ── The OPTIONAL sweep — ComposerConfigRepoGitIntegrationTests.Dispose ─

    /// <summary>
    /// The optional sweep's converted fixture (the config-repo git INTEGRATION fixture, whose
    /// directories real spawned git processes wrote into) routes its cleanup through the shared
    /// helper and keeps its cleanup failure DIAGNOSTIC — path plus exception.
    /// </summary>
    [Fact]
    public void ComposerConfigRepoGitIntegrationTests_Dispose_UsesTheSharedHelperAndKeepsDiagnostics()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile(Path.Combine("Orchestration", "ComposerToolTests.cs"))));

        var fixture = TestSourceScanner.ExtractTypeBlock(
            code,
            "public sealed class ComposerConfigRepoGitIntegrationTests : IDisposable");
        Assert.NotNull(fixture);

        var dispose = TestSourceScanner.ExtractMethodBlock(fixture!, "public void Dispose()");
        Assert.NotNull(dispose);

        // The shared helper replaces the swallowed one-shot delete.
        Assert.Contains(
            "TestHelpers.ForceDeleteDirectory(",
            dispose!,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete", dispose!, StringComparison.Ordinal);

        // The loud failure is caught and reported, with the path and the exception.
        Assert.Contains("catch (Exception ex)", dispose!, StringComparison.Ordinal);
        Assert.Contains("SendDiagnosticMessage", dispose!, StringComparison.Ordinal);

        // The helper's call is INSIDE the try block, so its loud failure is actually caught: a
        // call at the method-body level would let the failure escape the disposable and fail the
        // test, and a plain text search for "catch" would not notice.
        Assert.True(
            TestSourceScanner.IsInsideTryBlock(dispose!, "TestHelpers.ForceDeleteDirectory("),
            "The shared helper's call must sit inside the try block, so its loud failure is caught "
            + "and reported instead of escaping the disposable.");

        // The diagnostic is emitted from the CATCH block.
        Assert.True(
            TestSourceScanner.IsInsideCatchBlock(dispose!, "SendDiagnosticMessage"),
            "The cleanup diagnostic must be emitted from the catch block, so it reports the failure.");
    }

    // ── The ConfigRepoManagerTests sweep ─────────────────────────────────────

    /// <summary>
    /// The config-repo MANAGER fixture (real `git clone` destinations) routes its Dispose cleanup
    /// through the SHARED hardened helper and keeps NO one-shot delete of its own.
    /// </summary>
    /// <remarks>
    /// This fixture's retry is genuinely needed even though it kills nothing: its
    /// <c>SyncRepoAsync_ConcurrentCalls_AreSerializedBySemaphore</c> drives a REAL manager over
    /// <c>_tempDir</c> with no <c>GitRunner</c> seam, so <c>SyncRepoAsync</c> reaches the real
    /// <c>git clone</c> (ConfigRepoManager.cs:187), which initialises and writes <c>.git</c> in that
    /// destination from a real spawned process — for a failed clone too.
    /// </remarks>
    [Fact]
    public void ConfigRepoManagerTests_Dispose_UsesTheSharedHelperAndKeepsDiagnostics()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile("ConfigRepoManagerTests.cs")));

        var fixture = TestSourceScanner.ExtractTypeBlock(
            code,
            "public class ConfigRepoManagerTests : IDisposable");
        Assert.NotNull(fixture);

        var dispose = TestSourceScanner.ExtractMethodBlock(fixture!, "public void Dispose()");
        Assert.NotNull(dispose);

        // The shared helper replaces the one-shot delete.
        Assert.Contains(
            "TestHelpers.ForceDeleteDirectory(",
            dispose!,
            StringComparison.Ordinal);
        Assert.Contains("_tempDir,", dispose!, StringComparison.Ordinal);

        // …and the fixture's Dispose performs no delete of its own.
        Assert.DoesNotContain("Directory.Delete", dispose!, StringComparison.Ordinal);

        // The loud failure is caught (never thrown from a disposable) and reported.
        Assert.Contains("catch (Exception ex)", dispose!, StringComparison.Ordinal);
        Assert.Contains("ReportCleanupFailure(_tempDir, ex);", dispose!, StringComparison.Ordinal);

        // The helper's call is INSIDE the try block, so its loud failure is actually caught: a
        // call outside the try would let the failure escape the disposable and fail the test,
        // and a plain text search for "catch" would not notice.
        Assert.True(
            TestSourceScanner.IsInsideTryBlock(dispose!, "TestHelpers.ForceDeleteDirectory("),
            "The shared helper's call must sit inside the try block, so its loud failure is caught "
            + "and reported instead of escaping the disposable.");

        // The reporter is called from the CATCH block, i.e. from inside the handler that
        // actually received the failure — not from the try block itself.
        Assert.True(
            TestSourceScanner.IsInsideCatchBlock(dispose!, "ReportCleanupFailure(_tempDir, ex);"),
            "The cleanup reporter must be called from the catch block, so it observes the failure.");
    }

    /// <summary>
    /// The config-repo manager fixture's cleanup reporter PRESERVES the diagnostic information:
    /// the emitted message interpolates the directory path AND the exception. Asserted against the
    /// <c>SendDiagnosticMessage(...)</c> ARGUMENTS, not against the method text — the signature and
    /// the forwarding call already contain the words "path" and "failure", so a plain text search
    /// would pass even if the message dropped the values.
    /// </summary>
    [Fact]
    public void ConfigRepoManagerTests_CleanupReporter_PreservesPathAndException()
    {
        var code = TestSourceScanner.StripNonCode(
            File.ReadAllText(TestRepoRoot.TestFile("ConfigRepoManagerTests.cs")));
        var withLiterals = File.ReadAllText(TestRepoRoot.TestFile("ConfigRepoManagerTests.cs"));

        var reporter = TestSourceScanner.ExtractMethodBlock(
            code,
            "private static void ReportCleanupFailure(string path, Exception failure)");
        Assert.NotNull(reporter);
        Assert.Contains("SendDiagnosticMessage", reporter!, StringComparison.Ordinal);

        var reporterWithLiterals = TestSourceScanner.ExtractMethodBlockPreservingLiterals(
            withLiterals,
            "private static void ReportCleanupFailure(string path, Exception failure)");
        Assert.NotNull(reporterWithLiterals);

        var message = TestSourceScanner.CallArguments(reporterWithLiterals!, "SendDiagnosticMessage");
        Assert.NotNull(message);
        Assert.Contains("{path}", message!, StringComparison.Ordinal);
        Assert.Contains("{failure}", message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The config-repo manager fixture really SPAWNS the real clone that writes into its temp
    /// directory: the semaphore-serialization test builds a real manager without installing the
    /// <c>GitRunner</c> seam. Pinned so the retrying cleanup's rationale cannot silently become
    /// stale (if the test started stubbing git, the fixture would no longer need the retry).
    /// </summary>
    /// <remarks>
    /// REMOVAL-PROOF: installing <c>GitRunner = ...</c> in that test makes the third assertion fail,
    /// because the production real-`git clone` branch would no longer be reached.
    /// </remarks>
    [Fact]
    public void ConfigRepoManagerTests_SemaphoreTest_DrivesTheRealCloneWithoutTheGitRunnerSeam()
    {
        var path = TestRepoRoot.TestFile("ConfigRepoManagerTests.cs");
        var code = TestSourceScanner.StripNonCode(File.ReadAllText(path));

        const string marker = "public async Task SyncRepoAsync_ConcurrentCalls_AreSerializedBySemaphore()";
        var method = TestSourceScanner.ExtractMethodBlock(code, marker);
        Assert.NotNull(method);

        // The manager is built over _tempDir — the directory the fixture's Dispose cleans. Read
        // from the ORIGINAL source, because the masked copy blanks the URL literal inside the call.
        var methodWithLiterals = TestSourceScanner.ExtractMethodBlockPreservingLiterals(
            File.ReadAllText(path),
            marker);
        Assert.NotNull(methodWithLiterals);

        // CallArguments locates the call by NAME and then reads between ITS parens, so the marker
        // must stop before the opening parenthesis.
        var managerArguments = TestSourceScanner.CallArguments(methodWithLiterals!, "new ConfigRepoManager");
        Assert.NotNull(managerArguments);

        // …its clone DESTINATION is this fixture's temp directory…
        Assert.Contains("_tempDir", managerArguments!, StringComparison.Ordinal);

        // …it really calls SyncRepoAsync…
        Assert.Contains("manager.SyncRepoAsync(ct)", method!, StringComparison.Ordinal);

        // …and NO GitRunner seam is installed, so the production real-process clone path runs.
        Assert.DoesNotContain("GitRunner", method!, StringComparison.Ordinal);
    }
}
