using System.Reflection;

using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// THE REAL HANDOFF, worker-side: <see cref="TaskExecutor"/> receives a reviewer
/// <see cref="WorkTask"/> whose metadata carries the tester's FULL report and must forward it
/// verbatim into the agent runner so the model-facing <c>get_test_report</c> tool returns the
/// complete text.
/// <para>
/// These are the WORKER-LOCAL vectors (clearing, role gating, blank handling). The single
/// CONTINUOUS end-to-end proof — real dispatch → real completion → captured reviewer task →
/// GrpcMapper round-trip → this executor/runner → the actual tool result — lives in
/// <c>TaskDispatchServiceTests.TesterReport_FullChain_ReachesGetTestReportToolIntact</c>, which
/// reuses <see cref="TesterReportRunner"/> and <see cref="NoOpTesterReportGit"/> from this file.
/// </para>
/// <para>
/// The runner under test is a REAL <see cref="SharpCoderRunner"/> wrapped in a small test-only
/// <see cref="IAgentRunner"/> adapter: <see cref="TaskExecutor"/>'s <c>SetTesterReport</c> calls
/// are forwarded to the real runner, and the adapter's <c>SendPromptAsync</c> invokes the actual
/// <c>get_test_report</c> <see cref="AIFunction"/> built by the real runner's private
/// <c>BuildCustomTools</c> (same reflection practice as
/// <see cref="SharpCoderRunnerToolsTests"/>). No live client, no tool bridge, no LLM.
/// </para>
/// </summary>
public sealed class TaskExecutorTesterReportTests
{
    internal const string TrailingEvidence = "TRAILING-EVIDENCE-AT-END: all 636 tests green.";
    internal const string Beyond4000Marker = "MUTATION-KILL-EVIDENCE-BEYOND-4000";

    /// <summary>The exact text the real tool returns when no tester report is set.</summary>
    internal const string UnavailableResponse =
        "No test report available — the testing phase was not part of this iteration's plan, or no results were recorded.";

    // ── Full report survives TaskExecutor → runner → get_test_report ─────

    /// <summary>
    /// The report reaches the model through the tool invocation that happens DURING the run
    /// (recorded by the adapter inside <c>SendPromptAsync</c>), and the executor run itself
    /// succeeded — so an executor that swallowed an exception before the prompt cannot pass.
    /// </summary>
    [Fact]
    public async Task TaskExecutor_ReviewerWithMetadata_ForwardsFullReportToGetTestReportTool()
    {
        var report = BuildRealisticReport();

        await using var runner = new TesterReportRunner();
        runner.WireRole(WorkerRole.Reviewer);
        var executor = new TaskExecutor(runner, gitOperations: new NoOpTesterReportGit());

        var task = MakeReviewerTask(report);
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Completed, result.Status);

