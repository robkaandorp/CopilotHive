using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;

using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Tests that <see cref="PipelineDriver"/> merge behavior is target-repository-aware:
/// only <see cref="Goal.ResolveTargetRepositoryNames"/> targets are merged, hashes are
/// collected locally and assigned only when ALL targets succeed, and partial/empty
/// failures are retryable (never marking the goal failed directly).
/// </summary>
public sealed class TargetReposMergeTests
{
    // ── Single repo, null targets: merge called once (backward compat) ────

    [Fact]
    public async Task PerformMergeAsync_SingleRepoNullTargets_MergesOnceAndCompletes()
    {
        var goal = BuildGoal(["repo-a"], targets: null);
        var repoManager = new RecordingRepoManager(("repo-a", _ => "hash-a"));
        var (driver, pipeline) = CreateMergeDriver(goal, repoManager);

        await RunMergePhaseAsync(driver, pipeline);

        Assert.Equal(["repo-a"], repoManager.MergedRepos);
        Assert.Equal("hash-a", pipeline.MergeCommitHash);
        Assert.Equal(GoalPhase.Done, pipeline.Phase);
    }

    // ── Multi-repo, no targets: merge called for each; all succeed → complete ──

    [Fact]
    public async Task PerformMergeAsync_MultiRepoNullTargets_MergesAllAndCompletesWithCommaSeparatedHash()
    {
        var goal = BuildGoal(["repo-a", "repo-b"], targets: null);
        var repoManager = new RecordingRepoManager(
            ("repo-a", _ => "hash-a"),
            ("repo-b", _ => "hash-b"));
        var (driver, pipeline) = CreateMergeDriver(goal, repoManager);

        await RunMergePhaseAsync(driver, pipeline);

        Assert.Equal(["repo-a", "repo-b"], repoManager.MergedRepos);
        Assert.Equal("hash-a,hash-b", pipeline.MergeCommitHash);
        Assert.Equal(GoalPhase.Done, pipeline.Phase);
    }

    // ── Multi-repo explicit (one): only that target merged ────────────────

    [Fact]
    public async Task PerformMergeAsync_MultiRepoExplicitOne_MergesOnlyThatTarget()
    {
        var goal = BuildGoal(["repo-a", "repo-b"], targets: "repo-b");
        var repoManager = new RecordingRepoManager(
            ("repo-a", _ => "hash-a"),
            ("repo-b", _ => "hash-b"));
        var (driver, pipeline) = CreateMergeDriver(goal, repoManager);

        await RunMergePhaseAsync(driver, pipeline);

        Assert.Equal(["repo-b"], repoManager.MergedRepos);
        Assert.Equal("hash-b", pipeline.MergeCommitHash);
        Assert.Equal(GoalPhase.Done, pipeline.Phase);
    }

    // ── Multi-repo explicit (both): both merged; all succeed → complete ───

    [Fact]
    public async Task PerformMergeAsync_MultiRepoExplicitBoth_MergesBothAndCompletes()
    {
        var goal = BuildGoal(["repo-a", "repo-b"], targets: "repo-a, repo-b");
        var repoManager = new RecordingRepoManager(
            ("repo-a", _ => "hash-a"),
            ("repo-b", _ => "hash-b"));
        var (driver, pipeline) = CreateMergeDriver(goal, repoManager);

        await RunMergePhaseAsync(driver, pipeline);

        Assert.Equal(["repo-a", "repo-b"], repoManager.MergedRepos);
        Assert.Equal("hash-a,hash-b", pipeline.MergeCommitHash);
        Assert.Equal(GoalPhase.Done, pipeline.Phase);
    }

    // ── One target fails (throws): retryable failure, MergeCommitHash NOT assigned ──

