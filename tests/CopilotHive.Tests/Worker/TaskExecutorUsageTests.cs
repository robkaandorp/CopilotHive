using CopilotHive.Services;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using SharpCoder;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Pins <see cref="TaskExecutor"/>'s per-task usage boundary: the executor starts a fresh
/// accumulation (via <see cref="IAgentRunner.ResetTaskUsage"/>) BEFORE any prompt turn of the task,
/// and stamps the resulting snapshot onto EVERY terminal result at its ONE shared return boundary —
/// so Completed and Failed outcomes carry the same usage.
/// <para>
/// The recording double below overrides both usage members, which is exactly what the default
/// interface implementations exist for: the <see cref="DefaultImplementationRunner"/> in this file
/// keeps them and is the positive control proving that a runner which does NOT report usage yields
/// <c>Usage == null</c> ("not reported"), never an empty summary.
/// </para>
/// <para>
/// Deterministic throughout: the doubles complete synchronously and the ordering assertions are
/// made on recorded call sequences, never on wall-clock timing.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class TaskExecutorUsageTests
{
    // ── Recording test doubles ────────────────────────────────────────────────

    /// <summary>
    /// Shared plumbing for the agent-runner doubles in this fixture: no-op everything except the
    /// report fields, the prompt call and the usage members the specific double overrides.
    /// <para>
    /// It deliberately does NOT implement <see cref="IAgentRunner"/>: each concrete double lists
    /// the interface itself. A derived class that merely declares a member does NOT re-implement an
    /// interface member its base bound — the base's binding (here: the interface DEFAULT) would keep
    /// being used through the interface, silently ignoring the override.
    /// </para>
    /// </summary>
    private abstract class StubAgentRunnerBase
    {
        public TestResultReport? LastTestReport { get; protected set; }
        public WorkerReport? LastWorkerReport { get; protected set; }

        /// <summary>Every prompt this runner was asked to send, in order.</summary>
        public List<string> Prompts { get; } = [];

        public void ClearTestReport() => LastTestReport = null;
        public void ClearWorkerReport() => LastWorkerReport = null;
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(WorkerRole role, string agentsMdContent) { }
        public void SetMaxContextTokens(int maxTokens) { }
        public void SetCompactionModel(string? model) { }
        public void SetCompactionMaxTokens(int? maxTokens) { }
        public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) { }
        public void SetSession(object? session) { }
        public object? GetSession() => null;
        public int GetContextUsagePercent() => 0;
        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) { }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
            => Task.CompletedTask;

        public virtual Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult("Mock agent response");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A runner that REPORTS usage and records the ORDER of its usage boundary against its prompt
    /// calls, which is what pins "reset happens before the first prompt".
    /// </summary>
    private sealed class RecordingUsageRunner : StubAgentRunnerBase, IAgentRunner
    {
        /// <summary>One entry per usage-boundary or prompt event, in the order they happened.</summary>
        public List<string> Events { get; } = [];

        /// <summary>Number of <see cref="ResetTaskUsage"/> calls.</summary>
        public int ResetCount { get; private set; }

        /// <summary>Number of <see cref="GetTaskUsage"/> calls.</summary>
        public int GetUsageCount { get; private set; }

        /// <summary>The usage this runner reports for the current task; non-null by design here.</summary>
        public UsageSummary ReportedUsage { get; } = new(
        [
            new UsageEntry(UsageSource.Agent, "model-a", new TokenUsage
            {
                InputTokens = 4_321,
                OutputTokens = 321,
                CachedInputTokens = 21,
                ReasoningTokens = 3,
                Calls = 2,
                CachedInputReportedCalls = 2,
                ReasoningReportedCalls = 2,
            }),
        ]);

        public void ResetTaskUsage()
        {
            ResetCount++;
            Events.Add("reset");
        }

        public UsageSummary? GetTaskUsage()
        {
            GetUsageCount++;
            Events.Add("get");
            return ReportedUsage;
        }

        public override Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            Events.Add($"prompt:{prompt}");
            return base.SendPromptAsync(prompt, workDir, ct);
        }
    }

    /// <summary>
    /// A runner that reports usage for the FIRST task and then a DIFFERENT value, so a result can be
    /// tied to the snapshot taken for its own task rather than to any value of the right shape.
    /// </summary>
    private sealed class RotatingUsageRunner : StubAgentRunnerBase, IAgentRunner
    {
        private int _taskIndex = -1;

        /// <summary>One distinct summary per <see cref="ResetTaskUsage"/> call.</summary>
        public List<UsageSummary> HandedOut { get; } = [];

        public void ResetTaskUsage() => _taskIndex++;

        public UsageSummary? GetTaskUsage()
        {
            var usage = new UsageSummary(
            [
                new UsageEntry(UsageSource.Agent, $"model-{_taskIndex}", new TokenUsage
                {
                    InputTokens = 1_000 * (_taskIndex + 1),
                    OutputTokens = 10 * (_taskIndex + 1),
                    Calls = 1,
                    CachedInputReportedCalls = 1,
                    ReasoningReportedCalls = 1,
                }),
            ]);
            HandedOut.Add(usage);
            return usage;
        }
    }

    /// <summary>
    /// A runner that keeps the DEFAULT <see cref="IAgentRunner"/> usage implementations — the
    /// deliberate no-op / null pair a legacy runner or an untouched test double has.
    /// </summary>
    private sealed class DefaultImplementationRunner : StubAgentRunnerBase, IAgentRunner
    {
    }

    /// <summary>A runner whose prompts throw, so the executor takes its Failed path.</summary>
    private sealed class ThrowingUsageRunner : StubAgentRunnerBase, IAgentRunner
    {
        /// <summary>The usage reported for the failing task.</summary>
        public UsageSummary ReportedUsage { get; } = new(
        [
            new UsageEntry(UsageSource.Agent, "model-failing", new TokenUsage
            {
                InputTokens = 777,
                OutputTokens = 77,
                Calls = 1,
                CachedInputReportedCalls = 1,
                ReasoningReportedCalls = 1,
            }),
        ]);

        public void ResetTaskUsage() { }

        public UsageSummary? GetTaskUsage() => ReportedUsage;

        public override Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
            => throw new InvalidOperationException("agent failure for the usage fixture");
    }

    /// <summary>
    /// A runner that reports usage and then CANCELS the token source the caller handed to the
    /// executor, so the executor takes its requested-cancellation path (Cancelled/CANCELLED) — the
    /// third terminal outcome that shares the same return boundary.
    /// </summary>
    private sealed class CancellingUsageRunner(CancellationTokenSource toCancel) : StubAgentRunnerBase, IAgentRunner
    {
        /// <summary>The usage reported for the cancelled task.</summary>
        public UsageSummary ReportedUsage { get; } = new(
        [
            new UsageEntry(UsageSource.Agent, "model-cancelled", new TokenUsage
            {
                InputTokens = 555,
                OutputTokens = 55,
                Calls = 1,
                CachedInputReportedCalls = 1,
                ReasoningReportedCalls = 1,
            }),
        ]);

        public void ResetTaskUsage() { }

        public UsageSummary? GetTaskUsage() => ReportedUsage;

        public override Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            Prompts.Add(prompt);

            // A REAL requested cancellation: the executor's `ct.IsCancellationRequested` guard — not
            // a timeout heuristic — is what classifies this as Cancelled/CANCELLED.
            toCancel.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("unreachable");
        }
    }

    // ── Fakes / helpers ───────────────────────────────────────────────────────

    /// <summary>Minimal git transport: a clean worktree, no merge base, nothing pushed.</summary>
    private sealed class CleanGitOperations : IGitOperations
    {
        public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct) => Task.CompletedTask;
        public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
        public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct)
            => Task.CompletedTask;
        public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
        public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
            => Task.FromResult(new GitChangeSummary());
        public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct) => Task.FromResult(false);
        public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct)
            => Task.FromResult<string?>(null);
        public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(
            string workDir, string args, CancellationToken ct)
            => Task.FromResult((0, "", ""));
        public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5) => Task.CompletedTask;
    }

    /// <summary>
    /// An assignment with NO repositories: the executor then uses its configurable work root
    /// (<c>WORKER_WORK_ROOT</c>, pointed at a real directory for the whole test assembly), so the run
    /// reaches the prompt without any clone, branch or config-repo operation.
    /// </summary>
    private static WorkTask MakeTask(string taskId) => new()
    {
        TaskId = taskId,
        GoalId = "goal-usage-transport",
        GoalDescription = "Usage transport",
        Prompt = "Do the work",
        Role = WorkerRole.Coder,
        Repositories = [],
    };

    private static TaskExecutor MakeExecutor(IAgentRunner runner) =>
        new(runner, gitOperations: new CleanGitOperations());

    // ── (a) Completed result carries the runner's usage ───────────────────────

    /// <summary>
    /// A COMPLETED result carries the runner's usage snapshot — the same entries, counts,
    /// reported-calls counters and the source, taken from the runner that ran the task.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CompletedResult_CarriesRunnerUsage()
    {
        var runner = new RecordingUsageRunner();

        var result = await MakeExecutor(runner).ExecuteAsync(
            MakeTask("task-usage-completed"), TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Completed, result.Status);

        Assert.NotNull(result.Usage);
        Assert.Same(runner.ReportedUsage, result.Usage);
        var entry = Assert.Single(result.Usage.Entries);
        Assert.Equal(UsageSource.Agent, entry.Source);
        Assert.Equal("model-a", entry.Model);
        Assert.Equal(4_321, entry.Usage.InputTokens);
        Assert.Equal(321, entry.Usage.OutputTokens);
        Assert.Equal(21, entry.Usage.CachedInputTokens);
        Assert.Equal(3, entry.Usage.ReasoningTokens);
        Assert.Equal(2, entry.Usage.Calls);
        Assert.Equal(2, entry.Usage.CachedInputReportedCalls);
        Assert.Equal(2, entry.Usage.ReasoningReportedCalls);
    }

    // ── (b) Failed result carries the runner's usage ──────────────────────────

    /// <summary>
    /// A FAILED result carries the runner's usage too: usage is stamped at the ONE shared return
    /// boundary, so it does not depend on the outcome. On a revision that stamped usage only on the
    /// Completed branch this assertion fails with a null usage.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_FailedResult_CarriesRunnerUsage()
    {
        var runner = new ThrowingUsageRunner();

        var result = await MakeExecutor(runner).ExecuteAsync(
            MakeTask("task-usage-failed"), TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Failed, result.Status);

        Assert.NotNull(result.Usage);
        Assert.Same(runner.ReportedUsage, result.Usage);
        var entry = Assert.Single(result.Usage.Entries);
        Assert.Equal(777, entry.Usage.InputTokens);
        Assert.Equal(77, entry.Usage.OutputTokens);
        Assert.Equal(1, entry.Usage.Calls);
    }

    // ── (c) Reset precedes the first prompt ──────────────────────────────────

    /// <summary>
    /// <see cref="IAgentRunner.ResetTaskUsage"/> runs BEFORE the task's first
    /// <see cref="IAgentRunner.SendPromptAsync"/>, so no call of the task can be recorded into an
    /// accumulation that is about to be discarded. The assertion is on the recorded ORDER of the
    /// runner's own events, so a reset that ran after (or instead of) the first prompt fails.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ResetsTaskUsageBeforeTheFirstPrompt()
    {
        var runner = new RecordingUsageRunner();

        await MakeExecutor(runner).ExecuteAsync(MakeTask("task-usage-order"), TestContext.Current.CancellationToken);

        Assert.Equal(1, runner.ResetCount);
        Assert.NotEmpty(runner.Prompts);

        var resetIndex = runner.Events.IndexOf("reset");
        var firstPromptIndex = runner.Events.FindIndex(e => e.StartsWith("prompt:", StringComparison.Ordinal));
        var getIndex = runner.Events.IndexOf("get");

        Assert.Equal(0, resetIndex);
        Assert.True(firstPromptIndex > resetIndex,
            $"The first prompt must come after the reset; observed order: {string.Join(", ", runner.Events)}");
        Assert.True(getIndex > firstPromptIndex,
            $"The usage snapshot must be taken after the prompt turns; observed order: {string.Join(", ", runner.Events)}");
    }

    /// <summary>
    /// The executor's own sequence is not a one-off: a SECOND task on the SAME runner resets again
    /// and reports that task's OWN snapshot, so the previous task's numbers cannot be re-reported.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TwoTasksOnOneRunner_EachResultCarriesItsOwnSnapshot()
    {
        var runner = new RotatingUsageRunner();
        var executor = MakeExecutor(runner);

        var first = await executor.ExecuteAsync(MakeTask("task-usage-first"), TestContext.Current.CancellationToken);
        var second = await executor.ExecuteAsync(MakeTask("task-usage-second"), TestContext.Current.CancellationToken);

        Assert.Equal(2, runner.HandedOut.Count);

        Assert.NotNull(first.Usage);
        Assert.NotNull(second.Usage);
        Assert.Same(runner.HandedOut[0], first.Usage);
        Assert.Same(runner.HandedOut[1], second.Usage);

        // Distinct, task-identifying values — never the same numbers reused across tasks.
        Assert.Equal(1_000, first.Usage.Total.InputTokens);
        Assert.Equal(2_000, second.Usage.Total.InputTokens);
        Assert.Equal("model-0", first.Usage.Entries[0].Model);
        Assert.Equal("model-1", second.Usage.Entries[0].Model);
    }

    /// <summary>
    /// A CANCELLED result carries the runner's usage too — the same ONE shared return boundary
    /// serves all three terminal outcomes. The cancellation is a REAL requested one (the runner
    /// cancels the caller's own token), so the executor's existing
    /// <c>ct.IsCancellationRequested</c> guard classifies it as Cancelled/CANCELLED rather than as
    /// a timeout failure, and no wall-clock deadline is involved anywhere.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CancelledResult_CarriesRunnerUsage()
    {
        using var cts = new CancellationTokenSource();
        var runner = new CancellingUsageRunner(cts);

        var result = await MakeExecutor(runner).ExecuteAsync(MakeTask("task-usage-cancelled"), cts.Token);

        Assert.Equal(TaskOutcome.Cancelled, result.Status);
        Assert.Equal("CANCELLED", result.Metrics!.Verdict);

        Assert.NotNull(result.Usage);
        Assert.Same(runner.ReportedUsage, result.Usage);
        var entry = Assert.Single(result.Usage.Entries);
        Assert.Equal(555, entry.Usage.InputTokens);
        Assert.Equal(55, entry.Usage.OutputTokens);
        Assert.Equal(1, entry.Usage.Calls);
    }

    // ── (d) Default implementation yields null ───────────────────────────────

    /// <summary>
    /// A runner that keeps the DEFAULT <see cref="IAgentRunner"/> usage members reports no usage, and
    /// the result must carry <c>Usage == null</c> — "not reported", which the wire mapping keeps
    /// ABSENT. An implementation that fabricated an empty summary here would be indistinguishable
    /// from a real "zero model calls" measurement.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DefaultImplementationRunner_YieldsNullUsage()
    {
        var runner = new DefaultImplementationRunner();

        var result = await MakeExecutor(runner).ExecuteAsync(
            MakeTask("task-usage-default"), TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Completed, result.Status);

        // Read through the INTERFACE: the default implementation is not a member of the double's
        // own type, which is exactly the shape a legacy runner/double has.
        IAgentRunner asInterface = runner;
        Assert.Null(asInterface.GetTaskUsage());
        Assert.Null(result.Usage);
    }
}
