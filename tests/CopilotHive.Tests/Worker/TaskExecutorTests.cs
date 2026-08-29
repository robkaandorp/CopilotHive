using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Tests for <see cref="TaskExecutor"/> push error handling.
/// </summary>
[Collection("ConsoleOutput")]
public sealed class TaskExecutorTests
{
    /// <summary>
    /// Mock implementation of <see cref="IGitOperations"/> that simulates git operations.
    /// </summary>
    internal sealed class MockGitOperations : IGitOperations
    {
        /// <summary>Controls whether PushBranchAsync throws an exception.</summary>
        public bool PushShouldFail { get; set; }

        /// <summary>The error message to use when push fails.</summary>
        public string PushErrorMessage { get; set; } = "Failed to push branch 'feature-branch': Permission denied";

        /// <summary>Controls whether GetGitStatusAsync reports file changes.</summary>
        public int FilesChanged { get; set; } = 5;

        /// <summary>
        /// Optional per-repository status overrides, keyed by repository name (the last
        /// segment of the clone directory). When a key matches, that summary is returned
        /// instead of the default one built from <see cref="FilesChanged"/>.
        /// </summary>
        public Dictionary<string, GitChangeSummary> StatusByRepoName { get; } = [];

        /// <summary>Repository names for which PushBranchAsync should throw.</summary>
        public HashSet<string> PushFailsForRepos { get; } = [];

        /// <summary>Tracks if PushBranchAsync was called.</summary>
        public bool PushWasCalled { get; private set; }

        public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct)
            => Task.CompletedTask;