    [Fact]
    public async Task PerformMergeAsync_TargetThrows_RetryableFailure_NoMergeCommitHash()
    {
        var goal = BuildGoal(["repo-a", "repo-b"], targets: null);
        var repoManager = new RecordingRepoManager(
            ("repo-a", _ => "hash-a"),
            ("repo-b", _ => throw new InvalidOperationException("merge conflict in repo-b")));
        var (driver, pipeline) = CreateMergeDriver(goal, repoManager, maxRetries: 0);

        await RunMergePhaseAsync(driver, pipeline);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Null(pipeline.MergeCommitHash);
        Assert.Equal(["repo-a", "repo-b"], repoManager.MergedRepos);
    }

    [Fact]
    public async Task PerformMergeAsync_TargetReturnsEmptyHash_RetryableFailure_NoMergeCommitHash()
    {
        var goal = BuildGoal(["repo-a", "repo-b"], targets: null);
        var repoManager = new RecordingRepoManager(
            ("repo-a", _ => "hash-a"),
            ("repo-b", _ => "   "));
        var (driver, pipeline) = CreateMergeDriver(goal, repoManager, maxRetries: 0);

        await RunMergePhaseAsync(driver, pipeline);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Null(pipeline.MergeCommitHash);
    }

    // ── Empty RepositoryNames → zero targets → retryable failure ─────────

    [Fact]
    public async Task PerformMergeAsync_EmptyRepositoryNames_RetryableFailure()
    {
        var goal = BuildGoal([], targets: null);
        var repoManager = new RecordingRepoManager();
        var (driver, pipeline) = CreateMergeDriver(goal, repoManager, maxRetries: 0);

        await RunMergePhaseAsync(driver, pipeline);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Null(pipeline.MergeCommitHash);
        Assert.Empty(repoManager.MergedRepos);
    }

    // ── Partial merge: A succeeds, B fails → retry; A re-merged (no-op hash), B retried ──

    [Fact]
    public async Task PerformMergeAsync_PartialMerge_Retry_ReMergesAAndRetriesB()
    {
        var goal = BuildGoal(["repo-a", "repo-b"], targets: null);
        // Repo-a always succeeds; repo-b fails the first time, succeeds on retry.
        var repoManager = new RecordingRepoManager(
            ("repo-a", _ => "hash-a"),
            ("repo-b", attempts => attempts == 1 ? throw new InvalidOperationException("b failed once") : "hash-b"));
        var (driver, pipeline) = CreateMergeDriver(goal, repoManager, maxRetries: 3, brain: new MergeRetryFakeBrain());

        // First merge attempt: A succeeds, B fails → retryable failure.
        await RunMergePhaseAsync(driver, pipeline);

        Assert.Null(pipeline.MergeCommitHash);
        Assert.Equal(["repo-a", "repo-b"], repoManager.MergedRepos);
        // The failure path re-plans and starts a new iteration at Coding.
        Assert.Equal(GoalPhase.Coding, pipeline.Phase);

        // Second merge attempt: A re-merged (no-op returns a valid hash), B succeeds.
        // Restore the state machine to Merging for the retry iteration.
        var plan = IterationPlan.Default();
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Merging);
        pipeline.AdvanceTo(GoalPhase.Merging);
        await RunMergePhaseAsync(driver, pipeline);

        Assert.Equal(["repo-a", "repo-b", "repo-a", "repo-b"], repoManager.MergedRepos);
        Assert.Equal("hash-a,hash-b", pipeline.MergeCommitHash);
        Assert.Equal(GoalPhase.Done, pipeline.Phase);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static Goal BuildGoal(string[] repositories, string? targets) => new()
    {
        Id = $"goal-{Guid.NewGuid():N}",
        Description = "Target-aware merge test",
        RepositoryNames = [.. repositories],
        TargetRepositoryNames = targets,
    };

