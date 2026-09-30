using System.Reflection;

using CopilotHive.Services;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using SharpCoder;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// THE TRANSPORT SEAM, worker-side: <see cref="TaskExecutor"/> reads the completed-phase reports of
/// the current iteration out of the assignment's metadata through the shared
/// <see cref="PhaseReportMetadata"/> contract and hands them to the agent runner, for EVERY role —
/// and the runner REPLACES its stored reports, so a re-used runner never shows a previous
/// assignment's reports.
/// <para>
/// The runner under test is a REAL <see cref="SharpCoderRunner"/> wrapped in a small test-only
/// <see cref="IAgentRunner"/> adapter that forwards the phase-report call to it and records every
/// value handed over. There is no live client, no tool bridge and no LLM here: the adapter's
/// <c>SendPromptAsync</c> does nothing, and the assertions read either the recorded values or the
/// real runner's own field.
/// </para>
/// <para>
/// The USER-FACING half of this handoff (the <c>get_phase_report</c> tool reading those stored
/// reports) belongs to the next round, so its vectors are not written here.
/// </para>
/// </summary>
public sealed class TaskExecutorPhaseReportsTests
{
    /// <summary>
    /// The exact reports written onto the assignment's metadata reach the runner, verbatim — with a
    /// report longer than 4,000 characters whose evidence sits at the very END, so a truncating or
    /// trimming handoff cannot pass.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    [InlineData(WorkerRole.Reviewer)]
    [InlineData(WorkerRole.DocWriter)]
    [InlineData(WorkerRole.Improver)]
    public async Task TaskExecutor_AssignmentWithPhaseReports_ForwardsThemVerbatim(WorkerRole role)
    {
        using var configRepo = new PhaseReportConfigRepo();
        var coderReport = new string('C', 4_100) + "\nCODER-TAIL-MARKER: verbatim, never capped.";
        PhaseReport[] expected =
        [
            new("Coding", 1, coderReport),
            new("DocWriting", 1, "DOCWRITER-REPORT: docs/ARCHITECTURE.md updated."),
        ];

        await using var runner = new PhaseReportRunner();
        var executor = new TaskExecutor(
            runner, gitOperations: new PhaseReportGit(), configRepoDir: configRepo.Directory);

        var task = MakeTask(role);
        PhaseReportMetadata.Write(task.Metadata, expected);

        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Completed, result.Status);