        public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct)
            => Task.CompletedTask;

        public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct)
            => Task.CompletedTask;

        public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct)
        {
            PushWasCalled = true;
            if (PushShouldFail || PushFailsForRepos.Contains(Path.GetFileName(repoDir)))
                throw new GitOperationException(PushErrorMessage);
            return Task.CompletedTask;
        }

        public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
        {
            var name = Path.GetFileName(repoDir);
            if (StatusByRepoName.TryGetValue(name, out var overrideStatus))
                return Task.FromResult(overrideStatus);
            return Task.FromResult(new GitChangeSummary { FilesChanged = FilesChanged, Insertions = 10, Deletions = 2 });
        }

        public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct)
            => Task.FromResult(false);

        public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct)
            => Task.FromResult<string?>("abc123def456789012345678");

        /// <summary>
        /// Optional scripted responder for <see cref="RunGitCommandAsync"/>, keyed on the raw
        /// argument string. Returns null to fall through to the default success response.
        /// </summary>
        public Func<string, (int ExitCode, string Stdout, string Stderr)?>? GitCommandResponder { get; set; }

        /// <summary>
        /// Optional thrower for <see cref="RunGitCommandAsync"/>, keyed on the raw argument
        /// string. When it returns a non-null <see cref="Exception"/>, the mock throws that
        /// exception instead of consulting <see cref="GitCommandResponder"/>. This simulates
        /// a git command failing with an exception (e.g. git binary not found) rather than a
        /// non-zero exit code. Checked BEFORE <see cref="GitCommandResponder"/>.
        /// </summary>
        public Func<string, Exception?>? GitCommandThrower { get; set; }

        /// <summary>Every argument string passed to <see cref="RunGitCommandAsync"/>, in order.</summary>
        public List<string> GitCommands { get; } = [];

        /// <summary>Every working directory passed to <see cref="RunGitCommandAsync"/>, in order.</summary>
        public List<string> WorkDirs { get; } = [];

        /// <summary>Every cancellation token passed to <see cref="RunGitCommandAsync"/>, in order.</summary>
        public List<CancellationToken> GitTokens { get; } = [];

        public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(
            string workDir, string args, CancellationToken ct)
        {
            GitCommands.Add(args);
            WorkDirs.Add(workDir);
            GitTokens.Add(ct);
            if (GitCommandThrower?.Invoke(args) is { } ex)
                throw ex;
            var scripted = GitCommandResponder?.Invoke(args);
            return Task.FromResult(scripted ?? (0, "", ""));
        }

        public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Mock implementation of <see cref="IAgentRunner"/> for testing.
    /// </summary>
    internal sealed class MockAgentRunner : IAgentRunner
    {
        /// <summary>
        /// The WorkerReport to inject into <see cref="LastWorkerReport"/> when <see cref="SendPromptAsync"/> is called.
        /// Set this before calling the code under test.
        /// </summary>
        public WorkerReport? WorkerReportToReturn { get; set; }

        /// <summary>
        /// The TestResultReport to inject into <see cref="LastTestReport"/> when <see cref="SendPromptAsync"/> is called.
        /// Set this before calling the code under test.
        /// </summary>
        public TestResultReport? TestReportToReturn { get; set; }

        // Internally tracked; set to null when Clear is called, set to ToReturn when SendPrompt is called.
        public WorkerReport? LastWorkerReport { get; private set; }
        public TestResultReport? LastTestReport { get; private set; }

        private object? _session;

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

        /// <summary>Captures the catalog passed to <see cref="SetSubAgentModels"/>, or null if never called.</summary>
        public IReadOnlyList<SubAgentModelDto>? CapturedSubAgentModels { get; private set; }

        /// <summary>The working directory passed to the most recent <see cref="SendPromptAsync"/> call.</summary>
        public string? LastWorkDir { get; private set; }

        /// <summary>The prompt content passed to the most recent <see cref="SendPromptAsync"/> call.</summary>
        public string? LastPrompt { get; private set; }

        public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) => CapturedSubAgentModels = models;
        public void SetSession(object? session) => _session = session;
        public object? GetSession() => _session;
        public int GetContextUsagePercent() => 0;

        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) { }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            // After TaskExecutor clears reports (ClearWorkerReport/ClearTestReport), inject the
            // mock reports here so they are visible to the code that reads LastWorkerReport/LastTestReport.
            LastWorkerReport = WorkerReportToReturn;
            LastTestReport = TestReportToReturn;
            LastWorkDir = workDir;
            LastPrompt = prompt;
            return Task.FromResult("Mock agent response");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// The sub-agent model catalog on the <see cref="WorkTask"/> must be forwarded verbatim to the
    /// agent runner. Removing the <c>SetSubAgentModels</c> call from <see cref="TaskExecutor"/>
    /// leaves <c>CapturedSubAgentModels</c> null and fails this test.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ForwardsSubAgentModelsToAgentRunner()
    {
        // Arrange
        var git = new MockGitOperations { PushShouldFail = false, FilesChanged = 1 };
        var agentRunner = new MockAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        IReadOnlyList<SubAgentModelDto> catalog =
        [
            new SubAgentModelDto { Id = "model-a", ContextWindow = 200_000, Description = "Big model" },
            new SubAgentModelDto { Id = "model-b", ContextWindow = null, Description = "Unknown ctx" },
        ];

        var task = new WorkTask
        {
            TaskId = "test-task-subagents",
            GoalId = "goal-subagents",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Coder,
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
            BranchInfo = new BranchSpec { Action = BranchAction.Create, BaseBranch = "main", FeatureBranch = "feature-branch" },
            SubAgentModels = catalog,
        };

        // Act
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert — the exact catalog instance and its contents reached the runner
        Assert.NotNull(agentRunner.CapturedSubAgentModels);
        Assert.Same(catalog, agentRunner.CapturedSubAgentModels);
        Assert.Equal(2, agentRunner.CapturedSubAgentModels!.Count);

        Assert.Equal("model-a", agentRunner.CapturedSubAgentModels[0].Id);
        Assert.Equal(200_000, agentRunner.CapturedSubAgentModels[0].ContextWindow);
        Assert.Equal("Big model", agentRunner.CapturedSubAgentModels[0].Description);

        Assert.Equal("model-b", agentRunner.CapturedSubAgentModels[1].Id);
        Assert.Null(agentRunner.CapturedSubAgentModels[1].ContextWindow);
        Assert.Equal("Unknown ctx", agentRunner.CapturedSubAgentModels[1].Description);
    }

    /// <summary>
    /// A task with no sub-agent catalog must still call <c>SetSubAgentModels</c> with an empty
    /// list, so a previously-configured catalog on a reused runner is cleared.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithEmptyCatalog_StillForwardsEmptyListToAgentRunner()
    {
        var git = new MockGitOperations { PushShouldFail = false, FilesChanged = 1 };
        var agentRunner = new MockAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-task-no-subagents",
            GoalId = "goal-no-subagents",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Coder,
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
            BranchInfo = new BranchSpec { Action = BranchAction.Create, BaseBranch = "main", FeatureBranch = "feature-branch" },
        };

        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        Assert.NotNull(agentRunner.CapturedSubAgentModels);
        Assert.Empty(agentRunner.CapturedSubAgentModels!);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPushFails_IncludesGitPushErrorsSection()
    {
        // Arrange
        var git = new MockGitOperations
        {
            PushShouldFail = true,
            PushErrorMessage = "Failed to push branch 'feature-branch': Permission denied",
            FilesChanged = 5
        };
        var agentRunner = new MockAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-task-1",
            GoalId = "goal-1",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Coder,
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
            BranchInfo = new BranchSpec { Action = BranchAction.Create, BaseBranch = "main", FeatureBranch = "feature-branch" }
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("[Git Push Errors]", result.Output);
        Assert.Contains("Push failed for test-repo: Failed to push branch 'feature-branch': Permission denied", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPushFails_AddsErrorToIssues()
    {
        // Arrange
        var git = new MockGitOperations
        {
            PushShouldFail = true,
            PushErrorMessage = "Failed to push branch 'feature-branch': Permission denied",
            FilesChanged = 5
        };
        var agentRunner = new MockAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-task-2",
            GoalId = "goal-2",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Coder,
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
            BranchInfo = new BranchSpec { Action = BranchAction.Create, BaseBranch = "main", FeatureBranch = "feature-branch" }
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEmpty(result.Metrics!.Issues);
        Assert.Contains("Push failed for test-repo: Failed to push branch 'feature-branch': Permission denied", result.Metrics.Issues);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPushSucceeds_NoGitPushErrorsSection()
    {
        // Arrange
        var git = new MockGitOperations
        {
            PushShouldFail = false,
            FilesChanged = 5
        };
        var agentRunner = new MockAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-task-3",
            GoalId = "goal-3",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Coder,
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
            BranchInfo = new BranchSpec { Action = BranchAction.Create, BaseBranch = "main", FeatureBranch = "feature-branch" }
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("[Git Push Errors]", result.Output);
        Assert.True(git.PushWasCalled);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPushSucceeds_NoIssuesFromPush()
    {
        // Arrange
        var git = new MockGitOperations
        {
            PushShouldFail = false,
            FilesChanged = 5
        };
        var agentRunner = new MockAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-task-4",
            GoalId = "goal-4",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Coder,
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
            BranchInfo = new BranchSpec { Action = BranchAction.Create, BaseBranch = "main", FeatureBranch = "feature-branch" }
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("Push failed", string.Join(", ", result.Metrics?.Issues ?? []));
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoFilesChanged_DoesNotAttemptPush()
    {
        // Arrange
        var git = new MockGitOperations
        {
            PushShouldFail = true, // Would fail if called
            FilesChanged = 0 // No files changed
        };
        var agentRunner = new MockAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-task-5",
            GoalId = "goal-5",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Coder,
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
            BranchInfo = new BranchSpec { Action = BranchAction.Create, BaseBranch = "main", FeatureBranch = "feature-branch" }
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert - no push was attempted, no error section
        Assert.False(git.PushWasCalled);
        Assert.DoesNotContain("[Git Push Errors]", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_WhenReviewerRole_DoesNotAttemptPush()
    {
        // Arrange - reviewer role should never push
        var git = new MockGitOperations
        {
            PushShouldFail = true, // Would fail if called
            FilesChanged = 5
        };
        var agentRunner = new MockAgentRunner(); // No worker report - but Reviewer doesn't push so this is OK
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-task-6",
            GoalId = "goal-6",
            GoalDescription = "Test goal",
            Prompt = "Review the changes",
            Role = WorkerRole.Reviewer, // Reviewer never pushes (read-only role)
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
            BranchInfo = new BranchSpec { Action = BranchAction.Checkout, BaseBranch = "main", FeatureBranch = "feature-branch" }
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert - reviewer never pushes, even with file changes
        Assert.False(git.PushWasCalled);
        Assert.DoesNotContain("[Git Push Errors]", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_MultiplePushErrors_AllIncludedInOutput()
    {
        // Arrange - simulate push failing for multiple repos
        var git = new MockGitOperations
        {
            PushShouldFail = true,
            PushErrorMessage = "Authentication failed",
            FilesChanged = 5
        };
        var agentRunner = new MockAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-task-7",
            GoalId = "goal-7",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Coder,
            Repositories =
            [
                new TargetRepository { Name = "repo1", Url = "https://github.com/test/repo1.git", DefaultBranch = "main" },
                new TargetRepository { Name = "repo2", Url = "https://github.com/test/repo2.git", DefaultBranch = "main" }
            ],
            BranchInfo = new BranchSpec { Action = BranchAction.Create, BaseBranch = "main", FeatureBranch = "feature-branch" }
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert - both repos should have push errors (our mock simulates failure for each)
        Assert.Contains("[Git Push Errors]", result.Output);
        // Each repo gets a push attempt, and each fails
        Assert.Contains("Push failed for repo1", result.Output);
        Assert.Contains("Push failed for repo2", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_MultiplePushErrors_AllAddedToIssues()
    {
        // Arrange
        var git = new MockGitOperations
        {
            PushShouldFail = true,
            PushErrorMessage = "Authentication failed",
            FilesChanged = 5
        };
        var agentRunner = new MockAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-task-8",
            GoalId = "goal-8",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Coder,
            Repositories =
            [
                new TargetRepository { Name = "repo1", Url = "https://github.com/test/repo1.git", DefaultBranch = "main" },
                new TargetRepository { Name = "repo2", Url = "https://github.com/test/repo2.git", DefaultBranch = "main" }
            ],
            BranchInfo = new BranchSpec { Action = BranchAction.Create, BaseBranch = "main", FeatureBranch = "feature-branch" }
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEmpty(result.Metrics!.Issues);
        // Verify both push errors are in the issues (there may be additional issues like missing report)
        Assert.Contains(result.Metrics.Issues, i => i.Contains("Push failed for repo1"));
        Assert.Contains(result.Metrics.Issues, i => i.Contains("Push failed for repo2"));
    }

    // ── Summary population ─────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="TaskMetrics.Summary"/> is populated from
    /// <see cref="WorkerReport.Summary"/> when a WorkerReport is available.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithWorkerReport_PopulatesSummaryFromWorkerReport()
    {
        // Arrange
        var git = new MockGitOperations { FilesChanged = 0 };
        var agentRunner = new MockAgentRunner(); // Start with no reports
        agentRunner.WorkerReportToReturn = new WorkerReport // Set AFTER construction so it survives ClearWorkerReport()
        {
            TaskVerdict = TaskVerdict.Pass,
            Summary = "Added feature X to module Y",
            Issues = [],
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-summary-worker",
            GoalId = "goal-summary",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Coder,
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Added feature X to module Y", result.Metrics!.Summary);
    }

    /// <summary>
    /// Verifies that <see cref="TaskMetrics.Summary"/> is populated from
    /// <see cref="TestResultReport.Summary"/> when a TestResultReport is available
    /// but no WorkerReport is present.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithTestResultReport_PopulatesSummaryFromTestReport()
    {
        // Arrange
        var git = new MockGitOperations { FilesChanged = 0 };
        var agentRunner = new MockAgentRunner(); // Start with no reports
        agentRunner.TestReportToReturn = new TestResultReport // Set AFTER construction so it survives ClearTestReport()
        {
            Verdict = TaskVerdict.Pass,
            TotalTests = 10,
            PassedTests = 10,
            FailedTests = 0,
            Summary = "All 10 tests passed, build succeeded",
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-summary-test",
            GoalId = "goal-test-summary",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Tester,
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("All 10 tests passed, build succeeded", result.Metrics!.Summary);
    }

    /// <summary>
    /// Verifies that <see cref="WorkerReport.Summary"/> takes priority over
    /// <see cref="TestResultReport.Summary"/> when both are available.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithBothReports_WorkerReportTakesPriority()
    {
        // Arrange
        var git = new MockGitOperations { FilesChanged = 0 };
        var agentRunner = new MockAgentRunner(); // Start with no reports
        agentRunner.WorkerReportToReturn = new WorkerReport // Set AFTER construction
        {
            TaskVerdict = TaskVerdict.Pass,
            Summary = "Coder summary — implemented feature X",
        };
        agentRunner.TestReportToReturn = new TestResultReport // Set AFTER construction
        {
            Verdict = TaskVerdict.Pass,
            TotalTests = 5,
            PassedTests = 5,
            FailedTests = 0,
            Summary = "Tester summary — tests passed",
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-summary-both",
            GoalId = "goal-both-summary",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Coder,
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Coder summary — implemented feature X", result.Metrics!.Summary);
    }

    /// <summary>
    /// Verifies that <see cref="TaskMetrics.Summary"/> defaults to empty string
    /// when neither report provides a summary.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithNoReports_SummaryIsEmpty()
    {
        // Arrange
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations { FilesChanged = 0 };
        var agentRunner = new MockAgentRunner(); // No reports set
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        var task = new WorkTask
        {
            TaskId = "test-summary-none",
            GoalId = "goal-no-summary",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Improver, // Improver has no report tool
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("", result.Metrics!.Summary);

        // Path-resolution assertions: improver working directory and context header resolve to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    // ── Missing report tool tests ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that when a non-Improver worker (e.g. Coder) completes without filing a report,
    /// the verdict is FAIL with a descriptive issue message explaining the missing report.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NonImproverWithoutReport_FailsWithDescriptiveIssue()
    {
        // Arrange
        var git = new MockGitOperations { FilesChanged = 0 };
        var agentRunner = new MockAgentRunner(); // No reports set — simulates worker completing without calling report tool
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-no-report",
            GoalId = "goal-no-report",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Coder, // Coder has a mandatory report tool
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Single(result.Metrics.Issues);
        Assert.Contains("Worker (coder) completed without calling its mandatory report tool", result.Metrics.Issues[0]);
        Assert.Contains("API errors, timeouts, or the worker hallucinating tool calls as text", result.Metrics.Issues[0]);
    }

    /// <summary>
    /// Verifies that when a Reviewer completes without filing a report,
    /// the verdict is REQUEST_CHANGES (not FAIL) with a descriptive issue message.
    /// Reviewer missing-report must not route through the test-retry path.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerWithoutReport_ProducesRequestChanges()
    {
        // Arrange
        var git = new MockGitOperations { FilesChanged = 0 };
        var agentRunner = new MockAgentRunner(); // No reports set — simulates reviewer completing without calling report tool
        var executor = new TaskExecutor(agentRunner, gitOperations: git);

        var task = new WorkTask
        {
            TaskId = "test-reviewer-no-report",
            GoalId = "goal-reviewer-no-report",
            GoalDescription = "Test goal",
            Prompt = "Review the changes",
            Role = WorkerRole.Reviewer, // Reviewer has a mandatory report tool
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
            BranchInfo = new BranchSpec { Action = BranchAction.Checkout, BaseBranch = "main", FeatureBranch = "feature-branch" }
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("REQUEST_CHANGES", result.Metrics!.Verdict);
        Assert.Single(result.Metrics.Issues);
        Assert.Contains("Worker (reviewer) completed without calling its mandatory report tool", result.Metrics.Issues[0]);
        Assert.Contains("API errors, timeouts, or the worker hallucinating tool calls as text", result.Metrics.Issues[0]);
    }

    /// <summary>
    /// Verifies that when an Improver completes without filing a report, the verdict is PASS
    /// (since Improver does not have a mandatory report tool).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverWithoutReport_Passes()
    {
        // Arrange
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations { FilesChanged = 0 };
        var agentRunner = new MockAgentRunner(); // No reports set
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        var task = new WorkTask
        {
            TaskId = "test-improver-no-report",
            GoalId = "goal-improver-no-report",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Improver, // Improver has no report tool
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
        };

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("PASS", result.Metrics!.Verdict);
        Assert.Empty(result.Metrics.Issues);

        // Path-resolution assertions: improver working directory and context header resolve to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    // ── Improver config-repo changed-file paths ───────────────────────────────

    /// <summary>
    /// Creates a fresh per-test config-repo directory (with a <c>.git</c> marker and an
    /// <c>agents</c> subfolder) and returns a disposable that removes it. All improver
    /// tests use this instead of the hardcoded <c>/config-repo</c> path so they can run
    /// in CI environments that do not mount the Docker config repo.
    /// </summary>
    private static IDisposable EnsureConfigRepoMarker(out string configRepoDir)
    {
        configRepoDir = Path.Combine(Path.GetTempPath(), $"copilothive-test-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(configRepoDir, ".git"));
        Directory.CreateDirectory(Path.Combine(configRepoDir, "agents"));
        return new DirectoryRemover(configRepoDir);
    }

    private sealed class DirectoryRemover(string path) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (IOException) { /* best-effort cleanup of test scaffolding */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup of test scaffolding */ }
        }
    }

    private static WorkTask BuildImproverTask(string id) => new()
    {
        TaskId = id,
        GoalId = $"goal-{id}",
        GoalDescription = "Improve the agents",
        Prompt = "Improve prompt",
        Role = WorkerRole.Improver,
        Repositories = [Repo("test-repo")],
    };

    /// <summary>
    /// Builds a responder that reports the given staged config-repo paths via the
    /// NUL-delimited <c>diff --cached --name-only -z</c> output, optionally failing the push.
    /// </summary>
    private static Func<string, (int, string, string)?> ConfigRepoResponder(
        IEnumerable<string> stagedPaths, bool pushFails)
    {
        var stagedOut = string.Concat(stagedPaths.Select(p => p + "\0"));
        return args =>
        {
            if (args.StartsWith("diff --cached --name-only"))
                return (0, stagedOut, "");
            if (args == "push" && pushFails)
                return (1, "", "remote rejected: permission denied");
            return null;
        };
    }

    /// <summary>
    /// On the improver SUCCESS path the returned summary must carry the real config-repo
    /// relative paths, not just a count.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverPushSucceeds_ReportsConfigRepoChangedPaths()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        string[] staged = ["agents/reviewer.agents.md", "agents/coder.agents.md"];
        var agentRunner = new MockAgentRunner();
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(staged, pushFails: false),
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        var result = await executor.ExecuteAsync(
            BuildImproverTask("improver-push-ok"), TestContext.Current.CancellationToken);

        Assert.NotNull(result.GitStatus);
        Assert.True(result.GitStatus!.Pushed);
        Assert.Equal(2, result.GitStatus.FilesChanged);
        Assert.Equal(staged, result.GitStatus.ChangedFiles);

        // Path-resolution assertions: git work dir, agent work dir, and prompt context all resolve to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Contains(configRepoDir, git.WorkDirs);
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    /// <summary>
    /// END-TO-END regression for the reviewer's CRITICAL finding: when the improver's config-repo
    /// push FAILS, the result must reach the orchestrator with a positive count AND the real
    /// changed-file paths — previously the filenames were discarded, leaving an empty list.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverPushFails_StillReportsConfigRepoChangedPaths()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        string[] staged = ["agents/reviewer.agents.md", "agents/tester.agents.md"];
        var agentRunner = new MockAgentRunner();
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(staged, pushFails: true),
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        var result = await executor.ExecuteAsync(
            BuildImproverTask("improver-push-fail"), TestContext.Current.CancellationToken);

        Assert.NotNull(result.GitStatus);
        // Push failed but the diagnostic paths survive — this is exactly the state
        // PipelineDriver needs to log a useful "push failed" warning.
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(2, result.GitStatus.FilesChanged);
        Assert.Equal(staged, result.GitStatus.ChangedFiles);
        Assert.NotEmpty(result.GitStatus.ChangedFiles);

        // Path-resolution assertions: git work dir, agent work dir, and prompt context all resolve to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Contains(configRepoDir, git.WorkDirs);
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    /// <summary>
    /// When the improver's COMMIT fails, the paths must still be reported.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverCommitFails_StillReportsConfigRepoChangedPaths()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        string[] staged = ["agents/improver.agents.md"];
        var stagedOut = string.Concat(staged.Select(p => p + "\0"));
        var agentRunner = new MockAgentRunner();
        var git = new MockGitOperations
        {
            GitCommandResponder = args =>
            {
                if (args.StartsWith("diff --cached --name-only"))
                    return (0, stagedOut, "");
                if (args.StartsWith("commit -m"))
                    return (1, "", "nothing to commit / hook rejected");
                return null;
            },
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        var result = await executor.ExecuteAsync(
            BuildImproverTask("improver-commit-fail"), TestContext.Current.CancellationToken);

        Assert.NotNull(result.GitStatus);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(1, result.GitStatus.FilesChanged);
        Assert.Equal(staged, result.GitStatus.ChangedFiles);

        // Path-resolution assertions: git work dir, agent work dir, and prompt context all resolve to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Contains(configRepoDir, git.WorkDirs);
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    /// <summary>
    /// The improver's config-repo path is a SINGLE repository, so paths stay plain — no
    /// <c>repoName:</c> qualification prefix — and the shared 50-path cap still applies.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverManyFiles_AppliesCapAndKeepsPathsPlain()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        const int total = GitOperations.ChangedFilesMaxPaths + 12;
        var staged = Enumerable.Range(0, total).Select(i => $"agents/file{i}.agents.md").ToArray();
        var agentRunner = new MockAgentRunner();
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(staged, pushFails: true),
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        var result = await executor.ExecuteAsync(
            BuildImproverTask("improver-cap"), TestContext.Current.CancellationToken);

        Assert.NotNull(result.GitStatus);
        // Count reflects every changed file; the diagnostic list is capped.
        Assert.Equal(total, result.GitStatus!.FilesChanged);
        Assert.Equal(GitOperations.ChangedFilesMaxPaths, result.GitStatus.ChangedFiles.Count);
        Assert.Equal("agents/file0.agents.md", result.GitStatus.ChangedFiles[0]);
        // No repo-qualification prefix and no synthetic truncation marker.
        Assert.DoesNotContain(result.GitStatus.ChangedFiles, p => p.Contains(':'));
        Assert.DoesNotContain(result.GitStatus.ChangedFiles, p => p.Contains("more"));

        // Path-resolution assertions: git work dir, agent work dir, and prompt context all resolve to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Contains(configRepoDir, git.WorkDirs);
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    /// <summary>
    /// The config-repo staged-file query must use the NUL-delimited <c>-z</c> form so that
    /// filenames needing C-quoting are not mangled.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Improver_UsesNulDelimitedStagedFileQuery()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var agentRunner = new MockAgentRunner();
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(["agents/coder.agents.md"], pushFails: false),
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        await executor.ExecuteAsync(
            BuildImproverTask("improver-z-flag"), TestContext.Current.CancellationToken);

        Assert.Contains("diff --cached --name-only -z", git.GitCommands);

        // Path-resolution assertions: git work dir, agent work dir, and prompt context all resolve to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Contains(configRepoDir, git.WorkDirs);
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    /// <summary>
    /// A config-repo filename containing a space and a double quote — which plain
    /// <c>--name-only</c> would C-quote — is reported verbatim through the <c>-z</c> path.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverSpecialCharacterFilename_IsReportedVerbatim()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        string[] staged = ["agents/we ird\"name.agents.md", "agents/normal.agents.md"];
        var agentRunner = new MockAgentRunner();
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(staged, pushFails: true),
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        var result = await executor.ExecuteAsync(
            BuildImproverTask("improver-special"), TestContext.Current.CancellationToken);

        Assert.Equal(staged, result.GitStatus!.ChangedFiles);
        Assert.DoesNotContain(result.GitStatus.ChangedFiles, p => p.StartsWith('"'));

        // Path-resolution assertions: git work dir, agent work dir, and prompt context all resolve to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Contains(configRepoDir, git.WorkDirs);
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    // ── Improver worker-side log safety ───────────────────────────────────────

    /// <summary>
    /// Runs the improver with the given staged paths while capturing <see cref="Console.Out"/>,
    /// and returns the single worker log line that reports the changed filenames.
    /// </summary>
    private static async Task<(string LogLine, string ConfigRepoDir, MockAgentRunner AgentRunner)> CaptureImproverChangedFilesLogAsync(
        string taskId, IReadOnlyCollection<string> staged)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var agentRunner = new MockAgentRunner();
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(staged, pushFails: false),
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        var originalOut = Console.Out;
        using var captured = new StringWriter();
        string output;
        try
        {
            Console.SetOut(captured);
            await executor.ExecuteAsync(
                BuildImproverTask(taskId), TestContext.Current.CancellationToken);
            output = captured.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // Split on EVERY line-breaking convention so an injected break would surface as
        // its own entry rather than hiding inside the matched line.
        var lines = output.Split(['\n', '\r', '\u0085', '\u2028', '\u2029']);
        var logLine = Array.Find(lines, l => l.Contains("Improver changed"));
        Assert.NotNull(logLine);
        return (logLine!, configRepoDir, agentRunner);
    }

    /// <summary>
    /// The worker-side Improver log must stay BOUNDED: with far more changed files than the
    /// display cap it renders at most <see cref="TaskExecutor.ImproverLogMaxPaths"/> paths and
    /// reports the remainder as a count. The true total is still stated.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverManyFiles_WorkerLogIsBounded()
    {
        const int total = 62;
        var staged = Enumerable.Range(0, total).Select(i => $"agents/file{i:D3}.agents.md").ToArray();

        var (logLine, configRepoDir, agentRunner) = await CaptureImproverChangedFilesLogAsync("improver-log-bounded", staged);

        // The true total is reported.
        Assert.Contains($"Improver changed {total} file(s)", logLine);

        // Only the display cap number of paths is rendered.
        var renderedPaths = staged.Where(p => logLine.Contains(p)).ToList();
        Assert.Equal(TaskExecutor.ImproverLogMaxPaths, renderedPaths.Count);

        // The omitted remainder is reported as a count, not as paths.
        var omitted = total - TaskExecutor.ImproverLogMaxPaths;
        Assert.Contains($"(+{omitted} more)", logLine);

        // The first path is present and a path beyond the cap is NOT.
        Assert.Contains("agents/file000.agents.md", logLine);
        Assert.DoesNotContain("agents/file061.agents.md", logLine);

        // Path-resolution assertion: the improver's working directory resolves to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    /// <summary>
    /// With fewer files than the display cap, every path is shown and no
    /// <c>(+N more)</c> suffix is emitted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverFewFiles_WorkerLogShowsAllPathsWithoutMoreSuffix()
    {
        string[] staged = ["agents/coder.agents.md", "agents/tester.agents.md"];

        var (logLine, configRepoDir, agentRunner) = await CaptureImproverChangedFilesLogAsync("improver-log-few", staged);

        Assert.Contains("Improver changed 2 file(s)", logLine);
        Assert.Contains("agents/coder.agents.md", logLine);
        Assert.Contains("agents/tester.agents.md", logLine);
        Assert.DoesNotContain("more)", logLine);

        // Path-resolution assertion: the improver's working directory resolves to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    /// <summary>
    /// REGRESSION for the iteration-2 finding: the <c>-z</c> query returns UNQUOTED paths, so a
    /// legal staged filename containing control characters or Unicode line separators must be
    /// sanitized before it reaches the worker log — otherwise it forges extra log lines.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverControlCharacterFilenames_WorkerLogIsSanitized()
    {
        // NUL (0x00) is the `-z` record delimiter, so it can never occur INSIDE a path.
        // Every other C0 control character legitimately can.
        var c0 = new string([.. Enumerable.Range(0x01, 0x1F).Select(i => (char)i)]);
        string[] staged =
        [
            $"agents/evil{c0}\u007F\u0085\u2028\u2029forged.agents.md",
            "agents/x\nERROR forged log entry.agents.md",
            "agents/tab\there.agents.md",
            "agents/normal.agents.md",
        ];

        var (logLine, configRepoDir, agentRunner) = await CaptureImproverChangedFilesLogAsync("improver-log-sanitized", staged);

        // No raw control character of ANY kind survived into the log line.
        Assert.DoesNotContain(logLine, char.IsControl);
        Assert.DoesNotContain('\u2028', logLine);
        Assert.DoesNotContain('\u2029', logLine);

        // The legal text around the control characters is preserved, proving the path was
        // sanitized in place rather than dropped.
        Assert.Contains("agents/evil", logLine);
        Assert.Contains("forged.agents.md", logLine);
        Assert.Contains("agents/normal.agents.md", logLine);
        Assert.Contains("Improver changed 4 file(s)", logLine);

        // Path-resolution assertion: the improver's working directory resolves to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    /// <summary>
    /// A filename crafted to forge a whole extra log line must NOT produce one: the captured
    /// console output contains no line that looks like an independent log entry.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverInjectedLogLine_ProducesNoExtraLogEntry()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        string[] staged = ["agents/a\n[Task] ERROR totally forged failure.agents.md"];
        var agentRunner = new MockAgentRunner();
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(staged, pushFails: false),
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        var originalOut = Console.Out;
        using var captured = new StringWriter();
        string output;
        try
        {
            Console.SetOut(captured);
            await executor.ExecuteAsync(
                BuildImproverTask("improver-log-injection"), TestContext.Current.CancellationToken);
            output = captured.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var lines = output.Split(['\n', '\r', '\u0085', '\u2028', '\u2029'],
            StringSplitOptions.RemoveEmptyEntries);

        // The forged text never appears at the start of its own line.
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("[Task] ERROR totally forged"));

        // It survives only as sanitized text INSIDE the single changed-files line.
        var changedLine = Array.Find(lines, l => l.Contains("Improver changed"));
        Assert.NotNull(changedLine);
        Assert.Contains("totally forged failure.agents.md", changedLine!);
        Assert.DoesNotContain(changedLine!, char.IsControl);

        // Path-resolution assertions: git work dir, agent work dir, and prompt context all resolve to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Contains(configRepoDir, git.WorkDirs);
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    /// <summary>
    /// The log display cap is independent of, and stricter than, the domain list cap: the
    /// <c>ChangedFiles</c> diagnostic list is still governed by
    /// <see cref="GitOperations.ChangedFilesMaxPaths"/> and carries UNSANITIZED real paths.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverLogCap_DoesNotAffectDomainChangedFiles()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        const int total = 62;
        var staged = Enumerable.Range(0, total).Select(i => $"agents/file{i:D3}.agents.md").ToArray();
        var agentRunner = new MockAgentRunner();
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(staged, pushFails: true),
        };
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: configRepoDir);

        var result = await executor.ExecuteAsync(
            BuildImproverTask("improver-log-vs-domain"), TestContext.Current.CancellationToken);

        // Domain list uses the 50-path domain cap, NOT the 10-path log display cap.
        Assert.Equal(GitOperations.ChangedFilesMaxPaths, result.GitStatus!.ChangedFiles.Count);
        Assert.NotEqual(TaskExecutor.ImproverLogMaxPaths, result.GitStatus.ChangedFiles.Count);
        Assert.Equal(total, result.GitStatus.FilesChanged);
        // And it still contains no synthetic truncation marker.
        Assert.DoesNotContain(result.GitStatus.ChangedFiles, p => p.Contains("more"));

        // Path-resolution assertions: git work dir, agent work dir, and prompt context all resolve to the injected agents path.
        var expectedAgentsDir = Path.Combine(configRepoDir, "agents");
        Assert.Contains(configRepoDir, git.WorkDirs);
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    /// <summary>
    /// Regression: the improver's working directory and context header resolve to
    /// {configRepoDir}/agents. Uses a non-existent temp path so the test never reads
    /// real agents.md files from the CI/test environment.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImproverInjectedPath_ResolvesToAgentsDir()
    {
        var git = new MockGitOperations { FilesChanged = 0 };
        var agentRunner = new MockAgentRunner();
        // Use a non-existent config repo dir so EnsureAgentsMdWithinLimitsAsync returns
        // early (Directory.Exists check) — no real agents.md files are read.
        var tempConfigRepo = Path.Combine(Path.GetTempPath(), $"test-config-repo-{Guid.NewGuid():N}");
        var executor = new TaskExecutor(agentRunner, gitOperations: git, configRepoDir: tempConfigRepo);

        var task = new WorkTask
        {
            TaskId = "test-improver-injected-path",
            GoalId = "goal-improver-injected-path",
            GoalDescription = "Test goal",
            Prompt = "Test prompt",
            Role = WorkerRole.Improver,
            Repositories = [new TargetRepository { Name = "test-repo", Url = "https://github.com/test/test.git", DefaultBranch = "main" }],
        };

        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        Assert.Equal("PASS", result.Metrics!.Verdict);
        var expectedAgentsDir = Path.Combine(tempConfigRepo, "agents");
        Assert.Equal(expectedAgentsDir, agentRunner.LastWorkDir);
        Assert.Contains($"Working directory: {expectedAgentsDir}", agentRunner.LastPrompt);
    }

    // ── Changed-file path aggregation ─────────────────────────────────────────

    private static WorkTask BuildTask(string id, params TargetRepository[] repos) => new()
    {
        TaskId = id,
        GoalId = $"goal-{id}",
        GoalDescription = "Test goal",
        Prompt = "Test prompt",
        Role = WorkerRole.Coder,
        Repositories = [.. repos],
        BranchInfo = new BranchSpec { Action = BranchAction.Create, BaseBranch = "main", FeatureBranch = "feature-branch" },
    };

    private static TargetRepository Repo(string name) =>
        new() { Name = name, Url = $"https://github.com/test/{name}.git", DefaultBranch = "main" };

    /// <summary>
    /// A single repository with changes contributes its plain repository-relative paths —
    /// no repository-name qualification prefix.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SingleRepo_UsesPlainRelativePaths()
    {
        var git = new MockGitOperations();
        git.StatusByRepoName["repoA"] = new GitChangeSummary
        {
            FilesChanged = 2,
            Insertions = 10,
            Deletions = 3,
            ChangedFiles = ["src/Services/Foo.cs", "tests/FooTests.cs"],
        };

        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);
        var result = await executor.ExecuteAsync(
            BuildTask("task-single-repo", Repo("repoA")), TestContext.Current.CancellationToken);

        Assert.NotNull(result.GitStatus);
        Assert.Equal(2, result.GitStatus!.FilesChanged);
        Assert.Equal(["src/Services/Foo.cs", "tests/FooTests.cs"], result.GitStatus.ChangedFiles);
        Assert.DoesNotContain(result.GitStatus.ChangedFiles, p => p.StartsWith("repoA:"));
    }

    /// <summary>
    /// When MULTIPLE repositories have changes, counts AND paths accumulate across all of
    /// them and each path is qualified with its repository name.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MultiRepo_AccumulatesCountsAndQualifiesPaths()
    {
        var git = new MockGitOperations();
        git.StatusByRepoName["repoA"] = new GitChangeSummary
        {
            FilesChanged = 2,
            Insertions = 10,
            Deletions = 3,
            ChangedFiles = ["file1.cs", "src/A.cs"],
        };
        git.StatusByRepoName["repoB"] = new GitChangeSummary
        {
            FilesChanged = 1,
            Insertions = 5,
            Deletions = 1,
            ChangedFiles = ["file2.cs"],
        };

        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);
        var result = await executor.ExecuteAsync(
            BuildTask("task-multi-repo", Repo("repoA"), Repo("repoB")), TestContext.Current.CancellationToken);

        Assert.NotNull(result.GitStatus);
        // Counts accumulate across ALL changed repos (previously only the first repo counted)
        Assert.Equal(3, result.GitStatus!.FilesChanged);
        Assert.Equal(15, result.GitStatus.Insertions);
        Assert.Equal(4, result.GitStatus.Deletions);

        Assert.Equal(
            ["repoA:file1.cs", "repoA:src/A.cs", "repoB:file2.cs"],
            result.GitStatus.ChangedFiles);
    }

    /// <summary>
    /// Repositories with no changes must not contribute counts or paths, and the
    /// single remaining changed repo keeps plain (unqualified) paths.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnchangedRepo_ContributesNothingAndKeepsPathsPlain()
    {
        var git = new MockGitOperations();
        git.StatusByRepoName["repoA"] = new GitChangeSummary
        {
            FilesChanged = 1,
            Insertions = 4,
            Deletions = 0,
            ChangedFiles = ["only.cs"],
        };
        git.StatusByRepoName["repoB"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };

        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);
        var result = await executor.ExecuteAsync(
            BuildTask("task-one-changed", Repo("repoA"), Repo("repoB")), TestContext.Current.CancellationToken);

        Assert.NotNull(result.GitStatus);
        Assert.Equal(1, result.GitStatus!.FilesChanged);
        Assert.Equal(["only.cs"], result.GitStatus.ChangedFiles);
    }

    /// <summary>
    /// If ANY repository's push fails, the aggregated <c>Pushed</c> flag is false.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MultiRepo_AnyPushFailure_SetsPushedFalse()
    {
        var git = new MockGitOperations();
        git.StatusByRepoName["repoA"] = new GitChangeSummary { FilesChanged = 1, ChangedFiles = ["a.cs"] };
        git.StatusByRepoName["repoB"] = new GitChangeSummary { FilesChanged = 1, ChangedFiles = ["b.cs"] };
        git.PushFailsForRepos.Add("repoB");

        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);
        var result = await executor.ExecuteAsync(
            BuildTask("task-push-partial", Repo("repoA"), Repo("repoB")), TestContext.Current.CancellationToken);

        Assert.NotNull(result.GitStatus);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(["repoA:a.cs", "repoB:b.cs"], result.GitStatus.ChangedFiles);
    }

    /// <summary>
    /// The global cap <see cref="GitOperations.ChangedFilesMaxPaths"/> truncates the
    /// aggregated path list, while the file COUNT still reflects every changed file.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AppliesGlobalChangedFilesCap()
    {
        const int total = GitOperations.ChangedFilesMaxPaths + 25;
        var git = new MockGitOperations();
        git.StatusByRepoName["repoA"] = new GitChangeSummary
        {
            FilesChanged = total,
            Insertions = total,
            Deletions = 0,
            ChangedFiles = [.. Enumerable.Range(0, total).Select(i => $"src/File{i}.cs")],
        };

        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);
        var result = await executor.ExecuteAsync(
            BuildTask("task-cap", Repo("repoA")), TestContext.Current.CancellationToken);

        Assert.NotNull(result.GitStatus);
        Assert.Equal(total, result.GitStatus!.FilesChanged);
        Assert.Equal(GitOperations.ChangedFilesMaxPaths, result.GitStatus.ChangedFiles.Count);
        Assert.Equal("src/File0.cs", result.GitStatus.ChangedFiles[0]);
        // No synthetic truncation marker is ever placed in the list itself
        Assert.DoesNotContain(result.GitStatus.ChangedFiles, p => p.Contains("more"));
    }

    // ── Read-only role classification (reviewer baseline + aggregate Pushed) ────

    /// <summary>
    /// Builds a <see cref="MockGitOperations.GitCommandResponder"/> that returns successive
    /// SHA values for successive <c>rev-parse HEAD</c> calls. A null SHA simulates a capture
    /// failure (non-zero exit code). Call order is deterministic: all start captures first
    /// (in repo order), then all final captures (in repo order).
    /// </summary>
    private static Func<string, (int ExitCode, string Stdout, string Stderr)?> RevParseResponder(
        params string?[] shas)
    {
        var idx = 0;
        return args =>
        {
            if (args != "rev-parse HEAD")
                return null;
            var sha = idx < shas.Length ? shas[idx] : null;
            idx++;
            if (sha is null)
                return (1, "", "fatal: not a git repository");
            return (0, sha + "\n", "");
        };
    }

    /// <summary>
    /// Builds a reviewer <see cref="WorkTask"/> with a checkout branch action.
    /// </summary>
    private static WorkTask BuildReviewerTask(string id, params TargetRepository[] repos) => new()
    {
        TaskId = id,
        GoalId = $"goal-{id}",
        GoalDescription = "Test goal",
        Prompt = "Review the changes",
        Role = WorkerRole.Reviewer,
        Repositories = [.. repos],
        BranchInfo = new BranchSpec { Action = BranchAction.Checkout, BaseBranch = "main", FeatureBranch = "feature-branch" },
    };

    /// <summary>
    /// Executes the task while capturing <see cref="Console.Out"/>, returning the result
    /// and the captured output string.
    /// </summary>
    private static async Task<(TaskResult Result, string Output)> ExecuteWithConsoleCaptureAsync(
        TaskExecutor executor, WorkTask task)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        TaskResult result;
        try
        {
            Console.SetOut(sw);
            result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return (result, sw.ToString());
    }

    /// <summary>
    /// Counts the number of fully-prefixed <c>[Task] WARN:</c> occurrences in the captured
    /// console output.
    /// </summary>
    private static int CountTaskWarns(string output) =>
        output.Split("[Task] WARN:").Length - 1;

    /// <summary>
    /// Class A (suppress): reviewer, one repo — HEAD unmoved, FilesChanged > 0.
    /// The aggregate Pushed is true (no Class-B, usable baseline) and the changed-file
    /// paths are still accumulated. No Class-C note.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerClassA_SuppressesPushWarning_AndKeepsChangedPaths()
    {
        var git = new MockGitOperations
        {
            GitCommandResponder = RevParseResponder("aaa111", "aaa111"), // start == final (unmoved)
        };
        git.StatusByRepoName["repoA"] = new GitChangeSummary
        {
            FilesChanged = 2,
            Insertions = 10,
            Deletions = 3,
            ChangedFiles = ["src/Foo.cs", "tests/FooTests.cs"],
        };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask("task-classA-changes", Repo("repoA")));

        Assert.NotNull(result.GitStatus);
        Assert.True(result.GitStatus!.Pushed);
        Assert.Equal(2, result.GitStatus.FilesChanged);
        Assert.Contains("src/Foo.cs", result.GitStatus.ChangedFiles);
        Assert.Contains("tests/FooTests.cs", result.GitStatus.ChangedFiles);
        Assert.Equal(0, CountTaskWarns(output));
    }

    /// <summary>
    /// Class A (suppress): reviewer, one repo — HEAD unmoved, FilesChanged == 0.
    /// Pushed is true, no changed files, no Class-C note.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerClassA_NoChanges_PushesTrueAndNoWarn()
    {
        var git = new MockGitOperations
        {
            GitCommandResponder = RevParseResponder("aaa111", "aaa111"),
        };
        git.StatusByRepoName["repoA"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask("task-classA-nochange", Repo("repoA")));

        Assert.NotNull(result.GitStatus);
        Assert.True(result.GitStatus!.Pushed);
        Assert.Equal(0, result.GitStatus.FilesChanged);
        Assert.Empty(result.GitStatus.ChangedFiles);
        Assert.Equal(0, CountTaskWarns(output));
    }

    /// <summary>
    /// Class B (dominates): reviewer, one repo — HEAD moved, FilesChanged > 0.
    /// Pushed is false, changed paths are present. No Class-C note (it's Class B, not C).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerClassB_Dominates_PushesFalseAndNoClassCNote()
    {
        var git = new MockGitOperations
        {
            GitCommandResponder = RevParseResponder("aaa111", "bbb222"), // moved
        };
        git.StatusByRepoName["repoA"] = new GitChangeSummary
        {
            FilesChanged = 1,
            Insertions = 5,
            Deletions = 2,
            ChangedFiles = ["src/Bar.cs"],
        };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask("task-classB", Repo("repoA")));

        Assert.NotNull(result.GitStatus);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(1, result.GitStatus.FilesChanged);
        Assert.Contains("src/Bar.cs", result.GitStatus.ChangedFiles);
        Assert.Equal(0, CountTaskWarns(output));
    }

    /// <summary>
    /// Class C: reviewer, one repo — HEAD moved, FilesChanged == 0.
    /// Pushed is true, no files changed, and the fully-prefixed rendered
    /// <c>[Task] WARN: Task {id}: read-only role moved HEAD ...</c> note appears EXACTLY ONCE.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerClassC_PushesTrueAndEmitsWarnNoteOnce()
    {
        const string taskId = "task-classC";
        const string repoName = "repoA";
        var git = new MockGitOperations
        {
            GitCommandResponder = RevParseResponder("aaa111", "bbb222"), // moved
        };
        git.StatusByRepoName[repoName] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask(taskId, Repo(repoName)));

        Assert.NotNull(result.GitStatus);
        Assert.True(result.GitStatus!.Pushed);
        Assert.Equal(0, result.GitStatus.FilesChanged);

        // Assert the FULLY-PREFIXED rendered string, not just the unprefixed message.
        var expectedNote =
            $"[Task] WARN: Task {taskId}: read-only role moved HEAD during its run in repository {repoName} (no net diff vs base)";
        Assert.Contains(expectedNote, output);
        Assert.Equal(1, CountTaskWarns(output));
    }

    /// <summary>
    /// Class C with B present: reviewer, two repos — Repo1 is Class B (moved, FilesChanged > 0),
    /// Repo2 is Class C (moved, FilesChanged == 0). Pushed is false (B dominates), but the
    /// Class-C note for Repo2 still fires EXACTLY ONCE. ChangedFiles contains Repo1's paths.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerClassBWithC_BDominates_CNoteStillFires()
    {
        const string taskId = "task-BwithC";
        var git = new MockGitOperations
        {
            // Repo1 start, Repo2 start, Repo1 final, Repo2 final
            GitCommandResponder = RevParseResponder("aaa111", "ccc333", "bbb222", "ddd444"),
        };
        git.StatusByRepoName["repo1"] = new GitChangeSummary
        {
            FilesChanged = 1,
            Insertions = 3,
            Deletions = 0,
            ChangedFiles = ["src/B1.cs"],
        };
        git.StatusByRepoName["repo2"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask(taskId, Repo("repo1"), Repo("repo2")));

        Assert.NotNull(result.GitStatus);
        Assert.False(result.GitStatus!.Pushed); // B dominates
        Assert.Contains("src/B1.cs", result.GitStatus.ChangedFiles);

        var expectedNote =
            $"[Task] WARN: Task {taskId}: read-only role moved HEAD during its run in repository repo2 (no net diff vs base)";
        Assert.Contains(expectedNote, output);
        Assert.Equal(1, CountTaskWarns(output)); // Only the Class-C note for repo2
    }

    /// <summary>
    /// Mixed A+C: reviewer, two repos — Repo1 is Class A (unmoved, FilesChanged == 0),
    /// Repo2 is Class C (moved, FilesChanged == 0). Pushed is true (no B, at least 1 usable
    /// baseline). Class-C note fires for Repo2 ONCE. No note for Repo1.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerMixedA_C_PushesTrue_CNoteForRepo2Only()
    {
        const string taskId = "task-mixedAC";
        var git = new MockGitOperations
        {
            // Repo1 start, Repo2 start, Repo1 final, Repo2 final
            GitCommandResponder = RevParseResponder("aaa111", "ccc333", "aaa111", "ddd444"),
        };
        git.StatusByRepoName["repo1"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        git.StatusByRepoName["repo2"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask(taskId, Repo("repo1"), Repo("repo2")));

        Assert.NotNull(result.GitStatus);
        Assert.True(result.GitStatus!.Pushed); // no B, at least 1 usable baseline

        var expectedNote =
            $"[Task] WARN: Task {taskId}: read-only role moved HEAD during its run in repository repo2 (no net diff vs base)";
        Assert.Contains(expectedNote, output);
        Assert.Equal(1, CountTaskWarns(output)); // Only for repo2, not repo1
    }

    /// <summary>
    /// Capture-failure precedence: reviewer, one repo — start capture succeeds, final capture
    /// fails, FilesChanged > 0. Treated as Class B (Pushed == false). No Class-C note
    /// (not Class C because FilesChanged > 0).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerCaptureFailsWithChanges_PushesFalse_NoClassCNote()
    {
        var git = new MockGitOperations
        {
            // start succeeds, final fails
            GitCommandResponder = RevParseResponder("aaa111", null),
        };
        git.StatusByRepoName["repoA"] = new GitChangeSummary
        {
            FilesChanged = 2,
            Insertions = 8,
            Deletions = 1,
            ChangedFiles = ["src/A.cs", "src/B.cs"],
        };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask("task-capfail-changes", Repo("repoA")));

        Assert.NotNull(result.GitStatus);
        Assert.False(result.GitStatus!.Pushed); // Class B
        Assert.Equal(2, result.GitStatus.FilesChanged);
        Assert.Contains("src/A.cs", result.GitStatus.ChangedFiles);
        Assert.Equal(0, CountTaskWarns(output)); // No Class-C note
    }

    /// <summary>
    /// Capture-failure with no diff: reviewer, one repo — both captures fail, FilesChanged == 0.
    /// Pushed is false (no usable baseline, no FilesChanged). No Class-C note. No manufactured paths.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerCaptureFailsNoDiff_PushesFalse_NoClassCNote()
    {
        var git = new MockGitOperations
        {
            GitCommandResponder = RevParseResponder(null, null), // both fail
        };
        git.StatusByRepoName["repoA"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask("task-capfail-nodiff", Repo("repoA")));

        Assert.NotNull(result.GitStatus);
        Assert.False(result.GitStatus!.Pushed); // no usable baseline, no FilesChanged
        Assert.Equal(0, result.GitStatus.FilesChanged);
        Assert.Empty(result.GitStatus.ChangedFiles); // no manufactured paths
        Assert.Equal(0, CountTaskWarns(output)); // No Class-C note
    }

    /// <summary>
    /// Fully-unavailable one-sided: reviewer, two repos — both start captures fail, final
    /// captures succeed, FilesChanged == 0 for both. Pushed is false (no usable baseline
    /// pair anywhere, no FilesChanged). No Class-C notes.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerFullyUnavailableOneSided_PushesFalse_NoClassCNotes()
    {
        var git = new MockGitOperations
        {
            // Repo1 start (fail), Repo2 start (fail), Repo1 final (ok), Repo2 final (ok)
            GitCommandResponder = RevParseResponder(null, null, "bbb222", "ddd444"),
        };
        git.StatusByRepoName["repo1"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        git.StatusByRepoName["repo2"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask("task-unavail-one-sided", Repo("repo1"), Repo("repo2")));

        Assert.NotNull(result.GitStatus);
        Assert.False(result.GitStatus!.Pushed); // no usable baseline pair anywhere
        Assert.Equal(0, result.GitStatus.FilesChanged);
        Assert.Equal(0, CountTaskWarns(output)); // No Class-C notes
    }

    /// <summary>
    /// Fully-unavailable two-sided: reviewer, two repos — all captures fail, FilesChanged == 0
    /// for both. Pushed is false. No Class-C notes.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerFullyUnavailableTwoSided_PushesFalse_NoClassCNotes()
    {
        var git = new MockGitOperations
        {
            // All four captures fail
            GitCommandResponder = RevParseResponder(null, null, null, null),
        };
        git.StatusByRepoName["repo1"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        git.StatusByRepoName["repo2"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask("task-unavail-two-sided", Repo("repo1"), Repo("repo2")));

        Assert.NotNull(result.GitStatus);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(0, result.GitStatus.FilesChanged);
        Assert.Equal(0, CountTaskWarns(output)); // No Class-C notes
    }

    /// <summary>
    /// Mixed A/C + capture-failure-with-no-diff: reviewer, three repos — Repo1 Class A (unmoved,
    /// FilesChanged == 0), Repo2 Class C (moved, FilesChanged == 0), Repo3 both captures fail
    /// (FilesChanged == 0). Pushed is true (no B, at least 1 usable baseline from A or C).
    /// Class-C note fires for Repo2 only ONCE. No note for Repo3.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerMixedAC_WithCaptureFailure_PushesTrue_CNoteForRepo2Only()
    {
        const string taskId = "task-mixedAC-capfail";
        var git = new MockGitOperations
        {
            // Repo1 start, Repo2 start, Repo3 start, Repo1 final, Repo2 final, Repo3 final
            GitCommandResponder = RevParseResponder("aaa111", "ccc333", null, "aaa111", "ddd444", null),
        };
        git.StatusByRepoName["repo1"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        git.StatusByRepoName["repo2"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        git.StatusByRepoName["repo3"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask(taskId, Repo("repo1"), Repo("repo2"), Repo("repo3")));

        Assert.NotNull(result.GitStatus);
        Assert.True(result.GitStatus!.Pushed); // no B, at least 1 usable baseline

        var expectedNote =
            $"[Task] WARN: Task {taskId}: read-only role moved HEAD during its run in repository repo2 (no net diff vs base)";
        Assert.Contains(expectedNote, output);
        Assert.Equal(1, CountTaskWarns(output)); // Only repo2, not repo1 or repo3
    }

    /// <summary>
    /// Push-allowed role regression: coder, one repo, FilesChanged > 0, push throws.
    /// Pushed is false, changed paths present. This is existing behavior — no change.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CoderPushFails_PushesFalse_AndHasChangedPaths()
    {
        var git = new MockGitOperations
        {
            PushShouldFail = true,
            PushErrorMessage = "Failed to push branch 'feature-branch': Permission denied",
        };
        git.StatusByRepoName["repoA"] = new GitChangeSummary
        {
            FilesChanged = 3,
            Insertions = 10,
            Deletions = 2,
            ChangedFiles = ["src/A.cs", "src/B.cs", "src/C.cs"],
        };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var result = await executor.ExecuteAsync(
            BuildTask("task-coder-push-fail", Repo("repoA")),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.GitStatus);
        Assert.False(result.GitStatus!.Pushed);
        Assert.True(result.GitStatus.FilesChanged > 0);
        Assert.NotEmpty(result.GitStatus.ChangedFiles);
    }

    /// <summary>
    /// Push-allowed role regression: coder, one repo, FilesChanged > 0, push succeeds.
    /// Pushed is true, FilesChanged > 0. No [Task] WARN: note.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CoderPushSucceeds_PushesTrue_NoWarnNote()
    {
        var git = new MockGitOperations
        {
            PushShouldFail = false,
        };
        git.StatusByRepoName["repoA"] = new GitChangeSummary
        {
            FilesChanged = 2,
            Insertions = 5,
            Deletions = 1,
            ChangedFiles = ["src/X.cs", "src/Y.cs"],
        };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildTask("task-coder-push-ok", Repo("repoA")));

        Assert.NotNull(result.GitStatus);
        Assert.True(result.GitStatus!.Pushed);
        Assert.True(result.GitStatus.FilesChanged > 0);
        Assert.Equal(0, CountTaskWarns(output));
    }

    /// <summary>
    /// Non-reviewer regression: coder, one repo, FilesChanged == 0. GitStatus is a default
    /// GitChangeSummary with Pushed == false (existing behavior — default bool). No baseline
    /// capture happened (no [Task] WARN: notes).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CoderNoChanges_DefaultGitStatus_PushesFalse_NoWarnNotes()
    {
        var git = new MockGitOperations
        {
            FilesChanged = 0,
        };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildTask("task-coder-nochange", Repo("repoA")));

        // No changes → aggregatedStatus is null → default GitChangeSummary (Pushed = false)
        Assert.NotNull(result.GitStatus);
        Assert.False(result.GitStatus!.Pushed); // default bool
        Assert.Equal(0, result.GitStatus.FilesChanged);
        Assert.Empty(result.GitStatus.ChangedFiles);
        Assert.Equal(0, CountTaskWarns(output)); // No baseline capture for non-reviewer
    }

    // ── Thrown SHA-capture exception tests (iteration 2: silent catch) ──────────

    /// <summary>
    /// Builds a <see cref="MockGitOperations.GitCommandThrower"/> that throws on the Nth
    /// <c>rev-parse HEAD</c> call (1-based) and returns null for all other rev-parse calls.
    /// </summary>
    private static Func<string, Exception?> RevParseThrower(int throwOnCall, Exception ex)
    {
        var revParseCount = 0;
        return args =>
        {
            if (args != "rev-parse HEAD")
                return null;
            revParseCount++;
            return revParseCount == throwOnCall ? ex : null;
        };
    }

    /// <summary>
    /// Test 1: Start baseline capture THROWS, final capture succeeds, FilesChanged > 0.
    /// The coder's iteration 2 fix removed <c>_log.Warn</c> from the catch block — the capture
    /// failure must be silent. Classification: capture failed + files changed → Class B.
    /// Assert: Pushed == false, no [Task] WARN: in captured Console.Out, changed paths present.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerStartCaptureThrows_FilesChanged_PushesFalse_SilentCatch()
    {
        var git = new MockGitOperations
        {
            GitCommandThrower = RevParseThrower(1, new InvalidOperationException("git not found")),
            GitCommandResponder = RevParseResponder(null, "bbb222"), // call 1 throws, call 2 succeeds
        };
        git.StatusByRepoName["repoA"] = new GitChangeSummary
        {
            FilesChanged = 2,
            Insertions = 8,
            Deletions = 1,
            ChangedFiles = ["src/A.cs", "src/B.cs"],
        };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask("task-start-throw-changes", Repo("repoA")));

        Assert.NotNull(result.GitStatus);
        Assert.False(result.GitStatus!.Pushed); // Class B: capture failed + files changed
        Assert.Equal(2, result.GitStatus.FilesChanged);
        Assert.Contains("src/A.cs", result.GitStatus.ChangedFiles);
        Assert.Contains("src/B.cs", result.GitStatus.ChangedFiles);
        // The coder removed _log.Warn from the catch — capture failure must be SILENT.
        Assert.Equal(0, CountTaskWarns(output));
    }

    /// <summary>
    /// Test 2: Final SHA capture THROWS, start capture succeeds, FilesChanged == 0.
    /// Classification: capture-failure-with-no-diff → Pushed == false, no note, no paths.
    /// Assert: no [Task] WARN: in captured output (silent catch for final capture too).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerFinalCaptureThrows_NoChanges_PushesFalse_SilentCatch()
    {
        var git = new MockGitOperations
        {
            GitCommandThrower = RevParseThrower(2, new InvalidOperationException("git not found")),
            GitCommandResponder = RevParseResponder("aaa111", null), // call 1 succeeds, call 2 throws
        };
        git.StatusByRepoName["repoA"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask("task-final-throw-nochange", Repo("repoA")));

        Assert.NotNull(result.GitStatus);
        Assert.False(result.GitStatus!.Pushed); // no usable baseline pair, no FilesChanged
        Assert.Equal(0, result.GitStatus.FilesChanged);
        Assert.Empty(result.GitStatus.ChangedFiles);
        // Silent catch — no warning for capture-failure-with-no-diff.
        Assert.Equal(0, CountTaskWarns(output));
    }

    /// <summary>
    /// Test 3: Start baseline capture THROWS, final capture succeeds, FilesChanged == 0.
    /// The KEY test the reviewer demanded: a capture-failed repo with FilesChanged == 0 must
    /// NOT produce any worker warning. Assert: Pushed == false, no [Task] WARN:, empty ChangedFiles.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerStartCaptureThrows_NoChanges_PushesFalse_NoWarn()
    {
        var git = new MockGitOperations
        {
            GitCommandThrower = RevParseThrower(1, new InvalidOperationException("git not found")),
            GitCommandResponder = RevParseResponder(null, "bbb222"), // call 1 throws, call 2 succeeds
        };
        git.StatusByRepoName["repoA"] = new GitChangeSummary { FilesChanged = 0, ChangedFiles = [] };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var (result, output) = await ExecuteWithConsoleCaptureAsync(
            executor, BuildReviewerTask("task-start-throw-nochange", Repo("repoA")));

        Assert.NotNull(result.GitStatus);
        Assert.False(result.GitStatus!.Pushed); // no usable baseline pair
        Assert.Equal(0, result.GitStatus.FilesChanged);
        Assert.Empty(result.GitStatus.ChangedFiles);
        // The critical assertion: capture-failed + no diff must NOT produce any warning.
        Assert.Equal(0, CountTaskWarns(output));
    }

    // ── Cancellation during SHA capture (iteration 2: OperationCanceledException re-throw) ──

    /// <summary>
    /// Test 4: Cancellation during start baseline capture — the mock throws
    /// <see cref="OperationCanceledException"/> for the START rev-parse call. The production
    /// code's inner <c>catch (OperationCanceledException) { throw; }</c> guard must re-throw it
    /// so the generic <c>catch (Exception)</c> does NOT swallow it. The outer
    /// <see cref="TaskExecutor.ExecuteAsync"/> handler then catches it and returns a
    /// <see cref="TaskOutcome.Failed"/> (or <see cref="TaskOutcome.Cancelled"/>) result —
    /// the task must NOT silently complete with a <c>Pushed</c> value.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerStartCaptureCancelled_DoesNotSilentlyComplete()
    {
        using var cts = new CancellationTokenSource();
        var git = new MockGitOperations
        {
            GitCommandThrower = RevParseThrower(1, new OperationCanceledException(cts.Token)),
            GitCommandResponder = RevParseResponder(null, "bbb222"),
        };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var result = await executor.ExecuteAsync(
            BuildReviewerTask("task-start-cancel", Repo("repoA")),
            TestContext.Current.CancellationToken);

        // The OperationCanceledException was re-thrown by the inner guard and caught by the
        // outer ExecuteAsync handler — the task did NOT silently complete as Completed with
        // a Pushed value. It must be either Cancelled or Failed.
        Assert.NotEqual(TaskOutcome.Completed, result.Status);
        // No GitStatus was computed — the cancellation happened before classification.
        Assert.Null(result.GitStatus);
    }

    /// <summary>
    /// Test 5: Cancellation during final SHA capture — the mock throws
    /// <see cref="OperationCanceledException"/> for the FINAL rev-parse call. The production
    /// code's inner <c>catch (OperationCanceledException) { throw; }</c> guard must re-throw it.
    /// The task must NOT silently complete with a wrong <c>Pushed</c> value.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReviewerFinalCaptureCancelled_DoesNotSilentlyComplete()
    {
        using var cts = new CancellationTokenSource();
        var git = new MockGitOperations
        {
            GitCommandThrower = RevParseThrower(2, new OperationCanceledException(cts.Token)),
            GitCommandResponder = RevParseResponder("aaa111", null), // call 1 succeeds, call 2 throws OCE
        };
        var executor = new TaskExecutor(new MockAgentRunner(), gitOperations: git);

        var result = await executor.ExecuteAsync(
            BuildReviewerTask("task-final-cancel", Repo("repoA")),
            TestContext.Current.CancellationToken);

        // The OperationCanceledException was re-thrown by the inner guard and caught by the
        // outer ExecuteAsync handler — the task did NOT silently complete as Completed with
        // a Pushed value. It must be either Cancelled or Failed.
        Assert.NotEqual(TaskOutcome.Completed, result.Status);
        Assert.Null(result.GitStatus);
    }

    // ── Config-repo git routing: the SEAM path vs the LEGACY path (slice 2c-c-i) ──────

    /// <summary>The improver's config-repo commit message, identical on BOTH dispatch paths.</summary>
    private const string ImproverCommitMessage =
        "Improve agents.md files (automated by CopilotHive Improver)";

    /// <summary>
    /// An ELIGIBLE resolved config repo URL (HTTPS, host <c>github.com</c>, implicit port 443),
    /// so transport commands take the seam's Branch A: origin state machine, credential
    /// resolution and the canonicalized explicit-origin launch.
    /// </summary>
    private const string SeamEligibleUrl = "https://github.com/org/config-repo.git";

    /// <summary>The credential the test's resolver hands the seam for eligible transport.</summary>
    private const string SeamCredential = "ghp_test_credential";

    /// <summary>The credential helper path the test's delegate hands the seam.</summary>
    private const string SeamHelperPath = "/tmp/copilothive-askpass.sh";

    /// <summary>
    /// A fake <see cref="GitOperations.ProcessRunner"/>: it records every launched request and
    /// the token it was launched with, answers the Stage 6d origin commands and the Stage 6b
    /// <c>check-ref-format</c> subprocess, and delegates everything else to
    /// <see cref="Responder"/> (defaulting to a success).
    /// </summary>
    private sealed class SeamProcessRunnerFake
    {
        public List<GitProcessRequest> Requests { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        /// <summary>The origin reported by <c>remote get-url origin</c> (exit 0 by default).</summary>
        public string OriginStdout { get; set; } = SeamEligibleUrl;

        public int OriginExitCode { get; set; }

        public string OriginStderr { get; set; } = "";

        /// <summary>Scripted answer keyed on the tokenized args; null falls through to success.</summary>
        public Func<IReadOnlyList<string>, GitProcessResult?>? Responder { get; set; }

        public Task<GitProcessResult> RunAsync(GitProcessRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            Tokens.Add(ct);

            var tokens = request.TokenizedArgs!;
            if (tokens[0] == "remote")
            {
                return Task.FromResult(tokens[1] == "get-url"
                    ? new GitProcessResult(OriginExitCode, OriginStdout, OriginStderr)
                    : new GitProcessResult(0, "", ""));
            }

            if (tokens[0] == "check-ref-format")
                return Task.FromResult(new GitProcessResult(0, "", ""));

            return Task.FromResult(Responder?.Invoke(tokens) ?? new GitProcessResult(0, "", ""));
        }

        /// <summary>The tokenized command of each recorded request, in launch order.</summary>
        public List<string[]> Launched =>
            [.. Requests.Select(r => r.TokenizedArgs!.ToArray())];
    }

    /// <summary>
    /// Builds the injected config-repo seam. Every delegate is controlled by the test; the
    /// disposal callback is a no-op because <see cref="TaskExecutor"/> never disposes it.
    /// </summary>
    private static ConfigRepoGitOperations CreateConfigRepoSeam(
        string configRepoDir,
        Func<string?>? resolvedUrlResolver = null,
        Func<string?>? credentialResolver = null,
        Func<string>? credentialHelperPath = null) =>
        new(
            configRepoDir,
            resolvedUrlResolver ?? (static () => SeamEligibleUrl),
            credentialResolver ?? (static () => SeamCredential),
            new WorkerLogger("Test"),
            credentialHelperPath ?? (static () => SeamHelperPath),
            static () => { });

    /// <summary>
    /// Runs the improver through the SEAM path with the fake process runner installed
    /// (restored in a <c>finally</c>) while capturing both console streams.
    /// </summary>
    private static async Task<(TaskResult Result, string Stdout, string Stderr)> RunImproverWithSeamAsync(
        string taskId,
        string configRepoDir,
        ConfigRepoGitOperations seam,
        SeamProcessRunnerFake fake,
        MockGitOperations git,
        CancellationToken? ct = null)
    {
        var originalRunner = GitOperations.ProcessRunner;
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        try
        {
            GitOperations.ProcessRunner = fake.RunAsync;
            Console.SetOut(outWriter);
            Console.SetError(errWriter);

            var executor = new TaskExecutor(
                new MockAgentRunner(), null, git, null, configRepoDir, seam);
            var result = await executor.ExecuteAsync(
                BuildImproverTask(taskId), ct ?? TestContext.Current.CancellationToken);

            return (result, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    /// <summary>
    /// Runs the improver through the LEGACY path (the PUBLIC constructor) while capturing both
    /// console streams. The <see cref="GitOperations.ProcessRunner"/> seam is NOT installed —
    /// the mock intercepts at <see cref="IGitOperations.RunGitCommandAsync"/>.
    /// </summary>
    private static async Task<(TaskResult Result, string Stdout, string Stderr)> RunImproverLegacyAsync(
        string taskId, string configRepoDir, MockGitOperations git, CancellationToken? ct = null)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);

            var executor = new TaskExecutor(
                new MockAgentRunner(), gitOperations: git, configRepoDir: configRepoDir);
            var result = await executor.ExecuteAsync(
                BuildImproverTask(taskId), ct ?? TestContext.Current.CancellationToken);

            return (result, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    /// <summary>NUL-delimited <c>diff --cached --name-only -z</c> output for the given paths.</summary>
    private static string StagedOutput(params string[] paths) =>
        string.Concat(paths.Select(p => p + "\0"));

    /// <summary>
    /// The canonical spelling the seam launches with: the fully-qualified config repo directory
    /// with any trailing separator removed.
    /// </summary>
    private static string CanonicalConfigRepoDir(string configRepoDir) =>
        Path.GetFullPath(configRepoDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void AssertLaunchedSequence(SeamProcessRunnerFake fake, params string[][] expected)
    {
        Assert.Equal(expected.Length, fake.Requests.Count);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], fake.Launched[i]);
    }

    /// <summary>
    /// Splits captured console output on EVERY line-breaking convention, so a forged line
    /// surfaces as its own entry rather than hiding inside a matched line.
    /// </summary>
    private static string[] SplitLines(string output) =>
        output.Split(['\n', '\r', '\u0085', '\u2028', '\u2029']);

    private static string FindLine(string output, string needle)
    {
        var line = Array.Find(SplitLines(output), l => l.Contains(needle, StringComparison.Ordinal));
        Assert.NotNull(line);
        return line!;
    }

    // ── (a) Complete routing on the SEAM path ────────────────────────────────

    /// <summary>
    /// The FULL improver flow routed through the injected seam: EVERY config-repo command is
    /// launched by the seam, in order, with the seam's canonicalization and origin state
    /// machine — and the legacy <see cref="IGitOperations.RunGitCommandAsync"/> is never
    /// touched. Deleting the seam branch of the dispatch leaves the fake with zero requests
    /// and the mock with the opaque strings, failing this test.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_LaunchesEveryConfigRepoCommandThroughTheSeam()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        string[] staged = ["agents/coder.agents.md", "agents/tester.agents.md"];

        var urlCalls = 0;
        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens[0] == "diff"
                ? new GitProcessResult(0, StagedOutput(staged), "")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir, () => { urlCalls++; return SeamEligibleUrl; });
        var git = new MockGitOperations();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-routing", configRepoDir, seam, fake, git);

        // The COMPLETE launch sequence, including the seam's explicit-origin canonicalization
        // of the two positional-free pulls and the full push sequence.
        AssertLaunchedSequence(fake,
            ["remote", "get-url", "origin"],
            ["pull", "--ff-only", "origin"],
            ["add", "agents/*.agents.md"],
            ["diff", "--cached", "--name-only", "-z"],
            ["commit", "-m", ImproverCommitMessage],
            ["remote", "get-url", "origin"],
            ["pull", "--no-rebase", "origin"],
            ["check-ref-format", "--allow-onelevel", "HEAD"],
            ["remote", "get-url", "origin"],
            ["push", "origin", "HEAD"]);

        // The legacy path was NEVER consulted.
        Assert.Empty(git.GitCommands);

        // Every launch used the CANONICALIZED config repo directory.
        var canonical = CanonicalConfigRepoDir(configRepoDir);
        Assert.All(fake.Requests, r => Assert.Equal(canonical, r.WorkingDirectory));

        // The `-z` parsing and the summary construction are preserved.
        Assert.NotNull(result.GitStatus);
        Assert.True(result.GitStatus!.Pushed);
        Assert.Equal(2, result.GitStatus.FilesChanged);
        Assert.Equal(staged, result.GitStatus.ChangedFiles);

        // Stage 6a runs for the THREE transport commands only — the local commands
        // (add/diff/commit) never resolve the URL.
        Assert.Equal(3, urlCalls);
    }

    /// <summary>
    /// The credential boundary on the seam path: ONLY the final command of an eligible
    /// transport operation carries <c>GITHUB_CONFIG_REPO_TOKEN</c> / <c>GIT_ASKPASS</c>.
    /// Every local command, every origin inspection and the ref-validation subprocess are
    /// credential-free — and no local command triggers a <c>remote get-url</c> at all.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_LocalCommandsCarryNoUrlResolutionOrCredential()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens[0] == "diff"
                ? new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), "")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        await RunImproverWithSeamAsync("improver-seam-credential", configRepoDir, seam, fake, git);

        string[][] credentialCarrying =
        [
            ["pull", "--ff-only", "origin"],
            ["pull", "--no-rebase", "origin"],
            ["push", "origin", "HEAD"],
        ];

        foreach (var request in fake.Requests)
        {
            var tokens = request.TokenizedArgs!.ToArray();
            var expectsCredential = credentialCarrying.Any(c => c.SequenceEqual(tokens));

            Assert.Equal(expectsCredential, request.Env.ContainsKey("GITHUB_CONFIG_REPO_TOKEN"));
            Assert.Equal(expectsCredential, request.Env.ContainsKey("GIT_ASKPASS"));
            if (expectsCredential)
            {
                Assert.Equal(SeamCredential, request.Env["GITHUB_CONFIG_REPO_TOKEN"]);
                Assert.Equal(SeamHelperPath, request.Env["GIT_ASKPASS"]);
            }
        }

        // The origin inspection happens EXACTLY three times: once per transport command. A
        // local command never reaches Stage 6d.
        Assert.Equal(3, fake.Launched.Count(t => t is ["remote", "get-url", "origin"]));
    }

    /// <summary>
    /// An ABSENT origin is ADDED by the seam before the transport command; a CREDENTIAL-BEARING
    /// but equivalent origin is REPAIRED with <c>set-url</c>. Both variants carry the SANITIZED
    /// URL and precede the canonicalized pull.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Improver_SeamPath_OriginIsAddedOrRepairedBeforeTheTransportCommand(bool originAbsent)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var fake = new SeamProcessRunnerFake
        {
            // An absent origin: non-zero inspection with the recognized stderr. A present but
            // credential-bearing origin: exit 0 reporting the credential-bearing URL.
            OriginExitCode = originAbsent ? 2 : 0,
            OriginStdout = originAbsent
                ? ""
                : "https://x-access-token:ghp_secret@github.com/org/config-repo.git",
            OriginStderr = originAbsent ? "fatal: no such remote 'origin'" : "",
            // No staged changes: the flow stops after add + diff, isolating the FIRST pull.
            Responder = tokens => tokens[0] == "diff" ? new GitProcessResult(0, "", "") : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        await RunImproverWithSeamAsync("improver-seam-origin", configRepoDir, seam, fake, git);

        string[] repair = originAbsent
            ? ["remote", "add", "origin", SeamEligibleUrl]
            : ["remote", "set-url", "origin", SeamEligibleUrl];

        AssertLaunchedSequence(fake,
            ["remote", "get-url", "origin"],
            repair,
            ["pull", "--ff-only", "origin"],
            ["add", "agents/*.agents.md"],
            ["diff", "--cached", "--name-only", "-z"]);
    }

    /// <summary>
    /// The oversized-agent-file DISCARD path routes through the seam as the tokenized
    /// <c>checkout -- agents/</c> local command.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_OversizedAgentsFile_DiscardsThroughTheSeam()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        await File.WriteAllTextAsync(
            Path.Combine(configRepoDir, "agents", "coder.agents.md"),
            new string('x', WorkerConstants.AgentsMdMaxCharacters + 1),
            TestContext.Current.CancellationToken);

        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens[0] == "diff" ? new GitProcessResult(0, "", "") : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        await RunImproverWithSeamAsync("improver-seam-discard", configRepoDir, seam, fake, git);

        AssertLaunchedSequence(fake,
            ["remote", "get-url", "origin"],
            ["pull", "--ff-only", "origin"],
            ["checkout", "--", "agents/"],
            ["add", "agents/*.agents.md"],
            ["diff", "--cached", "--name-only", "-z"]);
        Assert.Empty(git.GitCommands);
    }

    /// <summary>
    /// A FAILING <c>pull --no-rebase</c> still aborts the merge and force-pushes — through the
    /// seam, with the tokenized <c>merge --abort</c> between them.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_FailedPull_AbortsMergeAndStillPushes()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["diff", ..] => new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), ""),
                ["pull", "--no-rebase", ..] => new GitProcessResult(1, "", "merge conflict"),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        var (result, _, stderr) = await RunImproverWithSeamAsync(
            "improver-seam-merge-abort", configRepoDir, seam, fake, git);

        AssertLaunchedSequence(fake,
            ["remote", "get-url", "origin"],
            ["pull", "--ff-only", "origin"],
            ["add", "agents/*.agents.md"],
            ["diff", "--cached", "--name-only", "-z"],
            ["commit", "-m", ImproverCommitMessage],
            ["remote", "get-url", "origin"],
            ["pull", "--no-rebase", "origin"],
            ["merge", "--abort"],
            ["check-ref-format", "--allow-onelevel", "HEAD"],
            ["remote", "get-url", "origin"],
            ["push", "origin", "HEAD"]);

        Assert.Contains("git pull failed: merge conflict", FindLine(stderr, "git pull failed"), StringComparison.Ordinal);
        Assert.True(result.GitStatus!.Pushed);
    }

    // ── (b) The LEGACY path (public constructor) ─────────────────────────────

    /// <summary>
    /// The public constructor keeps the LEGACY opaque routing — every argument string arrives
    /// VERBATIM, including the QUOTED commit message and the BARE <c>push</c> (which the
    /// tokenized form spells <c>push origin HEAD</c>). Reconstructing the opaque form from the
    /// tokenized one would produce <c>push origin HEAD</c> and fail here.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_SendsTheExactOpaqueArgumentStrings()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(["agents/coder.agents.md"], pushFails: false),
        };

        await RunImproverLegacyAsync("improver-legacy-opaque", configRepoDir, git);

        Assert.Equal(
            [
                "pull --ff-only",
                "add agents/*.agents.md",
                "diff --cached --name-only -z",
                $"commit -m \"{ImproverCommitMessage}\"",
                "pull --no-rebase",
                "push",
            ],
            git.GitCommands);
        Assert.All(git.WorkDirs, dir => Assert.Equal(configRepoDir, dir));
    }

    /// <summary>
    /// The legacy DISCARD and <c>merge --abort</c> opaque strings, which the happy path never
    /// reaches.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_DiscardAndMergeAbort_UseTheExactOpaqueStrings()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        await File.WriteAllTextAsync(
            Path.Combine(configRepoDir, "agents", "coder.agents.md"),
            new string('x', WorkerConstants.AgentsMdMaxCharacters + 1),
            TestContext.Current.CancellationToken);

        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                "pull --no-rebase" => (1, "", "merge conflict"),
                _ => null,
            },
        };

        await RunImproverLegacyAsync("improver-legacy-abort", configRepoDir, git);

        Assert.Equal(
            [
                "pull --ff-only",
                "checkout -- agents/",
                "add agents/*.agents.md",
                "diff --cached --name-only -z",
                $"commit -m \"{ImproverCommitMessage}\"",
                "pull --no-rebase",
                "merge --abort",
                "push",
            ],
            git.GitCommands);
    }

    /// <summary>
    /// The legacy mapping is <c>Success == exitCode == 0</c> — NOT "stderr is empty". A
    /// SUCCESSFUL <c>add</c> that still wrote to stderr must not be treated as a failure: the
    /// flow proceeds to the diff and produces a real summary.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_ZeroExitWithStderr_IsStillSuccess()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args switch
            {
                "add agents/*.agents.md" => (0, "", "warning: LF will be replaced by CRLF"),
                "diff --cached --name-only -z" => (0, StagedOutput("agents/coder.agents.md"), ""),
                _ => null,
            },
        };

        var (result, _, stderr) = await RunImproverLegacyAsync(
            "improver-legacy-zero-exit", configRepoDir, git);

        Assert.DoesNotContain("git add failed", stderr, StringComparison.Ordinal);
        Assert.Equal(1, result.GitStatus!.FilesChanged);
        Assert.Contains("push", git.GitCommands);
    }

    /// <summary>
    /// The legacy mapping TRIMS a non-blank stderr and maps a WHITESPACE-ONLY stderr to the
    /// empty string, so the error log line renders nothing rather than stray blank space.
    /// </summary>
    [Theory]
    [InlineData("   boom   ", "git add failed: boom")]
    [InlineData("\t \n ", "git add failed:")]
    public async Task Improver_LegacyPath_MapsStderrDeterministically(string stderr, string expectedLine)
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args =>
                args == "add agents/*.agents.md" ? (1, "", stderr) : null,
        };

        var (result, _, captured) = await RunImproverLegacyAsync(
            "improver-legacy-map-" + expectedLine.Length, configRepoDir, git);

        Assert.Equal(expectedLine, FindLine(captured, "git add failed").Replace("[Task] ERROR: ", "").TrimEnd());

        // The `add` failure is an early return: no diff was ever queried.
        Assert.DoesNotContain("diff --cached --name-only -z", git.GitCommands);
        Assert.Equal(0, result.GitStatus!.FilesChanged);
    }

    // ── (c) Cancellation flows downstream on BOTH paths ──────────────────────

    /// <summary>
    /// The token handed to <see cref="TaskExecutor.ExecuteAsync"/> reaches the FINAL requests
    /// on the seam path unchanged — no derived token, no <see cref="CancellationToken.None"/>.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_ForwardsTheExecuteTokenToEveryLaunch()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        using var cts = new CancellationTokenSource();

        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens[0] == "diff"
                ? new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), "")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        await RunImproverWithSeamAsync(
            "improver-seam-token", configRepoDir, seam, fake, git, cts.Token);

        Assert.NotEmpty(fake.Tokens);
        Assert.All(fake.Tokens, t => Assert.Equal(cts.Token, t));
    }

    /// <summary>
    /// The legacy path forwards the SAME token to <see cref="IGitOperations.RunGitCommandAsync"/>.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_ForwardsTheExecuteTokenToEveryCommand()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        using var cts = new CancellationTokenSource();
        var git = new MockGitOperations
        {
            GitCommandResponder = ConfigRepoResponder(["agents/coder.agents.md"], pushFails: false),
        };

        await RunImproverLegacyAsync("improver-legacy-token", configRepoDir, git, cts.Token);

        Assert.NotEmpty(git.GitTokens);
        Assert.All(git.GitTokens, t => Assert.Equal(cts.Token, t));
    }

    /// <summary>
    /// An ALREADY-CANCELLED token still produces the established <c>Cancelled</c> result on the
    /// seam path: the shared launch layer observes the cancellation before the fake runner is
    /// ever consulted, and <see cref="TaskExecutor.ExecuteAsync"/>'s outer guard maps it.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_CancelledToken_ProducesCancelledResult()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var fake = new SeamProcessRunnerFake();
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        var (result, _, _) = await RunImproverWithSeamAsync(
            "improver-seam-cancelled", configRepoDir, seam, fake, git, cts.Token);

        Assert.Equal(TaskOutcome.Cancelled, result.Status);
        Assert.Empty(fake.Requests); // the pre-launch check fired before the delegate
    }

    // ── (d) Preserved behaviors on the SEAM path ─────────────────────────────

    /// <summary>
    /// The <c>Directory.Exists(.git)</c> early-outs are preserved: with no <c>.git</c> marker
    /// NEITHER the pull NOR the commit/push sequence launches anything.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_MissingGitMarker_LaunchesNothing()
    {
        var configRepoDir = Path.Combine(
            Path.GetTempPath(), $"copilothive-test-nogit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(configRepoDir, "agents"));
        using var remover = new DirectoryRemover(configRepoDir);

        var fake = new SeamProcessRunnerFake();
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        var (result, stdout, _) = await RunImproverWithSeamAsync(
            "improver-seam-nogit", configRepoDir, seam, fake, git);

        Assert.Empty(fake.Requests);
        Assert.Empty(git.GitCommands);
        Assert.Contains("Config repo not found", stdout, StringComparison.Ordinal);
        Assert.Equal(0, result.GitStatus!.FilesChanged);
    }

    /// <summary>
    /// The uniform exit-code handling: a NON-ZERO seam result renders the preserved
    /// <c>Config repo pull failed (exit N)</c> wording with the seam's exit code.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_NonZeroPull_LogsThePreservedExitCodeWording()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens switch
            {
                ["pull", "--ff-only", ..] => new GitProcessResult(3, "", "could not fast-forward"),
                ["diff", ..] => new GitProcessResult(0, "", ""),
                _ => null,
            },
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        var (_, _, stderr) = await RunImproverWithSeamAsync(
            "improver-seam-exit3", configRepoDir, seam, fake, git);

        Assert.Contains(
            "Config repo pull failed (exit 3): could not fast-forward",
            FindLine(stderr, "Config repo pull failed"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A seam REJECTION (Stage 6a: the resolved URL is absent) never launches a process and
    /// flows into the SAME error log lines as a legacy non-zero exit, carrying the seam's
    /// fixed <c>SanitizedError</c> and its <c>-1</c> exit code.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_Rejection_FlowsIntoTheSameErrorLogLines()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens[0] == "diff"
                ? new GitProcessResult(0, StagedOutput("agents/coder.agents.md"), "")
                : null,
        };
        // No resolved URL: every TRANSPORT command is rejected at Stage 6a; the local
        // commands are unaffected and still launch.
        using var seam = CreateConfigRepoSeam(configRepoDir, resolvedUrlResolver: static () => null);
        var git = new MockGitOperations();

        var (result, _, stderr) = await RunImproverWithSeamAsync(
            "improver-seam-reject", configRepoDir, seam, fake, git);

        Assert.Contains(
            "Config repo pull failed (exit -1): Config repo URL is not available.",
            FindLine(stderr, "Config repo pull failed"),
            StringComparison.Ordinal);
        Assert.Contains(
            "git pull failed: Config repo URL is not available.",
            FindLine(stderr, "git pull failed"),
            StringComparison.Ordinal);
        Assert.Contains(
            "git push failed: Config repo URL is not available.",
            FindLine(stderr, "git push failed"),
            StringComparison.Ordinal);

        // Only the LOCAL commands reached a launch — no transport, no origin inspection.
        AssertLaunchedSequence(fake,
            ["add", "agents/*.agents.md"],
            ["diff", "--cached", "--name-only", "-z"],
            ["commit", "-m", ImproverCommitMessage],
            ["merge", "--abort"]);

        // The push-failure path preserves the diagnostic paths and reports Pushed = false.
        Assert.False(result.GitStatus!.Pushed);
        Assert.Equal(["agents/coder.agents.md"], result.GitStatus.ChangedFiles);
    }

    // ── (e) The reachable control-character boundary vector ──────────────────

    /// <summary>
    /// THE SANITIZATION BOUNDARY. A LOCAL command (<c>add</c>) fails with a stderr carrying an
    /// embedded NEWLINE. The seam redacts but deliberately does NOT control-sanitize, so the
    /// uniform result's <c>SanitizedError</c> reaches TaskExecutor with the newline intact —
    /// and the log line must render it as <c>LogSanitizer.SanitizeText(...)</c> does, with the
    /// newline replaced by <c>'?'</c>, so no extra log line can be forged.
    /// </summary>
    [Fact]
    public async Task Improver_SeamPath_NewlineBearingStderr_IsRenderedControlSafe()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);

        var fake = new SeamProcessRunnerFake
        {
            Responder = tokens => tokens[0] == "add"
                ? new GitProcessResult(1, "", "fatal: bad path\n[Task] ERROR: forged line")
                : null,
        };
        using var seam = CreateConfigRepoSeam(configRepoDir);
        var git = new MockGitOperations();

        var (_, _, stderr) = await RunImproverWithSeamAsync(
            "improver-seam-newline", configRepoDir, seam, fake, git);

        var line = FindLine(stderr, "git add failed");
        Assert.Contains(
            "git add failed: fatal: bad path?[Task] ERROR: forged line",
            line,
            StringComparison.Ordinal);

        // The raw newline never reached the sink: the message occupies exactly ONE line and
        // the forged text is not on a line of its own.
        Assert.DoesNotContain(
            "fatal: bad path\n", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SplitLines(stderr), l => l.Trim() == "[Task] ERROR: forged line");
    }

    /// <summary>
    /// The same boundary on the LEGACY path — the mapping trims but does not sanitize, so the
    /// SAME rendering rule must apply there too.
    /// </summary>
    [Fact]
    public async Task Improver_LegacyPath_NewlineBearingStderr_IsRenderedControlSafe()
    {
        using var marker = EnsureConfigRepoMarker(out var configRepoDir);
        var git = new MockGitOperations
        {
            GitCommandResponder = args => args == "add agents/*.agents.md"
                ? (1, "", "fatal: bad path\n[Task] ERROR: forged line")
                : null,
        };

        var (_, _, stderr) = await RunImproverLegacyAsync(
            "improver-legacy-newline", configRepoDir, git);

        Assert.Contains(
            "git add failed: fatal: bad path?[Task] ERROR: forged line",
            FindLine(stderr, "git add failed"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            SplitLines(stderr), l => l.Trim() == "[Task] ERROR: forged line");
    }
}
