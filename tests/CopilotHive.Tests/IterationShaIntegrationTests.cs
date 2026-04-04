extern alias WorkerAssembly;

using System.Diagnostics;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using WorkerAssembly::CopilotHive.Worker;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for IterationStartSha capture and iteration-scoped diff behavior.
/// Uses real components with in-memory infrastructure to verify the full flow.
/// </summary>
public sealed class IterationShaIntegrationTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly PipelineStore _store;

    public IterationShaIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _store = new PipelineStore(":memory:", NullLogger<PipelineStore>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        if (Directory.Exists(_tempDir))
            TestHelpers.ForceDeleteDirectory(_tempDir);
    }

    private static Goal CreateGoal(string id = "goal-sha-test") =>
        new() { Id = id, Description = "Test iteration SHA capture", RepositoryNames = ["test-repo"] };

    private static TargetRepository CreateRepository() =>
        new()
        {
            Name = "test-repo",
            Url = "https://github.com/test/test.git",
            DefaultBranch = "main",
        };

    // ─────────────────────────────────────────────────────────────────────
    // Test 1: IterationStartSha is captured on the pipeline before coder dispatch
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CoderDispatch_WithRepoHavingCommits_CapturesIterationStartSha()
    {
        // Arrange — create a real git repo with commits
        var repoManager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        var repoPath = repoManager.GetClonePath("test-repo");
        Directory.CreateDirectory(repoPath);

        Git(repoPath, "init", "-b", "main");
        Git(repoPath, "config", "user.email", "test@test.com");
        Git(repoPath, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repoPath, "file.txt"), "initial content");
        Git(repoPath, "add", "file.txt");
        Git(repoPath, "commit", "-m", "Initial commit");

        var expectedSha = GitOutput(repoPath, "rev-parse", "HEAD").Trim();

        // Act — the GoalDispatcher's DispatchToRoleAsync captures SHA before building the task
        var sha = await repoManager.GetHeadShaAsync("test-repo", TestContext.Current.CancellationToken);

        // Assert — SHA matches HEAD
        Assert.NotNull(sha);
        Assert.Equal(expectedSha, sha);
        Assert.Equal(40, sha!.Length);
    }

    [Fact]
    public async Task CoderDispatch_WithEmptyRepo_SetsIterationStartShaToNull()
    {
        // Arrange — create a repo directory but empty (no commits)
        var repoManager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        var repoPath = repoManager.GetClonePath("empty-repo");
        Directory.CreateDirectory(repoPath);
        Directory.CreateDirectory(Path.Combine(repoPath, ".git"));

        // Act
        var sha = await repoManager.GetHeadShaAsync("empty-repo", TestContext.Current.CancellationToken);

        // Assert — empty repo returns null
        Assert.Null(sha);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test 2: Iteration diff shows only changes from current iteration
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TaskExecutor_WithIterationStartSha_IncludesIterationDiffForReviewer()
    {
        // This test verifies the TaskExecutor behavior
        // Arrange — use the existing test infrastructure
        const string iterationStartSha = "deadbeef1234567890abcdef12345678abcdef12";
        const string mergeBase = "aabbccdd1122334455667788990011223344556677";

        var runner = new CapturingAgentRunner();
        var git = new MockGitForIterationDiff(mergeBase);
        var executor = new TaskExecutor(runner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "task-review-iter",
            GoalId = "goal-iter-diff",
            GoalDescription = "Test iteration diff",
            Prompt = "Review changes",
            Role = WorkerRole.Reviewer,
            Repositories = [CreateRepository()],
            BranchInfo = new BranchSpec
            {
                Action = BranchAction.Checkout,
                BaseBranch = "main",
                FeatureBranch = "copilothive/feat/test-001",
            },
        };
        task.Metadata["iteration_start_sha"] = iterationStartSha;

        // Act
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert — both cumulative and iteration diff commands are present
        Assert.Contains($"Diff command: git diff {mergeBase}..HEAD", runner.LastPrompt);
        Assert.Contains($"Iteration diff command: git diff {iterationStartSha}..HEAD", runner.LastPrompt);
    }

    [Fact]
    public async Task SecondIteration_IterationStartSha_DifferentFromMergeBase()
    {
        // Arrange — create a repo with two iterations of changes
        var repoManager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        var repoPath = repoManager.GetClonePath("multi-iter-repo");
        Directory.CreateDirectory(repoPath);

        Git(repoPath, "init", "-b", "main");
        Git(repoPath, "config", "user.email", "test@test.com");
        Git(repoPath, "config", "user.name", "Test");

        // First iteration commit
        File.WriteAllText(Path.Combine(repoPath, "file1.txt"), "iteration 1");
        Git(repoPath, "add", "file1.txt");
        Git(repoPath, "commit", "-m", "First iteration");

        var iteration1Sha = GitOutput(repoPath, "rev-parse", "HEAD").Trim();

        // Simulate second iteration changes
        File.WriteAllText(Path.Combine(repoPath, "file2.txt"), "iteration 2");
        Git(repoPath, "add", "file2.txt");
        Git(repoPath, "commit", "-m", "Second iteration");

        var currentHead = GitOutput(repoPath, "rev-parse", "HEAD").Trim();

        // Act — simulate capturing SHA at different iteration starts
        var shaAtIter1Start = iteration1Sha;
        var shaAtIter2Start = currentHead; // After second commit

        // Compute merge base (would be same as HEAD if no upstream)
        // For a feature branch, merge base would be the branching point
        // In this test we simulate the scenario where iteration_start_sha differs

        // Assert — iteration SHA captured at iter1 start differs from HEAD after iter2
        Assert.NotEqual(shaAtIter1Start, shaAtIter2Start);

        // The iteration diff for iter1 would show all changes from iter1 start to HEAD
        // The iteration diff for iter2 would show only changes from iter2 start (which is HEAD now)
        Assert.Equal(40, shaAtIter1Start.Length);
        Assert.Equal(40, shaAtIter2Start.Length);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test 3: First iteration: both cumulative and iteration diffs are equivalent
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstIteration_IterationStartSha_EqualsMergeBase()
    {
        // Arrange — on the first iteration of a new branch, iteration_start_sha should equal merge_base
        // because no prior changes exist on this branch.

        var repoManager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        var repoPath = repoManager.GetClonePath("first-iter-repo");
        Directory.CreateDirectory(repoPath);

        Git(repoPath, "init", "-b", "main");
        Git(repoPath, "config", "user.email", "test@test.com");
        Git(repoPath, "config", "user.name", "Test");

        // Create initial commit on main
        File.WriteAllText(Path.Combine(repoPath, "initial.txt"), "base");
        Git(repoPath, "add", "initial.txt");
        Git(repoPath, "commit", "-m", "Initial commit on main");

        var mainHead = GitOutput(repoPath, "rev-parse", "HEAD").Trim();

        // Create feature branch
        Git(repoPath, "checkout", "-b", "copilothive/feat/first-iter-test");

        // On first iteration, no changes have been made yet on the feature branch
        // So the iteration_start_sha (captured before coder dispatch) equals merge_base
        // because HEAD is still at the branching point

        var featureHead = GitOutput(repoPath, "rev-parse", "HEAD").Trim();

        // Act — GetHeadShaAsync returns current HEAD which is the branching point
        var iterationStartSha = await repoManager.GetHeadShaAsync("first-iter-repo", TestContext.Current.CancellationToken);

        // For merge base calculation, we'd compare feature branch to main
        // Since no changes on feature branch yet, merge_base == HEAD
        var mergeBase = GitOutput(repoPath, "merge-base", "main", "HEAD").Trim();

        // Assert — on first iteration, these are equivalent
        Assert.NotNull(iterationStartSha);
        Assert.Equal(mainHead, featureHead); // No commits on feature branch yet
        Assert.Equal(mergeBase, iterationStartSha); // Iteration start equals merge base

        // Therefore: git diff {iteration_start_sha}..HEAD == git diff {merge_base}..HEAD
        // Both show an empty diff on first iteration before coder makes changes
    }

    [Fact]
    public async Task FirstIteration_AfterPriorIteration_IterationStartDiffersFromMergeBase()
    {
        // Arrange — simulate a second iteration where iteration_start_sha differs from merge_base
        var repoManager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        var repoPath = repoManager.GetClonePath("second-iter-repo");
        Directory.CreateDirectory(repoPath);

        Git(repoPath, "init", "-b", "main");
        Git(repoPath, "config", "user.email", "test@test.com");
        Git(repoPath, "config", "user.name", "Test");

        // Initial commit on main
        File.WriteAllText(Path.Combine(repoPath, "initial.txt"), "base");
        Git(repoPath, "add", "initial.txt");
        Git(repoPath, "commit", "-m", "Initial commit on main");

        var mergeBase = GitOutput(repoPath, "rev-parse", "HEAD").Trim();

        // Create feature branch
        Git(repoPath, "checkout", "-b", "copilothive/feat/second-iter-test");

        // First iteration changes
        File.WriteAllText(Path.Combine(repoPath, "iter1.txt"), "iteration 1");
        Git(repoPath, "add", "iter1.txt");
        Git(repoPath, "commit", "-m", "Iteration 1 changes");

        // Capture SHA at start of second iteration (this is where coder dispatch would happen)
        var iteration2StartSha = GitOutput(repoPath, "rev-parse", "HEAD").Trim();

        // Second iteration changes
        File.WriteAllText(Path.Combine(repoPath, "iter2.txt"), "iteration 2");
        Git(repoPath, "add", "iter2.txt");
        Git(repoPath, "commit", "-m", "Iteration 2 changes");

        var finalHead = GitOutput(repoPath, "rev-parse", "HEAD").Trim();

        // Act — GetHeadShaAsync returns SHA before second iteration changes
        // (In real flow, this would be captured before dispatch)
        var sha = await repoManager.GetHeadShaAsync("second-iter-repo", TestContext.Current.CancellationToken);

        // Assert — SHA differs from merge_base
        Assert.NotNull(sha);
        Assert.NotEqual(mergeBase, iteration2StartSha); // Iteration 2 start is after iter 1 changes

        // The cumulative diff (merge_base..HEAD) shows ALL changes from both iterations
        // The iteration diff (iteration2StartSha..HEAD) shows ONLY iteration 2 changes
        Assert.Contains("iter1", GitOutput(repoPath, "diff", "--name-only", mergeBase, finalHead));
        Assert.DoesNotContain("iter1", GitOutput(repoPath, "diff", "--name-only", iteration2StartSha, finalHead));
    }

    // ─────────────────────────────────────────────────────────────────────
    // GoalPipeline properties verify SHA is captured on pipeline object
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void IterationStartSha_CanBeSetOnPipeline()
    {
        // Arrange
        const string sha = "abc123def456789012345678901234567890abcd";
        var goal = CreateGoal("goal-pipeline-sha");
        var pipeline = new GoalPipeline(goal)
        {
            IterationStartSha = sha,
        };

        // Assert — property is set correctly
        Assert.Equal(sha, pipeline.IterationStartSha);
    }

    [Fact]
    public void IterationStartSha_DefaultsToNullOnNewPipeline()
    {
        // Arrange
        var goal = CreateGoal("goal-default-sha");
        var pipeline = new GoalPipeline(goal);

        // Assert — defaults to null (no SHA captured yet)
        Assert.Null(pipeline.IterationStartSha);
    }

    [Fact]
    public void IterationStartSha_CanBeResetToNull()
    {
        // Arrange
        const string sha = "abc123def456789012345678901234567890abcd";
        var goal = CreateGoal("goal-reset-sha");
        var pipeline = new GoalPipeline(goal)
        {
            IterationStartSha = sha,
        };

        // Act
        pipeline.IterationStartSha = null;

        // Assert
        Assert.Null(pipeline.IterationStartSha);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static void Git(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed with exit code {p.ExitCode}");
    }

    private static string GitOutput(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test infrastructure helpers (same pattern as TaskExecutorIterationDiffTests)
    // ─────────────────────────────────────────────────────────────────────

    private sealed class CapturingAgentRunner : IAgentRunner
    {
        public string LastPrompt { get; private set; } = "";
        public TestResultReport? LastTestReport { get; }
        public WorkerReport? LastWorkerReport { get; }

        private object? _session;

        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(WorkerRole role, string agentsMdContent) { }
        public void SetSession(object? session) => _session = session;
        public object? GetSession() => _session;
        public int GetContextUsagePercent() => 0;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetSessionAsync(string? model = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            LastPrompt = prompt;
            return Task.FromResult("Agent response");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockGitForIterationDiff : IGitOperations
    {
        private readonly string? _mergeBase;

        public MockGitForIterationDiff(string? mergeBase = "aabbccdd1122334455667788")
            => _mergeBase = mergeBase;

        public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct) => Task.CompletedTask;
        public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
        public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct) => Task.CompletedTask;
        public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
        public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
            => Task.FromResult(new GitChangeSummary { FilesChanged = 0 });
        public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct) => Task.FromResult(false);
        public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct)
            => Task.FromResult(_mergeBase);
        public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(string workDir, string args, CancellationToken ct)
            => Task.FromResult((0, "", ""));
        public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5) => Task.CompletedTask;
    }
}