        var handedOver = Assert.Single(runner.PhaseReportAssignments);
        Assert.NotNull(handedOver);
        Assert.Equal(expected, handedOver);
        Assert.EndsWith("CODER-TAIL-MARKER: verbatim, never capped.", handedOver[0].Report, StringComparison.Ordinal);
    }

    /// <summary>
    /// A RE-USED runner must not keep serving the previous assignment's reports: the second
    /// assignment, whose metadata carries none, leaves the real runner with NOTHING — the executor
    /// still makes the call, and the runner really empties out.
    /// </summary>
    /// <remarks>
    /// KILLS TWO MUTANTS: (a) skipping the call when there are no reports (the recorded list would
    /// have one entry); (b) leaving the stored reports untouched (the real runner's field would
    /// still hold the first assignment's reports).
    /// </remarks>
    [Fact]
    public async Task TaskExecutor_NextAssignmentWithoutReports_ClearsTheReusedRunnersReports()
    {
        var report = "FIRST-ASSIGNMENT-REPORT: must not survive into the next task.";
        PhaseReport[] first = [new PhaseReport("Coding", 1, report)];

        await using var runner = new PhaseReportRunner();
        var executor = new TaskExecutor(runner, gitOperations: new NoOpTesterReportGit());

        var firstTask = MakeTask(WorkerRole.Coder);
        PhaseReportMetadata.Write(firstTask.Metadata, first);
        var firstResult = await executor.ExecuteAsync(firstTask, TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Completed, firstResult.Status);
        Assert.Equal(first, Assert.Single(runner.PhaseReportAssignments));
        Assert.Equal(first, runner.PeekPhaseReportsField());

        // The second assignment carries NO phase-report metadata at all.
        var secondTask = MakeTask(WorkerRole.Tester);
        Assert.Empty(PhaseReportMetadata.Read(secondTask.Metadata));
        var secondResult = await executor.ExecuteAsync(secondTask, TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Completed, secondResult.Status);
        Assert.Equal(2, runner.PhaseReportAssignments.Count);
        Assert.Empty(runner.PhaseReportAssignments[1]!);
        Assert.Empty(runner.PeekPhaseReportsField());
    }

    /// <summary>
    /// Unrelated and malformed metadata is never turned into a report: the runner receives the
    /// well-formed entries only (and nothing at all when none are well-formed).
    /// </summary>
    [Fact]
    public async Task TaskExecutor_MalformedMetadata_ForwardsOnlyTheWellFormedReports()
    {
        await using var runner = new PhaseReportRunner();
        var executor = new TaskExecutor(runner, gitOperations: new NoOpTesterReportGit());

        var task = MakeTask(WorkerRole.Tester);
        task.Metadata["tester_report"] = "the legacy tester report — NOT a phase report";
        task.Metadata["phase_report:Testing:not-an-int"] = "malformed occurrence";
        task.Metadata["phase_report:Review:2"] = "review report";

        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal(
            [new PhaseReport("Review", 2, "review report")],
            Assert.Single(runner.PhaseReportAssignments));
    }

    /// <summary>
    /// The REAL runner's own store: setting reports REPLACES the previous value, including the
    /// empty case — a runner is never left showing reports it was told to drop.
    /// </summary>
    [Fact]
    public async Task SharpCoderRunner_SetPhaseReports_ReplacesThePreviousReports()
    {
        await using var runner = new SharpCoderRunner();

        runner.SetPhaseReports([new PhaseReport("Coding", 1, "coder report")]);
        Assert.Equal([new PhaseReport("Coding", 1, "coder report")], PeekPhaseReports(runner));

        runner.SetPhaseReports([new PhaseReport("Review", 1, "review report")]);
        Assert.Equal([new PhaseReport("Review", 1, "review report")], PeekPhaseReports(runner));

        runner.SetPhaseReports([]);
        Assert.Empty(PeekPhaseReports(runner));

        // A null argument means "no reports" — never "keep the previous ones".
        runner.SetPhaseReports([new PhaseReport("Testing", 1, "tester report")]);
        runner.SetPhaseReports(null);
        Assert.Empty(PeekPhaseReports(runner));
    }

    /// <summary>
    /// Starting a NEW connection drops the phase reports, exactly like the stale tester report: the
    /// next connection never inherits the previous one's reports.
    /// </summary>
    [Fact]
    public async Task SharpCoderRunner_ConnectAsync_ClearsThePhaseReports()
    {
        await using var runner = new SharpCoderRunner();
        runner.SetPhaseReports([new PhaseReport("Coding", 1, "coder report")]);
        Assert.NotEmpty(PeekPhaseReports(runner));

        await runner.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Empty(PeekPhaseReports(runner));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="WorkTask"/> for <paramref name="role"/> with NO repositories: the executor then
    /// uses its configurable work root (<c>WORKER_WORK_ROOT</c>, pointed at a real directory for the
    /// whole test assembly), so the run reaches the assignment setup without any clone or branch
    /// operation.
    /// </summary>
    private static WorkTask MakeTask(WorkerRole role) => new()
    {
        TaskId = $"task-{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
        GoalId = "goal-phase-reports",
        GoalDescription = "Phase reports goal",
        Prompt = "Work on the assigned task",
        Role = role,
        Repositories = [],
    };

    private static List<PhaseReport> PeekPhaseReports(SharpCoderRunner runner) =>
        PhaseReportRunner.PeekPhaseReportsFieldOf(runner);
}

/// <summary>
/// A fresh per-test config-repo directory (a <c>.git</c> marker plus an <c>agents</c> subfolder —
/// the shape the real worker mounts), removed again on dispose. Shared by the phase-report vectors
/// that drive the REAL executor, whose Improver path refuses to run against a directory without
/// one and whose CI environment has no <c>/config-repo</c>.
/// </summary>
internal sealed class PhaseReportConfigRepo : IDisposable
{
    /// <summary>The directory to pass as the executor's <c>configRepoDir</c>.</summary>
    public string Directory { get; } =
        Path.Combine(Path.GetTempPath(), $"copilothive-phase-report-config-{Guid.NewGuid():N}");

    public PhaseReportConfigRepo()
    {
        System.IO.Directory.CreateDirectory(Path.Combine(Directory, ".git"));
        System.IO.Directory.CreateDirectory(Path.Combine(Directory, "agents"));
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException) { /* best-effort cleanup of test scaffolding */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup of test scaffolding */ }
    }
}

/// <summary>
/// The git transport the phase-report vectors share: the no-op behaviour of
/// <see cref="NoOpTesterReportGit"/> (a clean worktree, a fixed merge base, no network) PLUS the two
/// things the vectors that drive the REAL executor need:
/// <list type="bullet">
///   <item><description>a clone really CREATES its target directory — the real <c>SharpCoder</c>
///     agent validates <c>AgentOptions.WorkDirectory</c> exists, so a no-op clone would leave the
///     prompt turn failing with <c>DirectoryNotFoundException</c>;</description></item>
///   <item><description>the Improver's pre-run baseline preparation gets EXPLICIT valid answers,
///     since a bare exit-zero with an empty stdout is never a usable baseline.</description></item>
/// </list>
/// </summary>
internal sealed class PhaseReportGit : IGitOperations
{
    private readonly NoOpTesterReportGit _inner = new();