        // The IN-FLIGHT invocation — what the model actually saw during the run.
        var observed = Assert.Single(runner.ObservedGetTestReportResults);
        Assert.Equal(report, observed);
        Assert.Contains(Beyond4000Marker, observed);
        Assert.EndsWith(TrailingEvidence, observed);
    }

    // ── Reused runner: stale report cleared when next reviewer has none ──

    /// <summary>
    /// Exercise reviewer-with-report followed by reviewer-without-report on a REUSED runner:
    /// the second task must observe the stale text CLEARED — the model must not read a previous
    /// iteration's report.
    /// <para>
    /// THE ROLE IS WIRED EXACTLY ONCE, before any assignment. Re-wiring between assignments
    /// would call the real <c>SetCustomAgent</c>, which clears the tester report field on its
    /// own and would make the clearing assertion pass for the wrong reason.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TaskExecutor_ReviewerWithoutReport_AfterReviewerWithReport_ClearsStaleText()
    {
        var report = BuildRealisticReport();

        await using var runner = new TesterReportRunner();
        runner.WireRole(WorkerRole.Reviewer);
        var executor = new TaskExecutor(runner, gitOperations: new NoOpTesterReportGit());

        var first = await executor.ExecuteAsync(MakeReviewerTask(report), TestContext.Current.CancellationToken);
        Assert.Equal(TaskOutcome.Completed, first.Status);
        Assert.Equal(report, Assert.Single(runner.ObservedGetTestReportResults));

        // Second reviewer task with NO tester_report metadata — no role re-wiring in between,
        // so ONLY TaskExecutor's own SetTesterReport(null) can clear the field.
        var second = await executor.ExecuteAsync(MakeReviewerTask(null), TestContext.Current.CancellationToken);
        Assert.Equal(TaskOutcome.Completed, second.Status);
        Assert.Equal(2, runner.ObservedGetTestReportResults.Count);
        Assert.Equal(UnavailableResponse, runner.ObservedGetTestReportResults[1]);
    }

    // ── Non-reviewer role: metadata present but stale text still cleared ──

    /// <summary>
    /// A CODER task carrying tester_report metadata must NOT receive the report
    /// (<see cref="TaskExecutor"/> gates forwarding on the reviewer role), and the runner's
    /// stale text from the previous reviewer assignment must be cleared by the executor.
    /// </summary>
    /// <remarks>
    /// WHY THE ROLE IS NEVER RE-WIRED HERE. An earlier revision called
    /// <c>SetRole(Coder)</c> before the coder assignment and <c>SetRole(Reviewer)</c> before the
    /// observation. Both route into the real <c>SharpCoderRunner.SetCustomAgent</c>, which nulls
    /// the tester report field itself — so the test passed even if TaskExecutor's clearing were
    /// deleted, or if it wrongly forwarded the coder's metadata. The role is now wired exactly
    /// ONCE (reviewer) and never touched again, so the ONLY thing that can null the field
    /// between the two assertions is the production code under test.
    /// <para>
    /// This kills BOTH mutants:
    /// (a) delete <c>agentRunner.SetTesterReport(null)</c> from TaskExecutor → the prior
    ///     report survives into the coder assignment and the final observation returns it;
    /// (b) widen the reviewer-only gate so non-reviewer metadata is forwarded → the field is
    ///     re-populated with the coder task's metadata and the final observation returns it.
    /// Either way the "unavailable" assertion fails.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TaskExecutor_NonReviewerWithMetadata_ClearsStaleTextAndReportsUnavailable()
    {
        var report = BuildRealisticReport();

        await using var runner = new TesterReportRunner();
        runner.WireRole(WorkerRole.Reviewer); // wired ONCE — never re-wired below
        var executor = new TaskExecutor(runner, gitOperations: new NoOpTesterReportGit());

        // (1) PROVE THE REPORT IS PRESENT: a real reviewer assignment populates the field.
        var reviewerResult = await executor.ExecuteAsync(
            MakeReviewerTask(report), TestContext.Current.CancellationToken);
        Assert.Equal(TaskOutcome.Completed, reviewerResult.Status);
        Assert.Equal(report, Assert.Single(runner.ObservedGetTestReportResults));
        Assert.Equal(report, runner.PeekTesterReportField());

        // (2) THE NON-REVIEWER ASSIGNMENT, carrying tester_report metadata anyway.
        var coderResult = await executor.ExecuteAsync(
            MakeTask(WorkerRole.Coder, report), TestContext.Current.CancellationToken);
        Assert.Equal(TaskOutcome.Completed, coderResult.Status);

        // (3) THE OBSERVATION, taken WITHOUT clearing anything: the runner's own field is null
        // and the real tool (still built — the runner is still wired for the reviewer role)
        // returns the unavailable response rather than either report.
        Assert.Null(runner.PeekTesterReportField());
        var observed = await runner.InvokeGetTestReportAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UnavailableResponse, observed);
        Assert.DoesNotContain(Beyond4000Marker, observed);
    }

    // ── Empty/whitespace report keeps the unavailable-tool response ──────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TaskExecutor_ReviewerWithEmptyOrWhitespaceReport_RetainsUnavailableResponse(string report)
    {
        await using var runner = new TesterReportRunner();
        runner.WireRole(WorkerRole.Reviewer);
        var executor = new TaskExecutor(runner, gitOperations: new NoOpTesterReportGit());

        var result = await executor.ExecuteAsync(MakeReviewerTask(report), TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal(UnavailableResponse, Assert.Single(runner.ObservedGetTestReportResults));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// A synthetic ~8KB tester report with distinctive evidence beyond character 4,000 and at
    /// the very end — the shape the 4,000-char truncation used to clip.
    /// </summary>
    internal static string BuildRealisticReport()
    {
        var report = """
            ## Test Report — iteration 1
            Build: success. Tests: 636 passed, 0 failed. Coverage: 78%.

            """;
        while (report.Length < 4_100)
            report += "Filler analysis line with stable content for realistic report shape.\n";
        report += Beyond4000Marker + ": mutant truncation removed → suite red.\n";
        while (report.Length < 7_800)
            report += "Further section detail — metrics table rows and issue enumeration.\n";
        report += TrailingEvidence;
        return report;
    }

    /// <summary>
    /// A reviewer <see cref="WorkTask"/> with (or without) tester_report metadata, ready for
    /// <see cref="TaskExecutor.ExecuteAsync"/> with the NoOp git transport.
    /// </summary>
    private static WorkTask MakeReviewerTask(string? testerReport) =>
        MakeTask(WorkerRole.Reviewer, testerReport);

    /// <summary>
    /// A <see cref="WorkTask"/> for the given role with (or without) tester_report metadata,
    /// ready for <see cref="TaskExecutor.ExecuteAsync"/> with the NoOp git transport.
    /// </summary>
    private static WorkTask MakeTask(WorkerRole role, string? testerReport)
    {
        var task = new WorkTask
        {
            TaskId = $"task-{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
            GoalId = "goal-tester-report",
            GoalDescription = "Test goal",
            Prompt = "Work on the task",
            Role = role,
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://example.com/test-repo.git", DefaultBranch = "main" }],
            BranchInfo = new BranchSpec { Action = BranchAction.Unspecified, BaseBranch = "main", FeatureBranch = "feature/test-branch" },
        };
        if (testerReport is not null)
            task.Metadata["tester_report"] = testerReport;
        return task;
    }
}

// ── The test-only IAgentRunner adapter around a real SharpCoderRunner ────────

/// <summary>
/// Wraps a REAL <see cref="SharpCoderRunner"/> and forwards <see cref="IAgentRunner"/>'s
/// report-relevant calls to it. <see cref="SendPromptAsync"/> does NOT run the SharpCoder agent
/// loop — it invokes the REAL <c>get_test_report</c> <see cref="AIFunction"/> built by the real
/// runner's private <c>BuildCustomTools</c> and RECORDS the result in
/// <see cref="ObservedGetTestReportResults"/>, so a test can assert on what the model actually
/// saw DURING the run rather than on state inspected afterwards. If the tool is missing, an
/// <see cref="InvalidOperationException"/> names it, so a broken forwarding chain fails loudly
/// instead of silently returning nothing.
/// </summary>
/// <remarks>
/// ORDERING: <see cref="WireRole"/> routes into the real <c>SetCustomAgent</c>, which CLEARS the
/// tester report field. It must therefore run BEFORE any assignment injects a report — the same
/// order production uses (WorkerService applies role updates; TaskExecutor then injects the
/// report per assignment). Tests that assert clearing must wire the role exactly ONCE so the
/// production code is the only thing that can null the field.
/// </remarks>
internal sealed class TesterReportRunner : IAgentRunner
{
    private readonly SharpCoderRunner _inner = new();

    /// <summary>
    /// Every <c>get_test_report</c> result observed inside <see cref="SendPromptAsync"/>, in
    /// invocation order. An empty list proves the prompt was never reached.
    /// </summary>
    public List<string> ObservedGetTestReportResults { get; } = [];

    /// <summary>Every value <see cref="TaskExecutor"/> passed to <c>SetTesterReport</c>, in order.</summary>
    public List<string?> TesterReportAssignments { get; } = [];

    /// <summary>
    /// Wires the inner runner for <paramref name="role"/> (mirrors a WorkerService role update
    /// arriving BEFORE the assignment runs). Calls the real <c>SetCustomAgent</c>, which clears
    /// the tester report field — hence "before report injection", never between assignments.
    /// </summary>
    public void WireRole(WorkerRole role) => _inner.SetCustomAgent(role, "test agents.md");

    public TestResultReport? LastTestReport => _inner.LastTestReport;
    public WorkerReport? LastWorkerReport => _inner.LastWorkerReport;

    public void ClearTestReport() => _inner.ClearTestReport();
    public void ClearWorkerReport() => _inner.ClearWorkerReport();
    public void SetToolBridge(IToolCallBridge? bridge) => _inner.SetToolBridge(bridge);
    public void SetCurrentTaskId(string? taskId) => _inner.SetCurrentTaskId(taskId);
    public void SetCurrentGoalId(string? goalId) => _inner.SetCurrentGoalId(goalId);

    public void SetTesterReport(string? report)
    {
        TesterReportAssignments.Add(report);
        _inner.SetTesterReport(report);
    }

    public void SetCustomAgent(WorkerRole role, string agentsMdContent) =>
        _inner.SetCustomAgent(role, agentsMdContent);

    public void SetSession(object? session) => _inner.SetSession(session);
    public object? GetSession() => _inner.GetSession();
    public int GetContextUsagePercent() => _inner.GetContextUsagePercent();
    public void SetMaxContextTokens(int maxTokens) => _inner.SetMaxContextTokens(maxTokens);
    public void SetCompactionModel(string? model) => _inner.SetCompactionModel(model);
    public void SetCompactionMaxTokens(int? maxTokens) => _inner.SetCompactionMaxTokens(maxTokens);
    public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) => _inner.SetSubAgentModels(models);
    public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) => _inner.SetConfigProvisioner(provisioner);
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Stands in for the agent turn: invokes the REAL <c>get_test_report</c> tool exactly as the
    /// model would and records what it returned. The tool's text is returned as the "response".
    /// </summary>
    public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
    {
        var result = InvokeGetTestReportTool(ct);
        ObservedGetTestReportResults.Add(result);
        return Task.FromResult(result);
    }

    /// <summary>
    /// Invokes the real get_test_report tool outside a run. This is a pure READ — it never
    /// mutates the runner's tester report field.
    /// </summary>
    public Task<string> InvokeGetTestReportAsync(CancellationToken ct) =>
        Task.FromResult(InvokeGetTestReportTool(ct));

    /// <summary>
    /// Reads the real runner's private tester report field directly. A pure observation: unlike
    /// <c>SetCustomAgent</c>, it cannot clear the value it is inspecting.
    /// </summary>
    public string? PeekTesterReportField()
    {
        var field = typeof(SharpCoderRunner).GetField("_testerReport", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "SharpCoderRunner._testerReport not found — the tester report field was renamed or removed.");
        return (string?)field.GetValue(_inner);
    }

    private string InvokeGetTestReportTool(CancellationToken ct)
    {
        var method = typeof(SharpCoderRunner).GetMethod(
            "BuildCustomTools", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tools = (IList<AITool>)method.Invoke(_inner, [ct])!;

        var tool = tools.FirstOrDefault(t => t.Name == "get_test_report")
            ?? throw new InvalidOperationException(
                "get_test_report tool missing from real SharpCoderRunner.BuildCustomTools — " +
                "TaskExecutor no longer forwards the tester report or the role wiring changed.");

        var function = (AIFunction)tool;
        return function.InvokeAsync([], ct).GetAwaiter().GetResult()?.ToString() ?? string.Empty;
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>Minimal <see cref="IGitOperations"/> that does nothing — no network, no real repo.</summary>
internal sealed class NoOpTesterReportGit : IGitOperations
{
    public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct) => Task.CompletedTask;
    public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
    public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct) => Task.CompletedTask;
    public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
    public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
        => Task.FromResult(new GitChangeSummary { FilesChanged = 1 });
    public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct) => Task.FromResult(false);
    public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct)
        => Task.FromResult<string?>("abc123def456789012345678");
    public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(string workDir, string args, CancellationToken ct)
        => Task.FromResult((0, "", ""));
    public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5) => Task.CompletedTask;
}
