using System.Diagnostics;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for <see cref="KnowledgeDocumentCleanupService"/>.
/// </summary>
public sealed class KnowledgeDocumentCleanupServiceTests : IDisposable
{
    private readonly string _tempDir;

    public KnowledgeDocumentCleanupServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KDCleanupTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        TestHelpers.ForceDeleteDirectory(_tempDir);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static KnowledgeDocumentCleanupService CreateService(
        KnowledgeGraph? graph,
        ILogger<KnowledgeDocumentCleanupService>? logger = null)
        => new(graph, logger ?? NullLogger<KnowledgeDocumentCleanupService>.Instance);

    private static Task CreateDocAsync(KnowledgeGraph graph, string id, string? topic = null)
        => graph.CreateDocumentAsync(
            id, $"Doc {id}", DocumentType.Scratch, "content", topic: topic,
            ct: TestContext.Current.CancellationToken);

    private static async Task RunGitCommandAsync(string workingDir, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Disable commit signing: a host with commit.gpgsign=true globally configured can make
        // many concurrent `git commit` calls (under high xUnit parallelism) contend for the GPG
        // agent and intermittently fail with "gpg: signing failed: Not enough space" — these
        // test commits don't need to be signed.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await proc.WaitForExitAsync(TestContext.Current.CancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git exited with code {proc.ExitCode}: {stdout}\n{stderr}".Trim());
    }

