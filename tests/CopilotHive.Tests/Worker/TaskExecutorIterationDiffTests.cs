extern alias WorkerAssembly;

using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Workers;
using WorkerAssembly::CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Tests verifying that <see cref="TaskExecutor"/> includes the iteration-scoped diff command
/// in the WORKSPACE CONTEXT for reviewer tasks when an <c>iteration_start_sha</c> metadata
/// value is present.
/// </summary>
public sealed class TaskExecutorIterationDiffTests
{
    // ── Mock helpers ────────────────────────────────────────────────────────

    private sealed class CapturingAgentRunner : IAgentRunner
    {
        /// <summary>The last prompt passed to <see cref="SendPromptAsync"/>.</summary>
        public string LastPrompt { get; private set; } = "";

        public TestResultReport? LastTestReport { get; } = null;
        public WorkerReport? LastWorkerReport { get; } = null;

        private object? _session;

        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetCustomAgent(WorkerRole role, string agentsMdContent) { }
        public void SetSession(object? session) => _session = session;
        public object? GetSession() => _session;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetSessionAsync(string? model = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            LastPrompt = prompt;
            return Task.FromResult("Agent response");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockGitForReviewer : IGitOperations
    {
        private readonly string? _mergeBase;

        public MockGitForReviewer(string? mergeBase = "aabbccdd1122334455667788")
            => _mergeBase = mergeBase;

        public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct) => Task.CompletedTask;
        public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
        public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct) => Task.CompletedTask;
        public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
        public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
            => Task.FromResult(new GitChangeSummary { FilesChanged = 0 });
        public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct) => Task.FromResult(false);
        public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct)
            => Task.FromResult<string?>(_mergeBase);
        public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(string workDir, string args, CancellationToken ct)
            => Task.FromResult((0, "", ""));
        public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5) => Task.CompletedTask;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static WorkTask BuildReviewerTask(string? iterationStartSha = null)
    {
        var task = new WorkTask
        {
            TaskId = "task-review-1",
            GoalId = "goal-1",
            GoalDescription = "Test goal",
            Prompt = "Review the changes",
            Role = WorkerRole.Reviewer,
            Repositories =
            [
                new TargetRepository
                {
                    Name = "test-repo",
                    Url = "https://github.com/test/test.git",
                    DefaultBranch = "main",
                },
            ],
            BranchInfo = new BranchSpec
            {
                Action = BranchAction.Checkout,
                BaseBranch = "main",
                FeatureBranch = "copilothive/feat/test-001",
            },
        };

        if (iterationStartSha is not null)
            task.Metadata["iteration_start_sha"] = iterationStartSha;

        return task;
    }

    private sealed class MockGitWithHead : IGitOperations
    {
        private readonly string? _mergeBase;
        private readonly string? _headSha;

        public MockGitWithHead(string? mergeBase = null, string? headSha = "abc123def456789012345678901234567890abcd")
        {
            _mergeBase = mergeBase;
            _headSha = headSha;
        }

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
        {
            // Respond to "rev-parse HEAD" with the configured head SHA
            if (args.Contains("rev-parse") && _headSha is not null)
                return Task.FromResult((0, _headSha + "\n", ""));
            return Task.FromResult((0, "", ""));
        }
        public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5) => Task.CompletedTask;
    }

    [Fact]
    public async Task CoderTask_CapturesHeadSha_InTaskResult()
    {
        // Arrange — coder task where git HEAD is known
        const string expectedSha = "abc123def456789012345678901234567890abcd";
        var runner = new CapturingAgentRunner();
        var git = new MockGitWithHead(mergeBase: null, headSha: expectedSha);
        var executor = new TaskExecutor(runner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "task-coder-sha",
            GoalId = "goal-sha",
            GoalDescription = "Implement feature",
            Prompt = "Write the code",
            Role = WorkerRole.Coder,
            Repositories =
            [
                new TargetRepository
                {
                    Name = "test-repo",
                    Url = "https://github.com/test/test.git",
                    DefaultBranch = "main",
                },
            ],
            BranchInfo = new BranchSpec
            {
                Action = BranchAction.Create,
                BaseBranch = "main",
                FeatureBranch = "copilothive/feat/test-001",
            },
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert — IterationStartSha is populated from git HEAD captured before agent ran
        Assert.Equal(expectedSha, result.IterationStartSha);
    }

    [Fact]
    public async Task CoderTask_WhenGitHeadUnavailable_IterationStartShaIsNull()
    {
        // Arrange — git returns non-zero exit for rev-parse (empty repo scenario)
        var runner = new CapturingAgentRunner();
        var git = new MockGitWithHead(mergeBase: null, headSha: null); // null → returns exit 0 but empty stdout
        var executor = new TaskExecutor(runner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "task-coder-empty",
            GoalId = "goal-empty",
            GoalDescription = "Implement feature",
            Prompt = "Write the code",
            Role = WorkerRole.Coder,
            Repositories =
            [
                new TargetRepository
                {
                    Name = "test-repo",
                    Url = "https://github.com/test/test.git",
                    DefaultBranch = "main",
                },
            ],
            BranchInfo = new BranchSpec
            {
                Action = BranchAction.Create,
                BaseBranch = "main",
                FeatureBranch = "copilothive/feat/test-001",
            },
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert — no SHA when git rev-parse returns nothing
        Assert.Null(result.IterationStartSha);
    }

    [Fact]
    public async Task ReviewerTask_DoesNotCaptureIterationStartSha()
    {
        // Arrange — reviewer task should not capture SHA (only coder does)
        const string headSha = "abc123def456789012345678901234567890abcd";
        var runner = new CapturingAgentRunner();
        var git = new MockGitWithHead(mergeBase: "aabbccdd1122334455667788", headSha: headSha);
        var executor = new TaskExecutor(runner, gitOperations: git);
        var task = BuildReviewerTask();

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert — reviewer result never has IterationStartSha
        Assert.Null(result.IterationStartSha);
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewerTask_WithIterationStartSha_IncludesIterationDiffCommand()
    {
        // Arrange
        const string iterationStartSha = "deadbeef1234567890abcdef12345678abcdef12";
        var runner = new CapturingAgentRunner();
        var git = new MockGitForReviewer("aabbccdd1122334455667788");
        var executor = new TaskExecutor(runner, gitOperations: git);
        var task = BuildReviewerTask(iterationStartSha);

        // Act
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert — iteration diff command must appear in the enriched prompt
        Assert.Contains($"Iteration diff command: git diff {iterationStartSha}..HEAD", runner.LastPrompt);
    }

    [Fact]
    public async Task ReviewerTask_WithIterationStartSha_AlsoIncludesCumulativeDiffCommand()
    {
        // Arrange — the cumulative diff (merge-base) must still be present alongside the iteration diff
        const string iterationStartSha = "deadbeef1234567890abcdef12345678abcdef12";
        const string mergeBase = "aabbccdd1122334455667788990011223344556677";
        var runner = new CapturingAgentRunner();
        var git = new MockGitForReviewer(mergeBase);
        var executor = new TaskExecutor(runner, gitOperations: git);
        var task = BuildReviewerTask(iterationStartSha);

        // Act
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert — both diff commands are present
        Assert.Contains($"Diff command: git diff {mergeBase}..HEAD", runner.LastPrompt);
        Assert.Contains($"Iteration diff command: git diff {iterationStartSha}..HEAD", runner.LastPrompt);
    }

    [Fact]
    public async Task ReviewerTask_WithoutIterationStartSha_OmitsIterationDiffCommand()
    {
        // Arrange — no iteration_start_sha in metadata → omit iteration diff line
        var runner = new CapturingAgentRunner();
        var git = new MockGitForReviewer("aabbccdd1122334455667788990011223344556677");
        var executor = new TaskExecutor(runner, gitOperations: git);
        var task = BuildReviewerTask(iterationStartSha: null);

        // Act
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert — no iteration diff line
        Assert.DoesNotContain("Iteration diff command:", runner.LastPrompt);
    }

    [Fact]
    public async Task ReviewerTask_WithEmptyIterationStartSha_OmitsIterationDiffCommand()
    {
        // Arrange — empty string in metadata is treated the same as absent
        var runner = new CapturingAgentRunner();
        var git = new MockGitForReviewer("aabbccdd1122334455667788990011223344556677");
        var executor = new TaskExecutor(runner, gitOperations: git);
        var task = BuildReviewerTask(iterationStartSha: null);
        task.Metadata["iteration_start_sha"] = ""; // explicitly empty

        // Act
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert — no iteration diff line
        Assert.DoesNotContain("Iteration diff command:", runner.LastPrompt);
    }

    [Fact]
    public async Task ReviewerTask_WithNoMergeBase_OmitsIterationDiffCommand()
    {
        // Arrange — if there is no merge-base, the entire diff block is omitted,
        // so the iteration diff must not appear either.
        const string iterationStartSha = "deadbeef1234567890abcdef12345678abcdef12";
        var runner = new CapturingAgentRunner();
        var git = new MockGitForReviewer(mergeBase: null); // no merge-base
        var executor = new TaskExecutor(runner, gitOperations: git);
        var task = BuildReviewerTask(iterationStartSha);

        // Act
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert — iteration diff must not appear without a merge base
        Assert.DoesNotContain("Iteration diff command:", runner.LastPrompt);
    }

    [Fact]
    public async Task CoderTask_WithIterationStartSha_OmitsIterationDiffCommand()
    {
        // Arrange — iteration diff is only for reviewers; coders must not see it
        const string iterationStartSha = "deadbeef1234567890abcdef12345678abcdef12";
        var runner = new CapturingAgentRunner();
        var git = new MockGitForReviewer("aabbccdd1122334455667788990011223344556677");
        var executor = new TaskExecutor(runner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "task-coder-1",
            GoalId = "goal-1",
            GoalDescription = "Implement feature",
            Prompt = "Write the code",
            Role = WorkerRole.Coder, // NOT reviewer
            Repositories =
            [
                new TargetRepository
                {
                    Name = "test-repo",
                    Url = "https://github.com/test/test.git",
                    DefaultBranch = "main",
                },
            ],
            BranchInfo = new BranchSpec
            {
                Action = BranchAction.Create,
                BaseBranch = "main",
                FeatureBranch = "copilothive/feat/test-001",
            },
            Metadata = { ["iteration_start_sha"] = iterationStartSha },
        };

        // Act
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert — coder prompt does not receive iteration diff
        Assert.DoesNotContain("Iteration diff command:", runner.LastPrompt);
    }
}