    public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(targetDir);
        return Task.CompletedTask;
    }

    public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct) =>
        _inner.CheckoutBranchAsync(repoDir, branch, ct);

    public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct) =>
        _inner.CreateBranchAsync(repoDir, branchName, baseBranch, ct);

    public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct) =>
        _inner.PushBranchAsync(repoDir, branch, ct);

    public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct) =>
        _inner.GetGitStatusAsync(repoDir, baseBranch, ct);

    public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct) =>
        _inner.HasUncommittedChangesAsync(repoDir, ct);

    public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct) =>
        _inner.GetMergeBaseAsync(repoDir, baseBranch, ct);

    public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(
        string workDir, string args, CancellationToken ct) =>
        Task.FromResult(
            ConfigRepoPreparationFakes.LegacyAnswer(workDir, args)
            ?? (0, "", ""));

    public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5) =>
        _inner.ForceDeleteDirectoryAsync(path, maxRetries);
}

/// <summary>
/// Wraps a REAL <see cref="SharpCoderRunner"/> and forwards <see cref="SetPhaseReports"/> to it
/// while RECORDING every value handed over, so a test can assert both on what
/// <see cref="TaskExecutor"/> passed and on what the real runner ended up storing.
/// </summary>
internal sealed class PhaseReportRunner : IAgentRunner
{
    private readonly SharpCoderRunner _inner = new();

    /// <summary>Every value <see cref="TaskExecutor"/> passed to <c>SetPhaseReports</c>, in order.</summary>
    public List<IReadOnlyList<PhaseReport>?> PhaseReportAssignments { get; } = [];

    public void SetPhaseReports(IReadOnlyList<PhaseReport>? reports)
    {
        PhaseReportAssignments.Add(reports);
        _inner.SetPhaseReports(reports);
    }

    /// <summary>The reports the REAL runner stores right now.</summary>
    public List<PhaseReport> PeekPhaseReportsField() => PeekPhaseReportsFieldOf(_inner);

    /// <summary>
    /// Reads the real runner's private phase-report field directly. A pure observation: unlike
    /// <c>SetCustomAgent</c> or <c>ConnectAsync</c>, it cannot clear the value it is inspecting.
    /// </summary>
    public static List<PhaseReport> PeekPhaseReportsFieldOf(SharpCoderRunner runner)
    {
        var field = typeof(SharpCoderRunner)
            .GetField("_phaseReports", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "SharpCoderRunner._phaseReports not found — the phase-report field was renamed or removed.");
        return [.. (IReadOnlyList<PhaseReport>)field.GetValue(runner)!];
    }

    public TestResultReport? LastTestReport => _inner.LastTestReport;
    public WorkerReport? LastWorkerReport => _inner.LastWorkerReport;

    public void ClearTestReport() => _inner.ClearTestReport();
    public void ClearWorkerReport() => _inner.ClearWorkerReport();
    public void SetToolBridge(IToolCallBridge? bridge) => _inner.SetToolBridge(bridge);
    public void SetCurrentTaskId(string? taskId) => _inner.SetCurrentTaskId(taskId);
    public void SetCurrentGoalId(string? goalId) => _inner.SetCurrentGoalId(goalId);
    public void SetTesterReport(string? report) => _inner.SetTesterReport(report);
    public void SetCustomAgent(WorkerRole role, string agentsMdContent) =>
        _inner.SetCustomAgent(role, agentsMdContent);
    public void SetSession(object? session) => _inner.SetSession(session);
    public object? GetSession() => _inner.GetSession();
    public int GetContextUsagePercent() => _inner.GetContextUsagePercent();
    public void SetMaxContextTokens(int maxTokens) => _inner.SetMaxContextTokens(maxTokens);
    public void SetCompactionModel(string? model) => _inner.SetCompactionModel(model);
    public void SetCompactionMaxTokens(int? maxTokens) => _inner.SetCompactionMaxTokens(maxTokens);
    public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) => _inner.SetSubAgentModels(models);
    public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) =>
        _inner.SetConfigProvisioner(provisioner);
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Stands in for the agent turn: it deliberately runs NO agent loop and returns a fixed
    /// response, so the vector observes ONLY the assignment setup under test.
    /// </summary>
    public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct) =>
        Task.FromResult("Done.");

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