    private static async Task<string> RunGitOutputAsync(string workingDir, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await proc.WaitForExitAsync(TestContext.Current.CancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git exited with code {proc.ExitCode}: {stdout}\n{stderr}".Trim());
        return stdout;
    }

    /// <summary>Creates a bare remote + clone pair with an initial commit pushed.</summary>
    private async Task<(string BareDir, string CloneDir)> CreateGitRemoteAsync()
    {
        var bareDir = Path.Combine(_tempDir, $"bare-{Guid.NewGuid():N}");
        var cloneDir = Path.Combine(_tempDir, $"clone-{Guid.NewGuid():N}");

        Directory.CreateDirectory(bareDir);
        await RunGitCommandAsync(bareDir, ["init", "--bare"]);

        Directory.CreateDirectory(cloneDir);
        await RunGitCommandAsync(Path.GetDirectoryName(cloneDir)!,
            ["clone", bareDir, Path.GetFileName(cloneDir)]);
        await RunGitCommandAsync(cloneDir, ["config", "user.email", "test@test.com"]);
        await RunGitCommandAsync(cloneDir, ["config", "user.name", "Test"]);

        await File.WriteAllTextAsync(Path.Combine(cloneDir, "hive-config.yaml"),
            "version: \"1.0\"\n", TestContext.Current.CancellationToken);
        await RunGitCommandAsync(cloneDir, ["add", "--all"]);
        await RunGitCommandAsync(cloneDir, ["commit", "-m", "initial"]);
        await RunGitCommandAsync(cloneDir, ["push", "origin", "HEAD"]);

        return (bareDir, cloneDir);
    }

    // ── 1. In-memory graph (no configRepo) ───────────────────────────────────

    [Fact]
    public async Task CleanupGoalDocumentsAsync_InMemoryGraph_DeletesBothDocs()
    {
        var graph = new KnowledgeGraph();
        await CreateDocAsync(graph, "progress-g1");
        await CreateDocAsync(graph, "review-g1");
        var service = CreateService(graph);

        var count = await service.CleanupGoalDocumentsAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
        Assert.Null(graph.GetDocument("progress-g1"));
        Assert.Null(graph.GetDocument("review-g1"));
    }

    [Fact]
    public async Task CleanupGoalDocumentsAsync_OnlyProgressDoc_DeletesOne()
    {
        var graph = new KnowledgeGraph();
        await CreateDocAsync(graph, "progress-g1");
        var service = CreateService(graph);

        var count = await service.CleanupGoalDocumentsAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CleanupGoalDocumentsAsync_NeitherDocExists_ReturnsZero()
    {
        var graph = new KnowledgeGraph();
        var service = CreateService(graph);

        var count = await service.CleanupGoalDocumentsAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CleanupGoalDocumentsAsync_NullGraph_ReturnsZero()
    {
        var service = CreateService(null);

        var count = await service.CleanupGoalDocumentsAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CleanupGoalDocumentsAsync_NullGraph_PreCancelledToken_Throws()
    {
        var service = CreateService(null);
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.CleanupGoalDocumentsAsync("g1", canceled));
    }

    // ── 2. Argument validation ───────────────────────────────────────────────

    [Fact]
    public async Task CleanupGoalDocumentsAsync_NullGoalId_ThrowsArgumentException()
    {
        var service = CreateService(new KnowledgeGraph());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CleanupGoalDocumentsAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CleanupGoalDocumentsAsync_WhitespaceGoalId_ThrowsArgumentException()
    {
        var service = CreateService(new KnowledgeGraph());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CleanupGoalDocumentsAsync("   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CleanupGoalsDocumentsAsync_NullGoalIds_ThrowsArgumentNullException()
    {
        var service = CreateService(new KnowledgeGraph());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.CleanupGoalsDocumentsAsync(null!, "ctx", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CleanupGoalsDocumentsAsync_NullCommitContext_ThrowsArgumentException()
    {
        var service = CreateService(new KnowledgeGraph());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CleanupGoalsDocumentsAsync(["g1"], null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CleanupGoalsDocumentsAsync_WhitespaceCommitContext_ThrowsArgumentException()
    {
        var service = CreateService(new KnowledgeGraph());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CleanupGoalsDocumentsAsync(["g1"], "  ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new KnowledgeDocumentCleanupService(new KnowledgeGraph(), null!));
    }

    [Fact]
    public async Task SweepOrphanedDocumentsAsync_NullAllGoals_ThrowsArgumentNullException()
    {
        var service = CreateService(new KnowledgeGraph());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.SweepOrphanedDocumentsAsync(null!, [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SweepOrphanedDocumentsAsync_NullAllReleases_ThrowsArgumentNullException()
    {
        var service = CreateService(new KnowledgeGraph());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.SweepOrphanedDocumentsAsync([], null!, TestContext.Current.CancellationToken));
    }

    // ── 3. goalIds with null/whitespace elements + dedup ─────────────────────

    [Fact]
    public async Task CleanupGoalsDocumentsAsync_SkipsInvalidElements_AndDeduplicates()
    {
        var graph = new KnowledgeGraph();
        await CreateDocAsync(graph, "progress-g1");
        await CreateDocAsync(graph, "review-g1");
        await CreateDocAsync(graph, "progress-g2");
        await CreateDocAsync(graph, "review-g2");
        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();
        var service = CreateService(graph, logger);

        var count = await service.CleanupGoalsDocumentsAsync(
            ["g1", "G1", "g1", null!, "   ", "g2"], "batch cleanup", TestContext.Current.CancellationToken);

        // 2 docs for g1 (deduplicated from g1/G1/g1) + 2 docs for g2.
        Assert.Equal(4, count);
        Assert.Null(graph.GetDocument("progress-g1"));
        Assert.Null(graph.GetDocument("review-g1"));
        Assert.Null(graph.GetDocument("progress-g2"));
        Assert.Null(graph.GetDocument("review-g2"));

        // Two warnings: one for the null element, one for the whitespace element.
        var warnings = logger.LogEntries.Where(e => e.LogLevel == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("Skipping null or whitespace goal ID", w.Message));
    }

    // ── 4. Pre-cancelled ct ──────────────────────────────────────────────────

    [Fact]
    public async Task PreCancelledToken_AllMethods_ThrowOperationCanceled()
    {
        var service = CreateService(new KnowledgeGraph());
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.CleanupGoalDocumentsAsync("g1", canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.CleanupGoalsDocumentsAsync(["g1"], "ctx", canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SweepOrphanedDocumentsAsync([], [], canceled));
    }

    [Fact]
    public async Task CleanupGoalsDocumentsAsync_NullGraph_ReturnsZero()
    {
        var service = CreateService(null);

        var count = await service.CleanupGoalsDocumentsAsync(
            ["g1", "g2"], "ctx", TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
    }

    // ── 5. Throwing IEnumerable (non-OCE) ────────────────────────────────────

    [Fact]
    public async Task CleanupGoalsDocumentsAsync_ThrowingEnumerable_ReturnsZeroAndLogsWarning()
    {
        var graph = new KnowledgeGraph();
        await CreateDocAsync(graph, "progress-g1");
        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();
        var service = CreateService(graph, logger);
        var throwing = new ThrowingGoalIdsEnumerable();

        var count = await service.CleanupGoalsDocumentsAsync(throwing, "ctx", TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
        Assert.True(throwing.GetEnumeratorCalled, "The throwing enumerator must have been invoked");
        Assert.Contains(logger.LogEntries,
            e => e.LogLevel == LogLevel.Warning &&
                 e.Message.Contains("Failed to enumerate goal IDs", StringComparison.Ordinal) &&
                 e.Exception is InvalidOperationException);
        // Nothing was deleted.
        Assert.NotNull(graph.GetDocument("progress-g1"));
    }

    [Fact]
    public async Task CleanupGoalsDocumentsAsync_EnumerableThrowsOperationCanceled_Rethrows()
    {
        var graph = new KnowledgeGraph();
        await CreateDocAsync(graph, "progress-g1");
        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();
        var service = CreateService(graph, logger);
        var throwing = new ThrowingOceGoalIdsEnumerable();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.CleanupGoalsDocumentsAsync(throwing, "ctx", TestContext.Current.CancellationToken));

        Assert.True(throwing.GetEnumeratorCalled, "The throwing enumerator must have been invoked");
        // No warning should be logged for an OCE — it propagates.
        Assert.DoesNotContain(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
        // Nothing was deleted.
        Assert.NotNull(graph.GetDocument("progress-g1"));
    }

    // ── 6. Persist failure ───────────────────────────────────────────────────

    [Fact]
    public async Task CleanupGoalDocumentsAsync_PersistFails_ReturnsDeletedCountAndLogsWarning()
    {
        // A ConfigRepoManager whose local path is not a git repository makes every
        // git operation fail, so DeleteDocumentsAndCommitAsync reports Persisted=false.
        var brokenPath = Path.Combine(_tempDir, $"broken-{Guid.NewGuid():N}");
        Directory.CreateDirectory(brokenPath);
        var manager = new ConfigRepoManager("https://example.com/broken.git", brokenPath);
        var graph = new KnowledgeGraph(manager);
        await CreateDocAsync(graph, "progress-g1");
        await CreateDocAsync(graph, "review-g1");
        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();
        var service = CreateService(graph, logger);

        var count = await service.CleanupGoalDocumentsAsync("g1", TestContext.Current.CancellationToken);

        Assert.True(count > 0, "Docs should be removed from the in-memory graph even when persist fails");
        Assert.Contains(logger.LogEntries,
            e => e.LogLevel == LogLevel.Warning &&
                 e.Message.Contains("Failed to persist cleanup of progress/review docs", StringComparison.Ordinal));
    }

    // ── 7. Persist retry ─────────────────────────────────────────────────────

    [Fact]
    public async Task CleanupGoalDocumentsAsync_RetryAfterPersistFailure_ReturnsZeroWithoutWarning()
    {
        var flakyPath = Path.Combine(_tempDir, $"flaky-{Guid.NewGuid():N}");
        Directory.CreateDirectory(flakyPath);
        var manager = new FlakyDeleteConfigRepoManager(flakyPath);
        var graph = new KnowledgeGraph(manager);
        await CreateDocAsync(graph, "progress-g1");
        await CreateDocAsync(graph, "review-g1");
        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();
        var service = CreateService(graph, logger);

        var first = await service.CleanupGoalDocumentsAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(2, first);
        var warningsAfterFirst = logger.LogEntries.Count(e => e.LogLevel == LogLevel.Warning);
        Assert.Equal(1, warningsAfterFirst);

        var second = await service.CleanupGoalDocumentsAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(0, second);
        var warningsAfterSecond = logger.LogEntries.Count(e => e.LogLevel == LogLevel.Warning);
        Assert.Equal(1, warningsAfterSecond); // no additional warning
    }

    // ── 8. Batch ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CleanupGoalsDocumentsAsync_TwoGoals_TwoDocsEach_DeletesFour()
    {
        var graph = new KnowledgeGraph();
        await CreateDocAsync(graph, "progress-g1");
        await CreateDocAsync(graph, "review-g1");
        await CreateDocAsync(graph, "progress-g2");
        await CreateDocAsync(graph, "review-g2");
        var service = CreateService(graph);

        var count = await service.CleanupGoalsDocumentsAsync(["g1", "g2"], "batch", TestContext.Current.CancellationToken);

        Assert.Equal(4, count);
    }

    [Fact]
    public async Task CleanupGoalsDocumentsAsync_PersistFails_ReturnsDeletedCountAndLogsWarning()
    {
        // A ConfigRepoManager whose local path is not a git repository makes every
        // git operation fail, so DeleteDocumentsAndCommitAsync reports Persisted=false.
        var brokenPath = Path.Combine(_tempDir, $"broken-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(brokenPath);
        var manager = new ConfigRepoManager("https://example.com/broken.git", brokenPath);
        var graph = new KnowledgeGraph(manager);
        await CreateDocAsync(graph, "progress-g1");
        await CreateDocAsync(graph, "review-g1");
        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();
        var service = CreateService(graph, logger);

        var count = await service.CleanupGoalsDocumentsAsync(
            ["g1"], "batch cleanup", TestContext.Current.CancellationToken);

        Assert.True(count > 0, "Docs should be removed from the in-memory graph even when persist fails");
        Assert.Null(graph.GetDocument("progress-g1"));
        Assert.Null(graph.GetDocument("review-g1"));
        Assert.Contains(logger.LogEntries,
            e => e.LogLevel == LogLevel.Warning &&
                 e.Message.Contains("Failed to persist cleanup of goal documents", StringComparison.Ordinal));
    }

    // ── 9. Successful persist integration ────────────────────────────────────

    [Fact]
    public async Task CleanupGoalDocumentsAsync_WithRealGitRemote_DeletesAndPersists()
    {
        var (bareDir, cloneDir) = await CreateGitRemoteAsync();
        var manager = new ConfigRepoManager(bareDir, cloneDir);
        var graph = new KnowledgeGraph(manager);
        await CreateDocAsync(graph, "progress-g1");
        await CreateDocAsync(graph, "review-g1");

        // Write the docs to disk and commit them so the deletion has real files to remove.
        await graph.CommitToConfigRepoAsync(cloneDir, "create docs", TestContext.Current.CancellationToken);

        var progressPath = Path.Combine(cloneDir, "knowledge", "progress", "g1.md");
        var reviewPath = Path.Combine(cloneDir, "knowledge", "review", "g1.md");
        Assert.True(File.Exists(progressPath));
        Assert.True(File.Exists(reviewPath));

        // The deletion must have been committed to git — the log grows after cleanup.
        var logBefore = int.Parse((await RunGitOutputAsync(cloneDir, ["rev-list", "--count", "HEAD"])).Trim());
        Assert.True(logBefore >= 2, $"Expected at least the initial commit plus doc commits, got {logBefore}");

        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();
        var service = CreateService(graph, logger);

        var count = await service.CleanupGoalDocumentsAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
        Assert.Null(graph.GetDocument("progress-g1"));
        Assert.Null(graph.GetDocument("review-g1"));
        Assert.False(File.Exists(progressPath));
        Assert.False(File.Exists(reviewPath));
        Assert.DoesNotContain(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);

        // The deletion must have been committed to git — the log grows after cleanup.
        var logAfter = int.Parse((await RunGitOutputAsync(cloneDir, ["rev-list", "--count", "HEAD"])).Trim());
        Assert.True(logAfter > logBefore, $"Git log must grow after cleanup (before: {logBefore}, after: {logAfter})");
    }

    // ── 10. Sweep (all scenarios) ────────────────────────────────────────────

    [Fact]
    public async Task SweepOrphanedDocumentsAsync_DeletesOrphansAndReleased_KeepsEverythingElse()
    {
        var graph = new KnowledgeGraph();

        // (a) live unreleased progress → KEPT
        await CreateDocAsync(graph, "progress-gA");
        // (b) progress in Released release → DELETED
        await CreateDocAsync(graph, "progress-gB");
        // (c) review for orphaned goal → DELETED
        await CreateDocAsync(graph, "review-gC");
        // (d) non-progress/review topic → KEPT
        await CreateDocAsync(graph, "features-whatever");
        // (e) topic progress + id review-... → KEPT (mismatched pair)
        await CreateDocAsync(graph, "review-mismatch", topic: "progress");
        // (f) live unreleased review → KEPT
        await CreateDocAsync(graph, "review-gA");
        // (g) review in Released release → DELETED
        await CreateDocAsync(graph, "review-gB");
        // (h) mixed-case Progress-MyGoal orphaned → KEPT (Ordinal prefix mismatch)
        await CreateDocAsync(graph, "Progress-MyGoal");
        // (i) empty goal ID (progress-) → KEPT
        await CreateDocAsync(graph, "progress-");
        // (j) missing-release-reference → KEPT (goal exists, release not released/known)
        await CreateDocAsync(graph, "progress-gD");

        var goals = new List<Goal>
        {
            new() { Id = "gA", Description = "Live unreleased" },
            new() { Id = "gB", Description = "In released release", ReleaseId = "r1" },
            new() { Id = "gD", Description = "Missing release reference", ReleaseId = "missing-release" },
        };
        var releases = new List<Release>
        {
            new() { Id = "r1", Tag = "v1.0.0", Status = ReleaseStatus.Released },
        };
        var service = CreateService(graph);

        var count = await service.SweepOrphanedDocumentsAsync(goals, releases, TestContext.Current.CancellationToken);

        Assert.Equal(3, count); // (b), (c), (g)

        // Deleted
        Assert.Null(graph.GetDocument("progress-gB"));
        Assert.Null(graph.GetDocument("review-gC"));
        Assert.Null(graph.GetDocument("review-gB"));

        // Kept
        Assert.NotNull(graph.GetDocument("progress-gA"));
        Assert.NotNull(graph.GetDocument("features-whatever"));
        Assert.NotNull(graph.GetDocument("review-mismatch"));
        Assert.NotNull(graph.GetDocument("review-gA"));
        Assert.NotNull(graph.GetDocument("Progress-MyGoal"));
        Assert.NotNull(graph.GetDocument("progress-"));
        Assert.NotNull(graph.GetDocument("progress-gD"));
    }

    [Fact]
    public async Task SweepOrphanedDocumentsAsync_MixedCasePrefix_KeptNotDeleted()
    {
        var graph = new KnowledgeGraph();
        // Topic matches case-insensitively, but the ID prefix "Progress-" does not match
        // "progress-" under StringComparison.Ordinal, so the document is never a candidate
        // even though its goal is orphaned.
        await CreateDocAsync(graph, "Progress-orphan", topic: "progress");
        var service = CreateService(graph);

        var count = await service.SweepOrphanedDocumentsAsync([], [], TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
        Assert.NotNull(graph.GetDocument("Progress-orphan"));
    }

    [Fact]
    public async Task SweepOrphanedDocumentsAsync_WithRealGitRemote_DeletesAllStaleDocsInSingleCommit()
    {
        var (bareDir, cloneDir) = await CreateGitRemoteAsync();
        var manager = new ConfigRepoManager(bareDir, cloneDir);
        var graph = new KnowledgeGraph(manager);

        // Stale docs (deleted by sweep):
        //  - progress-gX: orphaned (goal not in store)
        //  - review-gY: orphaned (goal not in store)
        //  - progress-gZ: goal is in a Released release
        // Kept doc:
        //  - progress-gA: live, unreleased goal
        await CreateDocAsync(graph, "progress-gX");
        await CreateDocAsync(graph, "review-gY");
        await CreateDocAsync(graph, "progress-gZ");
        await CreateDocAsync(graph, "progress-gA");

        // Write the docs to disk and commit them so the deletion has real files to remove.
        await graph.CommitToConfigRepoAsync(cloneDir, "create docs", TestContext.Current.CancellationToken);

        // Note: BuildFilePath lowercases the on-disk paths (e.g. knowledge/progress/gx.md).
        var gXPath = Path.Combine(cloneDir, "knowledge", "progress", "gx.md");
        var gYPath = Path.Combine(cloneDir, "knowledge", "review", "gy.md");
        var gZPath = Path.Combine(cloneDir, "knowledge", "progress", "gz.md");
        var gAPath = Path.Combine(cloneDir, "knowledge", "progress", "ga.md");
        Assert.True(File.Exists(gXPath));
        Assert.True(File.Exists(gYPath));
        Assert.True(File.Exists(gZPath));
        Assert.True(File.Exists(gAPath));

        var logBefore = int.Parse((await RunGitOutputAsync(cloneDir, ["rev-list", "--count", "HEAD"])).Trim());

        var goals = new List<Goal>
        {
            new() { Id = "gA", Description = "Live unreleased" },
            new() { Id = "gZ", Description = "In released release", ReleaseId = "r1" },
        };
        var releases = new List<Release>
        {
            new() { Id = "r1", Tag = "v1.0.0", Status = ReleaseStatus.Released },
        };
        var service = CreateService(graph);

        var count = await service.SweepOrphanedDocumentsAsync(goals, releases, TestContext.Current.CancellationToken);

        Assert.Equal(3, count);
        Assert.Null(graph.GetDocument("progress-gX"));
        Assert.Null(graph.GetDocument("review-gY"));
        Assert.Null(graph.GetDocument("progress-gZ"));
        Assert.NotNull(graph.GetDocument("progress-gA"));
        Assert.False(File.Exists(gXPath));
        Assert.False(File.Exists(gYPath));
        Assert.False(File.Exists(gZPath));
        Assert.True(File.Exists(gAPath));

        // Deletions must be persisted to git in exactly ONE commit.
        // CommitInternal batches 2+ deleted paths into a single path-scoped
        // ConfigRepoManager.DeleteFilesAsync call, so the log grows by exactly 1.
        var logAfter = int.Parse((await RunGitOutputAsync(cloneDir, ["rev-list", "--count", "HEAD"])).Trim());
        Assert.True(logAfter > logBefore, $"Git log must grow after sweep (before: {logBefore}, after: {logAfter})");
        Assert.Equal(1, logAfter - logBefore);
    }

    [Fact]
    public async Task SweepOrphanedDocumentsAsync_DocumentEvaluationThrows_LogsWarningAndSweepsTheRest()
    {
        var graph = new KnowledgeGraph();
        await CreateDocAsync(graph, "progress-gOrphan");

        // A malformed document whose Id is null makes the prefix check throw. The sweep
        // must log it, skip it, and still process the remaining documents.
        InjectDocument(graph, "broken", new KnowledgeDocument
        {
            Id = null!,
            Title = "Broken",
            Topic = "progress",
            Type = DocumentType.Scratch,
            Status = DocumentStatus.Draft,
            FilePath = "knowledge/progress/broken.md",
        });

        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();
        var service = CreateService(graph, logger);

        var count = await service.SweepOrphanedDocumentsAsync([], [], TestContext.Current.CancellationToken);

        // The healthy orphan was still swept despite the broken sibling.
        Assert.Equal(1, count);
        Assert.Null(graph.GetDocument("progress-gOrphan"));
        Assert.Contains(logger.LogEntries,
            e => e.LogLevel == LogLevel.Warning &&
                 e.Message.Contains("Failed to evaluate knowledge document", StringComparison.Ordinal) &&
                 e.Exception is not null);
    }

    /// <summary>Injects a document directly into the graph's private store, bypassing validation.</summary>
    private static void InjectDocument(KnowledgeGraph graph, string key, KnowledgeDocument document)
    {
        var field = typeof(KnowledgeGraph).GetField(
            "_documents",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var documents = field!.GetValue(graph) as Dictionary<string, KnowledgeDocument>;
        Assert.NotNull(documents);
        documents![key] = document;
    }

    // ── 11. Sweep zero ───────────────────────────────────────────────────────

    [Fact]
    public async Task SweepOrphanedDocumentsAsync_NoCandidates_ReturnsZero()
    {
        var graph = new KnowledgeGraph();
        // Only a live, unreleased progress doc — not a candidate.
        await CreateDocAsync(graph, "progress-gA");

        var goals = new List<Goal> { new() { Id = "gA", Description = "Live" } };
        var service = CreateService(graph);

        var count = await service.SweepOrphanedDocumentsAsync(goals, [], TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
        Assert.NotNull(graph.GetDocument("progress-gA"));
    }

    [Fact]
    public async Task SweepOrphanedDocumentsAsync_NullGraph_ReturnsZero()
    {
        var service = CreateService(null);

        var count = await service.SweepOrphanedDocumentsAsync([], [], TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task SweepOrphanedDocumentsAsync_PersistFails_ReturnsDeletedCountAndLogsWarning()
    {
        // A ConfigRepoManager whose local path is not a git repository makes every
        // git operation fail, so DeleteDocumentsAndCommitAsync reports Persisted=false.
        var brokenPath = Path.Combine(_tempDir, $"broken-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(brokenPath);
        var manager = new ConfigRepoManager("https://example.com/broken.git", brokenPath);
        var graph = new KnowledgeGraph(manager);
        // (c) review for orphaned goal → DELETED
        await CreateDocAsync(graph, "review-gX");
        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();
        var service = CreateService(graph, logger);

        var count = await service.SweepOrphanedDocumentsAsync(
            [], [], TestContext.Current.CancellationToken);

        Assert.True(count > 0, "Orphaned docs should be removed from the in-memory graph even when persist fails");
        Assert.Null(graph.GetDocument("review-gX"));
        Assert.Contains(logger.LogEntries,
            e => e.LogLevel == LogLevel.Warning &&
                 e.Message.Contains("Failed to persist startup sweep", StringComparison.Ordinal));
    }

    // ── 12. ExecuteStartupSweepAsync (startup wiring) ────────────────────────

    /// <summary>Builds a graph seeded with the standard stale/live document mix.</summary>
    private static async Task<KnowledgeGraph> CreateSeededGraphAsync()
    {
        var graph = new KnowledgeGraph();
        await CreateDocAsync(graph, "progress-gX"); // orphaned → deleted
        await CreateDocAsync(graph, "review-gY");   // orphaned → deleted
        await CreateDocAsync(graph, "progress-gZ"); // released → deleted
        await CreateDocAsync(graph, "progress-gA"); // live, unreleased → kept
        return graph;
    }

    [Fact]
    public async Task ExecuteStartupSweepAsync_ServicesRegistered_SweepsAndReturnsDeletedCount()
    {
        var graph = await CreateSeededGraphAsync();
        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetAllGoalsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Goal>)
            [
                new Goal { Id = "gA", Description = "Live unreleased" },
                new Goal { Id = "gZ", Description = "In released release", ReleaseId = "r1" },
            ]);
        goalStore.Setup(s => s.GetReleasesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Release>)
            [
                new Release { Id = "r1", Tag = "v1.0.0", Status = ReleaseStatus.Released },
            ]);

        var services = new ServiceCollection();
        services.AddSingleton(CreateService(graph));
        services.AddSingleton(goalStore.Object);
        using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();

        var deleted = await KnowledgeDocumentCleanupService.ExecuteStartupSweepAsync(
            provider, logger, TestContext.Current.CancellationToken);

        Assert.Equal(3, deleted);
        Assert.Null(graph.GetDocument("progress-gX"));
        Assert.Null(graph.GetDocument("review-gY"));
        Assert.Null(graph.GetDocument("progress-gZ"));
        Assert.NotNull(graph.GetDocument("progress-gA"));
        Assert.Contains(logger.LogEntries,
            e => e.LogLevel == LogLevel.Information &&
                 e.Message.Contains("Startup sweep removed 3 stale knowledge documents", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteStartupSweepAsync_CleanupServiceNotRegistered_ReturnsZeroWithoutTouchingGoalStore()
    {
        var goalStore = new Mock<IGoalStore>(MockBehavior.Strict);

        var services = new ServiceCollection();
        services.AddSingleton(goalStore.Object);
        using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();

        var deleted = await KnowledgeDocumentCleanupService.ExecuteStartupSweepAsync(
            provider, logger, TestContext.Current.CancellationToken);

        Assert.Equal(0, deleted);
        goalStore.Verify(s => s.GetAllGoalsAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.DoesNotContain(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteStartupSweepAsync_GoalStoreNotRegistered_ReturnsZeroAndKeepsDocuments()
    {
        var graph = await CreateSeededGraphAsync();

        var services = new ServiceCollection();
        services.AddSingleton(CreateService(graph));
        using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();

        var deleted = await KnowledgeDocumentCleanupService.ExecuteStartupSweepAsync(
            provider, logger, TestContext.Current.CancellationToken);

        Assert.Equal(0, deleted);
        // Nothing was swept — the orphaned documents survive.
        Assert.NotNull(graph.GetDocument("progress-gX"));
        Assert.NotNull(graph.GetDocument("progress-gA"));
        Assert.DoesNotContain(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteStartupSweepAsync_NoServicesRegistered_ReturnsZero()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();

        var deleted = await KnowledgeDocumentCleanupService.ExecuteStartupSweepAsync(
            provider, logger, TestContext.Current.CancellationToken);

        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task ExecuteStartupSweepAsync_GoalStoreThrows_LogsWarningAndReturnsZero()
    {
        var graph = await CreateSeededGraphAsync();
        var goalStore = new Mock<IGoalStore>();
        goalStore.Setup(s => s.GetAllGoalsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var services = new ServiceCollection();
        services.AddSingleton(CreateService(graph));
        services.AddSingleton(goalStore.Object);
        using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger<KnowledgeDocumentCleanupService>();

        // Must not rethrow — a failed sweep never blocks startup.
        var deleted = await KnowledgeDocumentCleanupService.ExecuteStartupSweepAsync(
            provider, logger, TestContext.Current.CancellationToken);

        Assert.Equal(0, deleted);
        Assert.Contains(logger.LogEntries,
            e => e.LogLevel == LogLevel.Warning &&
                 e.Message.Contains("Startup sweep of stale knowledge documents failed", StringComparison.Ordinal) &&
                 e.Exception is InvalidOperationException);
        // The documents are untouched because the sweep never ran.
        Assert.NotNull(graph.GetDocument("progress-gX"));
    }

    [Fact]
    public async Task ExecuteStartupSweepAsync_NullServices_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => KnowledgeDocumentCleanupService.ExecuteStartupSweepAsync(
                null!, NullLogger.Instance, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteStartupSweepAsync_NullLogger_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => KnowledgeDocumentCleanupService.ExecuteStartupSweepAsync(
                provider, null!, TestContext.Current.CancellationToken));
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    /// <summary>Captures log entries for verification.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel LogLevel, string Message, Exception? Exception)> LogEntries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LogEntries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    /// <summary>An IEnumerable that throws a non-cancellation exception when enumerated.</summary>
    private sealed class ThrowingGoalIdsEnumerable : IEnumerable<string>
    {
        public bool GetEnumeratorCalled { get; private set; }

        public IEnumerator<string> GetEnumerator()
        {
            GetEnumeratorCalled = true;
            throw new InvalidOperationException("simulated enumeration failure");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>An IEnumerable that throws an OCE when enumerated — must propagate, not be swallowed.</summary>
    private sealed class ThrowingOceGoalIdsEnumerable : IEnumerable<string>
    {
        public bool GetEnumeratorCalled { get; private set; }

        public IEnumerator<string> GetEnumerator()
        {
            GetEnumeratorCalled = true;
            throw new OperationCanceledException("simulated cancellation during enumeration");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// A ConfigRepoManager whose first delete call fails, then succeeds.
    /// Both the single-file and the batch delete paths share the same counter so the
    /// double behaves identically regardless of how many paths the graph persists.
    /// </summary>
    private sealed class FlakyDeleteConfigRepoManager : ConfigRepoManager
    {
        private int _deleteCalls;

        public FlakyDeleteConfigRepoManager(string localPath) : base("https://example.com/flaky.git", localPath) { }

        public override Task DeleteFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
            => FailFirstCall();

        public override Task DeleteFilesAsync(IReadOnlyList<string> filePaths, string commitMessage, CancellationToken ct = default)
            => FailFirstCall();

        private Task FailFirstCall()
        {
            _deleteCalls++;
            if (_deleteCalls == 1)
                throw new InvalidOperationException("simulated persist failure");
            return Task.CompletedTask;
        }
    }
}