    /// <summary>
    /// Builds a <see cref="GoalPipeline"/> positioned at the Merging phase and a
    /// <see cref="PipelineDriver"/> whose merge path uses the given repo manager.
    /// </summary>
    private static (PipelineDriver Driver, GoalPipeline Pipeline) CreateMergeDriver(
        Goal goal, RecordingRepoManager repoManager, int maxRetries = 3, IDistributedBrain? brain = null)
    {
        var goalSource = new InMemoryGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: maxRetries);
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Merging);
        pipeline.AdvanceTo(GoalPhase.Merging);
        pipeline.CoderBranch = "feature/test-branch";

        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);

        var driver = new PipelineDriver(
            brain: brain,
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: repoManager,
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: (_, _, _, _) => Task.CompletedTask,
            resolvePrompt: (_, _, _, _) => Task.FromResult("prompt"),
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(IterationPlan.Default())),
            resolveRepositories: g => g.RepositoryNames
                .Select(name => new TargetRepository { Name = name, Url = $"https://github.com/org/{name}", DefaultBranch = "main" })
                .ToList(),
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("Merge commit message"),
            logger: NullLogger<PipelineDriver>.Instance);

        return (driver, pipeline);
    }

    private static async Task RunMergePhaseAsync(PipelineDriver driver, GoalPipeline pipeline)
    {
        // DispatchPhaseAsync adds the Merging PhaseResult entry itself.
        await driver.DispatchPhaseAsync(pipeline, GoalPhase.Merging, null, TestContext.Current.CancellationToken);
    }

    /// <summary>In-memory goal source holding a single goal.</summary>
    private sealed class InMemoryGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "target-repos-merge-fake";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>(goal.Status == GoalStatus.Pending ? [goal] : []);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            goal.Status = status;
            if (metadata?.MergeCommitHash is not null)
                goal.MergeCommitHash = metadata.MergeCommitHash;
            return Task.CompletedTask;
        }
    }

    /// <summary>Minimal brain stub that returns the default plan for replanning.</summary>
    private sealed class MergeRetryFakeBrain : IDistributedBrain
    {
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(
            string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

        public BrainStats? GetStats() => null;
    }

    /// <summary>
    /// Fake <see cref="IBrainRepoManager"/> that records merge calls and returns
    /// per-repo scripted hashes or exceptions. Attempts are counted per repository
    /// so tests can script first-attempt failures followed by retry successes.
    /// </summary>
    private sealed class RecordingRepoManager : IBrainRepoManager
    {
        private readonly Dictionary<string, Func<int, string>> _scripted;
        private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

        public RecordingRepoManager()
            : this([])
        {
        }

        public RecordingRepoManager(params (string Repo, Func<int, string> Handler)[] scripted)
        {
            _scripted = scripted.ToDictionary(
                s => s.Repo,
                s => s.Handler,
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>All merge calls in order: repo names only.</summary>
        public List<string> MergedRepos { get; } = [];

        public string WorkDirectory => "/fake/work";

        public Task<string> MergeFeatureBranchAsync(
            string repoName, string featureBranch, string defaultBranch, string commitMessage, CancellationToken ct = default)
        {
            MergedRepos.Add(repoName);
            var attempt = _attempts.TryGetValue(repoName, out var count) ? count + 1 : 1;
            _attempts[repoName] = attempt;

            if (!_scripted.TryGetValue(repoName, out var handler))
                return Task.FromResult($"hash-{repoName}");

            return Task.FromResult(handler(attempt));
        }

        public Task<string> EnsureCloneAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.FromResult($"/fake/work/{repoName}");

        public Task<BranchDeleteResult> DeleteRemoteBranchAsync(string repoName, string branchName, CancellationToken ct = default) =>
            Task.FromResult(BranchDeleteResult.Success);

        public string GetClonePath(string repoName) => $"/fake/work/{repoName}";

        public Task<string?> GetHeadShaAsync(string repoName, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> MergeBranchAsync(string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> CreateTagAsync(string repoName, string tag, string branch, string message, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> DeleteTagAsync(string repoName, string tag, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<List<string>> ListRemoteBranchesAsync(string repoName, CancellationToken ct = default) =>
            Task.FromResult(new List<string>());
    }
}
